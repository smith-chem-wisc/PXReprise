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
}
