using System.Text.Json;
using PXReprise.Search;

namespace PXReprise.Tests;

public class SearchSummaryTests
{
    [Test]
    public void TheSummaryLineIsCanonicalAndTheEngineLineIsTakenOnce()
    {
        const string txt = "PSMs within 1% FDR: 27000\nfile a\nPSMs within 1% FDR: 1500\nAll target PSMs with q-value <= 0.01: 26582\n";
        Assert.That(SearchSummary.Parse(txt), Is.EqualTo(new SearchSummary(26582, 27000)));
        Assert.That(SearchSummary.Parse("nothing here"), Is.EqualTo(new SearchSummary(null, null)));
    }

    /// <summary>
    /// For every aging search with a results.txt, the counts read here equal what the aging pipeline recorded in its
    /// provenance (<c>id_rate.psms_1pct</c>, <c>id_rate.psms_fdr_engine_1pct</c>). Needs the aging work disk.
    /// </summary>
    [Test, Category("LocalCorpus")]
    public void EveryAgingSearchsCountsAreReadTheSame()
    {
        const string root = @"F:\aging_data";
        if (!Directory.Exists(root)) Assert.Ignore("the aging work disk is not attached");
        int compared = 0;
        var diffs = new List<string>();
        foreach (string prov in Directory.EnumerateFiles(root, "provenance.json", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "04_search").OrderBy(f => f))
        {
            var rec = JsonDocument.Parse(File.ReadAllText(prov)).RootElement;
            if (!rec.TryGetProperty("id_rate", out var idr) || idr.ValueKind != JsonValueKind.Object) continue;
            if (!idr.TryGetProperty("definition", out var def) || def.GetString() != SearchSummary.PsmDefinition) continue;
            string mm = Path.Combine(Path.GetDirectoryName(prov)!, "mm");
            string? results = Directory.Exists(mm)
                ? Directory.EnumerateDirectories(mm, "Task*SearchTask").OrderBy(d => d).Select(d => Path.Combine(d, "results.txt"))
                    .LastOrDefault(File.Exists)
                : null;
            if (results is null) continue;
            var s = SearchSummary.Read(results);
            compared++;
            int? want = idr.TryGetProperty("psms_1pct", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;
            int? wantEngine = idr.TryGetProperty("psms_fdr_engine_1pct", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
            if (s.Psms1Pct != want || s.PsmsFdrEngine1Pct != wantEngine)
                diffs.Add($"{prov}: read {s}, recorded ({want}, {wantEngine})");
        }
        TestContext.Out.WriteLine($"{compared} searches compared, {diffs.Count} differ");
        Assert.That(compared, Is.GreaterThan(0));
        Assert.That(diffs, Is.Empty);
    }
}
