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
    internal static string Psms(IEnumerable<Dictionary<string, string>> rows)
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

    internal static Dictionary<string, string> Row(string file, string seq, string organism = "Mus musculus", string accession = "Q8BPB5",
        string? full = null, string dct = "T", string q = "0.001") => new()
    {
        ["File Name"] = file, ["Base Sequence"] = seq, ["Full Sequence"] = full ?? seq, ["Organism Name"] = organism,
        ["Accession"] = accession, ["Decoy/Contaminant/Target"] = dct, ["QValue"] = q,
    };

    // D87 (the user, 2026-11-22): one filter, set in the search. The probe search writes only the matches its filter
    // passed (WriteHighQValuePsms = false), so the measures filter nothing again: a row's own q-value decides nothing
    // (HIGHQK, EDGEQK count). Decoys and contaminants still count for no run.
    [Test]
    public void MeasuresAreCountedPerRunOverTheTargetsTheProbeSearchWrote()
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
            Row("a", "HIGHQK", q: "0.05"),          // the search's filter wrote it: it counts
            Row("a", "EDGEQK", q: "0.01"),
            Row("b", "OTHERFILEK"),
        });
        var m = ProbeMeasures.FromAllPsms(path);
        Assert.Multiple(() =>
        {
            Assert.That(m.Keys, Is.EquivalentTo(new[] { "a", "b" }));
            var a = m["a"];
            Assert.That(a.Psms, Is.EqualTo(8));
            Assert.That(a.SpeciesPsms["Mus musculus"], Is.EqualTo(6));
            Assert.That(a.SpeciesPsms["Homo sapiens"], Is.EqualTo(1));
            Assert.That(a.SharedSpeciesPsms, Is.EqualTo(1));
            Assert.That((a.Peptides, a.CysteinePeptides), Is.EqualTo((8, 1)));
            Assert.That((a.PhosphoPsms, a.GgPsms), Is.EqualTo((1, 1)));
        });
    }

    // v0.4.13: MetaMorpheus keeps a panel entry whose accession a species database also holds as that species' target, so a
    // human keratin from handling (P13645, KRT10) is a human target PSM. Any candidate from the panel takes the PSM out of
    // the species tally; the other measures still count it.
    [Test]
    public void AContaminantPanelProteinCountsForNoSpecies()
    {
        string path = Psms(new[]
        {
            Row("a", "MOUSEK"),
            Row("a", "SQYEQLAEQNRK", organism: "Homo sapiens", accession: "P13645"),
            Row("a", "SHAREDKRTK", organism: "Homo sapiens|Mus musculus", accession: "P35527|Q6IFZ6"),
            Row("a", "HUMANK", organism: "Homo sapiens", accession: "P2"),
        });
        var panel = new HashSet<string>(StringComparer.Ordinal) { "P13645", "P35527" };
        var m = ProbeMeasures.FromAllPsms(path, panel)["a"];
        var without = ProbeMeasures.FromAllPsms(path)["a"];
        Assert.Multiple(() =>
        {
            Assert.That(m.SpeciesPsms, Is.EquivalentTo(new Dictionary<string, int> { ["Mus musculus"] = 1, ["Homo sapiens"] = 1 }));
            Assert.That((m.SharedSpeciesPsms, m.PanelPsms, m.Psms, m.UnambiguousPsms), Is.EqualTo((0, 2, 4, 4)));
            Assert.That((without.SpeciesPsms["Homo sapiens"], without.SharedSpeciesPsms, without.PanelPsms), Is.EqualTo((2, 1, 0)));
        });
    }

    /// <summary>
    /// PXD022196 (mouse gel bands) parked <c>waiting_species</c> on 2026-10-13: its low-yield bands carried more human
    /// keratin PSMs than mouse ones (WT_band_08: human 504, mouse 76). v0.4.13 left the panel out; on the three-file probe
    /// a retry runs (17:56 UTC), WT_band_08 was still 55 human (skin proteins the panel lacks) vs 49 mouse, so v0.4.15 judges
    /// a run only from 500 species-specific PSMs. Reads the batch's own probe output where it exists (a retry rewrites it).
    /// </summary>
    [Test]
    public void PXD022196sGelBandsAreMouseOnceThePanelIsLeftOut()
    {
        const string psms = @"F:\aging_data\run_2026-09-21\PXD022196\02c_probe_search\mm\Task1SearchTask\AllPSMs.psmtsv";
        if (!File.Exists(psms)) Assert.Ignore($"{psms} is not on this computer");
        var panel = SearchMetrics.PrimaryAccessions(Path.Combine(TestSupport.ProfilesDir, "lists", "MetaMorpheusContaminants_NoUPS.xml"));
        var before = ProbeJudge.Judge(ProbeMeasures.FromAllPsms(psms), "Mus musculus", true, false);
        var runs = ProbeMeasures.FromAllPsms(psms, panel);
        var after = ProbeJudge.Judge(runs, "Mus musculus", true, false);
        Assert.Multiple(() =>
        {
            Assert.That(before.Parks.Select(p => p.Park), Does.Contain("waiting_species"), "the park reproduces without the panel");
            Assert.That(after.Parks, Is.Empty, string.Join("; ", after.Parks.Select(p => p.Evidence)));
            Assert.That(runs.Values.All(r => r.PanelPsms > 0), "every band carries panel PSMs");
            Assert.That(runs.Values.Count(r => r.SpeciesSpecificPsms >= ProbeJudge.MinSpeciesPsms), Is.GreaterThan(0), "at least one band is judged, as mouse");
        });
    }

    // v0.4.15: PXD022196's three-file probe (17:56 UTC), panel left out. A weak band is noted, not judged; the strong ones are.
    [Test]
    public void AWeakRunTippedBySkinProteinsIsNotedNotJudged()
    {
        var runs = new Dictionary<string, RunMeasures>
        {
            ["5-K0_band_01"] = Run(mouse: 3823, human: 70),
            ["WT_band_06"] = Run(mouse: 1178, human: 38),
            ["WT_band_08"] = Run(mouse: 49, human: 55),
        };
        var v = ProbeJudge.Judge(runs, "Mus musculus", true, false);
        Assert.Multiple(() =>
        {
            Assert.That(v.Parks, Is.Empty, string.Join("; ", v.Parks.Select(p => p.Evidence)));
            Assert.That(v.Notes, Does.Contain("WT_band_08: species not judged (104 species-specific PSMs < 500)"));
        });
        // A strong run of another species still parks, however strong the others are (PXD044773's mouse runs: ~11,000-17,000).
        runs["WT_band_08"] = Run(mouse: 49, human: 900);
        Assert.That(ProbeJudge.Judge(runs, "Mus musculus", true, false).Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_species" }));
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
        var few = ProbeJudge.Judge(new Dictionary<string, RunMeasures> { ["a"] = Run(mouse: 30, shared: 40) }, "Homo sapiens", true, false);
        Assert.That((few.Parks.Count, few.Notes.Single(n => n.Contains("not judged"))), Is.EqualTo((0, "a: species not judged (30 species-specific PSMs < 500)")));
    }

    // v0.4.14: PXD000046 (dimethyl-labelled, read as label-free) had 0 PSMs in all three probe runs and passed on no evidence.
    [Test]
    public void AProbeThatIdentifiesNothingParksInsteadOfPassing()
    {
        string path = Psms(Array.Empty<Dictionary<string, string>>());
        var none = ProbeMeasures.FromAllPsms(path);
        var v = ProbeJudge.Judge(none, "Mus musculus", true, false, new[] { "f10", "f9_2", "fr10" });
        Assert.Multiple(() =>
        {
            Assert.That(none, Is.Empty);
            Assert.That(v.Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_no_identifications" }));
            Assert.That(v.Parks[0].Evidence, Is.EqualTo("no probe run reaches 50 PSMs at 1% FDR: f10 0; f9_2 0; fr10 0"));
        });
        var thin = new Dictionary<string, RunMeasures> { ["a"] = Run(mouse: 30, shared: 19), ["b"] = Run(mouse: 2) };
        Assert.That(ProbeJudge.Judge(thin, "Mus musculus", true, false).Parks.Select(p => p.Park), Is.EqualTo(new[] { "waiting_no_identifications" }));
        // One run with enough is enough: a deposit's weakest bands are judged beside its others (PXD022196).
        var oneBand = new Dictionary<string, RunMeasures> { ["a"] = Run(mouse: 30, shared: 20), ["b"] = Run(mouse: 2) };
        Assert.That(ProbeJudge.Judge(oneBand, "Mus musculus", true, false).Parks, Is.Empty);
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
            Assert.That(text, Does.Contain("PrecursorMassTolerance = \"±20.0000 PPM\"").And.Not.Contain("PrecursorMassTolerance = \"±5.0000 PPM\""),
                "no calibration in the probe: PXD051644's instrument was off by +7.6 ppm");
        });
        Assert.Throws<SearchSetupException>(() => ProbeSearch.TaskText(defaults, m, null, new HashSet<(string, string)>()),
            "a modification the pinned MetaMorpheus lacks is refused, never silently skipped");
    }

    // D87 (the user, 2026-11-22): "just setting the one notch filter in the search should accomplish everything". The probe
    // is a search too, so it takes its profile's identification filter; a profile that sets none keeps MetaMorpheus's own.
    [Test]
    public void TheProbeFiltersItsMatchesAsItsProfilesSearchDoes()
    {
        string defaults = File.ReadAllText(Fixture("mm-1.1.15", "SearchTask.toml"));
        var known = new HashSet<(string, string)>
        {
            ("Common Biological", "Phosphorylation on S"), ("Common Biological", "Phosphorylation on T"), ("Common Biological", "Phosphorylation on Y"),
            ("Trypsin Digested", "GG (Ubiquitination Site) on K"), ("Common Biological", "Acetylation on K"),
        };
        var m = Config.Machine.Load(TestSupport.WriteFile(TestSupport.TempDir(), "m.toml",
            "work_root = \"F:/w\"\nmax_threads = 8\n[metamorpheus]\n\"1.1.15\" = \"C:/mm/CMD.exe\"\n"));
        var profiles = Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var notch = new[] { ("IdentificationFilter", "\"QValueNotch\""), ("FilterThreshold", "0.01") };
        Assert.Multiple(() =>
        {
            foreach (string key in new[] { "label-free-dda@15", "label-free-dda-lowres@9", "tmt-dda@6" })
            {
                var filter = ProbeSearch.FilterSettings(profiles[key]);
                Assert.That(filter.Select(s => (s.Key, s.TomlValue)), Is.EquivalentTo(notch), key);
                string text = ProbeSearch.TaskText(defaults, m, null, known, filter);
                Assert.That(text, Does.Contain("IdentificationFilter = \"QValueNotch\"").And.Not.Contain("PepQValue"), key);
                Assert.That(text, Does.Contain("FilterThreshold = 0.01"), key);
            }
            Assert.That(ProbeSearch.FilterSettings(profiles["label-free-dda@14"]), Is.Empty, "no filter set: MetaMorpheus's own");
            Assert.That(ProbeSearch.FilterSettings(profiles["label-free-dda@1"]), Is.Empty, "no [metamorpheus_settings] at all");
            Assert.That(ProbeSearch.TaskText(defaults, m, null, known), Does.Contain("IdentificationFilter = \"PepQValue\""),
                "without a profile filter the default task's stays");
        });
        Assert.Throws<SearchSetupException>(() => ProbeSearch.TaskText(File.ReadAllText(Fixture("mm-1.1.12", "SearchTask.toml")), m, null, known,
            ProbeSearch.FilterSettings(profiles["tmt-dda@6"])), "a MetaMorpheus without the setting cannot filter as the profile asks: refused, never skipped");
    }

    [Test]
    public void TheNewParksAreAnnouncedAndOnlyLabelFreeDda7AndLowres3TurnTheProbeOn()
    {
        foreach (string park in new[] { "waiting_species", "waiting_mixed_species", "waiting_alkylation_check", "waiting_no_identifications" })
            Assert.That(DecisionsNeeded.For(park), Is.Not.Null, park);
        // The user, 2026-10-12: "Yes to the preliminary search". Profiles already used keep it off (a profile never changes).
        // lowres@3 (2026-10-17) runs it to measure the precursor offset its uncalibrated search must hold.
        Assert.That(Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir).Where(kv => kv.Value.ProbeSearch).Select(kv => kv.Key),
            Is.EquivalentTo(new[] { "label-free-dda@7", "label-free-dda@8", "label-free-dda@9", "label-free-dda@10", "label-free-dda@11", "label-free-dda@12", "label-free-dda@13", "label-free-dda@14", "label-free-dda@15", "label-free-dda@16", "label-free-dda-uniprot@1", "label-free-dda-lowres@3", "label-free-dda-lowres@4", "label-free-dda-lowres@5", "label-free-dda-lowres@6", "label-free-dda-lowres@7", "label-free-dda-lowres@8", "label-free-dda-lowres@9", "label-free-dda-lowres@10" }));
    }

    [Test]
    public void TheProbeMeasuresEachRunsPrecursorOffsetAndTheWindowHoldsTheLargest()
    {
        // PXD045821 (2026-10-17): +8.7 ppm in every run. Notch 0 and notch +1 PSMs measure the same offset; decoys and runs
        // with fewer than 20 PSMs measure none. No q-value is read: the probe search wrote only what its filter passed (D87).
        string dir = TestSupport.TempDir();
        var sb = new System.Text.StringBuilder("File Name\tQValue\tDecoy/Contaminant/Target\tMass Diff (Da)\tMonoisotopic Mass\n");
        string Da(double ppm, double mass, int notch) => (notch * Chemistry.Constants.C13MinusC12 + ppm * mass / 1e6).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < 30; i++)
        {
            double ppm = 8.7 + (i % 5 - 2) * 0.5;   // 7.7 .. 9.7, median 8.7
            sb.Append($"offset\t0.001\tT\t{Da(ppm, 1500 + i, i % 3 == 0 ? 1 : 0)}|0.5\t{1500 + i}|1600\n");
            sb.Append($"centred\t0.002\tT\t{Da((i % 5 - 2) * 0.4, 2000 + i, 0)}\t{2000 + i}\n");
        }
        for (int i = 0; i < 40; i++) sb.Append($"centred\t0.002\tD\t{Da(-15, 1800, 0)}\t1800\n");    // decoys say nothing
        for (int i = 0; i < 19; i++) sb.Append($"thin\t0.001\tT\t{Da(12, 1800, 0)}\t1800\n");          // 19 < 20
        string psms = TestSupport.WriteFile(dir, "AllPSMs.psmtsv", sb.ToString());

        var offsets = Qc.ProbeMeasures.PrecursorOffsets(psms);
        Assert.That(offsets.Keys, Is.EquivalentTo(new[] { "offset", "centred" }));
        Assert.That(offsets["offset"], Is.EqualTo(8.7).Within(0.01));
        Assert.That(offsets["centred"], Is.EqualTo(0).Within(0.01));
        Assert.Multiple(() =>
        {
            Assert.That(Qc.ProbeMeasures.WindowPpm(offsets.Values), Is.EqualTo(14), "5 ppm beyond +8.7, rounded up");
            Assert.That(Qc.ProbeMeasures.WindowPpm(new[] { 0.0, -1.9, 2.0 }), Is.Null, "within 2 ppm: MetaMorpheus's ±5 holds them");
            Assert.That(Qc.ProbeMeasures.WindowPpm(new[] { 0.3, -6.2 }), Is.EqualTo(12), "the largest offset either way");
            Assert.That(Qc.ProbeMeasures.WindowPpm(new[] { 19.0 }), Is.EqualTo(20), "never wider than the probe saw");
            Assert.That(Qc.ProbeMeasures.WindowPpm(Array.Empty<double>()), Is.Null);
            Assert.That(Qc.ProbeMeasures.WindowPpm(null), Is.Null);
        });
        Assert.Throws<InvalidDataException>(() => Qc.ProbeMeasures.PrecursorOffsets(TestSupport.WriteFile(dir, "b.psmtsv", "File Name\tQValue\tDecoy/Contaminant/Target\n")),
            "a probe output without the mass columns is an error, never an offset of 0");
    }

    /// <summary><c>search --probe-offsets</c> reads a JSON object of run name to offset in ppm; anything else is a usage error.</summary>
    [TestCase("{\"a\": 9.4, \"b\": -1.5}", null)]
    [TestCase(null, "does not exist")]
    [TestCase("not json", "is not JSON")]
    [TestCase("[]", "must be a JSON object")]
    [TestCase("{}", "must be a JSON object")]
    [TestCase("{\"a\": \"x\"}", "run 'a' has no number")]
    public void ProbeOffsetsAreAJsonObjectOfRunToPpm(string? json, string? error)
    {
        string dir = TestSupport.TempDir();
        string path = json is null ? Path.Combine(dir, "none.json") : TestSupport.WriteFile(dir, "offsets.json", json);
        if (error is null)
            Assert.That(Program.ReadProbeOffsets(path), Is.EqualTo(new Dictionary<string, double> { ["a"] = 9.4, ["b"] = -1.5 }));
        else
            Assert.That(Assert.Throws<Cli.UsageException>(() => Program.ReadProbeOffsets(path))!.Message, Does.Contain(error));
    }

    /// <summary>
    /// The search verb takes --probe-offsets into its request, and fetch takes --include. Both stop before any work here: the
    /// search has no database for the organism, and fetch has no machine file.
    /// </summary>
    [Test]
    public async Task TheSearchAndFetchVerbsTakeTheirNewOptions()
    {
        string root = TestSupport.TempDir();
        string machine = TestSupport.WriteFile(root, "machine.toml",
            $"work_root = '{root}'\ndatabase_dir = '{root}'\nmax_threads = 1\n[metamorpheus]\n\"1.1.11\" = '{Path.Combine(root, "CMD.dll")}'\n");
        string offsets = TestSupport.WriteFile(root, "offsets.json", "{\"a\": 9.4}");
        async Task<(int Exit, string Out)> Run(params string[] argv)
        {
            var sw = new StringWriter();
            int exit = await Program.RunAsync(argv, sw, CancellationToken.None);
            return (exit, sw.ToString());
        }

        var search = await Run("search", "--profile", "label-free-dda@1", "--profiles", TestSupport.ProfilesDir, "--organism", "no-such-organism",
            "--machine", machine, "--spectra", root, "--qc", Path.Combine(root, "qc.json"), "--out", Path.Combine(root, "out"), "--probe-offsets", offsets);
        Assert.That(search.Out, Does.Contain("has no database for 'no-such-organism'"), "the request was built, offsets included");
        var noOffsets = await Run("search", "--profile", "label-free-dda@1", "--profiles", TestSupport.ProfilesDir, "--organism", "no-such-organism",
            "--machine", machine, "--spectra", root, "--qc", Path.Combine(root, "qc.json"), "--out", Path.Combine(root, "out2"));
        Assert.That(noOffsets.Out, Does.Contain("has no database for 'no-such-organism'"), "--probe-offsets is optional");

        var fetch = await Run("fetch", "PXD000001", "--out", Path.Combine(root, "fetch"), "--machine", Path.Combine(root, "none.toml"), "--include", @"\.raw$");
        Assert.That((fetch.Exit, fetch.Out.Contains("none.toml")), Is.EqualTo((2, true)), "it stops at the missing machine file, after reading --include");
    }
}
