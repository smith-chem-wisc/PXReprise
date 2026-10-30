using DataRepo.Bundle;
using DataRepo.Catalog;
using DuckDB.NET.Data;
using Quantification;
using Quantification.PtmQtl;

namespace DataRepo.Runner;

/// <summary>One bundle's tables as the ptmQtl engines' inputs (<see cref="PtmQtlDataset"/>).</summary>
/// <remarks>
/// <para>Read with DuckDB over the bundle's own Parquet files, so a large PSM table is filtered rather than loaded.
/// Each rule below is ptmQtl's (S1-S5), or what ptmQtl's own export (`tools/export_observations.py`, which built bundle
/// v3) did where the definitions do not say. Those places are marked, and asked of ptmQtl (our ptmQtl 032).</para>
/// <list type="bullet">
/// <item><b>Observations (S7):</b> target PSMs that pass the shared quantified-PSM rule (ptmQtl D36; mzLib
/// <see cref="QuantifiedPsmRule"/>, #1425) and are at ambiguity level 1, on one accession with one start, per (run,
/// peptidoform, protein). The rule's tier is chosen once per search (<see cref="Tiers"/>). The MetaMorpheus name is the
/// peptidoform's `engine_full_sequences`. A peptidoform written under two names is left out and counted, since its
/// category would be ambiguous. The intensity is `QuantProject:DEF-PEP-INT`, MS/MS, value &gt; 0; without one, the
/// observation is NaN (identified, not quantified).</item>
/// <item><b>Occupancy (S1):</b> `ptm_stoichiometry` per run (label-free assays, `denominator_grouping = run`) at target
/// sites of ambiguity level 1. `unmodified_quantified` is `intensity_is_ceiling = false`.</item>
/// <item><b>Samples (S4):</b> each run's label-free assay's `sample_id`. Runs sharing a sample are combined only when
/// each carries a distinct `fraction`; a sample whose runs cannot be told apart that way is refused. Repeat injections
/// are never combined. On aging's corpus (2026-10-06) one sample in 1,869 holds several runs.</item>
/// <item><b>Pooled samples (DATAREPO-P32; schema 0.0.16):</b> MetaMorpheus's own value for a design sample made of
/// fractions (a `design_sample` assay, `denominator_grouping = sample_group`) is that sample's S4 occupancy, so it is
/// read as one cell of the sample. One that pools technical replicates is left out and counted (ptmQtl 046 (b), 049).
/// Its runs must be the runs S4 combines into that sample, and none may hold a per-run cell of its own.</item>
/// <item><b>Sequence windows (S5, D35):</b> `ptm_sites.sequence_window` per (accession, position), for pooling.</item>
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
    /// <remarks>A sample's runs are grouped by technical replicate. Within one replicate, runs are fractions of one
    /// injection and are combined, so they must carry distinct fractions. Repeat injections (distinct replicates) are
    /// never combined (S4): an injection of one file stays its own run, and one of several fractions is keyed
    /// <c>&lt;sample&gt;:tr&lt;n&gt;</c>. A sample whose runs the records cannot tell apart is refused.</remarks>
    /// <exception cref="RunnerException">A sample holds several runs that are neither distinct fractions nor distinct
    /// injections, or records a replicate for some runs and not others.</exception>
    public static Dictionary<string, string> RunToSample(IEnumerable<(string Run, string Sample, long? Fraction, long? TechnicalReplicate)> runs, string where)
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
            string Refuse(string why) => throw new RunnerException(
                $"{where}: sample {group.Key} holds {members.Count} runs ({string.Join(", ", members.Select(m => m.Run).Order(StringComparer.Ordinal).Take(4))}{(members.Count > 4 ? ", ..." : "")}) "
                + $"{why}, so whether to combine them (fractions) or keep them apart (injections) cannot be told (ptmQtl S4). "
                + "The bundle's run and sample records decide it; nothing is guessed.");
            if (members.Any(m => m.TechnicalReplicate is null) && members.Any(m => m.TechnicalReplicate is not null))
                Refuse("that record a technical replicate for some runs and not others");
            var injections = members.GroupBy(m => m.TechnicalReplicate).ToList();
            foreach (var injection in injections)
            {
                var files = injection.ToList();
                if (files.Count == 1 && injections.Count > 1)
                {
                    map[files[0].Run] = files[0].Run;
                    continue;
                }
                if (files.Any(m => m.Fraction is null) || files.Select(m => m.Fraction).Distinct().Count() != files.Count)
                    Refuse(injections.Count == 1 ? "that are not distinct fractions" : $"whose injection {injection.Key} holds files that are not distinct fractions");
                var key = injections.Count == 1 ? group.Key : $"{group.Key}:tr{injection.Key}";
                foreach (var m in files) map[m.Run] = key;
            }
        }
        return map;
    }

    /// <summary>S7: each search's quantified-PSM tier (ptmQtl D36), as mzLib's <see cref="QuantifiedPsmRule.ChooseTier"/>
    /// decides it from the search's PSMs.</summary>
    /// <remarks>From the search's target PSMs only, PEPs included, as S7 says ("from the search's target PSMs") and
    /// ptmQtl's export does (<c>export_observations.py</c>, <c>search_tier</c>). mzLib's own FlashLFQ caller passes every
    /// match's PEP, decoys included; on a search file holding every PSM the two differ only if all its targets share one
    /// PEP. The values are reduced in SQL to what the rule reads, so a search of millions of PSMs is not streamed: whether
    /// any PEP q-value or notch q-value is a probability, and the finite PEPs' smallest and largest value.</remarks>
    public static Dictionary<string, QuantifiedPsmTier> Tiers(DuckDBConnection con, string psms)
    {
        var tiers = new Dictionary<string, QuantifiedPsmTier>(StringComparer.Ordinal);
        foreach (var r in Query(con, $"""
            SELECT search,
                   bool_or(pep_q_value BETWEEN 0 AND 1),
                   bool_or(q_value_notch BETWEEN 0 AND 1),
                   min(pep) FILTER (WHERE isfinite(pep)), max(pep) FILTER (WHERE isfinite(pep))
            FROM {psms} WHERE target_decoy = 'target' GROUP BY search
            """))
        {
            if (r[0] is not string search) throw new RunnerException("a PSM names no search, so its quantified-PSM tier cannot be chosen (ptmQtl S7)");
            double[] Probability(object? any) => any is true ? [0.0] : [];
            var peps = r[3] is null ? Array.Empty<double>()
                : Convert.ToDouble(r[3]) == Convert.ToDouble(r[4]) ? [Convert.ToDouble(r[3])] : [Convert.ToDouble(r[3]), Convert.ToDouble(r[4])];
            tiers[search] = QuantifiedPsmRule.ChooseTier(Probability(r[1]), Probability(r[2]), peps);
        }
        return tiers;
    }

    /// <summary>S7's filter as SQL over a <c>psms</c> row: the value each search's tier names is strictly below
    /// <see cref="QuantifiedPsmRule.DefaultThreshold"/>, as <see cref="QuantifiedPsmRule.PassesConfidence"/> has it. A
    /// PSM with no value for its tier fails.</summary>
    public static string PassesSql(IReadOnlyDictionary<string, QuantifiedPsmTier> tiers)
    {
        if (tiers.Count == 0) return "false";
        var cases = tiers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"WHEN {Q(kv.Key)} THEN " + kv.Value switch
        {
            QuantifiedPsmTier.PepQValue => "pep_q_value",
            QuantifiedPsmTier.QValueNotch => "q_value_notch",
            _ => "q_value",
        });
        return $"coalesce((CASE search {string.Join(" ", cases)} END) < {QuantifiedPsmRule.DefaultThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}, false)";
    }

    /// <summary>The bundle as one dataset's engine inputs, with the deposit a PTM capture (S3) or not.</summary>
    public static (PtmQtlDataset Dataset, Dictionary<string, long> Counts) Read(BundleRef bundle, bool enriched)
    {
        var (dataset, counts, _) = Read(bundle, new PtmQtlDeposit(enriched ? "ptm_capture" : null));
        return (dataset, counts);
    }

    /// <summary>The modifications a MetaMorpheus install knew, by its folder, loaded once per process (S2d (b)'s masses).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DataRepo.Ingest.ModRegistry> Registries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>mzLib's monoisotopic mass of a <c>Category:IdWithMotif</c> name, from the MetaMorpheus install the
    /// bundle's search read (<c>bundle.json</c> <c>modification_registry.source</c>), loaded as the ingest loads it (G26).</summary>
    /// <exception cref="RunnerException">The install is not on this machine, or mzLib gives no mass.</exception>
    private static double MassOf(BundleRef bundle, string name, string where)
    {
        var source = (bundle.Manifest.GetValueOrDefault("modification_registry") as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("source") as string;
        if (source is null || !Directory.Exists(source))
            throw new RunnerException($"{where}: S2d (b) needs the mass of '{name}' from the MetaMorpheus install the search read ({source ?? "not recorded"}), "
                + "which is not on this machine.");
        var registry = Registries.GetOrAdd(source, DataRepo.Ingest.ModRegistry.FromMzLib);
        var colon = name.IndexOf(':');
        return registry.Lookup(name[(colon + 1)..], category: name[..colon])?.MonoisotopicMass
            ?? throw new RunnerException($"{where}: mzLib gives '{name}' no mass in {source}, so S2d (b) cannot match it to a cysteine label.");
    }

    /// <summary>The bundle as one dataset's engine inputs.</summary>
    /// <param name="deposit">What the operator's lists say of the deposit (S2d, S3, S3b).</param>
    /// <returns>The inputs; counts of what was read and left out; and the run record's account of the rules: S2d (b)'s
    /// screen and every site S2c and S2d left out.</returns>
    public static (PtmQtlDataset Dataset, Dictionary<string, long> Counts, Dictionary<string, object?> Rules) Read(BundleRef bundle, PtmQtlDeposit deposit)
    {
        var where = $"{bundle.DatasetId} bundle {bundle.BundleId}";
        if (bundle.SchemaVersion != SchemaContract.Version)
            throw new RunnerException($"{where} was written against schema {bundle.SchemaVersion}; the ptmQtl engines read schema "
                + $"{SchemaContract.Version} bundles (sequence windows, pooled samples). Re-ingest it first.");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        string psms = Table(bundle, "psms"), peptidoforms = Table(bundle, "peptidoforms"), quant = Table(bundle, "quant_values"),
            assays = Table(bundle, "assays"), runs = Table(bundle, "runs"), sites = Table(bundle, "ptm_sites"), stoich = Table(bundle, "ptm_stoichiometry");
        var tiers = Tiers(con, psms);
        var passes = PassesSql(tiers);

        var species = Query(con, $"SELECT DISTINCT unnest(organisms) FROM {Table(bundle, "datasets")}").Select(r => (string)r[0]!).ToList();
        if (species.Count != 1)
            throw new RunnerException($"{where} records {species.Count} organisms; pooling is within one species (ptmQtl S5).");

        // A run's fraction and technical replicate are the search's own design's where the run is one file of a design
        // sample (assay_runs, schema 0.0.16), since that is how MetaMorpheus grouped it, else the run's.
        string Design(string column) => bundle.TablePath("assay_runs") is null ? "NULL"
            : $"(SELECT min(ar.{column}) FROM {Table(bundle, "assay_runs")} ar WHERE ar.run_id = a.run_id)";
        long? Long(object? v) => v is null ? null : Convert.ToInt64(v);
        var runToSample = RunToSample(Query(con,
                $"SELECT a.run_id, a.sample_id, coalesce({Design("fraction")}, r.fraction), coalesce({Design("technical_replicate")}, r.technical_replicate) "
                + $"FROM {assays} a JOIN {runs} r USING (run_id) WHERE a.assay_id LIKE '%:label_free'")
            .Select(r => ((string)r[0]!, (string)r[1]!, Long(r[2]), Long(r[3]))), where);
        counts["samples combined from fractions"] = runToSample.GroupBy(kv => kv.Value).Count(g => g.Count() > 1);

        var nterms = Query(con, $"SELECT DISTINCT protein_accession, position FROM {sites} WHERE site_type = 'protein_n_term'")
            .Select(r => ((string)r[0]!, Convert.ToInt32(r[1]))).ToHashSet();
        var unimod = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Query(con, $"SELECT DISTINCT modification_name, modification FROM {sites} WHERE modification IS NOT NULL"))
            if (!unimod.TryAdd((string)r[0]!, (string)r[1]!) && unimod[(string)r[0]!] != (string)r[1]!)
                throw new RunnerException($"{where}: '{r[0]}' carries two UNIMOD accessions in ptm_sites");

        var observations = new List<PeptidoformObservation>();
        var verdicts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var prefix = bundle.DatasetId + ":";
        foreach (var r in Query(con, $"""
            WITH ident AS (
              SELECT run_id, peptidoform, protein_accessions[1] AS protein, min(start_residue) AS st, max(end_residue) AS en
              FROM {psms}
              WHERE target_decoy = 'target' AND {passes} AND ambiguity_level = '1'
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
            if (names.Count == 0)
            {
                counts["observations left out: no MetaMorpheus name"] = counts.GetValueOrDefault("observations left out: no MetaMorpheus name") + 1;
                continue;
            }
            // S7 (D57): several names are kept under the first when they are one set of modifications.
            if (names.Count > 1)
            {
                var combination = string.Join("|", names);
                if (!verdicts.TryGetValue(combination, out var verdict)) verdicts[combination] = verdict = PtmQtlRules.SameModifications(names);
                if (verdict is null)
                {
                    counts["observations left out: several names, other modifications"] = counts.GetValueOrDefault("observations left out: several names, other modifications") + 1;
                    continue;
                }
            }
            var (protein, start) = ((string)r[2]!, Convert.ToInt32(r[3]));
            char? previous = start == 2 ? (nterms.Contains((protein, 2)) ? 'M' : 'X') : null;
            observations.Add(new PeptidoformObservation((string)r[0]!, names[0], protein, start, Convert.ToInt32(r[4]),
                r[5] is null ? double.NaN : Convert.ToDouble(r[5]), previous));
        }
        counts["observations"] = observations.Count;
        // ptmQtl's three counters (their summary.tsv): distinct name combinations, not rows.
        counts["several_names_kept_unimod"] = verdicts.Count(v => v.Value == PtmQtlRules.SameByUnimod);
        counts["several_names_kept_formula"] = verdicts.Count(v => v.Value == PtmQtlRules.SameByFormula);
        counts["several_names_left_out"] = verdicts.Count(v => v.Value is null);

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

        // DATAREPO-P32: a design sample made of fractions of one injection is one S4 sample, and MetaMorpheus's value
        // for it is already S4's combination (ptmQtl 046). Its cells carry the sample's key as their run.
        var pooledMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (bundle.TablePath("assay_runs") is not null)
        {
            var assayRuns = Table(bundle, "assay_runs");
            var withRunCells = occupancy.Select(c => c.Run).ToHashSet(StringComparer.Ordinal);
            var pooledAssays = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in Query(con, $"""
                SELECT a.assay_id, a.sample_id, list(ar.run_id ORDER BY ar.run_id),
                       count(DISTINCT coalesce(ar.technical_replicate, 1)) = 1,
                       count(ar.fraction) = count(*) AND count(DISTINCT ar.fraction) = count(*)
                FROM {assays} a JOIN {assayRuns} ar USING (assay_id)
                WHERE a.assay_kind = 'design_sample'
                GROUP BY ALL ORDER BY 1
                """))
            {
                var (assay, sample) = ((string)r[0]!, (string)r[1]!);
                var members = ((System.Collections.IEnumerable)r[2]!).Cast<object?>().Select(m => (string)m!).ToList();
                if (r[3] is not true)
                {
                    counts["P32 pooled samples left out: technical replicates"] = counts.GetValueOrDefault("P32 pooled samples left out: technical replicates") + 1;
                    continue;
                }
                if (r[4] is not true)
                    throw new RunnerException($"{where}: pooled sample {sample} ({assay}) holds {members.Count} files of one injection that are not distinct fractions (ptmQtl S4).");
                var astray = members.Where(m => runToSample.GetValueOrDefault(m) != sample).ToList();
                if (astray.Count > 0)
                    throw new RunnerException($"{where}: pooled sample {sample} ({assay}) holds run(s) {string.Join(", ", astray.Take(3))} that S4 does not combine into it.");
                var doubled = members.Where(withRunCells.Contains).ToList();
                if (doubled.Count > 0)
                    throw new RunnerException($"{where}: run(s) {string.Join(", ", doubled.Take(3))} hold occupancy of their own and in pooled sample {sample}, which would count them twice.");
                pooledAssays[assay] = sample;
                pooledMembers[sample] = members;
                runToSample[sample] = sample;
            }
            if (pooledAssays.Count > 0)
            {
                var pooledCells = Query(con, $"""
                    SELECT o.assay_id, s.protein_accession, s.position, s.residue, s.site_type, s.modification_name, o.occupancy_state,
                           o.modified_fraction_intensity, o.intensity_modified, o.intensity_total, o.intensity_is_ceiling
                    FROM {stoich} o JOIN {sites} s USING (ptm_site_id) JOIN {assays} a USING (assay_id)
                    WHERE o.denominator_grouping = 'sample_group' AND a.assay_kind = 'design_sample'
                      AND s.target_decoy = 'target' AND s.best_ambiguity_level = '1'
                    ORDER BY 1, 2, 3, 6
                    """).Where(r => pooledAssays.ContainsKey((string)r[0]!))
                    .Select(r => new StoredOccupancyCell(pooledAssays[(string)r[0]!], (string)r[1]!, Convert.ToInt32(r[2]), ((string)r[3]!)[0], (string)r[4]!,
                        (string)r[5]!, (string)r[6]!, r[7] is null ? double.NaN : Convert.ToDouble(r[7]),
                        r[8] is null ? double.NaN : Convert.ToDouble(r[8]), r[9] is null ? double.NaN : Convert.ToDouble(r[9]),
                        r[10] is false)).ToList();
                counts["P32 pooled samples read"] = pooledAssays.Count;
                counts["P32 pooled occupancy cells"] = pooledCells.Count;
                occupancy.AddRange(pooledCells);
            }
        }

        // S5 (D35): each site's sequence window, for the cross-dataset join. A position holding two windows has none.
        var windows = new Dictionary<(string, int), string?>();
        foreach (var r in Query(con, $"SELECT DISTINCT protein_accession, position, sequence_window FROM {sites} WHERE sequence_window IS NOT NULL"))
        {
            var key = ((string)r[0]!, Convert.ToInt32(r[1]));
            windows[key] = windows.TryGetValue(key, out var known) && known != (string)r[2]! ? null : (string)r[2]!;
        }

        var unknownRuns = observations.Select(o => o.Run).Concat(occupancy.Select(c => c.Run)).Where(run => !runToSample.ContainsKey(run))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        if (unknownRuns.Count > 0)
            throw new RunnerException($"{where}: run(s) {string.Join(", ", unknownRuns.Take(3))} have no label-free assay, so no sample (ptmQtl S4).");

        // S2: a site's category comes from the MetaMorpheus names of the peptidoforms covering it -- all of the bundle's
        // target peptidoforms, not only those selected as observations.
        var categoryNames = Query(con, $"SELECT DISTINCT unnest(engine_full_sequences) FROM {peptidoforms} WHERE target_decoy = 'target' AND base_sequence <> peptidoform")
            .Select(r => (string)r[0]!).ToList();

        // S3, S3b: each run's sample type; occupancy-based results read the whole-proteome runs, and a pooled sample
        // only when all its files are.
        var runTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Query(con, $"SELECT run_id, enrichment FROM {runs} ORDER BY 1"))
            runTypes[(string)r[0]!] = PtmQtlRules.RunType((string)r[0]!,
                r[1] is System.Collections.IEnumerable labels ? labels.Cast<object?>().Select(l => (string)l!).ToList() : null, deposit);
        var occupancyRuns = runTypes.Where(kv => kv.Value == PtmQtlRules.WholeProteome).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (sample, members) in pooledMembers)
            if (members.All(occupancyRuns.Contains)) occupancyRuns.Add(sample);
        foreach (var type in runTypes.Values.Distinct()) counts[$"S3b runs: {type}"] = runTypes.Values.Count(v => v == type);

        // S2c and S2d: sites left out of every occupancy-based result.
        // D60: S2c is defined only for a K/R-cutting protease in a fully specific search, read from the bundle's own copy
        // of every task file the search ran (in task order); elsewhere the deposit is refused, never guessed.
        // MetaMorpheus numbers the tasks it ran (`1_CalibrationTask.toml`, ..., `3_SearchTask.toml`); an unnumbered task
        // file is not one of this run's, so it never stands in for the search task.
        var taskTomls = Directory.Exists(Path.Combine(bundle.Path, "sources"))
            ? Directory.EnumerateFiles(Path.Combine(bundle.Path, "sources"), "*Task.toml")
                .Select(f => (File: f, Order: int.TryParse(Path.GetFileName(f).Split('_')[0], out var n) ? n : (int?)null))
                .Where(t => t.Order is not null).OrderBy(t => t.Order)
                .Select(t => File.ReadAllText(t.File)).ToList()
            : [];
        var engineVersion = Query(con, $"SELECT search_engine_version FROM {Table(bundle, "datasets")}").Select(r => r[0] as string).FirstOrDefault();
        var s2c = PtmQtlRules.Gate(taskTomls, engineVersion);
        if (s2c.Refusal is not null)
            throw new RunnerException($"{where} is refused by ptmQtl S2c (D60): {s2c.Refusal}. Nothing is written for it.");
        var excluded = new List<ExcludedSite>(PtmQtlRules.BlockedSites(observations, s2c));
        var screened = new List<object?>();
        List<PeptidoformObservation>? physical = null;
        var targetSites = Query(con, $"SELECT DISTINCT protein_accession, position, residue, site_type FROM {sites} WHERE target_decoy = 'target'")
            .Select(r => ((string)r[0]!, Convert.ToInt32(r[1]), ((string)r[2]!)[0], (string)r[3]!)).ToList();
        if (deposit.CysteineClass == "nem")
        {
            var spans = Query(con, $"""
                SELECT DISTINCT unnest(protein_accessions), start_residue, coalesce(end_residue, start_residue + length(base_sequence) - 1)
                FROM {psms}
                WHERE target_decoy = 'target' AND {passes} AND base_sequence LIKE '%C%' AND start_residue IS NOT NULL
                """).Select(r => ((string)r[0]!, Convert.ToInt32(r[1]), Convert.ToInt32(r[2])));
            var covered = PtmQtlRules.CoveredSites(spans, targetSites.Select(s => (s.Item1, s.Item2, s.Item3)));
            var places = covered.Select(e => (e.Protein, e.Position)).ToHashSet();
            excluded.AddRange(covered);
            excluded.AddRange(PtmQtlRules.CysteineSites(targetSites, PtmQtlRules.NemReason).Where(e => !places.Contains((e.Protein, e.Position))));
            physical = observations.Where(o => !PtmQtlRules.Parse(o.FullSequence).Base.Contains('C')).ToList();
        }
        else
        {
            if (deposit.CysteineClass == "cysteine_poor")
                excluded.AddRange(PtmQtlRules.CysteineSites(targetSites, PtmQtlRules.CysteinePoorReason(deposit.CysteineReason)));
            // (b): the first name of each confident target peptidoform, as ptmQtl's screen reads them.
            var flagged = new List<(string Name, string Label)>();
            foreach (var s in PtmQtlRules.Screen(Query(con, $"""
                         SELECT base_sequence, engine_full_sequences[1] FROM {peptidoforms}
                         WHERE target_decoy = 'target' AND best_q_value < 0.01 AND len(engine_full_sequences) > 0
                         """).Select(r => ((string)r[0]!, (string)r[1]!))))
            {
                if (!s.Flag) continue;
                var mass = MassOf(bundle, s.Name, where);
                var label = PtmQtlRules.LabelOf(mass);
                screened.Add(new Dictionary<string, object?>
                {
                    ["modification"] = s.Name, ["peptidoforms"] = s.Peptidoforms, ["with_c"] = s.WithC, ["sequences_with_c"] = s.SequencesWithC,
                    ["share_with_c"] = s.Share, ["baseline_share_with_c"] = s.BaselineShare, ["mass"] = mass, ["label"] = label,
                });
                if (label is not null) flagged.Add((s.Name, label));
            }
            if (flagged.Count > 0)
            {
                var carriers = Query(con, $"""
                    SELECT DISTINCT unnest(s.protein_accessions), s.start_residue, p.engine_full_sequences[1]
                    FROM (SELECT * FROM {psms} WHERE target_decoy = 'target' AND {passes} AND ambiguity_level = '1'
                            AND base_sequence LIKE '%C%' AND start_residue IS NOT NULL) s
                    JOIN {peptidoforms} p ON p.peptidoform = s.peptidoform
                    WHERE len(p.engine_full_sequences) > 0
                    """).Select(r => ((string)r[0]!, Convert.ToInt32(r[1]), (string)r[2]!)).ToList();
                var stored = Query(con, $"SELECT DISTINCT protein_accession, position, modification_name FROM {sites} WHERE target_decoy = 'target'")
                    .Select(r => ((string)r[0]!, Convert.ToInt32(r[1]), (string)r[2]!)).ToHashSet();
                foreach (var (name, label) in flagged) excluded.AddRange(PtmQtlRules.CarriedSites(carriers, name, label, stored));
            }
        }

        // S2e (ptmQtl D58): a methyl-K/R site whose PSMs mostly also carry a carbamyl, unless the search offered
        // carbamidomethyl on Met, Lys and the N-terminus. It is a share, so it counts the PSMs of the runs whose
        // occupancy it protects, the whole-proteome runs, as ptmQtl's list does: another run's PSMs could move a site
        // either way. S2c's "any" reads every run, since more evidence only leaves more out (the safer error).
        var declared = bundle.TablePath("search_modifications_declared") is null ? []
            : Query(con, $"SELECT modification, residues FROM {Table(bundle, "search_modifications_declared")}")
                .Select(r => ((string?)r[0], (string)r[1]!)).ToList();
        var s2eApplies = !PtmQtlRules.CarbamidomethylOffered(declared);
        if (s2eApplies && occupancyRuns.Count > 0)
        {
            var methylSites = PtmQtlRules.MethylSites(Query(con, $"""
                SELECT peptidoform, protein_accessions[1], start_residue, count(*)
                FROM {psms}
                WHERE target_decoy = 'target' AND {passes} AND ambiguity_level = '1'
                  AND len(protein_accessions) = 1 AND start_residue IS NOT NULL AND {PtmQtlRules.MethylPsmSql}
                  AND run_id IN ({string.Join(", ", occupancyRuns.Order(StringComparer.Ordinal).Select(Q))})
                GROUP BY ALL
                """).Select(r => ((string)r[0]!, (string)r[1]!, Convert.ToInt32(r[2]), Convert.ToInt64(r[3]))));
            var methylStored = Query(con, $"""
                SELECT DISTINCT protein_accession, position, residue, modification_name, modification FROM {sites}
                WHERE target_decoy = 'target' AND modification IN ('UNIMOD:34', 'UNIMOD:36', 'UNIMOD:37')
                """).Select(r => ((string)r[0]!, Convert.ToInt32(r[1]), ((string)r[2]!)[0], (string)r[3]!, (string)r[4]!));
            counts["S2e methyl-K/R sites"] = methylSites.Count;
            counts["S2e methyl-K/R sites carbamyl-carried"] = methylSites.Count(s => s.Share >= PtmQtlRules.S2eShare);
            excluded.AddRange(PtmQtlRules.CarbamylSites(methylSites, methylStored));
        }
        foreach (var rule in excluded.Select(e => e.Rule).Distinct()) counts[$"{rule} sites"] = excluded.Count(e => e.Rule == rule);

        var rules = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capture_class"] = deposit.CaptureClass,
            ["cysteine_class"] = deposit.CysteineClass,
            ["s2c_applies"] = !s2c.Lifted,
            ["s2c_gate"] = new Dictionary<string, object?>
            {
                ["protease"] = s2c.Protease, ["search_mode"] = s2c.SearchMode, ["cut_residues"] = s2c.CutResidues,
                ["lifted"] = s2c.Lifted, ["task_files"] = (long)taskTomls.Count,
            },
            ["s2d_b_flagged"] = screened,
            ["s2e_applies"] = s2eApplies,
        };
        return (new PtmQtlDataset(bundle.DatasetId, species[0], observations, occupancy, runToSample, unimod, deposit.Enriched, categoryNames)
        {
            Windows = windows,
            Tiers = tiers.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
            RunTypes = runTypes,
            OccupancyRuns = occupancyRuns,
            Excluded = excluded,
            PhysicalObservations = physical,
            Poolable = !deposit.Enriched && runTypes.Values.All(t => t == PtmQtlRules.WholeProteome),
        }, counts, rules);
    }
}
