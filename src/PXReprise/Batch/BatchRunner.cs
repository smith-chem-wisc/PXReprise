using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Census;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Fetch;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Batch;

public sealed record QueueEntry(string Accession, string Title, string Organism);

/// <summary>
/// The unattended batch for one question: every queued deposit screened, probed, fetched whole, quality-checked,
/// searched under the profile routing gives it, cleaned up, and delivered to the question's repository. It reproduces
/// aging's batch_runner.py; every gate there was paid for by a real failure, and the reasons are kept beside the code.
///
/// State lives in the question's state directory: state.json (one entry per deposit), batch.log, STOP, driver.pid.
/// A deposit's settled status (deferred, excluded, skipped, failed) is never retried automatically; clear its entry to.
/// Unavailability is not settled: a deposit PRIDE could not serve is retried on later passes (<c>fetch_passes</c>).
/// </summary>
public sealed class BatchRunner
{
    private readonly Question _q;
    private readonly IReadOnlyDictionary<string, Profile> _profiles;
    private readonly Machine _m;
    private readonly IPrideFiles _files;
    private readonly IProjectSearch _search;
    private readonly string _runDate;
    private readonly string _stateDir, _runRoot;
    private readonly object _gate = new();
    private bool _libraryOk = true;
    private readonly HashSet<string> _searching = new(StringComparer.Ordinal);
    private int _started;

    /// <summary>The deposits being searched (up to search_slots) while the next one is screened and fetched.</summary>
    internal IReadOnlyList<string> Searching { get { lock (_searching) return _searching.ToList(); } }

    internal void SearchStarted(string acc) { lock (_searching) _searching.Add(acc); }
    internal void SearchEnded(string acc) { lock (_searching) _searching.Remove(acc); }

    public BatchRunner(Question q, IReadOnlyDictionary<string, Profile> profiles, Machine m, IPrideFiles files, IProjectSearch search, string runDate)
    {
        _q = q;
        _profiles = profiles;
        _m = m;
        _files = files;
        _search = search;
        _runDate = runDate;
        var b = q.Batch ?? throw new ConfigException(q.SourceFile, "a batch needs a [batch] table (run_root, state_dir, queue)");
        _stateDir = b.StateDir;
        _runRoot = b.RunRoot;
        Directory.CreateDirectory(_stateDir);
        Directory.CreateDirectory(_runRoot);
    }

    private string StateFile => Path.Combine(_stateDir, "state.json");
    private string StopFile => Path.Combine(_stateDir, "STOP");
    private string PidFile => Path.Combine(_stateDir, "driver.pid");
    private string Run(string acc) => Path.Combine(_runRoot, acc);

    // ------------------------------------------------------------------ state, log

    public void Log(string msg)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'+00:00'}  {msg}";
        lock (_gate)
        {
            Console.Error.WriteLine(line);
            File.AppendAllText(Path.Combine(_stateDir, "batch.log"), line + "\n");
        }
    }

    public JsonObject State()
    {
        lock (_gate)
            return File.Exists(StateFile)
                ? JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject()
                : new JsonObject { ["started_utc"] = DateTime.UtcNow.ToString("o"), ["datasets"] = new JsonObject() };
    }

    public void Record(string acc, params (string Key, JsonNode? Value)[] fields)
    {
        lock (_gate)
        {
            var st = State();
            var ds = st["datasets"]!.AsObject();
            if (ds[acc] is not JsonObject e) ds[acc] = e = new JsonObject();
            // A new status without its own detail retires the old one: the first-run's PXD058082 read "searched" next to a
            // TLS error from a pass that had since succeeded. Kept as earlier_detail, so the history is not lost.
            if (fields.Any(f => f.Key == "status") && fields.All(f => f.Key != "detail") && e["detail"] is { } stale)
            {
                e.Remove("detail");
                e["earlier_detail"] = stale;
            }
            foreach (var (k, v) in fields) e[k] = v?.DeepClone();
            e["updated_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
            Save(st);
        }
    }

    /// <summary>Writes state.json beside, then renames it over the old one. Call under <c>_gate</c>.</summary>
    private void Save(JsonObject st)
    {
        string tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, st.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        File.Move(tmp, StateFile, overwrite: true);
    }

    private JsonObject? Entry(string acc) => State()["datasets"]![acc] as JsonObject;

    /// <summary>
    /// One driver at a time. Without this guard, clearing STOP and relaunching once left two drivers fetching into the same
    /// folders and both writing state.json.
    /// </summary>
    private void ClaimOrDie()
    {
        if (LiveDriver(PidFile) is int old)
            throw new UsageException($"a batch driver is already running as pid {old}; stop it first (or delete {PidFile} if it is stale)");
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        WriteDriverRecord();
    }

    /// <summary>
    /// The command line <c>batch run</c> was given (set by the CLI). With it, <see cref="DriverRecordFile"/> records how to
    /// start this driver again, which is what a start after a reboot runs (G18, <c>tools/autostart</c>).
    /// </summary>
    public IReadOnlyList<string>? CommandLine { get; set; }

    /// <summary>driver.last.json beside driver.pid: who the driver is (pid and start time) and how to start it again.</summary>
    public const string DriverRecordFile = "driver.last.json";

    private void WriteDriverRecord()
    {
        using var me = Process.GetCurrentProcess();
        var o = new JsonObject
        {
            ["pid"] = me.Id,
            ["start_utc"] = me.StartTime.ToUniversalTime().ToString("o"),
            ["engine"] = Provenance.RunRecord.Versions()["pxreprise"]?.ToString(),
            ["exe"] = Environment.ProcessPath,
            ["args"] = CommandLine is null ? null : new JsonArray(CommandLine.Select(a => (JsonNode?)a).ToArray()),
            ["cwd"] = Environment.CurrentDirectory,
        };
        string f = Path.Combine(_stateDir, DriverRecordFile), tmp = f + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        File.Move(tmp, f, overwrite: true);
    }

    /// <summary>
    /// The pid in <paramref name="pidFile"/> when it names a live process other than this one. After a reboot that number
    /// may belong to some other program, so when driver.last.json records the driver's start time for the same pid, the
    /// process must have started then too (G18); a pid file older than that record is taken on the pid alone.
    /// </summary>
    internal static int? LiveDriver(string pidFile, int? self = null)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid) || pid == (self ?? Environment.ProcessId)) return null;
        DateTime? started = null;
        try
        {
            var rec = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(pidFile)!, DriverRecordFile)));
            if (rec?["pid"]?.GetValue<int>() == pid && DateTime.TryParse(rec["start_utc"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var s))
                started = s.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException) { }
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            if (started is { } t && Math.Abs((p.StartTime.ToUniversalTime() - t).TotalSeconds) > 2) return null;   // a reused pid
            return pid;
        }
        catch (ArgumentException) { return null; }   // no such process: the file is stale
    }

    /// <summary>
    /// <c>batch retry</c>: puts a deposit back in the queue (status <c>requeued_user</c>), so a settled status such as
    /// <c>fetch_failed</c> is never undone by hand-editing state.json. The previous status, detail, time, user and reason
    /// are kept in the entry's <c>requeued</c> list and in batch.log. Refused while a driver runs (it owns state.json)
    /// and for a deposit whose search finished.
    /// </summary>
    public static object Requeue(string stateDir, string runRoot, string acc, string reason)
    {
        string pidFile = Path.Combine(stateDir, "driver.pid"), stateFile = Path.Combine(stateDir, "state.json");
        if (string.IsNullOrWhiteSpace(reason)) throw new UsageException("--reason must say why the deposit is retried");
        if (LiveDriver(pidFile) is int pid) throw new UsageException($"a batch driver is running as pid {pid}; stop it (batch stop) and let it exit first");
        if (File.Exists(Path.Combine(runRoot, acc, "04_search", "provenance.json")))
            throw new UsageException($"{acc} has a finished search; there is nothing to retry");
        if (!File.Exists(stateFile)) throw new UsageException($"no {stateFile}: the batch has not run");
        var st = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        if (st["datasets"]?[acc] is not JsonObject e) throw new UsageException($"{acc} is not in {stateFile}");
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
        string previous = e["status"]?.GetValue<string>() ?? "?";
        if (e["requeued"] is not JsonArray history) e["requeued"] = history = new JsonArray();
        history.Add(new JsonObject
        {
            ["utc"] = now, ["previous_status"] = previous, ["previous_detail"] = e["detail"]?.DeepClone(),
            ["by"] = Environment.UserName, ["reason"] = reason,
        });
        e["status"] = "requeued_user";
        e.Remove("detail");
        e.Remove("unavailable_passes");
        e["updated_utc"] = now;
        string tmp = stateFile + ".tmp";
        File.WriteAllText(tmp, st.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        File.Move(tmp, stateFile, overwrite: true);
        File.AppendAllText(Path.Combine(stateDir, "batch.log"), $"{now}  {acc} REQUEUED by {Environment.UserName} (was {previous}): {reason}\n");
        return new { accession = acc, previous_status = previous, status = "requeued_user" };
    }

    /// <summary>
    /// <c>batch research</c>: searches a deposit again whose search finished, e.g. under a profile that reads its chemistry
    /// (PXR-A29). The deposit's folder is moved aside whole (<c>PXD….superseded-&lt;utc&gt;</c>), never deleted, and its
    /// state entry is archived in its <c>researched</c> list; the next <c>batch run</c> downloads and searches it again like
    /// a new deposit, under <paramref name="profile"/> when given (one of the question's profiles), else as routing chooses.
    /// The delivery already in the repository is untouched until the new search is ingested; the manifest entry is then
    /// replaced, keeping its status, flags and notes (<see cref="ManifestEntry.Append"/>). Refused while a driver runs, for a
    /// deposit with no finished search (use <c>batch retry</c>), and for one the manifest excludes.
    /// </summary>
    public static object Research(string stateDir, string runRoot, string acc, string reason, string? profile, string? manifest, bool byDriver = false)
    {
        string pidFile = Path.Combine(stateDir, "driver.pid"), stateFile = Path.Combine(stateDir, "state.json");
        if (string.IsNullOrWhiteSpace(reason)) throw new UsageException("--reason must say why the deposit is searched again");
        // The driver itself may re-search (D39 catch-up, under its own state lock); anyone else waits for it to exit.
        if (!byDriver && LiveDriver(pidFile) is int pid) throw new UsageException($"a batch driver is running as pid {pid}; stop it (batch stop) and let it exit first");
        string dir = Path.Combine(runRoot, acc);
        if (!File.Exists(Path.Combine(dir, "04_search", "provenance.json")))
            throw new UsageException($"{acc} has no finished search; use batch retry for a deposit that never searched");
        if (manifest is not null && File.Exists(manifest))
        {
            string text = File.ReadAllText(manifest);
            if (text.Contains($"accession: {acc}\n") || text.Contains($"accession: {acc}\r"))
            {
                var (s, end) = ManifestEntry.Block(text, acc);
                if (System.Text.RegularExpressions.Regex.Match(text[s..end], @"(?m)^[ \t]+status:[ \t]*(\S+)") is { Success: true } m && m.Groups[1].Value != "include")
                    throw new UsageException($"{acc} is '{m.Groups[1].Value}' in {manifest}; include it there first");
            }
        }
        if (!File.Exists(stateFile)) throw new UsageException($"no {stateFile}: the batch has not run");
        var st = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        var ds = st["datasets"]!.AsObject();
        var old = ds[acc] as JsonObject ?? new JsonObject();
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
        string stamp = dir + $".superseded-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}", aside = stamp;
        for (int k = 2; Directory.Exists(aside) || File.Exists(aside); k++) aside = $"{stamp}-{k}";
        Directory.Move(dir, aside);
        var history = old["researched"]?.DeepClone() as JsonArray ?? new JsonArray();
        var previous = old.DeepClone().AsObject();
        previous.Remove("researched");
        history.Add(new JsonObject
        {
            ["utc"] = now, ["by"] = Environment.UserName, ["reason"] = reason, ["profile"] = profile,
            ["previous_status"] = old["status"]?.DeepClone(), ["previous_profile"] = old["profile"]?.DeepClone(),
            ["previous_bundle_id"] = old["bundle_id"]?.DeepClone(), ["moved_to"] = Path.GetFileName(aside),
            ["previous_entry"] = previous,
        });
        var e = new JsonObject
        {
            ["status"] = "requeued_user", ["title"] = old["title"]?.DeepClone(), ["organism"] = old["organism"]?.DeepClone(),
            ["research_profile"] = profile, ["researched"] = history, ["updated_utc"] = now,
        };
        if (old["requeued"] is { } rq) e["requeued"] = rq.DeepClone();
        ds[acc] = e;
        string tmp = stateFile + ".tmp";
        File.WriteAllText(tmp, st.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        File.Move(tmp, stateFile, overwrite: true);
        File.AppendAllText(Path.Combine(stateDir, "batch.log"),
            $"{now}  {acc} RE-SEARCH requested by {Environment.UserName} (was {old["status"]} under {old["profile"]}; {(profile is null ? "profile as routed" : $"profile {profile}")}); folder moved to {Path.GetFileName(aside)}: {reason}\n");
        return new { accession = acc, previous_status = old["status"]?.GetValue<string>(), status = "requeued_user", moved_to = aside, profile };
    }

    private double FreeGb() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_runRoot))!).AvailableFreeSpace / 1e9;

    /// <summary>A status the runner does not spend time on again. Clear the state entry to retry.</summary>
    public string Settled(string acc)
    {
        if (_q.Holds.ContainsKey(acc)) return "on_hold_user";
        string? s = Entry(acc)?["status"]?.GetValue<string>();
        // A deposit deferred by a cap, or settled by size, gets ONE more look when the question adds an exception for it:
        // the exception is recorded on that look, so a deposit that still does not fit settles again (D28).
        if (s is not null && (s.StartsWith("deferred_", StringComparison.Ordinal) || s == "skipped_selection_empty")
            && _q.ExceptionFor(acc) is { } ex && Entry(acc)?["exception"]?.GetValue<string>() != ex.Describe())
            return "";
        return s is not null && (s is "fetch_failed" or "probe_fetch_failed" or "search_failed"
                                 || s.StartsWith("deferred_", StringComparison.Ordinal) || s.StartsWith("skipped_", StringComparison.Ordinal)
                                 || s.StartsWith("excluded_", StringComparison.Ordinal) || s.StartsWith("waiting_", StringComparison.Ordinal))
            ? s : "";
    }

    private bool SearchedOk(string acc) =>
        File.Exists(Path.Combine(Run(acc), "04_search", "provenance.json"))
        && JsonNode.Parse(File.ReadAllText(Path.Combine(Run(acc), "04_search", "provenance.json")))?["success"]?.GetValue<bool>() == true;

    // ------------------------------------------------------------------ the loop

    public async Task RunAsync(IReadOnlyList<QueueEntry> queue, CancellationToken ct)
    {
        ClaimOrDie();
        Log($"batch start: pid {Environment.ProcessId}, {queue.Count} queued, free {FreeGb():0} GB");
        Log($"  engine   pxreprise {Provenance.RunRecord.Versions()["pxreprise"]}");
        Log($"  question {_q.SourceFile} ({_q.Name}); profiles {string.Join(", ", _q.Profiles)}");
        Log($"  manifest {_q.Publish?.Manifest ?? "(none)"}");
        Log($"  datarepo {_m.DataRepo ?? "(none)"}");
        CancellationTokenSource? stopPublishing = null;
        Task? publisher = null;
        bool ranOut = false;
        try
        {
            if (_m.DataRepo is not null)
            {
                int vrc; string vout, verr;
                try { (vrc, vout, verr) = await RunProcessFullAsync(new[] { _m.DataRepo, "--version" }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
                catch (System.ComponentModel.Win32Exception ex) { (vrc, vout, verr) = (-1, "", ex.Message); }   // not startable: ingest will say so
                string version = (vout + verr).Trim().Split('\n')[0].Trim();
                _ingestJson = vrc == 0 && TakesIngestJson(version);
                Log($"  datarepo version: {(version.Length > 0 ? version : $"unknown (rc {vrc})")}; ingest reads {(_ingestJson ? "the --json envelope" : "the exit code")}");
            }
            await ReconcileAsync(queue, ct).ConfigureAwait(false);
            if (Publishes)
            {
                var pub = _q.Publish!;
                Log($"  publish  at most every {pub.EveryHours:0.#} h, when something was delivered since the last success; limit {pub.TimeoutHours:0.#} h; {AwaitingPublish().Count} deposit(s) waiting, next {PublishWhen()}");
                stopPublishing = new CancellationTokenSource();
                publisher = Task.Run(() => PublishLoopAsync(stopPublishing.Token, ct), CancellationToken.None);
            }
            // Pass 1 walks the queue; each later pass walks only the deposits PRIDE or UniProt failed to serve, after a
            // wait. The pass count bounds the loop: an outage longer than fetch_passes waits leaves them for the next run.
            var walk = queue;
            for (int pass = 1; ; pass++)
            {
                if (!await WalkAsync(walk, ct).ConfigureAwait(false)) return;
                walk = queue.Where(e => Retriable(e.Accession)).ToList();
                if (walk.Count == 0) { Log("queue exhausted"); ranOut = true; return; }
                string which = string.Join(", ", walk.Take(10).Select(e => e.Accession)) + (walk.Count > 10 ? ", ..." : "");
                if (pass >= _m.FetchPasses)
                {
                    Log($"queue exhausted; {walk.Count} deposit(s) still waiting on PRIDE after {pass} pass(es) ({which}): run the batch again later");
                    ranOut = true;
                    return;
                }
                Log($"pass {pass} done; {walk.Count} deposit(s) failed on PRIDE or UniProt availability, or on a runtime change ({which}); pass {pass + 1} in {_m.PassWaitMinutes:0} min");
                if (!await WaitAsync(TimeSpan.FromMinutes(_m.PassWaitMinutes), ct).ConfigureAwait(false)) return;
            }
        }
        finally
        {
            await EndPublishingAsync(stopPublishing, publisher, ranOut, ct).ConfigureAwait(false);
            try { File.Delete(PidFile); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The driver's end: a publish under way finishes (STOP drains, it never kills a build). A driver that ran out of
    /// queue publishes what still waits at once, since no search competes for the disk any more; one stopped by STOP or
    /// the disk floor leaves it to the next driver, and says so.
    /// </summary>
    private async Task EndPublishingAsync(CancellationTokenSource? stop, Task? publisher, bool ranOut, CancellationToken ct)
    {
        if (publisher is null) return;
        if (_publishRunning) Log("the publish under way finishes before the driver exits");
        stop!.Cancel();
        await publisher.ConfigureAwait(false);
        stop.Dispose();
        if (ct.IsCancellationRequested || AwaitingPublish() is not { Count: > 0 } waiting) return;
        if (!ranOut)
        {
            Log($"{waiting.Count} deposit(s) delivered but not yet published ({Brief(waiting)}): the next driver publishes them first");
            return;
        }
        Log($"queue done: publishing the {waiting.Count} deposit(s) waiting now, not at the schedule");
        try { await PublishIfDueAsync(true, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or OperationCanceledException)
        {
            Log($"PUBLISH could not run: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One walk of <paramref name="queue"/>. True when it ran out; false when STOP, the disk floor or cancellation ended it.</summary>
    private async Task<bool> WalkAsync(IReadOnlyList<QueueEntry> queue, CancellationToken ct)
    {
        // Up to search_slots searches run at once (the user, 2026-10-07: "start a second search immediately following any
        // complete download"). The next deposit is screened, probed and fetched while they run (network-bound), and starts
        // as soon as it is downloaded and a slot is free. With one slot this is the old rhythm: search one, fetch the next.
        int slots = Math.Max(1, _m.SearchSlots);
        var running = new List<(Task Task, string Organism)>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        bool ranOut = false, stopped = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (File.Exists(StopFile)) { stopped = true; break; }
                if (FreeGb() < _m.MinFreeGb) { Log($"free space {FreeGb():0} GB below {_m.MinFreeGb:0} GB floor: stopping"); break; }
                var busy = running.Select(r => r.Organism).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ready = await NextReadyAsync(queue, consumed, busy, ct).ConfigureAwait(false);
                if (ready is null) { stopped = File.Exists(StopFile); ranOut = !ct.IsCancellationRequested && !stopped; break; }
                while (running.Count >= slots)
                {
                    var done = await Task.WhenAny(running.Select(r => r.Task)).ConfigureAwait(false);
                    running.RemoveAll(r => r.Task == done);
                    await done.ConfigureAwait(false);   // a search that threw ends the driver, as it always did
                }
                var (e, profile) = ready.Value;
                // v0.4.10: STOP written while this deposit downloaded or waited for a slot. It is left fetched for the next
                // driver instead of starting one more search: PXD008934 (2026-10-12) finished downloading 85 min after STOP
                // and its 6 h search held the pin swap until it ended.
                if (File.Exists(StopFile))
                {
                    Log($"{e.Accession} fetched, but STOP is present: not searched by this driver; the next one searches it");
                    stopped = true;
                    break;
                }
                // Task.Run: the search begins with the full-file QC, which is synchronous and must not hold up the next fetch.
                running.Add((Task.Run(() => SearchSlotAsync(e, profile, ct), CancellationToken.None), e.Organism));
            }
            if (stopped)
            {
                if (running.Count == 0) Log("STOP file present: finishing (no search under way)");
                else await DownloadWhileDrainingAsync(queue, consumed, running, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // STOP drains: every search under way finishes and is delivered before the walk ends.
            await Task.WhenAll(running.Select(r => r.Task)).ConfigureAwait(false);
        }
        return ranOut;
    }

    private async Task SearchSlotAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        SearchStarted(e.Accession);
        try { await (SearchForTest ?? SearchAndDeliverAsync)(e, profile, ct).ConfigureAwait(false); }
        finally { SearchEnded(e.Accession); }
    }

    /// <summary>Tests only: replaces the probe and the whole-deposit fetch (returns the profile, or null to skip).</summary>
    internal Func<QueueEntry, CancellationToken, Task<Profile?>>? PrepareForTest { get; set; }

    /// <summary>Tests only: replaces the search and its delivery.</summary>
    internal Func<QueueEntry, Profile, CancellationToken, Task>? SearchForTest { get; set; }

    /// <summary>Waits between passes, watching for STOP each minute. False when STOP or cancellation ended the wait.</summary>
    private async Task<bool> WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(StopFile)) { Log("STOP file present: finishing"); return false; }
            var left = until - DateTime.UtcNow;
            try { await Task.Delay(left < TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return !File.Exists(StopFile) && !ct.IsCancellationRequested;
    }

    /// <summary>
    /// Left for a later pass by unavailability: downloads that exhausted their attempts (<c>*_unavailable</c>), a
    /// screen, database or listing call that never got an answer (still <c>probing</c>), or a search the .NET runtime
    /// changed under (<c>search_interrupted</c>, G5).
    /// </summary>
    internal bool Retriable(string acc) =>
        !File.Exists(Path.Combine(Run(acc), "04_search", "provenance.json")) && Settled(acc).Length == 0
        && Entry(acc)?["status"]?.GetValue<string>() is "fetch_unavailable" or "probe_fetch_unavailable" or "probing" or "search_interrupted";

    /// <summary>
    /// A failed probe or whole-deposit fetch. Unavailability that outlasted every attempt (PRIDE down for hours, S52) is
    /// not the deposit's fault: it is left unsettled for a later pass, up to <c>fetch_passes</c> in all, then settled.
    /// Anything else (a checksum mismatch, a 404, a full disk) settles at once, as before.
    /// </summary>
    internal void RecordFetchFailure(string acc, bool probe, Exception ex)
    {
        string what = probe ? "PROBE fetch" : "FETCH";
        string settled = probe ? "probe_fetch_failed" : "fetch_failed";
        string why = FetchStage.Describe(ex);
        if (!FetchStage.IsTransient(ex))
        {
            Log($"{acc} {what} failed: {why}");
            Record(acc, ("status", settled), ("detail", why));
            return;
        }
        int used = (Entry(acc)?["unavailable_passes"]?.GetValue<int>() ?? 0) + 1;
        if (used >= _m.FetchPasses)
        {
            Log($"{acc} {what} failed on availability, pass {used} of {_m.FetchPasses}: settled. {why}");
            Record(acc, ("status", settled), ("detail", why), ("unavailable_passes", used));
            return;
        }
        Log($"{acc} {what} failed on availability, pass {used} of {_m.FetchPasses}: retry next pass. {why}");
        Record(acc, ("status", probe ? "probe_fetch_unavailable" : "fetch_unavailable"), ("detail", why), ("unavailable_passes", used));
    }

    /// <summary>
    /// The next deposit ready to search: screened, probed and fully fetched. In queue order, except that a deposit of a
    /// species no running search has goes first (the user: "just try to alternate species"), because two searches of one
    /// species cannot both extend its spectral library: the second to finish is refused registration, by design. Every
    /// <c>catch_up_every</c>-th start is a catch-up re-search instead, when one is due (D39). Null when nothing is left.
    /// </summary>
    private async Task<(QueueEntry, Profile)?> NextReadyAsync(IReadOnlyList<QueueEntry> queue, HashSet<string> consumed,
        IReadOnlySet<string> busy, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !File.Exists(StopFile))
        {
            bool catchUpTurn = _m.CatchUpEvery > 0 && (_started + 1) % _m.CatchUpEvery == 0;
            var catchUp = catchUpTurn ? CatchUpCandidate(queue, consumed, busy) : null;
            var e = catchUp ?? PickNext(queue, consumed, busy);
            if (e is null) return null;
            consumed.Add(e.Accession);
            if (catchUp is not null && !StartCatchUp(e)) continue;
            Record(e.Accession, ("status", "probing"), ("title", e.Title), ("organism", e.Organism));
            if (await PrepareAsync(e, ct).ConfigureAwait(false) is { } profile)
            {
                _started++;
                return (e, profile);
            }
        }
        return null;
    }

    /// <summary>The screen, the probe and the whole-deposit fetch: the profile to search with, or null when settled otherwise.</summary>
    private async Task<Profile?> PrepareAsync(QueueEntry e, CancellationToken ct)
    {
        if (PrepareForTest is { } prepare) return await prepare(e, ct).ConfigureAwait(false);
        var profile = await ProbeAsync(e, ct).ConfigureAwait(false);
        return profile is not null && await FetchWholeAsync(e, profile, ct).ConfigureAwait(false) ? profile : null;
    }

    /// <summary>
    /// STOP drains (v0.4.10; the user, 2026-10-12: "I don't know why we have to stop downloads"). No new search starts, but
    /// downloading goes on while the searches under way finish: each next deposit is screened, probed and fetched whole as
    /// usual and left <c>fetched</c>. The next driver checks those files against PRIDE's checksums instead of downloading
    /// them again, and searches the deposits first, since they lead the queue. When the last search ends, the download in
    /// flight is cut (its deposit stays unsettled, so the next driver takes it up) and the driver exits: the drain lasts no
    /// longer than the searches it waits for.
    /// </summary>
    private async Task DownloadWhileDrainingAsync(IReadOnlyList<QueueEntry> queue, HashSet<string> consumed,
        List<(Task Task, string Organism)> running, CancellationToken ct)
    {
        Log($"STOP file present: no new search; downloading ahead while the {running.Count} search(es) under way finish");
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var searches = Task.WhenAll(running.Select(r => r.Task));
        var ahead = Task.Run(() => DownloadAheadAsync(queue, consumed, cut.Token), CancellationToken.None);
        await Task.WhenAny(searches, ahead).ConfigureAwait(false);
        if (!ahead.IsCompleted) Log("the searches under way have finished: the download ahead is cut; the next driver goes on with it");
        cut.Cancel();
        var fetched = await ahead.ConfigureAwait(false);
        Log(fetched.Count == 0 ? "downloaded ahead for the next driver: none"
            : $"downloaded ahead for the next driver: {fetched.Count} deposit(s) ({string.Join(", ", fetched)})");
    }

    private async Task<List<string>> DownloadAheadAsync(IReadOnlyList<QueueEntry> queue, HashSet<string> consumed, CancellationToken cut)
    {
        var fetched = new List<string>();
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!cut.IsCancellationRequested)
            {
                if (FreeGb() < _m.MinFreeGb) { Log($"free space {FreeGb():0} GB below {_m.MinFreeGb:0} GB floor: no more downloading ahead"); break; }
                // Queue order only: no catch-up re-search starts here (it moves a delivered search aside), and the species
                // turn is the next driver's to take.
                if (PickNext(queue, consumed, none) is not { } e) break;
                consumed.Add(e.Accession);
                Record(e.Accession, ("status", "probing"), ("title", e.Title), ("organism", e.Organism));
                if (await PrepareAsync(e, cut).ConfigureAwait(false) is not null)
                {
                    fetched.Add(e.Accession);
                    Log($"{e.Accession} downloaded ahead under STOP: left fetched; the next driver searches it");
                }
            }
        }
        catch (Exception) when (cut.IsCancellationRequested)
        {
            // The cut: the deposit in flight keeps its unsettled status (probing or fetching), and a .partial is deleted
            // by the download itself; an Aspera transfer's ascp is killed with it (Aspera.DownloadAsync).
        }
        return fetched;
    }

    /// <summary>The first queued deposit not yet done, preferring one whose species no running search has.</summary>
    private QueueEntry? PickNext(IReadOnlyList<QueueEntry> queue, HashSet<string> consumed, IReadOnlySet<string> busy)
    {
        QueueEntry? first = null;
        foreach (var x in queue)
        {
            if (consumed.Contains(x.Accession)) continue;
            if (File.Exists(Path.Combine(Run(x.Accession), "04_search", "provenance.json"))) { consumed.Add(x.Accession); continue; }
            string settled = Settled(x.Accession);
            if (settled.Length > 0) { Log($"{x.Accession} skipped: {settled} on a previous pass"); consumed.Add(x.Accession); continue; }
            if (!busy.Contains(x.Organism)) return x;
            first ??= x;
        }
        return first;
    }

    /// <summary>
    /// D39's catch-up: a deposit searched under a profile the question no longer names, ordered by gain (the user,
    /// 2026-10-07): rat first, then a deposit the question now has a curated design for, then the oldest. A species no
    /// running search has goes first among them. Null when none is due.
    /// </summary>
    internal QueueEntry? CatchUpCandidate(IReadOnlyList<QueueEntry> queue, ISet<string> consumed, IReadOnlySet<string> busy)
    {
        var picks = new List<(QueueEntry E, int Rank, string When)>();
        foreach (var (acc, node) in State()["datasets"]!.AsObject())
        {
            if (consumed.Contains(acc) || node is not JsonObject o || o["status"]?.GetValue<string>() != "searched") continue;
            string? had = o["profile"]?.GetValue<string>();
            if (had is not null && _q.Profiles.Contains(had)) continue;
            if (!File.Exists(Path.Combine(Run(acc), "04_search", "provenance.json"))) continue;
            var qe = queue.FirstOrDefault(x => x.Accession == acc)
                     ?? (o["organism"]?.GetValue<string>() is { } org ? new QueueEntry(acc, o["title"]?.GetValue<string>() ?? "", org) : null);
            if (qe is null) continue;
            int rank = qe.Organism.Equals("rat", StringComparison.OrdinalIgnoreCase) ? 0
                : _q.Designs?.For(acc) is { } design && File.Exists(design) ? 1 : 2;
            picks.Add((qe, rank, o["updated_utc"]?.GetValue<string>() ?? ""));
        }
        return picks.OrderBy(p => busy.Contains(p.E.Organism) ? 1 : 0).ThenBy(p => p.Rank)
            .ThenBy(p => p.When, StringComparer.Ordinal).Select(p => p.E).FirstOrDefault();
    }

    /// <summary>Moves a catch-up deposit's earlier search aside, as <c>batch research</c> does. False when refused.</summary>
    private bool StartCatchUp(QueueEntry e)
    {
        try
        {
            lock (_gate)
                Research(_stateDir, _runRoot, e.Accession, $"D39 catch-up re-search (one in {_m.CatchUpEvery})", null, _q.Publish?.Manifest, byDriver: true);
            Log($"{e.Accession} RE-SEARCH (catch-up, one in {_m.CatchUpEvery}): earlier search moved aside");
            return true;
        }
        catch (UsageException ex)
        {
            Log($"{e.Accession} catch-up refused: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The screen, the size gates, the probe fetch and its QC. Returns the profile to search with, or null when the
    /// deposit is settled otherwise (or PRIDE could not be reached, which leaves it for the next pass).
    /// </summary>
    public async Task<Profile?> ProbeAsync(QueueEntry e, CancellationToken ct)
    {
        string acc = e.Accession;
        // Screen the LIVE PRIDE record before any download: acquisition, labelling and enrichment are read from every text
        // field (a labelling time course once reached the queue because discovery read only the protocols).
        // The search index is safe here: the screen never reads its organisms, organism parts or diseases, the fields
        // PRIDE's index has carried SDRF headers in (pride 001/002), and the organism comes from the queue, which the
        // census fills from the project record (0.3.2).
        PrideProjectSearchResultLike? rec;
        try
        {
            var hits = await _search.SearchAsync(acc, ct).ConfigureAwait(false);
            rec = hits.FirstOrDefault(h => h.Accession == acc) is { } h ? new PrideProjectSearchResultLike(h) : null;
        }
        catch (Exception ex) when (Envelope.IsUnavailable(ex)) { Log($"{acc} screen: PRIDE unavailable ({ex.Message}); retry next pass"); return null; }
        if (rec is null) { Log($"{acc} screen: PRIDE returned no record; retry next pass"); return null; }

        var acq = AcquisitionClassifier.Classify(rec.Record);
        var route = Router.Assign(acc, acq, new RelevanceResult(RelevanceVerdict.DecidedInclude, "queued"), _q, _profiles);
        // D30/D32: what experiment this is, fact by fact with sources, written at every exit of this stage.
        var xr = new ExperimentRecord(acc);
        xr.Species(e.Organism, "queue (the census reads it from the PRIDE project record)");
        xr.Screen(acq);
        if (acq.Enrichment != "none")
        {
            // Searched, and ANNOTATED: an enrichment is not a whole proteome, and its intensities must never be pooled as one.
            Log($"{acc} screened as enriched ({acq.Enrichment}); searching and annotating: ...{acq.Evidence}...");
            Record(acc, ("enrichment", acq.Enrichment), ("screen_evidence", acq.Evidence));
        }
        if (route.Kind != RouteKind.Search)
        {
            string status = route.Kind switch
            {
                RouteKind.Held => "on_hold_user",
                _ => "waiting_" + Slug(route.Profile ?? route.Reason),
            };
            Log($"{acc} SCREENED OUT ({status}): {route.Reason}");
            Record(acc, ("status", status), ("screen_evidence", acq.Evidence), ("route", route.Reason));
            Experiment(acc, xr, status, route.Reason);
            return null;
        }
        var profile = _profiles[route.Profile!];
        // batch research --profile: the profile asked for, when the question still names it and it takes this deposit.
        if (Entry(acc)?["research_profile"]?.GetValue<string>() is { } asked && asked != profile.Key)
        {
            string? refusal = !_q.Profiles.Contains(asked) || !_profiles.TryGetValue(asked, out var p) ? "the question no longer names it" : p.Accepts.Refusal(acq);
            if (refusal is null) { profile = _profiles[asked]; Log($"{acc} RE-SEARCH under {asked} (routing chose {route.Profile})"); }
            else Log($"{acc} RE-SEARCH asked for {asked}, which cannot take it ({refusal}); searching under {profile.Key} as routed");
        }
        Record(acc, ("profile", profile.Key));
        if (!profile.Databases.ContainsKey(e.Organism))
        {
            Log($"{acc} SKIPPED: {profile.Key} has no database for organism '{e.Organism}'");
            Record(acc, ("status", "skipped_organism"));
            Experiment(acc, xr, "skipped_organism", $"{profile.Key} has no database for '{e.Organism}'");
            return null;
        }
        // A UniProt database is downloaded (once) before any PRIDE download, so an outage costs nothing and is retried.
        if (profile.Databases[e.Organism].UniProt is { } up)
        {
            try
            {
                if (!Search.ProteomeCache.IsCached(profile.Databases[e.Organism], _m.DatabaseDir))
                    Log($"{acc} database: downloading UniProt {up} (reviewed, xml) for {e.Organism}; first use on this machine, takes minutes");
                var (dbPath, retrieval) = await Search.ProteomeCache.ResolveAsync(profile.Databases[e.Organism], _m.DatabaseDir, ct).ConfigureAwait(false);
                Log($"{acc} database: {dbPath} (retrieved {retrieval?["retrieved_utc"]}, sha256 {retrieval?["sha256"]?.GetValue<string>()[..12]})");
            }
            catch (Exception ex) when (Envelope.IsUnavailable(ex)) { Log($"{acc} database: UniProt {up} unavailable ({ex.Message}); retry next pass"); return null; }
        }

        // Size gates BEFORE any download: whole deposits only, never subsampled by file size (S43).
        List<UsefulProteomicsDatabases.PrideArchiveFile> all, listing;
        try { all = await _files.ListFilesAsync(acc, ct).ConfigureAwait(false); }
        catch (Exception ex) when (FetchStage.IsTransient(ex)) { Log($"{acc} size listing failed ({ex.Message}); retry next pass"); return null; }
        listing = all.Where(f => f.FileName.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)).ToList();
        // D28 (D-2): the question's exception for this deposit, if any: its own caps and its own choice of runs.
        var exception = _q.ExceptionFor(acc);
        if (exception is not null)
        {
            int before = listing.Count;
            if (exception.SelectsFiles) listing = listing.Where(f => exception.Keeps(f.FileName)).ToList();
            Log($"{acc} EXCEPTION (question): {exception.Describe()}" + (exception.SelectsFiles ? $"; {listing.Count} of {before} raw files selected" : ""));
            Record(acc, ("exception", exception.Describe()));
            if (listing.Count == 0)
            {
                Log($"{acc} SKIPPED: the question's file selection keeps no raw file");
                Record(acc, ("status", "skipped_selection_empty"));
                Experiment(acc, xr, "skipped_selection_empty", exception.Describe());
                return null;
            }
        }

        // G19: a profile that searches with the deposit's own chemistry reads it before any download, and a deposit it cannot
        // search as read (two proteases with no per-file map, an unknown protease or modification, a label) waits.
        DepositChemistry? chemistry = null;
        if (profile.Chemistry == "deposit")
        {
            DepositChemistry chem;
            try { chem = await ChemistryAsync(acc, all, listing.Select(f => f.FileName).ToList(), profile, ct).ConfigureAwait(false); }
            catch (Exception ex) when (Envelope.IsUnavailable(ex) || FetchStage.IsTransient(ex)) { Log($"{acc} chemistry: PRIDE unavailable ({ex.Message}); retry next pass"); return null; }
            xr.Chemistry(chem);
            chemistry = chem;
            string? wait = chem.Park ?? (chem.Label.Value == "label_free" ? null : $"waiting_{Slug(chem.Label.Value)}_labelling");
            if (wait is not null)
            {
                Log($"{acc} SCREENED OUT ({wait}): chemistry {Describe(chem)}");
                Record(acc, ("status", wait), ("detail", Describe(chem)));
                Experiment(acc, xr, wait, Describe(chem));
                return null;
            }
            Log($"{acc} chemistry: {Describe(chem)}");
        }

        // The same raw files already searched under another accession: refused before any download (PXR-A9).
        // The deposit being searched counts too: it is fetched in full, and the next one is screened alongside it (PXR-A21).
        var searching = Searching;
        var dup = DuplicateScreen.Check(acc, listing.Select(f => (f.FileName, f.FileSizeBytes)).ToList(),
            DuplicateScreen.Index(DuplicateRoots(), searching.Count == 0 ? null : searching.Select(Run).ToArray()));
        string When(string other) => searching.Contains(other) ? "being searched now" : "already searched";
        if (dup.DuplicateOf is { } original)
        {
            Log($"{acc} SCREENED OUT (excluded_duplicate): all {listing.Count} raw files (name and size) are in {original}, {When(original)}");
            Record(acc, ("status", "excluded_duplicate"), ("duplicate_of", original));
            Experiment(acc, xr, "excluded_duplicate", $"all {listing.Count} raw files are in {original}");
            return null;
        }
        foreach (var (other, shared) in dup.Partial)
            Log($"{acc} shares {shared} of {listing.Count} raw files (name and size) with {other}, {When(other)}; not a duplicate, continuing");

        var sizes = listing.Select(f => f.FileSizeBytes / 1e6).OrderBy(x => x).ToList();
        var dep = DepositFor(acc, profile);
        var oversize = sizes.Where(s => s > dep.MaxFileMb).ToList();
        if (sizes.Count > dep.MaxFiles || oversize.Count > 0)
        {
            string why = sizes.Count > dep.MaxFiles ? $"{sizes.Count} raw files > {dep.MaxFiles}" : $"{oversize.Count} file(s) above {dep.MaxFileMb} MB";
            Log($"{acc} DEFERRED: {why}. A subset would be a design decision made by file size (S43); it waits for SDRF -> experimental-design selection.");
            Record(acc, ("status", "deferred_needs_design_selection"), ("n_files", sizes.Count), ("total_gb", Math.Round(sizes.Sum() / 1000, 1)), ("detail", why));
            Experiment(acc, xr, "deferred_needs_design_selection", why);
            return null;
        }
        if (sizes.Count > 0 && dep.DeferIfMedianFileMb > 0 && sizes[sizes.Count / 2] > dep.DeferIfMedianFileMb)
        {
            double med = sizes[sizes.Count / 2];
            Log($"{acc} DEFERRED: median file {med:0} MB > {dep.DeferIfMedianFileMb} MB ({sizes.Count} files, {sizes.Sum() / 1000:0.0} GB). EBI drops these mid-transfer and a retry re-pays from zero (S40).");
            Record(acc, ("status", "deferred_large_files"), ("median_file_mb", Math.Round(med)), ("n_files", sizes.Count));
            Experiment(acc, xr, "deferred_large_files", $"median file {med:0} MB");
            return null;
        }

        string d = Run(acc);
        try
        {
            await FetchStage.RunAsync(FetchRequestFor(acc, Pick.ProbeSpread, dep), _files, ct, m => Log($"{acc} PROBE fetch {m}")).ConfigureAwait(false);
        }
        // Not the caller's cancellation: an HttpClient timeout is a TaskCanceledException too, and it is unavailability.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFetchFailure(acc, probe: true, ex);
            return null;
        }
        var (report, allPass) = QcStage.Run(Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "02b_qc_probe"), profile, _m, ParamsFile(acc, profile), _runDate);
        xr.Probe(report);
        if (RoutedByReporters(acc, report, "PROBE"))
        {
            string status = Entry(acc)?["status"]?.GetValue<string>() ?? "isobaric";
            Experiment(acc, xr, status, "isobaric reporter ions in the probe files' own spectra");
            return null;
        }
        var excl = QcExclusion(report, profile.Qc);
        bool ok = allPass || excl is { Count: > 0 };
        // MS2 read in the ion trap: a question that names a low-res profile (no Orbitrap-HCD gate, per-file fragmentation)
        // searches it there instead of skipping it (the user, 2026-10-07: "Do whatever you can"). The screen never routes to
        // that profile directly; only the spectra can.
        if (!ok && OnlyLowRes(report, profile.Qc) && LowResProfile(acq, profile) is { } low)
        {
            Log($"{acc} PROBE qc: MS2 not read in the Orbitrap (low_res_ms2); routing to {low.Key}");
            profile = low;
            Record(acc, ("profile", profile.Key), ("routed_low_res", true));
            (report, allPass) = QcStage.Run(Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "02b_qc_probe"), profile, _m, ParamsFile(acc, profile), _runDate);
            xr.Probe(report);
            excl = QcExclusion(report, profile.Qc);
            ok = allPass || excl is { Count: > 0 };
        }
        if (!allPass && ok) Log($"{acc} PROBE qc: {string.Join(", ", excl!)} fail only on excludable reasons; continuing (decided at full QC)");
        Log($"{acc} PROBE qc {(ok ? "ok" : "FAILED")} " + string.Join("; ", report.Take(4).Select(kv =>
            $"{kv.Key}: hcd={kv.Value!["fraction_orbitrap_hcd"]} ms2={kv.Value["ms2"]} min={kv.Value["run_minutes"]}")));
        if (!ok)
        {
            Record(acc, ("status", "skipped_acquisition"), ("detail", Reasons(report)));
            bool fragmentation = report.Any(kv => kv.Value!["fail_reasons"]?.AsArray().Any(r => r?.GetValue<string>() == Qc.SpectraQc.UnsupportedFragmentation) == true);
            Experiment(acc, xr, "skipped_acquisition", Reasons(report), fragmentation ? Qc.SpectraQc.UnsupportedFragmentation : null);
            return null;
        }
        var ms2 = report.Select(kv => kv.Value!["ms2"]!.GetValue<int>()).Where(x => x > 0).ToList();
        if (dep.MaxEstimatedMs2 > 0 && ms2.Count > 0 && sizes.Count > 0)
        {
            double est = ms2.Average() * sizes.Count;
            if (est > dep.MaxEstimatedMs2)
            {
                Log($"{acc} DEFERRED: estimated {est / 1e6:0.00}M MS2 ({sizes.Count} files x {ms2.Average():N0}) > {dep.MaxEstimatedMs2 / 1e6:0.0}M.");
                Record(acc, ("status", "deferred_search_too_large"), ("est_ms2", Math.Round(est)), ("n_files", sizes.Count));
                Experiment(acc, xr, "deferred_search_too_large", $"estimated {est / 1e6:0.00}M MS2");
                return null;
            }
        }
        if (profile.ProbeSearch && !await ProbeSearchAsync(e, profile, acq, chemistry, report, xr, ct).ConfigureAwait(false))
            return null;
        Experiment(acc, xr, "probed", $"to be searched under {profile.Key}");
        return profile;
    }

    /// <summary>
    /// True when the files fail on <c>low_res_ms2</c> and on nothing else that the gates do not already allow (an
    /// excludable reason such as one blank run). A file that is unreadable or has too few MS2 stays a skip.
    /// </summary>
    internal static bool OnlyLowRes(JsonObject report, QcGates gates)
    {
        var reasons = report.SelectMany(kv => kv.Value!["fail_reasons"]?.AsArray().Select(r => r!.GetValue<string>()) ?? Enumerable.Empty<string>()).ToList();
        return reasons.Contains(Qc.SpectraQc.LowResMs2)
            && reasons.All(r => r == Qc.SpectraQc.LowResMs2 || gates.Excludable.Contains(r));
    }

    /// <summary>The question's low-res profile for this deposit: available, after <paramref name="current"/> in the
    /// question's order, without the Orbitrap-HCD gate, writing fragmentation per file, and accepting the deposit.</summary>
    internal Profile? LowResProfile(Acquisition acq, Profile current) =>
        _q.Profiles.SkipWhile(k => k != current.Key).Skip(1)
            .Select(k => _profiles.TryGetValue(k, out var p) ? p : null)
            .FirstOrDefault(p => p is { Status: ProfileStatus.Available, Fragmentation: "per_file" } && p.Qc.MinFractionOrbitrapHcd == 0
                                 && p.Accepts.Refusal(acq) is null);

    /// <summary>The probe search, for tests: (profile, machine, spectra, chemistry, fragmentation, outDir) -> AllPSMs.psmtsv.</summary>
    internal Func<Profile, Machine, IReadOnlyList<string>, DepositChemistry?, IReadOnlyDictionary<string, Qc.FragmentationCall>?, string, CancellationToken, Task<string>>
        ProbeSearchRunner { get; init; } = Search.ProbeSearch.RunAsync;

    /// <summary>
    /// D32/D34/D35 slice 3: a short search of the probe files measures each run's species, cysteine share and phospho/GG
    /// share. A measurement that contradicts the record parks the deposit and is announced; a measured enrichment is
    /// recorded beside the screen's. False when the deposit stops here.
    /// </summary>
    private async Task<bool> ProbeSearchAsync(QueueEntry e, Profile profile, Acquisition acq, DepositChemistry? chemistry,
        JsonObject probeQc, ExperimentRecord xr, CancellationToken ct)
    {
        string acc = e.Accession, d = Run(acc);
        var spectra = Directory.EnumerateFiles(Path.Combine(d, "02_fetch", "spectra"), "*.raw")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, Qc.RunMeasures> runs;
        try
        {
            var fragmentation = Search.SearchStage.FragmentationFromQc(probeQc, spectra, profile);
            string psms = await ProbeSearchRunner(profile, _m, spectra, chemistry, fragmentation, Path.Combine(d, "02c_probe_search"), ct).ConfigureAwait(false);
            runs = Qc.ProbeMeasures.FromAllPsms(psms);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log($"{acc} PROBE SEARCH FAILED ({ex.GetType().Name}): {ex.Message}");
            Record(acc, ("status", "probe_search_failed"), ("detail", ex.Message));
            Experiment(acc, xr, "probe_search_failed", ex.Message);
            return false;
        }
        xr.ProbeSearch(runs);
        bool expectCarbamidomethyl = chemistry is null || chemistry.CysMods.Any(c => c.Name == "Carbamidomethyl" && c.Fixed);
        var verdict = Qc.ProbeJudge.Judge(runs, Qc.ProbeJudge.SpeciesName(e.Organism), expectCarbamidomethyl, acq.Enrichment != "none");
        foreach (string note in verdict.Notes) Log($"{acc} PROBE SEARCH: {note}");
        Log($"{acc} PROBE SEARCH " + string.Join("; ", runs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            $"{kv.Key}: psms={kv.Value.Psms} species={string.Join("/", kv.Value.SpeciesPsms.OrderByDescending(s => s.Value).Select(s => $"{s.Key}:{s.Value}"))} cys={kv.Value.CysteineShare:P1}")));
        if (verdict.MeasuredEnrichment is { } measured)
        {
            // D34: measured beside the screen's call, never overwriting it here; the record says both.
            xr.Fact("enrichment_measured", measured, "probe_search", string.Join("; ", verdict.Notes.Where(n => n.StartsWith("measured", StringComparison.Ordinal))));
            Record(acc, ("enrichment_measured", measured));
        }
        if (verdict.Parks.Count == 0) return true;
        foreach (var extra in verdict.Parks.Skip(1)) DecisionsNeeded.Announce(_runRoot, acc, extra.Park, extra.Evidence);
        var first = verdict.Parks[0];
        Log($"{acc} PROBE SEARCH PARKS ({first.Park}): {first.Evidence}");
        Record(acc, ("status", first.Park), ("detail", first.Evidence));
        Experiment(acc, xr, first.Park, first.Evidence);
        return false;
    }

    /// <summary>
    /// D30/D32/D34: writes the deposit's experiment record and notes, and announces a park that needs a person
    /// (<see cref="DecisionsNeeded"/>; <paramref name="park"/> overrides <paramref name="outcome"/> as the park's name).
    /// Never fails the batch: a record that cannot be written is logged.
    /// </summary>
    private void Experiment(string acc, ExperimentRecord xr, string outcome, string? detail, string? park = null)
    {
        try
        {
            var decision = DecisionsNeeded.Announce(_runRoot, acc, park ?? outcome, detail);
            if (decision is not null) Log($"{acc} DECISION NEEDED ({park ?? outcome}): {decision.Question} Listed in {DecisionsNeeded.FileName}.");
            xr.Write(Run(acc), outcome, detail, decision);
        }
        catch (IOException ex) { Log($"{acc} experiment record not written: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Log($"{acc} experiment record not written: {ex.Message}"); }
    }

    /// <summary>Where a whole-deposit fetch puts the raw files in <c>02_fetch/spectra</c> that its selection does not name.</summary>
    internal const string NotSelectedFolder = "not_selected";

    /// <summary>
    /// Moves every .raw in <paramref name="spectraDir"/> that <paramref name="selected"/> does not name, with its
    /// <c>.partial</c> files, into the sibling <see cref="NotSelectedFolder"/>, and returns the .raw names moved. QC and
    /// the search read every .raw in the folder, so a file left by an earlier attempt under another selection would be
    /// counted, checked and searched with this one (PXD057407, 2026-10-12: five phospho runs from the 10-08 attempt
    /// beside the ten global runs of PXR-A68 gave "15 of 10" and fetch_failed). Cleanup deletes the folder's files.
    /// </summary>
    internal static IReadOnlyList<string> SetAsideUnselected(string spectraDir, IReadOnlyCollection<string> selected)
    {
        var moved = new List<string>();
        if (!Directory.Exists(spectraDir)) return moved;
        var keep = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        string aside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, NotSelectedFolder);
        foreach (string path in Directory.EnumerateFiles(spectraDir).OrderBy(p => p, StringComparer.Ordinal).ToList())
        {
            string name = Path.GetFileName(path);
            string? rawName = name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) ? name
                : name.IndexOf(".raw.partial", StringComparison.OrdinalIgnoreCase) is int i and >= 0 ? name[..(i + 4)] : null;
            if (rawName is null || keep.Contains(rawName)) continue;
            Directory.CreateDirectory(aside);
            File.Move(path, Path.Combine(aside, name), overwrite: true);
            if (rawName.Length == name.Length) moved.Add(name);
        }
        return moved;
    }

    /// <summary>The whole deposit, or nothing: anything short of every listed file is a failure that says what it got (S43).</summary>
    private async Task<bool> FetchWholeAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        string acc = e.Accession;
        JsonObject manifest;
        try
        {
            manifest = await FetchStage.RunAsync(FetchRequestFor(acc, Pick.All, DepositFor(acc, profile)), _files, ct, m => Log($"{acc} FETCH {m}")).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFetchFailure(acc, probe: false, ex);
            return false;
        }
        int listed = manifest["rest_raw_count"]!.GetValue<int>();
        string spectra = Path.Combine(Run(acc), "02_fetch", "spectra");
        var selected = manifest["files"]?.AsArray().Select(f => f?["name"]?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        if (selected.Count > 0 && SetAsideUnselected(spectra, selected) is { Count: > 0 } aside)
        {
            Log($"{acc} FETCH set aside {aside.Count} raw file(s) this selection does not name, left by an earlier attempt: "
                + $"{string.Join(", ", aside)} -> 02_fetch/{NotSelectedFolder} (deleted at cleanup)");
            Record(acc, ("set_aside", new JsonArray(aside.Select(n => (JsonNode?)n).ToArray())));
        }
        int got = Directory.EnumerateFiles(spectra, "*.raw").Count();
        Log($"{acc} FETCH raw_files={got} of {listed}");
        if (listed > 0 && got == listed) { Record(acc, ("status", "fetched"), ("n_raw", got), ("n_listed", listed)); return true; }
        Log($"{acc} FETCH INCOMPLETE: {got} of {listed} raw files -- not searched (whole deposits only)");
        Record(acc, ("status", "fetch_failed"), ("n_raw", got), ("n_listed", listed));
        return false;
    }

    private async Task SearchAndDeliverAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        string acc = e.Accession, d = Run(acc);
        var (report, allPass) = QcStage.Run(Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "02b_qc"), profile, _m, ParamsFile(acc, profile), _runDate);
        if (RoutedByReporters(acc, report, "QC(full)")) return;
        var excl = new List<string>();
        if (!allPass)
        {
            var x = QcExclusion(report, profile.Qc);
            if (x is null || x.Count == 0)
            {
                Log($"{acc} QC(full) failed: {Reasons(report)}");
                Record(acc, ("status", "skipped_acquisition_full"), ("detail", Reasons(report)));
                return;
            }
            excl = x;
            Log($"{acc} QC EXCLUDED {excl.Count} of {report.Count} file(s) on {string.Join("/", profile.Qc.Excludable)}: {string.Join(", ", excl)}");
            Record(acc, ("qc_excluded", new JsonArray(excl.Select(n => (JsonNode?)n).ToArray())), ("qc_excluded_of", report.Count));
        }

        bool library = _libraryOk;
        Log($"{acc} SEARCH starting (profile={profile.Key}, organism={e.Organism}, library={library})");
        Record(acc, ("status", "searching"));
        var overlays = _q.Overlays.TryGetValue(e.Organism, out var ov) ? ov : Array.Empty<string>();
        string? curated = _q.Designs?.For(acc);
        if (curated is not null && File.Exists(curated) && profile.Design != "sdrf")
            Log($"{acc} note: the question has a design for it ({curated}), but {profile.Key} does not use designs; searching without it");
        DepositChemistry? chemistry = null;
        if (profile.Chemistry == "deposit" && !_chemistry.TryGetValue(acc, out chemistry))
        {
            // A restart since the probe lost the cache: read it again, from the same sources.
            try
            {
                var all = await _files.ListFilesAsync(acc, ct).ConfigureAwait(false);
                chemistry = await ChemistryAsync(acc, all, all.Where(f => f.FileName.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)).Select(f => f.FileName).ToList(), profile, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Envelope.IsUnavailable(ex) || FetchStage.IsTransient(ex))
            {
                Log($"{acc} chemistry: PRIDE unavailable before the search ({ex.Message}); retry next pass");
                Record(acc, ("status", "search_interrupted"), ("detail", "chemistry could not be read before the search"));
                return;
            }
        }
        var req = new SearchRequest(profile, e.Organism, _m, Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "04_search"),
            Path.Combine(d, "02b_qc"), overlays, excl, _runDate, acc, UseLibrary: library,
            CuratedDesign: curated, DesignConditionColumns: _q.Designs is { ConditionColumns.Count: > 0 } ds ? ds.ConditionColumns : null, Chemistry: chemistry);
        string runtimeRoot = Search.DotnetRuntimes.Root(_m.DotnetRoot);
        var runtimesBefore = Search.DotnetRuntimes.Snapshot(runtimeRoot);
        SearchOutcome outcome;
        try
        {
            outcome = await SearchStage.RunAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (InterruptedByRuntime(acc, runtimesBefore, runtimeRoot)) return;
            // The stage threw: before MetaMorpheus ran (setup) or after it (a bug in our own bookkeeping). Say which
            // could not be told apart from the message alone, so keep the type and where the output stands.
            bool ran = Directory.Exists(Path.Combine(d, "04_search", "mm"));
            Log($"{acc} SEARCH STAGE FAILED ({ex.GetType().Name}, MetaMorpheus {(ran ? "HAD run" : "had not run")}): {ex.Message}");
            Record(acc, ("status", "search_failed"), ("detail", $"{ex.GetType().Name}: {ex.Message}"), ("metamorpheus_ran", ran));
            return;
        }
        if (!outcome.Success && InterruptedByRuntime(acc, runtimesBefore, runtimeRoot)) return;
        var prov = JsonNode.Parse(File.ReadAllText(outcome.ProvenanceFile))!;
        var lib = prov["spectral_library"];
        Log($"{acc} SEARCH rc={outcome.ExitCode} success={outcome.Success} psms={outcome.Psms1Pct} rate={prov["id_rate"]?["rate"]} lib={lib?["mode"]}/v{lib?["written"]?["version"]}");
        Record(acc, ("status", outcome.Success ? "searched" : "search_failed"), ("search_rc", outcome.ExitCode),
            ("psms_1pct", outcome.Psms1Pct), ("id_rate", prov["id_rate"]?["rate"]?.DeepClone()), ("spectral_library", lib?.DeepClone()));
        if (outcome.Success)
        {
            QcPayload(acc, profile);
            Cleanup(acc);
            await DeliverAsync(e, profile, ct).ConfigureAwait(false);
        }
        else if (library && lib?["mode"]?.GetValue<string>() == "update" && !outcome.TimedOut)
        {
            // A failure while updating the library may be the library's fault; stop feeding it until someone looks (G46).
            // A timeout is not evidence against the library (S60), so it never disables it.
            _libraryOk = false;
            Log($"{acc} SEARCH FAILED IN UPDATE MODE - disabling the spectral library for the rest of this batch. Raw files kept. INVESTIGATE.");
            Record(acc, ("library_disabled_after_failure", true));
        }
    }

    /// <summary>
    /// qc's payload for the search (stage 05_qc), and qc's validate + render when the machine names a qc_python. Never
    /// fatal: a payload that is rejected, unrenderable or unbuildable is recorded and the deposit is still delivered.
    /// </summary>
    private void QcPayload(string acc, Profile profile)
    {
        string d = Run(acc);
        try
        {
            var o = QcPayloadStage.Run(Path.Combine(d, "04_search"), Path.Combine(d, "02b_qc"), Path.Combine(d, "05_qc"), acc, _runDate,
                _m.WorkRoot, ParamsFile(acc, profile), _m.QcPython);
            Log($"{acc} QC-PAYLOAD files={o.Files} pg_quantified={o.ProteinGroupsQuantified?.ToString() ?? "n/a"} validate={o.Validate} render={(o.Render is null ? "not run" : o.FindingsFile is null ? "FAILED" : "ok")}");
            Record(acc, ("qc_payload", o.PayloadFile), ("qc_payload_validate", o.Validate), ("qc_findings", o.FindingsFile),
                ("qc_payload_flags", new JsonArray(o.Flags.Select(f => (JsonNode?)f).ToArray())));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"{acc} QC-PAYLOAD FAILED ({ex.GetType().Name}): {ex.Message}; delivering anyway");
            Record(acc, ("qc_payload_error", $"{ex.GetType().Name}: {ex.Message}"), ("qc_payload_flags", new JsonArray("qc_payload_failed")));
        }
    }

    private void Cleanup(string acc)
    {
        try
        {
            var r = CleanupStage.Run(Run(acc), _m.WorkRoot);
            Log($"{acc} CLEANUP files={r["files"]} freed_gb={r["gb"]}");
            Record(acc, ("deleted_files", r["files"]!.DeepClone()), ("cleanup_gb", r["gb"]!.DeepClone()));
        }
        catch (Exception ex) when (ex is UsageException or IOException)
        {
            Log($"{acc} CLEANUP failed: {ex.Message}");
            Record(acc, ("cleanup_error", ex.Message));
        }
    }

    /// <summary>Finish anything a previous driver searched but never cleaned up or delivered. A skip that looks like success must not strand a dataset.</summary>
    private async Task ReconcileAsync(IReadOnlyList<QueueEntry> queue, CancellationToken ct)
    {
        RecoverInterruptedSearches(queue);
        string manifest = _q.Publish?.Manifest is { } mf && File.Exists(mf) ? File.ReadAllText(mf) : "";
        foreach (var e in queue)
        {
            if (!SearchedOk(e.Accession)) continue;
            string d = Run(e.Accession);
            bool rawsLeft = Directory.Exists(Path.Combine(d, "02_fetch", "spectra")) && Directory.EnumerateFiles(Path.Combine(d, "02_fetch", "spectra"), "*.raw").Any();
            bool cleaned = File.Exists(Path.Combine(d, "09_cleanup", "provenance.json"));
            bool delivered = Delivered(manifest, e.Accession, Entry(e.Accession));
            var todo = new List<string>();
            if (rawsLeft && !cleaned) todo.Add("cleanup");
            if (_q.Publish?.Manifest is not null && !delivered) todo.Add("ingest");
            if (todo.Count == 0) continue;
            Log($"{e.Accession} RECONCILE: previous run left [{string.Join(", ", todo)}] undone");
            if (todo.Contains("cleanup")) Cleanup(e.Accession);
            if (todo.Contains("ingest"))
            {
                string key = Entry(e.Accession)?["profile"]?.GetValue<string>() ?? _q.Profiles[0];
                await DeliverAsync(e, _profiles[key], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// In the manifest AND not refused by ingest. The entry is appended before <c>datarepo ingest</c> runs, so the manifest
    /// alone says nothing about ingest: PXD075372 (ingest_rc 1, 2026-09-30) looked delivered to every later pass. No
    /// <c>ingest_rc</c> means ingest never ran here (no datarepo on the machine, or the Python runner delivered it).
    /// </summary>
    internal static bool Delivered(string manifestText, string acc, JsonObject? entry) =>
        (manifestText.Contains($"accession: {acc}\n") || manifestText.Contains($"accession: {acc}\r"))
        && entry?["ingest_rc"]?.GetValue<int>() is null or 0;

    // ------------------------------------------------------------------ delivery

    private readonly SemaphoreSlim _delivering = new(1, 1);

    /// <summary>One delivery at a time: two searches may finish together, and the manifest, the store and the publish
    /// script are shared.</summary>
    private async Task DeliverAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        await _delivering.WaitAsync(ct).ConfigureAwait(false);
        try { await DeliverOneAsync(e, profile, ct).ConfigureAwait(false); }
        finally { _delivering.Release(); }
    }

    private async Task DeliverOneAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        if (_q.Publish?.Manifest is not { } manifest) return;
        try
        {
            ManifestEntry.EnsureExists(manifest, _q.Name, _runRoot);
            ManifestEntry.Append(manifest, e, profile, Run(e.Accession), Entry(e.Accession), _m.WorkRoot);
            Log($"{e.Accession} MANIFEST entry appended");
            var qcNames = JsonNode.Parse(File.ReadAllText(Path.Combine(Run(e.Accession), "02b_qc", "qc_report.json")))!.AsObject().Select(kv => kv.Key);
            if (MixedSamples.Detect(qcNames) is { } mixed)
                Log($"{e.Accession} POSSIBLY MIXED SAMPLES (flagged in the manifest; curate run_enrichment): {MixedSamples.Describe(mixed)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Log($"{e.Accession} MANIFEST failed: {ex.Message}"); Record(e.Accession, ("ingest", "manifest_failed")); return; }
        if (_m.DataRepo is null) return;
        // G18: the manifest entry already exists, and an entry with no ingest_rc reads as delivered elsewhere. Mark the
        // ingest as begun, so a driver that dies inside it leaves the deposit undelivered and the next start re-ingests.
        Record(e.Accession, ("ingest_rc", -1), ("ingest_out", $"ingest started {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} and has not finished"));
        int rc;
        if (_ingestJson)
        {
            // datarepo 1.0+: one JSON envelope on stdout, the human report on stderr (dataRepo 010/014, our 011).
            var argv = new[] { _m.DataRepo, "ingest", manifest, e.Accession, "--json" };
            var (exit, stdout, stderr) = await RunProcessFullAsync(argv, TimeSpan.FromHours(3), ct).ConfigureAwait(false);
            string full = KeepOutput(OutputDir, e.Accession, "ingest", argv, exit, stdout, stderr, DateTime.UtcNow);
            var o = IngestEnvelope.Read(stdout, e.Accession, exit);
            rc = o.Rc;
            string report = Tail(stderr);
            Log($"{e.Accession} INGEST rc={rc} status={o.Status ?? "?"}{(o.BundleId is null ? "" : $" bundle={o.BundleId}")}{(o.Mismatches is > 0 ? $" mismatches={o.Mismatches}" : "")}"
                + (o.Reasons.Count > 0 ? $" reasons: {string.Join(" | ", o.Reasons)}" : "") + $" (full output {full})\n{report}");
            Record(e.Accession, ("ingest_rc", rc), ("ingest_exit_code", exit), ("ingest_status", o.Status), ("bundle_id", o.BundleId),
                ("ingest_reasons", new JsonArray(o.Reasons.Select(r => (JsonNode?)r).ToArray())), ("ingest_out", report), ("ingest_log", full));
        }
        else
        {
            var argv = new[] { _m.DataRepo, "ingest", manifest, e.Accession };
            var (exit, stdout, stderr) = await RunProcessFullAsync(argv, TimeSpan.FromHours(3), ct).ConfigureAwait(false);
            string full = KeepOutput(OutputDir, e.Accession, "ingest", argv, exit, stdout, stderr, DateTime.UtcNow);
            rc = exit;
            string tail = Tail((stdout + stderr).Trim());
            Log($"{e.Accession} INGEST rc={rc} (full output {full})\n{tail}");
            Record(e.Accession, ("ingest_rc", rc), ("ingest_out", tail), ("ingest_log", full));
        }
        if (rc != 0 || _q.Publish.Command.Count == 0) return;
        // D41 rule 6: publishing is scheduled (PublishLoopAsync), no longer run after each ingest. A new delivery waits for
        // the next scheduled publish; an earlier publish result belongs to the bundle this ingest replaced.
        Record(e.Accession, ("publish_rc", null), ("publish_out", null), ("publish_log", null));
        Log($"{e.Accession} PUBLISH waits for the next scheduled publish ({PublishWhen()})");
    }

    // ------------------------------------------------------------------ publishing (D41 rule 6)

    /// <summary>
    /// The deposits delivered and not yet served: <c>ingest_rc</c> 0 and no successful publish since (<c>publish_rc</c>
    /// missing or not 0). The question's publish command serves everything ingested, so one success serves them all.
    /// </summary>
    internal IReadOnlyList<string> AwaitingPublish() =>
        State()["datasets"]!.AsObject()
            .Where(kv => kv.Value is JsonObject o && o["ingest_rc"] is JsonValue i && i.TryGetValue<int>(out int irc) && irc == 0
                         && !(o["publish_rc"] is JsonValue p && p.TryGetValue<int>(out int prc) && prc == 0))
            .Select(kv => kv.Key).OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// <summary>When the last publish started (state.json <c>publish.last_started_utc</c>); null before the first.</summary>
    internal DateTime? LastPublishStarted() =>
        State()["publish"]?["last_started_utc"]?.GetValue<string>() is { } s
        && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;

    /// <summary>A publish is due when something waits and <paramref name="everyHours"/> have passed since the last one started.</summary>
    internal static bool PublishDue(DateTime nowUtc, DateTime? lastStartedUtc, double everyHours, int waiting) =>
        waiting > 0 && (lastStartedUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(everyHours));

    private string PublishWhen() =>
        _q.Publish is not { } p ? "no publish command"
        : LastPublishStarted() is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(p.EveryHours)
            ? $"not before {last + TimeSpan.FromHours(p.EveryHours):yyyy-MM-dd HH:mm} UTC, every {p.EveryHours:0.#} h"
            : "within a minute";

    private bool Publishes => _q.Publish is { Manifest: not null, Command.Count: > 0 } && _m.DataRepo is not null;

    private volatile bool _publishRunning;

    /// <summary>
    /// Runs the question's publish command when it is due (or at once with <paramref name="now"/>, when something waits).
    /// It holds the delivery lock, so no ingest writes the store under a build. A failed publish is logged, never fatal:
    /// the bundles exist, and a stale site is a warning, not a reason to stop. The deposits it served are recorded only
    /// when it succeeds; after a failure they wait for the next one. Returns the exit code, or null when nothing ran.
    /// </summary>
    internal async Task<int?> PublishIfDueAsync(bool now, CancellationToken ct)
    {
        if (!Publishes) return null;
        var pub = _q.Publish!;
        if (!now && !PublishDue(DateTime.UtcNow, LastPublishStarted(), pub.EveryHours, AwaitingPublish().Count)) return null;
        await _delivering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var waiting = AwaitingPublish();   // again, under the lock: an ingest may have finished meanwhile
            if (waiting.Count == 0) return null;
            _publishRunning = true;
            DateTime started = DateTime.UtcNow;
            RecordPublish(("last_started_utc", started.ToString("o")), ("waiting", Names(waiting)));
            Log($"PUBLISH start: {waiting.Count} deposit(s) delivered since the last successful publish ({Brief(waiting)}); limit {pub.TimeoutHours:0.#} h, free {FreeGb():0} GB");
            // G38 (promised to dataRepo, our 032): the whole output goes to a file; batch.log and state keep only the tail.
            var argv = PublishArgv(pub.Command, pub.Manifest!, _m.DataRepo!);
            var (rc, stdout, stderr) = await RunProcessFullAsync(argv, TimeSpan.FromHours(pub.TimeoutHours), ct).ConfigureAwait(false);
            DateTime finished = DateTime.UtcNow;
            string full = KeepOutput(OutputDir, _q.Name, "publish", argv, rc, stdout, stderr, finished);
            string tail = Tail((stdout + stderr).Trim());
            Log($"PUBLISH rc={rc} after {(finished - started).TotalMinutes:0} min{(rc == 0 ? $", served {waiting.Count}" : $"; {waiting.Count} still wait for the next one")} (full output {full})\n{tail}");
            RecordPublish(("last_finished_utc", finished.ToString("o")), ("last_rc", rc), ("last_log", full), ("waiting", null),
                rc == 0 ? ("last_served", Names(waiting)) : ("last_failed", Names(waiting)));
            if (rc == 0) RecordAll(waiting, ("publish_rc", 0), ("publish_out", tail), ("publish_log", full), ("published_utc", finished.ToString("o")));
            else RecordAll(waiting, ("publish_rc", rc), ("publish_out", tail), ("publish_log", full));
            return rc;
        }
        finally { _publishRunning = false; _delivering.Release(); }

        static JsonArray Names(IReadOnlyList<string> a) => new(a.Select(x => (JsonNode?)x).ToArray());
    }

    private static string Brief(IReadOnlyList<string> accs) => string.Join(", ", accs.Take(8)) + (accs.Count > 8 ? $", +{accs.Count - 8} more" : "");

    /// <summary>Checks once a minute until <paramref name="stop"/>; a publish under way is never stopped by it (STOP drains).</summary>
    private async Task PublishLoopAsync(CancellationToken stop, CancellationToken ct)
    {
        while (!stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { await PublishIfDueAsync(false, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
            {
                Log($"PUBLISH could not run: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Writes fields of state.json's top-level <c>publish</c> record.</summary>
    private void RecordPublish(params (string Key, JsonNode? Value)[] fields)
    {
        lock (_gate)
        {
            var st = State();
            if (st["publish"] is not JsonObject p) st["publish"] = p = new JsonObject();
            foreach (var (k, v) in fields) p[k] = v?.DeepClone();
            Save(st);
        }
    }

    /// <summary>The same fields on several deposits' entries, in one write.</summary>
    private void RecordAll(IEnumerable<string> accs, params (string Key, JsonNode? Value)[] fields)
    {
        lock (_gate)
        {
            var st = State();
            var ds = st["datasets"]!.AsObject();
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
            foreach (string acc in accs)
            {
                if (ds[acc] is not JsonObject e) continue;
                foreach (var (k, v) in fields) e[k] = v?.DeepClone();
                e["updated_utc"] = now;
            }
            Save(st);
        }
    }

    /// <summary>Where each ingest's and publish's whole output is kept (G38).</summary>
    private string OutputDir => Path.Combine(_stateDir, "outputs");

    /// <summary>
    /// G38: writes a step's whole stdout and stderr, never cut, to <c>&lt;dir&gt;/&lt;accession&gt;_&lt;step&gt;_&lt;utc&gt;.log</c>
    /// and returns the path. batch.log and state.json keep only the tail, which loses a .NET exception's type and message
    /// (PXR-R14). A file that cannot be written is reported in the returned text, never thrown: the step itself succeeded.
    /// </summary>
    internal static string KeepOutput(string dir, string accession, string step, IReadOnlyList<string> argv, int rc,
        string stdout, string stderr, DateTime utc)
    {
        string path = Path.Combine(dir, $"{accession}_{step}_{utc:yyyyMMdd'T'HHmmss'Z'}.log");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                $"# {step} of {accession}, finished {utc:yyyy-MM-dd'T'HH:mm:ss'Z'}, exit code {rc}\n" +
                $"# command: {string.Join(" ", argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}\n" +
                $"## stdout\n{stdout}\n## stderr\n{stderr}\n");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(not kept: {ex.Message})";
        }
    }

    /// <summary>
    /// The question's publish command with its placeholders filled: <c>{manifest}</c>, <c>{manifest_dir}</c>, and
    /// <c>{datarepo}</c> (the machine's install), so a question names no path on anyone's machine.
    /// </summary>
    internal static List<string> PublishArgv(IReadOnlyList<string> command, string manifest, string datarepo) =>
        command.Select(c => c.Replace("{manifest_dir}", Path.GetDirectoryName(Path.GetFullPath(manifest))!.Replace('\\', '/'))
                             .Replace("{manifest}", manifest).Replace("{datarepo}", datarepo)).ToList();

    private static string Tail(string s) => (s = s.Trim()).Length > 1200 ? s[^1200..] : s;

    /// <summary>
    /// Exit code 124 with a TIMEOUT line on stderr when <paramref name="timeout"/> passes (the process tree is killed).
    /// What the process wrote before the kill is kept: on 2026-10-11 four killed publishes left nothing to read.
    /// </summary>
    internal static async Task<(int Rc, string Stdout, string Stderr)> RunProcessFullAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            p.Kill(entireProcessTree: true);
            async Task<string> Partial(Task<string> t) =>
                await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) == t && t.IsCompletedSuccessfully ? t.Result : "";
            string stdout = await Partial(outTask).ConfigureAwait(false), stderr = await Partial(errTask).ConfigureAwait(false);
            return (124, stdout, (stderr.Length > 0 ? stderr.TrimEnd() + "\n" : "") + $"TIMEOUT after {timeout.TotalSeconds:0} s");
        }
        return (p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    /// <summary>
    /// Whether this machine's datarepo takes <c>ingest --json</c>: 1.0.0 and later (the C# executable). 0.32.0 does
    /// not, and keeps the exit code alone. Read from <c>datarepo --version</c> ("datarepo 1.0.0") once per batch.
    /// </summary>
    internal static bool TakesIngestJson(string versionLine) =>
        System.Text.RegularExpressions.Regex.Match(versionLine, @"datarepo\s+(\d+)\.(\d+)\.(\d+)") is { Success: true } m
        && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 1;

    private bool _ingestJson;

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Files to exclude, [] when every file passed, or null when the deposit must be skipped. Deliberately narrow: only the
    /// profile's excludable reasons (a blank or failed injection), at most its share of the deposit (one file is always
    /// allowed), and at least one file must pass. An acquisition failure (ion-trap MS2) still drops the deposit (aging D52).
    /// </summary>
    /// <summary>
    /// G4 (aging S64): the deposit's text never said it was labelled, but its spectra carry isobaric reporter ions.
    /// Every QC'd file labelled: it waits for the profile that takes isobaric labels, as the screen would have routed it.
    /// Some files only: <c>deferred_mixed_labelling</c>, because searching the label-free part alone is a design decision
    /// (PXD052189: 12 label-free runs and 20 TMT fractions in one deposit; whole deposits only, S43).
    /// </summary>
    /// <summary>
    /// G5 (aging S66): a search that failed while the .NET runtimes it loads from changed (a machine-wide update replaced
    /// 10.0.8 with 10.0.10 and PXD021194 died 2 h 21 m in) failed on the environment, not on its data. Its output is moved
    /// aside, the raw files stay (a failed search never cleans up), and it is searched again on a later pass: twice at
    /// most, then it settles as <c>search_failed</c> like any other.
    /// </summary>
    internal bool InterruptedByRuntime(string acc, IReadOnlyList<string> before, string root)
    {
        var after = Search.DotnetRuntimes.Snapshot(root);
        if (after.SequenceEqual(before)) return false;
        string change = $"{string.Join(",", before)} -> {string.Join(",", after)} under {root}";
        int n = (Entry(acc)?["search_interruptions"]?.GetValue<int>() ?? 0) + 1;
        if (n > MaxSearchInterruptions)
        {
            Log($"{acc} SEARCH failed while the .NET runtimes changed again ({change}); interrupted {n - 1} time(s) already: settled");
            Record(acc, ("status", "search_failed"), ("detail", $"runtime changed during the search: {change}"), ("search_interruptions", n));
            return true;
        }
        string d = Path.Combine(Run(acc), "04_search"), aside = Aside(d);
        if (Directory.Exists(d)) Directory.Move(d, aside);
        Log($"{acc} SEARCH INTERRUPTED: the .NET runtimes changed during the search ({change}); the environment failed, not the data. Output moved to {Path.GetFileName(aside)}; searched again on a later pass ({n} of {MaxSearchInterruptions}). Set dotnet_root in the machine file to stop this.");
        Record(acc, ("status", "search_interrupted"), ("search_interruptions", n), ("detail", $"runtime changed during the search: {change}"));
        return true;
    }

    private const int MaxSearchInterruptions = 2;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DepositChemistry> _chemistry = new(StringComparer.Ordinal);

    /// <summary>
    /// G19: the deposit's chemistry (ChemistryDetector, D22-D24), from PRIDE's project record, the deposit's SDRF (downloaded
    /// into 01_chemistry, kilobytes) and the question's curated SDRF. Written to 01_chemistry/chemistry.json and the state;
    /// cached for the search, and read again if a restart lost the cache.
    /// </summary>
    private async Task<DepositChemistry> ChemistryAsync(string acc, IReadOnlyList<UsefulProteomicsDatabases.PrideArchiveFile> all,
        IReadOnlyList<string> rawFiles, Profile profile, CancellationToken ct)
    {
        string dir = Path.Combine(Run(acc), "01_chemistry");
        Directory.CreateDirectory(dir);
        var project = await _search.TryGetProjectAsync(acc, ct).ConfigureAwait(false);
        Readers.SdrfDocument? deposited = null, curated = null;
        var sdrf = all.FirstOrDefault(f => f.FileName.EndsWith(".sdrf.tsv", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(f => f.FileName.Contains("sdrf", StringComparison.OrdinalIgnoreCase) && f.FileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase));
        if (sdrf is not null)
        {
            string path = Path.Combine(dir, sdrf.FileName);
            if (!File.Exists(path)) path = await _files.DownloadAsync(sdrf, dir, ct).ConfigureAwait(false);
            deposited = new Readers.SdrfDocument(path);
        }
        if (_q.Designs?.For(acc) is { } c && File.Exists(c)) curated = new Readers.SdrfDocument(c);
        // G40: a profile that ships a modification for a thiol reagent (NBF-Cl) searches such a deposit instead of parking it.
        var chem = ChemistryDetector.Detect(project, curated, deposited, rawFiles, profile.ThiolReagents);
        var json = Search.SearchStage.Chemistry(chem);
        json["sdrf_file"] = sdrf?.FileName;
        json["curated_sdrf"] = curated is not null;
        File.WriteAllText(Path.Combine(dir, "chemistry.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        Record(acc, ("chemistry", json));
        _chemistry[acc] = chem;
        return chem;
    }

    private static string Describe(DepositChemistry c) =>
        $"protease {c.Protease.Value} [{c.Protease.Source}], cysteine {(c.CysMods.Count == 0 ? "unmodified" : string.Join(" + ", c.CysMods.Select(m => $"{m.Name} {(m.Fixed ? "fixed" : "variable")}")))} [{c.Alkylation.Source}], label {c.Label.Value} [{c.Label.Source}]"
        + (c.Park is null ? "" : $"; {c.Park}");

    /// <summary>
    /// G18: a deposit still marked <c>searching</c> when a driver starts was cut off by the previous driver's death (a
    /// reboot, a crash, a kill), never by its data. Its half-written output is moved aside, since MetaMorpheus refuses to
    /// reuse it and the deposit would otherwise settle as <c>search_failed</c>, and it is searched again on this run. Three
    /// such deaths on one deposit settle it: a deposit that keeps taking the machine down is worth a look.
    /// </summary>
    internal void RecoverInterruptedSearches(IEnumerable<QueueEntry> queue)
    {
        foreach (var e in queue)
        {
            string acc = e.Accession;
            if (Entry(acc)?["status"]?.GetValue<string>() != "searching") continue;
            string d = Path.Combine(Run(acc), "04_search");
            if (File.Exists(Path.Combine(d, "provenance.json"))) continue;   // the search finished; ReconcileAsync delivers it
            int n = (Entry(acc)?["driver_interruptions"]?.GetValue<int>() ?? 0) + 1;
            const string why = "the previous driver stopped during the search (a reboot, a crash or a kill)";
            if (n > MaxDriverInterruptions)
            {
                Log($"{acc} SEARCH INTERRUPTED again: {why}; interrupted {n - 1} time(s) already: settled");
                Record(acc, ("status", "search_failed"), ("detail", $"{why}, {n} times"), ("driver_interruptions", n));
                continue;
            }
            string aside = Aside(d);
            if (Directory.Exists(d)) Directory.Move(d, aside);
            Log($"{acc} SEARCH INTERRUPTED: {why}. " + (Directory.Exists(aside) ? $"Output moved to {Path.GetFileName(aside)}; " : "")
                + $"searched again on this run ({n} of {MaxDriverInterruptions}).");
            Record(acc, ("status", "search_interrupted"), ("driver_interruptions", n), ("detail", why));
        }
    }

    private const int MaxDriverInterruptions = 3;

    /// <summary><c>04_search.interrupted-&lt;utc&gt;</c>, made unique: two interruptions can fall in one second.</summary>
    private static string Aside(string d)
    {
        string stamp = d + $".interrupted-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}", aside = stamp;
        for (int k = 2; Directory.Exists(aside) || File.Exists(aside); k++) aside = $"{stamp}-{k}";
        return aside;
    }

    internal bool RoutedByReporters(string acc, JsonObject report, string where)
    {
        var labelled = report.Where(kv => kv.Value!["isobaric_reporters"] is not null)
            .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (labelled.Count == 0) return false;
        string tags = string.Join("/", labelled.Select(f => report[f]!["isobaric_reporters"]!["tag"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal));
        string status;
        if (labelled.Count == report.Count)
        {
            var taker = _q.Profiles.Select(k => _profiles[k]).FirstOrDefault(p => p.Accepts.Labellings.Contains(Labelling.Isobaric));
            status = "waiting_" + Slug(taker?.Key ?? "isobaric");
        }
        else status = "deferred_mixed_labelling";
        Log($"{acc} {where} SPECTRA SAY ISOBARIC ({status}): {tags} reporter ions in {labelled.Count} of {report.Count} file(s): {string.Join(", ", labelled.Take(6))}{(labelled.Count > 6 ? ", ..." : "")}");
        Record(acc, ("status", status), ("isobaric_files", new JsonArray(labelled.Select(n => (JsonNode?)n).ToArray())),
            ("isobaric_of", report.Count), ("isobaric_tag", tags));
        return true;
    }

    public static List<string>? QcExclusion(JsonObject report, QcGates gates)
    {
        var failed = report.Where(kv => kv.Value!["pass"]!.GetValue<bool>() != true)
            .ToDictionary(kv => kv.Key, kv => kv.Value!["fail_reasons"]?.AsArray().Select(r => r!.GetValue<string>()).ToHashSet() ?? new HashSet<string> { "unspecified" });
        if (failed.Count == 0) return new List<string>();
        if (failed.Values.Any(rs => rs.Any(r => !gates.Excludable.Contains(r)))) return null;
        if (report.Count - failed.Count < 1) return null;
        if (failed.Count > Math.Max(1, (int)(gates.MaxExcludedFraction * report.Count))) return null;
        return failed.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private FetchRequest FetchRequestFor(string acc, Pick pick, DepositPolicy dep) =>
        new(acc, Path.Combine(Run(acc), "02_fetch"), pick, pick == Pick.All ? dep.MaxFiles : 1, dep.MaxFileMb, ".raw",
            _m.ParallelDownloads, _m.FetchAttempts, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), _m.WorkRoot,
            TimeSpan.FromMinutes(_m.FetchStallMinutes),
            _q.ExceptionFor(acc) is { SelectsFiles: true } ex ? new FileSelection(ex.Keeps, ex.Describe()) : null,
            Sha1Source ?? Integrity.PrideChecksumsAsync,
            _m.AsperaAscp is { } ascp ? new AsperaClient(ascp, _m.AsperaKey!, _m.AsperaAttempts, StartGap: TimeSpan.FromSeconds(10)) : null,
            PrideHosts.Parse(_m.PrideHttps));

    /// <summary>Where a fetch gets PRIDE's SHA-1s; tests replace it (null in production means PRIDE's v3 file list).</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? Sha1Source { get; set; }

    /// <summary>The profile's deposit caps, with the question's exception for this deposit applied (D28).</summary>
    private DepositPolicy DepositFor(string acc, Profile profile) => _q.ExceptionFor(acc)?.Apply(profile.Deposit) ?? profile.Deposit;

    private string ParamsFile(string acc, Profile profile)
    {
        string p = Path.Combine(_stateDir, $"params_{acc}.json");
        File.WriteAllText(p, JsonSerializer.Serialize(new { accession = acc, profile = profile.Key, question = _q.Name, run_date = _runDate }, Envelope.Json));
        return p;
    }

    private static string Reasons(JsonObject report) => string.Join("; ", report.Where(kv => kv.Value!["pass"]!.GetValue<bool>() != true)
        .Select(kv => $"{kv.Key}: {string.Join("/", kv.Value!["fail_reasons"]?.AsArray().Select(r => r!.GetValue<string>()) ?? Array.Empty<string>())}"));

    private static string Slug(string s) => new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Thin wrappers so the runner's gates read plainly.</summary>
    private sealed record PrideProjectSearchResultLike(UsefulProteomicsDatabases.PrideProjectSearchResult Record);
    /// <summary>Where searched deposits live: this run root, the machine's work root, and the question manifest's root.</summary>
    private IEnumerable<string> DuplicateRoots()
    {
        yield return _runRoot;
        yield return _m.WorkRoot;
        if (_q.Publish?.Manifest is { } mf && File.Exists(mf) && ManifestEntry.WorkRootOf(mf) is { } wr) yield return wr;
    }

    public static IReadOnlyList<QueueEntry> LoadQueue(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsArray().Select(x => new QueueEntry(
            x!["accession"]!.GetValue<string>(), x["title"]?.GetValue<string>() ?? "",
            (x["organism"]?.GetValue<string>() ?? "human").ToLowerInvariant())).ToList();
}
