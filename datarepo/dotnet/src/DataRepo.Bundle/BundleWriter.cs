using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DataRepo.Bundle;

/// <summary>Collects tables and source files, then writes them under a content-addressed directory.</summary>
/// <remarks>
/// Layer 1 of the architecture: the files are the product. One directory per dataset-run, one Parquet
/// file per table, the producer's own metadata files copied in beside them, and a <c>bundle.json</c>
/// that says exactly which inputs produced it and what came out. The directory name is a content hash,
/// so re-ingesting unchanged inputs with an unchanged ingester lands on the same path, and changed
/// inputs cannot quietly overwrite a bundle somebody already cited. Ported from <c>bundle.py</c>.
/// </remarks>
/// <param name="store">The instance's bundle store, e.g. <c>F:/aging_data/repo/store</c>.</param>
/// <param name="datasetId">ProteomeXchange accession.</param>
/// <param name="ingestPath">The ingest-path version hashed into the id; defaults to <see cref="IngesterVersion"/>.
/// A parity run passes its own marked value so its ids can never collide with a real bundle's.</param>
public sealed class BundleWriter(string store, string datasetId, string? ingestPath = null)
{
    /// <summary>The version of the C# INGEST PATH, and the only version in a bundle's content hash.</summary>
    /// <remarks>
    /// Deliberately not the package version: a bundle id must move when the ingester reads a file
    /// differently or writes a different row, and must NOT move because something else in the package
    /// changed. <b>Bump it in the same commit as any change to what an ingest reads, parses, derives or
    /// writes.</b> The C# line starts apart from the Python one (which ended at 0.22.0) so a C# bundle id
    /// can never collide with a Python one; the operator re-ingests once at the switch (D41). cs-1.0.0 is the
    /// first released C# ingest path: G76 (D40), G81, D37 and covered_zero, on schema 0.0.14.
    /// </remarks>
    public const string IngesterVersion = "cs-1.0.0";

    /// <summary>QPX release the column names are written against (D4). Provisional (G13).</summary>
    public const string QpxVersion = "unpinned";

    public const string ManifestName = "bundle.json";
    public const string SourcesDir = "sources";

    private readonly Dictionary<string, List<IReadOnlyDictionary<string, object?>>> _tables = new(StringComparer.Ordinal);
    private readonly List<OrderedDictionary<string, object?>> _sources = [];
    private readonly List<(string Source, string CopyAs)> _copied = [];

    public string Store { get; } = store;

    /// <summary>The ingest-path version this bundle's id is computed from.</summary>
    public string IngestPath { get; } = ingestPath ?? IngesterVersion;
    public string DatasetId { get; } = datasetId;

    /// <summary>Top-level <c>bundle.json</c> keys added after the fixed ones (readers, reconciliation, ...).</summary>
    public OrderedDictionary<string, object?> Notes { get; } = new(StringComparer.Ordinal);

    /// <summary>The rows collected so far, by table.</summary>
    public IReadOnlyDictionary<string, List<IReadOnlyDictionary<string, object?>>> TableRows => _tables;

    /// <summary>The recorded inputs, in the order they were added.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Sources => _sources;

    /// <summary>Appends rows to a table.</summary>
    /// <exception cref="IngestException">The schema has no such table.</exception>
    public void Add(string table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        if (!Tables.ByName.ContainsKey(table))
            throw new IngestException($"no table named '{table}' in schema {SchemaContract.Version}");
        if (!_tables.TryGetValue(table, out var list))
            _tables[table] = list = [];
        list.AddRange(rows);
    }

    /// <summary>Records an input file by hash, and optionally copies it into the bundle.</summary>
    /// <param name="path">The producer's file.</param>
    /// <param name="role">What it was read for, e.g. <c>provenance:search</c>.</param>
    /// <param name="copyAs">Its name inside the bundle's <c>sources/</c>; null to record without copying.</param>
    /// <returns>The recorded entry, whose <c>sha256</c> and <c>bundle_path</c> rows can point at.</returns>
    public IReadOnlyDictionary<string, object?> AddSource(string path, string role, string? copyAs = null)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = role,
            ["path"] = PathText(path),
            ["size_bytes"] = new FileInfo(path).Length,
            ["sha256"] = Sha256File(path),
        };
        if (copyAs is not null)
        {
            entry["bundle_path"] = $"{SourcesDir}/{copyAs}";
            _copied.Add((path, copyAs));
        }
        _sources.Add(entry);
        return entry;
    }

    /// <summary>Records an input whose sha256 the caller already has, without copying it: a protein
    /// database, hashed once to check it against the search provenance and archived by the producer.</summary>
    public IReadOnlyDictionary<string, object?> AddHashedSource(string path, string role, string sha256)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = role,
            ["path"] = PathText(path),
            ["size_bytes"] = new FileInfo(path).Length,
            ["sha256"] = sha256,
        };
        _sources.Add(entry);
        return entry;
    }

    /// <summary>Records a non-file input that shapes the bundle, so it lands in the content hash.</summary>
    /// <remarks>The producer's manifest entry supplies the dataset's title and axes, which go straight
    /// into the <c>datasets</c> row, so a bundle built from an edited entry holds different content. The
    /// entry is hashed rather than the manifest file, so an unrelated entry's edit cannot churn this id.
    /// The bytes hashed are Python's <c>json.dumps(payload, sort_keys=True, default=str,
    /// ensure_ascii=False)</c>, so the same declaration gives the same hash in either implementation.</remarks>
    public IReadOnlyDictionary<string, object?> AddDeclaration(string role, object? payload)
    {
        var canonical = PyFormat.Json(payload, sortKeys: true, ensureAscii: false);
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = role,
            ["path"] = $"<{role}>",
            ["kind"] = "declaration",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
        };
        _sources.Add(entry);
        return entry;
    }

    /// <summary>Content hash of the inputs, the schema and the ingest path.</summary>
    public string BundleId => ComputeBundleId(IngestPath, SchemaContract.Version, DatasetId, _sources);

    /// <summary>The bundle id rule, exposed so a stored bundle's id can be recomputed from its manifest.</summary>
    /// <remarks>sha256 over <c>datarepo/&lt;ingest path&gt;\nschema/&lt;schema&gt;\n&lt;dataset&gt;\n</c>, then
    /// <c>role\tsha256\n</c> per source, sources sorted by (role, path) ordinally; the first 16 hex
    /// digits. The same rule as <c>BundleWriter.bundle_id</c> in Python.</remarks>
    public static string ComputeBundleId(
        string ingestPath, string schemaVersion, string datasetId,
        IEnumerable<IReadOnlyDictionary<string, object?>> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"datarepo/{ingestPath}\nschema/{schemaVersion}\n{datasetId}\n"));
        var ordered = sources
            .OrderBy(e => (string)e["role"]!, StringComparer.Ordinal)
            .ThenBy(e => (string)e["path"]!, StringComparer.Ordinal);
        foreach (var entry in ordered)
            hash.AppendData(Encoding.UTF8.GetBytes($"{entry["role"]}\t{entry["sha256"]}\n"));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>The directory the bundle is written to: <c>&lt;store&gt;/&lt;dataset&gt;/&lt;bundle id&gt;</c>.</summary>
    public string BundlePath() => PathText(Path.Combine(Store, DatasetId, BundleId));

    /// <summary>Writes every table, copies the recorded sources, and emits <c>bundle.json</c>.</summary>
    /// <exception cref="IngestException">The bundle is already written and <paramref name="overwrite"/> is
    /// false, or a reference between the tables does not resolve.</exception>
    public string Write(bool overwrite = false)
    {
        var output = BundlePath();
        var manifestPath = Path.Combine(output, ManifestName);
        if (File.Exists(manifestPath) && !overwrite)
            throw new IngestException(
                $"{output} already exists. The inputs and the ingester are unchanged, so this bundle "
                + "is already written; pass --overwrite to rebuild it in place.");

        var problems = Integrity.Check(_tables.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)kv.Value, StringComparer.Ordinal));
        if (problems.Count > 0)
            throw new IngestException(
                "the bundle's own tables do not hold together, so nothing was written:\n  - "
                + string.Join("\n  - ", problems));

        Directory.CreateDirectory(Path.Combine(output, SourcesDir));
        var rowCounts = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var spec in Tables.Core)
        {
            if (!_tables.TryGetValue(spec.Name, out var rows) || rows.Count == 0) continue;
            var batch = ArrowTables.FromRows(spec.Name, SchemaContract.Columns(spec), rows);
            ArrowTables.WriteParquet(batch, Path.Combine(output, $"{spec.Name}.parquet"));
            rowCounts[spec.Name] = (long)batch.Length;
        }
        foreach (var (source, copyAs) in _copied)
            File.Copy(source, Path.Combine(output, SourcesDir, copyAs), overwrite: true);

        var manifest = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bundle_id"] = BundleId,
            ["dataset_id"] = DatasetId,
            ["schema_version"] = SchemaContract.Version,
            ["qpx_version"] = QpxVersion,
            // Both versions, because they answer different questions: `version` is what an operator
            // installed, `ingest_path` is what the bundle id was computed from.
            ["ingester"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = "datarepo",
                ["version"] = PackageVersion,
                ["ingest_path"] = IngestPath,
            },
            ["written_utc"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'"),
            ["tables"] = rowCounts,
            ["sources"] = _sources,
        };
        foreach (var (key, value) in Notes) manifest[key] = value;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson) + "\n", new UTF8Encoding(false));
        return output;
    }

    /// <summary>The installed package's version, written as <c>ingester.version</c>.</summary>
    public static string PackageVersion { get; } =
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(BundleWriter).Assembly)?.InformationalVersion ?? "0.0.0-dev";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>SHA-256 of a file, streamed, as lowercase hex.</summary>
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A path as Python's <c>str(Path)</c> writes it on this platform: on Windows, separators are
    /// backslashes. It matters beyond display, because sources are hashed in (role, path) order.</summary>
    public static string PathText(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
}
