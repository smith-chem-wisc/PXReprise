using DataRepo.Bundle;
using DataRepo.Catalog;
using DuckDB.NET.Data;
using Quantification.PtmQtl;

namespace DataRepo.Runner;

/// <summary>One bundle's tables as the ptmQtl engines' inputs (<see cref="PtmQtlDataset"/>).</summary>
/// <remarks>
/// <para>Read with DuckDB over the bundle's own Parquet files, so a large PSM table is filtered rather than loaded.
/// Each rule below is ptmQtl's (S1-S5), or what ptmQtl's own export (`tools/export_observations.py`, which built bundle
/// v3) did where the definitions do not say. Those places are marked, and asked of ptmQtl (our ptmQtl 032).</para>
/// <list type="bullet">
/// <item><b>Observations</b> (export rule, not yet in the definitions): target PSMs at q &lt;= 0.01 and ambiguity level
/// 1, on one accession with one start, per (run, peptidoform, protein). The MetaMorpheus name is the peptidoform's
/// `engine_full_sequences`. A peptidoform written under two names is left out and counted, since its category would
/// be ambiguous. The intensity is `QuantProject:DEF-PEP-INT`, MS/MS, value &gt; 0; without one, the observation is
/// NaN (identified, not quantified).</item>
/// <item><b>Occupancy (S1):</b> `ptm_stoichiometry` per run (label-free assays, `denominator_grouping = run`) at target
/// sites of ambiguity level 1. `unmodified_quantified` is `intensity_is_ceiling = false`.</item>
/// <item><b>Samples (S4):</b> each run's label-free assay's `sample_id`. Runs sharing a sample are combined only when
/// each carries a distinct `fraction`; a sample whose runs cannot be told apart that way is refused. Repeat injections
/// are never combined. On aging's corpus (2026-10-06) one sample in 1,869 holds several runs.</item>
/// <item><b>Protein N-termini (S5):</b> `ptm_sites.site_type = protein_n_term`. A peptide starting at residue 2 is
/// given previous residue `M` when MetaMorpheus called a protein N-terminal site there, and `X` otherwise. PSMs record
/// no previous residue, and mzLib would otherwise assume Met.</item>
/// <item><b>UNIMOD (S5):</b> `ptm_sites.modification` by `modification_name`.</item>
/// </list>
/// </remarks>
public static class PtmQtlBundle
{
    public const string PeptideIntensity = "QuantProject:DEF-PEP-INT";

    private static string Q(string path) => "'" + path.Replace('\\', '/').Replace("'", "''") + "'";

    private static string Table(BundleRef bundle, string table) =>
        bundle.TablePath(table) is { } path ? $"read_parquet({Q(path)})"
            : throw new RunnerException($"{bundle.DatasetId} bundle {bundle.BundleId} has no `{table}` table, which the ptmQtl engines read.");

    private static List<object?[]> Query(DuckDBConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Runs -> samples (S4), from the bundle's assays and runs.</summary>
    /// <exception cref="RunnerException">A sample holds several runs that are not distinct fractions.</exception>
    public static Dictionary<string, string> RunToSample(IEnumerable<(string Run, string Sample, long? Fraction)> runs, string where)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in runs.GroupBy(r => r.Sample, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                map[members[0].Run] = members[0].Run;
                continue;
            }
            if (members.Any(m => m.Fraction is null) || members.Select(m => m.Fraction).Distinct().Count() != members.Count)
                throw new RunnerException(
                    $"{where}: sample {group.Key} holds {members.Count} runs ({string.Join(", ", members.Select(m => m.Run).Order(StringComparer.Ordinal))}) "
                    + "that are not distinct fractions, so whether to combine them (fractions) or keep them apart (injections) "
                    + "cannot be told (ptmQtl S4). The bundle's run and sample records decide it; nothing is guessed.");
            foreach (var m in members) map[m.Run] = $"{group.Key}";
        }
        return map;
    }

    /// <summary>The bundle as one dataset's engine inputs.</summary>
    /// <param name="enriched">From the operator's enrichment list (S3).</param>
    /// <returns>The inputs, and counts of what was read and left out, for the run record.</returns>
    public static (PtmQtlDataset Dataset, Dictionary<string, long> Counts) Read(BundleRef bundle, bool enriched)
    {
        var where = $"{bundle.DatasetId} bundle {bundle.BundleId}";
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        string psms = Table(bundle, "psms"), peptidoforms = Table(bundle, "peptidoforms"), quant = Table(bundle, "quant_values"),
            assays = Table(bundle, "assays"), runs = Table(bundle, "runs"), sites = Table(bundle, "ptm_sites"), stoich = Table(bundle, "ptm_stoichiometry");

        var species = Query(con, $"SELECT DISTINCT unnest(organisms) FROM {Table(bundle, "datasets")}").Select(r => (string)r[0]!).ToList();
        if (species.Count != 1)
            throw new RunnerException($"{where} records {species.Count} organisms; pooling is within one species (ptmQtl S5).");

        var runToSample = RunToSample(Query(con,
                $"SELECT a.run_id, a.sample_id, r.fraction FROM {assays} a JOIN {runs} r USING (run_id) WHERE a.assay_id LIKE '%:label_free'")
            .Select(r => ((string)r[0]!, (string)r[1]!, r[2] is null ? (long?)null : Convert.ToInt64(r[2]))), where);
        counts["samples combined from fractions"] = runToSample.GroupBy(kv => kv.Value).Count(g => g.Count() > 1);

        var nterms = Query(con, $"SELECT DISTINCT protein_accession, position FROM {sites} WHERE site_type = 'protein_n_term'")
            .Select(r => ((string)r[0]!, Convert.ToInt32(r[1]))).ToHashSet();
        var unimod = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Query(con, $"SELECT DISTINCT modification_name, modification FROM {sites} WHERE modification IS NOT NULL"))
            if (!unimod.TryAdd((string)r[0]!, (string)r[1]!) && unimod[(string)r[0]!] != (string)r[1]!)
                throw new RunnerException($"{where}: '{r[0]}' carries two UNIMOD accessions in ptm_sites");

        var observations = new List<PeptidoformObservation>();
        var prefix = bundle.DatasetId + ":";
        foreach (var r in Query(con, $"""
            WITH ident AS (
              SELECT run_id, peptidoform, protein_accessions[1] AS protein, min(start_residue) AS st, max(end_residue) AS en
              FROM {psms}
              WHERE target_decoy = 'target' AND q_value <= 0.01 AND ambiguity_level = '1'
                AND len(protein_accessions) = 1 AND start_residue IS NOT NULL
              GROUP BY ALL HAVING min(start_residue) = max(start_residue)),
            q AS (
              SELECT a.run_id, qv.feature_id, max(qv.value) AS intensity
              FROM {quant} qv JOIN {assays} a USING (assay_id)
              WHERE qv.feature_type = 'peptidoform' AND qv.definition_id = '{PeptideIntensity}'
                AND qv.detection_type = 'MSMS' AND qv.value > 0
              GROUP BY ALL)
            SELECT i.run_id, p.engine_full_sequences, i.protein, i.st, i.en, q.intensity
            FROM ident i
            JOIN {peptidoforms} p ON p.peptidoform = i.peptidoform
            LEFT JOIN q ON q.run_id = i.run_id AND q.feature_id = {Q(prefix)} || i.peptidoform
            ORDER BY 1, 3, 4, 2
            """))
        {
            var names = ((System.Collections.IEnumerable?)r[1] ?? Array.Empty<object>()).Cast<object?>().Select(n => (string)n!).ToList();
            if (names.Count != 1)
            {
                counts[$"observations left out: {names.Count} MetaMorpheus names"] = counts.GetValueOrDefault($"observations left out: {names.Count} MetaMorpheus names") + 1;
                continue;
            }
            var (protein, start) = ((string)r[2]!, Convert.ToInt32(r[3]));
            char? previous = start == 2 ? (nterms.Contains((protein, 2)) ? 'M' : 'X') : null;
            observations.Add(new PeptidoformObservation((string)r[0]!, names[0], protein, start, Convert.ToInt32(r[4]),
                r[5] is null ? double.NaN : Convert.ToDouble(r[5]), previous));
        }
        counts["observations"] = observations.Count;

        var occupancy = Query(con, $"""
            SELECT a.run_id, s.protein_accession, s.position, s.residue, s.site_type, s.modification_name, o.occupancy_state,
                   o.modified_fraction_intensity, o.intensity_modified, o.intensity_total, o.intensity_is_ceiling
            FROM {stoich} o JOIN {sites} s USING (ptm_site_id) JOIN {assays} a USING (assay_id)
            WHERE o.denominator_grouping = 'run' AND a.assay_id LIKE '%:label_free'
              AND s.target_decoy = 'target' AND s.best_ambiguity_level = '1'
            ORDER BY 1, 2, 3, 6
            """).Select(r => new StoredOccupancyCell((string)r[0]!, (string)r[1]!, Convert.ToInt32(r[2]), ((string)r[3]!)[0], (string)r[4]!,
                (string)r[5]!, (string)r[6]!, r[7] is null ? double.NaN : Convert.ToDouble(r[7]),
                r[8] is null ? double.NaN : Convert.ToDouble(r[8]), r[9] is null ? double.NaN : Convert.ToDouble(r[9]),
                r[10] is false)).ToList();
        counts["occupancy cells"] = occupancy.Count;

        var unknownRuns = observations.Select(o => o.Run).Concat(occupancy.Select(c => c.Run)).Where(run => !runToSample.ContainsKey(run))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        if (unknownRuns.Count > 0)
            throw new RunnerException($"{where}: run(s) {string.Join(", ", unknownRuns.Take(3))} have no label-free assay, so no sample (ptmQtl S4).");

        // S2: a site's category comes from the MetaMorpheus names of the peptidoforms covering it -- all of the bundle's
        // target peptidoforms, not only those selected as observations.
        var categoryNames = Query(con, $"SELECT DISTINCT unnest(engine_full_sequences) FROM {peptidoforms} WHERE target_decoy = 'target' AND base_sequence <> peptidoform")
            .Select(r => (string)r[0]!).ToList();

        return (new PtmQtlDataset(bundle.DatasetId, species[0], observations, occupancy, runToSample, unimod, enriched, categoryNames), counts);
    }
}
