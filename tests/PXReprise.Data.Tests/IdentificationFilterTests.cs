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

    // PXD058082 under MetaMorpheus #2982's PEP build (the worker's filter trial f2, 2026-11-18), lines as written.
    private const string PepLines = """
        All target PSMs with pep q-value <= 0.01: 52778
        All target peptides with pep q-value <= 0.01: 6772
        All target protein groups with pep q-value <= 0.01 (1% FDR): 1607
        PEP filtering was requested, but PEP was not trained for 7 target PSMs (trypsin: 7), so those were filtered by q-value notch <= 0.01.
        PSMs with pep q-value <= 0.01: 53229
        Number of proteins with q-value <= 0.01: 1620
        """;

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
        Assert.That(Identifications.ProducerCounts(c), Is.EqualTo(1), "q-value and notch, a resolved notch (up to 1.1.14)");
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValueNotch), Is.EqualTo(3));
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.QValue), Is.EqualTo(2));
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue), Is.EqualTo(3), "rows 1, 4 and 5");
        Assert.That(Identifications.ProducerCounts(c, requireResolvedNotch: false, filter: ProducerFilter.PepQValue), Is.EqualTo(4),
            "the peptide count keeps the notch-ambiguous row");
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
        Assert.That(Identifications.ProducerCounts(c, filter: ProducerFilter.PepQValue), Is.EqualTo(2), "rows 1 and 2");
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
}
