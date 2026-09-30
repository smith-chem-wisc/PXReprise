using System.Text.Json;
using PXReprise.Config;
using PXReprise.Qc;

namespace PXReprise.Tests;

public class SpectraQcTests
{
    private static QcGates Gates => ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@1"].Qc;

    [Test]
    public void MostCommonBreaksTiesByFirstSeenLikePython()
    {
        var r = SpectraQc.MostCommon(new[] { "b", "a", "a", "b", "c" });
        Assert.That(r.Keys, Is.EqualTo(new[] { "b", "a", "c" }));
        Assert.That(SpectraQc.MostCommon(new[] { "x", "y", "y" }, top: 1).Keys, Is.EqualTo(new[] { "y" }));
    }

    [Test]
    public void AnUnreadableFileIsAVerdictNotAnException()
    {
        string f = TestSupport.WriteFile(TestSupport.TempDir(), "broken.raw", "not a raw file");
        var r = SpectraQc.Check(f, Gates);
        Assert.That(r.Pass, Is.False);
        Assert.That(r.FailReasons, Is.EqualTo(new[] { SpectraQc.Unreadable }));
    }

    /// <summary>
    /// Field-for-field agreement with aging's qc_report.json on files still on disk: a passing set (PXD036557, 6
    /// Q Exactive files) and a failing one (PXD060431, MS2 read in the ion trap: every file low_res_ms2). Needs the
    /// aging work disk; reads a few GB of raw files, so it is slow by design.
    /// </summary>
    [TestCase(@"F:\aging_data\run_2026-09-18\PXD036557_n6", 6)]
    [TestCase(@"F:\aging_data\run_2026-09-19\PXD060431", 2)]
    [Category("LocalCorpus")]
    public void QcAgreesWithTheAgingPipelinesReport(string run, int maxFiles)
    {
        string report = Path.Combine(run, "02b_qc", "qc_report.json");
        if (!File.Exists(report)) Assert.Ignore("the aging work disk is not attached");
        var want = JsonDocument.Parse(File.ReadAllText(report)).RootElement;
        int n = 0;
        foreach (var file in want.EnumerateObject().Take(maxFiles))
        {
            string raw = Path.Combine(run, "02_fetch", "spectra", file.Name);
            if (!File.Exists(raw)) continue;
            var got = SpectraQc.Check(raw, Gates);
            var w = file.Value;
            Assert.Multiple(() =>
            {
                Assert.That(got.Pass, Is.EqualTo(w.GetProperty("pass").GetBoolean()), file.Name + " pass");
                // Reports written before 2026-09-20 predate `fail_reasons`; a passing file then has none.
                var reasons = w.TryGetProperty("fail_reasons", out var fr)
                    ? fr.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : new List<string>();
                Assert.That(got.FailReasons, Is.EqualTo(reasons), file.Name);
                Assert.That(got.Scans, Is.EqualTo(w.GetProperty("scans").GetInt32()), file.Name + " scans");
                Assert.That(got.Ms2, Is.EqualTo(w.GetProperty("ms2").GetInt32()), file.Name + " ms2");
                Assert.That(got.FractionOrbitrapHcd, Is.EqualTo(w.GetProperty("fraction_orbitrap_hcd").GetDouble()), file.Name + " fraction");
                Assert.That(got.RunMinutes, Is.EqualTo(w.GetProperty("run_minutes").GetDouble()), file.Name + " minutes");
                Assert.That(got.Ms2AnalyzerDissociation, Is.EquivalentTo(Dict(w.GetProperty("ms2_analyzer_dissociation"))), file.Name + " pairs");
                Assert.That(got.ChargeStates, Is.EquivalentTo(Dict(w.GetProperty("charge_states"))), file.Name + " charges");
            });
            n++;
        }
        Assert.That(n, Is.GreaterThan(0));
    }

    private static Dictionary<string, int> Dict(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
}
