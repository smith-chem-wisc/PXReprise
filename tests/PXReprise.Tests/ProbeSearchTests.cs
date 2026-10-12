using PXReprise.Discovery;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// D32/D34/D35 slice 3: the probe search measures each probe run's species, cysteine share and phospho/GG share, and the
/// measurements that contradict the record park the deposit. The PSM file is read through mzLib's LightWeightSpectralMatchReader
/// (the oracle of 2026-10-07); the template row is a real 1.1.12 AllPSMs.psmtsv row from the PXD017058 trial.
/// </summary>
public class ProbeSearchTests
{
    private static string Fixture(params string[] parts) => Path.Combine(new[] { TestContext.CurrentContext.TestDirectory, "Fixtures" }.Concat(parts).ToArray());

    /// <summary>A PSM file of rows made from the template, each with the given cells changed.</summary>
    private static string Psms(IEnumerable<Dictionary<string, string>> rows)
    {
        var lines = File.ReadAllLines(Fixture("probe", "AllPSMs_template.psmtsv"));
        var header = lines[0].Split('\t');
        var template = lines[1].Split('\t');
        var outLines = new List<string> { lines[0] };
        foreach (var row in rows)
        {
            var cells = (string[])template.Clone();
            foreach (var (col, value) in row) cells[Array.IndexOf(header, col)] = value;
            outLines.Add(string.Join('\t', cells));
        }
        string path = Path.Combine(TestSupport.TempDir(), "AllPSMs.psmtsv");
        File.WriteAllLines(path, outLines);
        return path;
    }

    private static Dictionary<string, string> Row(string file, string seq, string organism = "Mus musculus", string accession = "Q8BPB5",
        string? full = null, string dct = "T", string q = "0.001") => new()
    {
        ["File Name"] = file, ["Base Sequence"] = seq, ["Full Sequence"] = full ?? seq, ["Organism Name"] = organism,
        ["Accession"] = accession, ["Decoy/Contaminant/Target"] = dct, ["QValue"] = q,
    };

    [Test]
    public void MeasuresAreCountedPerRunOverOnePercentTargetsOnly()
    {
        string path = Psms(new[]
        {
            Row("a", "PEPTIDEK"),
            Row("a", "PEPCTIDEK"),
            Row("a", "PEPSIDEK", full: "PEPS[Common Biological:Phosphorylation on S]IDEK"),
            Row("a", "PEPKIDER", full: "PEPK[Trypsin Digested:GG (Ubiquitination Site) on K]IDER"),
            Row("a", "SHAREDK", organism: "Homo sapiens|Mus musculus", accession: "P1|Q1"),
            Row("a", "HUMANK", organism: "Homo sapiens", accession: "P2"),
            Row("a", "DECOYK", dct: "D"),
            Row("a", "CONTAMK", dct: "C"),
            Row("a", "HIGHQK", q: "0.05"),
            Row("a", "EDGEQK", q: "0.01"),          // strict: q exactly 0.01 is out (D36's comparison)
            Row("b", "OTHERFILEK"),
        });
        var m = ProbeMeasures.FromAllPsms(path);
        Assert.Multiple(() =>
        {
            Assert.That(m.Keys, Is.EquivalentTo(new[] { "a", "b" }));
            var a = m["a"];
            Assert.That(a.Psms, Is.EqualTo(6));
            Assert.That(a.SpeciesPsms["Mus musculus"], Is.EqualTo(4));
            Assert.That(a.SpeciesPsms["Homo sapiens"], Is.EqualTo(1));
            Assert.That(a.SharedSpeciesPsms, Is.EqualTo(1));
            Assert.That((a.Peptides, a.CysteinePeptides), Is.EqualTo((6, 1)));
            Assert.That((a.PhosphoPsms, a.GgPsms), Is.EqualTo((1, 1)));
        });
    }

    [Test]
    public void AMissingColumnIsFatalNotAnEmptyResult()
    {
        string path = Psms(new[] { Row("a", "PEPTIDEK") });
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, lines.Select(l => l.Replace("Organism Name", "Organism")));
        Assert.Throws<InvalidDataException>(() => ProbeMeasures.FromAllPsms(path));
    }

    private static RunMeasures Run(int mouse = 0, int human = 0, int shared = 0, int peptides = 500, int cys = 75, int phospho = 1, int gg = 1, int unambiguous = 500) =>
        new(mouse + human + shared, new Dictionary<string, int> { ["Mus musculus"] = mouse, ["Homo sapiens"] = human }.Where(kv => kv.Value > 0).ToDictionary(),
            shared, unambiguous, peptides, cys, phospho, gg);

    [Test]
    public void TheExpectedSpeciesPassesAndAnotherSpeciesOrAMixParks()
    {
        Assert.That(ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, mouse: 30) }, "Homo sapiens", true, false).Parks, Is.Empty);
        // PXD044773 (aging S83): mouse runs in a deposit searched as human.
        var wrong = ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900), ["b"] = Run(mouse: 850, human: 40) }, "Homo sapiens", true, false);
        Assert.That(wrong.Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_species" }));
        Assert.That(wrong.Parks[0].Evidence, Does.Contain("b (Mus musculus 850"));
        // The expected species leads but holds under half (a xenograft-like run): mixed.
        var mixed = ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 400, mouse: 350) with
            { SpeciesPsms = new Dictionary<string, int> { ["Homo sapiens"] = 400, ["Mus musculus"] = 350, ["Rattus norvegicus"] = 250 } } },
            "Homo sapiens", true, false);
        Assert.That(mixed.Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_mixed_species" }));
        // G31 calibration: pure rat muscle is only 62% rat (reviewed rat is thin) and must pass; so must a run at 55%.
        var ratMuscle = new Dictionary<string, RunMeasures> { ["a"] = Run(mouse: 1176) with
            { SpeciesPsms = new Dictionary<string, int> { ["Rattus norvegicus"] = 2743, ["Mus musculus"] = 1176, ["Homo sapiens"] = 478 } } };
        Assert.That(ProbeJudge.Judge(ratMuscle, "Rattus norvegicus", true, false).Parks, Is.Empty);
        Assert.That(ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 500, mouse: 400) }, "Homo sapiens", true, false).Parks, Is.Empty);
        var few = ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(mouse: 30) }, "Homo sapiens", true, false);
        Assert.That((few.Parks.Count, few.Notes.Single(n => n.Contains("not judged"))), Is.EqualTo((0, "a: species not judged (30 species-specific PSMs < 50)")));
    }

    [Test]
    public void FewCysteinePeptidesParkOnlyWhenCarbamidomethylWasExpectedAndNothingExplainsIt()
    {
        // aging S82: PXD035171 says chloroacetamide but shows 0.26% cysteine peptides.
        var low = new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, cys: 1) };
        Assert.That(ProbeJudge.Judge(low, "Homo sapiens", true, false).Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_alkylation_check" }));
        Assert.That(ProbeJudge.Judge(low, "Homo sapiens", false, false).Parks, Is.Empty, "NEM/MMTS searched as such: not carbamidomethyl");
        Assert.That(ProbeJudge.Judge(low, "Homo sapiens", true, true).Parks, Is.Empty, "a screened enrichment explains a low share (2-5%)");
        var lowPhospho = new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, cys: 1, phospho: 40) };
        var v = ProbeJudge.Judge(lowPhospho, "Homo sapiens", true, false);
        Assert.That((v.Parks.Count, v.MeasuredEnrichment), Is.EqualTo((0, "phospho")), "a MEASURED enrichment explains it too");
        var tiny = new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, peptides: 50, cys: 0) };
        Assert.That(ProbeJudge.Judge(tiny, "Homo sapiens", true, false).Parks, Is.Empty, "under 100 peptides: not judged");
        Assert.That(ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, cys: 10) }, "Homo sapiens", true, false).Parks, Is.Empty, "exactly 2% passes");
    }

    // PXR-A88 (aging 082): PXD061654's acetyl-lysine captures say so in no file name, so only the probe can see them.
    // Acetyl-K is MetaMorpheus's own modification or a UniProt site from the database; N-terminal acetylation is neither.
    [Test]
    public void AcetylKIsMeasuredUnderEitherNameAndAnAcetylCaptureIsAnEnrichment()
    {
        string path = Psms(new[]
        {
            Row("cap", "PEPKIDER", full: "PEPK[Common Biological:Acetylation on K]IDER"),
            Row("cap", "PEPKIDEK", full: "PEPK[UniProt:N6-acetyllysine on K]IDEK"),
            Row("cap", "PEPTIDEK"),
            Row("cap", "PEPTIDER"),
            Row("cap", "MPEPTIDEK", full: "[Common Biological:Acetylation on X]MPEPTIDEK"),
        });
        var m = ProbeMeasures.FromAllPsms(path)["cap"];
        Assert.That((m.AcetylKPsms, m.UnambiguousPsms), Is.EqualTo((2, 5)));

        var runs = new Dictionary<string, RunMeasures>
        {
            ["cap"] = Run(mouse: 900) with { AcetylKPsms = 140 },     // 28%, as PXD038328's capture runs
            ["whole"] = Run(mouse: 900) with { AcetylKPsms = 2 },     // 0.4%, a whole proteome
        };
        var v = ProbeJudge.Judge(runs, "Mus musculus", true, false);
        Assert.Multiple(() =>
        {
            Assert.That(v.MeasuredEnrichment, Is.EqualTo("acetyl"));
            Assert.That(v.Notes.Single(n => n.StartsWith("measured", StringComparison.Ordinal)), Does.Contain("in 1 of 2 probe run(s)").And.Contain("acetyl-K 28.0 %"));
            Assert.That(ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["whole"] = runs["whole"] }, "Mus musculus", true, false).MeasuredEnrichment,
                Is.Null, "a whole proteome's acetyl-K is no enrichment");
        });
    }

    /// <summary>
    /// The G31 calibration (results/calibration_probe_2026-10-07, real probe searches of 14 deposits) replayed through the
    /// species rule: every single-species run passes, rat muscle included, and only PXD044773's two mouse runs (recorded
    /// as human; aging S83) are called another species.
    /// </summary>
    [Test]
    public void TheCalibrationRunsAreJudgedAsTheirDepositsAre()
    {
        string tsv = Fixture("probe", "calibration_2026-10-07.tsv");   // a copy of results/calibration_probe_2026-10-07/calibration.tsv
        var lines = File.ReadAllLines(tsv);
        var h = lines[0].Split('\t');
        int C(string name) => Array.IndexOf(h, name);
        var latin = new Dictionary<string, string> { ["human"] = "Homo sapiens", ["mouse"] = "Mus musculus", ["rat"] = "Rattus norvegicus" };
        var called = new List<string>();
        foreach (var r in lines.Skip(1).Select(l => l.Split('\t')))
        {
            var species = new Dictionary<string, int>
            {
                ["Homo sapiens"] = int.Parse(r[C("human")]), ["Mus musculus"] = int.Parse(r[C("mouse")]), ["Rattus norvegicus"] = int.Parse(r[C("rat")]),
            };
            var m = new RunMeasures(int.Parse(r[C("psms")]), species, int.Parse(r[C("shared")]), 1000, 1000, 150, 1, 1);
            var v = ProbeJudge.Judge(new Dictionary<string, RunMeasures> { [r[C("run")]] = m }, latin[r[C("expected")]], false, false);
            called.AddRange(v.Parks.Select(p => $"{r[C("accession")]} {r[C("run")]} {p.Park}"));
        }
        Assert.That(lines, Has.Length.GreaterThan(30), "the committed calibration is read");
        Assert.That(called, Is.EquivalentTo(new[]
        {
            "PXD044773 QEp21-5401_Norton_294D waiting_species", "PXD044773 QEp21-5426_Norton_303V waiting_species",
        }));
    }

    [TestCase(25, 1, "phospho")]
    [TestCase(1, 10, "GG")]
    [TestCase(25, 10, "phospho+GG")]
    [TestCase(24, 9, null)]   // 4.8% and 1.8%: whole-proteome background, not enrichment
    public void EnrichmentIsMeasuredAtTheBuildersThresholds(int phospho, int gg, string? expected) =>
        Assert.That(ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(human: 900, phospho: phospho, gg: gg) }, "Homo sapiens", true, false).MeasuredEnrichment,
            Is.EqualTo(expected));

    [Test]
    public void TheProbeTaskIsFastAndAddsPhosphoAndGgOnTheDepositsChemistry()
    {
        string defaults = File.ReadAllText(Fixture("mm-1.1.12", "SearchTask.toml"));
        var known = new HashSet<(string, string)>
        {
            ("Common Biological", "Phosphorylation on S"), ("Common Biological", "Phosphorylation on T"), ("Common Biological", "Phosphorylation on Y"),
            ("Trypsin Digested", "GG (Ubiquitination Site) on K"), ("Common Biological", "Acetylation on K"), ("Common Variable", "Oxidation on M"),
            ("Common Fixed", "Carbamidomethyl on C"), ("Common Fixed", "Carbamidomethyl on U"),
        };
        var m = Config.Machine.Load(TestSupport.WriteFile(TestSupport.TempDir(), "m.toml",
            "work_root = \"F:/w\"\nmax_threads = 8\n[metamorpheus]\n\"1.1.12\" = \"C:/mm/CMD.exe\"\n"));
        string text = ProbeSearch.TaskText(defaults, m, null, known);
        Assert.Multiple(() =>
        {
            foreach (var (key, value) in ProbeSearch.FastSettings) Assert.That(text, Does.Contain($"{key} = {value}"), key);
            Assert.That(text, Does.Contain("SearchType = \"Classic\"").And.Contain("MatchBetweenRuns = false").And.Contain("MaxThreadsToUsePerFile = 8"));
            Assert.That(text, Does.Contain(@"Common Biological\tPhosphorylation on Y\t\tTrypsin Digested\tGG (Ubiquitination Site) on K\t\tCommon Biological\tAcetylation on K"));
            Assert.That(text, Does.Contain("MaxModsForPeptide = 2"), "MetaMorpheus's default: more mods per peptide explode the probe");
        });
        Assert.Throws<SearchSetupException>(() => ProbeSearch.TaskText(defaults, m, null, new HashSet<(string, string)>()),
            "a modification the pinned MetaMorpheus lacks is refused, never silently skipped");
    }

    [Test]
    public void TheNewParksAreAnnouncedAndNoShippedProfileTurnsTheProbeOn()
    {
        foreach (string park in new[] { "waiting_species", "waiting_mixed_species", "waiting_alkylation_check" })
            Assert.That(DecisionsNeeded.For(park), Is.Not.Null, park);
        Assert.That(Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir).Values.Select(p => p.ProbeSearch), Has.All.False);
    }
}
