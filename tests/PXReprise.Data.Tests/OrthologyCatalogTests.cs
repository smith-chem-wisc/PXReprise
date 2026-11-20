using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest;
using PXReprise.Runner;
using DuckDB.NET.Data;

namespace PXReprise.Data.Tests;

/// <summary>The registered orthology snapshot in a catalog (<c>CatalogOrthology.cs</c>): five tables, in either layout,
/// so "every ortholog of protein X" is one query, with every refusal a row.</summary>
/// <remarks>Runs on the current rules (a Python 0.32.0 parity build has no orthology tables). The fixture search is
/// ingested into a scratch store, the snapshot is <see cref="OrthologySnapshots"/>' with A5 a one-to-many ortholog of B3
/// and B4, and a gene resolution maps real fixture accessions onto its genes:
/// <list type="bullet">
/// <item>P04406 to A1, which has an ortholog (B1, which P08238 resolves to);</item>
/// <item>P07437 to A1 and A3 (<c>multi_gene</c>), which follows both;</item>
/// <item>P60709 to A2, P21266 to A4: no edge in a shared tree, and in no tree;</item>
/// <item>P05141 to no gene (<c>not_in_source</c>);</item>
/// <item>P13010 to no gene in the search database, and to A2 through Ensembl's xref: logs 023's case, two rows, the
/// xref row keeping the <c>not_in_source</c> outcome. It must be answered through A2 only, never also <c>no_gene</c>;</item>
/// <item>P35527 to A5, whose orthologs are B3 (P08123 resolves to it, on a v1 row) and B4 (nothing does);</item>
/// <item>P12882 to B2, which has no edge in its shared tree;</item>
/// <item>P00761 (a contaminant in the fixture) and Q9ZZZ9 (in no searched database) to A1: neither is a target protein a
/// search held, so neither reaches <c>protein_orthologs</c>, as a source or as a partner.</item>
/// </list>
/// The links carry each of v2's agreements (<c>agrees</c>, <c>silent</c>, <c>disagrees</c>), and P08123's is a v1 row,
/// with the boolean only. One artefact resolves the fixture's one target database against both test gene sets, which a
/// real resolution never does (one gene set per species), so that one search can stand for two species here.</remarks>
public class OrthologyCatalogTests
{
    private const string V2 = "logs:DEF-GENE-RESOLUTION v2";
    private const string V1 = "logs:DEF-GENE-RESOLUTION v1";
    private const string Db = "search_database_dbreference";
    private const string Xref = "ensembl_xref";
    private const string GeneSetA = OrthologySnapshots.GeneSetA;
    private const string GeneSetB = OrthologySnapshots.GeneSetB;

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-orthology-catalog-" + Guid.NewGuid().ToString("N"));
        var from = DataPaths.Instance;
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
    }

    [TearDown]
    public void RemoveScratch()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { /* a DuckDB file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    private CatalogResult Build(string output)
    {
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance, artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    /// <summary>One <c>gene_resolutions</c> row of the fixture: the columns the bridge reads.</summary>
    private sealed record Link(string Accession, string Outcome, string? Gene, string GeneSet, string Source, string? Agreement, bool? Agrees,
        string Definition = V2);

    /// <summary>The resolution in the class remarks.</summary>
    private static readonly Link[] Resolutions =
    [
        new("P04406", "resolved", "A1", GeneSetA, Db, "agrees", true),
        new("P07437", "multi_gene", "A1", GeneSetA, Db, "agrees", true),
        new("P07437", "multi_gene", "A3", GeneSetA, Db, "disagrees", false),
        new("P60709", "resolved", "A2", GeneSetA, Db, "silent", false),
        new("P21266", "resolved", "A4", GeneSetA, Db, "agrees", true),
        new("P13010", "not_in_source", null, GeneSetA, Db, null, null),
        new("P13010", "not_in_source", "A2", GeneSetA, Xref, "agrees", true),
        new("P05141", "not_in_source", null, GeneSetA, Db, null, null),
        new("P35527", "resolved", "A5", GeneSetA, Db, "agrees", true),
        new("P00761", "resolved", "A1", GeneSetA, Db, "agrees", true),
        new("Q9ZZZ9", "resolved", "A1", GeneSetA, Db, "agrees", true),
        new("P08238", "resolved", "B1", GeneSetB, Db, "silent", false),
        new("P12882", "resolved", "B2", GeneSetB, Db, "agrees", true),
        new("P08123", "resolved", "B3", GeneSetB, Db, null, true, V1),
    ];

    /// <summary>A current-schema <c>logs.resolve_genes</c> artefact for the fixture's target database, with these rows
    /// (<see cref="Resolutions"/> by default), as the engine's writer stores them.</summary>
    private ArtefactRef ResolveGenes(IEnumerable<Link>? links = null)
    {
        var all = (links ?? Resolutions).ToList();
        var bundle = CatalogBuilder.SelectBundles(Manifest.Load(ManifestPath), ["PXD999999"], Store).Single();
        var database = CatalogBuilder.GoInputsOf(bundle).Databases.Single();
        var rows = all.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["definition_id"] = r.Definition, ["accession"] = r.Accession, ["entry_accession"] = r.Accession, ["namespace"] = "uniprot",
            ["outcome"] = r.Outcome, ["n_genes"] = all.Count(o => o.Accession == r.Accession && o.Gene is not null && o.Source == Db),
            ["gene_id"] = r.Gene, ["gene_symbol"] = r.Gene?.ToLowerInvariant(), ["gene_biotype"] = r.Gene is null ? null : "protein_coding",
            ["off_primary_genes"] = 0L, ["source"] = r.Source, ["search_database_sha256"] = database, ["gene_set_release"] = "116",
            ["gene_set_sha256"] = r.GeneSet, ["ensembl_xref_agrees"] = r.Agrees, ["ensembl_xref_agreement"] = r.Agreement,
            ["ensembl_xref_sha256"] = "x",
        }).ToList();
        var inputs = new Dictionary<string, string> { ["search_database"] = database };
        const string engine = "logs.resolve_genes";
        return EngineRunner.WriteArtefact(Store, engine,
            EngineRunner.ArtefactId(engine, new Dictionary<string, string> { ["logs"] = "test" }, inputs, V2),
            new Dictionary<string, object?>
            {
                ["definition_id"] = V2,
                ["release"] = new Dictionary<string, object?> { ["logs"] = "test" },
                ["inputs"] = inputs.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            },
            new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { ["gene_resolutions"] = rows });
    }

    private ArtefactRef RegisterSnapshot()
    {
        var (inputs, release) = OrthologySnapshots.Release(OrthologySnapshots.Make(_root, "snap1", oneToMany: true));
        return OrthologySnapshots.Register(Store, inputs, release).Written.Single();
    }

    private static string Cell(object? value) => value switch
    {
        null => "-",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    private static List<string> Rows(string catalog, string sql) =>
        CatalogBuilder.RunQuery(catalog, sql).Rows.Select(r => string.Join("|", r.Select(Cell))).ToList();

    private static long Scalar(string catalog, string sql) => Convert.ToInt64(CatalogBuilder.RunQuery(catalog, sql).Rows.Single()[0], CultureInfo.InvariantCulture);

    /// <summary>The snapshot's own answer: its <c>views.sql</c>, loaded as logs ships it into a separate in-memory DuckDB.</summary>
    private static List<string> Oracle(string snapshot, string sql)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using (var load = con.CreateCommand())
        {
            load.CommandText = File.ReadAllText(Path.Combine(snapshot, "views.sql"));
            load.ExecuteNonQuery();
        }
        using var query = con.CreateCommand();
        query.CommandText = sql.Replace("{root}", snapshot.Replace('\\', '/'));
        using var reader = query.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader.IsDBNull(i) ? null : reader.GetValue(i)))));
        return rows;
    }

    private static readonly string TableList = string.Join(", ", CatalogBuilder.OrthologyTables.Select(t => $"'{t}'"));

    private static List<string> Shape(string catalog) => Rows(catalog,
        $"SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_name IN ({TableList}) "
        + "ORDER BY table_name, ordinal_position");

    [Test]
    public void WithNoSnapshotTheFiveTablesAreThereAndEmpty()
    {
        ResolveGenes();
        foreach (var output in new[] { Path.Combine(_root, "catalog.duckdb"), Path.Combine(_root, "catalogs") })
        {
            var built = Build(output);
            Assert.Multiple(() =>
            {
                Assert.That(Rows(built.Path, $"SELECT table_name, rows, kind FROM catalog_tables WHERE table_name IN ({TableList}) ORDER BY table_name"),
                    Is.EqualTo(new[]
                    {
                        "gene_ortholog_status|0|engine:logs.register_orthology",
                        "gene_orthologs|0|engine:logs.register_orthology",
                        "orthology_genes|0|engine:logs.register_orthology",
                        "orthology_species|0|engine:logs.register_orthology",
                        "protein_orthologs|0|derived",
                    }), output);
                foreach (var table in CatalogBuilder.OrthologyTables)
                {
                    Assert.That(Scalar(built.Path, $"SELECT count(*) FROM {table}"), Is.EqualTo(0), $"{table}, with gene resolutions but no snapshot");
                    Assert.That(Rows(built.Path, $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"),
                        Is.EquivalentTo(CatalogBuilder.DerivedDocs[table].Columns.Keys), $"{table}: every column is documented, and only those");
                }
                Assert.That(Scalar(built.Path, "SELECT count(*) FROM gene_resolutions"), Is.EqualTo(Resolutions.Length));
                Assert.That(built.Checks.Where(c => c.Kind == "engine-consistency"), Is.Empty, "nothing to check without a snapshot");
                Assert.That(built.Checks.Single(c => c.Name.StartsWith("logs.register_orthology", StringComparison.Ordinal)).Detail,
                    Is.EqualTo("no snapshot registered"));
                Assert.That(Rows(built.Path, "SELECT catalog_version FROM catalog_meta").Single(), Is.EqualTo("17"));
            });
        }
    }

    [Test]
    public void ASnapshotReachesTheCatalogInBothLayoutsAndEveryProteinGetsAnAnswer()
    {
        ResolveGenes();
        var empty = Build(Path.Combine(_root, "before.duckdb"));
        var snapshot = RegisterSnapshot();
        var root = Path.Combine(snapshot.Path, "snapshot");
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Path.Combine(_root, "catalogs"));
        Assert.That(folder.Layout, Is.EqualTo(CatalogLayout.Folder));
        Assert.That(Rows(file.Path, "SELECT is_contaminant FROM proteins WHERE protein_accession = 'P00761'"), Is.EqualTo(new[] { "true" }),
            "the fixture searched P00761 as a contaminant");

        var expectedOrthologs = Oracle(root,
            "SELECT 'sp_a', 'sp_b', homology_id, relationship_type, gene, protein, identity, partner_gene, partner_protein, partner_identity, "
            + "dn, ds, goc_score, wga_coverage, is_high_confidence FROM orthologs('{root}', 'sp_a', 'sp_b') UNION ALL "
            + "SELECT 'sp_b', 'sp_a', homology_id, relationship_type, gene, protein, identity, partner_gene, partner_protein, partner_identity, "
            + "dn, ds, goc_score, wga_coverage, is_high_confidence FROM orthologs('{root}', 'sp_b', 'sp_a') ORDER BY ALL");
        var expectedStatus = Oracle(root,
            "SELECT 'sp_a', gene_id, gene_biotype, 'sp_b', status FROM pair_status('{root}', 'sp_a', 'sp_b') UNION ALL "
            + "SELECT 'sp_b', gene_id, gene_biotype, 'sp_a', status FROM pair_status('{root}', 'sp_b', 'sp_a') ORDER BY ALL");
        Assert.That(expectedStatus, Has.Count.EqualTo(9), "the oracle answers: 5 genes of sp_a, 4 of sp_b");

        // protein_accession, species, resolution_outcome, resolution_source, gene_id, target_species, status,
        // relationship_type, is_high_confidence, partner_gene, partner_gene_name, partner_accession,
        // partner_resolution_outcome, partner_resolution_source.
        string[] answers =
        [
            $"P04406|sp_a|resolved|{Db}|A1|sp_b|has_ortholog|ortholog_one2one|true|B1|b1|P08238|resolved|{Db}",
            $"P07437|sp_a|multi_gene|{Db}|A1|sp_b|has_ortholog|ortholog_one2one|true|B1|b1|P08238|resolved|{Db}",
            $"P07437|sp_a|multi_gene|{Db}|A3|sp_b|tree_lacks_target_species|-|-|-|-|-|-|-",
            $"P60709|sp_a|resolved|{Db}|A2|sp_b|no_edge_in_shared_tree|-|-|-|-|-|-|-",
            $"P21266|sp_a|resolved|{Db}|A4|sp_b|not_in_any_tree|-|-|-|-|-|-|-",
            // logs 023: Ensembl's gene answers, and the no-gene row of the search database gives no second, no_gene answer.
            $"P13010|sp_a|not_in_source|{Xref}|A2|sp_b|no_edge_in_shared_tree|-|-|-|-|-|-|-",
            $"P05141|sp_a|not_in_source|{Db}|-|sp_b|no_gene|-|-|-|-|-|-|-",
            $"P35527|sp_a|resolved|{Db}|A5|sp_b|has_ortholog|ortholog_one2many|true|B3|b3|P08123|resolved|{Db}",
            $"P35527|sp_a|resolved|{Db}|A5|sp_b|has_ortholog|ortholog_one2many|true|B4|-|-|-|-",
            $"P08238|sp_b|resolved|{Db}|B1|sp_a|has_ortholog|ortholog_one2one|true|A1|a1|P04406|resolved|{Db}",
            $"P08238|sp_b|resolved|{Db}|B1|sp_a|has_ortholog|ortholog_one2one|true|A1|a1|P07437|multi_gene|{Db}",
            $"P12882|sp_b|resolved|{Db}|B2|sp_a|no_edge_in_shared_tree|-|-|-|-|-|-|-",
            $"P08123|sp_b|resolved|{Db}|B3|sp_a|has_ortholog|ortholog_one2many|true|A5|a5|P35527|resolved|{Db}",
        ];
        // The view columns, for X and for the partner: protein_accession, gene_id, resolution_xref_agreement,
        // resolution_xref_agrees, gene_resolution_definition_id, partner_accession, partner_xref_agreement, partner_xref_agrees.
        string[] views =
        [
            $"P04406|A1|agrees|true|{V2}|P08238|silent|false",
            $"P07437|A1|agrees|true|{V2}|P08238|silent|false",
            $"P07437|A3|disagrees|false|{V2}|-|-|-",
            $"P60709|A2|silent|false|{V2}|-|-|-",
            $"P21266|A4|agrees|true|{V2}|-|-|-",
            $"P13010|A2|agrees|true|{V2}|-|-|-",
            $"P05141|-|-|-|{V2}|-|-|-",
            $"P35527|A5|agrees|true|{V2}|P08123|-|true",
            $"P35527|A5|agrees|true|{V2}|-|-|-",
            $"P08238|B1|silent|false|{V2}|P04406|agrees|true",
            $"P08238|B1|silent|false|{V2}|P07437|agrees|true",
            $"P12882|B2|agrees|true|{V2}|-|-|-",
            $"P08123|B3|-|true|{V1}|P35527|agrees|true",
        ];

        foreach (var built in new[] { file, folder })
        {
            var at = built.Path;
            Assert.Multiple(() =>
            {
                Assert.That(Shape(at), Is.EqualTo(Shape(empty.Path)).And.Not.Empty, $"{at}: the shape never depends on a snapshot");
                Assert.That(Rows(at, $"SELECT table_name, rows, kind FROM catalog_tables WHERE table_name IN ({TableList}) ORDER BY table_name"),
                    Is.EqualTo(new[]
                    {
                        "gene_ortholog_status|9|engine:logs.register_orthology",
                        "gene_orthologs|6|engine:logs.register_orthology",
                        "orthology_genes|9|engine:logs.register_orthology",
                        "orthology_species|2|engine:logs.register_orthology",
                        $"protein_orthologs|{answers.Length}|derived",
                    }), at);
                Assert.That(Scalar(at, $"SELECT count(*) FROM information_schema.tables WHERE table_name IN ({TableList}) AND table_type = 'BASE TABLE'"),
                    Is.EqualTo(5), "copied in both layouts, never views over the snapshot");

                Assert.That(Rows(at, "SELECT species, gene_set_sha256, n_genes, artefact_id FROM orthology_species ORDER BY species"), Is.EqualTo(new[]
                {
                    $"sp_a|{GeneSetA}|5|{snapshot.ArtefactId}", $"sp_b|{GeneSetB}|4|{snapshot.ArtefactId}",
                }));
                Assert.That(Rows(at, "SELECT gene_name FROM orthology_genes WHERE gene_id = 'B4'"), Is.EqualTo(new[] { "-" }), "a NULL name stays NULL");

                // Both orientations of every ortholog row, exactly the snapshot's own; paralogs never.
                Assert.That(Rows(at,
                        "SELECT species, partner_species, homology_id, relationship_type, gene, protein, identity, partner_gene, partner_protein, "
                        + "partner_identity, dn, ds, goc_score, wga_coverage, is_high_confidence FROM gene_orthologs ORDER BY ALL"),
                    Is.EqualTo(expectedOrthologs));
                Assert.That(Rows(at, "SELECT species, gene, partner_species, partner_gene FROM gene_orthologs WHERE homology_id = '1' ORDER BY species"),
                    Is.EqualTo(new[] { "sp_a|A1|sp_b|B1", "sp_b|B1|sp_a|A1" }));
                Assert.That(Scalar(at, "SELECT count(*) FROM gene_orthologs WHERE relationship_type LIKE '%paralog%' OR species = partner_species"), Is.EqualTo(0));
                Assert.That(Rows(at, "SELECT species, gene_id, gene_biotype, target_species, status FROM gene_ortholog_status ORDER BY ALL"),
                    Is.EqualTo(expectedStatus), "row for row what pair_status gives");
                Assert.That(Scalar(at, $"SELECT count(*) FROM gene_orthologs WHERE artefact_id <> '{snapshot.ArtefactId}'"), Is.EqualTo(0));

                // The bridge: every refusal a row, each partner accession its own row, contaminants and unsearched accessions nowhere.
                Assert.That(Rows(at,
                        "SELECT protein_accession, species, resolution_outcome, resolution_source, gene_id, target_species, status, relationship_type, "
                        + "is_high_confidence, partner_gene, partner_gene_name, partner_accession, partner_resolution_outcome, partner_resolution_source "
                        + "FROM protein_orthologs"),
                    Is.EquivalentTo(answers));
                Assert.That(Rows(at,
                        "SELECT protein_accession, gene_id, resolution_xref_agreement, resolution_xref_agrees, gene_resolution_definition_id, "
                        + "partner_accession, partner_xref_agreement, partner_xref_agrees FROM protein_orthologs"),
                    Is.EquivalentTo(views));
                // Each of logs' views is a filter on the columns: the search database's leaves P13010 with no gene.
                Assert.That(Rows(at, $"SELECT DISTINCT protein_accession FROM protein_orthologs WHERE resolution_source = '{Db}' ORDER BY 1"),
                    Does.Not.Contain("P13010"));
                Assert.That(Rows(at, "SELECT DISTINCT protein_accession, gene_id FROM protein_orthologs "
                        + "WHERE resolution_xref_agreement NOT IN ('agrees', 'silent')"),
                    Is.EqualTo(new[] { "P07437|A3" }), "v2's default view leaves out the link Ensembl contradicts");
                Assert.That(Scalar(at, "SELECT count(*) FROM protein_orthologs WHERE status = 'not_in_gene_set' OR status IS NULL"), Is.EqualTo(0));
                Assert.That(Scalar(at, "SELECT count(*) FROM protein_orthologs WHERE 'P00761' IN (protein_accession, partner_accession) "
                    + "OR 'Q9ZZZ9' IN (protein_accession, partner_accession)"), Is.EqualTo(0));
                Assert.That(Scalar(at,
                    "SELECT count(*) FROM (SELECT protein_accession, species FROM protein_orthologs GROUP BY ALL "
                    + "HAVING count(DISTINCT target_species) <> (SELECT count(*) - 1 FROM orthology_species))"), Is.EqualTo(0),
                    "every accession has a row for every other species");
                Assert.That(Scalar(at,
                    "SELECT count(*) FROM protein_orthologs o LEFT JOIN protein_index i ON i.protein_accession = o.partner_accession "
                    + "WHERE o.partner_n_datasets_1pct IS DISTINCT FROM CASE WHEN o.partner_accession IS NOT NULL THEN coalesce(i.n_datasets_1pct, 0) END"),
                    Is.EqualTo(0));
                Assert.That(Rows(at, "SELECT DISTINCT orthology_definition_id, artefact_id FROM protein_orthologs"),
                    Is.EqualTo(new[] { $"logs:DEF-ORTHOLOGY v1|{snapshot.ArtefactId}" }));

                // The checks the build ran, and nothing of the snapshot's macros left behind.
                var name = $"logs.register_orthology/{snapshot.ArtefactId}";
                var consistency = built.Checks.Where(c => c.Kind == "engine-consistency" && c.Name.Contains(snapshot.ArtefactId)).ToList();
                Assert.That(consistency.Select(c => c.Name), Is.EquivalentTo(new[]
                {
                    $"{name} gene sets (gene_resolutions)",
                    $"{name} pair_status sp_a -> sp_b (gene_ortholog_status)",
                    $"{name} pair_status sp_b -> sp_a (gene_ortholog_status)",
                    $"{name} genes sp_a (orthology_genes)",
                    $"{name} genes sp_b (orthology_genes)",
                    $"{name} protein_orthologs (a row per accession and target species)",
                    $"{name} protein_orthologs (no accession both no_gene and with a gene)",
                    $"{name} resolved genes in the snapshot's gene sets (orthology_genes)",
                }));
                Assert.That(consistency.Where(c => !c.Ok), Is.Empty);
                Assert.That(consistency.Single(c => c.Name.Contains("a row per accession")).Expected, Is.EqualTo(10), "ten source accessions, one other species each");
                Assert.That(Scalar(at, "SELECT count(*) FROM catalog_checks WHERE kind = 'engine-consistency' AND ok"), Is.EqualTo(8));
                Assert.That(Rows(at, $"SELECT ok, observed FROM catalog_checks WHERE kind = 'integrity' AND name LIKE '{name} snapshot files%'"),
                    Is.EqualTo(new[] { "true|9" }), "manifest.json and the eight files it lists");
                Assert.That(Scalar(at, "SELECT count(*) FROM duckdb_functions() "
                    + "WHERE function_name IN ('orthologs', 'pair_status', 'species_set3', 'orthology_pair_file')"), Is.EqualTo(0));
                Assert.That(Rows(at, "SELECT catalog_version FROM catalog_meta").Single(), Is.EqualTo("17"));
            });
        }
        Assert.That(file.RowCounts[CatalogBuilder.ProteinOrthologsTable], Is.EqualTo(answers.Length));
        Assert.That(file.Indexes, Is.EqualTo(empty.Indexes), "the orthology tables are indexed whether or not a snapshot is served");
        Assert.That(Scalar(file.Path, "SELECT count(*) FROM duckdb_indexes() WHERE table_name = 'protein_orthologs'"), Is.EqualTo(1));

        // The two worked queries in querying.md run, and answer one row per protein_orthologs row.
        var documented = DocumentedQueries().Select(sql => sql.Replace("'P14136'", "'P35527'")).ToList();
        Assert.That(documented, Has.Count.EqualTo(2));
        foreach (var sql in documented)
        {
            Assert.That(CatalogBuilder.RunQuery(file.Path, sql).Rows, Has.Count.EqualTo(2), sql);
            Assert.That(CatalogBuilder.RunQuery(folder.Path, sql).Rows, Has.Count.EqualTo(2), sql);
        }
    }

    /// <summary>The SQL blocks of <c>docs/data/querying.md</c> that read <c>protein_orthologs</c>.</summary>
    private static List<string> DocumentedQueries()
    {
        var page = File.ReadAllText(Path.Combine(DataPaths.Docs, "querying.md")).Replace("\r\n", "\n");
        return Regex.Matches(page, "```sql\n(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value)
            .Where(sql => sql.Contains("FROM protein_orthologs", StringComparison.Ordinal)).ToList();
    }

    [Test]
    public void ATableThatDisagreesWithTheRegistrationFailsTheBuild()
    {
        ResolveGenes();
        var snapshot = RegisterSnapshot();
        var record = Path.Combine(snapshot.Path, "run.json");
        var json = JsonNode.Parse(File.ReadAllText(record))!;
        var direction = json["engine_summary"]!["pair_status"]!.AsArray().Single(d => (string)d!["from"]! == "sp_a")!;
        direction["status_counts"]!["has_ortholog"] = 3;
        File.WriteAllText(record, json.ToJsonString());

        var output = Path.Combine(_root, "catalog.duckdb");
        Assert.That(() => Build(output), Throws.TypeOf<CatalogException>()
            .With.Message.Contains($"logs.register_orthology/{snapshot.ArtefactId} pair_status sp_a -> sp_b (gene_ortholog_status): 5 vs 6")
            .And.Message.Contains("has_ortholog 3"));
        Assert.That(File.Exists(output), Is.False, "nothing was written");
    }

    /// <summary>Makes the registration record the snapshot as it now is on disk: a new manifest, and its sha256 in
    /// <c>run.json</c>, as if this snapshot had been released and registered.</summary>
    private static void Reregister(ArtefactRef snapshot)
    {
        var directory = Path.Combine(snapshot.Path, "snapshot");
        OrthologySnapshots.WriteManifest(directory);
        var record = Path.Combine(snapshot.Path, "run.json");
        var json = JsonNode.Parse(File.ReadAllText(record))!;
        json["inputs"]!["snapshot_manifest"] = BundleWriter.Sha256File(Path.Combine(directory, "manifest.json"));
        File.WriteAllText(record, json.ToJsonString());
    }

    [Test]
    public void AViewsSqlThatWouldWriteIntoTheCatalogIsRefused()
    {
        ResolveGenes();
        var snapshot = RegisterSnapshot();
        File.AppendAllText(Path.Combine(snapshot.Path, "snapshot", "views.sql"), "\nCREATE TABLE leaked AS SELECT 1 AS x;\n");
        Reregister(snapshot);  // past the sha256 check, to the one that guards the catalog
        var output = Path.Combine(_root, "catalog.duckdb");
        Assert.That(() => Build(output), Throws.TypeOf<CatalogException>()
            .With.Message.Contains("did more than define its macros").And.Message.Contains("1 other object(s)"));
        Assert.That(File.Exists(output), Is.False, "nothing was written");
    }

    /// <summary>logs 023: the build reads the snapshot only when its files are the registered ones. A changed
    /// <c>relationship_type</c> or partner gene would move no count, so the counts cannot stand in for this.</summary>
    [TestCase("file", "pairs/sp_a__sp_b.parquet hashes to")]
    [TestCase("manifest", "manifest.json hashes to")]
    public void ASnapshotChangedSinceItsRegistrationIsNotRead(string changed, string message)
    {
        ResolveGenes();
        var snapshot = RegisterSnapshot();
        var directory = Path.Combine(snapshot.Path, "snapshot");
        var pairs = Path.Combine(directory, "pairs", "sp_a__sp_b.parquet");
        var stale = Path.Combine(_root, "stale.parquet");
        File.Copy(pairs, stale);
        using (var con = new DuckDBConnection("DataSource=:memory:"))
        {
            // The same rows, one relationship retyped: every count stays as registered.
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"COPY (SELECT * REPLACE (CASE WHEN homology_id = '1' THEN 'ortholog_one2many' ELSE relationship_type END AS relationship_type) "
                + $"FROM read_parquet('{stale.Replace('\\', '/')}')) TO '{pairs.Replace('\\', '/')}'";
            cmd.ExecuteNonQuery();
        }
        if (changed == "manifest") OrthologySnapshots.WriteManifest(directory);  // a manifest that agrees with the files, unregistered
        var output = Path.Combine(_root, "catalog.duckdb");
        Assert.That(() => Build(output), Throws.TypeOf<CatalogException>()
            .With.Message.Contains($"logs.register_orthology/{snapshot.ArtefactId}: ").And.Message.Contains(message)
            .And.Message.Contains("is not the one registered"));
        Assert.That(File.Exists(output), Is.False, "nothing was written");
    }

    /// <summary>logs 023: a resolved gene the snapshot's gene set does not hold is a failed build, not a status.</summary>
    [Test]
    public void AGeneOutsideTheSnapshotsGeneSetFailsTheBuild()
    {
        ResolveGenes(Resolutions.Append(new Link("P13533", "resolved", "A9", GeneSetA, Db, "agrees", true)));
        var snapshot = RegisterSnapshot();
        var output = Path.Combine(_root, "catalog.duckdb");
        Assert.That(() => Build(output), Throws.TypeOf<CatalogException>()
            .With.Message.Contains($"logs.register_orthology/{snapshot.ArtefactId} resolved genes in the snapshot's gene sets (orthology_genes): 1 vs 0")
            .And.Message.Contains("P13533 -> A9 (sp_a)"));
        Assert.That(File.Exists(output), Is.False, "nothing was written");
    }
}
