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
            foreach (var (k, v) in fields) e[k] = v?.DeepClone();
            e["updated_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'");
            string tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, st.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
            File.Move(tmp, StateFile, overwrite: true);
        }
    }

    private JsonObject? Entry(string acc) => State()["datasets"]![acc] as JsonObject;

    /// <summary>
    /// One driver at a time. Without this guard, clearing STOP and relaunching once left two drivers fetching into the same
    /// folders and both writing state.json.
    /// </summary>
    private void ClaimOrDie()
    {
        if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out int old) && old != Environment.ProcessId)
        {
            try
            {
                using var p = Process.GetProcessById(old);
                if (!p.HasExited) throw new UsageException($"a batch driver is already running as pid {old}; stop it first (or delete {PidFile} if it is stale)");
            }
            catch (ArgumentException) { }   // no such process: the file is stale
        }
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());
    }

    private double FreeGb() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_runRoot))!).AvailableFreeSpace / 1e9;

    /// <summary>A status the runner does not spend time on again. Clear the state entry to retry.</summary>
    public string Settled(string acc)
    {
        if (_q.Holds.ContainsKey(acc)) return "on_hold_user";
        string? s = Entry(acc)?["status"]?.GetValue<string>();
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
        try
        {
            await ReconcileAsync(queue, ct).ConfigureAwait(false);
            // Pass 1 walks the queue; each later pass walks only the deposits PRIDE or UniProt failed to serve, after a
            // wait. The pass count bounds the loop: an outage longer than fetch_passes waits leaves them for the next run.
            var walk = queue;
            for (int pass = 1; ; pass++)
            {
                if (!await WalkAsync(walk, ct).ConfigureAwait(false)) return;
                walk = queue.Where(e => Retriable(e.Accession)).ToList();
                if (walk.Count == 0) { Log("queue exhausted"); return; }
                string which = string.Join(", ", walk.Take(10).Select(e => e.Accession)) + (walk.Count > 10 ? ", ..." : "");
                if (pass >= _m.FetchPasses)
                {
                    Log($"queue exhausted; {walk.Count} deposit(s) still waiting on PRIDE after {pass} pass(es) ({which}): run the batch again later");
                    return;
                }
                Log($"pass {pass} done; {walk.Count} deposit(s) failed on PRIDE or UniProt availability ({which}); pass {pass + 1} in {_m.PassWaitMinutes:0} min");
                if (!await WaitAsync(TimeSpan.FromMinutes(_m.PassWaitMinutes), ct).ConfigureAwait(false)) return;
            }
        }
        finally
        {
            try { File.Delete(PidFile); } catch (IOException) { }
        }
    }

    /// <summary>One walk of <paramref name="queue"/>. True when it ran out; false when STOP, the disk floor or cancellation ended it.</summary>
    private async Task<bool> WalkAsync(IReadOnlyList<QueueEntry> queue, CancellationToken ct)
    {
        int i = 0;
        (QueueEntry Entry, Profile Profile)? pending = null;
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(StopFile)) { Log("STOP file present: finishing"); return false; }
            if (FreeGb() < _m.MinFreeGb) { Log($"free space {FreeGb():0} GB below {_m.MinFreeGb:0} GB floor: stopping"); return false; }
            if (pending is null)
            {
                (pending, i) = await NextReadyAsync(queue, i, ct).ConfigureAwait(false);
                if (pending is null) return !ct.IsCancellationRequested && !File.Exists(StopFile);
            }
            var (e, profile) = pending.Value;
            pending = null;
            // While this dataset searches (hours), the next one is screened, probed and fetched (network-bound).
            var search = SearchAndDeliverAsync(e, profile, ct);
            var next = NextReadyAsync(queue, i, ct);
            await Task.WhenAll(search, next).ConfigureAwait(false);
            (pending, i) = next.Result;
        }
        return false;
    }

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
    /// Left for a later pass by unavailability: downloads that exhausted their attempts (<c>*_unavailable</c>), or a
    /// screen, database or listing call that never got an answer (still <c>probing</c>).
    /// </summary>
    internal bool Retriable(string acc) =>
        !File.Exists(Path.Combine(Run(acc), "04_search", "provenance.json")) && Settled(acc).Length == 0
        && Entry(acc)?["status"]?.GetValue<string>() is "fetch_unavailable" or "probe_fetch_unavailable" or "probing";

    /// <summary>
    /// A failed probe or whole-deposit fetch. Unavailability that outlasted every attempt (PRIDE down for hours, S52) is
    /// not the deposit's fault: it is left unsettled for a later pass, up to <c>fetch_passes</c> in all, then settled.
    /// Anything else (a checksum mismatch, a 404, a full disk) settles at once, as before.
    /// </summary>
    internal void RecordFetchFailure(string acc, bool probe, Exception ex)
    {
        string what = probe ? "PROBE fetch" : "FETCH";
        string settled = probe ? "probe_fetch_failed" : "fetch_failed";
        if (!FetchStage.IsTransient(ex))
        {
            Log($"{acc} {what} failed: {ex.Message}");
            Record(acc, ("status", settled), ("detail", ex.Message));
            return;
        }
        int used = (Entry(acc)?["unavailable_passes"]?.GetValue<int>() ?? 0) + 1;
        if (used >= _m.FetchPasses)
        {
            Log($"{acc} {what} failed on availability, pass {used} of {_m.FetchPasses}: settled. {ex.Message}");
            Record(acc, ("status", settled), ("detail", ex.Message), ("unavailable_passes", used));
            return;
        }
        Log($"{acc} {what} failed on availability, pass {used} of {_m.FetchPasses}: retry next pass. {ex.Message}");
        Record(acc, ("status", probe ? "probe_fetch_unavailable" : "fetch_unavailable"), ("detail", ex.Message), ("unavailable_passes", used));
    }

    private async Task<((QueueEntry, Profile)? Ready, int Index)> NextReadyAsync(IReadOnlyList<QueueEntry> queue, int i, CancellationToken ct)
    {
        while (i < queue.Count && !ct.IsCancellationRequested && !File.Exists(StopFile))
        {
            var e = queue[i++];
            if (File.Exists(Path.Combine(Run(e.Accession), "04_search", "provenance.json"))) continue;
            string settled = Settled(e.Accession);
            if (settled.Length > 0) { Log($"{e.Accession} skipped: {settled} on a previous pass"); continue; }
            Record(e.Accession, ("status", "probing"), ("title", e.Title), ("organism", e.Organism));
            var profile = await ProbeAsync(e, ct).ConfigureAwait(false);
            if (profile is not null && await FetchWholeAsync(e, profile, ct).ConfigureAwait(false)) return ((e, profile), i);
        }
        return (null, i);
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
            return null;
        }
        var profile = _profiles[route.Profile!];
        Record(acc, ("profile", profile.Key));
        if (!profile.Databases.ContainsKey(e.Organism))
        {
            Log($"{acc} SKIPPED: {profile.Key} has no database for organism '{e.Organism}'");
            Record(acc, ("status", "skipped_organism"));
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
        List<UsefulProteomicsDatabases.PrideArchiveFile> listing;
        try { listing = (await _files.ListFilesAsync(acc, ct).ConfigureAwait(false)).Where(f => f.FileName.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)).ToList(); }
        catch (Exception ex) when (FetchStage.IsTransient(ex)) { Log($"{acc} size listing failed ({ex.Message}); retry next pass"); return null; }

        // The same raw files already searched under another accession: refused before any download (PXR-A9).
        var dup = DuplicateScreen.Check(acc, listing.Select(f => (f.FileName, f.FileSizeBytes)).ToList(), DuplicateScreen.Index(DuplicateRoots()));
        if (dup.DuplicateOf is { } original)
        {
            Log($"{acc} SCREENED OUT (excluded_duplicate): all {listing.Count} raw files (name and size) are in {original}, already searched");
            Record(acc, ("status", "excluded_duplicate"), ("duplicate_of", original));
            return null;
        }
        foreach (var (other, shared) in dup.Partial)
            Log($"{acc} shares {shared} of {listing.Count} raw files (name and size) with {other}, already searched; not a duplicate, continuing");

        var sizes = listing.Select(f => f.FileSizeBytes / 1e6).OrderBy(x => x).ToList();
        var dep = profile.Deposit;
        var oversize = sizes.Where(s => s > dep.MaxFileMb).ToList();
        if (sizes.Count > dep.MaxFiles || oversize.Count > 0)
        {
            string why = sizes.Count > dep.MaxFiles ? $"{sizes.Count} raw files > {dep.MaxFiles}" : $"{oversize.Count} file(s) above {dep.MaxFileMb} MB";
            Log($"{acc} DEFERRED: {why}. A subset would be a design decision made by file size (S43); it waits for SDRF -> experimental-design selection.");
            Record(acc, ("status", "deferred_needs_design_selection"), ("n_files", sizes.Count), ("total_gb", Math.Round(sizes.Sum() / 1000, 1)), ("detail", why));
            return null;
        }
        if (sizes.Count > 0 && dep.DeferIfMedianFileMb > 0 && sizes[sizes.Count / 2] > dep.DeferIfMedianFileMb)
        {
            double med = sizes[sizes.Count / 2];
            Log($"{acc} DEFERRED: median file {med:0} MB > {dep.DeferIfMedianFileMb} MB ({sizes.Count} files, {sizes.Sum() / 1000:0.0} GB). EBI drops these mid-transfer and a retry re-pays from zero (S40).");
            Record(acc, ("status", "deferred_large_files"), ("median_file_mb", Math.Round(med)), ("n_files", sizes.Count));
            return null;
        }

        string d = Run(acc);
        try
        {
            await FetchStage.RunAsync(FetchRequestFor(acc, Pick.ProbeSpread, dep), _files, ct).ConfigureAwait(false);
        }
        // Not the caller's cancellation: an HttpClient timeout is a TaskCanceledException too, and it is unavailability.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFetchFailure(acc, probe: true, ex);
            return null;
        }
        var (report, allPass) = QcStage.Run(Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "02b_qc_probe"), profile, _m, ParamsFile(acc, profile), _runDate);
        var excl = QcExclusion(report, profile.Qc);
        bool ok = allPass || excl is { Count: > 0 };
        if (!allPass && ok) Log($"{acc} PROBE qc: {string.Join(", ", excl!)} fail only on excludable reasons; continuing (decided at full QC)");
        Log($"{acc} PROBE qc {(ok ? "ok" : "FAILED")} " + string.Join("; ", report.Take(4).Select(kv =>
            $"{kv.Key}: hcd={kv.Value!["fraction_orbitrap_hcd"]} ms2={kv.Value["ms2"]} min={kv.Value["run_minutes"]}")));
        if (!ok)
        {
            Record(acc, ("status", "skipped_acquisition"), ("detail", Reasons(report)));
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
                return null;
            }
        }
        return profile;
    }

    /// <summary>The whole deposit, or nothing: anything short of every listed file is a failure that says what it got (S43).</summary>
    private async Task<bool> FetchWholeAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        string acc = e.Accession;
        JsonObject manifest;
        try
        {
            manifest = await FetchStage.RunAsync(FetchRequestFor(acc, Pick.All, profile.Deposit), _files, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFetchFailure(acc, probe: false, ex);
            return false;
        }
        int listed = manifest["rest_raw_count"]!.GetValue<int>();
        int got = Directory.EnumerateFiles(Path.Combine(Run(acc), "02_fetch", "spectra"), "*.raw").Count();
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
        var req = new SearchRequest(profile, e.Organism, _m, Path.Combine(d, "02_fetch", "spectra"), Path.Combine(d, "04_search"),
            Path.Combine(d, "02b_qc"), overlays, excl, _runDate, acc, UseLibrary: library);
        SearchOutcome outcome;
        try
        {
            outcome = await SearchStage.RunAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stage threw: before MetaMorpheus ran (setup) or after it (a bug in our own bookkeeping). Say which
            // could not be told apart from the message alone, so keep the type and where the output stands.
            bool ran = Directory.Exists(Path.Combine(d, "04_search", "mm"));
            Log($"{acc} SEARCH STAGE FAILED ({ex.GetType().Name}, MetaMorpheus {(ran ? "HAD run" : "had not run")}): {ex.Message}");
            Record(acc, ("status", "search_failed"), ("detail", $"{ex.GetType().Name}: {ex.Message}"), ("metamorpheus_ran", ran));
            return;
        }
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

    private async Task DeliverAsync(QueueEntry e, Profile profile, CancellationToken ct)
    {
        if (_q.Publish?.Manifest is not { } manifest) return;
        try
        {
            ManifestEntry.EnsureExists(manifest, _q.Name, _runRoot);
            ManifestEntry.Append(manifest, e, profile, Run(e.Accession), Entry(e.Accession), _m.WorkRoot);
            Log($"{e.Accession} MANIFEST entry appended");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Log($"{e.Accession} MANIFEST failed: {ex.Message}"); Record(e.Accession, ("ingest", "manifest_failed")); return; }
        if (_m.DataRepo is null) return;
        var (rc, tail) = await RunProcessAsync(new[] { _m.DataRepo, "ingest", manifest, e.Accession }, TimeSpan.FromHours(3), ct).ConfigureAwait(false);
        Log($"{e.Accession} INGEST rc={rc}\n{tail}");
        Record(e.Accession, ("ingest_rc", rc), ("ingest_out", tail));
        if (rc != 0 || _q.Publish.Command.Count == 0) return;
        // A failed publish is logged, never fatal: the bundle exists, and a stale site is a warning, not a reason to stop.
        var (prc, ptail) = await RunProcessAsync(PublishArgv(_q.Publish.Command, manifest, _m.DataRepo), TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        Log($"{e.Accession} PUBLISH rc={prc}\n{ptail}");
        Record(e.Accession, ("publish_rc", prc), ("publish_out", ptail));
    }

    /// <summary>
    /// The question's publish command with its placeholders filled: <c>{manifest}</c>, <c>{manifest_dir}</c>, and
    /// <c>{datarepo}</c> (the machine's install), so a question names no path on anyone's machine.
    /// </summary>
    internal static List<string> PublishArgv(IReadOnlyList<string> command, string manifest, string datarepo) =>
        command.Select(c => c.Replace("{manifest_dir}", Path.GetDirectoryName(Path.GetFullPath(manifest))!.Replace('\\', '/'))
                             .Replace("{manifest}", manifest).Replace("{datarepo}", datarepo)).ToList();

    private static async Task<(int Rc, string Tail)> RunProcessAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
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
            return (124, $"TIMEOUT after {timeout.TotalSeconds:0} s");
        }
        string all = ((await outTask.ConfigureAwait(false)) + (await errTask.ConfigureAwait(false))).Trim();
        return (p.ExitCode, all.Length > 1200 ? all[^1200..] : all);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Files to exclude, [] when every file passed, or null when the deposit must be skipped. Deliberately narrow: only the
    /// profile's excludable reasons (a blank or failed injection), at most its share of the deposit (one file is always
    /// allowed), and at least one file must pass. An acquisition failure (ion-trap MS2) still drops the deposit (aging D52).
    /// </summary>
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
            _m.ParallelDownloads, _m.FetchAttempts, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), _m.WorkRoot);

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
