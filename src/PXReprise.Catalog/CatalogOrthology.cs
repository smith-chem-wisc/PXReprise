using System.Text.RegularExpressions;
using PXReprise.Bundle;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;

namespace PXReprise.Catalog;

/// <summary>The registered orthology snapshot as catalog tables, so "every ortholog of protein X" is one query
/// (<c>logs.register_orthology</c>; logs' <c>logs:DEF-ORTHOLOGY v1</c>, from Ensembl Compara).</summary>
/// <remarks>
/// <para><b>The pattern for repository-wide reference data.</b> Orthology is the first reference a catalog serves that
/// belongs to no bundle and no search. GO and KEGG are to follow it, so the shape is stated once, here:</para>
/// <list type="number">
/// <item><b>One copy per reference release, never per bundle.</b> The reference is registered once as an engine
/// artefact, checked against its producer's file contract and kept whole in the store
/// (<c>&lt;store&gt;/_engine/&lt;engine&gt;/&lt;id&gt;/</c>). A catalog serves at most one (<see cref="SelectOrthology"/>), and
/// its id names it.</item>
/// <item><b>Reference tables, always there.</b> A build copies the reference's rows into tables of kind
/// <c>engine:&lt;engine&gt;</c>, every row carrying <c>artefact_id</c>. With no reference selected the tables are created
/// typed and empty, so the catalog's shape never depends on what is registered, and an empty table reads "none served",
/// never "no such table".</item>
/// <item><b>One derived bridge</b> from the repository's own accessions to the reference, in which every refusal is a row
/// with its reason (here <c>protein_orthologs.status</c>). An empty answer reads as "not detected", which is false.</item>
/// <item><b>Checks against the registration's own numbers</b> (kind <c>engine-consistency</c>), and that the bridge
/// answers every source row. A failing one fails the build.</item>
/// </list>
/// <para><b>Copied in both layouts.</b> The tables are small (about 200,000 genes, 170,000 ortholog rows and 400,000
/// status rows for human, mouse and rat), so a one-file catalog needs no Parquet at query time, which the MCP sandbox
/// forbids, and a folder catalog does not depend on the snapshot's macros. The rows are what the snapshot's own
/// <c>views.sql</c> gives (logs' SQL, never restated here), run on the build connection as TEMP macros and dropped, so
/// nothing of them reaches the written catalog. Columns are selected by name, as logs' contract asks.</para>
/// <para><b>What the bridge follows</b> (logs' rules for "every ortholog of protein X"): the accession to its gene
/// through <c>gene_resolutions</c>, stating the outcome, a <c>multi_gene</c> accession following each of its genes; the
/// gene to its orthologs in each other species directly, never chained, every row kept; each ortholog gene back to the
/// accessions that resolve to it in that species' searched databases. Paralogs never, gene symbols never.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    public const string OrthologySpeciesTable = "orthology_species";
    public const string OrthologyGenesTable = "orthology_genes";
    public const string GeneOrthologsTable = "gene_orthologs";
    public const string GeneOrthologStatusTable = "gene_ortholog_status";
    public const string ProteinOrthologsTable = "protein_orthologs";

    /// <summary>The snapshot format a build reads, and its one version: logs' file contract. The registration
    /// (<c>OrthologyEngine</c>) refuses any other, and so does a build, rather than read another version as this one.</summary>
    public const string OrthologySnapshotFormat = "ensembl-orthology-snapshot";

    public const long OrthologySnapshotFormatVersion = 1;

    /// <summary>The orthology tables every catalog of the current rules holds, in the order they are built: four copied
    /// from the snapshot and the derived bridge, <c>protein_orthologs</c>.</summary>
    public static readonly IReadOnlyList<string> OrthologyTables =
        [OrthologySpeciesTable, OrthologyGenesTable, GeneOrthologsTable, GeneOrthologStatusTable, ProteinOrthologsTable];

    /// <summary>The kind <c>catalog_tables</c> gives an orthology table: the engine's for the snapshot's own rows,
    /// <c>derived</c> for the bridge, which joins them to the repository's proteins.</summary>
    public static string OrthologyTableKind(string table) => table == ProteinOrthologsTable ? "derived" : $"engine:{Runner.OrthologyEngine}";

    /// <summary>Each table's columns and types, so an empty table has the shape a filled one has. The snapshot's columns
    /// keep their contract types (<c>gene_version</c> and <c>goc_score</c> are int32 there).</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string Type)[]> OrthologyColumns =
        new Dictionary<string, (string Name, string Type)[]>(StringComparer.Ordinal)
        {
            [OrthologySpeciesTable] = [("species", "VARCHAR"), ("gene_set_sha256", "VARCHAR"), ("n_genes", "BIGINT"), ("artefact_id", "VARCHAR")],
            [OrthologyGenesTable] =
            [
                ("gene_id", "VARCHAR"), ("gene_version", "INTEGER"), ("gene_biotype", "VARCHAR"), ("gene_name", "VARCHAR"),
                ("seq_region", "VARCHAR"), ("species", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [GeneOrthologsTable] =
            [
                ("species", "VARCHAR"), ("partner_species", "VARCHAR"), ("homology_id", "VARCHAR"), ("relationship_type", "VARCHAR"),
                ("gene", "VARCHAR"), ("protein", "VARCHAR"), ("identity", "DOUBLE"),
                ("partner_gene", "VARCHAR"), ("partner_protein", "VARCHAR"), ("partner_identity", "DOUBLE"),
                ("dn", "DOUBLE"), ("ds", "DOUBLE"), ("goc_score", "INTEGER"), ("wga_coverage", "DOUBLE"), ("is_high_confidence", "BOOLEAN"),
                ("artefact_id", "VARCHAR"),
            ],
            [GeneOrthologStatusTable] =
            [
                ("species", "VARCHAR"), ("gene_id", "VARCHAR"), ("gene_biotype", "VARCHAR"), ("target_species", "VARCHAR"),
                ("status", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [ProteinOrthologsTable] =
            [
                ("protein_accession", "VARCHAR"), ("species", "VARCHAR"), ("resolution_outcome", "VARCHAR"), ("gene_id", "VARCHAR"),
                ("target_species", "VARCHAR"), ("status", "VARCHAR"), ("relationship_type", "VARCHAR"), ("is_high_confidence", "BOOLEAN"),
                ("partner_gene", "VARCHAR"), ("partner_gene_name", "VARCHAR"), ("partner_accession", "VARCHAR"),
                ("partner_resolution_outcome", "VARCHAR"), ("partner_n_datasets_1pct", "BIGINT"), ("definition_id", "VARCHAR"),
                ("artefact_id", "VARCHAR"),
            ],
        };

    /// <summary>The selected snapshot as a build reads it: its directory (absolute, posix) and its record's species and
    /// gene sets.</summary>
    private sealed record OrthologySnapshot(ArtefactRef Artefact, string Root, IReadOnlyList<string> Species, IReadOnlyDictionary<string, string> GeneSets)
    {
        public string Name => $"{Runner.OrthologyEngine}/{Artefact.ArtefactId}";
    }

    /// <summary>An Ensembl production name. Species reach file paths through the snapshot's macros, so nothing else is
    /// let through.</summary>
    private static readonly Regex EnsemblSpecies = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>The snapshot among these artefacts, or null when none is.</summary>
    /// <exception cref="CatalogException">Two snapshots, or a record or directory that is not a format 1 snapshot.</exception>
    private static OrthologySnapshot? OrthologySnapshotOf(IReadOnlyList<ArtefactRef> artefacts)
    {
        var registered = artefacts.Where(a => a.Engine == Runner.OrthologyEngine).ToList();
        if (registered.Count == 0) return null;
        if (registered.Count > 1)
            throw new CatalogException(
                $"{Runner.OrthologyEngine}: {registered.Count} snapshots given ({string.Join(", ", registered.Select(a => a.ArtefactId))}). "
                + "A catalog serves one.");
        var artefact = registered[0];
        CatalogException Refused(string why) => new($"{Runner.OrthologyEngine}/{artefact.ArtefactId}: {why}. Register the snapshot again.");

        var record = SourcesPy.Get(artefact.Record, "snapshot") as IReadOnlyDictionary<string, object?>
            ?? throw Refused("run.json has no snapshot record");
        if (!Equals(SourcesPy.Get(record, "format"), OrthologySnapshotFormat)
            || SourcesPy.Get(record, "format_version") is not long version || version != OrthologySnapshotFormatVersion)
            throw Refused($"the snapshot is {PyStr(SourcesPy.Get(record, "format"))} version {PyStr(SourcesPy.Get(record, "format_version"))}, "
                + $"and this build reads {OrthologySnapshotFormat} version {OrthologySnapshotFormatVersion} only");
        var directory = SourcesPy.Get(record, "directory") as string;
        if (string.IsNullOrEmpty(directory) || Path.IsPathRooted(directory) || directory.StartsWith('/')
            || directory.Replace('\\', '/').Split('/').Any(p => p is "" or "." or ".."))
            throw Refused($"its snapshot directory {PyStr(directory)} is not a folder inside the artefact");
        var root = Path.GetFullPath(Path.Combine(artefact.Path, directory));
        if (!File.Exists(Path.Combine(root, "views.sql")))
            throw Refused($"{SourcesPy.PathStr(Path.Combine(root, "views.sql"))} is missing");
        var species = (SourcesPy.Get(record, "species") as IEnumerable<object?> ?? []).Select(s => s as string ?? "").ToList();
        if (species.Count == 0 || species.Any(s => !EnsemblSpecies.IsMatch(s)) || species.Distinct(StringComparer.Ordinal).Count() != species.Count)
            throw Refused($"its species [{string.Join(", ", species)}] are not distinct Ensembl production names");
        var sets = SourcesPy.Get(record, "gene_set_sha256") as IReadOnlyDictionary<string, object?>;
        if (sets is null || !sets.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(species) || sets.Values.Any(v => v is not string { Length: > 0 }))
            throw Refused("its gene_set_sha256 does not name one gene set for each of its species");
        return new OrthologySnapshot(artefact, AsPosix(root), species,
            sets.ToDictionary(kv => kv.Key, kv => (string)kv.Value!, StringComparer.Ordinal));
    }

    /// <summary>Every ordered pair of distinct species, in the record's order.</summary>
    private static IEnumerable<(string A, string B)> OrderedPairs(IReadOnlyList<string> species) =>
        species.SelectMany(a => species.Where(b => b != a).Select(b => (a, b)));

    /// <summary>The accessions the bridge answers for, each with its gene resolution in a snapshot species: the
    /// <c>gene_resolutions</c> rows of target proteins a search held (<c>protein_genes</c>, which leaves out decoys,
    /// contaminants and proteins whose label is unknown), read through the database that search read. One row per
    /// (accession, gene set, outcome, gene): identical rows from several databases are one, naming every definition
    /// behind them. A gene set that is not the snapshot's maps to no species (and fails <see cref="CheckOrthology"/>).</summary>
    private const string OrthologySources = @"
        SELECT g.accession, s.species, g.outcome, g.gene_id,
               array_to_string(list_sort(list(DISTINCT g.definition_id)), '; ') AS definition_id
        FROM gene_resolutions g
        JOIN orthology_species s ON s.gene_set_sha256 = g.gene_set_sha256
        WHERE EXISTS (SELECT 1 FROM protein_genes pg WHERE pg.artefact_id = g.artefact_id AND pg.protein_accession = g.accession)
        GROUP BY g.accession, s.species, g.gene_set_sha256, g.outcome, g.gene_id";

    /// <summary><c>protein_orthologs</c>: every source accession against every other species. <c>{artefact_id}</c> is the
    /// snapshot's, as a literal.</summary>
    /// <remarks>Every LEFT JOIN keeps the row it starts from, so each (accession, gene, target species) gives at least one
    /// row whatever its status (<see cref="CheckOrthologyTables"/> checks it). A <c>has_ortholog</c> gene gives a row per
    /// ortholog gene (one-to-many and many-to-many kept), times each accession resolving to that gene in the target's
    /// gene set, or one row with no accession.</remarks>
    private const string ProteinOrthologsSelect = $@"
        WITH resolutions AS ({OrthologySources}
        ),
        asked AS (
            SELECT r.accession, r.species, r.outcome, r.gene_id, r.definition_id, t.species AS target_species,
                   CASE WHEN r.gene_id IS NULL THEN 'no_gene'
                        WHEN g.gene_id IS NULL THEN 'not_in_gene_set'
                        ELSE st.status END AS status
            FROM resolutions r
            JOIN orthology_species t ON t.species <> r.species
            LEFT JOIN orthology_genes g ON g.species = r.species AND g.gene_id = r.gene_id
            LEFT JOIN gene_ortholog_status st
                   ON st.species = r.species AND st.gene_id = r.gene_id AND st.target_species = t.species
        ),
        partners AS (
            SELECT accession, species, gene_id, array_to_string(list_sort(list(DISTINCT outcome)), '; ') AS outcome
            FROM resolutions WHERE gene_id IS NOT NULL
            GROUP BY accession, species, gene_id
        )
        SELECT a.accession AS protein_accession, a.species, a.outcome AS resolution_outcome, a.gene_id,
               a.target_species, a.status, o.relationship_type, o.is_high_confidence, o.partner_gene,
               pg.gene_name AS partner_gene_name, p.accession AS partner_accession, p.outcome AS partner_resolution_outcome,
               CASE WHEN p.accession IS NOT NULL THEN coalesce(pi.n_datasets_1pct, 0) END AS partner_n_datasets_1pct,
               a.definition_id, {{artefact_id}} AS artefact_id
        FROM asked a
        LEFT JOIN gene_orthologs o
               ON a.status = 'has_ortholog' AND o.species = a.species AND o.gene = a.gene_id AND o.partner_species = a.target_species
        LEFT JOIN orthology_genes pg ON pg.species = o.partner_species AND pg.gene_id = o.partner_gene
        LEFT JOIN partners p ON p.species = o.partner_species AND p.gene_id = o.partner_gene
        LEFT JOIN protein_index pi ON pi.protein_accession = p.accession
        ORDER BY a.accession, a.species, a.gene_id NULLS FIRST, a.target_species, o.partner_gene, p.accession";

    /// <summary>The five orthology tables (<see cref="OrthologyTables"/>), filled from the selected snapshot, or typed and
    /// empty when there is none.</summary>
    /// <returns><c>{table: rows}</c> when a snapshot was loaded, else nothing: go's and ptmQtl's tables report only what an
    /// artefact filled, too.</returns>
    /// <remarks>Runs after <see cref="BuildEngineDerived"/>, since the bridge reads <c>gene_resolutions</c>,
    /// <c>protein_genes</c> and <c>protein_index</c>.</remarks>
    /// <exception cref="CatalogException">Two snapshots, a record that is not a format 1 snapshot, files that cannot be
    /// read, or a <c>views.sql</c> that does more than define macros.</exception>
    private static Dictionary<string, long> BuildOrthology(DuckDBConnection con, IReadOnlyList<ArtefactRef> artefacts)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!GoActive) return counts;
        foreach (var table in OrthologyTables)
            Exec(con, $"CREATE TABLE \"{table}\" ({string.Join(", ", OrthologyColumns[table].Select(c => $"\"{c.Name}\" {c.Type}"))})");
        var snapshot = OrthologySnapshotOf(artefacts);
        if (snapshot is null) return counts;

        var aid = Quote(snapshot.Artefact.ArtefactId);
        var root = Quote(snapshot.Root);
        try
        {
            foreach (var s in snapshot.Species)
                Exec(con, $"INSERT INTO {OrthologyGenesTable} BY NAME SELECT gene_id, gene_version, gene_biotype, gene_name, seq_region, species, "
                    + $"{aid} AS artefact_id FROM read_parquet({Quote($"{snapshot.Root}/genes/{s}.parquet")})");
            foreach (var s in snapshot.Species)
                Exec(con, $"INSERT INTO {OrthologySpeciesTable} SELECT ?, ?, (SELECT count(*) FROM {OrthologyGenesTable} WHERE species = ?), ?",
                    s, snapshot.GeneSets[s], s, snapshot.Artefact.ArtefactId);
            var macros = LoadOrthologyMacros(con, snapshot);
            try
            {
                foreach (var (a, b) in OrderedPairs(snapshot.Species))
                {
                    var args = $"{root}, {Quote(a)}, {Quote(b)}";
                    Exec(con, $"INSERT INTO {GeneOrthologsTable} BY NAME SELECT {Quote(a)} AS species, {Quote(b)} AS partner_species, "
                        + "homology_id, relationship_type, gene, protein, identity, partner_gene, partner_protein, partner_identity, "
                        + $"dn, ds, goc_score, wga_coverage, is_high_confidence, {aid} AS artefact_id FROM orthologs({args})");
                    Exec(con, $"INSERT INTO {GeneOrthologStatusTable} BY NAME SELECT {Quote(a)} AS species, gene_id, gene_biotype, "
                        + $"{Quote(b)} AS target_species, status, {aid} AS artefact_id FROM pair_status({args})");
                }
            }
            finally
            {
                DropOrthologyMacros(con, macros);
            }
        }
        catch (DuckDBException e)
        {
            throw new CatalogException($"{snapshot.Name}: the snapshot at {snapshot.Root} could not be read: {e.Message}");
        }
        Exec(con, $"INSERT INTO {ProteinOrthologsTable} BY NAME {ProteinOrthologsSelect.Replace("{artefact_id}", aid)}");
        foreach (var table in OrthologyTables) counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        return counts;
    }

    /// <summary>One <c>CREATE OR REPLACE MACRO</c> statement of <c>views.sql</c>, and the macro's name.</summary>
    private static readonly Regex ViewsMacro = new(@"\bCREATE\s+OR\s+REPLACE\s+MACRO\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The macros of logs' contract that the build calls.</summary>
    private static readonly string[] OrthologyMacrosCalled = ["orthologs", "pair_status"];

    /// <summary>A count of the persistent tables, views and macros on the connection, to show that loading
    /// <c>views.sql</c> added none.</summary>
    private const string PersistentObjects =
        "SELECT (SELECT count(*) FROM duckdb_tables() WHERE NOT temporary) "
        + "+ (SELECT count(*) FROM duckdb_views() WHERE NOT internal AND NOT temporary) "
        + "+ (SELECT count(DISTINCT function_name) FROM duckdb_functions() "
        + "   WHERE function_type IN ('macro', 'table_macro') AND database_name <> 'temp' AND NOT internal)";

    /// <summary>Runs the snapshot's <c>views.sql</c> with every macro made TEMP, so the macros live only on this
    /// connection and never in the catalog file.</summary>
    /// <returns>The macros created, with their DuckDB function type, for <see cref="DropOrthologyMacros"/>.</returns>
    /// <exception cref="CatalogException">The file lacks <c>orthologs</c> or <c>pair_status</c>, or running it created
    /// anything but the TEMP macros its statements name: a persistent object would be written into the catalog.</exception>
    private static List<(string Name, string Type)> LoadOrthologyMacros(DuckDBConnection con, OrthologySnapshot snapshot)
    {
        var path = $"{snapshot.Root}/views.sql";
        var named = new List<string>();
        var sql = ViewsMacro.Replace(File.ReadAllText(path), m =>
        {
            named.Add(m.Groups["name"].Value);
            return $"CREATE OR REPLACE TEMP MACRO {m.Groups["name"].Value}";
        });
        var missing = OrthologyMacrosCalled.Where(n => !named.Contains(n, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
            throw new CatalogException(
                $"{snapshot.Name}: {path} defines no {string.Join(" or ", missing)} macro (it defines [{string.Join(", ", named)}]), "
                + "which logs' file contract promises");
        var before = Count(con, PersistentObjects);
        Exec(con, sql);
        var created = Query(con,
                "SELECT DISTINCT function_name, function_type FROM duckdb_functions() "
                + "WHERE database_name = 'temp' AND function_type IN ('macro', 'table_macro')")
            .Select(row => ((string)row[0]!, (string)row[1]!))
            .Where(m => named.Contains(m.Item1, StringComparer.Ordinal))
            .ToList();
        var added = Count(con, PersistentObjects) - before;
        if (added != 0 || created.Count != named.Distinct(StringComparer.Ordinal).Count())
        {
            DropOrthologyMacros(con, created);
            throw new CatalogException(
                $"{snapshot.Name}: {path} did more than define its macros: {named.Count} macro statement(s) were made TEMP, "
                + $"{created.Count} TEMP macro(s) were created, and {added} other object(s) would have been written into the catalog");
        }
        return created;
    }

    /// <summary>Drops the TEMP macros, table macros first since they call the others.</summary>
    private static void DropOrthologyMacros(DuckDBConnection con, IReadOnlyList<(string Name, string Type)> macros)
    {
        foreach (var (name, type) in macros.OrderBy(m => m.Type == "table_macro" ? 0 : 1).ThenBy(m => m.Name, StringComparer.Ordinal))
            Exec(con, $"DROP MACRO {(type == "table_macro" ? "TABLE " : "")}IF EXISTS temp.main.\"{name}\"");
    }

    /// <summary>The orthology tables agree with the registration, and the bridge answers every source accession.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>For each ordered pair, <c>gene_ortholog_status</c>'s count of each status equals the count the registration
    /// recorded in <c>run.json</c> (<c>engine_summary.pair_status</c>), and no direction is in one and not the other.</item>
    /// <item>Each species' <c>orthology_genes</c> rows equal the genes the registration counted for it.</item>
    /// <item>Every source accession has at least one <c>protein_orthologs</c> row for each target species: refusals are
    /// rows, so an absence would read as "no ortholog" when the bridge simply lost the row.</item>
    /// </list>
    /// </remarks>
    private static List<CatalogCheck> CheckOrthologyTables(DuckDBConnection con, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        if (!GoActive) return checks;
        var snapshot = OrthologySnapshotOf(artefacts);
        if (snapshot is null) return checks;

        // Statuses counted 0 are left out on both sides: the registration lists all four, a GROUP BY only those present.
        var registered = new Dictionary<(string From, string To), (long Genes, SortedDictionary<string, long> Counts)>();
        foreach (var d in (Summary(snapshot.Artefact)?.GetValueOrDefault("pair_status") as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>())
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var (status, n) in d.GetValueOrDefault("status_counts") as IReadOnlyDictionary<string, object?> ?? SourcesPy.EmptyDict)
                if (PyInt(n) != 0) counts[status] = PyInt(n);
            registered[(SourcesPy.Str(d.GetValueOrDefault("from")), SourcesPy.Str(d.GetValueOrDefault("to")))] = (PyInt(d.GetValueOrDefault("genes") ?? 0L), counts);
        }
        var observed = new Dictionary<(string From, string To), SortedDictionary<string, long>>();
        foreach (var row in Query(con, $"SELECT species, target_species, status, count(*) FROM {GeneOrthologStatusTable} GROUP BY ALL"))
        {
            var key = ((string)row[0]!, (string)row[1]!);
            if (!observed.TryGetValue(key, out var counts)) observed[key] = counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            counts[PyStr(row[2])] = PyInt(row[3]);
        }
        static string Describe(SortedDictionary<string, long> counts) => string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"));
        foreach (var key in registered.Keys.Union(observed.Keys).OrderBy(k => k.From, StringComparer.Ordinal).ThenBy(k => k.To, StringComparer.Ordinal))
        {
            var mine = observed.GetValueOrDefault(key) ?? new SortedDictionary<string, long>(StringComparer.Ordinal);
            var theirs = registered.TryGetValue(key, out var r) ? r.Counts : null;
            var ok = theirs is not null && mine.Count == theirs.Count && mine.All(kv => theirs.TryGetValue(kv.Key, out var n) && n == kv.Value);
            checks.Add(new CatalogCheck($"{snapshot.Name} pair_status {key.From} -> {key.To} (gene_ortholog_status)", "engine-consistency", ok,
                mine.Values.Sum(), theirs?.Values.Sum(),
                ok ? Describe(mine)
                    : $"the catalog has [{Describe(mine)}]; the registration recorded "
                      + (theirs is null ? "no such direction" : $"[{Describe(theirs)}]")));
        }
        var genes = Query(con, $"SELECT species, count(*) FROM {OrthologyGenesTable} GROUP BY species")
            .ToDictionary(row => (string)row[0]!, row => PyInt(row[1]), StringComparer.Ordinal);
        foreach (var s in snapshot.Species)
        {
            var counted = registered.Where(kv => kv.Key.From == s).Select(kv => (long?)kv.Value.Genes).FirstOrDefault();
            if (counted is null) continue;  // a one-species snapshot has no direction to have counted it in
            var held = genes.GetValueOrDefault(s);
            checks.Add(new CatalogCheck($"{snapshot.Name} genes {s} (orthology_genes)", "engine-consistency", held == counted,
                held, counted, $"genes/{s}.parquet, against the registration's count"));
        }

        var coverage = Query(con, $@"
            WITH resolutions AS ({OrthologySources}
            ),
            expected AS (
                SELECT DISTINCT r.accession, r.species, t.species AS target_species
                FROM resolutions r JOIN orthology_species t ON t.species <> r.species
            ),
            covered AS (SELECT DISTINCT protein_accession, species, target_species FROM {ProteinOrthologsTable})
            SELECT count(*), count(c.protein_accession),
                   min(e.accession || ' (' || e.species || ') -> ' || e.target_species) FILTER (WHERE c.protein_accession IS NULL),
                   count(DISTINCT e.accession)
            FROM expected e
            LEFT JOIN covered c ON c.protein_accession = e.accession AND c.species = e.species AND c.target_species = e.target_species")[0];
        var (wanted, answered) = (PyInt(coverage[0]), PyInt(coverage[1]));
        checks.Add(new CatalogCheck($"{snapshot.Name} protein_orthologs (a row per accession and target species)", "engine-consistency",
            answered == wanted, answered, wanted,
            answered == wanted
                ? $"{PyInt(coverage[3])} accession(s) with a gene resolution in a snapshot species, each answered for every other species"
                : $"no row for {wanted - answered} (accession, target species) pair(s), e.g. {PyStr(coverage[2])}"));
        return checks;
    }
}
