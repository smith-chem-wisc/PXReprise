using System.Text.Json;
using PXReprise.Bundle;
using ParquetSharp;
using PXReprise.Storage;

namespace PXReprise.DataCli;

/// <summary>The kinds of folder a store holds, each complete once its record file exists.</summary>
public enum ItemKind
{
    /// <summary>A search bundle, <c>&lt;dataset&gt;/&lt;bundle_id&gt;/bundle.json</c>.</summary>
    Bundle,
    /// <summary>An engine's output (orthology, GO, ptmQtl, ...), <c>_engine/&lt;engine&gt;/&lt;artefact_id&gt;/run.json</c>.</summary>
    Engine,
    /// <summary>A study delivery, <c>_study/&lt;layer&gt;/&lt;id&gt;/study.json</c>.</summary>
    Study,
}

/// <summary>One complete folder in a store: a bundle, an engine's output or a study delivery.</summary>
/// <param name="Group">The dataset for a bundle, the engine for engine output, the layer for a study delivery.</param>
/// <param name="Id">The bundle id, artefact id or study bundle id.</param>
public sealed record StoreItem(ItemKind Kind, string Group, string Id)
{
    /// <summary>The engine output folder, as <c>PXReprise.Catalog.Runner.EngineDir</c> names it.</summary>
    public const string EngineDir = "_engine";

    /// <summary>The engine output's record, as <c>PXReprise.Catalog.Runner.RunRecord</c> names it.</summary>
    public const string EngineRecord = "run.json";

    /// <summary>The study folder, as <c>PXReprise.Study.StudyWriter.StudyDir</c> names it.</summary>
    public const string StudyDir = "_study";

    /// <summary>The study delivery's record, as <c>PXReprise.Study.StudyWriter.StudyBundleManifest</c> names it.</summary>
    public const string StudyRecord = "study.json";

    public static StoreItem Bundle(string dataset, string bundleId) => new(ItemKind.Bundle, dataset, bundleId);

    /// <summary>The dataset, for a bundle; null otherwise.</summary>
    public string? Dataset => Kind == ItemKind.Bundle ? Group : null;

    public string Folder => Kind switch
    {
        ItemKind.Bundle => $"{Group}/{Id}/",
        ItemKind.Engine => $"{EngineDir}/{Group}/{Id}/",
        _ => $"{StudyDir}/{Group}/{Id}/",
    };

    /// <summary>The file written last: once it exists, the folder exists for every reader.</summary>
    public string Record => Folder + Kind switch
    {
        ItemKind.Bundle => BundleWriter.ManifestName,
        ItemKind.Engine => EngineRecord,
        _ => StudyRecord,
    };

    public override string ToString() => Folder.TrimEnd('/');

    /// <summary>The item whose record is <paramref name="key"/>, or null when the key is no record (or sits in a work
    /// folder: a name starting with <c>.</c> is one being written, such as an engine's <c>.&lt;id&gt;.writing</c>).</summary>
    public static StoreItem? FromRecordKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Any(p => p.StartsWith('.'))) return null;
        if (parts.Length == 3 && parts[2] == BundleWriter.ManifestName && !parts[0].StartsWith('_'))
            return Bundle(parts[0], parts[1]);
        if (parts.Length == 4 && parts[0] == EngineDir && parts[3] == EngineRecord)
            return new StoreItem(ItemKind.Engine, parts[1], parts[2]);
        if (parts.Length == 4 && parts[0] == StudyDir && parts[3] == StudyRecord)
            return new StoreItem(ItemKind.Study, parts[1], parts[2]);
        return null;
    }
}

/// <summary>What happened to one item.</summary>
public enum TransferOutcome
{
    /// <summary>Every file and then the record were written.</summary>
    Copied,
    /// <summary>The destination already held the item (its record exists); nothing was read or written.</summary>
    Present,
    /// <summary>A dry run: it would be copied.</summary>
    WouldCopy,
}

/// <summary>
/// Copies whole bundles, engine outputs and study deliveries from one store to another: a local store to S3 (push), S3
/// to a local store (pull), or folder to folder.
/// </summary>
/// <remarks>
/// <para><b>A folder exists in a store once its record does</b> (<c>bundle.json</c>, <c>run.json</c>,
/// <c>study.json</c>; for bundles this is <see cref="BundleWriter"/>'s guarantee, PXReprise PXR-R26). So the record is
/// written last, after every other file: a copy that stops halfway leaves files no reader counts, and running it again
/// finishes it, because each file is written once and an identical file already there is accepted.</para>
/// <para>A bundle's files are checked against its <c>bundle.json</c> before they are written: a file the record gives a
/// <c>sha256</c> for (a copied source, and a table once 1.5.0 records one, dataRepo D61) must have it, and a table's
/// Parquet row count must equal its count in <c>tables</c>. A file the record does not name is copied as it is. A
/// bundle that does not match is refused and nothing marks it complete. Engine outputs and study deliveries are copied
/// as they are; every file still carries its sha256 to the destination, which S3 checks on arrival.</para>
/// </remarks>
public static class StoreTransfer
{
    /// <summary>The complete items in the store, sorted. With <paramref name="datasets"/>, only those datasets'
    /// bundles; without, every bundle, engine output and study delivery.</summary>
    public static async Task<IReadOnlyList<StoreItem>> FindAsync(IStore store, IReadOnlyCollection<string>? datasets = null,
        CancellationToken ct = default)
    {
        var found = new List<StoreItem>();
        var prefixes = datasets is { Count: > 0 } ? datasets.Select(d => StoreKey.Check(d) + "/") : [""];
        foreach (var prefix in prefixes)
            await foreach (var entry in store.ListAsync(prefix, ct))
                if (StoreItem.FromRecordKey(entry.Key) is { } item && (prefix.Length == 0 || item.Kind == ItemKind.Bundle))
                    found.Add(item);
        return found.Distinct()
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Group, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Copies one item unless the destination already has it.</summary>
    public static async Task<TransferOutcome> CopyAsync(IStore from, IStore to, StoreItem item, bool dryRun = false,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (await to.ExistsAsync(item.Record, ct)) return TransferOutcome.Present;

        var scratch = Path.Combine(Path.GetTempPath(), "datarepo-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var recordFile = await Fetch(from, item.Record, scratch, ct);
            var record = item.Kind == ItemKind.Bundle ? BundleRecord.Read(recordFile, item) : BundleRecord.None(recordFile, item);
            var files = new List<StoreEntry>();
            await foreach (var entry in from.ListAsync(item.Folder, ct))
                if (entry.Key != item.Record) files.Add(entry);
            files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            // A table with no rows is not written (D10), so only a table with rows must have its file.
            foreach (var (table, expected) in record.Tables)
                if (expected.Rows is not 0 && !files.Any(f => f.Key == $"{item.Folder}{table}.parquet"))
                    throw new DataRepoException($"{from.Location}: {item}: bundle.json records table '{table}' but {table}.parquet is missing");
            if (dryRun) return TransferOutcome.WouldCopy;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var local = await Fetch(from, file.Key, scratch, ct);
                var sha = BundleWriter.Sha256File(local);
                record.Verify(file.Key[item.Folder.Length..], local, sha, from.Location);
                var outcome = await to.WriteOnceAsync(file.Key, local, sha, ct);
                log?.Invoke($"  {(outcome == WriteOutcome.Written ? "wrote  " : "present")} {file.Key}");
                if (from.LocalFile(file.Key) is null) File.Delete(local);
            }
            await to.WriteOnceAsync(item.Record, recordFile, BundleWriter.Sha256File(recordFile), ct);
            log?.Invoke($"  wrote   {item.Record}");
            return TransferOutcome.Copied;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The object as a local file: the store's own file when it has one, otherwise a download into
    /// <paramref name="scratch"/>.</summary>
    private static async Task<string> Fetch(IStore store, string key, string scratch, CancellationToken ct)
    {
        if (store.LocalFile(key) is { } path)
        {
            if (!File.Exists(path)) throw new DataRepoException($"{store.Location}: {key} is missing");
            return path;
        }
        var local = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        await store.DownloadAsync(key, local, ct);
        return local;
    }

    /// <summary>What a table's entry in <c>tables</c> says: its rows, and its sha256 when the bundle records one.</summary>
    private sealed record TableRecord(long? Rows, string? Sha256);

    /// <summary>What a bundle's <c>bundle.json</c> says its files are.</summary>
    private sealed class BundleRecord
    {
        public required IReadOnlyDictionary<string, TableRecord> Tables { get; init; }

        /// <summary>sha256 of each file the record hashes, by its path inside the bundle.</summary>
        public required IReadOnlyDictionary<string, string> Hashes { get; init; }

        /// <summary>An engine output's or study delivery's record: it must parse, and it checks no file.</summary>
        public static BundleRecord None(string recordFile, StoreItem item)
        {
            try { using var _ = JsonDocument.Parse(File.ReadAllText(recordFile)); }
            catch (JsonException e) { throw new DataRepoException($"{item}: its record is not JSON: {e.Message}"); }
            return new BundleRecord { Tables = new Dictionary<string, TableRecord>(), Hashes = new Dictionary<string, string>() };
        }

        /// <remarks>A table's entry in <c>tables</c> is its row count (every bundle up to 1.4.x) or an object holding
        /// <c>rows</c> and <c>sha256</c> (the shape proposed for 1.5.0's per-table hash, D61; adjust here if 1.5.0
        /// settles on another).</remarks>
        public static BundleRecord Read(string manifestFile, StoreItem bundle)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
            var root = doc.RootElement;
            string? Text(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Text(root, "bundle_id") != bundle.Id || Text(root, "dataset_id") != bundle.Group)
                throw new DataRepoException(
                    $"{bundle}: bundle.json names {Text(root, "dataset_id")}/{Text(root, "bundle_id")}; a bundle's folder must be <dataset_id>/<bundle_id>");
            var tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tables", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var p in t.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        tables[p.Name] = new TableRecord(p.Value.GetInt64(), null);
                    else if (p.Value.ValueKind == JsonValueKind.Object)
                    {
                        long? rows = p.Value.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null;
                        var sha = Text(p.Value, "sha256");
                        tables[p.Name] = new TableRecord(rows, sha);
                        if (sha is not null) hashes[p.Name + ".parquet"] = sha;
                    }
                }
            if (root.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (var source in s.EnumerateArray())
                    if (Text(source, "bundle_path") is { } path && Text(source, "sha256") is { } h)
                        hashes[path] = h;
            return new BundleRecord { Tables = tables, Hashes = hashes };
        }

        public void Verify(string relative, string localFile, string sha, string where)
        {
            if (Hashes.TryGetValue(relative, out var recorded) && !string.Equals(recorded, sha, StringComparison.OrdinalIgnoreCase))
                throw new DataRepoException($"{where}: {relative} has sha256 {sha}; its bundle.json records {recorded}. Not copied.");
            if (relative.EndsWith(".parquet", StringComparison.Ordinal) && !relative.Contains('/')
                && Tables.TryGetValue(relative[..^".parquet".Length], out var table) && table.Rows is { } rows)
            {
                long actual;
                using (var reader = new ParquetFileReader(localFile)) actual = reader.FileMetaData.NumRows;
                if (actual != rows)
                    throw new DataRepoException($"{where}: {relative} holds {actual} rows; its bundle.json records {rows}. Not copied.");
            }
        }
    }
}
