using PXReprise.Cli;

namespace PXReprise.Config;

/// <summary>
/// Where things live on THIS machine: the work root (the shared corpus), the prepared databases, each MetaMorpheus
/// release's CMD, its settings directory, and how many threads a search may use. Nothing here changes a result, so
/// none of it belongs in a profile; all of it is recorded in provenance.
/// </summary>
public sealed record Machine(
    string SourceFile,
    string WorkRoot,
    string DatabaseDir,
    IReadOnlyDictionary<string, string> MetaMorpheus,
    string MetaMorpheusSettingsRoot,
    int MaxThreads,
    string Dotnet,
    bool AcceptThermoLicence,
    string SpectralLibraryRoot = "",
    double MinFreeGb = 250,
    string? DataRepo = null,
    int FetchAttempts = 8,
    int ParallelDownloads = 4,
    string? QcPython = null,
    int FetchPasses = 3,
    double PassWaitMinutes = 30,
    double SearchTimeoutHours = 48,
    double SearchStallMinutes = 90,
    string? DotnetRoot = null,
    double FetchStallMinutes = 5,
    int SearchSlots = 1,
    int CatchUpEvery = 0,
    string? AsperaAscp = null,
    string? AsperaKey = null,
    int AsperaAttempts = 3,
    string PrideHttps = "globus",
    DepositLimits? DepositLimits = null,
    double? ProbeSearchMinutes = null)
{
    public static Machine Load(string file)
    {
        file = Path.GetFullPath(file);
        var root = TomlSection.Load(file);
        string workRoot = root.RequiredString("work_root");
        string dbDir = root.OptionalString("database_dir") ?? Path.Combine(workRoot, "db");
        var mm = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var mmTable = root.RequiredTable("metamorpheus");
        foreach (string release in mmTable.Keys.ToList()) mm[release] = mmTable.RequiredString(release);
        mmTable.RefuseUnknownKeys();
        string settings = root.OptionalString("metamorpheus_settings_root") ?? Path.Combine(workRoot, "mm_settings");
        int threads = checked((int)(root.OptionalInteger("max_threads") ?? Environment.ProcessorCount));
        string dotnet = root.OptionalString("dotnet") ?? "dotnet";
        // The Thermo RawFileReader licence is the operator's to accept, and it is recorded, never assumed.
        bool licence = root.OptionalBool("accept_thermo_licence") ?? false;
        // Under the work root, never beside the spectra: cleanup deletes a run's spectra and must never touch libraries.
        string libraries = root.OptionalString("spectral_library_root") ?? Path.Combine(workRoot, "spectral_libraries");
        // Stop rather than fill a disk other projects share.
        double minFree = root.OptionalNumber("min_free_gb") ?? 250;
        // The dataRepo RELEASE install used to ingest, never an editable development checkout (aging D-rule).
        string? datarepo = root.OptionalString("datarepo");
        int attempts = checked((int)(root.OptionalInteger("fetch_attempts") ?? 8));
        int parallel = checked((int)(root.OptionalInteger("parallel_downloads") ?? 4));
        // A deposit whose downloads exhaust their attempts on unavailability is retried on a later pass, up to this many in all.
        int passes = checked((int)(root.OptionalInteger("fetch_passes") ?? 3));
        double passWait = root.OptionalNumber("pass_wait_minutes") ?? 30;
        // How long a search may take depends on this machine and what else runs on it, not on the method, so the limits
        // live here. The wall clock is only a ceiling; a hung search is caught by the stall check, which kills MetaMorpheus
        // only after it has used no CPU for that long. A slow search on a busy box is never killed for being slow.
        double searchTimeout = root.OptionalNumber("search_timeout_h") ?? 48;
        double stall = root.OptionalNumber("search_stall_minutes") ?? 90;
        // A Python with qc's qctemplates installed: the qc-payload stage runs its validate and render (qc owns them).
        // Optional: without it the payload is still built, with vendored bin edges, and the stage says so.
        string? qcPython = root.OptionalString("qc_python");
        // A private .NET install for MetaMorpheus (G5): a machine-wide runtime update once removed assemblies under a
        // running search (aging S66). Optional; without it MetaMorpheus uses the machine's runtime and the batch watches it.
        string? dotnetRoot = root.OptionalString("dotnet_root");
        // A download whose file has not grown for this long is abandoned and retried (0 = off).
        double fetchStall = root.OptionalNumber("fetch_stall_minutes") ?? 5;
        // Searches run at once (the user, 2026-10-07: a downloaded deposit starts its search at once, never waiting on the
        // previous one). Each search still gets max_threads; one search leaves most of a large machine idle.
        int slots = checked((int)(root.OptionalInteger("search_slots") ?? 1));
        // D39: every Nth search started is a catch-up re-search of a deposit searched under a profile the question no
        // longer names (0 = off).
        int catchUp = checked((int)(root.OptionalInteger("catch_up_every") ?? 0));
        // IBM Aspera, tried before HTTPS for each raw file (2026-10-08): EBI requires ascp >= 4.4.4 with encryption on and
        // its public key (KB0011565). Both or neither; off when absent.
        string? ascp = root.OptionalString("aspera_ascp"), asperaKey = root.OptionalString("aspera_key");
        // How many of a file's attempts go to Aspera before HTTPS; a damaged Aspera copy ends them early. 0 = HTTPS only.
        int asperaAttempts = checked((int)(root.OptionalInteger("aspera_attempts") ?? 3));
        // The HTTPS host for raw files (pride 014): "globus", EBI's Globus collection over plain HTTPS, which came back
        // clean where ftp.pride.ebi.ac.uk did not; or "ftp", mzLib's own choice.
        string prideHttps = (root.OptionalString("pride_https") ?? "globus").ToLowerInvariant();
        // v0.4.21 (the user, 2026-10-17: "incrementally increase the limits"): how big a deposit this box downloads and
        // searches whole is the box's, like search_timeout_h, not the method's. Each key here replaces the profiles' own
        // [deposit] value; a question's [exceptions] still wins for its deposit.
        var limits = DepositLimits.Load(root.OptionalTable("deposit_limits"));
        // The probe search's wall clock (the profile's probe_search_minutes otherwise): larger files take longer.
        double? probeMinutes = root.OptionalNumber("probe_search_minutes");
        root.RefuseUnknownKeys();
        if (probeMinutes is <= 0) throw new ConfigException(file, "'probe_search_minutes' must be positive");
        if (asperaAttempts < 0) throw new ConfigException(file, "'aspera_attempts' must not be negative (0 sends every attempt to HTTPS)");
        if (prideHttps is not ("globus" or "ftp")) throw new ConfigException(file, $"'pride_https' must be \"globus\" or \"ftp\", not \"{prideHttps}\"");
        if ((ascp is null) != (asperaKey is null)) throw new ConfigException(file, "'aspera_ascp' and 'aspera_key' go together");
        if (ascp is not null && !File.Exists(ascp)) throw new ConfigException(file, $"'aspera_ascp' = '{ascp}' does not exist");
        if (asperaKey is not null && !File.Exists(asperaKey)) throw new ConfigException(file, $"'aspera_key' = '{asperaKey}' does not exist");
        if (slots < 1) throw new ConfigException(file, "'search_slots' must be 1 or more");
        if (catchUp is < 0 or 1) throw new ConfigException(file, "'catch_up_every' must be 0 (off) or 2 or more");
        if (fetchStall < 0) throw new ConfigException(file, "'fetch_stall_minutes' must not be negative (0 turns the stall check off)");
        if (dotnetRoot is not null && !Directory.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App")))
            throw new ConfigException(file, $"'dotnet_root' = '{dotnetRoot}' holds no shared\\Microsoft.NETCore.App: copy a .NET install there (dotnet, host, shared)");
        if (threads < 1) throw new ConfigException(file, "'max_threads' must be 1 or more");
        if (passes < 1) throw new ConfigException(file, "'fetch_passes' must be 1 or more");
        if (passWait < 0) throw new ConfigException(file, "'pass_wait_minutes' must not be negative");
        if (searchTimeout <= 0) throw new ConfigException(file, "'search_timeout_h' must be positive");
        if (stall < 0) throw new ConfigException(file, "'search_stall_minutes' must not be negative (0 turns the stall check off)");
        return new Machine(file, workRoot, dbDir, mm, settings, threads, dotnet, licence, libraries, minFree, datarepo, attempts, parallel, qcPython, passes, passWait, searchTimeout, stall, dotnetRoot, fetchStall, slots, catchUp, ascp, asperaKey, asperaAttempts, prideHttps, limits, probeMinutes);
    }

    public string CmdFor(string release) =>
        MetaMorpheus.TryGetValue(release, out string? cmd)
            ? cmd
            : throw new UsageException($"{Path.GetFileName(SourceFile)} names no MetaMorpheus {release} (has: {string.Join(", ", MetaMorpheus.Keys)})");
}

/// <summary>
/// The machine's <c>[deposit_limits]</c> (v0.4.21): the profiles' deposit gates as this box sets them. Each key left out
/// keeps the profile's value. A gate changes which deposits are searched, never how, so a profile keeps its version.
/// </summary>
public sealed record DepositLimits(int? MaxFiles = null, int? MaxFileMb = null, long? MaxEstimatedMs2 = null, int? DeferIfMedianFileMb = null)
{
    public static DepositLimits? Load(TomlSection? t)
    {
        if (t is null) return null;
        int? Int(string key) => t.OptionalInteger(key) is { } v ? checked((int)v) : null;
        var l = new DepositLimits(Int("max_files"), Int("max_file_mb"), t.OptionalInteger("max_est_ms2"), Int("defer_if_median_file_mb"));
        t.RefuseUnknownKeys();
        if (l.MaxFiles is < 1) throw new ConfigException(t.File, "'deposit_limits.max_files' must be 1 or more");
        if (l.MaxFileMb is < 1) throw new ConfigException(t.File, "'deposit_limits.max_file_mb' must be 1 or more");
        if (l.MaxEstimatedMs2 is < 0) throw new ConfigException(t.File, "'deposit_limits.max_est_ms2' must not be negative (0 turns the gate off)");
        if (l.DeferIfMedianFileMb is < 0) throw new ConfigException(t.File, "'deposit_limits.defer_if_median_file_mb' must not be negative (0 turns the gate off)");
        return l.Describe().Length == 0 ? null : l;
    }

    public DepositPolicy Apply(DepositPolicy p) => p with
    {
        MaxFiles = MaxFiles ?? p.MaxFiles,
        MaxFileMb = MaxFileMb ?? p.MaxFileMb,
        MaxEstimatedMs2 = MaxEstimatedMs2 ?? p.MaxEstimatedMs2,
        DeferIfMedianFileMb = DeferIfMedianFileMb ?? p.DeferIfMedianFileMb,
    };

    /// <summary>One line for the log and for the state entry of a deposit these limits deferred.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (MaxFiles is { } mf) parts.Add($"max_files {mf}");
        if (MaxFileMb is { } mm) parts.Add($"max_file_mb {mm}");
        if (MaxEstimatedMs2 is { } me) parts.Add($"max_est_ms2 {me}");
        if (DeferIfMedianFileMb is { } dm) parts.Add($"defer_if_median_file_mb {dm}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Whether a deposit deferred by a size gate now fits these limits, read from what its deferral measured
    /// (<c>median_file_mb</c>, <c>est_ms2</c>, <c>n_files</c>, the detail's MB cap). A gate these limits leave to the
    /// profile admits nothing: the profile has not changed.
    /// </summary>
    public bool Admits(string status, System.Text.Json.Nodes.JsonObject entry)
    {
        double? Num(string key) => entry[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out double d) ? d : null;
        switch (status)
        {
            case "deferred_large_files":
                return DeferIfMedianFileMb is { } gate && Num("median_file_mb") is { } med && (gate == 0 || med <= gate);
            case "deferred_search_too_large":
                return MaxEstimatedMs2 is { } cap && Num("est_ms2") is { } est && (cap == 0 || est <= cap);
            case "deferred_needs_design_selection":
                string detail = entry["detail"]?.GetValue<string>() ?? "";
                if (detail.Contains("raw files >", StringComparison.Ordinal))
                    return MaxFiles is { } mf && Num("n_files") is { } n && n <= mf;
                var above = System.Text.RegularExpressions.Regex.Match(detail, @"above (\d+) MB");
                return above.Success && MaxFileMb is { } mb && mb > int.Parse(above.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            default:
                return false;
        }
    }
}
