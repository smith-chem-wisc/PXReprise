using PXReprise.Config;
using PXReprise.Discovery;

namespace PXReprise.Tests;

public class DiscoveryTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);

    private static Question Q(string extra = "") =>
        QuestionLoader.Load(TestSupport.WriteFile(TestSupport.TempDir(), "q.toml", TestSupport.MinimalQuestion + extra));

    [Test]
    public void LabelFreeOrbitrapDdaIsRoutedToTheLabelFreeProfile()
    {
        var r = TestSupport.Record("PXD000010", "Muscle proteome in type 2 diabetes");
        var a = AcquisitionClassifier.Classify(r);
        var route = Router.Assign(r.Accession, a, Relevance.Evaluate(r, Q().Relevance), Q(), Profiles);
        Assert.That(route, Is.EqualTo(new Route(RouteKind.Search, "label-free-dda@1", "accepted")));
    }

    [Test]
    public void TmtWaitsOnThePendingTmtProfileAndSaysSo()
    {
        var r = TestSupport.Record("PXD000011", "Islets in type 2 diabetes", "TMTpro 16-plex labelling");
        var route = Router.Assign(r.Accession, AcquisitionClassifier.Classify(r), Relevance.Evaluate(r, Q().Relevance), Q(), Profiles);
        Assert.That(route.Kind, Is.EqualTo(RouteKind.WaitingOnCapability));
        Assert.That(route.Profile, Is.EqualTo("tmt-dda@1"));
    }

    [TestCase("data-independent acquisition", null, "dia")]
    [TestCase("", "timsTOF Pro 2", "timstof")]
    [TestCase("SILAC labelled cells", null, "metabolic_labelling")]
    [TestCase("TMT 11-plex with a SILAC spike-in", null, "mixed_labelling: needs a hand decision")]
    // Aging 017 (PXR-A11): the two deposits the screen let through, in their own PRIDE words.
    [TestCase("Quantitative cross-linking mass spectrometry and proteomics together with a transgenic mouse", null, "crosslinking")]
    [TestCase("Mitochondria Mouse Xl-ms Kidney", null, "crosslinking")]
    [TestCase("isolated mitochondria were crosslinked with the iqPIR reagent", null, "crosslinking")]
    [TestCase("lysates were cross-linked with DSS before digestion", null, "crosslinking")]
    [TestCase("analysed by proteomics using 18O labeled internal standard", null, "o18_labelling")]
    [TestCase("O18 labelling", null, "o18_labelling")]
    public void WhatNoProfileTakesIsNamedByTheCapabilityItNeeds(string text, string? instrument, string capability)
    {
        var r = TestSupport.Record("PXD000012", "Liver in type 2 diabetes", text,
            instruments: instrument is null ? null : new[] { instrument });
        var route = Router.Assign(r.Accession, AcquisitionClassifier.Classify(r), Relevance.Evaluate(r, Q().Relevance), Q(), Profiles);
        Assert.That(route, Is.EqualTo(new Route(RouteKind.WaitingOnCapability, null, capability)));
    }

    [Test]
    public void TypeOneOnlyIsExcludedButAStudyNamingBothIsKept()
    {
        var q = Q();
        var t1 = TestSupport.Record("PXD000013", "Type 1 diabetes in NOD mice");
        var both = TestSupport.Record("PXD000014", "Type 1 and type 2 diabetes plasma");
        Assert.That(Relevance.Evaluate(t1, q.Relevance).Verdict, Is.EqualTo(RelevanceVerdict.NotRelevant).Or.EqualTo(RelevanceVerdict.Excluded));
        Assert.That(Relevance.Evaluate(both, q.Relevance).IsIn, Is.True);
    }

    [Test]
    public void AHandDecisionOverridesTheRules()
    {
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "d.tsv", "accession\tverdict\treason\nPXD000015\texclude\tstreptozotocin model\n");
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "q.toml",
            TestSupport.MinimalQuestion.Replace("unless_any = [\"type (2|ii) diabet\"]",
                "unless_any = [\"type (2|ii) diabet\"]\ndecisions = \"d.tsv\"")));
        var r = TestSupport.Record("PXD000015", "Type 2 diabetes model");
        var rel = Relevance.Evaluate(r, q.Relevance);
        Assert.That(rel, Is.EqualTo(new RelevanceResult(RelevanceVerdict.DecidedExclude, "streptozotocin model")));
        Assert.That(Router.Assign(r.Accession, AcquisitionClassifier.Classify(r), rel, q, Profiles).Kind, Is.EqualTo(RouteKind.OutOfScope));
    }

    [Test]
    public void AHeldDepositIsNotSearched()
    {
        var q = Q("\n[holds]\nPXD000016 = \"waits for the MetaMorpheus fix\"\n");
        var r = TestSupport.Record("PXD000016", "Type 2 diabetes heart");
        var route = Router.Assign(r.Accession, AcquisitionClassifier.Classify(r), Relevance.Evaluate(r, q.Relevance), q, Profiles);
        Assert.That(route, Is.EqualTo(new Route(RouteKind.Held, null, "waits for the MetaMorpheus fix")));
    }

    // The short reagent names have other meanings, and "crosslinked" alone describes hydrogels and ChIP fixation.
    [TestCase("colitis was induced with 3% DSS in drinking water")]
    [TestCase("searched against UniProt and PIR")]
    [TestCase("MSCs grown on soft or stiff crosslinked hydrogels with a PEG crosslinker")]
    [TestCase("chromatin was crosslinked with formaldehyde")]
    [TestCase("searched with PEAKS; O-GlcNAc on S/T")]
    public void WordsThatOnlyResembleCrosslinkingOr18OAreLabelFree(string text)
    {
        var r = TestSupport.Record("PXD000013", "Liver in type 2 diabetes", text, instruments: new[] { "Q Exactive HF" }, files: new[] { "a.raw" });
        var a = AcquisitionClassifier.Classify(r);
        Assert.That((a.Crosslinked, a.Labelling), Is.EqualTo((false, Labelling.LabelFree)));
        Assert.That(Router.Assign(r.Accession, a, Relevance.Evaluate(r, Q().Relevance), Q(), Profiles).Kind, Is.EqualTo(RouteKind.Search));
    }

    // Aging 030 (PXR-A23): immunopeptidomes passed the screen and were searched as tryptic. The sentences are PRIDE's own.
    [TestCase("Bead coupling and immunopurification of MHC class I peptides were performed as previously described")]   // PXD034059
    [TestCase("Native MHC-II-peptide complexes were purified using the InvivoMab anti-mouse MHC-II antibody")]          // PXD058775
    [TestCase("The database search was performed with an unspecified peptide cleavage.")]                                // PXD058775
    [TestCase("Proteomic and immunopeptidomic analyses reveal a distinct MHC-II antigen repertoire")]                     // PXD058775
    [TestCase("HLA-bound peptides were eluted with 0.1% TFA")]
    [TestCase("Endogenous peptides were extracted from the hypothalamus; peptidomics by LC-MS/MS")]
    public void NonTrypticPeptidesWaitForAProfileThatSearchesThem(string text)
    {
        var r = TestSupport.Record("PXD000017", "Liver in type 2 diabetes", text, instruments: new[] { "Q Exactive HF" }, files: new[] { "a.raw" });
        var a = AcquisitionClassifier.Classify(r);
        Assert.That(a.NonspecificCleavage, Is.True);
        var route = Router.Assign(r.Accession, a, Relevance.Evaluate(r, Q().Relevance), Q(), Profiles);
        Assert.That((route.Kind, route.Reason), Is.EqualTo((RouteKind.WaitingOnCapability, "nonspecific_cleavage")));
    }

    [Test]
    public void TheHupoHippTagAloneIsEnough()
    {
        var r = TestSupport.Record("PXD000018", "Liver in type 2 diabetes", "senescent cells", instruments: new[] { "Q Exactive HF" }, files: new[] { "a.raw" });
        r.ProjectTags.Add("Human immuno-peptidome project (hupo-hipp) (b/d-hpp)");
        var a = AcquisitionClassifier.Classify(r);
        Assert.That((a.NonspecificCleavage, a.Evidence), Is.EqualTo((true, "project tag: Human immuno-peptidome project (hupo-hipp) (b/d-hpp)")));
    }

    // Proteome biology that names MHC/HLA, and the other senses of "nonspecific", stay searchable.
    [TestCase("MHC class I expression was increased in senescent cells")]
    [TestCase("HLA-B27 transgenic rats develop spondyloarthritis")]
    [TestCase("nonspecific binding was blocked with 5% BSA")]
    [TestCase("unspecific binding to the beads was removed by washing")]
    [TestCase("the phosphopeptidome was enriched with TiO2")]
    [TestCase("proteins were digested with trypsin (enzyme: Trypsin/P, 2 missed cleavages)")]
    public void WordsThatOnlyResembleNonTrypticPeptidesAreSearched(string text)
    {
        var r = TestSupport.Record("PXD000019", "Liver in type 2 diabetes", text, instruments: new[] { "Q Exactive HF" }, files: new[] { "a.raw" });
        Assert.That(AcquisitionClassifier.Classify(r).NonspecificCleavage, Is.False);
    }

    [TestCase("Q Exactive HF", InstrumentClass.OrbitrapHcdOnly)]
    [TestCase("Orbitrap Exploris 480", InstrumentClass.OrbitrapHcdOnly)]
    [TestCase("Orbitrap Fusion Lumos", InstrumentClass.OrbitrapHybrid)]
    [TestCase("Orbitrap Astral", InstrumentClass.Astral)]
    [TestCase("timsTOF Pro", InstrumentClass.Timstof)]
    [TestCase("LTQ", InstrumentClass.ThermoLowRes)]
    [TestCase("TripleTOF 6600", InstrumentClass.Sciex)]
    [TestCase("Synapt G2-Si", InstrumentClass.Waters)]
    [TestCase("something else", InstrumentClass.Unknown)]
    public void InstrumentsAreClassedAsTheAgingPipelineClassedThem(string instrument, InstrumentClass expected) =>
        Assert.That(AcquisitionClassifier.ClassifyInstrument(new[] { instrument }), Is.EqualTo(expected));

    [Test]
    public void EnrichmentUsesTheFirstMatchingKindInDataRepoOrder()
    {
        // A TurboID capture IS a streptavidin pulldown; proximity labelling must win over affinity purification.
        var r = TestSupport.Record("PXD000017", "LAMP1-TurboID proximity labelling", "streptavidin pulldown of biotinylated proteins");
        Assert.That(AcquisitionClassifier.Classify(r).Enrichment, Is.EqualTo("proximity_labelling"));
        Assert.That(AcquisitionClassifier.Classify(TestSupport.Record("PXD000018", "Whole proteome")).Enrichment, Is.EqualTo("none"));
    }

    [Test]
    public void MsFilesAreCountedByType()
    {
        var counts = AcquisitionClassifier.CountMsFiles(new[] { "a.raw", "B.RAW", "c.d.zip", "d.mzML", "e.txt", "f.d" });
        Assert.That(counts, Is.EquivalentTo(new Dictionary<string, int> { [".raw"] = 2, [".d.zip"] = 1, [".mzml"] = 1, [".d"] = 1 }));
    }
}
