using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.45 (the user, 2026-11-11: "once a file is calibrated, we don't need to calibrate it again. and we don't need all
/// files calibrated in the same session. they can split."). Windows Update restarted MOZZLE during PXD028799's search after
/// 34 of its 48 raws were calibrated and deleted (D59); the next driver fetched all 34 again and calibrated every file afresh.
/// </summary>
public class CalibratedStandInsTests
{
    private const string Params = """
        {"profile":"tmt-dda@4","organism":"human","search":{"metamorpheus_version":"1.1.14","metamorpheus_mzlib_version":"1.0.597"},
         "database":{"prepared":"F:/db/human-v8.xml","extra_prepared":["F:/db/iso-v7.xml"]}}
        """;

    private const string Closed = "<indexedmzML><mzML></mzML><indexList/></indexedmzML>\n";

    /// <summary>A set-aside search: its params, its Calibration task file, and the calibrated files given.</summary>
    private static string Aside(string run, string name, params (string Stem, string? Mzml, bool Toml)[] files)
    {
        string d = Directory.CreateDirectory(Path.Combine(run, name)).FullName;
        TestSupport.WriteFile(d, "params.json", Params);
        TestSupport.WriteFile(Directory.CreateDirectory(Path.Combine(d, "tasks")).FullName, "1_CalibrationTask.toml", "TaskType = \"Calibrate\"\r\nMaxThreadsToUsePerFile = 32\r\n");
        string cal = Directory.CreateDirectory(Path.Combine(d, "mm", "Task1CalibrationTask")).FullName;
        foreach (var (stem, mzml, toml) in files)
        {
            if (mzml is not null) File.WriteAllText(Path.Combine(cal, stem + "-calib.mzML"), mzml);
            if (toml) File.WriteAllText(Path.Combine(cal, stem + "-calib.toml"), "PrecursorMassTolerance = \"±4.1 PPM\"\n");
        }
        return d;
    }

    [Test]
    public void HarvestTakesOnlyFinishedCalibrationsOfAnInterruptedSearch()
    {
        string run = TestSupport.TempDir();
        TestSupport.WriteFile(Directory.CreateDirectory(Path.Combine(run, "02_fetch")).FullName, "fetch_manifest.json",
            """{"files":[{"name":"m01.RAW","sha256":"aa","pride_checksum":"s1","category":"RAW"},{"name":"m02.raw","sha256":"bb"},{"name":"m04.raw","sha256":""}]}""");
        string aside = Aside(run, "04_search.interrupted-20261111T133322Z",
            ("m01", Closed, true),                  // finished
            ("m02", "<indexedmzML><mzML>", true),   // cut short: its tail never reached the disk
            ("m03", Closed, false),                 // the toml is written after the mzML: still being written
            ("m04", Closed, true));                 // finished, but the manifest has no SHA-256 for its raw
        // A failed search a person retried is never harvested: a retry usually follows a change.
        Aside(run, "04_search.failed-20261111T120000Z", ("m05", Closed, true));

        var got = CalibratedStandIns.Harvest(run);

        Assert.That(got.Select(g => g.RawName), Is.EqualTo(new[] { "m01.RAW", "m04.raw" }), "named as the fetch named them, extension case and all");
        var m01 = got[0];
        Assert.That((m01.From, m01.FetchEntry?["sha256"]?.GetValue<string>(), m01.Identity["profile"]?.GetValue<string>()),
            Is.EqualTo(("04_search.interrupted-20261111T133322Z", "aa", "tmt-dda@4")));
        Assert.That(got[1].FetchEntry, Is.Null, "no SHA-256 to write again, so that raw will be fetched");
        string spectra = Path.Combine(run, "02_fetch", "spectra");
        Assert.That(File.Exists(Path.Combine(spectra, "calibrated", "m01-calib.mzML")) && File.Exists(Path.Combine(spectra, "calibrated", "m01-calib.toml")), Is.True);
        Assert.That(File.Exists(Path.Combine(aside, "mm", "Task1CalibrationTask", "m01-calib.mzML")), Is.False, "moved, not copied");
        Assert.That(File.Exists(Path.Combine(aside, "mm", "Task1CalibrationTask", "m02-calib.mzML")), Is.True, "an unfinished one stays where it was");
        Assert.That(CalibratedStandIns.In(spectra).Select(s => s.RawName), Is.EqualTo(new[] { "m01.RAW", "m04.raw" }));
        Assert.That(CalibratedStandIns.In(spectra)[0].FetchEntry?["pride_checksum"]?.GetValue<string>(), Is.EqualTo("s1"), "the record carries the whole entry");
        Assert.That(CalibratedStandIns.Harvest(run), Is.Empty, "a second look finds nothing new");

        CalibratedStandIns.Discard(spectra, new[] { "m04.raw" });
        Assert.That(CalibratedStandIns.In(spectra).Select(s => s.RawName), Is.EqualTo(new[] { "m01.RAW" }));
        Assert.That(Directory.EnumerateFiles(Path.Combine(spectra, "calibrated"), "m04*"), Is.Empty);
    }

    [Test]
    public void TheIdentityChangesWithEverySettingCalibrationReadsAndNotWithLineEnds()
    {
        var p = JsonNode.Parse(Params)!;
        var a = CalibratedStandIns.Identity(p, "TaskType = \"Calibrate\"\r\nX = 1\r\n");
        Assert.That(CalibratedStandIns.Same(a, CalibratedStandIns.Identity(p, "TaskType = \"Calibrate\"\nX = 1\n")), Is.True, "CRLF and LF are one file");
        var b = CalibratedStandIns.Identity(p, "TaskType = \"Calibrate\"\nX = 2\n");
        Assert.That(CalibratedStandIns.Differences(a, b), Is.EqualTo(new[] { "calibration_task_sha256" }));
        var q = JsonNode.Parse(Params)!;
        q["search"]!["metamorpheus_version"] = "1.1.15";
        q["database"]!["extra_prepared"] = new JsonArray("F:/db/iso-v9.xml");
        Assert.That(CalibratedStandIns.Differences(a, CalibratedStandIns.Identity(q, "TaskType = \"Calibrate\"\nX = 1\n")),
            Is.EqualTo(new[] { "database_extra", "metamorpheus_version" }));
    }

    [Test]
    public void AnMzmlIsCompleteOnlyWhenItsLastBytesCloseTheDocument()
    {
        string dir = TestSupport.TempDir();
        Assert.That(CalibratedStandIns.Complete(TestSupport.WriteFile(dir, "a.mzML", Closed)), Is.True);
        Assert.That(CalibratedStandIns.Complete(TestSupport.WriteFile(dir, "b.mzML", "<mzML>" + new string(' ', 2000) + "</mzML>")), Is.True);
        Assert.That(CalibratedStandIns.Complete(TestSupport.WriteFile(dir, "c.mzML", "<indexedmzML><mzML><spectrum>")), Is.False);
        Assert.That(CalibratedStandIns.Complete(TestSupport.WriteFile(dir, "d.mzML", "")), Is.False);
        Assert.That(CalibratedStandIns.Complete(Path.Combine(dir, "missing.mzML")), Is.False);
    }

    // QC reads a raw; a raw that is gone keeps the verdict the last full QC gave it, and only one with a stand-in does.
    [Test]
    public void FullQcKeepsTheEarlierVerdictOfAGoneRawThatHasAStandIn()
    {
        string dir = TestSupport.TempDir();
        string spectra = Directory.CreateDirectory(Path.Combine(dir, "02_fetch", "spectra")).FullName;
        string qc = Directory.CreateDirectory(Path.Combine(dir, "02b_qc")).FullName;
        TestSupport.WriteFile(qc, "qc_report.json",
            """{"b.raw":{"pass":true,"fail_reasons":[],"ms2":20},"a.raw":{"pass":true,"fail_reasons":[],"ms2":30},"c.raw":{"pass":false,"fail_reasons":["too_few_ms2"],"ms2":2}}""");
        var machine = Machine.Load(TestSupport.WriteFile(dir, "machine.toml", $"work_root = '{dir}'\nmin_free_gb = 0\n[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n"));
        var profile = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["tmt-dda@4"];
        string paramsFile = TestSupport.WriteFile(dir, "params.json", "{}");

        var (report, all) = QcStage.Run(spectra, qc, profile, machine, paramsFile, null, kept: new[] { "b.raw", "a.raw" });

        Assert.That(report.Select(kv => kv.Key), Is.EqualTo(new[] { "a.raw", "b.raw" }), "in name order; c.raw has no stand-in and is not kept");
        Assert.That(all, Is.True);
        Assert.That(report["b.raw"]!["ms2"]!.GetValue<int>(), Is.EqualTo(20));
        Assert.That(File.ReadAllText(Path.Combine(qc, "provenance.json")), Does.Contain("verdicts_kept").And.Contain("not read again"));
        // Nothing on disk and nothing kept is still no deposit to judge.
        Assert.Throws<PXReprise.Cli.UsageException>(() => QcStage.Run(spectra, Path.Combine(dir, "02b_qc_2"), profile, machine, paramsFile, null, kept: new[] { "b.raw" }));
    }

    // A search split in two by stand-ins writes Task2SearchTask under a three-task profile; the manifest names what is on disk.
    [Test]
    public void TheManifestNamesTheSearchFolderOnDisk()
    {
        var profile = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@14"];
        string run = TestSupport.TempDir();
        Assert.That(ManifestEntry.SearchTaskFolder(profile, run), Is.EqualTo(ManifestEntry.SearchTaskFolder(profile)), "nothing on disk: the profile's numbering");
        Directory.CreateDirectory(Path.Combine(run, "04_search", "mm", "Task1CalibrationTask"));
        Directory.CreateDirectory(Path.Combine(run, "04_search", "mm", "Task2SearchTask"));
        Assert.That(ManifestEntry.SearchTaskFolder(profile, run), Is.EqualTo("Task2SearchTask"));
    }
}
