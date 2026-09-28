using System.Text.Json;
using PXReprise.Search;

namespace PXReprise.Tests;

public class SearchMetricsTests
{
    [TestCase(0.12345, 4, 0.1234)]   // the double is just below the midpoint: Python gives 0.1234
    [TestCase(0.125, 2, 0.12)]       // an exact binary midpoint: halves to even
    [TestCase(0.375, 2, 0.38)]
    [TestCase(2.675, 2, 2.67)]       // the classic: 2.675 is stored as 2.67499999...
    public void PyRoundMatchesPython(double x, int digits, double expected) =>
        Assert.That(SearchMetrics.PyRound(x, digits), Is.EqualTo(expected));

    [Test]
    public void OnlyMbrRowsThatPassEveryConditionAreKept()
    {
        Dictionary<string, string> Row(string type, string rnd = "False", string q = "0.001", string decoy = "False") =>
            new() { ["Peak Detection Type"] = type, ["Random RT"] = rnd, ["PIP Q-Value"] = q, ["Decoy Peptide"] = decoy };
        Assert.Multiple(() =>
        {
            Assert.That(SearchMetrics.ClassifyPeak(Row("MSMS")).Kind, Is.EqualTo(SearchMetrics.PeakKind.Msms));
            Assert.That(SearchMetrics.ClassifyPeak(Row("MBR")).Kind, Is.EqualTo(SearchMetrics.PeakKind.MbrKept));
            Assert.That(SearchMetrics.ClassifyPeak(Row("MBR", rnd: "True")), Is.EqualTo((SearchMetrics.PeakKind.MbrOther, true)));
            Assert.That(SearchMetrics.ClassifyPeak(Row("MBR", q: "0.2")).Kind, Is.EqualTo(SearchMetrics.PeakKind.MbrOther));
            Assert.That(SearchMetrics.ClassifyPeak(Row("MBR", q: "")).Kind, Is.EqualTo(SearchMetrics.PeakKind.MbrOther));
            Assert.That(SearchMetrics.ClassifyPeak(Row("MBR", decoy: "True")).Kind, Is.EqualTo(SearchMetrics.PeakKind.MbrOther));
            Assert.That(SearchMetrics.ClassifyPeak(Row("")).Kind, Is.EqualTo(SearchMetrics.PeakKind.None));
        });
    }

    /// <summary>
    /// For every aging search whose provenance carries today's definitions, the MBR and contamination blocks computed
    /// here equal the recorded ones, the upper bound included when its databases are still on disk. Needs the aging
    /// work disk. Reads every AllPSMs.psmtsv (tens of GB), so it is slow by design.
    /// </summary>
    [Test, Category("LocalCorpus")]
    public void EveryAgingSearchsDerivedBlocksAreReproduced()
    {
        const string root = @"F:\aging_data";
        if (!Directory.Exists(root)) Assert.Ignore("the aging work disk is not attached");
        var diffs = new List<string>();
        int mbrCompared = 0, contamCompared = 0, upperCompared = 0;
        var sharedCache = new Dictionary<string, (IReadOnlyList<string>, string)>();

        foreach (string prov in Directory.EnumerateFiles(root, "provenance.json", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "04_search").OrderBy(f => f))
        {
            var rec = JsonDocument.Parse(File.ReadAllText(prov)).RootElement;
            string mm = Path.Combine(Path.GetDirectoryName(prov)!, "mm");
            string? sd = Directory.Exists(mm) ? Directory.EnumerateDirectories(mm, "Task*SearchTask").OrderBy(d => d).LastOrDefault() : null;
            if (sd is null) continue;

            if (rec.TryGetProperty("mbr", out var mbr) && mbr.ValueKind == JsonValueKind.Object
                && File.Exists(Path.Combine(sd, "AllQuantifiedPeaks.tsv")))
            {
                var got = SearchMetrics.Mbr(Path.Combine(sd, "AllQuantifiedPeaks.tsv"));
                mbrCompared++;
                if (got.MbrRows != mbr.GetProperty("mbr_rows").GetInt32() || got.MbrKept != mbr.GetProperty("mbr_kept").GetInt32()
                    || got.MsmsPeaks != mbr.GetProperty("msms_peaks").GetInt32() || got.MbrRandomRtWon != mbr.GetProperty("mbr_random_rt_won").GetInt32()
                    || got.KeptOverMsms != Num(mbr, "kept_over_msms"))
                    diffs.Add($"{prov}: mbr {got}");
            }

            if (!rec.TryGetProperty("contamination", out var c) || c.ValueKind != JsonValueKind.Object) continue;
            if (Str(c, "intensity_share_definition") != Contamination.IntensityShareDefinition) continue;
            string psms = Path.Combine(sd, "AllPSMs.psmtsv");
            if (!File.Exists(psms)) continue;

            // The databases the search used, as its provenance lists them (paths under the work root).
            var xmls = rec.GetProperty("inputs").EnumerateArray()
                .Select(i => Path.Combine(root, i.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)))
                .Where(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            string? contamDb = xmls.FirstOrDefault(p => Path.GetFileName(p).Contains("Contaminants", StringComparison.OrdinalIgnoreCase));
            var proteomes = xmls.Where(p => p != contamDb).ToList();
            IReadOnlyList<string>? shared = null; string? sha = null;
            if (c.TryGetProperty("shared_accessions_sha256", out var ss) && ss.ValueKind == JsonValueKind.String
                && contamDb is not null && File.Exists(contamDb) && proteomes.All(File.Exists))
            {
                string key = string.Join("|", proteomes.Prepend(contamDb));
                if (!sharedCache.TryGetValue(key, out var hit)) sharedCache[key] = hit = SearchMetrics.SharedAccessions(contamDb, proteomes);
                (shared, sha) = hit;
            }

            var gotC = SearchMetrics.Contamination(psms, Path.Combine(sd, "AllQuantifiedProteinGroups.tsv"), shared, sha);
            contamCompared++;
            void Check(string name, object? got, object? want) { if (!Equals(got, want)) diffs.Add($"{prov}: {name} {got} vs {want}"); }
            Check("psm_share", gotC.PsmShare, Num(c, "psm_share"));
            Check("contaminant_psms", gotC.ContaminantPsms, c.GetProperty("contaminant_psms").GetInt32());
            Check("target_plus_contaminant_psms", gotC.TargetPlusContaminantPsms, c.GetProperty("target_plus_contaminant_psms").GetInt32());
            Check("intensity_share_max", gotC.IntensityShareMax, Num(c, "intensity_share_max"));
            Check("intensity_share_median", gotC.IntensityShareMedian, Num(c, "intensity_share_median"));
            Check("top", string.Join(";", gotC.Top), string.Join(";", c.GetProperty("top").EnumerateArray().Select(x => x.GetString())));
            foreach (var f in c.GetProperty("intensity_share_per_file").EnumerateObject())
                Check("per_file " + f.Name, gotC.IntensitySharePerFile.GetValueOrDefault(f.Name), f.Value.ValueKind == JsonValueKind.Number ? f.Value.GetDouble() : null);
            if (sha is not null)
            {
                upperCompared++;
                Check("shared_sha", sha, Str(c, "shared_accessions_sha256"));
                Check("upper_max", gotC.IntensityShareUpperMax, Num(c, "intensity_share_upper_max"));
                foreach (var f in c.GetProperty("intensity_share_upper_per_file").EnumerateObject())
                    Check("upper " + f.Name, gotC.IntensityShareUpperPerFile.GetValueOrDefault(f.Name), f.Value.ValueKind == JsonValueKind.Number ? f.Value.GetDouble() : null);
            }
        }
        TestContext.Out.WriteLine($"mbr {mbrCompared}, contamination {contamCompared}, upper bound {upperCompared} searches compared; {diffs.Count} differences");
        foreach (string d in diffs.Take(20)) TestContext.Out.WriteLine(d);
        Assert.That(contamCompared, Is.GreaterThan(0));
        Assert.That(diffs, Is.Empty);
    }

    private static double? Num(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
