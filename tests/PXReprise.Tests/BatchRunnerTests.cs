using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Fetch;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

public class BatchRunnerTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
    private static QcGates Gates => Profiles["label-free-dda@1"].Qc;

    /// <summary>No PRIDE SHA-1s in offline tests (the error-text scan still runs).</summary>
    private static Task<IReadOnlyDictionary<string, string>> NoSha1(string acc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

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

    // PXD057407, 2026-10-12: five phospho runs from the 10-08 attempt lay beside the ten global runs the question now
    // selects (PXR-A68). The fetch counted "15 of 10" and failed; had the count passed, QC and the search would have taken
    // all fifteen. A whole-deposit fetch now sets aside what its selection does not name.
    [Test]
    public void AWholeFetchSetsAsideRawFilesItsSelectionDoesNotName()
    {
        string run = TestSupport.TempDir(), spectra = Path.Combine(run, "02_fetch", "spectra");
        Directory.CreateDirectory(spectra);
        foreach (string f in new[] { "Global_1.raw", "Global_2.raw", "Global_2.raw.partial", "Phospho_1.raw", "Phospho_2.RAW",
                                     "Phospho_2.RAW.partial", "Phospho_2.RAW.partial.validator", "Phospho_3.raw.partial", "notes.txt" })
            File.WriteAllText(Path.Combine(spectra, f), f);

        var moved = BatchRunner.SetAsideUnselected(spectra, new[] { "Global_1.raw", "Global_2.raw" });

        string aside = Path.Combine(run, "02_fetch", BatchRunner.NotSelectedFolder);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.EqualTo(new[] { "Phospho_1.raw", "Phospho_2.RAW" }), "the .raw names moved, for the log");
            Assert.That(Directory.EnumerateFiles(spectra).Select(Path.GetFileName).Order(StringComparer.Ordinal),
                Is.EqualTo(new[] { "Global_1.raw", "Global_2.raw", "Global_2.raw.partial", "notes.txt" }),
                "the selection stays, with its own partial; anything that is not a raw file is not ours to move");
            Assert.That(Directory.EnumerateFiles(aside).Select(Path.GetFileName).Order(StringComparer.Ordinal),
                Is.EqualTo(new[] { "Phospho_1.raw", "Phospho_2.RAW", "Phospho_2.RAW.partial", "Phospho_2.RAW.partial.validator", "Phospho_3.raw.partial" }));
            Assert.That(BatchRunner.SetAsideUnselected(spectra, new[] { "Global_1.raw", "Global_2.raw" }), Is.Empty, "a second pass moves nothing");
        });
    }

    [Test]
    public void CleanupDeletesSetAsideFilesAndStalePartialsButKeepsResults()
    {
        string run = TestSupport.TempDir();
        string spectra = Path.Combine(run, "02_fetch", "spectra"), aside = Path.Combine(run, "02_fetch", BatchRunner.NotSelectedFolder);
        Directory.CreateDirectory(spectra);
        Directory.CreateDirectory(aside);
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), "{\"success\": true}");
        File.WriteAllText(Path.Combine(run, "04_search", "results.txt"), "kept");
        foreach (string f in new[] { "a.raw", "a.raw.partial", "a.raw.partial.validator" }) File.WriteAllText(Path.Combine(spectra, f), f);
        foreach (string f in new[] { "p.raw", "p.raw.partial" }) File.WriteAllText(Path.Combine(aside, f), f);

        var r = CleanupStage.Run(run, run);

        Assert.Multiple(() =>
        {
            Assert.That(r["files"]!.GetValue<int>(), Is.EqualTo(5));
            Assert.That(Directory.EnumerateFiles(spectra), Is.Empty);
            Assert.That(Directory.EnumerateFiles(aside), Is.Empty);
            Assert.That(File.Exists(Path.Combine(run, "04_search", "results.txt")), Is.True);
        });
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
        return (new BatchRunner(question, Profiles, machine, files, search, "2026-09-28") { Sha1Source = NoSha1 }, search, files, dir);
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

    // D28 (the user's D-2; aging 044): a question releases a deferred deposit by exception, with its own caps or its own
    // choice of runs. The deposit gets ONE more look; if it still does not fit, it settles again instead of looping.
    [Test]
    public async Task AQuestionExceptionReleasesADeferredDepositOnce()
    {
        var (runner, search, files, dir) = Runner();
        search.Results["PXD000203"] = new() { TestSupport.Record("PXD000203", "Type 2 diabetes muscle") };
        files.Listing = Enumerable.Range(0, 61).Select(i => new PrideArchiveFile { FileName = i < 40 ? $"hippo{i}.raw" : $"cortex{i}.raw", FileSizeBytes = 500_000_000 }).ToList();
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000203", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000203"), Is.EqualTo("deferred_needs_design_selection"));

        // The same state, read by a question that now names an exception.
        string q = TestSupport.MinimalQuestion + "\n[batch]\nrun_root = \"runs\"\nstate_dir = \"state\"\nqueue = \"queue.json\"\n"
            + "[exceptions.PXD000203]\nreason = \"the hippocampus runs only\"\ninclude_files = \"^hippo\"\n";
        var withEx = new BatchRunner(QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml", q)), Profiles,
            Machine.Load(Path.Combine(dir, "machine.toml")), files, search, "2026-09-28");
        Assert.That(withEx.Settled("PXD000203"), Is.Empty, "an exception added after the deferral earns one more look");
        Assert.That(await withEx.ProbeAsync(new QueueEntry("PXD000203", "t", "human"), CancellationToken.None), Is.Null);
        var e = withEx.State()["datasets"]!["PXD000203"]!;
        Assert.That(e["exception"]!.GetValue<string>(), Does.Contain("include_files /^hippo/").And.Contain("the hippocampus runs only"));
        Assert.That(e["status"]!.GetValue<string>(), Is.Not.EqualTo("deferred_needs_design_selection"), "40 runs fit the 60-file cap: " + e.ToJsonString());
        Assert.That(files.Downloads, Is.GreaterThan(0), "the probe went ahead: " + e.ToJsonString());

        string q2 = TestSupport.MinimalQuestion + "\n[batch]\nrun_root = \"runs\"\nstate_dir = \"state\"\nqueue = \"queue.json\"\n"
            + "[exceptions.PXD000204]\nreason = \"still too many\"\nmax_files = 5\n";
        var tight = new BatchRunner(QuestionLoader.Load(TestSupport.WriteFile(dir, "question2.toml", q2)), Profiles,
            Machine.Load(Path.Combine(dir, "machine.toml")), files, search, "2026-09-28");
        search.Results["PXD000204"] = new() { TestSupport.Record("PXD000204", "Type 2 diabetes liver") };
        files.Listing = Enumerable.Range(0, 10).Select(i => new PrideArchiveFile { FileName = $"g{i}.raw", FileSizeBytes = 500_000_000 }).ToList();
        Assert.That(await tight.ProbeAsync(new QueueEntry("PXD000204", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(tight.Settled("PXD000204"), Is.EqualTo("deferred_needs_design_selection"), "settled again under the exception, no loop");
    }

    [Test]
    public void AnExceptionMustNameItsReasonAndChangeSomething()
    {
        string dir = TestSupport.TempDir();
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "a.toml", TestSupport.MinimalQuestion + "\n[exceptions.PXD000203]\nmax_files = 90\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "b.toml", TestSupport.MinimalQuestion + "\n[exceptions.PXD000203]\nreason = \"x\"\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "c.toml", TestSupport.MinimalQuestion + "\n[exceptions.PXD000203]\nreason = \"x\"\nexclude_files = \"(\"\n")));
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "d.toml", TestSupport.MinimalQuestion + "\n[exceptions.PXD000203]\nreason = \"x\"\nexclude_files = \"atg5ko\"\nmax_est_ms2 = 0\n"));
        var ex = q.ExceptionFor("PXD000203")!;
        Assert.That((ex.Keeps("P25_vesicle_1.raw"), ex.Keeps("ATG5KO_1.raw")), Is.EqualTo((true, false)), "case-insensitive");
        Assert.That(ex.Apply(Profiles["label-free-dda@4"].Deposit).MaxEstimatedMs2, Is.Zero);
    }

    // v0.4.15 (PXR-A99, aging 098): PXD015239's protocol has no reduction or alkylation step.
    [Test]
    public void AnAlkylationExceptionSaysNoneAndNothingElse()
    {
        string dir = TestSupport.TempDir();
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "a.toml", TestSupport.MinimalQuestion
            + "\n[exceptions.PXD015239]\nreason = \"no reduction or alkylation step (PXR-A99)\"\nalkylation = \"none\"\n"));
        var ex = q.ExceptionFor("PXD015239")!;
        Assert.That((ex.Alkylation, ex.SelectsFiles, ex.Describe()),
            Is.EqualTo(("none", false, "alkylation none (no reduction or alkylation step (PXR-A99))")));
        var bad = Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "b.toml", TestSupport.MinimalQuestion
            + "\n[exceptions.PXD015239]\nreason = \"x\"\nalkylation = \"iodoacetamide\"\n")));
        Assert.That(bad!.Message, Does.Contain("curated SDRF"));

        // It outranks what was read: PRIDE's carbamidomethyl, and a wait on an unmodelled thiol reagent; a protease wait stays.
        var read = new DepositChemistry(new("trypsin", ChemistrySource.Default), null, new("Carbamidomethyl", ChemistrySource.PridePtm),
            new[] { new CysMod("Carbamidomethyl", "UNIMOD:4", true) }, new("label_free", ChemistrySource.Default), "waiting_unknown_alkylant");
        var none = read.Unalkylated(ex.Reason);
        Assert.That((none.Alkylation.Value, none.Alkylation.Source, none.CysMods.Count, none.Park, none.AnyGuessed),
            Is.EqualTo(("none", ChemistrySource.QuestionException, 0, (string?)null, false)));
        Assert.That((read with { Park = "waiting_multi_protease" }).Unalkylated("x").Park, Is.EqualTo("waiting_multi_protease"));
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

    // v0.4.16: PXD011740's re-search (PXR-A103) was refused as a duplicate of PXD012985, which the manifest already excludes
    // as PXD011740's own copy (PXR-A8). The copy holds the original's files because it IS the original deposited again.
    [Test]
    public void ACopyTheManifestExcludesIsNoOriginalForTheDepositItCopies()
    {
        const string manifest = "deposits:\r\n"
            + "  - accession: PXD012985\r\n    status: exclude\r\n    reason: >\r\n      duplicate_of: PXD011740. EXCLUDED 2026-10-02 (aging 015/017, PXR-A8)\r\n"
            + "    title: \"Translational regulation\"\r\n"
            + "  - accession: PXD011740\n    status: include\n    title: \"Translational regulation\"\n"
            + "  - accession: PXD000572\n    status: exclude\n    reason: >\n      dimethyl-labelled, searched label-free\n";
        Assert.That(ManifestEntry.ExcludedCopiesOf(manifest, "PXD011740"), Is.EquivalentTo(new[] { "PXD012985" }));
        Assert.That(ManifestEntry.ExcludedCopiesOf(manifest, "PXD012985"), Is.Empty, "the original is no copy of its copy");
        Assert.That(ManifestEntry.ExcludedCopiesOf(manifest.Replace("status: exclude\r\n    reason", "status: include\r\n    reason"), "PXD011740"), Is.Empty);

        var raws = new[] { ("sample1.raw", 772102128L), ("sample1sec.raw", 783929316L) };
        var index = new[] { new DuplicateScreen.Searched("PXD012985", raws.ToHashSet()) };
        Assert.That(DuplicateScreen.Check("PXD011740", raws, index).DuplicateOf, Is.EqualTo("PXD012985"), "before v0.4.16");
        Assert.That(DuplicateScreen.Check("PXD011740", raws, index, ManifestEntry.ExcludedCopiesOf(manifest, "PXD011740")).DuplicateOf, Is.Null);
        Assert.That(DuplicateScreen.Check("PXD099999", raws, index, ManifestEntry.ExcludedCopiesOf(manifest, "PXD099999")).DuplicateOf,
            Is.EqualTo("PXD012985"), "a third deposit with the same files is still a duplicate");
    }

    // PXR-A21: PXD042302 (PXD042301 deposited again) was screened 7 s after PXD042301's search started, with no search
    // provenance yet, and was fetched and searched in full.
    [Test]
    public async Task ADuplicateOfTheDepositBeingSearchedNowIsRefusedToo()
    {
        var (runner, search, files, dir) = Runner();
        string inFlight = Path.Combine(dir, "runs", "PXD000221");
        Directory.CreateDirectory(Path.Combine(inFlight, "02_fetch"));
        File.WriteAllText(Path.Combine(inFlight, "02_fetch", "fetch_manifest.json"), """
            {"accession":"PXD000221","files":[{"name":"a.raw","pride_size_bytes":1293721889},{"name":"b.raw","pride_size_bytes":1410000000}]}
            """);
        search.Results["PXD000222"] = new() { TestSupport.Record("PXD000222", "Type 2 diabetes podocytes") };
        files.Listing = new() { new() { FileName = "a.raw", FileSizeBytes = 1293721889 }, new() { FileName = "b.raw", FileSizeBytes = 1410000000 } };

        await runner.ProbeAsync(new QueueEntry("PXD000222", "t", "human"), CancellationToken.None);
        Assert.That(runner.Settled("PXD000222"), Is.Not.EqualTo("excluded_duplicate"), "fetched but neither searched nor in flight: not evidence");

        runner.Record("PXD000222", ("status", "probing"));
        runner.SearchStarted("PXD000221");
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000222", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000222"), Is.EqualTo("excluded_duplicate"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("are in PXD000221, being searched now"));
    }

    // The first-run on the other computer: PXD058082 read "searched" beside a TLS error from a pass that had since succeeded.
    [Test]
    public void ANewStatusRetiresTheOldDetailButKeepsIt()
    {
        var (runner, _, _, _) = Runner();
        runner.Record("PXD000223", ("status", "fetch_unavailable"), ("detail", "HttpIOException: TLS EOF"));
        runner.Record("PXD000223", ("n_raw", 3));   // no status: nothing retired
        Assert.That(runner.State()["datasets"]!["PXD000223"]!["detail"]!.GetValue<string>(), Is.EqualTo("HttpIOException: TLS EOF"));
        runner.Record("PXD000223", ("status", "searched"));
        var e = runner.State()["datasets"]!["PXD000223"]!;
        Assert.That(e["detail"], Is.Null);
        Assert.That(e["earlier_detail"]!.GetValue<string>(), Is.EqualTo("HttpIOException: TLS EOF"));
        runner.Record("PXD000223", ("status", "search_failed"), ("detail", "boom"));
        Assert.That(runner.State()["datasets"]!["PXD000223"]!["detail"]!.GetValue<string>(), Is.EqualTo("boom"));
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

    // G19 (D22-D27): under a profile that reads each deposit's chemistry, the probe reads it before any download, records
    // it, and holds back a deposit it cannot search as read.
    // The user, 2026-10-07: "start a second search immediately following any complete download", "just try to alternate
    // species" (two searches of one species cannot both extend its spectral library).
    [Test]
    public async Task WithTwoSlotsADownloadedDepositStartsAtOnceAndAnotherSpeciesGoesFirst()
    {
        var (runner, _, _, _) = Runner(extraMachine: "search_slots = 2\n");
        var started = new List<string>();
        int running = 0, most = 0;
        runner.PrepareForTest = (e, ct) => { runner.Record(e.Accession, ("status", "fetched")); return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]); };
        runner.SearchForTest = async (e, p, ct) =>
        {
            lock (started) { started.Add(e.Accession); most = Math.Max(most, ++running); }
            await Task.Delay(300, ct);
            lock (started) running--;
        };
        var queue = new[]
        {
            new QueueEntry("PXD000901", "a", "mouse"), new QueueEntry("PXD000902", "b", "mouse"),
            new QueueEntry("PXD000903", "c", "human"), new QueueEntry("PXD000904", "d", "mouse"),
        };

        await runner.RunAsync(queue, CancellationToken.None);

        Assert.That(most, Is.EqualTo(2), "two searches ran at once");
        Assert.That(started, Is.EqualTo(new[] { "PXD000901", "PXD000903", "PXD000902", "PXD000904" }),
            "the human deposit jumps the second mouse one while a mouse search runs; then queue order");
        Assert.That(runner.Searching, Is.Empty);
    }

    [Test]
    public async Task WithOneSlotSearchesRunOneAtATimeInQueueOrder()
    {
        var (runner, _, _, _) = Runner();
        var started = new List<string>();
        int running = 0, most = 0;
        runner.PrepareForTest = (e, ct) => { runner.Record(e.Accession, ("status", "fetched")); return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]); };
        runner.SearchForTest = async (e, p, ct) =>
        {
            lock (started) { started.Add(e.Accession); most = Math.Max(most, ++running); }
            await Task.Delay(50, ct);
            lock (started) running--;
        };
        await runner.RunAsync(new[] { new QueueEntry("PXD000911", "a", "mouse"), new QueueEntry("PXD000912", "b", "human") }, CancellationToken.None);
        Assert.That(most, Is.EqualTo(1));
        Assert.That(started, Is.EqualTo(new[] { "PXD000911", "PXD000912" }));
    }

    // v0.4.10 (the user, 2026-10-12: "I don't know why we have to stop downloads"): STOP starts no search, but the
    // downloads go on while the searches under way finish; the next driver searches what was fetched ahead.
    // Two slots, so the first search is under way while the second deposit downloads; STOP is written during that download
    // (as on 2026-10-12), which leaves it fetched and starts the drain.
    [Test]
    public async Task StopStartsNoSearchButDownloadsAheadWhileTheSearchUnderWayFinishes()
    {
        var (runner, _, _, dir) = Runner(extraMachine: "search_slots = 2\n");
        var prepared = new List<string>();
        var searched = new List<string>();
        runner.PrepareForTest = (e, ct) =>
        {
            lock (prepared) prepared.Add(e.Accession);
            if (e.Accession == "PXD000932") File.WriteAllText(Path.Combine(dir, "state", "STOP"), "hold for a pin swap");
            runner.Record(e.Accession, ("status", "fetched"));
            return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]);
        };
        runner.SearchForTest = async (e, p, ct) =>
        {
            lock (searched) searched.Add(e.Accession);
            for (int i = 0; i < 400 && prepared.Count < 4; i++) await Task.Delay(25, ct);
        };
        var queue = new[]
        {
            new QueueEntry("PXD000931", "a", "mouse"), new QueueEntry("PXD000932", "b", "human"),
            new QueueEntry("PXD000933", "c", "rat"), new QueueEntry("PXD000934", "d", "mouse"),
        };

        await runner.RunAsync(queue, CancellationToken.None);

        Assert.That(searched, Is.EqualTo(new[] { "PXD000931" }), "no search starts under STOP");
        Assert.That(prepared, Is.EqualTo(new[] { "PXD000931", "PXD000932", "PXD000933", "PXD000934" }), "the next ones are downloaded meanwhile");
        Assert.That(runner.State()["datasets"]!["PXD000934"]!["status"]!.GetValue<string>(), Is.EqualTo("fetched"));
        Assert.That(runner.Settled("PXD000934"), Is.Empty, "the next driver takes it up");
        string log = File.ReadAllText(Path.Combine(dir, "state", "batch.log"));
        Assert.That(log, Does.Contain("PXD000932 fetched, but STOP is present: not searched by this driver"));
        Assert.That(log, Does.Contain("downloaded ahead for the next driver: 2 deposit(s) (PXD000933, PXD000934)"));
    }

    [Test]
    public async Task TheDownloadAheadIsCutWhenTheLastSearchEnds()
    {
        var (runner, _, _, dir) = Runner(extraMachine: "search_slots = 2\n");
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.PrepareForTest = async (e, ct) =>
        {
            if (e.Accession == "PXD000942") File.WriteAllText(Path.Combine(dir, "state", "STOP"), "hold");
            if (e.Accession == "PXD000943")
            {
                downloading.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);   // a long download, cut by the drain's end
            }
            runner.Record(e.Accession, ("status", "fetched"));
            return Profiles["label-free-dda@1"];
        };
        runner.SearchForTest = async (e, p, ct) => await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var queue = new[] { new QueueEntry("PXD000941", "a", "mouse"), new QueueEntry("PXD000942", "b", "human"), new QueueEntry("PXD000943", "c", "rat") };
        var run = runner.RunAsync(queue, CancellationToken.None);

        Assert.That(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))), Is.SameAs(run), "the driver exits once the search ends");
        await run;
        Assert.That(runner.State()["datasets"]!["PXD000943"]!["status"]!.GetValue<string>(), Is.EqualTo("probing"));
        Assert.That(runner.Settled("PXD000943"), Is.Empty, "a cut download is not a failure");
        string log = File.ReadAllText(Path.Combine(dir, "state", "batch.log"));
        Assert.That(log, Does.Contain("the download ahead is cut"));
        Assert.That(log, Does.Contain("downloaded ahead for the next driver: none"));
    }

    // PXD008934 (2026-10-12) finished downloading 85 min after STOP, then searched for 6 h while the pin swap waited.
    [Test]
    public async Task ADepositFetchedAfterStopIsLeftForTheNextDriver()
    {
        var (runner, _, _, dir) = Runner();
        var searched = new List<string>();
        runner.PrepareForTest = (e, ct) =>
        {
            if (e.Accession == "PXD000952") File.WriteAllText(Path.Combine(dir, "state", "STOP"), "hold");
            runner.Record(e.Accession, ("status", "fetched"));
            return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]);
        };
        runner.SearchForTest = async (e, p, ct) => { lock (searched) searched.Add(e.Accession); await Task.Delay(50, ct); };

        await runner.RunAsync(new[] { new QueueEntry("PXD000951", "a", "mouse"), new QueueEntry("PXD000952", "b", "human") }, CancellationToken.None);

        Assert.That(searched, Is.EqualTo(new[] { "PXD000951" }));
        Assert.That(runner.State()["datasets"]!["PXD000952"]!["status"]!.GetValue<string>(), Is.EqualTo("fetched"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")),
            Does.Contain("PXD000952 fetched, but STOP is present: not searched by this driver"));
    }

    // v0.4.12: after the v0.4.11 swap (2026-10-13) three deposits downloaded ahead under STOP sat while the driver fetched
    // PXD009212 for about 2 h. A deposit with every selected file on disk goes first, another species before its own.
    [Test]
    public async Task ADepositAlreadyDownloadedIsSearchedBeforeTheQueueOrder()
    {
        var (runner, _, _, dir) = Runner();
        void OnDisk(string acc, int listed, int present, string status)
        {
            runner.Record(acc, ("status", status), ("n_listed", listed));
            string spectra = Path.Combine(dir, "runs", acc, "02_fetch", "spectra");
            Directory.CreateDirectory(spectra);
            for (int i = 1; i <= present; i++) File.WriteAllText(Path.Combine(spectra, $"r{i}.raw"), "x");
        }
        OnDisk("PXD000972", 2, 2, "fetched");                // downloaded ahead under STOP
        OnDisk("PXD000973", 3, 3, "probe_search_failed");    // PXD026335's case: whole on disk, its probe timed out
        OnDisk("PXD000974", 4, 2, "probing");                // part-fetched (PXD022196's case): queue order
        var searched = new List<string>();
        runner.PrepareForTest = (e, ct) => { runner.Record(e.Accession, ("status", "fetched")); return Task.FromResult<Profile?>(Profiles["label-free-dda@1"]); };
        runner.SearchForTest = async (e, p, ct) => { lock (searched) searched.Add(e.Accession); await Task.Delay(20, ct); };

        await runner.RunAsync(new[]
        {
            new QueueEntry("PXD000971", "a", "mouse"), new QueueEntry("PXD000972", "b", "mouse"),
            new QueueEntry("PXD000973", "c", "human"), new QueueEntry("PXD000974", "d", "rat"),
        }, CancellationToken.None);

        Assert.That(searched, Is.EqualTo(new[] { "PXD000972", "PXD000973", "PXD000971", "PXD000974" }));
    }

    // v0.4.12: PXD026335 (2026-10-13) had all 20 files on disk when v0.4.11 probed it, and the probe QC'd and searched them
    // all until its 60-minute limit. The probe reads the files its own fetch chose, whatever else is in the folder.
    [Test]
    public async Task TheProbeReadsItsOwnFilesWhenTheWholeDepositIsOnDisk()
    {
        var (runner, search, files, dir) = Runner();
        string acc = "PXD000981";
        search.Results[acc] = new() { TestSupport.Record(acc, "Type 2 diabetes muscle", "digested with trypsin", instruments: new[] { "Q Exactive HF" }, files: new[] { "run1.raw" }) };
        search.Projects[acc] = new PrideProject { Accession = acc, Title = "Type 2 diabetes muscle", SampleProcessingProtocol = "digested with trypsin" };
        files.Listing = Enumerable.Range(1, 6).Select(i => new PrideArchiveFile { FileName = $"run{i}.raw", FileSizeBytes = 64 }).ToList();
        string spectra = Path.Combine(dir, "runs", acc, "02_fetch", "spectra");
        Directory.CreateDirectory(spectra);
        foreach (var f in files.Listing) File.WriteAllText(Path.Combine(spectra, f.FileName), new string('x', 64));

        await runner.ProbeAsync(new QueueEntry(acc, "t", "human"), CancellationToken.None);

        Assert.That(files.Downloads, Is.Zero, "every file was on disk");
        var probeQc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "runs", acc, "02b_qc_probe", "qc_report.json")))!.AsObject();
        Assert.That(probeQc.Count, Is.EqualTo(3), "the probe's three files, not the six on disk");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain($"{acc} PROBE reads its 3 probe files only; 6 raw files are on disk"));
        Assert.That(BatchRunner.ProbeSpectra(spectra, probeQc).Select(Path.GetFileName), Is.EqualTo(probeQc.Select(kv => kv.Key).Order(StringComparer.Ordinal)));
    }

    // D39: catch-up re-searches by gain (rat, then a curated design, then oldest), another species first.
    [Test]
    public void CatchUpPicksRatFirstAndPrefersASpeciesNotRunning()
    {
        var (runner, _, _, dir) = Runner();
        foreach (var (acc, org, when) in new[] { ("PXD000921", "mouse", "1"), ("PXD000922", "rat", "3"), ("PXD000923", "human", "2"), ("PXD000924", "mouse", "0") })
        {
            runner.Record(acc, ("status", "searched"), ("profile", "label-free-dda@0"), ("organism", org), ("title", acc));
            Directory.CreateDirectory(Path.Combine(dir, "runs", acc, "04_search")); TestSupport.WriteFile(Path.Combine(dir, "runs", acc, "04_search"), "provenance.json", "{}");
        }
        // Searched under a profile the question still names: never due.
        runner.Record("PXD000925", ("status", "searched"), ("profile", "label-free-dda@1"), ("organism", "rat"));
        Directory.CreateDirectory(Path.Combine(dir, "runs", "PXD000925", "04_search")); TestSupport.WriteFile(Path.Combine(dir, "runs", "PXD000925", "04_search"), "provenance.json", "{}");
        var none = new HashSet<string>();

        Assert.That(runner.CatchUpCandidate(Array.Empty<QueueEntry>(), none, new HashSet<string>())!.Accession, Is.EqualTo("PXD000922"), "rat first");
        Assert.That(runner.CatchUpCandidate(Array.Empty<QueueEntry>(), none, new HashSet<string> { "rat" })!.Accession, Is.Not.EqualTo("PXD000922"),
            "a rat search is running: another species first");
        Assert.That(runner.CatchUpCandidate(Array.Empty<QueueEntry>(), new HashSet<string> { "PXD000922", "PXD000921", "PXD000923", "PXD000924" }, new HashSet<string>()),
            Is.Null, "nothing left once the old ones are taken");
    }

    // Ion-trap MS2 (low_res_ms2) is searched under a low-res profile when the question names one, never anything else.
    [Test]
    public void OnlyALowResFailureIsRoutedToTheLowResProfile()
    {
        var gates = Profiles["label-free-dda@5"].Qc;
        Assert.That(BatchRunner.OnlyLowRes(Report(("a.raw", false, new[] { "low_res_ms2" }), ("b.raw", false, new[] { "low_res_ms2" })), gates), Is.True);
        Assert.That(BatchRunner.OnlyLowRes(Report(("a.raw", false, new[] { "low_res_ms2" }), ("b.raw", false, new[] { "too_few_ms2" })), gates), Is.True,
            "one blank run is excludable, as anywhere else");
        Assert.That(BatchRunner.OnlyLowRes(Report(("a.raw", false, new[] { "low_res_ms2", "unreadable" })), gates), Is.False);
        Assert.That(BatchRunner.OnlyLowRes(Report(("a.raw", false, new[] { "too_few_ms2" })), gates), Is.False, "no low_res_ms2 at all");

        var lowres = Profiles["label-free-dda-lowres@1"];
        Assert.That((lowres.Qc.MinFractionOrbitrapHcd, lowres.Fragmentation, lowres.SpectralLibrary, string.Join(",", lowres.Tasks)),
            Is.EqualTo((0.0, "per_file", false, "Search")), "no Orbitrap gate, per-file LowCID, no library, Search only (the user)");
    }

    [Test]
    public void SearchSlotsAndCatchUpAreReadFromTheMachineAndChecked()
    {
        var (_, _, _, dir) = Runner();
        var m = Machine.Load(TestSupport.WriteFile(dir, "m2.toml", $"work_root = '{dir}'\nsearch_slots = 2\ncatch_up_every = 4\n[metamorpheus]\n\"1.1.11\" = 'x'\n"));
        Assert.That((m.SearchSlots, m.CatchUpEvery), Is.EqualTo((2, 4)));
        var d = Machine.Load(TestSupport.WriteFile(dir, "m3.toml", $"work_root = '{dir}'\n[metamorpheus]\n\"1.1.11\" = 'x'\n"));
        Assert.That((d.SearchSlots, d.CatchUpEvery), Is.EqualTo((1, 0)), "one slot, no catch-up unless asked");
        Assert.That(() => Machine.Load(TestSupport.WriteFile(dir, "m4.toml", $"work_root = '{dir}'\ncatch_up_every = 1\n[metamorpheus]\n\"1.1.11\" = 'x'\n")),
            Throws.InstanceOf<ConfigException>(), "every search a re-search would never search anything new");
    }

    // pride 014: HTTPS from the Globus host by default; Aspera's share of the attempts is the machine's to set (0 = HTTPS only).
    [Test]
    public void TheDownloadRoutesAreReadFromTheMachineAndChecked()
    {
        var (_, _, _, dir) = Runner();
        string M(string keys) => TestSupport.WriteFile(dir, $"r{keys.GetHashCode():x}.toml", $"work_root = '{dir}'\n{keys}[metamorpheus]\n\"1.1.11\" = 'x'\n");
        var d = Machine.Load(M(""));
        Assert.That((d.AsperaAttempts, d.PrideHttps), Is.EqualTo((3, "globus")));
        var m = Machine.Load(M("aspera_attempts = 0\npride_https = \"FTP\"\n"));
        Assert.That((m.AsperaAttempts, m.PrideHttps), Is.EqualTo((0, "ftp")));
        Assert.That(() => Machine.Load(M("pride_https = \"aspera\"\n")), Throws.InstanceOf<ConfigException>());
        Assert.That(() => Machine.Load(M("aspera_attempts = -1\n")), Throws.InstanceOf<ConfigException>());
    }

    private static (BatchRunner Runner, FakeSearch Search, FakeFiles Files, string Dir) DepositChemistryRunner()
    {
        string dir = TestSupport.TempDir();
        string q = TestSupport.MinimalQuestion.Replace("profiles = [\"label-free-dda@1\", \"tmt-dda@1\"]", "profiles = [\"label-free-dda@3\"]")
            + "\n[batch]\nrun_root = \"runs\"\nstate_dir = \"state\"\nqueue = \"queue.json\"\n";
        var question = QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml", q));
        var machine = Machine.Load(TestSupport.WriteFile(dir, "machine.toml",
            $"work_root = '{dir}'\nmin_free_gb = 0\n[metamorpheus]\n\"1.1.11\" = 'C:/nowhere/CMD.exe'\n"));
        var search = new FakeSearch();
        var files = new FakeFiles { Listing = Enumerable.Range(1, 6).Select(i => new PrideArchiveFile { FileName = $"run{i}.raw", FileSizeBytes = 500_000_000 }).ToList() };
        return (new BatchRunner(question, Profiles, machine, files, search, "2026-10-05") { Sha1Source = NoSha1 }, search, files, dir);
    }

    [Test]
    public async Task UnderADepositChemistryProfileTheProbeReadsTheChemistryFirst()
    {
        var (runner, search, files, dir) = DepositChemistryRunner();
        void Deposit(string acc, string protocol, string[]? ptms = null, string[]? quant = null)
        {
            search.Results[acc] = new() { TestSupport.Record(acc, "Type 2 diabetes muscle", protocol, instruments: new[] { "Q Exactive HF" }, files: new[] { "run1.raw" }) };
            search.Projects[acc] = new PrideProject
            {
                Accession = acc, Title = "Type 2 diabetes muscle", SampleProcessingProtocol = protocol,
                IdentifiedPTMStrings = (ptms ?? Array.Empty<string>()).Select(n => new MzLibUtil.CvParam { Name = n }).ToList(),
                QuantificationMethods = (quant ?? Array.Empty<string>()).Select(n => new MzLibUtil.CvParam { Name = n }).ToList(),
            };
        }

        // The NEM deposits' case: PRIDE lists light NEM, the protocol the heavy partner. Searched, with the chemistry recorded.
        Deposit("PXD000401", "thiol blocking buffer with d(0) NEM; Cys were alkylated with d(5) NEM; digested with trypsin", new[] { "Nethylmaleimide" });
        await runner.ProbeAsync(new QueueEntry("PXD000401", "t", "human"), CancellationToken.None);
        Assert.That(files.Downloads, Is.GreaterThan(0), "past the chemistry: on to the probe download (which this fake refuses)");
        Assert.That(runner.Settled("PXD000401"), Does.Not.StartWith("waiting_"));
        var chem = runner.State()["datasets"]!["PXD000401"]!["chemistry"]!;
        Assert.That(chem["cysteine_mods"]!.AsArray().Select(m => m!["metamorpheus"]!.GetValue<string>()),
            Is.EqualTo(new[] { "Unimod\tNethylmaleimide on C", "Unimod\tNEM:2H(5) on C" }));
        Assert.That(File.Exists(Path.Combine(dir, "runs", "PXD000401", "01_chemistry", "chemistry.json")), Is.True);

        int before = files.Downloads;
        // PRIDE says TMT (PXD027548's case): it waits for a profile that searches the label.
        Deposit("PXD000402", "digested with trypsin", quant: new[] { "TMT" });
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000402", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000402"), Is.EqualTo("waiting_tmt_labelling"));

        // Two proteases and file names that do not say which file is which: it waits.
        Deposit("PXD000403", "aliquots were digested with trypsin or with Glu-C");
        Assert.That(await runner.ProbeAsync(new QueueEntry("PXD000403", "t", "human"), CancellationToken.None), Is.Null);
        Assert.That(runner.Settled("PXD000403"), Is.EqualTo("waiting_multi_protease"));
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("PXD000403 SCREENED OUT (waiting_multi_protease)"));
        Assert.That(files.Downloads, Is.EqualTo(before), "nothing was downloaded for the two held back: the chemistry is read first");

        // D34: the park that needs a person is ANNOUNCED; the one that waits on a capability (a TMT profile) is not. Both
        // still get their experiment record and notes (D30, D32).
        var decisions = File.ReadAllLines(Path.Combine(dir, "runs", DecisionsNeeded.FileName));
        Assert.That(decisions.Skip(1).Select(l => (l.Split('\t')[1], l.Split('\t')[2])), Is.EqualTo(new[] { ("PXD000403", "waiting_multi_protease") }));
        Assert.That(File.ReadAllText(Path.Combine(dir, "runs", "PXD000403", ExperimentRecord.Folder, ExperimentRecord.NotesFile)),
            Does.Contain("## A person must decide").And.Contain("| protease | multiple (GUESS) | protocol_text |"));
        var tmt = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "runs", "PXD000402", ExperimentRecord.Folder, ExperimentRecord.JsonFile)))!;
        Assert.That((tmt["outcome"]!.GetValue<string>(), tmt["decision_needed"]), Is.EqualTo(("waiting_tmt_labelling", (JsonNode?)null)));
        Assert.That(tmt["facts"]!["label"]!["source"]!.GetValue<string>(), Is.EqualTo(ChemistrySource.PrideQuant));
        await runner.ProbeAsync(new QueueEntry("PXD000403", "t", "human"), CancellationToken.None);
        Assert.That(File.ReadAllLines(Path.Combine(dir, "runs", DecisionsNeeded.FileName)), Has.Length.EqualTo(2), "announced once, not once per pass");
    }

    // G18: a reboot mid-search left the deposit at "searching" with a half-written 04_search/mm, which MetaMorpheus refuses
    // to reuse; the restart would have settled it as search_failed.
    [Test]
    public void ASearchCutOffByTheDriversDeathIsSearchedAgainThenSettled()
    {
        var (runner, _, _, dir) = Runner();
        var e = new QueueEntry("PXD000218", "t", "human");
        string search = Path.Combine(dir, "runs", "PXD000218", "04_search");
        for (int death = 1; death <= 3; death++)
        {
            runner.Record("PXD000218", ("status", "searching"));
            Directory.CreateDirectory(Path.Combine(search, "mm"));
            runner.RecoverInterruptedSearches(new[] { e });
            Assert.That(runner.State()["datasets"]!["PXD000218"]!["status"]!.GetValue<string>(), Is.EqualTo("search_interrupted"), $"death {death}");
            Assert.That(Directory.Exists(search), Is.False, "the half-written output is moved aside");
            Assert.That(runner.Retriable("PXD000218"), Is.True);
        }
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(search)!, "04_search.interrupted-*"), Is.Not.Empty);
        runner.Record("PXD000218", ("status", "searching"));
        runner.RecoverInterruptedSearches(new[] { e });
        Assert.That(runner.Settled("PXD000218"), Is.EqualTo("search_failed"), "a fourth death on one deposit settles it");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("PXD000218 SEARCH INTERRUPTED: the previous driver stopped"));

        // A search that finished before the death is left for ReconcileAsync to deliver, and other statuses are untouched.
        var done = new QueueEntry("PXD000219", "t", "human");
        string doneSearch = Path.Combine(dir, "runs", "PXD000219", "04_search");
        Directory.CreateDirectory(doneSearch);
        File.WriteAllText(Path.Combine(doneSearch, "provenance.json"), "{\"success\": true}");
        runner.Record("PXD000219", ("status", "searching"));
        runner.Record("PXD000220", ("status", "probing"));
        runner.RecoverInterruptedSearches(new[] { done, new QueueEntry("PXD000220", "t", "human") });
        Assert.That(File.Exists(Path.Combine(doneSearch, "provenance.json")), Is.True);
        Assert.That(runner.State()["datasets"]!["PXD000219"]!["status"]!.GetValue<string>(), Is.EqualTo("searching"));
        Assert.That(runner.State()["datasets"]!["PXD000220"]!["status"]!.GetValue<string>(), Is.EqualTo("probing"));

        // v0.4.18 (PXD008990, 2026-10-15): a search that failed when the full disk also refused its record is settled now.
        var lost = new QueueEntry("PXD000221", "t", "human");
        string lostSearch = Path.Combine(dir, "runs", "PXD000221", "04_search");
        Directory.CreateDirectory(lostSearch);
        File.WriteAllText(Path.Combine(lostSearch, "provenance.json"), "{\"success\": false, \"exit_code\": 4}");
        runner.Record("PXD000221", ("status", "searching"));
        runner.RecoverInterruptedSearches(new[] { lost });
        Assert.That(runner.Settled("PXD000221"), Is.EqualTo("search_failed"));
        Assert.That(File.Exists(Path.Combine(lostSearch, "provenance.json")), Is.True, "the failed output stays for batch retry to move aside");
    }

    // v0.4.17 (PXD044227, 2026-10-14): a reboot cut off the full search of 61 files. Its 04_search was moved aside, but the
    // ExperimentalDesign.tsv it wrote beside the spectra stayed, and the re-probe of three files read it: MetaMorpheus
    // stopped with `Condition "all" biorep 2 is missing!` (exit 5) and the deposit settled probe_search_failed.
    [Test]
    public void AnInterruptedSearchsDesignGoesAsideWithItsOutputAndTheReprobeRunsWithoutOne()
    {
        var (runner, _, _, dir) = Runner();
        var e = new QueueEntry("PXD044227", "t", "mouse");
        string run = Path.Combine(dir, "runs", "PXD044227"), spectra = Path.Combine(run, "02_fetch", "spectra");
        Directory.CreateDirectory(Path.Combine(run, "04_search", "mm"));
        Directory.CreateDirectory(spectra);
        var raws = Enumerable.Range(1, 61).Select(i => Path.Combine(spectra, $"run_{i:00}.raw")).ToList();
        foreach (string r in raws) File.WriteAllText(r, "");
        string design = Path.Combine(spectra, Search.DesignStage.FileName);
        File.WriteAllLines(design, new[] { "FileName\tCondition\tBiorep\tFraction\tTechrep" }
            .Concat(raws.Select((r, i) => $"{Path.GetFileNameWithoutExtension(r)}\tall\t{i + 1}\t1\t1")));
        runner.Record("PXD044227", ("status", "searching"));

        runner.RecoverInterruptedSearches(new[] { e });

        Assert.That(File.Exists(design), Is.False, "nothing the cut-off search wrote is left beside the spectra");
        string aside = Directory.GetDirectories(run, "04_search.interrupted-*").Single();
        string kept = Path.Combine(aside, BatchRunner.InterruptedSpectraFolder, Search.DesignStage.FileName);
        Assert.That(File.ReadAllLines(kept), Has.Length.EqualTo(62), "kept with the output it belongs to");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")),
            Does.Contain($"its {Search.DesignStage.FileName} moved to {Path.GetFileName(aside)}/{BatchRunner.InterruptedSpectraFolder}"));

        // A design left by any other route (a finished search, then a retry or re-search) is cleared by the probe itself.
        File.WriteAllText(design, File.ReadAllText(kept));
        var probe = raws.Where((_, i) => i is 0 or 30 or 60).ToList();
        Assert.That(Search.ProbeSearch.ClearDesign(probe), Is.True);
        Assert.That(File.Exists(design), Is.False);
        Assert.That(Search.ProbeSearch.ClearDesign(probe), Is.False, "nothing to clear the second time");
    }

    // G18: after a reboot, driver.pid's number may belong to another program; the start time in driver.last.json tells.
    [Test]
    public void APidReusedAfterARebootIsNotTakenForTheDriver()
    {
        string dir = TestSupport.TempDir(), pidFile = Path.Combine(dir, "driver.pid"), rec = Path.Combine(dir, BatchRunner.DriverRecordFile);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllText(pidFile, me.Id.ToString());
        Assert.That(BatchRunner.LiveDriver(pidFile, self: -1), Is.EqualTo(me.Id), "no record: the pid alone, as before");
        File.WriteAllText(rec, new JsonObject { ["pid"] = me.Id, ["start_utc"] = me.StartTime.ToUniversalTime().ToString("o") }.ToJsonString());
        Assert.That(BatchRunner.LiveDriver(pidFile, self: -1), Is.EqualTo(me.Id), "same pid, same start: the driver");
        File.WriteAllText(rec, new JsonObject { ["pid"] = me.Id, ["start_utc"] = me.StartTime.ToUniversalTime().AddHours(-5).ToString("o") }.ToJsonString());
        Assert.That(BatchRunner.LiveDriver(pidFile, self: -1), Is.Null, "same pid, another start: a reused pid");
        File.WriteAllText(rec, new JsonObject { ["pid"] = me.Id + 1, ["start_utc"] = "2020-01-01T00:00:00Z" }.ToJsonString());
        Assert.That(BatchRunner.LiveDriver(pidFile, self: -1), Is.EqualTo(me.Id), "a record of another pid says nothing about this one");
        Assert.That(BatchRunner.LiveDriver(pidFile), Is.Null, "never this process itself");
    }

    [Test]
    public async Task ADriverRecordsHowToStartItAgain()
    {
        var (runner, _, _, dir) = Runner();
        runner.CommandLine = new[] { "batch", "run", "question.toml", "--machine", "machine.toml" };
        await runner.RunAsync(Array.Empty<QueueEntry>(), CancellationToken.None);
        var rec = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "state", BatchRunner.DriverRecordFile)))!;
        Assert.That(rec["pid"]!.GetValue<int>(), Is.EqualTo(Environment.ProcessId));
        Assert.That(rec["args"]!.AsArray().Select(a => a!.GetValue<string>()), Is.EqualTo(runner.CommandLine));
        Assert.That(rec["exe"]!.GetValue<string>(), Is.EqualTo(Environment.ProcessPath));
        Assert.That(rec["cwd"]!.GetValue<string>(), Is.EqualTo(Environment.CurrentDirectory));
        Assert.That(File.Exists(Path.Combine(dir, "state", "driver.pid")), Is.False, "a clean exit removes the pid file; the record stays");
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

    // v0.4.17: the ENRICHED note quoted screen_evidence, the acquisition call's words, which are empty for nearly every
    // enriched deposit ("the deposit's own text says '......'"). It quotes the enrichment's own words now.
    [Test]
    public void TheEnrichedNoteQuotesTheEnrichmentsOwnWords()
    {
        string Note(JsonObject state)
        {
            string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD038718");
            Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
            Directory.CreateDirectory(Path.Combine(run, "04_search"));
            File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
            File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.12"}}}""");
            string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\ndatasets:\n");
            ManifestEntry.Append(manifest, new QueueEntry("PXD038718", "NPAS4:NuA4", "mouse"), Profiles["label-free-dda@5"], run, state, dir);
            return File.ReadAllLines(manifest).Single(l => l.Contains("ENRICHED ("));
        }
        Assert.That(Note(new JsonObject { ["enrichment"] = "immunoprecipitation", ["enrichment_evidence"] = "IP'd with anti-FLAG M2 resin", ["screen_evidence"] = null }),
            Does.Contain("the deposit's own text says '...IP'd with anti-FLAG M2 resin...'"));
        Assert.That(Note(new JsonObject { ["enrichment"] = "immunoprecipitation", ["screen_evidence"] = null }),
            Does.Contain("the deposit's own text names an enrichment.").And.Not.Contain("'......'"), "a state from before 0.4.17");
    }

    // PXR-A88 (aging 082): a probe-measured enrichment the text did not state used to stop at state.json; the entry now
    // says so and asks for curation, without declaring an enrichment itself.
    [Test]
    public void AMeasuredEnrichmentTheTextMissedIsNotedAndFlaggedForCuration()
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD061654");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("Seq70745_HFX.raw", true, Array.Empty<string>()), ("Seq70763_HFX.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.12"}}}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\ndatasets:\n");
        var state = new JsonObject { ["enrichment"] = "none", ["enrichment_measured"] = "acetyl" };
        ManifestEntry.Append(manifest, new QueueEntry("PXD061654", "Acetylome", "mouse"), Profiles["label-free-dda@5"], run, state, dir);
        string text = File.ReadAllText(manifest);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("enrichment: [none]"), "a measurement on three probe runs declares nothing");
            Assert.That(text, Does.Contain("possibly_mixed_enrichment").And.Contain("MEASURED ENRICHMENT (acetyl)"));
            Assert.That(text, Does.Not.Match(@"[\[ ]mixed_enrichment[,\]]"), "mixed_enrichment changes the ingest: curation only");
        });
    }

    // G34: PXD059520 was searched under the Search-only ion-trap profile, whose results are in Task1SearchTask; the entry
    // named Task3SearchTask and datarepo refused it. The folder follows the Search task's place in the profile's task list.
    [TestCase("label-free-dda@5", "Task3SearchTask")]
    [TestCase("label-free-dda-lowres@1", "Task1SearchTask")]
    [TestCase("tmt-dda@1", "Task3SearchTask")]
    public void TheManifestEntryNamesTheFolderTheProfilesSearchTaskWrites(string profile, string folder)
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD059520");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.12"}}}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\ndatasets:\n");
        ManifestEntry.Append(manifest, new QueueEntry("PXD059520", "Retina", "rat"), Profiles[profile], run, null, dir);
        Assert.That(File.ReadAllText(manifest), Does.Contain($"    search_results: 04_search/mm/{folder}\n"));
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
    public void APublishsWholeOutputIsKeptUncut()
    {
        // G38 (PXR-R14): batch.log keeps 1,200 characters, which lost a .NET exception's type and message twice.
        string dir = Path.Combine(TestSupport.TempDir(), "outputs");
        string head = "Unhandled exception. System.UnauthorizedAccessException: Access to the path is denied.";
        string stderr = head + "\n" + string.Concat(Enumerable.Repeat("   at DataRepo.Catalog.Build() in Catalog.cs:line 1343\n", 200));
        string path = BatchRunner.KeepOutput(dir, "PXD008344", "publish", new[] { "powershell", "-File", "C:/a b/publish.ps1" }, -532462766,
            "building the catalog", stderr, new DateTime(2026, 10, 11, 9, 45, 13, DateTimeKind.Utc));

        Assert.That(path, Is.EqualTo(Path.Combine(dir, "PXD008344_publish_20261011T094513Z.log")));
        string text = File.ReadAllText(path);
        Assert.That(text, Does.Contain(head), "the exception's first line survives");
        Assert.That(text, Does.Contain(stderr), "nothing is cut");
        Assert.That(text, Does.Contain("exit code -532462766"));
        Assert.That(text, Does.Contain("\"C:/a b/publish.ps1\""), "an argument with a space is quoted");
        Assert.That(text.IndexOf("## stdout", StringComparison.Ordinal), Is.LessThan(text.IndexOf("## stderr", StringComparison.Ordinal)));
    }

    // D41 rule 6 (PXR-A72, PXR-A75): on 2026-10-11 aging's catalog build passed the fixed 1-hour limit, and four publishes
    // in a row were killed (rc 124), one per ingest. Publishing is now scheduled: at most every every_h, only when
    // something was delivered since the last success, with the question's own limit.

    /// <summary>
    /// This OS's shell: <c>cmd /c</c> on Windows, <c>sh -c</c> elsewhere (public CI is Linux). Windows' cmd by full path:
    /// the test folder holds tests/FakeMetaMorpheus built as CMD.exe, which a bare "cmd" starts instead.
    /// </summary>
    private static string[] Shell(string script) =>
        OperatingSystem.IsWindows() ? new[] { Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c", script } : new[] { "sh", "-c", script };

    private static string ShellToml(string script) => "[" + string.Join(", ", Shell(script).Select(a => $"'{a}'")) + "]";

    private static (BatchRunner Runner, string Dir) Publisher(string script)
    {
        var (runner, _, _, dir) = Runner($"[publish]\nmanifest = \"manifest.yaml\"\ncommand = {ShellToml(script)}\nevery_h = 6\ntimeout_h = 1\n", "datarepo = 'x'\n");
        return (runner, dir);
    }

    [Test]
    public void APublishIsDueOnlyWhenSomethingWaitsAndTheIntervalHasPassed()
    {
        var now = new DateTime(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc);
        Assert.That(BatchRunner.PublishDue(now, null, 6, 0), Is.False, "nothing delivered: nothing to publish");
        Assert.That(BatchRunner.PublishDue(now, null, 6, 1), Is.True, "never published: at once");
        Assert.That(BatchRunner.PublishDue(now, now.AddHours(-5.9), 6, 3), Is.False);
        Assert.That(BatchRunner.PublishDue(now, now.AddHours(-6), 6, 3), Is.True);
        Assert.That(BatchRunner.PublishDue(now, now.AddMinutes(-1), 0, 1), Is.True, "every_h = 0: after each ingest, as before");
    }

    [Test]
    public void ThePublishScheduleIsTheQuestionsAndDefaultsToSixHoursWithAFourHourLimit()
    {
        string dir = TestSupport.TempDir();
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "q1.toml", TestSupport.MinimalQuestion + "\n[publish]\ncommand = [\"x\"]\n"));
        Assert.That((q.Publish!.EveryHours, q.Publish.TimeoutHours), Is.EqualTo((6.0, 4.0)));
        q = QuestionLoader.Load(TestSupport.WriteFile(dir, "q2.toml", TestSupport.MinimalQuestion + "\n[publish]\ncommand = [\"x\"]\nevery_h = 0\ntimeout_h = 2.5\n"));
        Assert.That((q.Publish!.EveryHours, q.Publish.TimeoutHours), Is.EqualTo((0.0, 2.5)));
        Assert.That(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "q3.toml", TestSupport.MinimalQuestion + "\n[publish]\nevery_h = -1\n")),
            Throws.InstanceOf<ConfigException>().With.Message.Contains("every_h"));
        Assert.That(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "q4.toml", TestSupport.MinimalQuestion + "\n[publish]\ntimeout_h = 0\n")),
            Throws.InstanceOf<ConfigException>().With.Message.Contains("timeout_h"));
    }

    [Test]
    public async Task OneScheduledPublishServesEveryWaitingDepositThenWaitsItsTurn()
    {
        var (runner, _) = Publisher("exit 0");
        runner.Record("PXD000001", ("ingest_rc", 0), ("publish_rc", 124));   // killed under the old 1-hour limit
        runner.Record("PXD000002", ("ingest_rc", 0));                        // delivered, publish_rc cleared
        runner.Record("PXD000003", ("ingest_rc", 1));                        // refused: not delivered
        runner.Record("PXD000004", ("ingest_rc", 0), ("publish_rc", 0));     // already served
        Assert.That(runner.AwaitingPublish(), Is.EqualTo(new[] { "PXD000001", "PXD000002" }));

        Assert.That(await runner.PublishIfDueAsync(false, CancellationToken.None), Is.EqualTo(0));
        Assert.That(runner.AwaitingPublish(), Is.Empty, "one publish serves everything ingested");
        var st = runner.State();
        Assert.That(st["datasets"]!["PXD000001"]!["publish_rc"]!.GetValue<int>(), Is.Zero);
        Assert.That(st["datasets"]!["PXD000002"]!["published_utc"], Is.Not.Null);
        Assert.That(st["publish"]!["last_served"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "PXD000001", "PXD000002" }));
        Assert.That(File.Exists(st["publish"]!["last_log"]!.GetValue<string>()), "G38: the whole output is kept");

        runner.Record("PXD000005", ("ingest_rc", 0));
        Assert.That(await runner.PublishIfDueAsync(false, CancellationToken.None), Is.Null, "the next one waits six hours from the last start");
        Assert.That(runner.AwaitingPublish(), Is.EqualTo(new[] { "PXD000005" }));
        Assert.That(await runner.PublishIfDueAsync(true, CancellationToken.None), Is.EqualTo(0), "at once, when the driver asks");
        Assert.That(await runner.PublishIfDueAsync(true, CancellationToken.None), Is.Null, "nothing waits: nothing runs");
    }

    [Test]
    public async Task AFailedPublishLeavesItsDepositsWaitingForTheNextOne()
    {
        var (runner, _) = Publisher("exit 3");
        runner.Record("PXD000001", ("ingest_rc", 0));
        Assert.That(await runner.PublishIfDueAsync(false, CancellationToken.None), Is.EqualTo(3));
        Assert.That(runner.AwaitingPublish(), Is.EqualTo(new[] { "PXD000001" }));
        var st = runner.State();
        Assert.That(st["datasets"]!["PXD000001"]!["publish_rc"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(st["publish"]!["last_rc"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(st["publish"]!["last_failed"]!.AsArray().Count, Is.EqualTo(1));
        Assert.That(await runner.PublishIfDueAsync(false, CancellationToken.None), Is.Null, "a failure also waits its turn: no build every minute");
    }

    [Test]
    public async Task ADriverThatRanOutOfQueuePublishesWhatWaitsAndOneStoppedLeavesItToTheNext()
    {
        var (runner, dir) = Publisher("exit 0");
        runner.Record("PXD000001", ("ingest_rc", 0));
        string stateFile = Path.Combine(dir, "state", "state.json");
        void LastPublishedAnHourAgo()
        {
            var st = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
            st["publish"] = new JsonObject { ["last_started_utc"] = DateTime.UtcNow.AddHours(-1).ToString("o") };
            File.WriteAllText(stateFile, st.ToJsonString());
        }

        LastPublishedAnHourAgo();
        File.WriteAllText(Path.Combine(dir, "state", "STOP"), "hold");
        await runner.RunAsync(Array.Empty<QueueEntry>(), CancellationToken.None);
        Assert.That(runner.AwaitingPublish(), Is.EqualTo(new[] { "PXD000001" }), "STOP drains; it starts no build");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("not yet published (PXD000001): the next driver publishes them first"));

        File.Delete(Path.Combine(dir, "state", "STOP"));
        await runner.RunAsync(Array.Empty<QueueEntry>(), CancellationToken.None);
        Assert.That(runner.AwaitingPublish(), Is.Empty, "the queue ran out: nothing competes for the disk, so it publishes now");
        Assert.That(File.ReadAllText(Path.Combine(dir, "state", "batch.log")), Does.Contain("queue done: publishing the 1 deposit(s) waiting now"));
    }

    [Test]
    public async Task AKilledProcessKeepsWhatItWroteBeforeTheKill()
    {
        // 2026-10-11: four killed publishes left batch.log and state with "TIMEOUT after 3600 s" and nothing else.
        string[] argv = Shell(OperatingSystem.IsWindows() ? "echo building the catalog& ping -n 30 127.0.0.1 >nul" : "echo building the catalog; sleep 30");
        var (rc, stdout, stderr) = await BatchRunner.RunProcessFullAsync(argv, TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.That(rc, Is.EqualTo(124));
        Assert.That(stdout, Does.Contain("building the catalog"));
        Assert.That(stderr, Does.EndWith("TIMEOUT after 3 s"));
    }

    [Test]
    public void AnOutputThatCannotBeKeptIsReportedNotThrown()
    {
        // A file where the folder should be: the step succeeded, so keeping its output must not fail the delivery.
        string blocker = TestSupport.WriteFile(TestSupport.TempDir(), "outputs", "not a folder");
        string result = BatchRunner.KeepOutput(blocker, "PXD000001", "ingest", new[] { "datarepo" }, 0, "", "", DateTime.UtcNow);
        Assert.That(result, Does.StartWith("(not kept: "));
    }

    [Test]
    public void AFailedIngestIsNotDeliveredSoTheNextStartRetriesIt()
    {
        const string manifest = "manifest_version: 1\ndatasets:\n\n  - accession: PXD075372\n    status: include\n";
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject { ["ingest_rc"] = 1 }), Is.False, "the entry is appended before ingest runs");
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject { ["ingest_rc"] = 0 }), Is.True);
        Assert.That(BatchRunner.Delivered(manifest, "PXD075372", new JsonObject { ["ingest_rc"] = -1 }), Is.False, "G18: ingest began and the driver died inside it");
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

    // v0.4.18: a fetch that would cross the disk floor pauses the deposit (unsettled) and stops the driver taking new ones.
    [Test]
    public async Task AFetchAtTheDiskFloorPausesTheDepositAndStopsTheWalk()
    {
        var (runner, search, files, dir) = Runner(extraMachine: "fetch_attempts = 1\npass_wait_minutes = 0\n");
        search.Results["PXD000208"] = new() { TestSupport.Record("PXD000208", "Type 2 diabetes muscle") };
        search.Results["PXD000210"] = new() { TestSupport.Record("PXD000210", "Type 2 diabetes liver") };
        files.Listing = Enumerable.Range(0, 5).Select(i => new PrideArchiveFile { FileName = $"h{i}.raw", FileSizeBytes = 500_000_000 }).ToList();
        runner.FloorForTest = new Fetch.DiskFloor(() => 10, 9);
        await runner.RunAsync(new[] { new QueueEntry("PXD000208", "t", "human"), new QueueEntry("PXD000210", "t", "human") }, CancellationToken.None);

        var ds = runner.State()["datasets"]!;
        Assert.That(ds["PXD000208"]!["status"]!.GetValue<string>(), Is.EqualTo("fetch_paused_disk"));
        Assert.That(runner.Settled("PXD000208"), Is.Empty, "the next driver takes it up");
        Assert.That(ds["PXD000210"], Is.Null, "no further deposit was started");
        Assert.That(files.Downloads, Is.Zero);
        string log = File.ReadAllText(Path.Combine(dir, "state", "batch.log"));
        Assert.That(log, Does.Contain("PXD000208 PROBE fetch paused at the disk floor").And.Contain("a fetch reached the disk floor"));
        Assert.That(log, Does.Not.Contain("pass 1 done").And.Not.Contain("queue done"));
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
        File.WriteAllText(Path.Combine(runs, "PXD000209", "04_search", "provenance.json"), "{\"success\": true}");
        Assert.That(() => BatchRunner.Requeue(state, runs, "PXD000209", "x"), Throws.TypeOf<PXReprise.Cli.UsageException>().With.Message.Contains("finished search"));

        // v0.4.18: a failed search is retried: its output is moved aside (never deleted) and the raw files stay.
        File.WriteAllText(Path.Combine(runs, "PXD000209", "04_search", "provenance.json"), "{\"success\": false}");
        Directory.CreateDirectory(Path.Combine(runs, "PXD000209", "02_fetch", "spectra"));
        File.WriteAllText(Path.Combine(runs, "PXD000209", "02_fetch", "spectra", "a.raw"), "raw");
        BatchRunner.Requeue(state, runs, "PXD000209", "search failed on a full disk");
        Assert.That(Directory.Exists(Path.Combine(runs, "PXD000209", "04_search")), Is.False);
        Assert.That(Directory.GetDirectories(Path.Combine(runs, "PXD000209"), "04_search.failed-*"), Has.Length.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(runs, "PXD000209", "02_fetch", "spectra", "a.raw")), Is.True);
        Assert.That(runner.State()["datasets"]!["PXD000209"]!["requeued"]!.AsArray()[^1]!["failed_search_moved_to"]!.GetValue<string>(), Does.StartWith("04_search.failed-"));
    }

    [Test]
    public void BatchResearchMovesTheSearchedDepositAsideAndRequeuesIt()   // PXR-A29: the NEM deposits under label-free-dda@3
    {
        var (runner, _, _, dir) = Runner();
        string state = Path.Combine(dir, "state"), runs = Path.Combine(dir, "runs"), run = Path.Combine(runs, "PXD001054");
        Assert.That(() => BatchRunner.Research(state, runs, "PXD001054", "x", null, null),
            Throws.TypeOf<PXReprise.Cli.UsageException>().With.Message.Contains("batch retry"), "nothing searched yet");
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"success":true}""");
        runner.Record("PXD001054", ("status", "searched"), ("profile", "label-free-dda@1"), ("title", "Redox"), ("organism", "mouse"),
            ("bundle_id", "abc"), ("ingest_rc", 0));
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "datasets:\n\n  - accession: PXD001054\n    status: exclude\n");
        Assert.That(() => BatchRunner.Research(state, runs, "PXD001054", "x", null, manifest),
            Throws.TypeOf<PXReprise.Cli.UsageException>().With.Message.Contains("'exclude'"));
        Assert.Throws<PXReprise.Cli.UsageException>(() => BatchRunner.Research(state, runs, "PXD001054", " ", null, null), "a reason is required");

        File.WriteAllText(manifest, "datasets:\n\n  - accession: PXD001054\n    status: include\n");
        BatchRunner.Research(state, runs, "PXD001054", "NEM capping, aging PXR-A29", "label-free-dda@3", manifest);
        Assert.That(Directory.Exists(run), Is.False);
        Assert.That(Directory.GetDirectories(runs, "PXD001054.superseded-*").Single(), Does.Exist);
        var e = runner.State()["datasets"]!["PXD001054"]!;
        Assert.That(e["status"]!.GetValue<string>(), Is.EqualTo("requeued_user"));
        Assert.That(runner.Settled("PXD001054"), Is.Empty);
        Assert.That((e["research_profile"]!.GetValue<string>(), e["organism"]!.GetValue<string>()), Is.EqualTo(("label-free-dda@3", "mouse")));
        Assert.That(e["ingest_rc"], Is.Null, "the new search is delivered afresh");
        var h = e["researched"]![0]!;
        Assert.That((h["previous_profile"]!.GetValue<string>(), h["previous_bundle_id"]!.GetValue<string>(), h["previous_entry"]!["status"]!.GetValue<string>()),
            Is.EqualTo(("label-free-dda@1", "abc", "searched")));
        Assert.That(File.ReadAllText(Path.Combine(state, "batch.log")), Does.Contain("PXD001054 RE-SEARCH requested by").And.Contain("profile label-free-dda@3"));
    }

    [Test]
    public void AReSearchReplacesItsManifestEntryOnceAndKeepsWhatTheOwnerWrote()
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD001054");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.11"}}}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml",
            "datasets:\n\n  - accession: PXD001054\n    status: include\n    flags: [no_design_file, no_output_sdrf, hand_flag]\n    notes: >\n      Searched under @1.\n      Cys peptides missing; re-search planned.\n\n  - accession: PXD018999\n    status: include\n    notes: kept\n");
        var state = new JsonObject
        {
            ["researched"] = new JsonArray(new JsonObject { ["utc"] = "2026-10-05T22:00:00+00:00", ["reason"] = "NEM", ["previous_profile"] = "label-free-dda@1", ["moved_to"] = "PXD001054.superseded-x" }),
        };
        var e = new QueueEntry("PXD001054", "Redox", "mouse");
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@3"], run, state, dir);
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@3"], run, state, dir);
        string text = File.ReadAllText(manifest);
        Assert.That(text.Split("accession: PXD001054").Length - 1, Is.EqualTo(1));
        Assert.That(text, Does.Contain("accession: PXD018999\n    status: include\n    notes: kept"), "the other entries are untouched");
        Assert.That(text, Does.Contain("RE-SEARCHED 2026-10-05T22:00:00+00:00 (was label-free-dda@1").And.Contain("under profile label-free-dda@3")
            .And.Contain("BEFORE THE RE-SEARCH this entry said: \"Searched under @1. Cys peptides missing; re-search planned.\"")
            .And.Contain("hand_flag"));
        Assert.That(text.Split("BEFORE THE RE-SEARCH").Length - 1, Is.EqualTo(1), "replaced once, never nested");
    }

    // aging 042 (PXR-A36): keys the batch does not write are the owner's, and survive a re-search verbatim. The two
    // search-decided flags come from the new search's provenance (D28: @4 has a design and writes its own SDRF).
    [Test]
    public void AReSearchKeepsKeysItDoesNotOwnAndTakesTheDesignFlagsFromTheNewSearch()
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD012753");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.12"}},"flags":["low_core_use: 3 avg cores"]}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml",
            "datasets:\n\n  - accession: PXD012753\n    status: include\n    flags: [no_design_file, no_output_sdrf]\n"
            + "    excluded_runs:\n      20170317_VM_17: \"242 PSMs (aging S72, D68)\"\n      20170317_VM_18: \"326 PSMs\"\n"
            + "    restrictions: {protein_statistics: \"few PSMs\"}\n    notes: >\n      Searched under @1.\n");
        var state = new JsonObject
        {
            ["researched"] = new JsonArray(new JsonObject { ["utc"] = "2026-10-08T00:00:00+00:00", ["reason"] = "1.1.12", ["previous_profile"] = "label-free-dda@1", ["moved_to"] = "x" }),
        };
        ManifestEntry.Append(manifest, new QueueEntry("PXD012753", "Muscle", "mouse"), Profiles["label-free-dda@4"], run, state, dir);
        string text = File.ReadAllText(manifest);
        Assert.That(text, Does.Contain("    excluded_runs:\n      20170317_VM_17: \"242 PSMs (aging S72, D68)\"\n      20170317_VM_18: \"326 PSMs\"\n"));
        Assert.That(text, Does.Contain("    restrictions: {protein_statistics: \"few PSMs\"}\n"));
        Assert.That(text, Does.Contain("    flags: []").And.Contain("metamorpheus: \"1.1.12\""));
    }

    // aging 098 (PXR-A99): with no alkylation, a low cysteine share is expected, and the note says so.
    [Test]
    public void ACysteinePoorDepositWithNoAlkylationIsNotedAsExpected()
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", "PXD015239");
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), Report(("a.raw", true, Array.Empty<string>())).ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"),
            """{"tools":{"MetaMorpheus":{"release":"1.1.12"}},"flags":["cysteine_poor: 0.30% of 1% target peptides contain C (whole proteomes: 12-26%); check the alkylant (aging PXR-A37)"]}""");
        string manifest = TestSupport.WriteFile(dir, "manifest.yaml", "datasets:\n");
        var chem = Search.SearchStage.Chemistry(new DepositChemistry(new("trypsin", ChemistrySource.ProtocolText), null,
            new("none", ChemistrySource.QuestionException, "no reduction or alkylation step"), Array.Empty<CysMod>(), new("label_free", ChemistrySource.Default), null));
        var state = new JsonObject { ["exception"] = "alkylation none (no reduction or alkylation step)", ["chemistry"] = chem };
        ManifestEntry.Append(manifest, new QueueEntry("PXD015239", "Brain", "human"), Profiles["label-free-dda@7"], run, state, dir);
        string text = File.ReadAllText(manifest);
        Assert.That(text, Does.Contain("QUESTION EXCEPTION: alkylation none (no reduction or alkylation step).")
            .And.Contain("CYSTEINE-POOR: 0.30% of 1% target peptides contain C")
            .And.Contain("Expected here: the cysteines were not alkylated (alkylation none, question_exception)")
            .And.Contain("cysteine unmodified (question_exception)"));
        Assert.That(text, Does.Contain("cysteine_poor").And.Contain("question_exception"));
    }

    private sealed class FakeFiles : IPrideFiles
    {
        public List<PrideArchiveFile> Listing { get; set; } = new();
        public Func<Exception> Failure { get; set; } = () => new InvalidOperationException("no downloads in this test");
        public int Downloads;
        public Task<List<PrideArchiveFile>> ListFilesAsync(string a, CancellationToken ct) => Task.FromResult(Listing);
        public Task<List<string>> ListFtpNamesAsync(string a, CancellationToken ct) => Task.FromResult(Listing.Select(f => f.FileName).ToList());
        public Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct)
        {
            // As the PRIDE client does: a file already present is returned, not fetched again.
            if (File.Exists(Path.Combine(dir, f.FileName))) return Task.FromResult(Path.Combine(dir, f.FileName));
            Interlocked.Increment(ref Downloads);
            throw Failure();
        }
    }
}
