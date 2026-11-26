using PXReprise.Bundle;
using PXReprise.Ingest;

namespace PXReprise.Data.Tests;

/// <summary>
/// MetaMorpheus 1.1.15 (#2982, #2989) names the search's identification filter on results.txt's count lines. Under the
/// PEP q-value filter, the 1% PSM, peptide and protein-group counts are not the counts their v1 definitions describe (the
/// q-value and the notch together), so the ingest files them as no metric. The reconciliation still compares them, under
/// the filter results.txt names, and its labels say which.
/// </summary>
/// <remarks>The fixture's 1.1.11 label-free search is relabelled 1.1.15 and its results.txt reworded as 1.1.15 writes
/// it under the PEP filter.</remarks>
public class PepFilterIngestTests
{
    private const string Accession = "PXD999999";
    private static readonly string[] OnePercentCounts = ["psms_1pct", "peptides", "protein_groups"];
    private string _root = "";

    private string Manifest => Path.Combine(_root, "manifest.yaml");
    private string Search => Path.Combine(_root, "work_root", "run_test", Accession, "04_search");
    private string ResultsTxt => Path.Combine(Search, "mm", "Task3SearchTask", "results.txt");

    [SetUp]
    public void CopyTheFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-pepfilter-" + Guid.NewGuid().ToString("N"));
        var from = DataPaths.Instance;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(from, file)));
    }

    [TearDown]
    public void Remove()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static void Rewrite(string path, Func<string, string> edit) =>
        File.WriteAllText(path, edit(File.ReadAllText(path)), new System.Text.UTF8Encoding(false));

    /// <summary>Declares the search's MetaMorpheus release in its provenance and its manifest entry.</summary>
    private void SetRelease(string release)
    {
        var inProvenance = new System.Text.RegularExpressions.Regex(@"(""MetaMorpheus"":\s*\{\s*""release"":\s*)""[^""]*""");
        var inManifest = new System.Text.RegularExpressions.Regex(@"(?m)^    metamorpheus: ""[^""]*""\n");
        string provenance = Path.Combine(Search, "provenance.json");
        Assert.That(inProvenance.IsMatch(File.ReadAllText(provenance)) && inManifest.IsMatch(File.ReadAllText(Manifest)),
            "the fixture declares its release in both");
        Rewrite(provenance, t => inProvenance.Replace(t, $"$1\"{release}\""));
        Rewrite(Manifest, t => inManifest.Replace(t, $"    metamorpheus: \"{release}\"\n"));
    }

    private static List<string> ResultsMetricNames(IngestResult result) =>
        ArrowTables.ReadParquet(Path.Combine(result.BundlePath, "metrics.parquet")).Rows
            .Where(m => Equals(m["source"], "results.txt")).Select(m => (string)m["name"]!).ToList();

    private static string Source(IngestResult result, string check) =>
        (string)result.Checks.First(c => Equals(c["name"], check))["source"]!;

    [Test]
    public void UnderThePepFilterTheOnePercentCountsAreNoMetricAndTheReconciliationNamesTheFilter()
    {
        var before = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store-before"), overwrite: true);
        Assert.That(ResultsMetricNames(before), Is.SupersetOf(OnePercentCounts), "premise: under the old rule they are metrics");
        Assert.That(Source(before, "psms_target_1pct"), Does.StartWith("results.txt: All target PSMs with q-value <= 0.01"));

        SetRelease("1.1.15");
        Rewrite(ResultsTxt, t => t.Replace("with q-value <= 0.01", "with pep q-value <= 0.01"));
        var after = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store-after"), overwrite: true);
        Assert.Multiple(() =>
        {
            Assert.That(ResultsMetricNames(after).Intersect(OnePercentCounts), Is.Empty, "no count is filed under a definition that does not describe it");
            Assert.That(Source(after, "psms_target_1pct"), Does.StartWith("results.txt: All target PSMs with pep q-value <= 0.01"));
            Assert.That(Source(after, "peptidoforms_target_1pct"), Is.EqualTo("results.txt: All target peptides with pep q-value <= 0.01"));
        });
    }

    private string AllPsms => Path.Combine(Search, "mm", "Task3SearchTask", "AllPSMs.psmtsv");

    /// <summary>Rewrites cells of AllPSMs.psmtsv: <paramref name="edit"/> gets each row's cells by column name, and the
    /// row's index among the target rows (-1 for a decoy or contaminant).</summary>
    private void EditPsms(Action<Dictionary<string, string>, int> edit)
    {
        var lines = File.ReadAllText(AllPsms).Split('\n');
        var header = lines[0].TrimEnd('\r').Split('\t');
        int target = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Length == 0) continue;
            var cells = lines[i].TrimEnd('\r').Split('\t');
            var row = header.Select((h, k) => (h, k)).ToDictionary(p => p.h, p => cells[p.k]);
            edit(row, row["Decoy/Contaminant/Target"].Trim() == "T" ? target++ : -1);
            lines[i] = string.Join('\t', header.Select(h => row[h])) + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        File.WriteAllText(AllPsms, string.Join('\n', lines), new System.Text.UTF8Encoding(false));
    }

    private static IReadOnlyDictionary<string, object?> Check(IngestResult result, string name) =>
        result.Checks.First(c => Equals(c["name"], name));

    /// <summary>Rewords results.txt as 1.1.15 writes it under the PEP filter, with this PSM count, and the "PEP filtering
    /// was requested, but ..." sentence above it when <paramref name="notTrainedSentence"/> is given.</summary>
    private void PepResults(long psms, string? notTrainedSentence = null)
    {
        SetRelease("1.1.15");
        Rewrite(ResultsTxt, t => t
            .Replace("with q-value <= 0.01", "with pep q-value <= 0.01")
            .Replace("All target PSMs with pep q-value <= 0.01: 58",
                (notTrainedSentence is null ? "" : notTrainedSentence + "\n") + $"All target PSMs with pep q-value <= 0.01: {psms}"));
    }

    /// <summary>G148: PEP trained for every match. 1.1.15 counts a notch-ambiguous match and fails a PEP q-value of 2,
    /// whatever its notch. #38's counts gave 56 here, two short for the clause and one over for the 2.</summary>
    [Test]
    public void ATrainedSearchCountsNotchAmbiguousMatchesAndFailsAPepQValueOfTwo()
    {
        EditPsms((row, target) =>
        {
            if (target is 0 or 1) row["Notch"] = "0|1";
            if (target == 2) row["PEP_QValue"] = "2";
        });
        PepResults(57);
        var result = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);
        var psms = Check(result, "psms_target_1pct");
        Assert.Multiple(() =>
        {
            Assert.That(psms["observed"], Is.EqualTo(57.0), "58 targets, both ambiguous ones counted, the PEP q-value 2 failed");
            Assert.That(psms["ok"], Is.True);
            Assert.That(psms.ContainsKey("note"), Is.False);
            Assert.That((string)psms["source"]!, Is.EqualTo("results.txt: All target PSMs with pep q-value <= 0.01"),
                "DEF-PSM-1PCT v1 describes the q-value and the notch, so it is not cited");
        });
    }

    /// <summary>G148, the review of #42: under the notch filter a match whose candidates sat at different notches with
    /// different full sequences when FDR ran keeps the default notch q-value 2 and fails, though the psmtsv prints the best
    /// candidate's. One whose notch is ambiguous with one full sequence counts. PXD031782's notch arm has both kinds.</summary>
    [Test]
    public void UnderTheNotchFilterOnlyAMatchAmbiguousWhenFdrRanFails()
    {
        EditPsms((row, target) =>
        {
            if (target is 0 or 1) row["Notch"] = "0|1";
            if (target == 1) row["Mass Diff (Da)"] = row["Mass Diff (Da)"] + "|0.98402";
        });
        SetRelease("1.1.15");
        Rewrite(ResultsTxt, t => t.Replace("with q-value <= 0.01", "with q-value notch <= 0.01")
            .Replace("All target PSMs with q-value notch <= 0.01: 58", "All target PSMs with q-value notch <= 0.01: 57"));
        var result = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);
        var psms = Check(result, "psms_target_1pct");
        Assert.That(psms["observed"], Is.EqualTo(57.0), "target 0 counts (one mass difference), target 1 fails (two)");
        Assert.That(psms["ok"], Is.True);
    }

    /// <summary>G148: PEP training failed (every PEP 0), so MetaMorpheus judges every match by its notch, though its PEP
    /// q-values look usable. #38 read them per row and gave 55.</summary>
    [Test]
    public void ASearchWhosePepDidNotTrainIsCountedByItsNotch()
    {
        EditPsms((row, target) =>
        {
            row["PEP"] = "0";
            if (target is >= 0 and < 3) row["PEP_QValue"] = "0.5";
        });
        PepResults(58, "PEP filtering was requested, but PEP was not trained for 58 target PSMs (trypsin: 58), so those were filtered by q-value notch <= 0.01.");
        var result = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);
        var psms = Check(result, "psms_target_1pct");
        Assert.That(psms["observed"], Is.EqualTo(58.0));
        Assert.That(psms["ok"], Is.True);
    }

    /// <summary>G148: the sentence, but a usable PEP: trained for some matches and not others. The count can only
    /// approximate it, and the check says so.</summary>
    [Test]
    public void ASearchTrainedForSomeMatchesSaysItsCountIsApproximate()
    {
        PepResults(58, "PEP filtering was requested, but PEP was not trained for 4 target PSMs (trypsin (Semi): 4), so those were filtered by q-value notch <= 0.01.");
        var result = Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);
        Assert.That((string?)Check(result, "psms_target_1pct").GetValueOrDefault("note"), Does.StartWith("PEP was not trained for some"));
        Assert.That(Check(result, "peptidoforms_target_1pct").ContainsKey("note"), Is.False, "no sentence above the peptide count");
    }
}
