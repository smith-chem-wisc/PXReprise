using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest;
using PXReprise.Study;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 14 (c), aging's DATAREPO-88 as restated in their 135: aging layer 0.7.0 keys
/// <c>age_effect_meta</c> by <c>method_scope</c> (<c>aging:DEF-AGE-EFFECT-META v1.4</c>), counts each method's datasets,
/// names the method terms a pooled row used, and holds them in <c>age_effect_method_terms</c>, one per isobaric dataset and
/// response. A Python 0.32.0 parity scope keeps the layer it had.</summary>
public class AgingLayer070Tests
{
    private static List<string> Columns(string table) =>
        SchemaContract.StudyTables("aging").Single(t => t.Name == table).Columns.Select(c => c.Name).ToList();

    [Test]
    public void AgeEffectMetaIsKeyedByMethodScopeAndAParityScopeKeepsQuantMethod()
    {
        var meta = Columns("age_effect_meta");
        List<string> parity;
        string[] parityKey;
        bool parityHasTerms;
        using (SchemaContract.Python0320())
        {
            parity = Columns("age_effect_meta");
            parityKey = Integrity.StudyCompositeIdentifiers["aging"]["age_effect_meta"];
            parityHasTerms = SchemaContract.StudyTables("aging").Any(t => t.Name == "age_effect_method_terms");
        }
        Assert.Multiple(() =>
        {
            Assert.That(SchemaContract.StudyVersion("aging"), Is.EqualTo("0.7.0"));
            Assert.That(meta, Does.Contain("method_scope").And.Contain("n_datasets_label_free").And.Contain("n_datasets_isobaric")
                .And.Contain("method_term_ids").And.Not.Contain("quant_method"));
            Assert.That(Integrity.StudyCompositeIdentifiers["aging"]["age_effect_meta"][^1], Is.EqualTo("method_scope"));
            Assert.That(Integrity.StudyCompositeIdentifiers["aging"]["age_effect_method_terms"], Is.EqualTo(new[] { "dataset_id", "response" }));
            Assert.That(parity, Does.Contain("quant_method").And.Not.Contain("method_scope").And.Not.Contain("method_term_ids"));
            Assert.That(parity[parity.IndexOf("acquisition") + 1], Is.EqualTo("quant_method"), "where 0.6.0 had it");
            Assert.That(parityKey[^1], Is.EqualTo("quant_method"));
            Assert.That(parityHasTerms, Is.False, "the table is 0.7.0's");
            Assert.That(SchemaDocs.StudyEnums["aging"]["FitRefusal"].Values, Does.Contain("no_channel_design").And.Contain("method_term_not_estimable"));
            Assert.That(SchemaDocs.StudyEnums["aging"]["MethodScope"].Values, Is.EqualTo(new[] { "all", "label_free", "isobaric" }));
        });
    }

    private string _root = "";

    [SetUp]
    public void MakeScratch() => _root = Path.Combine(Path.GetTempPath(), "datarepo-aging070-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private const string MetaHeader =
        "organism\tfeature_type\tfeature_id\tresponse\testimator\tquant_basis\tstratum\ttissue\tacquisition\tmethod_scope\tbeta_meta\tse_meta\t"
        + "n_datasets\tdataset_ids\tn_datasets_label_free\tn_datasets_isobaric\tmethod_term_ids\tdefinition_id";

    private const string TermsHeader =
        "method_term_id\tdataset_id\tresponse\tmethod\tvalue\tci90_low\tci90_high\testimator\tlabel_free_dataset_ids\tn_shared_features\tstatus\tdefinition_id";

    private StudyBundleRef Deliver(string[] meta, string[] terms)
    {
        var delivery = Path.Combine(_root, "delivery");
        Directory.CreateDirectory(delivery);
        File.WriteAllText(Path.Combine(delivery, "meta.tsv"), string.Join("\n", [MetaHeader, .. meta]) + "\n");
        File.WriteAllText(Path.Combine(delivery, "terms.tsv"), string.Join("\n", [TermsHeader, .. terms]) + "\n");
        File.WriteAllText(Path.Combine(delivery, "study.yaml"),
            "study_manifest_version: 1\nlayer: aging\nstore: ../store\ntables:\n  age_effect_meta: meta.tsv\n  age_effect_method_terms: terms.tsv\n");
        var result = StudyWriter.WriteStudyBundle(StudyWriter.LoadStudyManifest(Path.Combine(delivery, "study.yaml")));
        return CatalogBuilder.SelectStudyBundles(Path.Combine(_root, "store"), latest: ["aging"]).Single(r => r.BundleId == result.BundleId);
    }

    private const string AllRow =
        "NCBITaxon:10090\tprotein_group\tP12345\tabundance\tintensity\tmsms_only\tall\tUBERON:0002107\tDDA\tall\t-0.1\t0.02\t"
        + "4\tPXD000001;PXD000002;PXD000003;PXD000004\t3\t1\tPXD000004:abundance\taging:DEF-AGE-EFFECT-META v1.4";

    private const string TermRow =
        "PXD000004:abundance\tPXD000004\tabundance\tisobaric_ms3\t0.9\t0.84\t0.95\tMR-RAPS\tPXD000001;PXD000002;PXD000003\t812\tentered\tQuantProject:DEF-METHOD-TERM v1";

    /// <summary>A pooled row and the term it used are delivered and read back with their lists; one term per (dataset,
    /// response), so a second term for the same pair is refused.</summary>
    [Test]
    public void APooledRowAndItsMethodTermAreDeliveredAndATermIsOnePerDatasetAndResponse()
    {
        var bundle = Deliver([AllRow], [TermRow]);
        var (_, meta) = ArrowTables.ReadParquet(bundle.TablePath("age_effect_meta")!);
        var (_, terms) = ArrowTables.ReadParquet(bundle.TablePath("age_effect_method_terms")!);
        Assert.Multiple(() =>
        {
            Assert.That(meta.Single()["method_scope"], Is.EqualTo("all"));
            Assert.That(meta.Single()["method_term_ids"], Is.EqualTo(new List<object?> { "PXD000004:abundance" }));
            Assert.That((meta.Single()["n_datasets_label_free"], meta.Single()["n_datasets_isobaric"]), Is.EqualTo(((object?)3L, (object?)1L)));
            Assert.That(terms.Single()["label_free_dataset_ids"], Is.EqualTo(new List<object?> { "PXD000001", "PXD000002", "PXD000003" }));
            Assert.That(terms.Single()["method"], Is.EqualTo("isobaric_ms3"));
        });
        Directory.Delete(_root, true);
        Assert.That(() => Deliver([AllRow], [TermRow, TermRow.Replace("PXD000004:abundance\tPXD000004", "PXD000004:abundance#2\tPXD000004")]),
            Throws.InstanceOf<DataRepoException>().With.Message.Contains("age_effect_method_terms"));
    }
}
