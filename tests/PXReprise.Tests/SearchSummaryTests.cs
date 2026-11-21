using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Search;

namespace PXReprise.Tests;

public class SearchSummaryTests
{
    [Test]
    public void TheSummaryLineIsCanonicalAndTheEngineLineIsTakenOnce()
    {
        const string txt = "PSMs within 1% FDR: 27000\nfile a\nPSMs within 1% FDR: 1500\nAll target PSMs with q-value <= 0.01: 26582\n";
        Assert.That(SearchSummary.Parse(txt), Is.EqualTo(new SearchSummary(26582, 27000, "q-value", 26582)));
        Assert.That(SearchSummary.Parse("nothing here"), Is.EqualTo(new SearchSummary(null, null)));
    }

    // D84: MetaMorpheus 1.1.15 names its filter on the summary line (PXD058082 under #2982's PEP build, 2026-11-18). The
    // definition's count stays the "q-value" line's, so it is null here; the filter's count and name are kept beside it.
    // The engine's 1.1.15 line ("PSMs with pep q-value <= 0.01") is not the FDRENGINE definition's and is not read.
    [TestCase("pep q-value", 52778)]
    [TestCase("q-value notch", 42392)]
    public void UnderAnotherFilterTheDefinitionsCountIsNullAndTheFiltersCountIsKept(string filter, int n)
    {
        string txt = $"All target PSMs with {filter} <= 0.01: {n}\nAll target peptides with {filter} <= 0.01: 6772\n"
                     + $"PSMs with {filter} <= 0.01: {n + 451}\n";
        Assert.That(SearchSummary.Parse(txt), Is.EqualTo(new SearchSummary(null, null, filter, n)));

        var idr = SearchMetrics.IdRate(SearchSummary.Parse(txt), 100_000);
        Assert.That((idr.Psms1Pct, idr.Rate), Is.EqualTo(((int?)null, (double?)null)), "the definition's rate stays null");
        Assert.That((idr.PsmsFilter1Pct, idr.RateFilter, idr.Filter), Is.EqualTo(((int?)n, (double?)Math.Round(n / 100_000.0, 4), filter)));
    }

    // D84: the search's id_rate block and its low_id_rate flag (S3). Under the PEP filter the definition's slots are null,
    // and the count the stage reports and the flag read the filter's own count; the flag names the filter.
    [Test]
    public void UnderThePepFilterTheLowIdRateFlagReadsTheFiltersCountAndNamesIt()
    {
        var pep = SearchSummary.Parse("All target PSMs with pep q-value <= 0.01: 500\n");
        var (block, psms, flag) = SearchStage.IdRateBlock(pep, 100_000);
        Assert.Multiple(() =>
        {
            Assert.That(psms, Is.EqualTo(500));
            Assert.That(flag, Is.EqualTo("low_id_rate: 500/100000 = 0.50% of MS2 identified (S3, pep q-value)"));
            Assert.That((block["psms_1pct"], block["rate"]), Is.EqualTo(((JsonNode?)null, (JsonNode?)null)), "the definition's slots stay null");
            Assert.That(block["filter"]!.GetValue<string>(), Is.EqualTo("pep q-value"));
            Assert.That(block["psms_filter_1pct"]!.GetValue<int>(), Is.EqualTo(500));
            Assert.That(block["rate_filter"]!.GetValue<double>(), Is.EqualTo(0.005));
        });

        var (old, oldPsms, oldFlag) = SearchStage.IdRateBlock(SearchSummary.Parse("All target PSMs with q-value <= 0.01: 500\n"), 100_000);
        Assert.That((oldPsms, oldFlag), Is.EqualTo(((int?)500, "low_id_rate: 500/100000 = 0.50% of MS2 identified (S3)")), "the old wording names no filter");
        Assert.That(old["rate"]!.GetValue<double>(), Is.EqualTo(0.005));
        Assert.That(SearchStage.IdRateBlock(pep, 0).Flag, Is.Null, "no MS2 counted, no rate");
        Assert.That(SearchStage.IdRateBlock(SearchSummary.Parse("All target PSMs with pep q-value <= 0.01: 50000\n"), 100_000).Flag, Is.Null,
            "above the floor");
        var none = SearchStage.IdRateBlock(SearchSummary.Parse("nothing here"), 100_000);
        Assert.That((none.Psms, none.Flag), Is.EqualTo(((int?)null, (string?)null)), "no count, no flag");
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
