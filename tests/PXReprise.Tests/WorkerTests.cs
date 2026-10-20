using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Fetch;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.26 (design/PLAN_worker.md; the user, 2026-10-19: "it would only run probes. and the full searches would only be
/// run on this machine using those that passed the probe"): the share, the controller and the probe worker.
/// </summary>
public class WorkerTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
    private static string Self => PXReprise.Provenance.RunRecord.Versions()["pxreprise"];
    private const string Question = TestSupport.MinimalQuestion + "\n[batch]\nrun_root = \"runs\"\nstate_dir = \"state\"\nqueue = \"queue.json\"\n";

    private sealed class Files : IPrideFiles
    {
        public List<PrideArchiveFile> Listing { get; } = new()
        {
            new PrideArchiveFile { FileName = "probe1.raw", FileSizeBytes = 9 }, new PrideArchiveFile { FileName = "rest1.raw", FileSizeBytes = 11 },
        };
        public Task<List<PrideArchiveFile>> ListFilesAsync(string a, CancellationToken ct) => Task.FromResult(Listing);
        public Task<List<string>> ListFtpNamesAsync(string a, CancellationToken ct) => Task.FromResult(Listing.Select(f => f.FileName).ToList());
        public Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct) => throw new InvalidOperationException("no downloads in this test");
    }

    private static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }

    // gap 0: the worker is assigned the very next deposits, as before v0.4.29, unless a test is about the gap.
    private static (BatchRunner Runner, string Dir, WorkerShare Share) Controller(int window = 2, string top = "", int gap = 0)
    {
        string dir = TestSupport.TempDir(), share = Path.Combine(dir, "share");
        Directory.CreateDirectory(share);
        var question = QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml", Question));
        var machine = Machine.Load(TestSupport.WriteFile(dir, "machine.toml",
            $"work_root = '{dir}'\nmin_free_gb = 0\n{top}[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n[workers]\nshare = '{share}'\nwindow = {window}\ngap = {gap}\n"));
        var runner = new BatchRunner(question, Profiles, machine, new Files(), new FakeSearch(), "2026-10-19") { WorkerPoll = TimeSpan.FromMilliseconds(50) };
        return (runner, dir, new WorkerShare(share));
    }

    private static (BatchRunner Runner, string Dir, WorkerShare Share) Worker(string? release = null, int generation = 1)
    {
        string dir = TestSupport.TempDir(), share = Path.Combine(dir, "share");
        var ws = new WorkerShare(share);
        Directory.CreateDirectory(ws.Control);
        File.WriteAllText(ws.CurrentFile, $"generation = {generation}\npxreprise = \"{release ?? Self}\"\n");
        // The controller's copy of the question, as its driver publishes it at its start.
        ws.PublishQuestion(TestSupport.WriteFile(TestSupport.TempDir(), "question.toml", Question), Array.Empty<string>(), Array.Empty<string>());
        var question = QuestionLoader.Load(ws.QuestionFile);
        var machine = Machine.Load(TestSupport.WriteFile(dir, "machine.toml",
            $"work_root = '{dir}'\nmin_free_gb = 0\n[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n[worker]\nshare = '{share}'\nrun_root = 'runs'\nstate_dir = 'state'\n"));
        var runner = new BatchRunner(question, Profiles, machine, new Files(), new FakeSearch(), "2026-10-19")
        {
            WorkerPoll = TimeSpan.FromMilliseconds(50), HeartbeatEvery = TimeSpan.FromMilliseconds(50),
        };
        return (runner, dir, ws);
    }

    /// <summary>A worker's package for <paramref name="acc"/>, written to <paramref name="share"/> as the worker writes it.</summary>
    private static string Package(WorkerShare share, string acc, string outcome, string? version = null, int attempt = 1, string[]? decisions = null,
        string? question = null)
    {
        string run = Path.Combine(TestSupport.TempDir(), acc);
        Directory.CreateDirectory(Path.Combine(run, "02_fetch", "spectra"));
        Directory.CreateDirectory(Path.Combine(run, "01_experiment"));
        File.WriteAllText(Path.Combine(run, "02_fetch", "spectra", "probe1.raw"), "raw bytes");
        File.WriteAllText(Path.Combine(run, "02_fetch", "spectra", "probe1.raw.partial"), "never travels");
        File.WriteAllText(Path.Combine(run, "01_experiment", "experiment_notes.md"), "notes");
        var entry = new JsonObject
        {
            ["status"] = outcome == "passed" ? "probing" : outcome, ["profile"] = "label-free-dda@1", ["title"] = "t", ["organism"] = "human",
            ["probe_precursor_offsets_ppm"] = new JsonObject { ["probe1"] = 4.2 }, ["handed_off_attempt"] = attempt,
        };
        if (outcome != "passed") entry["detail"] = "WT_band_08: human 55 vs mouse 49";
        return share.WritePackage(run, acc, entry, decisions ?? Array.Empty<string>(), new JsonObject
        {
            ["outcome"] = outcome, ["profile"] = "label-free-dda@1", ["attempt"] = attempt, ["machine"] = "WORKER", ["pxreprise"] = version ?? Self, ["generation"] = 1,
            ["question_identity"] = question ?? share.QuestionIdentity(),
        }, withRaw: outcome == "passed");
    }

    private static string Log(string dir) => File.ReadAllText(Path.Combine(dir, "state", "batch.log"));

    // ------------------------------------------------------------------ the share

    [Test]
    public void APackageVerifiesAndAChangedMissingOrExtraFileFailsItsCheck()
    {
        var share = new WorkerShare(TestSupport.TempDir());
        string dir = Package(share, "PXD000401", "passed");
        var (pkg, problems) = WorkerShare.Verify(dir);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Is.Empty);
            Assert.That(pkg!.Outcome, Is.EqualTo("passed"));
            Assert.That(pkg.Entry["probe_precursor_offsets_ppm"]!["probe1"]!.GetValue<double>(), Is.EqualTo(4.2));
            Assert.That(File.Exists(Path.Combine(dir, "02_fetch", "spectra", "probe1.raw")), Is.True, "a pass carries its probe files");
            Assert.That(File.Exists(Path.Combine(dir, "02_fetch", "spectra", "probe1.raw.partial")), Is.False, "a partial download never travels");
            Assert.That(share.ReadyPackages(), Is.EqualTo(new[] { dir }));
        });

        File.WriteAllText(Path.Combine(dir, "01_experiment", "experiment_notes.md"), "notez");
        File.Delete(Path.Combine(dir, "02_fetch", "spectra", "probe1.raw"));
        File.WriteAllText(Path.Combine(dir, "stray.txt"), "?");
        var (bad, why) = WorkerShare.Verify(dir);
        Assert.That(bad, Is.Null);
        Assert.That(why, Has.Some.Contains("experiment_notes.md: SHA-256 differs")
            .And.Some.Contains("probe1.raw: missing").And.Some.Contains("stray.txt: not listed"));
    }

    [Test]
    public void AParkTravelsWithoutItsRawFilesButWithItsDecisionRows()
    {
        var share = new WorkerShare(TestSupport.TempDir());
        string row = "2026-10-19T00:00:00Z\tPXD000402\twaiting_species\tq\tWT_band_08: human 55 vs mouse 49\tchoices";
        string dir = Package(share, "PXD000402", "waiting_species", decisions: new[] { row });
        var (pkg, problems) = WorkerShare.Verify(dir);
        Assert.That(problems, Is.Empty);
        Assert.That(Directory.EnumerateFiles(dir, "*.raw", SearchOption.AllDirectories), Is.Empty);
        Assert.That(pkg!.DecisionLines, Is.EqualTo(new[] { row }));
    }

    [Test]
    public void AssignmentsAreReadByColumnNameAndATabInATitleIsCleaned()
    {
        var share = new WorkerShare(TestSupport.TempDir());
        share.WriteAssignments(new[]
        {
            new WorkerShare.Assignment("PXD000403", "mouse", 2, "2026-10-19T01:00:00Z", "a\ttabbed title"),
            new WorkerShare.Assignment("PXD000404", "rat", 1, "2026-10-19T02:00:00Z", "b"),
        });
        Assert.That(share.WriteAssignments(share.ReadAssignments()), Is.False, "unchanged assignments are not rewritten");
        File.WriteAllText(share.AssignmentsFile, "title\taccession\tassigned_utc\tattempt\torganism\r\nx\tPXD000405\t2026-10-19T03:00:00Z\t3\thuman\r\nnot an accession\tfoo\t\t\t\r\n");
        var rows = share.ReadAssignments();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0], Is.EqualTo(new WorkerShare.Assignment("PXD000405", "human", 3, "2026-10-19T03:00:00Z", "x")));
    }

    [Test]
    public void Sha256SumsCatchAChangedFileAndASetsIdentityIgnoresLineEnds()
    {
        string set = TestSupport.TempDir(), copy = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(set, "sub"));
        File.WriteAllText(Path.Combine(set, "human.xml"), "<proteins/>");
        File.WriteAllText(Path.Combine(set, "sub", "mouse.xml"), "<proteins/> mouse");
        Assert.That(Sha256Sums.Write(set), Is.EqualTo(2));
        WorkerShare.CopyTree(set, copy);
        File.WriteAllText(Path.Combine(copy, "other.xml"), "not in the set");
        string sums = Path.Combine(set, Sha256Sums.FileName);
        Assert.That(Sha256Sums.Check(copy, sums), Is.EqualTo((2, new List<string>())), "files the set does not name are allowed");
        File.WriteAllText(Path.Combine(copy, "sub", "mouse.xml"), "<proteins/> rat");
        Assert.That(Sha256Sums.Check(copy, sums).Problems, Is.EqualTo(new[] { "sub/mouse.xml: SHA-256 differs" }));
        string id = Sha256Sums.Identity(sums)!;
        File.WriteAllText(sums, File.ReadAllText(sums).Replace("\n", "\r\n"));
        Assert.That(Sha256Sums.Identity(sums), Is.EqualTo(id));
    }

    [Test]
    public void AWorkerIsQuietWithNoFreshHeartbeatOrOneSayingItStopped()
    {
        var share = new WorkerShare(TestSupport.TempDir());
        var now = DateTime.UtcNow;
        Assert.That(share.Quiet(TimeSpan.FromHours(2), now), Is.True, "no heartbeat yet");
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        Assert.That(share.Quiet(TimeSpan.FromHours(2), now), Is.False);
        Assert.That(share.Quiet(TimeSpan.FromHours(2), now.AddHours(3)), Is.True, "stale");
        share.WriteHeartbeat(new JsonObject { ["state"] = "stopped: STOP" });
        Assert.That(share.Quiet(TimeSpan.FromHours(2), now), Is.True, "a fresh heartbeat that says it stopped");
    }

    [Test]
    public void AReleasePublishesTheBuildOnceAndRaisesTheGeneration()
    {
        var share = new WorkerShare(TestSupport.TempDir());
        string build = TestSupport.TempDir();
        File.WriteAllText(Path.Combine(build, "pxreprise.dll"), "build");
        Directory.CreateDirectory(Path.Combine(share.DatabaseSets, "set-1"));
        File.WriteAllText(Path.Combine(share.DatabaseSets, "set-1", "human.xml"), "<proteins/>");
        Sha256Sums.Write(Path.Combine(share.DatabaseSets, "set-1"));

        var first = share.Release(build, "0.4.26+abc1234", null, null, "set-1", "first");
        var second = share.Release(build, "0.4.26+abc1234", null, null, null, "again");
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new WorkerShare.Current(1, "0.4.26+abc1234", "pxreprise-abc1234", null, null, "set-1")));
            Assert.That(second.Generation, Is.EqualTo(2));
            Assert.That(second.DatabaseSet, Is.EqualTo("set-1"), "a value not given keeps the release's");
            Assert.That(share.ReadCurrent(), Is.EqualTo(second));
            Assert.That(Sha256Sums.Check(Path.Combine(share.Software, "pxreprise-abc1234")).Problems, Is.Empty);
            Assert.That(File.ReadAllLines(share.ReleasesFile), Has.Length.EqualTo(3));
        });
        Assert.Throws<PXReprise.Cli.UsageException>(() => share.Release(build, "0.4.26+abc1234", null, null, "no-such-set", "x"));
    }

    [Test]
    public void TheControllersLimitsTravelToTheWorkersProbes()
    {
        var (runner, _, share) = Worker();
        share.WriteLimits(WorkerShare.LimitsText(new DepositLimits(160, null, 3_500_000, 2600), 240, "MOZZLE"));
        runner.RefreshLimits(now: true);
        Assert.That(runner.Limits, Is.EqualTo(new DepositLimits(160, null, 3_500_000, 2600)));
    }

    [Test]
    public void AMachineIsAWorkerOrAControllerAndAWorkerNeverIngests()
    {
        string dir = TestSupport.TempDir();
        string Machine0(string extra) => TestSupport.WriteFile(dir, $"m{Guid.NewGuid():N}.toml", $"work_root = '{dir}'\n{extra}[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n");
        const string Worker = "[worker]\nshare = 'S'\nrun_root = 'runs'\nstate_dir = 'state'\n";
        Assert.That(Assert.Throws<ConfigException>(() => Machine.Load(Machine0("datarepo = 'C:/x/datarepo.exe'\n" + Worker)))!.Message, Does.Contain("never ingests"));
        Assert.Throws<ConfigException>(() => Machine.Load(Machine0("[worker]\nshare = 'S'\n")), "a worker names its own run root and state directory");
        Assert.Throws<ConfigException>(() => Machine.Load(TestSupport.WriteFile(dir, "both.toml",
            $"work_root = '{dir}'\n[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n{Worker}[workers]\nshare = 'T'\n")));
        var m = Machine.Load(TestSupport.WriteFile(dir, "ctl.toml", $"work_root = '{dir}'\n[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n[workers]\nshare = 'T'\n"));
        Assert.That(m.Workers, Is.EqualTo(new WorkersConfig("T", 6, 2, 24, 5)), "the worker is kept 5 deposits behind this machine's next");
        Assert.That(Machine.Load(TestSupport.WriteFile(dir, "gap.toml", $"work_root = '{dir}'\n[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n[workers]\nshare = 'T'\ngap = 10\n")).Workers!.Gap,
            Is.EqualTo(10));
        Assert.That(Assert.Throws<ConfigException>(() => Machine.Load(TestSupport.WriteFile(dir, "neg.toml",
            $"work_root = '{dir}'\n[metamorpheus]\n\"1.1.11\" = 'C:/x/CMD.exe'\n[workers]\nshare = 'T'\ngap = -1\n")))!.Message, Does.Contain("'workers.gap'"));
        var w = Machine.Load(Machine0(Worker)).Worker!;
        Assert.That((w.Share, w.RunRoot, w.StateDir), Is.EqualTo(("S", Path.Combine(dir, "runs"), Path.Combine(dir, "state"))), "relative to the machine file");
    }

    // ------------------------------------------------------------------ the controller

    [Test]
    public async Task AFreshDepositWaitsForTheWorkersProbeAndItsPassIsSearchedHere()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "waiting for assignments" });
        var probedHere = new List<string>();
        var searched = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); return Task.FromResult<Profile?>(null); };
        runner.FetchWholeForTest = (e, p, ct) => { runner.Record(e.Accession, ("status", "fetched")); return Task.FromResult(true); };
        runner.SearchForTest = (e, p, ct) => { lock (searched) searched.Add($"{e.Accession} {p.Key}"); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000501", "a", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000501"), cts.Token);
        Assert.That(searched, Is.Empty, "nothing is searched before the worker's probe");
        Package(share, "PXD000501", "passed");
        await run;

        var entry = runner.State()["datasets"]!["PXD000501"]!;
        string target = Path.Combine(dir, "runs", "PXD000501");
        Assert.Multiple(() =>
        {
            Assert.That(probedHere, Is.Empty, "no probe here");
            Assert.That(searched, Is.EqualTo(new[] { "PXD000501 label-free-dda@1" }));
            Assert.That(entry["probed_by"]!.GetValue<string>(), Is.EqualTo("WORKER"));
            Assert.That(entry["probe_precursor_offsets_ppm"]!["probe1"]!.GetValue<double>(), Is.EqualTo(4.2), "the probe's measurements are taken");
            Assert.That(entry["handed_off_attempt"], Is.Null, "the worker's bookkeeping is not");
            Assert.That(File.Exists(Path.Combine(target, "02_fetch", "spectra", "probe1.raw")), Is.True, "its probe files are here for the whole fetch");
            Assert.That(File.Exists(Path.Combine(target, "00_worker", WorkerShare.ReadyName)), Is.True);
            Assert.That(Directory.Exists(Path.Combine(share.Deposits, "PXD000501")), Is.False, "the package left the share");
            Assert.That(Log(dir), Does.Contain("PXD000501 PROBE PACKAGE taken from WORKER (attempt 1): passed under label-free-dda@1"));
        });
    }

    [Test]
    public async Task AParkFromTheWorkerIsRecordedAndAnnouncedHere()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.ProbeForTest = (e, ct) => throw new AssertionException("no probe here");
        runner.SearchForTest = (e, p, ct) => throw new AssertionException("a park is not searched");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000502", "b", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000502"), cts.Token);
        Package(share, "PXD000502", "waiting_species", decisions: new[] { "2026-10-19T00:00:00Z\tPXD000502\twaiting_species\tq\tWT_band_08: human 55 vs mouse 49\tc" });
        await run;

        Assert.That(runner.Settled("PXD000502"), Is.EqualTo("waiting_species"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "runs", DecisionsNeeded.FileName)), Does.Contain("PXD000502\twaiting_species"));
        Assert.That(Log(dir), Does.Contain("PXD000502 DECISION NEEDED (waiting_species)"));
    }

    [Test]
    public async Task WithTheWorkerQuietThisMachineProbesForItself()
    {
        var (runner, _, share) = Controller();
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { probedHere.Add(e.Accession); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runner.RunAsync(new[] { new QueueEntry("PXD000503", "c", "human") }, cts.Token);
        Assert.That(probedHere, Is.EqualTo(new[] { "PXD000503" }));
        Assert.That(share.ReadAssignments(), Is.Empty, "nothing is assigned to a worker that is not there");
    }

    // A worker not yet switched to this driver's build or question gets the deposit again; after three refused packages
    // this machine probes it.
    [Test]
    public async Task APackageFromAnotherBuildOrQuestionIsAssignedAgainAndTheThirdRefusalIsProbedHere()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000504", "d", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000504"), cts.Token);
        Assert.That(File.Exists(share.QuestionFile), Is.True, "the driver published the question it loaded");
        Package(share, "PXD000504", "passed", version: "0.0.0+other");
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000504" && a.Attempt == 2), cts.Token);
        Assert.That(probedHere, Is.Empty, "assigned again, not probed here yet");
        Package(share, "PXD000504", "passed", attempt: 2, question: "0123456789abcdef");
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000504" && a.Attempt == 3), cts.Token);
        Package(share, "PXD000504", "passed", attempt: 3, version: "0.0.0+other");
        await run;

        var asides = Directory.EnumerateDirectories(share.Deposits, "PXD000504.rejected-*").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(probedHere, Is.EqualTo(new[] { "PXD000504" }), "after the third refusal");
            Assert.That(asides, Has.Count.EqualTo(3));
            Assert.That(asides.SelectMany(a => Directory.EnumerateFiles(a, "*.raw", SearchOption.AllDirectories)), Is.Empty, "a refused package keeps no raw file");
            Assert.That(Log(dir), Does.Contain("probed by pxreprise 0.0.0+other").And.Contain("under another copy of the question (0123456789ab)")
                .And.Contain("the deposit is probed here"));
        });
    }

    [Test]
    public async Task ADamagedPackageIsSetAsideAndTheDepositAssignedAgain()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.ProbeForTest = (e, ct) => throw new AssertionException("no probe here");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000505", "e", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000505"), cts.Token);
        // Damaged in transit: built elsewhere, changed, then moved in whole, so the controller never sees it intact.
        var staging = new WorkerShare(TestSupport.TempDir());
        string pkg = Package(staging, "PXD000505", "passed");
        File.WriteAllText(Path.Combine(pkg, "01_experiment", "experiment_notes.md"), "damaged");
        Directory.CreateDirectory(share.Deposits);
        Directory.Move(pkg, Path.Combine(share.Deposits, "PXD000505"));
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000505" && a.Attempt == 2), cts.Token);
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "test");
        await run;

        Assert.That(Directory.EnumerateDirectories(share.Deposits, "PXD000505.rejected-*").Count(), Is.EqualTo(1));
        Assert.That(runner.State()["datasets"]!["PXD000505"]!["worker_attempt"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(Log(dir), Does.Contain("assigned again (attempt 2 of 3)"));
    }

    // v0.4.27 (the user: "it would only run probes", then "do what works best"): not only fresh deposits. On 2026-10-19 the
    // ageing queue held one fresh deposit of 295; the rest of the work was re-searches, retries and second looks.
    [Test]
    public async Task AReSearchOrRetryIsProbedByTheWorkerTooAndADepositWhoseFilesAreHereIsProbedHere()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.Record("PXD000701", ("status", "requeued_user"), ("title", "a"), ("organism", "human"));
        // Downloaded ahead under STOP: every listed raw file is here.
        runner.Record("PXD000702", ("status", "fetched"), ("title", "b"), ("organism", "human"), ("n_listed", 2));
        string spectra = Path.Combine(dir, "runs", "PXD000702", "02_fetch", "spectra");
        Directory.CreateDirectory(spectra);
        TestSupport.WriteFile(spectra, "x1.raw", "raw");
        TestSupport.WriteFile(spectra, "x2.raw", "raw");
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000701", "a", "human"), new QueueEntry("PXD000702", "b", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000701") && probedHere.Contains("PXD000702"), cts.Token);
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "test");
        await run;

        Assert.That(probedHere, Is.EqualTo(new[] { "PXD000702" }), "the retry went to the worker; the deposit whose files are here was probed here");
        Assert.That(runner.State()["datasets"]!["PXD000701"]!["status"]!.GetValue<string>(), Is.EqualTo(BatchRunner.AssignedStatus));
    }

    // A catch-up re-search moves its earlier search aside, raw files with it, so files on disk do not keep its probe here.
    [Test]
    public async Task ACatchUpReSearchIsAssignedToTheWorkerEvenWithItsRawFilesStillHere()
    {
        var (runner, dir, share) = Controller(top: "catch_up_every = 4\n");
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.Record("PXD000731", ("status", "searched"), ("profile", "label-free-dda@0"), ("title", "c"), ("organism", "human"), ("n_listed", 1));
        string run = Path.Combine(dir, "runs", "PXD000731");
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        TestSupport.WriteFile(Path.Combine(run, "04_search"), "provenance.json", "{}");
        Directory.CreateDirectory(Path.Combine(run, "02_fetch", "spectra"));
        TestSupport.WriteFile(Path.Combine(run, "02_fetch", "spectra"), "x1.raw", "raw");
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var go = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000731"), cts.Token);
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "test");
        await go;

        Assert.That(probedHere, Is.Empty);
        Assert.That(Log(dir), Does.Contain("PXD000731 RE-SEARCH (catch-up").And.Contain("PXD000731 ASSIGNED to the probe worker (attempt 1; a catch-up re-search)"));
    }

    // v0.4.29 (the user, 2026-10-20: "keep the worker stocked with probe sets that are five or 10 spots down the line"): on
    // 2026-10-20 the worker probed PXD028590 while this machine, having taken it back, fetched it.
    [Test]
    public async Task TheWorkerIsStockedBeyondTheGapWhileThisMachineProbesTheDepositsAheadOfIt()
    {
        var (runner, dir, share) = Controller(window: 2, gap: 2);
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var queue = Enumerable.Range(1, 5).Select(i => new QueueEntry($"PXD00074{i}", "t", "human")).ToArray();

        var run = runner.RunAsync(queue, cts.Token);
        await Until(() => { lock (probedHere) return probedHere.Count == 3; }, cts.Token);
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "test");
        await run;

        Assert.Multiple(() =>
        {
            Assert.That(share.ReadAssignments().Select(a => a.Accession), Is.EqualTo(new[] { "PXD000743", "PXD000744" }),
                "the two after this machine's next two");
            Assert.That(probedHere, Is.EqualTo(new[] { "PXD000741", "PXD000742", "PXD000745" }),
                "this machine probes its own two, passes over the worker's, and probes the next with the worker's window full");
            Assert.That(Log(dir), Does.Contain("the next 2 deposit(s) are probed here, and the worker's window of 2 is filled from the ones after them"));
        });
    }

    [Test]
    public async Task AWorkersPassGoesAheadOfTheDepositsStillToBeProbedHere()
    {
        var (runner, dir, share) = Controller(window: 1, gap: 5);
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.Record("PXD000753", ("status", BatchRunner.ProbedByWorkerStatus), ("profile", "label-free-dda@1"), ("title", "p"), ("organism", "human"));
        var order = new List<string>();
        void Did(string what) { lock (order) order.Add(what); }
        runner.ProbeForTest = (e, ct) => { Did($"probe {e.Accession}"); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        runner.FetchWholeForTest = (e, p, ct) => { Did($"fetch {e.Accession}"); runner.Record(e.Accession, ("status", "fetched")); return Task.FromResult(true); };
        runner.SearchForTest = (e, p, ct) => { Did($"search {e.Accession}"); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var queue = new[] { new QueueEntry("PXD000751", "a", "human"), new QueueEntry("PXD000752", "b", "human"), new QueueEntry("PXD000753", "p", "human") };

        var run = runner.RunAsync(queue, cts.Token);
        await Until(() => { lock (order) return order.Count >= 4; }, cts.Token);
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "test");
        await run;

        Assert.Multiple(() =>
        {
            Assert.That(order.IndexOf("fetch PXD000753"), Is.EqualTo(0), "the worker's pass, last in the queue, is taken first");
            Assert.That(order, Does.Contain("probe PXD000751").And.Contain("probe PXD000752").And.Contain("search PXD000753"));
            Assert.That(share.ReadAssignments(), Is.Empty, "two deposits are within the gap of 5: nothing for the worker");
            Assert.That(Log(dir), Does.Contain("PXD000753 taken next: the probe worker passed it"));
        });
    }

    [Test]
    public async Task ATakeBackRewritesTheWorkersAssignmentsBeforeTheDepositIsProbedHere()
    {
        var (runner, dir, share) = Controller(window: 1);
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        List<WorkerShare.Assignment>? atProbe = null;
        runner.ProbeForTest = (e, ct) => { atProbe = share.ReadAssignments(); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(new[] { new QueueEntry("PXD000761", "t", "human") }, cts.Token);
        await Until(() => share.ReadAssignments().Any(a => a.Accession == "PXD000761"), cts.Token);
        share.WriteHeartbeat(new JsonObject { ["state"] = "stopped: cancelled" });   // the worker went down
        await run;

        Assert.That(atProbe, Is.Empty, "the worker must not find it in assigned.tsv while this machine probes it");
        Assert.That(Log(dir), Does.Contain("PXD000761 taken back from the probe worker (the probe worker is quiet): probed here"));
    }

    // Owed since 2026-10-20: PXD006815's worker probe failed (no memory left there) and it was assigned straight back.
    [Test]
    public async Task ADepositWhoseWorkerProbeFailedIsProbedHereNotAssignedAgain()
    {
        var (runner, dir, share) = Controller();
        share.WriteHeartbeat(new JsonObject { ["state"] = "working" });
        runner.Record("PXD000771", ("status", "probe_search_failed"), ("probed_by", "WORKER"), ("worker_attempt", 1), ("title", "t"), ("organism", "human"));
        var probedHere = new List<string>();
        runner.ProbeForTest = (e, ct) => { lock (probedHere) probedHere.Add(e.Accession); runner.Record(e.Accession, ("status", "skipped_test")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await runner.RunAsync(new[] { new QueueEntry("PXD000771", "t", "human") }, cts.Token);

        Assert.That(probedHere, Is.EqualTo(new[] { "PXD000771" }));
        Assert.That(share.ReadAssignments(), Is.Empty);
        Assert.That(Log(dir), Does.Not.Contain("PXD000771 ASSIGNED"));
    }

    // ------------------------------------------------------------------ the probe worker

    [Test]
    public async Task TheWorkerProbesADepositAssignedAgainEvenWithTheSameAttemptNumber()
    {
        var (runner, dir, share) = Worker();
        // Handed off once before (the comparison test of 2026-10-19), then assigned again later as a catch-up re-search.
        runner.Record("PXD000721", ("status", "handed_off"), ("handed_off_attempt", 1), ("handed_off_assigned_utc", "2026-10-19T19:01:34Z"));
        share.WriteAssignments(new[] { new WorkerShare.Assignment("PXD000721", "human", 1, "2026-10-20T01:00:00.000Z", "a") });
        int probes = 0;
        runner.ProbeForTest = (e, ct) => { Interlocked.Increment(ref probes); runner.Record(e.Accession, ("status", "waiting_species"), ("detail", "x")); return Task.FromResult<Profile?>(null); };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.ReadyPackages().Count == 1, cts.Token);
        File.WriteAllText(share.StopFile, "test");
        await run;

        Assert.That(probes, Is.EqualTo(1));
        Assert.That(runner.State()["datasets"]!["PXD000721"]!["handed_off_assigned_utc"]!.GetValue<string>(), Is.EqualTo("2026-10-20T01:00:00.000Z"));
    }

    [Test]
    public async Task TheWorkerProbesOnlyItsAssignmentsHandsThemOffAndDeletesItsProbeFiles()
    {
        var (runner, dir, share) = Worker();
        string runs = Path.Combine(dir, "runs");
        share.WriteAssignments(new[]
        {
            new WorkerShare.Assignment("PXD000601", "human", 1, "2026-10-19T00:00:00Z", "a"),
            new WorkerShare.Assignment("PXD000602", "human", 1, "2026-10-19T00:00:00Z", "b"),
        });
        runner.ProbeForTest = (e, ct) =>
        {
            string spectra = Path.Combine(runs, e.Accession, "02_fetch", "spectra");
            Directory.CreateDirectory(spectra);
            File.WriteAllText(Path.Combine(spectra, "p1.raw"), "raw");
            if (e.Accession == "PXD000601")
            {
                runner.Record(e.Accession, ("profile", "label-free-dda@1"));
                return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]);
            }
            runner.Record(e.Accession, ("status", "waiting_species"), ("detail", "WT_band_08: human 55 vs mouse 49"));
            DecisionsNeeded.Announce(runs, e.Accession, "waiting_species", "WT_band_08: human 55 vs mouse 49");
            return Task.FromResult<Profile?>(null);
        };
        runner.SearchForTest = (e, p, ct) => throw new AssertionException("a probe worker never searches");
        runner.FetchWholeForTest = (e, p, ct) => throw new AssertionException("a probe worker never fetches a whole deposit");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.ReadyPackages().Count == 2, cts.Token);
        File.WriteAllText(share.StopFile, "test");
        await run;

        var (pass, p1) = WorkerShare.Verify(Path.Combine(share.Deposits, "PXD000601"));
        var (park, p2) = WorkerShare.Verify(Path.Combine(share.Deposits, "PXD000602"));
        Assert.Multiple(() =>
        {
            Assert.That(p1.Concat(p2), Is.Empty);
            Assert.That((pass!.Outcome, pass.Profile, pass.Pxreprise), Is.EqualTo(("passed", "label-free-dda@1", Self)));
            Assert.That(File.Exists(Path.Combine(pass.Dir, "02_fetch", "spectra", "p1.raw")), Is.True);
            Assert.That(park!.Outcome, Is.EqualTo("waiting_species"));
            Assert.That(Directory.EnumerateFiles(park.Dir, "*.raw", SearchOption.AllDirectories), Is.Empty);
            Assert.That(park.DecisionLines.Single(), Does.Contain("PXD000602\twaiting_species"));
            Assert.That(File.Exists(Path.Combine(runs, "PXD000601", "02_fetch", "spectra", "p1.raw")), Is.False, "probe files are deleted once handed off");
            Assert.That(File.Exists(Path.Combine(runs, "PXD000602", "02_fetch", "spectra", "p1.raw")), Is.False);
            Assert.That(runner.State()["datasets"]!["PXD000601"]!["status"]!.GetValue<string>(), Is.EqualTo("handed_off"));
            Assert.That(share.Heartbeat().State, Does.StartWith("stopped"));
        });
    }

    [Test]
    public async Task TheWorkerDeletesWhatTheControllerTookAndWhatItWasNoLongerAskedFor()
    {
        var (runner, dir, share) = Worker();
        string runs = Path.Combine(dir, "runs");
        share.WriteAssignments(new[]
        {
            new WorkerShare.Assignment("PXD000611", "human", 1, "2026-10-19T00:00:00Z", "a"),
            new WorkerShare.Assignment("PXD000612", "human", 1, "2026-10-19T00:00:00Z", "b"),
        });
        runner.ProbeForTest = (e, ct) =>
        {
            Directory.CreateDirectory(Path.Combine(runs, e.Accession, "02c_probe_search"));
            File.WriteAllText(Path.Combine(runs, e.Accession, "02c_probe_search", "AllPSMs.psmtsv"), "psms");
            if (e.Accession == "PXD000612") return Task.FromResult<Profile?>(null);   // PRIDE did not answer: left "probing"
            runner.Record(e.Accession, ("profile", "label-free-dda@1"));
            return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]);
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.ReadyPackages().Count == 1 && Directory.Exists(Path.Combine(runs, "PXD000612")), cts.Token);
        Directory.Delete(Path.Combine(share.Deposits, "PXD000611"), recursive: true);   // the controller took it in
        share.WriteAssignments(Array.Empty<WorkerShare.Assignment>());                    // and took PXD000612 back
        await Until(() => !Directory.Exists(Path.Combine(runs, "PXD000611")) && !Directory.Exists(Path.Combine(runs, "PXD000612")), cts.Token);
        File.WriteAllText(share.StopFile, "test");
        await run;

        Assert.That(Log(dir), Does.Contain("PXD000611 local folder deleted (the controller has its package)")
            .And.Contain("PXD000612 local folder deleted (no longer assigned: the controller took it back)"));
    }

    [Test]
    public async Task TheWorkerDoesNotStartOnAReleaseThatIsNotItsBuild()
    {
        var (runner, dir, share) = Worker(release: "0.0.0+other");
        share.WriteAssignments(new[] { new WorkerShare.Assignment("PXD000621", "human", 1, "2026-10-19T00:00:00Z", "a") });
        runner.ProbeForTest = (e, ct) => throw new AssertionException("nothing is probed on another release");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        Assert.That(Log(dir), Does.Contain("not starting").And.Contain("names pxreprise 0.0.0+other"));
        Assert.That(share.Heartbeat().State, Does.StartWith("not started"));
        Assert.That(share.Quiet(TimeSpan.FromHours(2), DateTime.UtcNow), Is.True, "so the controller does not wait on it");
    }

    [Test]
    public async Task TheWorkerRunsOnlyTheControllersCopyOfTheQuestion()
    {
        var (_, dir, share) = Worker();
        var own = QuestionLoader.Load(TestSupport.WriteFile(dir, "own.toml", Question));
        var runner = new BatchRunner(own, Profiles, Machine.Load(Path.Combine(dir, "machine.toml")), new Files(), new FakeSearch(), "2026-10-19");
        runner.ProbeForTest = (e, ct) => throw new AssertionException("nothing is probed by another question");
        share.WriteAssignments(new[] { new WorkerShare.Assignment("PXD000631", "human", 1, "2026-10-19T00:00:00Z", "a") });
        await runner.RunAsync(Array.Empty<QueueEntry>(), CancellationToken.None);
        Assert.That(Log(dir), Does.Contain("not starting").And.Contain("runs the controller's copy of the question"));
    }

    [Test]
    public async Task TheWorkerStopsWhenTheControllersQuestionChanges()
    {
        var (runner, dir, share) = Worker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.Heartbeat().State == "waiting for assignments", cts.Token);
        share.PublishQuestion(TestSupport.WriteFile(TestSupport.TempDir(), "question.toml", Question + "\n[holds]\nPXD000999 = \"held\"\n"),
            Array.Empty<string>(), Array.Empty<string>());
        await run;
        Assert.That(Log(dir), Does.Contain("the controller's question changed"));
        Assert.That(share.Heartbeat().State, Is.EqualTo("stopped: question changed: start again"));
    }

    [Test]
    public void TheQuestionIsPublishedWithTheFilesItReadsAndNothingOutsideItsFolder()
    {
        string home = TestSupport.TempDir(), outside = TestSupport.TempDir();
        string q = TestSupport.WriteFile(home, "question.toml", Question);
        TestSupport.WriteFile(home, "decisions.tsv", "accession\tdecision\n");
        Directory.CreateDirectory(Path.Combine(home, "designs"));
        TestSupport.WriteFile(Path.Combine(home, "designs"), "PXD000641.sdrf.tsv", "source name\n");
        var share = new WorkerShare(TestSupport.TempDir());

        var (id, left) = share.PublishQuestion(q, new[] { Path.Combine(home, "decisions.tsv"), TestSupport.WriteFile(outside, "x.tsv", "x") },
            new[] { Path.Combine(home, "designs") });
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(share.QuestionDir, "designs", "PXD000641.sdrf.tsv")), Is.True);
            Assert.That(File.Exists(Path.Combine(share.QuestionDir, "decisions.tsv")), Is.True);
            Assert.That(left, Is.EqualTo(new[] { Path.Combine(outside, "x.tsv") }));
            Assert.That(Sha256Sums.Check(share.QuestionDir).Problems, Is.Empty);
            Assert.That(share.PublishQuestion(q, new[] { Path.Combine(home, "decisions.tsv") }, new[] { Path.Combine(home, "designs") }).Identity,
                Is.EqualTo(id), "the same question publishes the same identity");
        });
    }

    [Test]
    public async Task TheWorkerStopsAsSoonAsTheReleaseChanges()
    {
        var (runner, dir, share) = Worker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runner.RunAsync(Array.Empty<QueueEntry>(), cts.Token);
        await Until(() => share.Heartbeat().State == "waiting for assignments", cts.Token);
        File.WriteAllText(share.CurrentFile, $"generation = 2\npxreprise = \"{Self}\"\n");
        await run;
        Assert.That(Log(dir), Does.Contain("generation 1 -> 2").And.Contain("stops for the switch"));
        Assert.That(share.Heartbeat().State, Does.StartWith("stopped: release generation 1 -> 2"));
    }
}
