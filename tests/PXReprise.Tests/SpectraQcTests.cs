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

    // DATAREPO-72: the raw file's own start time, model and serial, recorded as read, before cleanup deletes the file.
    [Test]
    public void RunMetadataIsWrittenAsReadAndNeverAsAnEmptyString()
    {
        static MassSpectrometry.SourceFile Source(DateTime? start, string? model, string? accession, string? serial) =>
            new("Thermo nativeID format", "Thermo RAW format", "abc", "SHA-1", "x.raw", null)
            {
                AcquisitionStartTime = start,
                InstrumentModel = model is null ? null! : new MzLibUtil.CvParam { CvLabel = "MS", Accession = accession ?? "", Name = model },
                InstrumentSerialNumber = serial!,
            };
        // A RAW header time has no zone: written with none, and not shifted.
        var m = SpectraQc.Metadata(Source(new DateTime(2022, 2, 8, 10, 15, 33, DateTimeKind.Unspecified), "Orbitrap Exploris 480", "MS:1003028", "Exploris 480 - 1234"))!;
        Assert.That(m, Is.EqualTo(new RunMetadata("2022-02-08T10:15:33", "Orbitrap Exploris 480", "MS:1003028", "Exploris 480 - 1234")));
        Assert.That(SpectraQc.Metadata(Source(new DateTime(2022, 2, 8, 10, 15, 33, 250, DateTimeKind.Utc), null, null, null))!.StartTime,
            Is.EqualTo("2022-02-08T10:15:33.25Z"), "a time the reader gave as UTC says so");
        var blanks = SpectraQc.Metadata(Source(new DateTime(2022, 2, 8, 0, 0, 0, DateTimeKind.Unspecified), "  ", "", " "))!;
        Assert.That((blanks.InstrumentModel, blanks.InstrumentModelAccession, blanks.InstrumentSerial), Is.EqualTo(((string?)null, (string?)null, (string?)null)));
        Assert.That(SpectraQc.Metadata(Source(null, null, null, null)), Is.Null, "nothing read: no metadata at all");
        Assert.That(SpectraQc.Metadata(null), Is.Null);
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
