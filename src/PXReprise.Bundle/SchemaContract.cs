namespace PXReprise.Bundle;

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
        ("0.0.17", "datasets", "confidence_rule"),
        ("0.0.17", "peptidoforms", "best_pep_q_value"),
        ("0.0.17", "gene_resolutions", "ensembl_xref_agreement"),
        ("0.0.17", "runs", "reporter_level"),
        ("0.0.17", "assays", "plex"),
        ("0.0.17", "assays", "design_sample_type"),
    ];

    /// <summary>Every core table added after Python 0.32.0's schema 0.0.13, with the version that added it.</summary>
    /// <remarks>A parity bundle or catalog holds exactly the tables Python wrote, so a table added since is not even
    /// created empty there. Add a table's entry in the commit that adds the table.</remarks>
    public static readonly IReadOnlyList<(string Version, string Table)> AddedTables =
    [
        ("0.0.16", "assay_runs"),
        ("0.0.17", "peptidoform_ions"),
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
    /// (G83 item 16); from 0.0.15 it is NULL. assays.run_id: NULL from 0.0.16 for a pooled design sample (PXR-R23).
    /// assays.sample_id: NULL from 0.0.17 for a TMT channel the design marks `empty` (PXR-R54).</remarks>
    public static readonly IReadOnlyList<(string Version, string Table, string Column)> RelaxedColumns =
    [
        ("0.0.15", "provenance_records", "provenance_schema"),
        ("0.0.16", "assays", "run_id"),
        ("0.0.17", "assays", "sample_id"),
    ];

    /// <summary>Every core column removed since Python 0.32.0's schema 0.0.13: the version that removed it, the column it
    /// followed, and its storage type. A parity bundle still writes it, where Python wrote it (as NULL in every row).</summary>
    /// <remarks>psms.matched_ion_series and matched_ion_count (D56, schema 0.0.17): reserved for fragment ions and never
    /// filled by any ingest, Python's or ours; the ions are in <c>peptidoform_ions</c> from 0.0.17. MetaMorpheus does
    /// write its matched-ion columns: the empty cells were ours. Add a column's entry in the commit that removes it; the
    /// parity tests fail on any one left out.</remarks>
    public static readonly IReadOnlyList<(string Version, string Table, string After, ColumnSpec Column)> RemovedColumns =
    [
        ("0.0.17", "psms", "end_residue", new ColumnSpec("matched_ion_series", ColumnType.String, IsList: false, Nullable: true)),
        ("0.0.17", "psms", "matched_ion_series", new ColumnSpec("matched_ion_count", ColumnType.Int64, IsList: false, Nullable: true)),
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
        var stillThere = RemovedColumns.Where(r => r.Table == spec.Name && System.Version.Parse(r.Version) > at).ToList();
        if (later.Count == 0 && stillRequired.Count == 0 && stillThere.Count == 0) return spec.Columns;
        var columns = spec.Columns
            .Where(c => !later.Contains(c.Name))
            .Select(c => stillRequired.Contains(c.Name) ? c with { Nullable = false } : c)
            .ToList();
        // In the order removed, each after the column it followed, which may itself be one put back here.
        foreach (var (_, _, after, column) in stillThere)
        {
            var index = columns.FindIndex(c => c.Name == after);
            if (index < 0) throw new InvalidOperationException($"{spec.Name}.{column.Name} followed {after}, which schema {version} does not have");
            columns.Insert(index + 1, column);
        }
        return columns;
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
        ("aging", "0.7.0", "age_effect_method_terms"),
    ];

    /// <summary>Every study column added to a table that already existed, with the layer version that added it.</summary>
    /// <remarks>Layer 0.7.0 (DATAREPO-88 c, aging DEF-AGE-EFFECT-META v1.4) keys <c>age_effect_meta</c> by
    /// <c>method_scope</c> in place of <c>quant_method</c>, with two counts per method and the method terms used. A table
    /// at an earlier layer version has the columns it had then (<see cref="RemovedStudyColumns"/>).</remarks>
    public static readonly IReadOnlyList<(string Layer, string Version, string Table, string Column)> AddedStudyColumns =
    [
        ("aging", "0.7.0", "age_effect_meta", "method_scope"),
        ("aging", "0.7.0", "age_effect_meta", "n_datasets_label_free"),
        ("aging", "0.7.0", "age_effect_meta", "n_datasets_isobaric"),
        ("aging", "0.7.0", "age_effect_meta", "method_term_ids"),
    ];

    /// <summary>Every study column removed from a table, with the layer version that removed it, the column it followed and
    /// its storage type: a table at an earlier version still has it there.</summary>
    public static readonly IReadOnlyList<(string Layer, string Version, string Table, string After, ColumnSpec Column)> RemovedStudyColumns =
    [
        ("aging", "0.7.0", "age_effect_meta", "acquisition", new ColumnSpec("quant_method", ColumnType.String, IsList: false, Nullable: false)),
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
        return Tables.Study[layer].Where(t => !later.Contains(t.Name)).Select(t => StudyTableAt(layer, t, at)).ToList();
    }

    /// <summary>A study table's columns at layer version <paramref name="at"/>: those added since left out, those removed
    /// since put back after the column they followed.</summary>
    private static TableSpec StudyTableAt(string layer, TableSpec table, System.Version at)
    {
        var added = AddedStudyColumns.Where(a => a.Layer == layer && a.Table == table.Name && System.Version.Parse(a.Version) > at)
            .Select(a => a.Column).ToHashSet(StringComparer.Ordinal);
        var removed = RemovedStudyColumns.Where(r => r.Layer == layer && r.Table == table.Name && System.Version.Parse(r.Version) > at).ToList();
        if (added.Count == 0 && removed.Count == 0) return table;
        var columns = table.Columns.Where(c => !added.Contains(c.Name)).ToList();
        foreach (var (_, _, _, after, column) in removed)
        {
            var index = columns.FindIndex(c => c.Name == after);
            if (index < 0) throw new InvalidOperationException($"{table.Name}.{column.Name} followed {after}, which layer {layer} {at} does not have");
            columns.Insert(index + 1, column);
        }
        return table with { Columns = columns };
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
