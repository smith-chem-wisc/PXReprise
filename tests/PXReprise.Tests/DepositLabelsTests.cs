using System.Text.Json;
using PXReprise.Discovery;
using PXReprise.Labels;

namespace PXReprise.Tests;

/// <summary>
/// D94: each run's sample type and the search's cysteine chemistry, set from what the deposit's folder records, for
/// ptmQtl to read in place of its hand lists (their D86). The cases are aging deposits the labels were checked on
/// (results/run-search-labels_2026-11-25 in the project repository).
/// </summary>
public class DepositLabelsTests
{
    private static readonly string[] Files = ["a.raw", "b.raw", "c.raw", "d.raw"];
    private static RunShares Run(double phospho = 0.003, double gg = 0.002, double acetylK = 0.004, int psms = 5000) => new(psms, phospho, gg, acetylK, 0.17);
    private static readonly IReadOnlyList<LabelDecision> NoDecisions = [];

    private static IReadOnlyList<RunSampleType> Label(string? text, IReadOnlyDictionary<string, RunShares> probe, IReadOnlyList<LabelDecision>? decisions = null) =>
        DepositLabeller.SampleTypes("PXD000001", Files, text, "the words", probe, decisions ?? NoDecisions);

    [TestCase("none", SampleType.WholeProteome, null)]
    [TestCase("phospho", SampleType.PtmCapture, "phospho")]
    [TestCase("ubiquitin_GG", SampleType.PtmCapture, "ubiquitin_GG")]
    [TestCase("acetyl", SampleType.PtmCapture, "acetyl")]
    [TestCase("succinyl", SampleType.PtmCapture, "succinyl")]
    [TestCase("glyco", SampleType.PtmCapture, "glyco")]
    [TestCase("immunoprecipitation", SampleType.ProteinPulldown, "immunoprecipitation")]
    [TestCase("proximity_labelling", SampleType.ProteinPulldown, "proximity_labelling")]
    [TestCase("affinity_purification", SampleType.ProteinPulldown, "affinity_purification")]
    [TestCase("chemical_probe", SampleType.ProteinPulldown, "chemical_probe")]
    [TestCase("other", SampleType.Unknown, "other")]
    [TestCase(null, SampleType.Unknown, null)]
    public void TheTextGivesTheSampleTypePtmQtlsEngineGivesTheSameEnrichment(string? text, string value, string? target) =>
        Assert.That(DepositLabeller.FromText(text), Is.EqualTo((value, target)));

    [Test]
    public void AWholeProteomeTheProbeConfirmsIsOneAndItsUnprobedRunsTakeTheText()
    {
        var runs = Label("none", new Dictionary<string, RunShares> { ["a.raw"] = Run(), ["b.raw"] = Run() });
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.WholeProteome));
        Assert.That(runs[0].Sources, Is.EqualTo(new[] { LabelSource.RepositoryText, LabelSource.DataCheck }));
        Assert.That(runs[2].Sources, Is.EqualTo(new[] { LabelSource.RepositoryText }));
        Assert.That(runs[2].Reason, Does.Contain("not checked: the probe did not search this run"));
        Assert.That(runs[0].RunId, Is.EqualTo("PXD000001:a"), "the bundle's run id");
        Assert.That(runs[0].Data!.Psms, Is.EqualTo(5000));
    }

    [Test]
    public void OneProbedRunThatContradictsTheTextMakesEveryRunUnknown()
    {
        // PXD039172: 14 samples x 7 SCX fractions, text none, phospho 17% in SCX1 falling to 1%. A fraction of an
        // enriched sample is enriched too, so the run whose own share looks normal is unknown as well.
        var runs = Label("none", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.17), ["b.raw"] = Run(phospho: 0.01) });
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
        Assert.That(runs[0].Reason, Does.Contain("but the run carries phospho on 17.0 %").And.Contain("they disagree"));
        Assert.That(runs[0].Target, Is.EqualTo("phospho"));
        Assert.That(runs[1].Reason, Does.Contain("another run of the deposit contradicts the text"));
        Assert.That(runs[3].Reason, Does.Contain("another run of the deposit contradicts the text"));
    }

    [Test]
    public void ACaptureTheTextNamesButTheDataDoNotShowIsUnknown()
    {
        // PXD007035: "PTMScan Ubiquitin Remnant Motif (K-e-GG) Kit", but its probe run carries GG on 0.3% of its PSMs.
        var runs = Label("ubiquitin_GG", new Dictionary<string, RunShares> { ["a.raw"] = Run(gg: 0.0026) });
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
        Assert.That(runs.Select(r => r.Target), Is.All.EqualTo("ubiquitin_GG"));
    }

    [Test]
    public void ACaptureTheDataConfirmIsOneAndItsUnprobedRunsTakeTheText()
    {
        var runs = Label("phospho", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.31), ["b.raw"] = Run(phospho: 0.70) });
        Assert.That(runs.Select(r => (r.Value, r.Target)), Is.All.EqualTo((SampleType.PtmCapture, "phospho")));
        Assert.That(runs[0].Sources, Does.Contain(LabelSource.DataCheck));
        Assert.That(runs[3].Sources, Is.EqualTo(new[] { LabelSource.RepositoryText }));
    }

    [Test]
    public void AChemicalProbeWhoseRunsCarryAModificationIsACaptureOfIt()
    {
        var runs = Label("chemical_probe", new Dictionary<string, RunShares> { ["a.raw"] = Run(gg: 0.08) });
        Assert.That((runs[0].Value, runs[0].Target), Is.EqualTo((SampleType.PtmCapture, "ubiquitin_GG")));
        Assert.That(runs[0].Reason, Does.Contain("a probe aimed at a modification"));
        Assert.That(runs[1].Value, Is.EqualTo(SampleType.ProteinPulldown), "an unprobed run keeps the text: nothing contradicted it");
    }

    [Test]
    public void APulldownWhoseRunsCarryAModificationIsUnknown()
    {
        var runs = Label("immunoprecipitation", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.2) });
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
    }

    [Test]
    public void ACaptureTheProbeCannotMeasureIsTakenFromTheTextAlone()
    {
        var runs = Label("succinyl", new Dictionary<string, RunShares> { ["a.raw"] = Run() });
        Assert.That((runs[0].Value, runs[0].Target), Is.EqualTo((SampleType.PtmCapture, "succinyl")));
        Assert.That(runs[0].Sources, Is.EqualTo(new[] { LabelSource.RepositoryText }));
        Assert.That(runs[0].Reason, Does.Contain("which the probe cannot measure"));
        Assert.That(Label("succinyl", new Dictionary<string, RunShares> { ["a.raw"] = Run(acetylK: 0.3) }).Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
    }

    [Test]
    public void AnUnclassifiedEnrichmentIsUnknownUnlessTheDataShowACapture()
    {
        Assert.That(Label("other", new Dictionary<string, RunShares> { ["a.raw"] = Run() }).Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
        var runs = Label("other", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.2, acetylK: 0.2) });
        Assert.That((runs[0].Value, runs[0].Target), Is.EqualTo((SampleType.PtmCapture, "phospho+acetyl")));
    }

    [Test]
    public void AProbeRunWithTooFewPsmsChecksNothing()
    {
        var runs = Label("none", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.5, psms: DepositLabeller.MinCheckedPsms - 1) });
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.WholeProteome));
        Assert.That(runs[0].Reason, Does.Contain($"the probe found {DepositLabeller.MinCheckedPsms - 1} PSMs in this run"));
    }

    [Test]
    public void APersonsDecisionOutranksTheTextAndTheDataAndTheRunsOwnOutranksTheDeposits()
    {
        // PXD018097-like: capture runs and lysate runs in one deposit; ptmQtl's capture_runs list, loaded as decisions.
        var decisions = new List<LabelDecision>
        {
            new("PXD000001", "PXD000001:a", "sample_type", SampleType.PtmCapture, "phospho", "ptmQtl", "2026-10-19", "capture_runs.tsv"),
            new("PXD000001", "*", "sample_type", SampleType.WholeProteome, null, "ptmQtl", "2026-10-19", "a run capture_runs.tsv does not name"),
        };
        var runs = Label("phospho", new Dictionary<string, RunShares> { ["a.raw"] = Run(phospho: 0.3), ["b.raw"] = Run() }, decisions);
        Assert.That(runs.Select(r => r.Value), Is.EqualTo(new[] { SampleType.PtmCapture, SampleType.WholeProteome, SampleType.WholeProteome, SampleType.WholeProteome }));
        Assert.That(runs.Select(r => r.Sources.Single()), Is.All.EqualTo(LabelSource.Person));
        Assert.That(runs[0].Reason, Is.EqualTo("a person's decision (ptmQtl, 2026-10-19): capture_runs.tsv"));
        Assert.That(runs[0].Decision!.By, Is.EqualTo("ptmQtl"));
        Assert.That(runs[0].Data, Is.Not.Null, "the data stay beside the decision");
    }

    [Test]
    public void APulldownTheDataDoNotContradictIsOne()
    {
        var runs = Label("immunoprecipitation", new Dictionary<string, RunShares> { ["a.raw"] = Run() });
        Assert.That(runs.Select(r => (r.Value, r.Target)), Is.All.EqualTo((SampleType.ProteinPulldown, "immunoprecipitation")));
        Assert.That(runs[0].Sources, Is.EqualTo(new[] { LabelSource.RepositoryText, LabelSource.DataCheck }));
    }

    [Test]
    public void ADecisionNamesARunByItsFileOrItsStemAndKeepsItsLabelWhenAnotherRunContradictsTheText()
    {
        var decisions = new List<LabelDecision>
        {
            new("PXD000001", "a.raw", "sample_type", SampleType.PtmCapture, "phospho", "a person", "2026-11-25", "the capture run"),
            new("PXD000001", "b", "sample_type", SampleType.WholeProteome, null, "a person", "2026-11-25", "the lysate run"),
        };
        // c.raw carries phospho although the text says none: the deposit's undecided runs go unknown, the decided stay.
        var runs = Label("none", new Dictionary<string, RunShares> { ["c.raw"] = Run(phospho: 0.4) }, decisions);
        Assert.That(runs.Select(r => r.Value), Is.EqualTo(new[] { SampleType.PtmCapture, SampleType.WholeProteome, SampleType.Unknown, SampleType.Unknown }));
    }

    [Test]
    public void ADecisionNamingTheAccessionCoversItsSplitDatasets()
    {
        var d = new LabelDecision("PXD000001", "*", "sample_type", SampleType.Unknown, null, "a person", "2026-11-25", "held");
        var runs = DepositLabeller.SampleTypes("PXD000001-lfq", Files, "none", null, new Dictionary<string, RunShares>(), [d]);
        Assert.That(runs.Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
        Assert.That(runs[0].RunId, Is.EqualTo("PXD000001-lfq:a"));
    }

    // ---------------------------------------------------------------- cysteine

    private static readonly SearchCysMod Cam = new("Carbamidomethyl", "UNIMOD:4", true);

    private static SearchCysteine Cys(string value, string source, string? evidence, double? share, IReadOnlyList<SearchCysMod>? mods = null, IReadOnlyList<LabelDecision>? decisions = null) =>
        DepositLabeller.Cysteine("PXD000001", new ChemistryFact(value, source, evidence), mods ?? [Cam], share, decisions ?? NoDecisions);

    [Test]
    public void NoReagentStatedAndANormalShareMatches()
    {
        var c = Cys("Carbamidomethyl", ChemistrySource.Default, null, 0.17);
        Assert.That((c.Verdict, c.Reagent), Is.EqualTo((CysteineVerdict.Matches, "not stated")));
        Assert.That(c.Sources, Is.EqualTo(new[] { LabelSource.DataCheck }));
        Assert.That(c.Reason, Does.Contain("17.0 % of its 1% target peptides contain C"));
    }

    [Test]
    public void NoReagentStatedAndALowShareIsAMismatch()
    {
        // PXD057499: alkylated with iodoethanol, which the chemistry step did not read; searched with carbamidomethyl.
        var c = Cys("Carbamidomethyl", ChemistrySource.Default, null, 0.0052);
        Assert.That(c.Verdict, Is.EqualTo(CysteineVerdict.Mismatch));
        Assert.That(c.Reason, Does.Contain("a chemistry the search did not assume"));
    }

    [Test]
    public void AReagentNamedAndSearchedButALowShareIsUnknown()
    {
        // PXD035171: "200 mM chloroacetamide", 0.3% cysteine peptides.
        var c = Cys("Carbamidomethyl", ChemistrySource.ProtocolText, "200 mM chloroacetamide stock solution", 0.0031);
        Assert.That((c.Verdict, c.Reagent), Is.EqualTo((CysteineVerdict.Unknown, "chloroacetamide")));
        Assert.That(c.Sources, Is.EqualTo(new[] { LabelSource.RepositoryText, LabelSource.DataCheck }));
        Assert.That(c.Reason, Does.Contain("they disagree"));
    }

    [Test]
    public void NemDeclaredInTheSearchMatchesAndNemNotDeclaredIsAMismatch()
    {
        var nem = new SearchCysMod("Nethylmaleimide", "UNIMOD:108", false);
        Assert.That(Cys("Nethylmaleimide", ChemistrySource.PridePtm, "Nethylmaleimide", 0.14, [nem]).Verdict, Is.EqualTo(CysteineVerdict.Matches), "PXD001054");
        var c = Cys("Nethylmaleimide", ChemistrySource.ProtocolText, "NEM", 0.14, [Cam]);
        Assert.That(c.Verdict, Is.EqualTo(CysteineVerdict.Mismatch));
        Assert.That(c.Reason, Does.Contain("NEM not declared"));
    }

    [Test]
    public void NoShareRecordedIsUnknownAndADecisionOutranksTheData()
    {
        Assert.That(Cys("Carbamidomethyl", ChemistrySource.Default, null, null).Verdict, Is.EqualTo(CysteineVerdict.Unknown));
        var d = new LabelDecision("PXD000001", "*", "cysteine", CysteineVerdict.Mismatch, "MMTS", "ptmQtl", "2026-11-25", "S2d cysteine_poor: MMTS");
        var c = Cys("Methylthio", ChemistrySource.ProtocolText, "MMTS", 0.103, [new("Methylthio", "UNIMOD:39", true)], [d]);
        Assert.That((c.Verdict, c.Reagent, c.Sources.Single()), Is.EqualTo((CysteineVerdict.Mismatch, "MMTS", LabelSource.Person)));
    }

    [Test]
    public void ASearchWithNoCysteineModificationSaysSoAndADecisionWithNoReagentKeepsTheNamedOne()
    {
        var c = Cys("none", ChemistrySource.QuestionException, "never alkylated", 0.15, []);
        Assert.That((c.Verdict, c.Reagent), Is.EqualTo((CysteineVerdict.Matches, "none")));
        Assert.That(c.Reason, Does.Contain("the search declares no cysteine modification"));
        Assert.That(c.Sources, Is.EqualTo(new[] { LabelSource.Person, LabelSource.DataCheck }), "the question's exception is a person's word");

        var d = new LabelDecision("PXD000001", "*", "cysteine", CysteineVerdict.Unknown, null, "a person", "2026-11-25", "the gel's acrylamide");
        Assert.That(Cys("Carbamidomethyl", ChemistrySource.ProtocolText, "iodoacetamide", 0.15, decisions: [d]).Reagent, Is.EqualTo("iodoacetamide"));
    }

    [Test]
    public void ACuratedSdrfsReagentIsAPersonsWord()
    {
        var c = Cys("Methylthio", ChemistrySource.CuratedSdrf, null, 0.117, [new("Methylthio", "UNIMOD:39", false)]);
        Assert.That((c.Verdict, c.Reagent), Is.EqualTo((CysteineVerdict.Matches, "MMTS")));
        Assert.That(c.Sources, Is.EqualTo(new[] { LabelSource.Person, LabelSource.DataCheck }));
    }

    [TestCase("Carbamidomethyl", ChemistrySource.ProtocolText, "reduced and alkylated with 55 mM iodoacetamide", "iodoacetamide")]
    [TestCase("Carbamidomethyl", ChemistrySource.ProtocolText, "40 mM CAA", "chloroacetamide")]
    [TestCase("Carbamidomethyl", ChemistrySource.PridePtm, "S-carboxamidomethyl-L-cysteine", "iodoacetamide or chloroacetamide")]
    [TestCase("Carbamidomethyl", ChemistrySource.Default, null, "not stated")]
    [TestCase("Methylthio", ChemistrySource.ProtocolText, "MMTS", "MMTS")]
    [TestCase("Ethanolyl", ChemistrySource.ProtocolText, "iodoethanol", "iodoethanol")]
    [TestCase("Propionamide", ChemistrySource.ProtocolText, "acrylamide", "acrylamide")]
    [TestCase("none", ChemistrySource.QuestionException, "never alkylated", "none")]
    [TestCase("NBF-Cl", ChemistrySource.ProtocolText, "NBF-Cl", "NBF-Cl")]
    [TestCase("NEM:2H(5)", ChemistrySource.ProtocolText, "d5-NEM", "d5-NEM")]
    [TestCase("Nethylmaleimide + NEM:2H(5)", ChemistrySource.PridePtm, "Nethylmaleimide | heavy partner", "NEM and d5-NEM")]
    [TestCase("Carboxymethyl", ChemistrySource.ProtocolText, "iodoacetic acid", "iodoacetic acid")]
    [TestCase("Pyridylethyl", ChemistrySource.ProtocolText, "4-vinylpyridine", "4-vinylpyridine")]
    public void TheReagentIsNamedInWords(string value, string source, string? evidence, string reagent) =>
        Assert.That(DepositLabeller.Reagent(new ChemistryFact(value, source, evidence)), Is.EqualTo(reagent));

    // ---------------------------------------------------------------- a person's decisions file

    private static string Decisions(params string[] rows) => TestSupport.WriteFile(TestSupport.TempDir(), "decisions.tsv",
        string.Join("\r\n", new[] { string.Join('\t', DepositLabelReader.DecisionColumns) }.Concat(rows)) + "\r\n");

    [Test]
    public void ADecisionsFileIsReadWithCrlfCommentsAndBlankLines()
    {
        var d = DepositLabelReader.LoadDecisions(Decisions(
            "# capture list v4", "",
            "PXD018097\tPXD018097:x_01\tsample_type\tptm_capture\tphospho\tptmQtl\t2026-10-19\tcapture_runs.tsv",
            "PXD013640\t\tcysteine\tmismatch\tMMTS\tptmQtl\t2026-11-25\tS2d"));
        Assert.That(d, Has.Count.EqualTo(2));
        Assert.That(d[0], Is.EqualTo(new LabelDecision("PXD018097", "PXD018097:x_01", "sample_type", "ptm_capture", "phospho", "ptmQtl", "2026-10-19", "capture_runs.tsv")));
        Assert.That(d[1].Run, Is.EqualTo(LabelDecision.EveryRun), "an empty run is every run");
    }

    [TestCase("PXD1\t*\tenrichment\tnone\t\ta\t2026-11-25\tr", "label 'enrichment'")]
    [TestCase("PXD1\t*\tsample_type\tlysate\t\ta\t2026-11-25\tr", "sample_type 'lysate'")]
    [TestCase("PXD1\t*\tcysteine\tcysteine_poor\t\ta\t2026-11-25\tr", "cysteine 'cysteine_poor'")]
    [TestCase("PXD1\t*\tsample_type\tunknown\t\t\t2026-11-25\tr", "by and reason are required")]
    [TestCase("PXD1\t*\tsample_type\tunknown\t\ta\t25/11/2026\tr", "is not yyyy-MM-dd")]
    [TestCase("PXD1\tPXD1:x\tcysteine\tmatches\t\ta\t2026-11-25\tr", "its run is '*'")]
    public void ABadDecisionIsRefusedWithItsLine(string row, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => DepositLabelReader.LoadDecisions(Decisions(row)));
        Assert.That(e!.Message, Does.Contain("line 2").And.Contain(message));
    }

    [Test]
    public void ADecisionMadeTwiceIsRefused()
    {
        string row = "PXD1\t*\tsample_type\tunknown\t\ta\t2026-11-25\tr";
        Assert.That(() => DepositLabelReader.LoadDecisions(Decisions(row, row)), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("decided twice"));
        Assert.That(() => DepositLabelReader.LoadDecisions(TestSupport.WriteFile(TestSupport.TempDir(), "d.tsv", "dataset\trun\n")),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no column(s) label"));
    }

    // ---------------------------------------------------------------- a deposit's folder, and the verb

    /// <summary>A deposit folder as the batch leaves it: the search's record (spectra inputs, each beside its own
    /// settings file, and its chemistry and cysteine share) and the experiment record (text and two probe runs).</summary>
    private static string Deposit(string name = "PXD000001", bool experiment = true)
    {
        string dir = Path.Combine(TestSupport.TempDir(), name);
        Directory.CreateDirectory(Path.Combine(dir, "04_search"));
        Directory.CreateDirectory(Path.Combine(dir, ExperimentRecord.Folder));
        string Input(string f) => $"{{\"path\": \"run/{name}/02_fetch/spectra/{f}\", \"root\": \"work_root\", \"size_bytes\": 1, \"sha256\": \"00\"}}";
        TestSupport.WriteFile(Path.Combine(dir, "04_search"), "provenance.json", $$"""
            {"inputs": [{"path": "run/{{name}}/04_search/design/x.sdrf.tsv"}, {{Input("a.raw")}}, {{Input("a.toml")}}, {{Input("b.raw")}}, {{Input("c.raw")}}],
             "chemistry": {"alkylation": {"value": "Carbamidomethyl", "source": "default", "evidence": null},
                           "cysteine_mods": [{"name": "Carbamidomethyl", "unimod": "UNIMOD:4", "fixed": true, "metamorpheus": "Common Fixed\tCarbamidomethyl on C"}]},
             "cysteine_peptide_share": 0.1681}
            """);
        if (experiment)
            TestSupport.WriteFile(Path.Combine(dir, ExperimentRecord.Folder), ExperimentRecord.JsonFile, """
                {"facts": {"enrichment": {"value": "none", "source": "pride_record_text", "evidence": null, "guessed": false}},
                 "runs": {"a.raw": {"probe": true, "probe_search": {"psms_1pct": 5729, "cysteine_share": 0.1739, "phospho_share": 0.0032, "gg_share": 0.0025, "acetyl_k_share": 0.0044}},
                          "b.raw": {"probe": true, "probe_search": {"psms_1pct": 4100, "cysteine_share": 0.17, "phospho_share": 0.003, "gg_share": 0.002, "acetyl_k_share": 0.004}}}}
                """);
        return dir;
    }

    [Test]
    public void ADepositsFolderIsLabelledFromItsSearchAndExperimentRecords()
    {
        var labels = DepositLabelReader.Read(Deposit(), NoDecisions);
        Assert.That(labels.Dataset, Is.EqualTo("PXD000001"));
        Assert.That(labels.Runs.Select(r => r.File), Is.EqualTo(new[] { "a.raw", "b.raw", "c.raw" }), "the settings file beside a run is not a run");
        Assert.That(labels.Runs.Select(r => r.Value), Is.All.EqualTo(SampleType.WholeProteome));
        Assert.That(labels.Runs.Count(r => r.Data is not null), Is.EqualTo(2));
        Assert.That((labels.Cysteine.Verdict, labels.Cysteine.CysteinePeptideShare), Is.EqualTo((CysteineVerdict.Matches, 0.1681)));
        Assert.That(labels.Cysteine.SearchAssumed, Is.EqualTo(new[] { Cam }));
        Assert.That(labels.Notes, Is.Empty);
    }

    [Test]
    public void ADepositNeverScreenedIsUnknownAndSaysSo()
    {
        var labels = DepositLabelReader.Read(Deposit(experiment: false), NoDecisions);
        Assert.That(labels.Runs.Select(r => r.Value), Is.All.EqualTo(SampleType.Unknown));
        Assert.That(labels.Notes, Has.Some.Contains("the record's text was never screened"));
        Assert.That(labels.Runs[0].Reason, Does.StartWith("the record was never screened"));
    }

    [Test]
    public void ASearchRecordWithNoChemistryFallsBackToTheChemistryStepAndThenToTheDefault()
    {
        string dir = Deposit();
        string prov = Path.Combine(dir, "04_search", "provenance.json");
        var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(prov))!.AsObject();
        o.Remove("chemistry");
        File.WriteAllText(prov, o.ToJsonString());
        var labels = DepositLabelReader.Read(dir, NoDecisions);
        Assert.That((labels.Cysteine.Reagent, labels.Cysteine.SearchAssumed.Single().Name), Is.EqualTo(("not stated", "Carbamidomethyl")));
        Assert.That(labels.Notes, Has.Some.Contains("the search ran its profile's default"));

        Directory.CreateDirectory(Path.Combine(dir, "01_chemistry"));
        TestSupport.WriteFile(Path.Combine(dir, "01_chemistry"), "chemistry.json", """
            {"alkylation": {"value": "Methylthio", "source": "protocol_text", "evidence": "blocked with MMTS"},
             "cysteine_mods": [{"name": "Methylthio", "unimod": "UNIMOD:39", "fixed": true}]}
            """);
        labels = DepositLabelReader.Read(dir, NoDecisions);
        Assert.That((labels.Cysteine.Reagent, labels.Cysteine.Verdict), Is.EqualTo(("MMTS", CysteineVerdict.Matches)));
        Assert.That(labels.Notes, Has.Some.Contains("read 01_chemistry/chemistry.json"));
    }

    [Test]
    public void AnOlderOrSparserRecordTakesItsGapsAsUnknownsAndDefaults()
    {
        // A search recorded before the cysteine-peptide share existed, with a chemistry block missing its source and a
        // modification missing "fixed"; an input with no path; an experiment record with no enrichment fact, a run the
        // probe did not search, and a probe run with no PSM count.
        string dir = Path.Combine(TestSupport.TempDir(), "PXD000005");
        Directory.CreateDirectory(Path.Combine(dir, "04_search"));
        Directory.CreateDirectory(Path.Combine(dir, ExperimentRecord.Folder));
        TestSupport.WriteFile(Path.Combine(dir, "04_search"), "provenance.json", """
            {"inputs": [{"root": "work_root"}, {"path": "run\\PXD000005\\02_fetch\\spectra\\a.raw"}],
             "chemistry": {"alkylation": {"value": "Carbamidomethyl"}, "cysteine_mods": [{"name": "Carbamidomethyl", "unimod": "UNIMOD:4"}]}}
            """);
        TestSupport.WriteFile(Path.Combine(dir, ExperimentRecord.Folder), ExperimentRecord.JsonFile, """
            {"facts": {}, "runs": {"a.raw": {"probe": false}, "b.raw": {"probe_search": {"psms_1pct": "n/a", "phospho_share": null, "gg_share": "n/a"}}}}
            """);
        var labels = DepositLabelReader.Read(dir, NoDecisions);
        Assert.That(labels.Runs.Select(r => r.File), Is.EqualTo(new[] { "a.raw" }), "a Windows path is read too");
        Assert.That(labels.Runs[0].Value, Is.EqualTo(SampleType.Unknown), "no enrichment fact: never screened");
        Assert.That((labels.Cysteine.Verdict, labels.Cysteine.Reagent), Is.EqualTo((CysteineVerdict.Unknown, "not stated")));
        Assert.That(labels.Cysteine.Reason, Does.Contain("recorded no cysteine-peptide share"));
        Assert.That(labels.Cysteine.SearchAssumed.Single().Fixed, Is.False);

        TestSupport.WriteFile(Path.Combine(dir, "04_search"), "provenance.json", """{"inputs": []}""");
        Assert.That(() => DepositLabelReader.Read(dir, NoDecisions), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("lists no spectra file"));
        TestSupport.WriteFile(Path.Combine(dir, "04_search"), "provenance.json", "[]");
        Assert.That(() => DepositLabelReader.Read(dir, NoDecisions), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("is not a JSON object"));
    }

    [Test]
    public void ADecisionsRowShortOfItsCellsIsRefused() =>
        Assert.That(() => DepositLabelReader.LoadDecisions(Decisions("PXD1\t*\tsample_type\tunknown\t\ta\t2026-11-25")),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("by and reason are required"));

    [Test]
    public void ADepositWithNoSearchIsRefused()
    {
        string dir = Path.Combine(TestSupport.TempDir(), "PXD000002");
        Directory.CreateDirectory(dir);
        Assert.That(() => DepositLabelReader.Read(dir, NoDecisions), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("no search record"));
    }

    [TestCase("run/PXD1/02_fetch/spectra/x.raw", "x.raw")]
    [TestCase("run/PXD1/02_fetch/spectra/x.mzML", "x.mzML")]
    [TestCase("run/PXD1/02_fetch/spectra/x.d/analysis.tdf", "x.d")]
    [TestCase("run/PXD1/02_fetch/spectra/x.toml", null)]
    [TestCase("run/PXD1/04_search/design/x.sdrf.tsv", null)]
    public void ASearchInputIsARunOnlyWhenItIsASpectraFile(string path, string? file) =>
        Assert.That(DepositLabelReader.SpectraFile(path), Is.EqualTo(file));

    [Test]
    public async Task TheLabelsVerbWritesEachDepositsLabelsAndListsTheOnesItCannotLabel()
    {
        string a = Deposit("PXD000001"), b = Deposit("PXD000003");
        string none = Path.Combine(TestSupport.TempDir(), "PXD000004");
        Directory.CreateDirectory(none);
        string decisions = Decisions("PXD000003\t*\tsample_type\tunknown\t\ta person\t2026-11-25\theld");
        string outDir = TestSupport.TempDir();
        var sw = new StringWriter();
        int rc = await Program.RunAsync(["labels", a, b, none, "--decisions", decisions, "--out", outDir], sw, CancellationToken.None);
        Assert.That(rc, Is.EqualTo(0), sw.ToString());
        var data = JsonDocument.Parse(sw.ToString()).RootElement.GetProperty("data");
        Assert.That(data.GetProperty("labelled").GetArrayLength(), Is.EqualTo(2));
        Assert.That(data.GetProperty("failed")[0].GetProperty("dataset").GetString(), Is.EqualTo("PXD000004"));
        Assert.That(data.GetProperty("labelled")[1].GetProperty("sample_types").GetProperty("unknown").GetInt32(), Is.EqualTo(3));

        var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "PXD000001", DepositLabelReader.JsonFile))).RootElement;
        Assert.That(written.GetProperty("schema").GetString(), Is.EqualTo("pxreprise-labels/1"));
        Assert.That(written.GetProperty("runs")[0].GetProperty("run_id").GetString(), Is.EqualTo("PXD000001:a"));
        Assert.That(written.GetProperty("cysteine").GetProperty("verdict").GetString(), Is.EqualTo("matches"));
        Assert.That(File.Exists(Path.Combine(outDir, "PXD000001", "provenance.json")));

        // One deposit and no --out: its own 07_labels. One deposit and --out: that folder itself.
        rc = await Program.RunAsync(["labels", a], new StringWriter(), CancellationToken.None);
        Assert.That(rc, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(a, DepositLabelReader.Folder, DepositLabelReader.JsonFile)));
        string single = TestSupport.TempDir();
        Assert.That(await Program.RunAsync(["labels", a, "--out", single], new StringWriter(), CancellationToken.None), Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(single, DepositLabelReader.JsonFile)));
        // One deposit that cannot be labelled is a handled failure.
        Assert.That(await Program.RunAsync(["labels", none], new StringWriter(), CancellationToken.None), Is.EqualTo(1));
        Assert.That(await Program.RunAsync(["labels"], new StringWriter(), CancellationToken.None), Is.EqualTo(2));
    }
}
