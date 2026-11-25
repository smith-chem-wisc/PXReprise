using PXReprise.Batch;
using PXReprise.Config;

namespace PXReprise.Tests;

/// <summary>
/// D96, G134: what the batch needs to run ptmQtl's engines through <c>datarepo run</c>, and the machine file's
/// <c>[ptmqtl]</c>. The batch's steps themselves are tested in BatchRunnerTests.
/// </summary>
public class PtmQtlBatchTests
{
    private static readonly PtmQtlConfig Lists = new("E:/l/enrichment.tsv", null, "E:/l/cysteine.tsv");

    [Test]
    public void TheRunCommandNamesTheEngineTheDatasetsTheStoreTheNewestBundleAndTheLists()
    {
        Assert.That(PtmQtlBatch.RunArgv("datarepo.exe", PtmQtlBatch.SitePairs, ["PXD000001"], "F:/store", Lists), Is.EqualTo(new[]
        {
            "datarepo.exe", "run", "ptmqtl.site_pairs", "PXD000001", "--store", "F:/store", "--latest",
            "--input", "enrichment=E:/l/enrichment.tsv", "--input", "cysteine=E:/l/cysteine.tsv",
        }));
        var withRuns = PtmQtlBatch.RunArgv("datarepo.exe", PtmQtlBatch.PoolPairs, ["PXD000001", "PXD000002"], "F:/store", Lists with { CaptureRuns = "E:/l/runs.tsv" });
        Assert.That(withRuns.Skip(2).Take(3), Is.EqualTo(new[] { "ptmqtl.pool_pairs", "PXD000001", "PXD000002" }));
        Assert.That(withRuns.TakeLast(2), Is.EqualTo(new[] { "--input", "capture_runs=E:/l/runs.tsv" }), "capture_runs only when the machine names it");
    }

    [TestCase("store: F:/aging_data/repo/store", "F:/aging_data/repo/store")]
    [TestCase("store: \"F:/repo/store\"", "F:/repo/store")]
    public void TheStoreIsTheManifestsOwn(string line, string store)
    {
        string manifest = TestSupport.WriteFile(TestSupport.TempDir(), "manifest.yaml", $"manifest_version: 1\r\ninstance: aging\r\n{line}\r\ndatasets:\r\n");
        Assert.That(PtmQtlBatch.StoreOf(manifest), Is.EqualTo(store));
    }

    [Test]
    public void ARelativeStoreIsTakenBesideTheManifestAndAManifestWithoutOneIsRefused()
    {
        string dir = TestSupport.TempDir();
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\nstore: store\n");
        Assert.That(PtmQtlBatch.StoreOf(manifest), Is.EqualTo(Path.GetFullPath(Path.Combine(dir, "store"))));
        string none = TestSupport.WriteFile(dir, "none.yaml", "manifest_version: 1\n  store: nested, not top-level\nstore:\n");
        Assert.That(() => PtmQtlBatch.StoreOf(none), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("names no store"));
    }

    [Test]
    public void TheArtefactsARunWroteAndFoundAreReadFromWhatItPrinted()
    {
        string stdout = "written  ptmqtl.pool_pairs artefact 8d1ca71f25b693ab  F:/store/_engine/ptmqtl.pool_pairs/8d1ca71f25b693ab\r\n"
            + "  rows     {'ptm_pairs': 229}\r\n"
            + "done     ptmqtl.pool_pairs artefact 7fe4c784f63d396a already exists; nothing re-run\r\n";
        var (written, done) = PtmQtlBatch.ReadOutput(stdout);
        Assert.That(written, Is.EqualTo(new[] { "8d1ca71f25b693ab" }));
        Assert.That(done, Is.EqualTo(new[] { "7fe4c784f63d396a" }));
        Assert.That(PtmQtlBatch.ReadOutput("").Written, Is.Empty);
    }

    [Test]
    public void OnlyLabelFreeDeliveriesGetPtmQtlsEngines()
    {
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(PtmQtlBatch.Runs(profiles["label-free-dda@1"]), Is.True);
        Assert.That(PtmQtlBatch.Runs(profiles["tmt-dda@1"]), Is.False, "an isobaric bundle has no ptm_stoichiometry table");
    }

    // ---------------------------------------------------------------- the machine file's [ptmqtl]

    private static string MachineFile(string dir, string extra) => TestSupport.WriteFile(dir, "machine.toml",
        $"work_root = '{dir}'\n{extra}[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n");

    private static string WithLists(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "enrichment.tsv"), "dataset\tclass\n");
        File.WriteAllText(Path.Combine(dir, "cysteine.tsv"), "dataset\tclass\treason\n");
        File.WriteAllText(Path.Combine(dir, "capture_runs.tsv"), "dataset\trun_id\n");
        return dir;
    }

    [Test]
    public void PtmqtlNamesTheListsBesideTheMachineFile()
    {
        string dir = WithLists(TestSupport.TempDir());
        var m = Machine.Load(MachineFile(dir, "datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncapture_runs = 'capture_runs.tsv'\ncysteine = 'cysteine.tsv'\ncatch_up_per_publish = 5\n"));
        Assert.That(m.PtmQtl, Is.EqualTo(new PtmQtlConfig(Path.Combine(dir, "enrichment.tsv"), Path.Combine(dir, "capture_runs.tsv"), Path.Combine(dir, "cysteine.tsv"), 5)));
        Assert.That(Machine.Load(MachineFile(dir, "datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\n")).PtmQtl,
            Is.EqualTo(new PtmQtlConfig(Path.Combine(dir, "enrichment.tsv"), null, Path.Combine(dir, "cysteine.tsv"), 20)), "capture_runs is optional; 20 a publish by default");
        Assert.That(Machine.Load(MachineFile(dir, "")).PtmQtl, Is.Null, "no [ptmqtl], no engines");
    }

    [TestCase("[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\n", "needs 'datarepo'")]
    [TestCase("datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'missing.tsv'\ncysteine = 'cysteine.tsv'\n", "does not exist")]
    [TestCase("datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\ncatch_up_per_publish = -1\n", "must not be negative")]
    [TestCase("datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\ntraits = 'x.tsv'\n", "traits")]
    [TestCase("datarepo = 'datarepo'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\n", "cysteine")]
    public void ABadPtmqtlTableIsRefused(string extra, string message)
    {
        string dir = WithLists(TestSupport.TempDir());
        Assert.That(() => Machine.Load(MachineFile(dir, extra)), Throws.InstanceOf<ConfigException>().With.Message.Contains(message));
    }

    [Test]
    public void AProbeWorkerRunsNoPtmQtlEngine()
    {
        string dir = WithLists(TestSupport.TempDir());
        string worker = "[worker]\nshare = 'S:/share'\nrun_root = 'runs'\nstate_dir = 'state'\n[ptmqtl]\nenrichment = 'enrichment.tsv'\ncysteine = 'cysteine.tsv'\n";
        Assert.That(() => Machine.Load(MachineFile(dir, worker)), Throws.InstanceOf<ConfigException>().With.Message.Contains("runs no ptmQtl engine"));
    }
}
