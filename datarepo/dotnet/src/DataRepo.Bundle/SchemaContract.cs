namespace DataRepo.Bundle;

/// <summary>The core schema version this build writes, hashes and accepts.</summary>
/// <remarks>
/// <para>It is <see cref="Tables.SchemaVersion"/>, except inside a <see cref="Python0320"/> scope, which pins the
/// schema Python 0.32.0 wrote (0.0.13). That scope exists for parity alone: the C# release writes a later schema
/// (0.0.14 added one enum value, <c>OccupancyState.covered_zero</c>; 0.0.15 added four <c>runs</c> columns, G87).
/// Inside the scope a table is written with 0.0.13's columns (<see cref="Columns"/>, from
/// <see cref="AddedColumns"/>), so a parity ingest (<c>IngestRules.Python0320</c>) writes the files Python wrote,
/// and a Python-written bundle, catalog or study layer can still be rebuilt and compared byte for byte.</para>
/// <para>The scope is an <see cref="AsyncLocal{T}"/>: it covers the code that opened it and whatever that code
/// awaits or starts, never a concurrent caller.</para>
/// </remarks>
public static class SchemaContract
{
    /// <summary>The core schema version Python 0.32.0 wrote.</summary>
    public const string Python0320SchemaVersion = "0.0.13";

    private static readonly AsyncLocal<string?> Pinned = new();

    /// <summary>The schema version in force here.</summary>
    public static string Version => Pinned.Value ?? Tables.SchemaVersion;

    /// <summary>True inside a <see cref="Python0320"/> scope: what is built must be what Python 0.32.0 built, so
    /// anything 0.32.0 did not have (the catalog's go engine tables, catalog version 9) is left out.</summary>
    public static bool IsPython0320 => Pinned.Value == Python0320SchemaVersion;

    /// <summary>Every core column added after Python 0.32.0's schema 0.0.13, with the version that added it.</summary>
    /// <remarks>A parity bundle must hold exactly the columns Python wrote, so a column added since cannot be
    /// written there even as all-NULL: the Parquet schema would differ, and so would a catalog built from it.
    /// Add a column's entry in the commit that adds the column; the parity tests fail on any one left out.</remarks>
    public static readonly IReadOnlyList<(string Version, string Table, string Column)> AddedColumns =
    [
        ("0.0.15", "runs", "instrument_model_source"),
        ("0.0.15", "runs", "instrument_model_accession"),
        ("0.0.15", "runs", "instrument_serial"),
        ("0.0.15", "runs", "acquisition_start_local"),
    ];

    /// <summary>The table's columns as schema <paramref name="version"/> has them, in the schema's order.</summary>
    public static IReadOnlyList<ColumnSpec> ColumnsAt(TableSpec spec, string version)
    {
        var at = System.Version.Parse(version);
        var later = AddedColumns
            .Where(a => a.Table == spec.Name && System.Version.Parse(a.Version) > at)
            .Select(a => a.Column)
            .ToHashSet(StringComparer.Ordinal);
        return later.Count == 0 ? spec.Columns : spec.Columns.Where(c => !later.Contains(c.Name)).ToList();
    }

    /// <summary>The table's columns under the schema version in force here (<see cref="Version"/>).</summary>
    public static IReadOnlyList<ColumnSpec> Columns(TableSpec spec) => ColumnsAt(spec, Version);

    /// <summary>Pins Python 0.32.0's schema version until the result is disposed.</summary>
    public static IDisposable Python0320()
    {
        var previous = Pinned.Value;
        Pinned.Value = Python0320SchemaVersion;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Pinned.Value = previous;
    }
}
