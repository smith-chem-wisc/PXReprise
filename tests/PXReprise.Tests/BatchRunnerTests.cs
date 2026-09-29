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
