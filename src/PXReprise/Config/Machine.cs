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
    string? QcPython = null)
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
        // A Python with qc's qctemplates installed: the qc-payload stage runs its validate and render (qc owns them).
        // Optional: without it the payload is still built, with vendored bin edges, and the stage says so.
        string? qcPython = root.OptionalString("qc_python");
        root.RefuseUnknownKeys();
        if (threads < 1) throw new ConfigException(file, "'max_threads' must be 1 or more");
        return new Machine(file, workRoot, dbDir, mm, settings, threads, dotnet, licence, libraries, minFree, datarepo, attempts, parallel, qcPython);
    }

    public string CmdFor(string release) =>
        MetaMorpheus.TryGetValue(release, out string? cmd)
            ? cmd
            : throw new UsageException($"{Path.GetFileName(SourceFile)} names no MetaMorpheus {release} (has: {string.Join(", ", MetaMorpheus.Keys)})");
}
