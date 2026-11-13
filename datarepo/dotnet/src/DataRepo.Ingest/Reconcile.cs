using DataRepo.Bundle;

namespace DataRepo.Ingest;

/// <summary>One comparison between a number the bundle holds and a number the producer reported.</summary>
public sealed record Check(string Name, double? Observed, double? Expected, string Source, string? Note = null)
{
    /// <summary>True when there is nothing to compare, or the two agree within <see cref="Reconcile.Tolerance"/>.
    /// An absence is reported, not judged.</summary>
    public bool Ok
    {
        get
        {
            if (Observed is null || Expected is null) return true;
            if (Expected == 0) return Observed == 0;
            return Math.Abs(Observed.Value - Expected.Value) / Math.Abs(Expected.Value) <= Reconcile.Tolerance;
        }
    }

    public bool Comparable => Observed is not null && Expected is not null;

    /// <summary>The <c>bundle.json</c> <c>reconciliation</c> entry.</summary>
    public OrderedDictionary<string, object?> AsDict()
    {
        var d = new OrderedDictionary<string, object?>
        {
            ["name"] = Name,
            ["observed"] = Observed,
            ["expected"] = Expected,
            ["source"] = Source,
            ["ok"] = Ok,
        };
        if (!string.IsNullOrEmpty(Note)) d["note"] = Note;
        return d;
    }
}

/// <summary>Checks the bundle's own tables against the producer's summary numbers.</summary>
/// <remarks>
/// An ingester that silently drops a tenth of the PSMs still writes a plausible-looking bundle, so every
/// bundle recounts its own tables and compares them with what MetaMorpheus's <c>results.txt</c> and the
/// pipeline's provenance reported. A mismatch is never fatal and never hidden: it goes into
/// <c>bundle.json</c> and becomes a Finding, because some mismatches are real and already known (aging's
/// S20 and S21). Ported from <c>reconcile.py</c>.
/// </remarks>
public static class Reconcile
{
    /// <summary>Counts within this relative distance of each other are treated as agreeing.</summary>
    public const double Tolerance = 0.0;

    /// <summary>Compares the bundle's own counts with the producer's reported totals.</summary>
    /// <param name="psmDefinition">The PSM count's definition, cited in the check's source; the namespace
    /// its record declares decides which twin (D37). Defaults to aging's.</param>
    public static List<Check> Build(
        long psmCount1pct,
        long peptidoformCount1pct,
        long? proteinGroupCount1pct,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> runs,
        IReadOnlyDictionary<string, Dictionary<string, long>> results,
        long? expectedFiles,
        long? provenanceMs2,
        Def? psmDefinition = null)
    {
        var psm = psmDefinition ?? Definitions.Psm1pct;
        var totals = results.TryGetValue("", out var t) ? t : new Dictionary<string, long>();
        double? Total(string name) => totals.TryGetValue(name, out var v) ? v : null;
        var ms2Observed = runs.Sum(r => r.TryGetValue("ms2_spectra", out var v) && v is not null ? Convert.ToDouble(v) : 0);
        return
        [
            new Check("psms_target_1pct", psmCount1pct, Total("psms"),
                $"results.txt: All target PSMs with q-value <= 0.01 ({psm.DefinitionId} {psm.Version})"),
            new Check("peptidoforms_target_1pct", peptidoformCount1pct, Total("peptides"),
                "results.txt: All target peptides with q-value <= 0.01"),
            new Check("protein_groups_1pct", proteinGroupCount1pct, Total("protein_groups"),
                "results.txt: All target protein groups with q-value <= 0.01"),
            new Check("runs", runs.Count, expectedFiles, "ingest manifest: files"),
            new Check("ms2_spectra", ms2Observed == 0 ? null : ms2Observed,
                provenanceMs2 is not null ? provenanceMs2 : Total("ms2_scans"),
                "provenance.json id_rate.ms2 / results.txt All MS2 Scans"),
        ];
    }

    /// <summary>A Finding wherever one metric arrives from two sources and they disagree.</summary>
    /// <remarks><c>psms_1pct</c> legitimately reaches a bundle twice, from the provenance and from
    /// <c>results.txt</c>, under one definition. That is corroboration only while they agree, so a
    /// disagreement is raised; every value is kept.</remarks>
    public static List<Row> MetricConflicts(IReadOnlyList<IReadOnlyDictionary<string, object?>> metrics, string datasetId)
    {
        var order = new List<string>();
        var seen = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var row in metrics)
        {
            var key = PyFormat.Json(new List<object?> { Get(row, "scope"), Get(row, "scope_id"), Get(row, "name"), Get(row, "definition_id") });
            if (!seen.TryGetValue(key, out var group))
            {
                seen[key] = group = [];
                order.Add(key);
            }
            group.Add(row);
        }

        var rows = new List<Row>();
        foreach (var key in order)
        {
            var group = seen[key];
            var values = group.Select(r => Get(r, "value")).Select(v => v is null ? "None" : PyFormat.Json(Normal(v))).Distinct().Count();
            if (group.Count < 2 || values < 2) continue;
            var first = group[0];
            var scope = Get(first, "scope");
            var scopeId = Get(first, "scope_id");
            var name = Get(first, "name");
            var definition = Get(first, "definition_id");
            var reported = string.Join(", ", group
                .OrderBy(r => PyFormat.Str(Get(r, "source") ?? "None"), StringComparer.Ordinal)
                .Select(r => $"{PyFormat.FormatG(Get(r, "value")!)} ({PyFormat.Str(Get(r, "source") ?? "None")})"));
            rows.Add(new Row
            {
                ["finding_id"] = $"{datasetId}:metric_conflict:{S(scope)}:{S(scopeId)}:{S(name)}",
                ["dataset_id"] = datasetId,
                ["run_id"] = Equals(scope, "run") ? scopeId : null,
                ["code"] = "metric_conflict",
                ["severity"] = "warning",
                ["status"] = "open",
                ["message"] =
                    $"'{S(name)}' at {S(scope)} scope reaches this bundle from more than one source under "
                    + $"{S(definition)}, and the sources disagree: {reported}. Every value is kept; the "
                    + "definition's owner has to say which source is canonical.",
                ["source"] = "datarepo ingest reconciliation",
            });
        }
        return rows;
    }

    /// <summary>A Finding for each comparison that did not agree.</summary>
    public static List<Row> FindingRows(IEnumerable<Check> checks, string datasetId)
    {
        var rows = new List<Row>();
        foreach (var check in checks)
        {
            if (check.Ok || !check.Comparable) continue;
            var message =
                $"The bundle holds {PyFormat.FormatG(check.Observed!.Value)} where the producer reported {PyFormat.FormatG(check.Expected!.Value)} "
                + $"({check.Source}). Both numbers are kept; treat the producer's as canonical until the "
                + "difference is explained.";
            if (!string.IsNullOrEmpty(check.Note)) message = $"{message} {check.Note}";
            rows.Add(new Row
            {
                ["finding_id"] = $"{datasetId}:count_mismatch:{check.Name}",
                ["dataset_id"] = datasetId,
                ["run_id"] = null,
                ["code"] = "count_mismatch",
                ["severity"] = "warning",
                ["status"] = "open",
                ["message"] = message,
                ["source"] = "datarepo ingest reconciliation",
            });
        }
        return rows;
    }

    private static object? Get(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    private static string S(object? v) => v is null ? "None" : PyFormat.Str(v);

    /// <summary>A value in Python's set-equality form: 1 and 1.0 are the same value.</summary>
    private static object? Normal(object? v) => v is double d && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : v;
}
