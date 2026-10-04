using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Fetch;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

public class BatchRunnerTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
    private static QcGates Gates => Profiles["label-free-dda@1"].Qc;

    private static JsonObject Report(params (string File, bool Pass, string[] Reasons)[] files)
    {
        var o = new JsonObject();
        foreach (var (f, p, rs) in files) o[f] = new JsonObject { ["pass"] = p, ["fail_reasons"] = new JsonArray(rs.Select(r => (JsonNode?)r).ToArray()) };
        return o;
    }

    [Test]
    public void OneBlankRunIsExcludedButAnAcquisitionFailureDropsTheDeposit()
    {
        var files = Enumerable.Range(1, 20).Select(i => ($"f{i:00}.raw", true, Array.Empty<string>())).ToList();
        Assert.That(BatchRunner.QcExclusion(Report(files.ToArray()), Gates), Is.Empty);
        files[3] = ("f04.raw", false, new[] { "too_few_ms2" });
        Assert.That(BatchRunner.QcExclusion(Report(files.ToArray()), Gates), Is.EqualTo(new[] { "f04.raw" }));
        files[5] = ("f06.raw", false, new[] { "too_few_ms2" });
        files[7] = ("f08.raw", false, new[] { "too_few_ms2" });   // 3 of 20 > 10%
        Assert.That(BatchRunner.QcExclusion(Report(files.ToArray()), Gates), Is.Null);
        Assert.That(BatchRunner.QcExclusion(Report(("a.raw", false, new[] { "low_res_ms2" }), ("b.raw", true, Array.Empty<string>())), Gates), Is.Null);
    }

    private static (BatchRunner Runner, FakeSearch Search, FakeFiles Files, string Dir) Runner(string extraQuestion = "", string extraMachine = "")
    {
        string dir = TestSupport.TempDir();
        string q = TestSupport.MinimalQuestion + $"\n[batch]\nrun_root = \"runs\"\nstate_dir = \"state\"\nqueue = \"queue.json\"\n" + extraQuestion;
        var question = QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml", q));
        var machine = Machine.Load(TestSupport.WriteFile(dir, "machine.toml",
            $"work_root = '{dir}'\nmin_free_gb = 0\n{extraMachine}[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n"));
        var search = new FakeSearch();
        var files = new FakeFiles();
        return (new BatchRunner(question, Profiles, machine, files, search, "2026-09-28"), search, files, dir);
    }

    [Test]
    public async Task TheScreenRoutesBeforeAnyDownload()
    {
        var (runner, search, files, _) = Runner();
        search.Results["PXD000201"] = new() { TestSupport.Record("PXD000201", "Type 2 diabetes plasma", "DIA-NN library-free") };
        search.Results["PXD000202"] = new() { TestSupport.Record("PXD000202", "Type 2 diabetes islets", "TMTpro 16-plex") };
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000201", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000202", "t", "human"), CancellationToken.None), Is.Null);
        var ds = runner.State()["datasets"]!;
        Assert.That(ds["PXD000201"]!["status"]!.GetValue<string>(), Is.EqualTo("waiting_dia"));
        Assert.That(ds["PXD000202"]!["status"]!.GetValue<string>(), Is.EqualTo("waiting_tmt_dda_1"));
        Assert.That(files.Downloads, Is.Zero, "nothing is downloaded for a deposit no profile takes");
        Assert.That(runner.Settled("PXD000201"), Is.EqualTo("waiting_dia"));
    }

    [Test]
    public async Task WholeDepositsOnlyTooManyOrTooLargeIsDeferredNotSubsampled()
    {
        var (runner, search, files, _) = Runner();
        search.Results["PXD000203"] = new() { TestSupport.Record("PXD000203", "Type 2 diabetes muscle") };
        files.Listing = Enumerable.Range(0, 61).Select(i => new PrideArchiveFile { FileName = $"f{i}.raw", FileSizeBytes = 500_000_000 }).ToList();
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000203", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000203"), Is.EqualTo("deferred_needs_design_selection"));

        search.Results["PXD000204"] = new() { TestSupport.Record("PXD000204", "Type 2 diabetes liver") };
        files.Listing = Enumerable.Range(0, 10).Select(i => new PrideArchiveFile { FileName = $"g{i}.raw", FileSizeBytes = 3_000_000_000 }).ToList();
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000204", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000204"), Is.EqualTo("deferred_large_files"));
        Assert.That(files.Downloads, Is.Zero);
    }

    // PXR-A9: PXD012985 was PXD011740 deposited again, and was fetched and searched in full before anyone saw it.
    [Test]
    public async Task ADepositWhoseRawFilesAreAlreadySearchedIsRefusedBeforeAnyDownload()
    {
        var (runner, search, files, dir) = Runner();
        string searched = Path.Combine(dir, "runs", "PXD000211");
        Directory.CreateDirectory(Path.Combine(searched, "02_fetch"));
        Directory.CreateDirectory(Path.Combine(searched, "04_search"));
        File.WriteAllText(Path.Combine(searched, "04_search", "provenance.json"), "{}");
        File.WriteAllText(Path.Combine(searched, "02_fetch", "fetch_manifest.json"), """
            {"accession":"PXD000211","files":[
              {"name":"sample1.raw","pride_size_bytes":772102128},{"name":"sample2.raw","pride_size_bytes":783929316},
              {"name":"sample3.raw","pride_size_bytes":700000000}]}
            """);

        search.Results["PXD000212"] = new() { TestSupport.Record("PXD000212", "Type 2 diabetes chondrocytes") };
        files.Listing = new() { new() { FileName = "sample1.raw", FileSizeBytes = 772102128 }, new() { FileName = "sample2.raw", FileSizeBytes = 783929316 } };
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000212", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000212"), Is.EqualTo("excluded_duplicate"));
        Assert.That(runner.State()["datasets"]!["PXD000212"]!["duplicate_of"]!.GetValue<string>(), Is.EqualTo("PXD000211"));
        Assert.That(files.Downloads, Is.Zero);

        // The same name with another size is another file; sharing some files is logged, never refused.
        search.Results["PXD000213"] = new() { TestSupport.Record("PXD000213", "Type 2 diabetes chondrocytes") };
        files.Listing = new() { new() { FileName = "sample1.raw", FileSizeBytes = 772102128 }, new() { FileName = "sample2.raw", FileSizeBytes = 1 } };
        await runner.ProbeAsync(new QueueEntry("PXD000213", "t", "human"), CancellationToken.None);
        Assert.That(runner.Settled("PXD000213"), Is.Not.EqualTo("excluded_duplicate"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("PXD000213 shares 1 of 2 raw files (name and size) with PXD000211"));
    }

    // G5 (aging S66): a machine-wide update replaced .NET 10.0.8 with 10.0.10 and PXD021194 died 2 h 21 m into its search.
    [Test]
    public void ASearchTheRuntimeChangedUnderIsRetriedTwiceThenSettled()
    {
        var (runner, _, _, dir) = Runner();
        string root = Path.Combine(dir, "dotnet"), shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        Directory.CreateDirectory(Path.Combine(shared, "10.0.8"));
        var before = PXReprise.Search.DotnetRuntimes.Snapshot(root);
        Assert.That(before, Is.EqualTo(new[] { "10.0.8" }));
        Assert.That(runner.InterruptedByRuntime("PXD000217", before, root), Is.False, "nothing changed: the failure is the search's own");

        Directory.Delete(Path.Combine(shared, "10.0.8"));
        Directory.CreateDirectory(Path.Combine(shared, "10.0.10"));
        string search = Path.Combine(dir, "runs", "PXD000217", "04_search");
        Directory.CreateDirectory(search);
        File.WriteAllText(Path.Combine(search, "provenance.json"), "{}");
        Assert.That(runner.InterruptedByRuntime("PXD000217", before, root), Is.True);
        Assert.That(runner.State()["datasets"]!["PXD000217"]!["status"]!.GetValue<string>(), Is.EqualTo("search_interrupted"));
        Assert.That(Directory.Exists(search), Is.False, "the failed output is moved aside, so the deposit is searched again");
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(search)!, "04_search.interrupted-*"), Has.Length.EqualTo(1));
        Assert.That(runner.Settled("PXD000217"), Is.Empty);
        Assert.That(runner.Retriable("PXD000217"), Is.True);

        Assert.That(runner.InterruptedByRuntime("PXD000217", before, root), Is.True);
        Assert.That(runner.Settled("PXD000217"), Is.Empty, "second interruption: still retried");
        Assert.That(runner.InterruptedByRuntime("PXD000217", before, root), Is.True);
        Assert.That(runner.Settled("PXD000217"), Is.EqualTo("search_failed"), "a third: settled like any failed search");
    }

    [Test]
    public void APrivateRuntimeHostsTheDllAndAMissingOneIsRefused()
    {
        string dir = TestSupport.TempDir(), root = Path.Combine(dir, "dotnet-10.0.10");
        var mm = new PXReprise.Search.MetaMorpheusRunner("C:/mm/CMD.dll", "dotnet", root);
        Assert.That(mm.Launch[0], Is.EqualTo(PXReprise.Search.DotnetRuntimes.Host(root)));
        Assert.That(new PXReprise.Search.MetaMorpheusRunner("C:/mm/CMD.dll", "dotnet").Launch[0], Is.EqualTo("dotnet"));

        string machine = $"work_root = '{dir}'\ndotnet_root = '{root}'\n[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n";
        Assert.That(() => Machine.Load(TestSupport.WriteFile(dir, "machine.toml", machine)),
            Throws.InstanceOf<ConfigException>().With.Message.Contains("dotnet_root"));
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.10"));
        Assert.That(Machine.Load(TestSupport.WriteFile(dir, "machine.toml", machine)).DotnetRoot, Is.EqualTo(root));
    }

    // G4 (aging S64): PXD052189, 12 label-free runs and 20 TMT fractions, and a text that never says TMT.
    [Test]
    public void SpectraWithReporterIonsRouteTheDepositAsIsobaricOrDeferAMixedOne()
    {
        var (runner, _, _, _) = Runner();
        JsonObject Qc(params (string File, bool Tmt)[] files)
        {
            var o = new JsonObject();
            foreach (var (f, tmt) in files)
                o[f] = tmt
                    ? new JsonObject { ["pass"] = false, ["fail_reasons"] = new JsonArray("isobaric_reporters"), ["isobaric_reporters"] = new JsonObject { ["tag"] = "TMT", ["fraction"] = 0.9 } }
                    : new JsonObject { ["pass"] = true, ["fail_reasons"] = new JsonArray() };
            return o;
        }
        Assert.That(runner.RoutedByReporters("PXD000214", Qc(("a.raw", false), ("b.raw", false)), "PROBE"), Is.False);

        Assert.That(runner.RoutedByReporters("PXD000215", Qc(("a.raw", true), ("b.raw", true)), "PROBE"), Is.True);
        Assert.That(runner.Settled("PXD000215"), Is.EqualTo("waiting_tmt_dda_1"), "the screen's route for a TMT deposit");

        Assert.That(runner.RoutedByReporters("PXD000216", Qc(("lf1.raw", false), ("tmt1.raw", true), ("tmt2.raw", true)), "QC(full)"), Is.True);
        Assert.That(runner.Settled("PXD000216"), Is.EqualTo("deferred_mixed_labelling"));
        var e = runner.State()["datasets"]!["PXD000216"]!;
        Assert.That(e["isobaric_files"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "tmt1.raw", "tmt2.raw" }));
        Assert.That(e["isobaric_tag"]!.GetValue<string>(), Is.EqualTo("TMT"));
    }

    [Test]
    public void HeldAndPreviouslySettledDepositsAreNotRetried()
    {
        var (runner, _, _, _) = Runner("[holds]\nPXD000205 = \"waits for the user\"\n");
        Assert.That(runner.Settled("PXD000205"), Is.EqualTo("on_hold_user"));
        // The statuses the aging batch already wrote are honoured as they are.
        foreach (string s in new[] { "excluded_labelled", "excluded_dia", "deferred_search_too_large", "skipped_acquisition_full", "fetch_failed", "probe_fetch_failed" })
        {
            runner.Record("PXD000206", ("status", s));
            Assert.That(runner.Settled("PXD000206"), Is.EqualTo(s));
        }
        runner.Record("PXD000206", ("status", "searching"));
        Assert.That(runner.Settled("PXD000206"), Is.Empty);
    }

    [Test]
    public void TheManifestEntryCountsFilesFromQcNotFromDeletedRawsAndIsIdempotent()
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD000207");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>()), ("b.raw", true, Array.Empty<string>()), ("c.raw", false, new[] { "too_few_ms2" })).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.11"}},"extra_databases":["F:/db/iso.xml"]}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\ndatasets:\n");
        var state = new JsonObject { ["qc_excluded"] = new JsonArray("c.raw"), ["enrichment"] = "immunoprecipitation", ["screen_evidence"] = "HA \"tag\" IP" };
        var e = new QueueEntry("PXD000207", "A \"quoted\" title", "human");
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir);
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir);
        string text = File.ReadAllText(manifest);
        Assert.That(text.Split("accession: PXD000207").Length - 1, Is.EqualTo(1));
        Assert.That(text, Does.Contain("files: 2").And.Contain("organism: NCBITaxon:9606").And.Contain("run: runs/PXD000207")
            .And.Contain("title: \"A \\u0022quoted\\u0022 title\"").Or.Contain("title: \"A \\\"quoted\\\" title\""));
        Assert.That(text, Does.Contain("flags: [no_design_file, no_output_sdrf, enriched, qc_excluded_files]").And.Contain("iso.xml"));
    }

    [Test]
    public void ANewQuestionGetsAManifestWhoseRunsAreRelativeToItsOwnWorkRoot()
    {
        string dir = TestSupport.TempDir(), runRoot = Path.Combine(dir, "work", "runs"), run = Path.Combine(runRoot, "PXD000210");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.11"}}}""");
        string manifest = Path.Combine(dir, "work", "manifest.yaml");

        ManifestEntry.EnsureExists(manifest, "first-run", runRoot);
        // The machine's work root is somewhere else entirely: the manifest's own root decides the entry's `run`.
        ManifestEntry.Append(manifest, new QueueEntry("PXD000210", "t", "human"), Profiles["label-free-dda@1"], run, null, "Z:/elsewhere");
        string text = File.ReadAllText(manifest).ReplaceLineEndings("\n");
        Assert.That(text, Does.Contain("instance: first-run\n").And.Contain("work_root: runs\n").And.Contain("store: store\n")
            .And.Contain("run: PXD000210\n").And.Contain("files: 1\n"));
        Assert.That(ManifestEntry.WorkRootOf(manifest), Is.EqualTo(Path.GetFullPath(runRoot)));

        // An absolute root, as aging's hand-written manifest has (F:/aging_data there; absolute on every OS here).
        string absolute = Path.GetFullPath(Path.Combine(dir, "aging_data")).Replace('\\', '/');
        File.WriteAllText(manifest, $"manifest_version: 1\nwork_root: {absolute}   # by hand\ndatasets:\n");
        ManifestEntry.EnsureExists(manifest, "first-run", runRoot);
        Assert.That(File.ReadAllText(manifest), Does.StartWith($"manifest_version: 1\nwork_root: {absolute}"), "an existing manifest is never rewritten");
        Assert.That(ManifestEntry.WorkRootOf(manifest), Is.EqualTo(Path.GetFullPath(absolute)));
    }

    [Test]
    public void APublishCommandNamesNoMachinePath()
    {
        var argv = BatchRunner.PublishArgv(new[] { "{datarepo}", "publish", "{manifest}", "--site", "{manifest_dir}/site" },
            Path.Combine("C:", "q", "work", "manifest.yaml"), "D:/tools/datarepo/datarepo.exe");
        Assert.That(argv, Is.EqualTo(new[] { "D:/tools/datarepo/datarepo.exe", "publish", Path.Combine("C:", "q", "work", "manifest.yaml"),
            "--site", Path.GetFullPath(Path.Combine("C:", "q", "work")).Replace('\\', '/') + "/site" }));
    }

    [Test]
    public void AFailedIngestIsNotDeliveredSoTheNextStartRetriesIt()
    {
        const string manifest = "manifest_version: 1\ndatasets:\n\n  - accession: PXD075372\n    status: include\n";
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject { ["ingest_rc"] = 1 }), Is.False, "the entry is appended before ingest runs");
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject { ["ingest_rc"] = 0 }), Is.True);
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject()), Is.True, "no ingest_rc: delivered elsewhere, or no datarepo here");
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", null), Is.True);
        Assert.That(BatchRunner.Delivered(manifest.Replace("\n", "\r\n"), "PXD075372", new JsonObject { ["ingest_rc"] = 0 }), Is.True);
        Assert.That(BatchRunner.Delivered(manifest, "PXD07537", new JsonObject { ["ingest_rc"] = 0 }), Is.False, "a prefix is not the accession");
        Assert.That(BatchRunner.Delivered(manifest, "PXD000001", new JsonObject { ["ingest_rc"] = 0 }), Is.False);
    }

    // G7. A deposit whose downloads PRIDE kept dropping is not the deposit's fault: it waits for a later pass, and only
    // settles after fetch_passes of them. The dropped transfer is the type .NET really throws (HttpIOException), not a
    // fake HttpRequestException(503): that fake is how the 09-29 retry defect survived its tests.
    private static (BatchRunner Runner, FakeFiles Files, string Dir, QueueEntry Entry) Droppy(int passes, Func<Exception> failure)
    {
        var (runner, search, files, dir) = Runner(extraMachine: $"fetch_attempts = 1\nfetch_passes = {passes}\npass_wait_minutes = 0\n");
        search.Results["PXD000208"] = new() { TestSupport.Record("PXD000208", "Type 2 diabetes muscle") };
        files.Listing = Enumerable.Range(0, 5).Select(i => new PrideArchiveFile { FileName = $"h{i}.raw", FileSizeBytes = 500_000_000 }).ToList();
        files.Failure = failure;
        return (runner, files, dir, new QueueEntry("PXD000208", "t", "human"));
    }

    [Test]
    public async Task ADownloadThatOutlastsItsAttemptsIsRetriedOnLaterPassesThenSettled()
    {
        var (runner, files, dir, e) = Droppy(3, () => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        await runner.RunAsync(new[] { e }, CancellationToken.None);
        var ds = runner.State()["datasets"]!["PXD000208"]!;
        Assert.That(ds["status"]!.GetValue<string>(), Is.EqualTo("probe_fetch_failed"));
        Assert.That(ds["unavailable_passes"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(files.Downloads, Is.EqualTo(3 * 3), "three probe files, one attempt each, on each of three passes");
        string log = File.ReadAllText(Path.Combine(dir, "state", "batch.log"));
        Assert.That(log, Does.Contain("pass 1 done").And.Contain("pass 2 done").And.Contain("retry next pass").And.Contain("pass 3 of 3: settled"));
    }

    [Test]
    public async Task AContractBreakSettlesAtOnceWithoutAnotherPass()
    {
        var (runner, files, dir, e) = Droppy(3, () => new MzLibUtil.MzLibException("h0.raw: checksum mismatch"));
        await runner.RunAsync(new[] { e }, CancellationToken.None);
        var ds = runner.State()["datasets"]!["PXD000208"]!;
        Assert.That(ds["status"]!.GetValue<string>(), Is.EqualTo("probe_fetch_failed"));
        Assert.That(ds["unavailable_passes"], Is.Null);
        Assert.That(files.Downloads, Is.EqualTo(3), "one pass only");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Not.Contain("pass 1 done"));
    }

    [Test]
    public void AnUnavailableWholeFetchIsNeitherSettledNorForgotten()
    {
        var (runner, _, _, _) = Runner(extraMachine: "fetch_passes = 2\n");
        var dropped = new HttpRequestException("h0.raw: 8 attempts all failed; last error: The response ended prematurely.",
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        runner.RecordFetchFailure("PXD000209", probe: false, dropped);
        Assert.That(runner.State()["datasets"]!["PXD000209"]!["status"]!.GetValue<string>(), Is.EqualTo("fetch_unavailable"));
        Assert.That(runner.Settled("PXD000209"), Is.Empty);
        Assert.That(runner.Retriable("PXD000209"), Is.True);
        runner.RecordFetchFailure("PXD000209", probe: false, dropped);
        Assert.That(runner.Settled("PXD000209"), Is.EqualTo("fetch_failed"));
        Assert.That(runner.Retriable("PXD000209"), Is.False);
        Assert.That(runner.State()["datasets"]!["PXD000209"]!["detail"]!.GetValue<string>(), Does.StartWith("HttpRequestException: h0.raw"));
    }

    [Test]
    public void BatchRetryRequeuesASettledDepositAndRecordsWhy()   // first-run 2026-10-03: un-settling meant editing state.json
    {
        var (runner, _, _, dir) = Runner(extraMachine: "fetch_passes = 1\n");
        runner.RecordFetchFailure("PXD000209", probe: false, new IOException("Received an unexpected EOF") { Source = "System.Net.Security" });
        Assert.That(runner.Settled("PXD000209"), Is.EqualTo("fetch_failed"));
        string state = Path.Combine(dir, "state"), runs = Path.Combine(dir, "runs");

        BatchRunner.Requeue(state, runs, "PXD000209", "TLS drop, fixed in v0.3.3");
        var e = runner.State()["datasets"]!["PXD000209"]!;
        Assert.That(runner.Settled("PXD000209"), Is.Empty);
        Assert.That(e["status"]!.GetValue<string>(), Is.EqualTo("requeued_user"));
        Assert.That(e["unavailable_passes"], Is.Null);
        var h = e["requeued"]![0]!;
        Assert.That((h["previous_status"]!.GetValue<string>(), h["reason"]!.GetValue<string>()), Is.EqualTo(("fetch_failed", "TLS drop, fixed in v0.3.3")));
        Assert.That(File.ReadAllText(Path.Combine(state, "batch.log")), Does.Contain("PXD000209 REQUEUED by").And.Contain("(was fetch_failed)"));

        Assert.Throws<PXReprise.Cli.UsageException>(() => BatchRunner.Requeue(state, runs, "PXD999999", "x"), "not in the state");
        Assert.Throws<PXReprise.Cli.UsageException>(() => BatchRunner.Requeue(state, runs, "PXD000209", " "), "a reason is required");
        Directory.CreateDirectory(Path.Combine(runs, "PXD000209", "04_search"));
        File.WriteAllText(Path.Combine(runs, "PXD000209", "04_search", "provenance.json"), "{}");
        Assert.That(() => BatchRunner.Requeue(state, runs, "PXD000209", "x"), Throws.TypeOf<PXReprise.Cli.UsageException>().With.Message.Contains("finished search"));
    }

    private sealed class FakeFiles : IPrideFiles
    {
        public List<PrideArchiveFile> Listing { get; set; } = new();
        public Func<Exception> Failure { get; set; } = () => new InvalidOperationException("no downloads in this test");
        public int Downloads;
        public Task<List<PrideArchiveFile>> ListFilesAsync(string a, CancellationToken ct) => Task.FromResult(Listing);
        public Task<List<string>> ListFtpNamesAsync(string a, CancellationToken ct) => Task.FromResult(Listing.Select(f => f.FileName).ToList());
        public Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct) { Interlocked.Increment(ref Downloads); throw Failure(); }
    }
}
