using System.Formats.Tar;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DuckDB.NET.Data;

namespace DataRepo.Runner;

/// <summary>One orthology snapshot logs has published as a release, as logs states it.</summary>
/// <param name="Tag">The GitHub release tag, e.g. <c>orthology-compara-116-b63a3331</c>.</param>
/// <param name="TarSha256">The sha256 of the released tar.</param>
/// <param name="ManifestSha256">The sha256 of the released manifest.</param>
/// <param name="Attribution">The citation logs' <c>LICENSING.md</c> asks for.</param>
public sealed record SnapshotRelease(string Tag, string TarSha256, string ManifestSha256, string Attribution);

/// <summary><c>logs.register_orthology</c>: verify one of logs' released orthology snapshots and register it, so a
/// catalog can cite it (DATAREPO-75, logs 031; LOGS-D4 (a), our logs 029).</summary>
/// <remarks>
/// <para><b>It copies no orthology rows.</b> The snapshot is read in place: the artefact holds <c>run.json</c> and the
/// extracted snapshot under <see cref="SnapshotDir"/>, and a query reaches its Parquet through the snapshot's own
/// <c>views.sql</c> (DuckDB table macros, with the snapshot directory as their first argument). The method and the
/// file contract are logs' (<c>logs:DEF-ORTHOLOGY v1</c>, <c>logs/design/DEFINITIONS.md</c>, "The file contract");
/// this checks that a snapshot keeps that contract and computes nothing about orthology.</para>
/// <para><b>Inputs by role:</b> <c>snapshot_tar</c> and <c>snapshot_manifest</c>, both from logs' release, supplied by
/// the operator. The runner fetches nothing.</para>
/// <para><b>Acceptance, each refusing the run:</b></para>
/// <list type="bullet">
/// <item>the tar and the manifest are a release logs published (<see cref="Releases"/>), by sha256;</item>
/// <item>the manifest inside the tar is byte-identical to the one given;</item>
/// <item><c>format</c> is <c>ensembl-orthology-snapshot</c> and <c>format_version</c> is 1, no other version read as 1;</item>
/// <item>the tar holds exactly the files the manifest lists, plus <c>manifest.json</c>, each with the manifest's sha256
/// and bytes, and each Parquet file with its rows;</item>
/// <item><c>views.sql</c> loads in DuckDB, and for every ordered pair of the snapshot's species <c>pair_status</c>
/// gives every gene of the first exactly one of its four statuses (our logs 029 check, run on every registration).</item>
/// </list>
/// <para>The cross-check logs proposed, that the snapshot's gene sets are the ones the stored gene resolutions used,
/// is made by <c>build</c>, where both are loaded together and it cannot be skipped
/// (<c>CatalogBuilder.RequireOrthologyGeneSets</c>).</para>
/// </remarks>
public static class OrthologyEngine
{
    public const string Engine = DataRepo.Catalog.Runner.OrthologyEngine;
    public const string DefinitionId = "logs:DEF-ORTHOLOGY v1";
    public static readonly IReadOnlyList<string> Roles = ["snapshot_tar", "snapshot_manifest"];

    /// <summary>Where the extracted snapshot sits in the artefact, so <c>views.sql</c> takes
    /// <c>&lt;artefact&gt;/snapshot</c> as its root.</summary>
    public const string SnapshotDir = "snapshot";

    public const string Format = "ensembl-orthology-snapshot";
    public const long FormatVersion = 1;

    /// <summary>The four statuses <c>pair_status</c> gives (logs' file contract). <c>not_in_gene_set</c> is the
    /// caller's, for a gene id the snapshot does not hold, and never comes from the macro.</summary>
    public static readonly IReadOnlyList<string> PairStatuses = ["has_ortholog", "no_edge_in_shared_tree", "tree_lacks_target_species", "not_in_any_tree"];

    /// <summary>logs' published snapshots (logs 027, 030, 031), copied as logs gave them. A new snapshot is a new
    /// release with a new tag, never an edit to one listed here, so adding one is one line and a datarepo release.</summary>
    public static readonly IReadOnlyList<SnapshotRelease> Releases =
    [
        new("orthology-compara-116-b63a3331",
            "b1d682a51e7e5759cd72719713d4a00a03b07b9ee033b8bdf8f0f54fd7fb747b",
            "fd0a9d8f5d211a91bdafd4c843ee4f0085be8cf0d7ff4fc779c5e6ebda5074aa",
            "Orthology snapshot `orthology-compara-116-b63a3331` (sha256 b1d682a51e7e5759cd72719713d4a00a03b07b9ee033b8bdf8f0f54fd7fb747b), "
            + "trishorts/logs, https://github.com/trishorts/logs, CC-BY-4.0; derived from Ensembl Compara release 116. "
            + "views.sql is mzLib's, LGPL-3.0."),
    ];

    private static string S(object? v) => v is null ? "None" : PyFormat.Str(v);

    /// <summary>The engine release: the snapshot's tag. The code that checks it is the runner's, versioned by
    /// <see cref="EngineRunner.RunnerVersion"/>.</summary>
    public static Dictionary<string, string> Release(SnapshotRelease snapshot) => new(StringComparer.Ordinal) { ["logs"] = snapshot.Tag };

    /// <summary>Verify and register one snapshot.</summary>
    /// <param name="releases">The published snapshots; <see cref="Releases"/> when null. Tests pass their own.</param>
    /// <param name="install">The datarepo install record; <see cref="EngineRunner.InstallIdentity"/> when null. Tests pass one.</param>
    /// <exception cref="RunnerException">A missing or unknown input, a snapshot logs did not release, any acceptance
    /// check above, or a development datarepo. Nothing is written unless every check passes.</exception>
    public static RunResult Run(string store, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyList<SnapshotRelease>? releases = null, IReadOnlyDictionary<string, object?>? install = null)
    {
        var missing = Roles.Where(r => !inputs.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new RunnerException($"{Engine} needs --input {string.Join("=<path>, --input ", missing)}=<path>, both from logs' release.");
        var unknown = inputs.Keys.Where(k => !Roles.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new RunnerException($"{Engine} takes inputs {string.Join(", ", Roles)}; not {string.Join(", ", unknown)}");
        foreach (var (role, path) in inputs)
            if (!File.Exists(path)) throw new RunnerException($"--input {role}={path}: no such file");

        releases ??= Releases;
        var tarSha = BundleWriter.Sha256File(inputs["snapshot_tar"]);
        var manifestSha = BundleWriter.Sha256File(inputs["snapshot_manifest"]);
        var snapshot = releases.FirstOrDefault(r => r.TarSha256 == tarSha)
            ?? throw new RunnerException(
                $"{Path.GetFileName(inputs["snapshot_tar"])} (sha256 {tarSha}) is not a snapshot logs has released. "
                + $"Known: {string.Join(", ", releases.Select(r => r.Tag))}. Pass the release's tar unchanged.");
        if (manifestSha != snapshot.ManifestSha256)
            throw new RunnerException(
                $"{Path.GetFileName(inputs["snapshot_manifest"])} (sha256 {manifestSha}) is not the manifest of {snapshot.Tag} "
                + $"({snapshot.ManifestSha256}).");

        var hashed = new Dictionary<string, string>(StringComparer.Ordinal) { ["snapshot_tar"] = tarSha, ["snapshot_manifest"] = manifestSha };
        var release = Release(snapshot);
        var aid = EngineRunner.ArtefactId(Engine, release, hashed, DefinitionId);
        var existing = EngineRunner.ArtefactDir(store, Engine, aid);
        var result = new RunResult();
        if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
        {
            result.AlreadyDone.Add(ArtefactRef.Load(existing));
            return result;
        }
        install ??= EngineRunner.InstallIdentity();

        var scratch = Path.Combine(Path.GetTempPath(), "datarepo-orthology-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (root, files) = Extract(inputs["snapshot_tar"], scratch);
            var manifestBytes = File.ReadAllBytes(inputs["snapshot_manifest"]);
            if (!File.Exists(Path.Combine(root, "manifest.json")) || !File.ReadAllBytes(Path.Combine(root, "manifest.json")).SequenceEqual(manifestBytes))
                throw new RunnerException($"the tar's manifest.json is not the manifest given ({snapshot.Tag}).");
            var manifest = CatalogBuilder.ReadJsonObject(inputs["snapshot_manifest"], msg => new RunnerException(msg));
            var summary = Accept(root, files, manifest);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = DefinitionId,
                ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["input_files"] = new Dictionary<string, object?>
                {
                    ["snapshot_tar"] = BundleWriter.PathText(inputs["snapshot_tar"]),
                    ["snapshot_manifest"] = BundleWriter.PathText(inputs["snapshot_manifest"]),
                },
                ["datarepo_install"] = install,
                ["engine_summary"] = summary,
                ["snapshot"] = new Dictionary<string, object?>
                {
                    ["tag"] = snapshot.Tag,
                    ["snapshot_id"] = manifest["snapshot_id"],
                    ["format"] = manifest["format"],
                    ["format_version"] = manifest["format_version"],
                    ["source"] = manifest.GetValueOrDefault("source"),
                    ["release"] = manifest.GetValueOrDefault("release"),
                    ["species"] = manifest["species"],
                    ["gene_set_sha256"] = manifest["gene_set_sha256"],
                    ["directory"] = SnapshotDir,
                    ["views"] = $"{SnapshotDir}/views.sql",
                    ["attribution"] = snapshot.Attribution,
                    ["licence"] = "data CC-BY-4.0 (logs LICENSING.md); views.sql LGPL-3.0 (mzLib)",
                },
                ["acceptance"] = "passed",
            };
            var keep = files.ToDictionary(f => $"{SnapshotDir}/{f}", f => Path.Combine(root, f), StringComparer.Ordinal);
            keep[$"{SnapshotDir}/manifest.json"] = Path.Combine(root, "manifest.json");
            result.Written.Add(EngineRunner.WriteArtefact(store, Engine, aid, record,
                new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(), keep));
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
        return result;
    }

    /// <summary>Unpacks the tar under <paramref name="into"/>: one top-level directory, regular files only.</summary>
    /// <returns>The snapshot directory, and every file in it except <c>manifest.json</c>, as <c>/</c>-separated
    /// paths relative to it.</returns>
    /// <exception cref="RunnerException">Not one top-level directory, a link or device, an absolute or <c>..</c> path,
    /// or a file listed twice.</exception>
    public static (string Root, List<string> Files) Extract(string tar, string into)
    {
        Directory.CreateDirectory(into);
        string? top = null;
        var files = new List<string>();
        using var stream = File.OpenRead(tar);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/').TrimEnd('/');
            var parts = name.Split('/');
            if (name.Length == 0 || name.StartsWith('/') || parts.Any(p => p is ".." or "." or ""))
                throw new RunnerException($"the tar holds an unsafe path {PyFormat.Repr(entry.Name)}");
            if (top is null) top = parts[0];
            else if (parts[0] != top)
                throw new RunnerException($"the tar holds more than one top-level directory ({top}, {parts[0]})");
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || parts.Length < 2)
                throw new RunnerException($"the tar holds {PyFormat.Repr(entry.Name)}, which is not a file in the snapshot directory");
            var relative = string.Join('/', parts[1..]);
            if (files.Contains(relative) || relative == "manifest.json" && File.Exists(Path.Combine(into, top, "manifest.json")))
                throw new RunnerException($"the tar holds {relative} twice");
            var target = Path.Combine(into, top, Path.Combine(parts[1..]));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            if (relative != "manifest.json") files.Add(relative);
        }
        if (top is null) throw new RunnerException("the tar is empty");
        files.Sort(StringComparer.Ordinal);
        return (Path.Combine(into, top), files);
    }

    private static object? G(object? d, string k) => d is IReadOnlyDictionary<string, object?> m && m.TryGetValue(k, out var v) ? v : null;

    /// <summary>logs' file contract, checked on the extracted snapshot.</summary>
    /// <returns>The engine summary: species, files, and each direction's status counts.</returns>
    /// <exception cref="RunnerException">Any check in the class remarks.</exception>
    public static Dictionary<string, object?> Accept(string root, IReadOnlyList<string> files, IReadOnlyDictionary<string, object?> manifest)
    {
        if (!Equals(manifest.GetValueOrDefault("format"), Format))
            throw new RunnerException($"format {PyFormat.Repr(manifest.GetValueOrDefault("format"))}; this runner reads {Format}");
        if (manifest.GetValueOrDefault("format_version") is not long version || version != FormatVersion)
            throw new RunnerException(
                $"format_version {PyFormat.Repr(manifest.GetValueOrDefault("format_version"))}; this runner reads {FormatVersion} only, "
                + "and never reads another version as it.");
        if (manifest.GetValueOrDefault("snapshot_id") is not string { Length: > 0 })
            throw new RunnerException("the manifest has no snapshot_id");
        var species = (manifest.GetValueOrDefault("species") as IEnumerable<object?> ?? []).Select(S).ToList();
        if (species.Count == 0) throw new RunnerException("the manifest lists no species");
        var geneSets = manifest.GetValueOrDefault("gene_set_sha256") as IReadOnlyDictionary<string, object?>;
        if (geneSets is null || !geneSets.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(species))
            throw new RunnerException("the manifest's gene_set_sha256 does not name exactly its species");

        var listed = (manifest.GetValueOrDefault("files") as IEnumerable<object?> ?? []).ToList();
        var byPath = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in listed)
            if (!byPath.TryAdd(S(G(f, "path")), f)) throw new RunnerException($"the manifest lists {S(G(f, "path"))} twice");
        var notListed = files.Where(f => !byPath.ContainsKey(f)).ToList();
        var absent = byPath.Keys.Where(p => !files.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (notListed.Count > 0 || absent.Count > 0)
            throw new RunnerException(
                $"the tar and the manifest disagree: in the tar only [{string.Join(", ", notListed)}], "
                + $"in the manifest only [{string.Join(", ", absent)}]");
        if (!files.Contains("views.sql")) throw new RunnerException("the snapshot has no views.sql");

        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        long Scalar(string sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        string Q(string path) => "'" + path.Replace('\\', '/').Replace("'", "''") + "'";

        foreach (var path in files)
        {
            var entry = byPath[path];
            var full = Path.Combine(root, path);
            var sha = BundleWriter.Sha256File(full);
            if (sha != S(G(entry, "sha256")))
                throw new RunnerException($"{path} hashes to {sha}; the manifest says {S(G(entry, "sha256"))}");
            if (G(entry, "bytes") is not long bytes || bytes != new FileInfo(full).Length)
                throw new RunnerException($"{path} is {new FileInfo(full).Length} bytes; the manifest says {S(G(entry, "bytes"))}");
            if (path.EndsWith(".parquet", StringComparison.Ordinal))
            {
                var rows = Scalar($"SELECT count(*) FROM read_parquet({Q(full)})");
                if (G(entry, "rows") is not long expected || expected != rows)
                    throw new RunnerException($"{path} holds {rows} rows; the manifest says {S(G(entry, "rows"))}");
            }
        }

        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = File.ReadAllText(Path.Combine(root, "views.sql"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception e) when (e is not RunnerException)
        {
            throw new RunnerException($"views.sql does not load in DuckDB: {e.Message}");
        }

        var statuses = string.Join(", ", PairStatuses.Select(s => $"'{s}'"));
        var directions = new List<object?>();
        foreach (var a in species)
        foreach (var b in species.Where(b => b != a))
        {
            var genes = Scalar($"SELECT count(*) FROM read_parquet({Q(Path.Combine(root, "genes", a + ".parquet"))})");
            var status = $"pair_status({Q(root)}, '{a}', '{b}')";
            var rows = Scalar($"SELECT count(*) FROM {status}");
            var distinct = Scalar($"SELECT count(DISTINCT gene_id) FROM {status}");
            var other = Scalar($"SELECT count(*) FROM {status} WHERE status IS NULL OR status NOT IN ({statuses})");
            if (rows != genes || distinct != genes || other != 0)
                throw new RunnerException(
                    $"pair_status({a} -> {b}) does not give each of {a}'s {genes} genes exactly one status: "
                    + $"{rows} rows, {distinct} distinct genes, {other} outside [{statuses}]");
            var counts = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var s in PairStatuses) counts[s] = Scalar($"SELECT count(*) FROM {status} WHERE status = '{s}'");
            directions.Add(new Dictionary<string, object?> { ["from"] = a, ["to"] = b, ["genes"] = genes, ["status_counts"] = counts });
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["species"] = species.Cast<object?>().ToList(),
            ["files"] = (long)files.Count,
            ["pair_status"] = directions,
        };
    }
}
