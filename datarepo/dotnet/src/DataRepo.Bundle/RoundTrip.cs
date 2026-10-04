using System.Collections;
using System.Text.Json;
using Apache.Arrow;

namespace DataRepo.Bundle;

/// <summary>What happened to one table when a stored bundle was read and rewritten.</summary>
public sealed record TableRoundTrip(
    string Table,
    long Rows,
    bool SchemaMatchesSpec,
    bool SchemaMatchesOriginal,
    bool RowsIdentical,
    string? FirstDifference);

/// <summary>The result of reading a stored bundle and rewriting every table through the C# writer.</summary>
public sealed record BundleRoundTrip(
    string BundleDir,
    string StoredId,
    string RecomputedId,
    string SchemaVersion,
    IReadOnlyList<TableRoundTrip> Tables,
    IReadOnlyList<string> IntegrityProblems,
    IReadOnlyList<string> UnknownTables)
{
    /// <summary>True when the id recomputes, every table matches, and the bundle holds together.</summary>
    public bool Passed =>
        StoredId == RecomputedId
        && UnknownTables.Count == 0
        && IntegrityProblems.Count == 0
        && Tables.All(t => t.SchemaMatchesSpec && t.SchemaMatchesOriginal && t.RowsIdentical);
}

/// <summary>Phase 1's parity check: a bundle written by the Python 0.32.0 reads and rewrites identically.</summary>
/// <remarks>
/// For each table: the stored Arrow schema must equal the generated spec (names, types, nullability);
/// the rows are coerced and written again by <see cref="ArrowTables"/>; the rewritten file's schema
/// must equal the original's; and every cell must be equal, compared exactly. The bundle id is
/// recomputed from <c>bundle.json</c>'s own sources and ingest path, and the integrity checks are run
/// on what was read. This tests by consuming the output, not by validating it.
/// </remarks>
public static class RoundTrip
{
    /// <summary>Round-trips one stored bundle, writing the rewritten tables under <paramref name="scratchDir"/>.</summary>
    public static BundleRoundTrip Verify(string bundleDir, string scratchDir)
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleDir, BundleWriter.ManifestName)));
        var manifest = manifestDoc.RootElement;
        var storedId = manifest.GetProperty("bundle_id").GetString()!;
        var schemaVersion = manifest.GetProperty("schema_version").GetString()!;
        var ingestPath = manifest.GetProperty("ingester").GetProperty("ingest_path").GetString()!;
        var datasetId = manifest.GetProperty("dataset_id").GetString()!;
        var sources = manifest.GetProperty("sources").EnumerateArray()
            .Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["role"] = s.GetProperty("role").GetString(),
                ["path"] = s.GetProperty("path").GetString(),
                ["sha256"] = s.GetProperty("sha256").GetString(),
            })
            .ToList();
        var recomputed = BundleWriter.ComputeBundleId(ingestPath, schemaVersion, datasetId, sources);

        Directory.CreateDirectory(scratchDir);
        var results = new List<TableRoundTrip>();
        var unknown = new List<string>();
        var allRows = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(bundleDir, "*.parquet").OrderBy(f => f, StringComparer.Ordinal))
        {
            var table = Path.GetFileNameWithoutExtension(file);
            if (!Tables.ByName.TryGetValue(table, out var spec))
            {
                unknown.Add(table);
                continue;
            }
            var (schema, rows) = ArrowTables.ReadParquet(file);
            allRows[table] = rows;
            var specSchema = spec.ToArrowSchema();
            var rewrittenPath = Path.Combine(scratchDir, Path.GetFileName(file));
            ArrowTables.WriteParquet(ArrowTables.FromRows(table, spec.Columns, rows), rewrittenPath);
            var (rewrittenSchema, rewrittenRows) = ArrowTables.ReadParquet(rewrittenPath);
            results.Add(new TableRoundTrip(
                table,
                rows.Count,
                SameSchema(schema, specSchema),
                SameSchema(schema, rewrittenSchema),
                FirstDifference(rows, rewrittenRows) is null,
                FirstDifference(rows, rewrittenRows) ?? DescribeSchemaDifference(schema, specSchema, rewrittenSchema)));
        }

        return new BundleRoundTrip(
            bundleDir, storedId, recomputed, schemaVersion, results, Integrity.Check(allRows), unknown);
    }

    /// <summary>Compares two bundles table by table (phase 2: Python's and C#'s ingest of the same inputs).</summary>
    /// <returns>One line per table that differs (schema, row count, or first differing cell); empty when the
    /// two hold the same rows in the same order. Ids and <c>bundle.json</c> are not compared.</returns>
    public static List<string> CompareBundles(string expectedDir, string actualDir)
    {
        var differences = new List<string>();
        var expected = Directory.GetFiles(expectedDir, "*.parquet").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var actual = Directory.GetFiles(actualDir, "*.parquet").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expected.Except(actual).Order(StringComparer.Ordinal))
            differences.Add($"{missing}: only in the expected bundle");
        foreach (var extra in actual.Except(expected).Order(StringComparer.Ordinal))
            differences.Add($"{extra}: only in the actual bundle");
        foreach (var file in expected.Intersect(actual).Order(StringComparer.Ordinal))
        {
            var (schemaA, rowsA) = ArrowTables.ReadParquet(Path.Combine(expectedDir, file!));
            var (schemaB, rowsB) = ArrowTables.ReadParquet(Path.Combine(actualDir, file!));
            if (!SameSchema(schemaA, schemaB))
                differences.Add($"{file}: schemas differ");
            if (FirstDifference(rowsA, rowsB) is { } difference)
                differences.Add($"{file}: {difference}");
        }
        return differences;
    }

    /// <summary>Two Arrow schemas with the same fields, in order, with the same types and nullability.</summary>
    public static bool SameSchema(Schema a, Schema b) =>
        a.FieldsList.Count == b.FieldsList.Count
        && a.FieldsList.Zip(b.FieldsList).All(p =>
            p.First.Name == p.Second.Name
            && p.First.IsNullable == p.Second.IsNullable
            && TypeText(p.First.DataType) == TypeText(p.Second.DataType));

    private static string TypeText(Apache.Arrow.Types.IArrowType type) => type switch
    {
        Apache.Arrow.Types.ListType l => $"list<{TypeText(l.ValueDataType)}>",
        Apache.Arrow.Types.TimestampType t => $"timestamp[{t.Unit}, {t.Timezone}]",
        _ => type.Name,
    };

    private static string? DescribeSchemaDifference(Schema original, Schema spec, Schema rewritten)
    {
        foreach (var (label, other) in new[] { ("spec", spec), ("rewritten", rewritten) })
        {
            if (SameSchema(original, other)) continue;
            var names = original.FieldsList.Select(f => $"{f.Name}:{TypeText(f.DataType)}{(f.IsNullable ? "?" : "")}");
            var otherNames = other.FieldsList.Select(f => $"{f.Name}:{TypeText(f.DataType)}{(f.IsNullable ? "?" : "")}");
            return $"schema differs from {label}: stored [{string.Join(", ", names.Except(otherNames))}] vs [{string.Join(", ", otherNames.Except(names))}]";
        }
        return null;
    }

    /// <summary>The first cell that differs between two row lists, or null if they are identical.</summary>
    public static string? FirstDifference(IReadOnlyList<IReadOnlyDictionary<string, object?>> a, IReadOnlyList<IReadOnlyDictionary<string, object?>> b)
    {
        if (a.Count != b.Count) return $"row count {a.Count} vs {b.Count}";
        for (var i = 0; i < a.Count; i++)
        {
            foreach (var (column, value) in a[i])
            {
                var other = b[i].TryGetValue(column, out var v) ? v : null;
                if (!CellEquals(value, other))
                    return $"row {i} column {column}: {Show(value)} vs {Show(other)}";
            }
            if (b[i].Keys.Except(a[i].Keys).FirstOrDefault() is { } extra)
                return $"row {i}: column {extra} only in the rewritten table";
        }
        return null;
    }

    /// <summary>Exact equality; lists compare element by element; a double compares by its bits.</summary>
    public static bool CellEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is double da && b is double db) return BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits(db);
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count) return false;
            for (var i = 0; i < la.Count; i++)
                if (!CellEquals(la[i], lb[i])) return false;
            return true;
        }
        return a.GetType() == b.GetType() && a.Equals(b);
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        IList l => "[" + string.Join(", ", l.Cast<object?>().Select(Show)) + "]",
        _ => $"{PyFormat.Str(v)} ({v.GetType().Name})",
    };
}
