using System.Collections;

namespace DataRepo.Bundle;

/// <summary>One group of rows identical in every column that became one row.</summary>
/// <param name="Table">The table.</param>
/// <param name="Key">The identifier columns.</param>
/// <param name="Identifier">The identifier's value, parts joined with <c>:</c>.</param>
/// <param name="Written">How many identical rows the producer wrote, including the one kept.</param>
public sealed record Collapse(string Table, IReadOnlyList<string> Key, string Identifier, int Written)
{
    public int Dropped => Written - 1;
}

/// <summary>Referential checks run before a bundle is written.</summary>
/// <remarks>
/// Parquet has no foreign keys, so nothing stops a bundle from shipping a quantity whose feature id
/// names a peptidoform that is not in the peptidoform table; an agent asking for it would get nothing
/// back and could not tell a missing measurement from a broken link. A dangling reference is an
/// ingester bug, not a property of the data, so it stops the write rather than becoming a Finding.
/// <para>One escape, and only one: rows identical in every column for one identifier are one thing
/// written twice (MetaMorpheus wrote one protein group three times in PXD027318, aging 015), so
/// <see cref="CollapseExactDuplicates"/> drops the copies and records them. Rows that share an
/// identifier and differ anywhere are still refused.</para>
/// <para>Ported from <c>integrity.py</c>; the tables of references and keys are the same.</para>
/// </remarks>
public static class Integrity
{
    /// <summary>(table, column, target table, target column). A multivalued column ends in <c>[]</c>.</summary>
    public static readonly IReadOnlyList<(string Table, string Column, string Target, string TargetColumn)> References =
    [
        ("samples", "dataset_id", "datasets", "dataset_id"),
        ("sample_characteristics", "sample_id", "samples", "sample_id"),
        ("runs", "dataset_id", "datasets", "dataset_id"),
        ("assays", "run_id", "runs", "run_id"),
        ("assays", "sample_id", "samples", "sample_id"),
        ("psms", "run_id", "runs", "run_id"),
        ("psms", "protein_accessions[]", "proteins", "protein_accession"),
        ("peptidoforms", "dataset_id", "datasets", "dataset_id"),
        ("peptidoforms", "protein_group_id", "protein_groups", "protein_group_id"),
        ("peptidoforms", "protein_accessions[]", "proteins", "protein_accession"),
        ("protein_groups", "dataset_id", "datasets", "dataset_id"),
        ("protein_groups", "protein_accessions[]", "proteins", "protein_accession"),
        ("ptm_sites", "dataset_id", "datasets", "dataset_id"),
        ("ptm_sites", "protein_accession", "proteins", "protein_accession"),
        ("ptm_stoichiometry", "ptm_site_id", "ptm_sites", "ptm_site_id"),
        ("ptm_stoichiometry", "assay_id", "assays", "assay_id"),
        ("ptm_stoichiometry", "protein_group_id", "protein_groups", "protein_group_id"),
        ("ptm_stoichiometry", "definition_id", "definitions", "definition_id"),
        ("quant_values", "assay_id", "assays", "assay_id"),
        ("quant_values", "definition_id", "definitions", "definition_id"),
        ("metrics", "definition_id", "definitions", "definition_id"),
        ("findings", "dataset_id", "datasets", "dataset_id"),
        ("findings", "run_id", "runs", "run_id"),
        ("provenance_records", "dataset_id", "datasets", "dataset_id"),
        ("search_modifications_declared", "dataset_id", "datasets", "dataset_id"),
    ];

    /// <summary><c>QuantValue.feature_id</c> points into whichever table <c>feature_type</c> names.</summary>
    public static readonly IReadOnlyDictionary<string, (string Table, string Column)> FeatureTables =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["peptidoform"] = ("peptidoforms", "peptidoform_id"),
            ["protein_group"] = ("protein_groups", "protein_group_id"),
            ["ptm_site"] = ("ptm_sites", "ptm_site_id"),
            ["glycopeptide"] = ("glycopeptides", "glycopeptide_id"),
            ["proteoform"] = ("proteoform_inferences", "proteoform_id"),
            ["protein"] = ("proteins", "protein_accession"),
        };

    /// <summary>Columns whose values must be unique within a bundle.</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> Identifiers =
    [
        ("datasets", "dataset_id"),
        ("samples", "sample_id"),
        ("runs", "run_id"),
        ("assays", "assay_id"),
        ("psms", "psm_id"),
        ("peptidoforms", "peptidoform_id"),
        ("protein_groups", "protein_group_id"),
        ("proteins", "protein_accession"),
        ("ptm_sites", "ptm_site_id"),
        ("definitions", "definition_id"),
        ("findings", "finding_id"),
    ];

    /// <summary>Tables with no single identifier whose rows must still be unique on a natural key.
    /// <c>quant_values</c> matters most: with no id, a duplicated source row would write one
    /// measurement twice and a caller summing intensities would double-count it. <c>metrics</c> is
    /// absent on purpose: one metric legitimately arrives from two sources.</summary>
    public static readonly IReadOnlyList<(string Table, string[] Key)> CompositeIdentifiers =
    [
        ("quant_values", ["assay_id", "feature_type", "feature_id", "definition_id"]),
        ("ptm_stoichiometry", ["ptm_site_id", "assay_id"]),
        ("organelle_term_categories", ["compartment", "category_map_name", "organelle_map_version", "go_release", "organelle_category", "organelle_subcategory"]),
        ("trait_effects", ["feature_type", "feature_key", "trait_id", "response", "scope", "definition_id"]),
        ("ptm_pairs", ["result_type", "scope", "feature_type", "feature_key_a", "feature_key_b", "trait_id", "stratum", "statistic", "definition_id"]),
    ];

    /// <summary>A study layer's natural keys, by layer and table (<c>integrity.STUDY_COMPOSITE_IDENTIFIERS</c>).</summary>
    /// <remarks>
    /// Enforced twice: <c>study</c> refuses a delivery whose rows repeat a key, and <c>build</c> re-checks it on
    /// what the catalog actually loaded. They were declared before anything could write these tables, and that
    /// is why the check existed the moment <c>study</c> did: <c>quant_values</c> shipped with no key at all and
    /// a duplicated source row wrote one measurement three times. <c>aging:DEF-AGE-EFFECT v1</c> section 2 gives
    /// <c>age_effects</c> its key outright, and every component of it is forced by a benchmark question --
    /// <c>estimator</c> because count- and intensity-based occupancy differ about threefold, <c>quant_basis</c>
    /// because H8+ asks whether MBR changes the answer, <c>stratum</c> because D13 asks whether a decline is
    /// seen in both sexes. <c>age_effect_refusals</c> has the same key as the fit it refuses; its
    /// <c>feature_id</c> is nullable and part of the key anyway, because a dataset-level refusal such as
    /// <c>no_age_metadata</c> is one row with no feature. <c>age_effect_meta</c> starts with <c>organism</c>
    /// (aging DEF-AGE-EFFECT-META v1.2 section 6.4, G40): never pool across it.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> StudyCompositeIdentifiers =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
        {
            ["aging"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["sample_ages"] = ["sample_id"],
                // One curated value per characteristic per sample (aging 078, REQ-DATAREPO-6).
                ["curated_sample_characteristics"] = ["sample_id", "name"],
                ["age_effects"] = ["dataset_id", "feature_id", "response", "estimator", "quant_basis", "model_form", "stratum"],
                ["age_effect_refusals"] = ["dataset_id", "feature_id", "response", "estimator", "quant_basis", "model_form", "stratum"],
                ["age_effect_meta"] = ["organism", "feature_id", "response", "estimator", "quant_basis", "stratum", "tissue", "acquisition", "quant_method"],
                ["organelle_age_summaries"] = ["compartment", "organism", "organism_part", "response"],
                ["clock_features"] = ["clock_id", "feature_type", "feature_id"],
                ["age_mappings"] = ["organism", "age_from", "age_to"],
            },
        };

    /// <summary>A study layer's references into the core, checked when a catalog loads a study bundle
    /// (<c>integrity.STUDY_REFERENCES</c>): (table, column, core table, core column).</summary>
    /// <remarks>
    /// These are the joins that make a study layer a layer rather than a second repository: an age effect that
    /// names a dataset the catalog does not hold cannot be traced to the evidence behind it. <c>build</c> refuses
    /// rather than dropping the rows. Two columns are deliberately absent, and their absence is the point:
    /// <c>feature_id</c> (DATAREPO-20(c) asks what a feature's cross-dataset identity even is, and a foreign key
    /// written now would freeze a guess with the authority of a constraint) and <c>definition_id</c> (whether a
    /// study layer's own definitions land in a search bundle is unsettled).
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Table, string Column, string Target, string TargetColumn)>> StudyReferences =
        new Dictionary<string, IReadOnlyList<(string Table, string Column, string Target, string TargetColumn)>>(StringComparer.Ordinal)
        {
            ["aging"] =
            [
                ("sample_ages", "sample_id", "samples", "sample_id"),
                ("curated_sample_characteristics", "sample_id", "samples", "sample_id"),
                ("age_effects", "dataset_id", "datasets", "dataset_id"),
                ("age_effect_refusals", "dataset_id", "datasets", "dataset_id"),
                ("clock_features", "clock_id", "clock_models", "clock_id"),
            ],
        };

    /// <summary>Tables where a row is a thing, so identical rows are one thing written twice.
    /// <c>psms</c> and <c>findings</c> are absent: a row there is an event, and the row count is itself
    /// a reported number.</summary>
    public static readonly IReadOnlySet<string> Collapsible = new HashSet<string>(StringComparer.Ordinal)
    {
        "datasets", "samples", "runs", "assays", "peptidoforms", "protein_groups", "proteins",
        "ptm_sites", "definitions", "quant_values",
    };

    private static SortedDictionary<string, string[]> Keys()
    {
        var keys = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (table, column) in Identifiers) keys[table] = [column];
        foreach (var (table, key) in CompositeIdentifiers) keys[table] = key;
        return keys;
    }

    /// <summary>A comparable form of a cell: lists compare by value, and 1 equals 1.0 as in Python.</summary>
    private static object? Normal(object? value) => value switch
    {
        null => null,
        string s => s,
        double d when d == Math.Floor(d) && Math.Abs(d) < 9.007199254740992E15 => (long)d,
        int or long or short or byte => Convert.ToInt64(value),
        IEnumerable e => e.Cast<object?>().Select(Normal).ToList(),
        _ => value,
    };

    private static string Fingerprint(IReadOnlyDictionary<string, object?> row, IEnumerable<string> columns) =>
        PyFormat.Json(columns.Select(c => Normal(row.TryGetValue(c, out var v) ? v : null)).ToList());

    private static string Display(IReadOnlyDictionary<string, object?> row, IEnumerable<string> columns) =>
        string.Join(":", columns.Select(c => row.TryGetValue(c, out var v) && v is not null ? PyFormat.Str(v) : "None"));

    /// <summary>Drops rows that repeat an earlier row of the same table in every column.</summary>
    /// <param name="tables">Table name to rows; edited in place, keeping the first of each identical set
    /// and the original order. Tables not in <see cref="Collapsible"/> are ignored.</param>
    /// <returns>One <see cref="Collapse"/> per identifier written more than once, ordered by table then
    /// identifier. Non-empty belongs in the bundle's findings: it is true about the producer's output.</returns>
    public static List<Collapse> CollapseExactDuplicates(IDictionary<string, List<IReadOnlyDictionary<string, object?>>> tables)
    {
        var collapses = new List<Collapse>();
        foreach (var (table, key) in Keys())
        {
            if (!Collapsible.Contains(table) || !tables.TryGetValue(table, out var rows) || rows.Count == 0)
                continue;
            var columns = rows.SelectMany(r => r.Keys).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            var groups = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var row in rows)
            {
                var fp = Fingerprint(row, key);
                if (!groups.TryGetValue(fp, out var group))
                {
                    groups[fp] = group = [];
                    order.Add(fp);
                }
                group.Add(row);
            }
            var dropped = new HashSet<IReadOnlyDictionary<string, object?>>(ReferenceEqualityComparer.Instance);
            foreach (var fp in order)
            {
                var group = groups[fp];
                if (group.Count == 1) continue;
                if (group.Select(r => Fingerprint(r, columns)).Distinct().Count() != 1) continue;  // check() refuses
                foreach (var r in group.Skip(1)) dropped.Add(r);
                collapses.Add(new Collapse(table, key, Display(group[0], key), group.Count));
            }
            if (dropped.Count > 0)
                rows.RemoveAll(r => dropped.Contains(r));
        }
        return collapses
            .OrderBy(c => c.Table, StringComparer.Ordinal)
            .ThenBy(c => c.Identifier, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> Values(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string column)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (column.EndsWith("[]", StringComparison.Ordinal))
        {
            var name = column[..^2];
            foreach (var row in rows)
                if (row.TryGetValue(name, out var v) && v is IEnumerable e and not string)
                    foreach (var item in e)
                        if (item is not null) set.Add(PyFormat.Str(item));
            return set;
        }
        foreach (var row in rows)
            if (row.TryGetValue(column, out var v) && v is not null)
                set.Add(PyFormat.Str(v));
        return set;
    }

    private static string Sample(IEnumerable<string> values) =>
        string.Join(", ", values.OrderBy(v => v, StringComparer.Ordinal).Take(3));

    /// <summary>Every broken reference and duplicate identifier in a set of table rows.</summary>
    /// <param name="tables">Table name to rows for the tables the bundle will hold. An absent table is
    /// empty, which is normal: a bundle holds only the tables its producer filled.</param>
    /// <returns>Human-readable problems; empty when the bundle is sound.</returns>
    public static List<string> Check(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> tables)
    {
        var problems = new List<string>();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> RowsOf(string t) =>
            tables.TryGetValue(t, out var r) ? r : [];

        foreach (var (table, key) in Keys())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in RowsOf(table))
            {
                if (key.Any(c => !row.TryGetValue(c, out var v) || v is null)) continue;
                var fp = Fingerprint(row, key);
                if (!seen.Add(fp)) duplicates[Display(row, key)] = fp;
            }
            if (duplicates.Count > 0)
            {
                var names = key.Length == 1 ? key[0] : "(" + string.Join(", ", key) + ")";
                problems.Add(
                    $"{table}.{names}: {duplicates.Count} duplicate identifier(s), e.g. {Sample(duplicates.Keys)}. "
                    + "Rows sharing an identifier and identical in every column are collapsed before "
                    + "this check, so these differ somewhere and the producer has to say which is right.");
            }
        }

        foreach (var (table, column, target, targetColumn) in References)
        {
            var rows = RowsOf(table);
            if (rows.Count == 0) continue;
            var known = Values(RowsOf(target), targetColumn);
            var dangling = Values(rows, column);
            dangling.ExceptWith(known);
            if (dangling.Count > 0)
                problems.Add($"{table}.{column} -> {target}.{targetColumn}: {dangling.Count} value(s) with no matching row, e.g. {Sample(dangling)}");
        }

        var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in RowsOf("quant_values"))
        {
            var type = row.TryGetValue("feature_type", out var t) && t is not null ? PyFormat.Str(t) : "None";
            if (!byType.TryGetValue(type, out var ids)) byType[type] = ids = new HashSet<string>(StringComparer.Ordinal);
            if (row.TryGetValue("feature_id", out var id) && id is not null) ids.Add(PyFormat.Str(id));
        }
        foreach (var (featureType, ids) in byType)
        {
            if (!FeatureTables.TryGetValue(featureType, out var target))
            {
                problems.Add($"quant_values.feature_type: '{featureType}' names no table");
                continue;
            }
            var dangling = new HashSet<string>(ids, StringComparer.Ordinal);
            dangling.ExceptWith(Values(RowsOf(target.Table), target.Column));
            if (dangling.Count > 0)
                problems.Add($"quant_values.feature_id ({featureType}) -> {target.Table}.{target.Column}: {dangling.Count} value(s) with no matching row, e.g. {Sample(dangling)}");
        }
        return problems;
    }
}
