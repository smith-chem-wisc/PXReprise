using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;
using DuckDB.NET.Data;

namespace DataRepo.Catalog;

/// <summary>D48 step 2b: a catalog folder's per-dataset derived tables, computed once per deposit and kept beside the
/// catalogs (PXReprise's public-data contract, rule 5: a publish recomputes only what is corpus-wide).</summary>
/// <remarks>
/// <para>Each deposit's rows are the same SQL a one-file catalog runs over the whole corpus (<c>OverviewSelect</c>,
/// <c>ProteinDatasetsSelect</c>, <c>ModificationsPlacedSelect</c>), run over that deposit's own files in a connection of
/// its own. Every join and group in them is within a dataset, so the rows are the corpus build's. The two corpus-wide
/// indexes, <c>protein_index</c> and <c>peptide_index</c>, are aggregated at every publish from per-deposit partial rows
/// that carry exactly what their aggregates need.</para>
/// <para>The files live in <c>&lt;folder&gt;/derived/&lt;derivation id&gt;/&lt;dataset&gt;/&lt;bundle&gt;/</c>. A bundle
/// never changes, so a deposit's directory is reused for as long as the derivation id is the same, and the id hashes the
/// SQL, the catalog format, the schema and DuckDB's version: any change to what a row would hold names a new directory.
/// A directory is written beside its final name and moved into place whole, with its <c>derived.json</c> (each file's row
/// count) written last; one whose files do not hold the rows it records is derived again.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    /// <summary>The record written last in a deposit's derived directory.</summary>
    public const string DerivedRecord = "derived.json";

    /// <summary>The per-deposit files, in the order they are written.</summary>
    private static readonly string[] DepositFiles =
        ["dataset_overview", "protein_datasets", "protein_partials", "search_modifications_placed", "peptide_partials", "reconciliation_terms"];

    /// <summary>The bundle tables the per-deposit SQL reads.</summary>
    private static readonly string[] DepositInputs =
        ["datasets", "runs", "samples", "assays", "findings", "proteins", "protein_groups", "peptidoforms", "ptm_sites", "quant_values", "psms"];

    /// <summary>Per (dataset, accession), what <see cref="ProteinIndexSelect"/>'s aggregates need from one deposit: the same
    /// joined rows, reduced so that an aggregate over every deposit's rows gives the corpus build's index.</summary>
    private const string ProteinPartialSelect = @"
        SELECT
            p.dataset_id,
            p.protein_accession,
            max(p.gene)                                                       AS gene,
            max(p.organism)                                                   AS organism,
            bool_or(p.is_contaminant)                                         AS is_contaminant,
            bool_or(d.n_protein_groups > 0 OR d.n_peptidoforms > 0)           AS passes_1pct,
            min(d.best_q_value)                                               AS best_q_value
        FROM proteins p
        LEFT JOIN protein_datasets d
               ON d.dataset_id = p.dataset_id AND d.protein_accession = p.protein_accession
        GROUP BY p.dataset_id, p.protein_accession
";

    /// <summary><see cref="ProteinIndexSelect"/>'s columns, aggregated over the deposits' <see cref="ProteinPartialSelect"/>
    /// rows (<c>{files}</c>).</summary>
    private const string ProteinIndexFromPartials = @"
        SELECT
            protein_accession,
            max(gene)                                                         AS gene,
            max(organism)                                                     AS organism,
            count(DISTINCT dataset_id) FILTER (WHERE is_contaminant)          AS n_datasets_contaminant,
            count(DISTINCT dataset_id)                                        AS n_datasets,
            count(DISTINCT dataset_id) FILTER (WHERE passes_1pct)             AS n_datasets_1pct,
            list_sort(list(DISTINCT dataset_id))                              AS dataset_ids,
            list_sort(list(DISTINCT dataset_id) FILTER (WHERE passes_1pct))   AS dataset_ids_1pct,
            min(best_q_value)                                                 AS best_q_value
        FROM read_parquet({files}, union_by_name = true)
        GROUP BY protein_accession
";

    /// <summary>Per (dataset, base sequence), what <see cref="PeptideIndexSelect"/>'s aggregates need from one deposit.</summary>
    private const string PeptidePartialSelect = @"
        SELECT dataset_id, base_sequence, count(*) AS n_peptidoforms, min(best_q_value) AS best_q_value
        FROM peptidoforms_1pct
        GROUP BY dataset_id, base_sequence
";

    /// <summary><see cref="PeptideIndexSelect"/>'s columns, aggregated over the deposits' <see cref="PeptidePartialSelect"/>
    /// rows (<c>{files}</c>).</summary>
    private const string PeptideIndexFromPartials = @"
        SELECT
            base_sequence,
            count(DISTINCT dataset_id)                                        AS n_datasets,
            list_sort(list(DISTINCT dataset_id))                              AS dataset_ids,
            CAST(sum(n_peptidoforms) AS BIGINT)                               AS n_peptidoforms,
            min(best_q_value)                                                 AS best_q_value
        FROM read_parquet({files}, union_by_name = true)
        GROUP BY base_sequence
";

    private static string? _derivationId;

    /// <summary>What a deposit's derived rows depend on besides the bundle: the SQL that computes them, the acceptance
    /// threshold, the catalog format, the schema and the DuckDB that runs it. A change to any names a new directory.</summary>
    public static string DerivationId => _derivationId ??= ComputeDerivationId();

    private static string ComputeDerivationId()
    {
        using var con = new DuckDBConnection("Data Source=:memory:");
        con.Open();
        var duckdb = (string)Query(con, "SELECT version()")[0][0]!;
        var text = new StringBuilder($"catalog/{CatalogVersion}\nschema/{SchemaContract.Version}\nduckdb/{duckdb}\n"
            + $"threshold/{PyFormat.FloatRepr(Identifications.ProducerThreshold)}\n");
        foreach (var (name, body) in CatalogedAcceptedViews) text.Append($"view/{name}\n{body}\n");
        foreach (var sql in new[] { OverviewSelect(DepositPsmCounts), ProteinDatasetsSelect, ProteinPartialSelect, ModificationsPlacedSelect, PeptidePartialSelect, ReconciliationTermsSql })
            text.Append(sql).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>The name of the one-row PSM-count table a deposit's overview reads.</summary>
    private const string DepositPsmCounts = "bundle_psm_counts";

    /// <summary>Where a catalog folder keeps the derived rows of this derivation: posix, ending in "/".</summary>
    internal static string DerivedRootOf(string folder) =>
        AsPosix(System.IO.Path.Combine(folder, "derived", DerivationId)).TrimEnd('/') + "/";

    private static string DepositDir(string derivedRoot, BundleRef r) =>
        System.IO.Path.Combine(derivedRoot, r.DatasetId, r.BundleId);

    /// <summary>The per-dataset derived tables of a catalog folder, from each deposit's derived directory: the deposit's own
    /// rows as views (the overview copied, because <c>restrictions</c> is added to it), the two corpus indexes aggregated.</summary>
    /// <returns>One check per deposit: its derived files hold the rows their record says.</returns>
    private static List<CatalogCheck> BuildDerivedFromDeposits(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, string derivedRoot, Action<string> lap,
        out int computed, out int reused, out Dictionary<string, ReconciliationTerms> terms)
    {
        (computed, reused) = (0, 0);
        terms = new Dictionary<string, ReconciliationTerms>(StringComparer.Ordinal);
        var checks = new List<CatalogCheck>();
        foreach (var r in bundles)
        {
            var dir = DepositDir(derivedRoot, r);
            var counts = RecordedRows(con, dir, r);
            if (counts is null)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                DeriveDeposit(r, dir);
                counts = RecordedRows(con, dir, r);
                computed++;
            }
            else reused++;
            if (counts is not null)
                terms[r.BundleId] = ReadReconciliationTerms(con,
                    $"read_parquet({Quote(AsPosix(System.IO.Path.Combine(dir, "reconciliation_terms.parquet")))})");
            checks.Add(new CatalogCheck($"{r.DatasetId}/derived", "derived", counts is not null, null, null,
                counts is not null ? $"bundle {r.BundleId}, derivation {DerivationId}" : $"bundle {r.BundleId}: its derived files do not hold the rows {DerivedRecord} records"));
        }
        lap($"derived: deposits ({computed} computed, {reused} reused)");

        string Files(string table) =>
            "[" + string.Join(", ", bundles.Select(r => Quote(AsPosix(System.IO.Path.Combine(DepositDir(derivedRoot, r), $"{table}.parquet"))))) + "]";
        Exec(con, $"CREATE TABLE dataset_overview AS SELECT * FROM read_parquet({Files("dataset_overview")}, union_by_name = true)");
        Exec(con, $"CREATE VIEW protein_datasets AS SELECT * FROM read_parquet({Files("protein_datasets")}, union_by_name = true)");
        Exec(con, $"CREATE VIEW search_modifications_placed AS SELECT * FROM read_parquet({Files("search_modifications_placed")}, union_by_name = true)");
        lap("derived: per-deposit tables");
        Exec(con, $"CREATE TABLE protein_index AS {ProteinIndexFromPartials.Replace("{files}", Files("protein_partials"))}");
        lap("derived: protein_index");
        Exec(con, $"CREATE TABLE peptide_index AS {PeptideIndexFromPartials.Replace("{files}", Files("peptide_partials"))}");
        lap("derived: peptide_index");
        return checks;
    }

    /// <summary>The rows a deposit's derived directory records for each file, when its record names this bundle and this
    /// derivation and every file's footer holds what it records; otherwise null (derive it again).</summary>
    private static Dictionary<string, long>? RecordedRows(DuckDBConnection con, string dir, BundleRef r)
    {
        var recordPath = System.IO.Path.Combine(dir, DerivedRecord);
        if (!File.Exists(recordPath)) return null;
        IReadOnlyDictionary<string, object?> record;
        try
        {
            record = ReadJsonObject(recordPath);
        }
        catch (CatalogException)
        {
            return null;
        }
        if (SourcesPy.Str(SourcesPy.Get(record, "bundle_id", "")) != r.BundleId
            || SourcesPy.Str(SourcesPy.Get(record, "derivation_id", "")) != DerivationId)
            return null;
        var rows = StrMap(SourcesPy.Get(record, "tables"), PyInt);
        var paths = DepositFiles.Select(t => System.IO.Path.Combine(dir, $"{t}.parquet")).ToList();
        if (DepositFiles.Any(t => !rows.ContainsKey(t)) || paths.Any(p => !File.Exists(p))) return null;
        Dictionary<string, long?> footers;
        try
        {
            footers = FooterRows(con, paths);
        }
        catch (DuckDBException)
        {
            return null;  // a file that is not whole Parquet any more
        }
        foreach (var t in DepositFiles)
            if (footers.GetValueOrDefault(AsPosix(System.IO.Path.Combine(dir, $"{t}.parquet"))) != rows[t]) return null;
        return rows.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Writes one deposit's derived files: the corpus SQL over that deposit's own Parquet, in a connection of its
    /// own, into a directory beside <paramref name="dir"/> that is moved into place whole once its record is written.</summary>
    private static void DeriveDeposit(BundleRef r, string dir)
    {
        var parent = System.IO.Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(parent);
        // Short: DuckDB opens these files without Windows' long-path prefix, so a path past 260 characters is not found.
        var staging = System.IO.Path.Combine(parent, $".{r.BundleId}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            using (var con = new DuckDBConnection("Data Source=:memory:"))
            {
                con.Open();
                foreach (var table in DepositInputs)
                {
                    var path = r.TablePath(table);
                    if (path is not null) Exec(con, $"CREATE VIEW \"{table}\" AS {SelectFromParquet(r, table, path)}");
                    else CreateEmptyView(con, table, DatasetProvenance, SchemaContract.Columns(Tables.ByName[table]).Where(c => c.Name != "dataset_id"));
                }
                CreateAcceptedViews(con);
                Exec(con, $"CREATE TABLE {DepositPsmCounts} (dataset_id VARCHAR, n_psms_1pct BIGINT, n_psms_all BIGINT)");
                Exec(con, $"INSERT INTO {DepositPsmCounts} VALUES (?, ?, ?)", r.DatasetId, r.Psms1pct, r.RowCounts.GetValueOrDefault("psms"));
                Exec(con, $"CREATE TABLE protein_datasets AS {ProteinDatasetsSelect}");

                var rows = new Dictionary<string, object?>(StringComparer.Ordinal);
                void Write(string table, string select)
                {
                    var file = System.IO.Path.Combine(staging, $"{table}.parquet");
                    Exec(con, $"COPY ({select}) TO {Quote(AsPosix(file))} (FORMAT parquet)");
                    rows[table] = Count(con, $"SELECT count(*) FROM read_parquet({Quote(AsPosix(file))})");
                }
                Write("dataset_overview", OverviewSelect(DepositPsmCounts));
                Write("protein_datasets", "SELECT * FROM protein_datasets");
                Write("protein_partials", ProteinPartialSelect);
                Write("search_modifications_placed", ModificationsPlacedSelect);
                Write("peptide_partials", PeptidePartialSelect);
                Write("reconciliation_terms", ReconciliationTermsSql);

                var record = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["builder_version"] = PackageVersion,
                    ["bundle_id"] = r.BundleId,
                    ["dataset_id"] = r.DatasetId,
                    ["derivation_id"] = DerivationId,
                    ["tables"] = rows,
                    ["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", System.Globalization.CultureInfo.InvariantCulture),
                };
                File.WriteAllText(System.IO.Path.Combine(staging, DerivedRecord), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            }
            try
            {
                Directory.Move(staging, dir);
            }
            catch (IOException) when (File.Exists(System.IO.Path.Combine(dir, DerivedRecord)))
            {
                // Another build derived this deposit meanwhile: its directory is the same rows. Keep it.
                Directory.Delete(staging, recursive: true);
            }
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }
}
