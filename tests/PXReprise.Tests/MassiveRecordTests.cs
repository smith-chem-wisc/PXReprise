using PXReprise.Census;
using PXReprise.Discovery;
using UsefulProteomicsDatabases;
using CvParam = MzLibUtil.CvParam;

namespace PXReprise.Tests;

/// <summary>
/// The MassIVE source, part 4a: MassIVE's dataset record (mzLib's <see cref="MassiveDataset"/>, its PROXI record) feeds
/// the screen, the chemistry and the gate through their repository-neutral forms. MassIVE gives no protocols,
/// quantification methods, experiment types, tags, organism parts or diseases, so those decide nothing, and every value
/// it does give is filed under MassIVE's own words.
/// </summary>
public class MassiveRecordTests
{
    /// <summary>MSV000096226's record as mzLib's fixture has it (Exploris 480, Oxidation and Carbamidomethyl), with a title,
    /// summary and keywords of the test's choosing.</summary>
    private static MassiveDataset Record(string title = "Aging mouse heart proteome", string summary = "Hearts of young and old mice.",
        CvParam[]? instruments = null, CvParam[]? modifications = null) => new()
        {
            MassiveAccession = "MSV000096226",
            PxdAccession = "PXD057243",
            Title = title,
            Summary = summary,
            Keywords = new[] { "aging", "heart" },
            Instruments = instruments ?? new[] { new CvParam("MS", "MS:1003028", "Orbitrap Exploris 480", "") },
            Modifications = modifications ?? new[] { new CvParam("UNIMOD", "UNIMOD:35", "Oxidation", ""), new CvParam("UNIMOD", "UNIMOD:4", "Carbamidomethyl", "") },
            Publications = new[] { new MassivePublication("12345678", null!, "Smith J, Doe A. The aging heart proteome. J Proteome Res. 2026;1:1-10.") },
        };

    [Test]
    public void TheScreenReadsMassivesTitleSummaryKeywordsInstrumentsAndFiles()
    {
        var words = DepositWords.From(Record(title: "Aging mouse heart, TMTpro 16-plex"), new[] { "raw/a.raw", "raw/b.raw" });
        var a = AcquisitionClassifier.Classify(words);
        Assert.Multiple(() =>
        {
            Assert.That((a.Mode, a.Labelling, a.Instrument, a.MsFileCount), Is.EqualTo((AcquisitionMode.Dda, Labelling.Isobaric, InstrumentClass.OrbitrapHcdOnly, 2)));
            Assert.That((words.SampleProcessingProtocol, words.DataProcessingProtocol), Is.EqualTo(("", "")), "MassIVE has no protocols");
            Assert.That(words.QuantificationMethods.Concat(words.ExperimentTypes).Concat(words.ProjectTags), Is.Empty, "nor these");
            Assert.That(AcquisitionClassifier.Text(words), Does.Contain("Hearts of young and old mice.").And.Contain("aging"));
        });
    }

    // MassIVE keeps the generic "instrument model" term (MS:1000031) only when its value names one; the model is the value.
    [TestCase("MS:1003028", "Orbitrap Exploris 480", "", "Orbitrap Exploris 480")]
    [TestCase("MS:1000031", "instrument model", "Q Exactive HF", "Q Exactive HF")]
    [TestCase("MS:1000031", "instrument model", "", "")]
    public void AnInstrumentTermGivesItsModel(string accession, string name, string value, string model) =>
        Assert.That(DepositWords.InstrumentModel(new CvParam("MS", accession, name, value)), Is.EqualTo(model));

    [Test]
    public void ARecordWithNoInstrumentsOrKeywordsGivesEmptyListsNotNull()
    {
        var bare = new MassiveDataset { MassiveAccession = "MSV000000001" };
        var words = DepositWords.From(bare, Array.Empty<string>());
        Assert.Multiple(() =>
        {
            Assert.That((words.Title, words.ProjectDescription), Is.EqualTo(("", "")));
            Assert.That(words.Keywords.Concat(words.Instruments), Is.Empty);
            Assert.That(AcquisitionClassifier.Classify(words).Instrument, Is.EqualTo(InstrumentClass.Unknown));
            Assert.That(RepositoryChemistry.From(bare).Ptms, Is.Empty);
            Assert.That(GateText.From(bare).Fields.Single(f => f.Field == "keywords").Text, Is.Empty);
        });
    }

    [Test]
    public void TheChemistryFilesMassivesDeclaredModificationsUnderMassivesWord()
    {
        var c = ChemistryDetector.DetectFrom(RepositoryChemistry.From(Record()), null, null, new[] { "a.raw" });
        Assert.Multiple(() =>
        {
            Assert.That((c.Alkylation.Value, c.Alkylation.Source), Is.EqualTo(("Carbamidomethyl", ChemistrySource.MassiveDeclaredModifications)));
            Assert.That(c.Alkylation.Source, Is.Not.EqualTo(ChemistrySource.PridePtm));
            Assert.That((c.Label.Value, c.Label.Source), Is.EqualTo(("label_free", ChemistrySource.Default)),
                "MassIVE gives no quantification methods, so nothing in the record decides the label");
        });
    }

    [Test]
    public void ALabelInMassivesWordsIsReadFromTheTextAsAGuess()
    {
        var c = ChemistryDetector.DetectFrom(RepositoryChemistry.From(Record(summary: "Peptides were labelled with TMT10plex reagents.")), null, null, new[] { "a.raw" });
        Assert.That(c.Label.Source, Is.EqualTo(ChemistrySource.ProtocolText));
        Assert.That(c.Label.Guessed, Is.True, "a label read from words is a guess the probe's spectra check");
    }

    [Test]
    public void TheGateReadsMassivesTitleSummaryKeywordsAndPublications()
    {
        var g = GateText.From(Record());
        string Field(string f) => g.Fields.Single(x => x.Field == f).Text;
        Assert.Multiple(() =>
        {
            Assert.That(g.Source, Is.EqualTo(GateTextSource.MassiveDataset));
            Assert.That(Field("title"), Is.EqualTo("Aging mouse heart proteome"));
            Assert.That(Field("description"), Is.EqualTo("Hearts of young and old mice."));
            Assert.That(Field("keywords"), Is.EqualTo("aging; heart"));
            Assert.That(Field("reference_titles"), Is.Not.Empty, "the publication's reference line gives a title");
            Assert.That(Field("sample_protocol") + Field("organism_parts") + Field("diseases"), Is.Empty, "MassIVE has none of these");
        });
    }
}
