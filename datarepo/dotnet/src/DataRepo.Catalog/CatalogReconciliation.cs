using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;
using DuckDB.NET.Data;

namespace DataRepo.Catalog;

/// <summary>What a bundle's stored rows say about its two producer-count checks (aging DATAREPO-86 and -87, 1.4.1).
/// Computed at build from the bundle's Parquet, so no bundle is ingested again (D32).</summary>
/// <param name="PsmsRecount">Target PSMs passing <c>Identifications.ProducerCounts</c>' rule, recounted from
/// <c>psms.parquet</c>. <c>target_decoy</c> already reads an entrapment target (<c>ET</c>, <c>T|ET</c>) as a target, so
/// this is the count DATAREPO-87 asks for, where the ingest's own tested the label for equality with <c>T</c>.</param>
/// <param name="PsmsAmbiguous">PSMs that pass both q-values but whose notch never resolved: the producer may count them.</param>
/// <param name="PsmsRounding">Counted PSMs whose q or notch q is exactly the threshold, i.e. printed <c>0.010000</c>: the
/// producer, holding the unrounded value, may not count them.</param>
/// <param name="PeptidoformsAmbiguous">An ESTIMATE of the peptide file's notch-ambiguous passing rows: passing target
/// peptidoforms whose best-scoring target PSM is notch-ambiguous. The bundle keeps no peptide file, so the exact term is
/// not stored; this one ran 5-70x the exact count on aging's store (results/reconciliation_bound_2026-10-30).</param>
/// <param name="PeptidoformsRounding">Passing target peptidoforms whose best q or best notch q is exactly the threshold.</param>
public sealed record ReconciliationTerms(
    long PsmsRecount, long PsmsAmbiguous, long PsmsRounding, long PeptidoformsAmbiguous, long PeptidoformsRounding);

/// <summary>One reconciliation check of one bundle as the catalog judges it: a row of <c>catalog_reconciliation</c>.</summary>
/// <param name="Status"><c>agrees</c>, <c>explained</c> (the producer's number lies inside the bound its own file format
/// leaves), <c>unexplained</c>, or <c>not_compared</c> (one side has no number).</param>
public sealed record ReconciliationRow(
    string DatasetId, string BundleId, string Check, double? Observed, double? RecordedObserved, double? Expected,
    bool RecordedOk, long? ExplainedLower, long? ExplainedUpper, string? Basis, string Status);

public static partial class CatalogBuilder
{
    /// <summary>The catalog table of every reconciliation check, judged at build (DATAREPO-86).</summary>
    public const string ReconciliationTable = "catalog_reconciliation";

    /// <summary>One row of <see cref="ReconciliationTerms"/> over a deposit's <c>psms</c> and <c>peptidoforms</c>, with
    /// the producer's threshold at <c>{t}</c>. A PSM's best is by <c>score</c>; a tie is ambiguous when any tied PSM is,
    /// so the estimate never depends on row order.</summary>
    private const string ReconciliationTermsSelect = @"
        WITH counted AS (
            SELECT coalesce(notch_ambiguous, false) AS ambiguous, q_value, q_value_notch
            FROM psms
            WHERE target_decoy = 'target' AND q_value <= {t} AND (q_value_notch IS NULL OR q_value_notch <= {t})
        ),
        ranked AS (
            SELECT peptidoform, coalesce(notch_ambiguous, false) AS ambiguous, score,
                   max(score) OVER (PARTITION BY peptidoform) AS top
            FROM psms
            WHERE target_decoy = 'target'
        ),
        best AS (
            SELECT peptidoform, bool_or(ambiguous) AS ambiguous
            FROM ranked
            WHERE score = top OR top IS NULL
            GROUP BY peptidoform
        ),
        passing AS (
            SELECT peptidoform, best_q_value, best_q_value_notch
            FROM peptidoforms
            WHERE target_decoy = 'target' AND best_q_value <= {t}
              AND (best_q_value_notch IS NULL OR best_q_value_notch <= {t})
        )
        SELECT
            (SELECT count(*) FROM counted WHERE NOT ambiguous)                                      AS psms_recount,
            (SELECT count(*) FROM counted WHERE ambiguous)                                          AS psms_ambiguous,
            (SELECT count(*) FROM counted WHERE NOT ambiguous AND (q_value = {t} OR q_value_notch = {t})) AS psms_rounding,
            (SELECT count(*) FROM passing JOIN best USING (peptidoform) WHERE best.ambiguous)       AS peptidoforms_ambiguous,
            (SELECT count(*) FROM passing WHERE best_q_value = {t} OR best_q_value_notch = {t})     AS peptidoforms_rounding
";

    private static string ReconciliationTermsSql =>
        ReconciliationTermsSelect.Replace("{t}", PyFormat.FloatRepr(Identifications.ProducerThreshold));

    /// <summary>The terms of one bundle, over its own Parquet in a connection of its own (a one-file build; a folder
    /// keeps them in each deposit's derived directory).</summary>
    internal static ReconciliationTerms ComputeReconciliationTerms(BundleRef r)
    {
        using var con = new DuckDBConnection("Data Source=:memory:");
        con.Open();
        foreach (var table in new[] { "psms", "peptidoforms" })
        {
            var path = r.TablePath(table);
            if (path is not null) Exec(con, $"CREATE VIEW \"{table}\" AS {SelectFromParquet(r, table, path)}");
            else CreateEmptyView(con, table, DatasetProvenance, SchemaContract.Columns(Tables.ByName[table]).Where(c => c.Name != "dataset_id"));
        }
        return ReadReconciliationTerms(con, $"({ReconciliationTermsSql})");
    }

    /// <summary>The single row of terms in <paramref name="source"/> (a subquery or a Parquet read).</summary>
    private static ReconciliationTerms ReadReconciliationTerms(DuckDBConnection con, string source)
    {
        var row = Query(con,
            $"SELECT psms_recount, psms_ambiguous, psms_rounding, peptidoforms_ambiguous, peptidoforms_rounding FROM {source}")[0];
        return new ReconciliationTerms(PyInt(row[0]!), PyInt(row[1]!), PyInt(row[2]!), PyInt(row[3]!), PyInt(row[4]!));
    }

    /// <summary>Each check a bundle recorded at ingest, judged against its explained bound.</summary>
    /// <remarks>
    /// <para><c>psms_target_1pct</c>: the observed count is the recount (<see cref="ReconciliationTerms.PsmsRecount"/>),
    /// which equalled the ingest's in all 179 bundles of aging's store, and the bound is [recount - rounding, recount +
    /// ambiguous], both terms exact.</para>
    /// <para><c>peptidoforms_target_1pct</c>: the observed count stays the ingest's (from the peptide file, which the
    /// bundle does not keep), and the bound is [count - ambiguous estimate - rounding, count]: an over-wide bound, said so
    /// in <c>basis</c>.</para>
    /// <para>Every other check has no bound: it agrees or it is unexplained, as its ingest recorded.</para>
    /// </remarks>
    internal static List<ReconciliationRow> JudgeReconciliation(BundleRef r, ReconciliationTerms? terms)
    {
        var rows = new List<ReconciliationRow>();
        if (SourcesPy.Get(r.Manifest, "reconciliation") is not List<object?> recorded) return rows;
        foreach (var c in recorded)
        {
            var check = c as IReadOnlyDictionary<string, object?> ?? SourcesPy.EmptyDict;
            var name = SourcesPy.Str(SourcesPy.Get(check, "name"));
            var recordedObserved = Number(SourcesPy.Get(check, "observed"));
            var expected = Number(SourcesPy.Get(check, "expected"));
            var recordedOk = SourcesPy.Truthy(SourcesPy.Get(check, "ok"));
            var observed = recordedObserved;
            long? lower = null, upper = null;
            string? basis = null;
            if (terms is not null && name == "psms_target_1pct")
            {
                observed = terms.PsmsRecount;
                (lower, upper) = (terms.PsmsRecount - terms.PsmsRounding, terms.PsmsRecount + terms.PsmsAmbiguous);
                basis = "stored PSMs: counted rows printed at the threshold, and passing rows whose notch did not resolve";
            }
            else if (terms is not null && name == "peptidoforms_target_1pct" && recordedObserved is not null)
            {
                var n = (long)recordedObserved.Value;
                (lower, upper) = (n - terms.PeptidoformsAmbiguous - terms.PeptidoformsRounding, n);
                basis = "estimate: the peptide file's notch is not stored, so each passing peptidoform whose best PSM's "
                    + "notch did not resolve is counted; wider than the exact bound";
            }
            else if (name == "protein_groups_1pct" && observed is not null)
            {
                // MetaMorpheus before 1.1.13 (#2923) could write one group twice, byte for byte, and count it twice
                // (PXD027971, aging 122 c). The ingest keeps one copy and records the other in `collapsed_duplicates`.
                var duplicated = DroppedDuplicates(r, "protein_groups");
                if (duplicated > 0)
                {
                    var n = (long)observed.Value;
                    (lower, upper) = (n, n + duplicated);
                    basis = "bundle.json collapsed_duplicates: protein-group rows the producer wrote twice, identically, "
                        + "and may have counted twice";
                }
            }
            string status;
            if (observed is null || expected is null) status = "not_compared";
            else if (lower is null) status = recordedOk ? "agrees" : "unexplained";
            else if (observed.Value == expected.Value) status = "agrees";
            else status = lower <= expected && expected <= upper ? "explained" : "unexplained";
            rows.Add(new ReconciliationRow(r.DatasetId, r.BundleId, name, observed, recordedObserved, expected, recordedOk,
                lower, upper, basis, status));
        }
        return rows;
    }

    /// <summary>Rows of <paramref name="table"/> the ingest dropped as exact duplicates of a row it kept.</summary>
    private static long DroppedDuplicates(BundleRef r, string table) =>
        SourcesPy.Get(r.Manifest, "collapsed_duplicates") is List<object?> entries
            ? entries.OfType<IReadOnlyDictionary<string, object?>>()
                .Where(e => SourcesPy.Str(SourcesPy.Get(e, "table", "")) == table)
                .Sum(e => SourcesPy.Get(e, "dropped") is { } d ? PyInt(d) : 0L)
            : 0;

    private static double? Number(object? v) => v is null ? null : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes <see cref="ReconciliationTable"/>.</summary>
    private static void WriteReconciliationTable(DuckDBConnection con, IEnumerable<ReconciliationRow> rows)
    {
        Exec(con, $@"
        CREATE TABLE {ReconciliationTable} (
            dataset_id VARCHAR, bundle_id VARCHAR, check_name VARCHAR, observed DOUBLE, recorded_observed DOUBLE,
            expected DOUBLE, recorded_ok BOOLEAN, explained_lower BIGINT, explained_upper BIGINT, basis VARCHAR,
            status VARCHAR
        )
        ");
        foreach (var x in rows)
            Exec(con, $"INSERT INTO {ReconciliationTable} VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                x.DatasetId, x.BundleId, x.Check, x.Observed, x.RecordedObserved, x.Expected, x.RecordedOk,
                x.ExplainedLower, x.ExplainedUpper, x.Basis, x.Status);
    }
}
