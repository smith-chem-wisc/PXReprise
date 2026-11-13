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
        ("0.0.16", "assays", "assay_kind"),
        ("0.0.16", "assays", "design_label"),
        ("0.0.16", "ptm_sites", "sequence_window"),
    ];

    /// <summary>Every core table added after Python 0.32.0's schema 0.0.13, with the version that added it.</summary>
    /// <remarks>A parity bundle or catalog holds exactly the tables Python wrote, so a table added since is not even
    /// created empty there. Add a table's entry in the commit that adds the table.</remarks>
    public static readonly IReadOnlyList<(string Version, string Table)> AddedTables =
    [
        ("0.0.16", "assay_runs"),
    ];

    /// <summary>False for a core table added after the schema version in force here; true for any other name.</summary>
    public static bool TableInForce(string table)
    {
        var at = System.Version.Parse(Version);
        return !AddedTables.Any(a => a.Table == table && System.Version.Parse(a.Version) > at);
    }

    /// <summary>The core tables under the schema version in force here (<see cref="Version"/>), in the schema's
    /// order: <see cref="Tables.Core"/> less any table added after it.</summary>
    public static IReadOnlyList<TableSpec> CoreTables
    {
        get
        {
            var at = System.Version.Parse(Version);
            var later = AddedTables.Where(a => System.Version.Parse(a.Version) > at).Select(a => a.Table).ToHashSet(StringComparer.Ordinal);
            return later.Count == 0 ? Tables.Core : Tables.Core.Where(t => !later.Contains(t.Name)).ToList();
        }
    }

    /// <summary>Every core column that was required in Python 0.32.0's schema and is optional since, with the version
    /// that relaxed it. A parity bundle writes it as required, as Python did (the Parquet field's nullability).</summary>
    /// <remarks>provenance_schema: a record stating no schema was stored as the text "None", a value it never wrote
    /// (G83 item 16); from 0.0.15 it is NULL. assays.run_id: NULL from 0.0.16 for a pooled design sample (PXR-R23).</remarks>
    public static readonly IReadOnlyList<(string Version, string Table, string Column)> RelaxedColumns =
    [
        ("0.0.15", "provenance_records", "provenance_schema"),
        ("0.0.16", "assays", "run_id"),
    ];

    /// <summary>The table's columns as schema <paramref name="version"/> has them, in the schema's order.</summary>
    public static IReadOnlyList<ColumnSpec> ColumnsAt(TableSpec spec, string version)
    {
        var at = System.Version.Parse(version);
        var later = AddedColumns
            .Where(a => a.Table == spec.Name && System.Version.Parse(a.Version) > at)
            .Select(a => a.Column)
            .ToHashSet(StringComparer.Ordinal);
        var stillRequired = RelaxedColumns
            .Where(r => r.Table == spec.Name && System.Version.Parse(r.Version) > at)
            .Select(r => r.Column)
            .ToHashSet(StringComparer.Ordinal);
        if (later.Count == 0 && stillRequired.Count == 0) return spec.Columns;
        return spec.Columns
            .Where(c => !later.Contains(c.Name))
            .Select(c => stillRequired.Contains(c.Name) ? c with { Nullable = false } : c)
            .ToList();
    }

    /// <summary>The table's columns under the schema version in force here (<see cref="Version"/>).</summary>
    public static IReadOnlyList<ColumnSpec> Columns(TableSpec spec) => ColumnsAt(spec, Version);

    /// <summary>Each study layer's version as Python 0.32.0 wrote it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Python0320StudyVersions =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["aging"] = "0.5.0" };

    /// <summary>Every study table added after Python 0.32.0's layer versions, with the layer version that added it.</summary>
    /// <remarks>A parity catalog holds exactly the tables Python built, so a table added since is not even created
    /// empty there. Add a table's entry in the commit that adds the table; the parity tests fail on one left out.</remarks>
    public static readonly IReadOnlyList<(string Layer, string Version, string Table)> AddedStudyTables =
    [
        ("aging", "0.6.0", "curated_run_design"),
        ("aging", "0.6.0", "curated_design_factors"),
    ];

    /// <summary>The layer's version in force here: <see cref="Tables.StudyVersions"/>, or Python 0.32.0's inside a
    /// <see cref="Python0320"/> scope.</summary>
    public static string StudyVersion(string layer) =>
        IsPython0320 && Python0320StudyVersions.TryGetValue(layer, out var pinned) ? pinned : Tables.StudyVersions[layer];

    /// <summary>The layer's tables at the version in force here (<see cref="StudyVersion"/>), in the schema's order.</summary>
    public static IReadOnlyList<TableSpec> StudyTables(string layer)
    {
        var at = System.Version.Parse(StudyVersion(layer));
        var later = AddedStudyTables
            .Where(a => a.Layer == layer && System.Version.Parse(a.Version) > at)
            .Select(a => a.Table)
            .ToHashSet(StringComparer.Ordinal);
        return later.Count == 0 ? Tables.Study[layer] : Tables.Study[layer].Where(t => !later.Contains(t.Name)).ToList();
    }

    /// <summary>Every study layer with its tables at the version in force here, in <see cref="Tables.Study"/>'s order.</summary>
    public static IEnumerable<(string Layer, IReadOnlyList<TableSpec> Tables)> StudyLayers =>
        Tables.Study.Keys.Select(layer => (layer, StudyTables(layer)));

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
