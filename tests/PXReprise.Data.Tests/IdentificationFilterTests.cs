using PXReprise.Ingest;
using PXReprise.Ingest.Sources;

namespace PXReprise.Data.Tests;

/// <summary>
/// D84: MetaMorpheus 1.1.15 (#2982, #2989) names a search's identification filter on every results.txt count line, and
/// the search counts its matches by that filter alone. datarepo reads the new lines, and its own producer counts follow
/// the named filter, so the reconciliation compares like with like. Up to 1.1.14 the lines said "q-value", and the rule
/// was the q-value and the notch together.
/// </summary>
public class IdentificationFilterTests
{
    private static string ResultsTxt(string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "datarepo-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "results.txt");
        File.WriteAllText(path, text);
        return path;
    }

    // PXD058082 under MetaMorpheus #2982's PEP build (the worker's filter trial f2, 2026-11-18), lines as written. PEP
    // trained there, so no "PEP filtering was requested, but ..." sentence.
    private const string PepLines = """
        All target PSMs with pep q-value <= 0.01: 52778
        All target peptides with pep q-value <= 0.01: 6772
        All target protein groups with pep q-value <= 0.01 (1% FDR): 1607
        PSMs with pep q-value <= 0.01: 53229
        Number of proteins with q-value <= 0.01: 1620
        """;

    // Where PEP was not trained, MetaMorpheus 1.1.15 writes the sentence on the line just above the count it qualifies,
    // all-files and per-file (PostSearchAnalysisTask.WithPepFilteringNotDone; FilteredPsms.PepNotTrainedSentence).
    private const string PepNotTrainedLines = """
        PEP filtering was requested, but PEP was not trained for 7 target PSMs (trypsin: 7), so those were filtered by q-value notch <= 0.01.
        All target PSMs with pep q-value <= 0.01: 318
        All target peptides with pep q-value <= 0.01: 84
        PEP filtering was requested, but PEP was not trained for 3 target PSMs (trypsin: 3), so those were filtered by q-value notch <= 0.01.
        lfq_a - Target PSMs with pep q-value <= 0.01: 104
        All target protein groups with pep q-value <= 0.01 (1% FDR): 20
        PSMs with pep q-value <= 0.01: 330
        """;

    [Test]
    public void TheSentenceAboveACountSaysPepWasNotTrainedForItAndTheEngineLineSaysWhichWording()
    {
        PXReprise.Ingest.Readers.ReadResultsTxtAndFilter(ResultsTxt(PepNotTrainedLines), out ResultsTxtFilter read);
        Assert.Multiple(() =>
        {
            Assert.That(read.Named, Is.EqualTo("pep q-value"));
            Assert.That(read.PepNotTrained, Is.EquivalentTo(new[] { "psms" }),
                "the all-files PSM line has the sentence; the peptide line has none, and a per-file sentence is not carried on");
            Assert.That(read.FdrEngineLineNamesFilter, Is.True, "1.1.15's engine line names the filter");
        });

        PXReprise.Ingest.Readers.ReadResultsTxtAndFilter(ResultsTxt(PepLines), out ResultsTxtFilter trained);
        Assert.That(trained.PepNotTrained, Is.Empty, "no sentence: PEP trained for every match");
        PXReprise.Ingest.Readers.ReadResultsTxtAndFilter(ResultsTxt("All target PSMs with q-value <= 0.01: 42125\nPSMs within 1% FDR: 42736\n"),
            out ResultsTxtFilter old);
        Assert.That(old.FdrEngineLineNamesFilter, Is.False, "up to 1.1.14 the engine line reads 'within 1% FDR'");
        PXReprise.Ingest.Readers.ReadResultsTxtAndFilter(ResultsTxt("All target PSMs with q-value <= 0.01: 42125\n"), out ResultsTxtFilter none);
        Assert.That(none.FdrEngineLineNamesFilter, Is.Null, "no engine line: not known");
    }

    [Test]
    public void TheCountsAndTheFilterOf115sLinesAreRead()
    {
        var results = PXReprise.Ingest.Readers.ReadResultsTxt(ResultsTxt(PepLines), out string? filter);
        Assert.That(filter, Is.EqualTo("pep q-value"));
        Assert.That(results[""], Is.EquivalentTo(new Dictionary<string, long> { ["psms"] = 52778, ["peptides"] = 6772, ["protein_groups"] = 1607 }));

        // The tests/metamorpheus-output sample under 1.1.15 with the old task keys, read as the notch alone.
        var notch = PXReprise.Ingest.Readers.ReadResultsTxt(ResultsTxt("""
            All target PSMs with q-value notch <= 0.01: 318
            lfq_a - Target PSMs with q-value notch <= 0.01: 104
            All target peptides with q-value notch <= 0.01: 84
            """), out string? notchFilter);
        Assert.That(notchFilter, Is.EqualTo("q-value notch"));
        Assert.That(notch[""]["psms"], Is.EqualTo(318));
        Assert.That(notch["lfq_a"]["psms"], Is.EqualTo(104), "the per-file line too");
    }

    [Test]
    public void TheOldWordingStillReadsAndACountAtAnotherThresholdIsNotAOnePercentCount()
    {
        var old = PXReprise.Ingest.Readers.ReadResultsTxt(ResultsTxt("""
            All target PSMs with q-value <= 0.01: 42125
            All target protein groups with q-value <= 0.01 (1% FDR): 1194
            PSMs within 1% FDR: 42736
            """), out string? oldFilter);
        Assert.That(oldFilter, Is.EqualTo("q-value"));
        Assert.That(old[""]["psms"], Is.EqualTo(42125));
        Assert.That(old[""]["protein_groups"], Is.EqualTo(1194));

        var loose = PXReprise.Ingest.Readers.ReadResultsTxt(ResultsTxt("""
            All target PSMs with q-value notch <= 0.05: 50000
            All target PSMs with pep q-value <= 1: 60000
            """), out string? looseFilter);
        Assert.That(loose.ContainsKey(""), Is.False, "neither is a 1% count");
        Assert.That(looseFilter, Is.Null);
        Assert.That(PXReprise.Ingest.Readers.ReadResultsTxt(ResultsTxt("All target PSMs with pep q-value <= 0.010: 7"), out _)[""]["psms"],
            Is.EqualTo(7), "the threshold is compared as a number");
    }

    [TestCase("pep q-value", "1.1.15", ProducerFilter.PepQValue)]
    [TestCase("q-value notch", "1.1.15", ProducerFilter.QValueNotch)]
    [TestCase("q-value", "1.1.15", ProducerFilter.QValue)]
    [TestCase("q-value", "1.1.16", ProducerFilter.QValue)]
    [TestCase("q-value", "1.1.14", ProducerFilter.QValueAndNotch)]
    [TestCase("q-value", null, ProducerFilter.QValueAndNotch)]
    [TestCase(null, "1.1.15", ProducerFilter.QValueAndNotch)]
    public void TheFilterFollowsTheNamedFilterAndTheRelease(string? named, string? release, ProducerFilter expected) =>
        Assert.That(Identifications.FilterOf(named, release), Is.EqualTo(expected));

    /// <summary>The FDR engine line's wording decides what a bare "q-value" means, however the release was written.</summary>
    [TestCase("q-value", "v1.1.15", true, ProducerFilter.QValue)]
    [TestCase("q-value", "1.1.15-rc", true, ProducerFilter.QValue)]
    [TestCase("q-value", null, true, ProducerFilter.QValue)]
    [TestCase("q-value", "1.1.15", false, ProducerFilter.QValueAndNotch)]
    [TestCase("q-value", "Not a release version", false, ProducerFilter.QValueAndNotch)]
    [TestCase("q-value", "1.1.15", null, ProducerFilter.QValue)]
    [TestCase("pep q-value", null, false, ProducerFilter.PepQValue)]
    public void TheEngineLineDecidesABareQValue(string? named, string? release, bool? engineLineNamesFilter, ProducerFilter expected) =>
        Assert.That(Identifications.FilterOf(named, release, engineLineNamesFilter), Is.EqualTo(expected));

    [TestCase(false, true, PepTraining.Every)]
    [TestCase(false, false, PepTraining.Every)]
    [TestCase(true, false, PepTraining.None)]
    [TestCase(true, true, PepTraining.Some)]
    public void WhetherPepTrainedIsTheSearchs(bool sentenceAbove, bool pepUsable, PepTraining expected) =>
        Assert.That(Identifications.PepTrainingOf(sentenceAbove, pepUsable), Is.EqualTo(expected));

    // Target rows: (q, notch q, PEP q, notch). PEP q 2 is MetaMorpheus's "not computed" (PEP not trained).
    private static Dictionary<string, List<object?>> Columns(params (string Status, double? Q, double? Notch, double? Pep, string Notches)[] rows) => new()
    {
        ["decoy_contam_target"] = rows.Select(r => (object?)r.Status).ToList(),
        ["q_value"] = rows.Select(r => (object?)r.Q).ToList(),
        ["q_value_notch"] = rows.Select(r => (object?)r.Notch).ToList(),
        ["pep_q_value"] = rows.Select(r => (object?)r.Pep).ToList(),
        ["notch"] = rows.Select(r => (object?)r.Notches).ToList(),
    };

    [Test]
    public void EachFilterCountsByItsOwnColumn()
    {
        var c = Columns(
            ("T", 0.005, 0.005, 0.005, "0"),   // passes everything
            ("T", 0.02, 0.005, 0.5, "0"),      // the notch alone
            ("T", 0.005, 0.02, 0.5, "0"),      // the q-value alone
            ("T", 0.5, 0.5, 0.008, "0"),       // the PEP q-value alone
            ("T", 0.5, 0.008, 2, "1"),         // PEP not trained: judged by the notch
            ("T", 0.005, 0.005, 0.005, "0|1"), // notch-ambiguous
            ("D", 0.001, 0.001, 0.001, "0"));  // a decoy
        Assert.Multiple(() =>
        {
            Assert.That(Identifications.ProducerCounts(c), Is.EqualTo(1), "q-value and notch, a resolved notch (up to 1.1.14)");
            Assert.That(Identifications.ProducerCounts(c, requireResolvedNotch: false), Is.EqualTo(2),
                "the peptide count keeps the notch-ambiguous row");
            // 1.1.15's filters count notch-ambiguous matches (TargetPsmsAboveThreshold), so row 6 counts under each.
            Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValueNotch), Is.EqualTo(4), "rows 1, 2, 5 and 6");
            Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValue), Is.EqualTo(3), "rows 1, 3 and 6");
            Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue), Is.EqualTo(3),
                "PEP trained for every match: rows 1, 4 and 6; row 5's PEP q-value 2 fails, whatever its notch");
            Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue, pepTraining: PepTraining.None), Is.EqualTo(4),
                "PEP trained for none: every match by its notch, rows 1, 2, 5 and 6");
            Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue, pepTraining: PepTraining.Some), Is.EqualTo(4),
                "for some: a PEP q-value in [0, 1] as trained, else the notch, rows 1, 4, 5 and 6");
        });
    }

    /// <summary>An empty cell: the notch and PEP filters fall back to the next value, and a match with nothing to judge
    /// it by is not counted.</summary>
    [Test]
    public void AnEmptyCellFallsBackOrIsNotCounted()
    {
        var c = Columns(
            ("T", null, null, 0.005, "0"),   // no q-values: only the PEP q-value keeps it
            ("T", 0.005, null, null, "0"),   // no notch and no PEP q-value: the q-value decides
            ("T", null, null, -1, "0"));     // a PEP q-value out of range, and nothing to fall back to
        Assert.That(Identifications.ProducerCounts(c), Is.EqualTo(1), "row 2: its q-value, and no notch to fail");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValue), Is.EqualTo(1), "row 2");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValueNotch), Is.EqualTo(1), "row 2, by its q-value");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue), Is.EqualTo(1),
            "trained: row 1 only; a trained match is judged by its PEP q-value and has no fallback");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue, pepTraining: PepTraining.None), Is.EqualTo(1),
            "not trained: row 2, by its q-value where it has no notch");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue, pepTraining: PepTraining.Some), Is.EqualTo(2),
            "rows 1 and 2");
    }

    [Test]
    public void TheReconciliationNamesTheFilterItComparedUnder()
    {
        var results = new Dictionary<string, Dictionary<string, long>> { [""] = new() { ["psms"] = 10, ["peptides"] = 5 } };
        var checks = Reconcile.Build(10, 5, null, [], results, null, null, filter: "pep q-value");
        Assert.That(checks.First(k => k.Name == "psms_target_1pct").Source, Does.StartWith("results.txt: All target PSMs with pep q-value <= 0.01"));
        Assert.That(checks.First(k => k.Name == "peptidoforms_target_1pct").Source, Is.EqualTo("results.txt: All target peptides with pep q-value <= 0.01"));
        Assert.That(Reconcile.Build(10, 5, null, [], results, null, null).First(k => k.Name == "psms_target_1pct").Source,
            Does.StartWith("results.txt: All target PSMs with q-value <= 0.01"), "no filter named: the old wording");
    }

    [Test]
    public void UnderA115FilterThePsmCheckCitesNoV1DefinitionAndCarriesItsNote()
    {
        var results = new Dictionary<string, Dictionary<string, long>> { [""] = new() { ["psms"] = 10, ["peptides"] = 5 } };
        var pep = Reconcile.Build(9, 5, null, [], results, null, null, filter: "pep q-value", definitionDescribesCount: false,
            psmNote: "approximate");
        var psm = pep.First(k => k.Name == "psms_target_1pct");
        Assert.That(psm.Source, Is.EqualTo("results.txt: All target PSMs with pep q-value <= 0.01"), "v1 describes the q-value and the notch");
        Assert.That(psm.Note, Is.EqualTo("approximate"));
        Assert.That(Reconcile.FindingRows(pep, "PXD1").Single()["message"], Does.EndWith(" approximate"), "the note reaches the finding");
        Assert.That(Reconcile.Build(10, 5, null, [], results, null, null).First(k => k.Name == "psms_target_1pct").Source,
            Does.EndWith($"({Definitions.Psm1pct.DefinitionId} {Definitions.Psm1pct.Version})"), "the old rule still cites v1");
    }
}
