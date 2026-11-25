using System.Globalization;
using System.Text.Json.Nodes;
using PXReprise.Config;

namespace PXReprise.Batch;

/// <summary>One deposit's traits file for one trait, or why it has none.</summary>
/// <param name="Path">The file written, or null when the deposit is left out.</param>
/// <param name="Covariates">The covariate columns written, in order.</param>
public sealed record TraitsFile(string Dataset, string TraitId, string? Path, int Runs, IReadOnlyList<string> Covariates, IReadOnlyList<string> Notes);

/// <summary>
/// D96: the traits file ptmQtl's per-site trait fit reads (<c>--input traits</c>), one per deposit and trait: <c>dataset</c>,
/// <c>run</c>, <c>replicate</c> (the animal or donor), <c>trait_id</c>, <c>value</c>, then one column per categorical
/// covariate. Built from the latest catalog, which already joins each run to its sample (<c>assays</c>) and holds the
/// question's curated tables; the question names the sources (<see cref="TraitSource"/>), so no question's names are in
/// this code. The rules are aging's (their 341, DEF-AGE-EFFECT 3.3 and 5), written for any question.
/// </summary>
public static class TraitsWriter
{
    /// <summary>The catalog file a <c>[ptmqtl] catalog</c> names: a catalog folder's <c>latest.json</c>, or a .duckdb file.</summary>
    /// <exception cref="InvalidDataException">Neither exists, or the folder's latest.json names no catalog.</exception>
    public static string CatalogFile(string catalog)
    {
        if (File.Exists(catalog)) return catalog;
        string latest = Path.Combine(catalog, "latest.json");
        if (!File.Exists(latest)) throw new InvalidDataException($"{catalog}: no catalog file, and no latest.json in it");
        string? name = (JsonNode.Parse(File.ReadAllText(latest)) as JsonObject)?["catalog"]?.GetValue<string>();
        string file = name is null ? "" : Path.Combine(catalog, name);
        return name is not null && File.Exists(file) ? file : throw new InvalidDataException($"{latest} names no catalog file that exists");
    }

    /// <summary>A covariate's column name in the file: <c>characteristics[sex]</c> gives <c>sex</c>, a factor
    /// <c>fear conditioning</c> gives <c>fear_conditioning</c>.</summary>
    public static string ColumnName(string covariate) =>
        (covariate.StartsWith("characteristics[", StringComparison.Ordinal) ? covariate["characteristics[".Length..^1] : covariate).Replace(' ', '_');

    private static string Literal(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// The catalog query: every label-free run with its individual, the sample's value and the design factor's (kept
    /// apart, so a disagreement can be said), the factor's level label, the sample's exclusion flags and each named
    /// covariate. Names reach it only as validated identifiers (<see cref="QuestionLoader"/>) or quoted literals.
    /// </summary>
    public static string Sql(TraitSource t)
    {
        string? table = null, column = null;
        if (t.SampleValue is { } sv) { var parts = sv.Split('.'); (table, column) = (parts[0], parts[1]); }
        var select = new List<string> { "a.dataset_id", "a.run_id", "d.individual AS replicate",
            table is null ? "CAST(NULL AS DOUBLE) AS sample_value" : $"CAST(sv.\"{column}\" AS DOUBLE) AS sample_value",
            t.DesignFactor is null ? "CAST(NULL AS DOUBLE) AS factor_value" : "CAST(f.level_value AS DOUBLE) AS factor_value",
            t.DesignFactor is null ? "CAST(NULL AS VARCHAR) AS factor_level" : "CAST(f.level AS VARCHAR) AS factor_level" };
        var joins = new List<string> { "LEFT JOIN curated_run_design d ON d.run_id = a.run_id" };
        if (t.DesignFactor is { } factor) joins.Add($"LEFT JOIN curated_design_factors f ON f.run_id = a.run_id AND f.factor = {Literal(factor)}");
        if (table is not null) joins.Add($"LEFT JOIN \"{table}\" sv ON sv.sample_id = a.sample_id");
        for (int i = 0; i < t.SampleExclusions.Count; i++) select.Add($"CAST(sv.\"{t.SampleExclusions[i]}\" AS VARCHAR) AS excl_{i}");
        for (int i = 0; i < t.Covariates.Count; i++)
        {
            string c = t.Covariates[i];
            if (c.StartsWith("characteristics[", StringComparison.Ordinal))
            {
                joins.Add($"LEFT JOIN curated_sample_characteristics c{i} ON c{i}.sample_id = a.sample_id AND c{i}.name = {Literal(c)}");
                select.Add($"c{i}.value AS cov_{i}");
            }
            else select.Add($"CAST(d.\"{c}\" AS VARCHAR) AS cov_{i}");
        }
        // One line: it is passed as one argument, and a Windows command shell ends a command at a line break.
        return $"SELECT DISTINCT {string.Join(", ", select)} FROM assays a {string.Join(" ", joins)} "
            + "WHERE a.assay_id LIKE '%:label_free' ORDER BY a.dataset_id, a.run_id";
    }

    /// <summary>Whether the trait needs the second query (<see cref="RoleSql"/>).</summary>
    public static bool NeedsRoles(TraitSource t) => t.CovariateFactorRole is not null || t.StratumFactorRole is not null;

    /// <summary>The second query, with <c>covariate_factor_role</c> or <c>stratum_factor_role</c>: each run's level of
    /// every other design factor of those roles (its trait's own factor left out), one row per run and factor.</summary>
    public static string RoleSql(TraitSource t) =>
        "SELECT DISTINCT run_id, factor, role, level FROM curated_design_factors WHERE role IN ("
        + string.Join(", ", new[] { t.CovariateFactorRole, t.StratumFactorRole }.OfType<string>().Select(Literal)) + ")"
        + (t.DesignFactor is null ? "" : $" AND factor <> {Literal(t.DesignFactor)}") + " ORDER BY run_id, factor";

    private static List<string[]> Rows(string tsv, out string[] header)
    {
        var lines = tsv.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) throw new InvalidDataException("a traits query returned nothing, not even a header");
        header = lines[0].Split('\t');
        return lines.Skip(1).Select(l => l.Split('\t')).ToList();
    }

    private static bool Empty(string s) => s is "" or "NULL";

    /// <summary>
    /// The files, from the queries' TSV (<c>datarepo query --format tsv</c>), for each deposit in
    /// <paramref name="datasets"/> that holds the trait's sources at all:
    /// <list type="bullet">
    /// <item>the sample's value wins over the factor's; a disagreement is said, sample by sample;</item>
    /// <item>a sample an exclusion flag marks (a range, a lower bound) is left out, and counted;</item>
    /// <item>a run with no value is left out, and counted (a level such as "old" with no number is said as such);</item>
    /// <item>a deposit with any run lacking a curated individual is left out whole: an injection must never be counted
    /// as an animal (ptmQtl D88); so is one with no value left, a run with two different rows, or a stratum factor with
    /// two or more levels (a fit per level is not built);</item>
    /// <item>a covariate is written only where it is complete and has two or more levels, each held by two or more
    /// individuals; one that splits the samples exactly as an earlier one does is left out.</item>
    /// </list>
    /// Every deposit left out comes back with its reason.
    /// </summary>
    public static IReadOnlyList<TraitsFile> Write(TraitSource t, string tsv, string? roleTsv, string outDir, IReadOnlySet<string> datasets)
    {
        var rows = Rows(tsv, out var header);
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new InvalidDataException($"the traits query has no '{name}' column");
        int ds = Col("dataset_id"), run = Col("run_id"), rep = Col("replicate"), sval = Col("sample_value"), fval = Col("factor_value"), flev = Col("factor_level");
        var excl = Enumerable.Range(0, t.SampleExclusions.Count).Select(i => Col($"excl_{i}")).ToArray();
        var cov = Enumerable.Range(0, t.Covariates.Count).Select(i => Col($"cov_{i}")).ToArray();
        static string Cell(string[] r, int i) => i < r.Length ? r[i].Trim() : "";

        // Each run's level of every other design factor of the covariate role, by factor; and of the stratum role apart.
        var roles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var roleNames = new List<string>();
        var strata = new HashSet<string>(StringComparer.Ordinal);
        if (roleTsv is not null)
        {
            var rr = Rows(roleTsv, out var rh);
            int rrun = Array.IndexOf(rh, "run_id"), rfac = Array.IndexOf(rh, "factor"), rrole = Array.IndexOf(rh, "role"), rlev = Array.IndexOf(rh, "level");
            if (rrun < 0 || rfac < 0 || rrole < 0 || rlev < 0) throw new InvalidDataException("the role query has no run_id, factor, role or level column");
            foreach (var r in rr)
            {
                string f = Cell(r, rfac);
                if (Cell(r, rrole) == t.StratumFactorRole) strata.Add(f);
                else if (Cell(r, rrole) != t.CovariateFactorRole) continue;
                else if (!roleNames.Contains(f)) roleNames.Add(f);
                (roles.TryGetValue(Cell(r, rrun), out var m) ? m : roles[Cell(r, rrun)] = new(StringComparer.Ordinal))[f] = Cell(r, rlev);
            }
            roleNames.Sort(StringComparer.Ordinal);
        }
        string LevelOf(string[] r, string factor) => roles.TryGetValue(Cell(r, run), out var m) ? m.GetValueOrDefault(factor, "") : "";

        var written = new List<TraitsFile>();
        Directory.CreateDirectory(outDir);
        foreach (var group in rows.GroupBy(r => Cell(r, ds)).Where(g => datasets.Contains(g.Key)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = group.ToList();
            if (all.All(r => Empty(Cell(r, sval)) && Empty(Cell(r, fval)) && Empty(Cell(r, flev)))) continue;   // the trait is not curated here
            var notes = new List<string>();
            TraitsFile Out(string why) => new(group.Key, t.TraitId, null, 0, [], [.. notes, why]);
            if (all.GroupBy(r => Cell(r, run)).Any(g => g.Count() > 1)) { written.Add(Out("a run has two different rows in the catalog")); continue; }
            if (all.Any(r => Empty(Cell(r, rep)))) { written.Add(Out("a run has no curated individual, so its replicate is unknown")); continue; }

            var kept = new List<(string[] Row, double Value)>();
            int excluded = 0, levelOnly = 0, none = 0;
            foreach (var r in all)
            {
                if (excl.Any(i => Cell(r, i).Equals("true", StringComparison.OrdinalIgnoreCase))) { excluded++; continue; }
                bool hasSample = double.TryParse(Cell(r, sval), NumberStyles.Float, CultureInfo.InvariantCulture, out double s);
                bool hasFactor = double.TryParse(Cell(r, fval), NumberStyles.Float, CultureInfo.InvariantCulture, out double f);
                if (hasSample && hasFactor && Math.Abs(s - f) > 1e-9 * Math.Max(1, Math.Abs(s)))
                    notes.Add($"{Cell(r, run)}: the sample's value {s.ToString("R", CultureInfo.InvariantCulture)} and the design factor's {f.ToString("R", CultureInfo.InvariantCulture)} disagree; the sample's is used");
                if (hasSample || hasFactor) kept.Add((r, hasSample ? s : f));
                else if (!Empty(Cell(r, flev))) levelOnly++;
                else none++;
            }
            if (excluded > 0) notes.Add($"{excluded} run(s) of samples marked {string.Join(" or ", t.SampleExclusions)} left out");
            if (levelOnly > 0) notes.Add($"{levelOnly} run(s) with a '{t.DesignFactor}' level and no number left out");
            if (none > 0) notes.Add($"{none} run(s) with no value left out");
            if (kept.Count == 0) { written.Add(Out("no run has a value")); continue; }
            // A stratum (a tissue) is fitted level by level, never pooled and never a covariate (aging D5); that fit is
            // not built, so a deposit where one varies waits.
            if (strata.Order(StringComparer.Ordinal).FirstOrDefault(s => kept.Select(k => LevelOf(k.Row, s)).Distinct().Count() > 1) is { } stratum)
            {
                written.Add(Out($"its {t.StratumFactorRole} factor '{stratum}' has {kept.Select(k => LevelOf(k.Row, stratum)).Distinct().Count()} levels, and a fit per level is not built yet"));
                continue;
            }

            // The covariates: the named ones, then each other factor of the role on these runs.
            var candidates = cov.Select((index, i) => (Name: ColumnName(t.Covariates[i]), Level: (Func<string[], string>)(r => Cell(r, index))))
                .Concat(roleNames.Where(f => kept.Any(k => LevelOf(k.Row, f) != ""))
                    .Select(f => (Name: ColumnName(f), Level: (Func<string[], string>)(r => LevelOf(r, f)))))
                .ToList();
            var columns = new List<(string Name, Func<string[], string> Level)>();
            var partitions = new List<string>();
            foreach (var c in candidates)
            {
                if (columns.Any(k => k.Name == c.Name)) continue;
                var values = kept.Select(k => (Level: c.Level(k.Row), Individual: Cell(k.Row, rep))).ToList();
                if (values.Any(v => Empty(v.Level))) { notes.Add($"covariate {c.Name} is incomplete, so it is left out"); continue; }
                var levels = values.GroupBy(v => v.Level).ToList();
                if (levels.Count < 2) continue;
                if (levels.Any(l => l.Select(v => v.Individual).Distinct().Count() < 2)) { notes.Add($"covariate {c.Name} has a level held by fewer than two individuals, so it is left out"); continue; }
                // The same split of the runs under other names is one covariate.
                var index = values.Select(v => v.Level).Distinct().Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i, StringComparer.Ordinal);
                string partition = string.Join(",", values.Select(v => index[v.Level]));
                if (partitions.Contains(partition)) { notes.Add($"covariate {c.Name} splits the samples as another does, so it is left out"); continue; }
                partitions.Add(partition);
                columns.Add(c);
            }

            string file = Path.Combine(outDir, $"{group.Key}.{t.TraitId}.traits.tsv");
            using (var w = new StreamWriter(file, false, new System.Text.UTF8Encoding(false)))
            {
                w.Write(string.Join("\t", new[] { "dataset", "run", "replicate", "trait_id", "value" }.Concat(columns.Select(k => k.Name))) + "\n");
                foreach (var (r, v) in kept.OrderBy(k => Cell(k.Row, run), StringComparer.Ordinal))
                    w.Write(string.Join("\t", new[] { group.Key, Cell(r, run), Cell(r, rep), t.TraitId, (v * t.Scale).ToString("R", CultureInfo.InvariantCulture) }
                        .Concat(columns.Select(k => k.Level(r)))) + "\n");
            }
            written.Add(new TraitsFile(group.Key, t.TraitId, file, kept.Count, columns.Select(k => k.Name).ToList(), notes));
        }
        return written;
    }
}
