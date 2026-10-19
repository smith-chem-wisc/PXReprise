using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;
using DataRepo.Mcp;
using DuckDB.NET.Data;

namespace DataRepo.Tests;

/// <summary>D48 step 1: a catalog FOLDER holds versioned catalogs whose bundle and engine tables are views over the store's
/// Parquet, with a <c>latest.json</c> pointer, and answers exactly as a one-file catalog of the same bundles does.</summary>
/// <remarks>Runs on the current rules: a Python 0.32.0 parity build writes one file, which <c>CatalogTests</c> holds.</remarks>
public class FolderCatalogTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string Folder => Path.Combine(_root, "catalogs");

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-folder-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        DeliverDesign();
    }

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>A small aging delivery, so the study tables are filled in both layouts.</summary>
    private void DeliverDesign()
    {
        var delivery = Path.Combine(_root, "delivery");
        Directory.CreateDirectory(delivery);
        File.WriteAllText(Path.Combine(delivery, "design.tsv"),
            "run_id\tindividual\tsource_reference\tcuration_version\n"
            + "PXD999999:QE-002106_GM1_a\tmouse 1\tPMID 1\taging-curation/1\n"
            + "PXD999999:QE-002107_GM1_b\tmouse 2\tPMID 1\taging-curation/1\n");
        File.WriteAllText(Path.Combine(delivery, "study.yaml"),
            "study_manifest_version: 1\nlayer: aging\nstore: ../store\ntables:\n  curated_run_design: design.tsv\n");
        DataRepo.Study.StudyWriter.WriteStudyBundle(DataRepo.Study.StudyWriter.LoadStudyManifest(Path.Combine(delivery, "study.yaml")));
    }

    private CatalogResult Build(string output, int? keep = null)
    {
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var study = CatalogBuilder.SelectStudyBundles(Store, latest: ["aging"]);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance, studyBundles: study,
            artefacts: artefacts, engineChecks: checks, manifest: manifest, keep: keep);
    }

    private static List<object?[]> Rows(DuckDBConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Every table and view, its columns and types, and its rows rendered by DuckDB itself, so a value is
    /// compared as the engine prints it.</summary>
    private static Dictionary<string, (string Kind, List<string> Columns, List<string> Rows)> Dump(string catalog, ISet<string> skip)
    {
        using var con = CatalogBuilder.Open(catalog, readOnly: true);
        var dump = new Dictionary<string, (string, List<string>, List<string>)>(StringComparer.Ordinal);
        foreach (var t in Rows(con, "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = 'main' ORDER BY 1"))
        {
            var name = (string)t[0]!;
            if (skip.Contains(name)) continue;
            var columns = Rows(con, $"SELECT column_name || ' ' || data_type FROM information_schema.columns WHERE table_name = '{name}' ORDER BY ordinal_position")
                .Select(r => (string)r[0]!).ToList();
            var rows = Rows(con, $"SELECT to_json(t)::VARCHAR FROM \"{name}\" t ORDER BY ALL").Select(r => (string)r[0]!).ToList();
            dump[name] = ((string)t[1]!, columns, rows);
        }
        return dump;
    }

    [Test]
    public void AFolderCatalogHoldsWhatAOneFileCatalogHolds()
    {
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Folder);
        Assert.Multiple(() =>
        {
            Assert.That(file.Layout, Is.EqualTo(CatalogLayout.File));
            Assert.That(folder.Layout, Is.EqualTo(CatalogLayout.Folder));
            Assert.That(folder.ReleaseId, Is.EqualTo(file.ReleaseId), "one release, two catalogs of it");
            Assert.That(folder.CatalogId, Is.Not.EqualTo(file.CatalogId));
            Assert.That(Path.GetFileName(folder.Path), Is.EqualTo($"catalog-{folder.CatalogId}.duckdb"));
            Assert.That(CatalogPointer.Resolve(Folder), Is.EqualTo(Path.GetFullPath(folder.Path)));
            Assert.That(folder.FailedChecks, Is.Empty);
        });

        // The catalog's account of itself differs by design (ids, layout, which checks ran); everything else must not.
        var skip = new HashSet<string>(StringComparer.Ordinal) { "catalog_meta", "catalog_checks" };
        var a = Dump(file.Path, skip);
        var b = Dump(folder.Path, skip);
        Assert.That(b.Keys, Is.EqualTo(a.Keys), "the same tables and views");
        Assert.That(a.Keys, Has.Count.GreaterThan(50), "every table and view is compared");
        Assert.That(a.Keys, Has.None.StartsWith("psms"), "PSMs stay in the bundles in either layout");
        Assert.That(a["dataset_overview"].Rows.Single(), Does.Contain("\"n_psms_1pct\":58").And.Contain("\"n_psms_all\":60"),
            "their counts are the bundle's own");
        Assert.That(a["quant_values"].Rows, Is.Not.Empty);
        foreach (var name in a.Keys)
        {
            Assert.That(b[name].Columns, Is.EqualTo(a[name].Columns), $"{name}: columns and types");
            Assert.That(b[name].Rows, Is.EqualTo(a[name].Rows), $"{name}: rows");
        }
        Assert.Multiple(() =>
        {
            foreach (var view in new[] { "peptidoforms", "quant_values", "proteins", "runs", "gene_resolutions", "protein_localizations" })
                Assert.That(b[view].Kind, Is.EqualTo("VIEW"), $"{view} reads the store");
            foreach (var table in new[] { "samples", "dataset_overview", "protein_index", "curated_run_design" })
                Assert.That(b[table].Kind, Is.EqualTo("BASE TABLE"), $"{table} is materialised");
            Assert.That(new FileInfo(folder.Path).Length, Is.LessThan(new FileInfo(file.Path).Length));
        });

        using var con = CatalogBuilder.Open(folder.Path, readOnly: true);
        var meta = Rows(con, "SELECT catalog_version, release_id, layout, data_root FROM catalog_meta").Single();
        Assert.That(meta, Is.EqualTo(new object?[] { "14", folder.ReleaseId, "views", CatalogBuilder.AsPosix(Path.GetFullPath(Store)) + "/" }));
        Assert.That(Rows(con, "SELECT ok FROM catalog_checks WHERE kind = 'integrity'").Single()[0], Is.EqualTo(true),
            "the per-bundle integrity checks are not re-run, and the catalog says so");
    }

    [Test]
    public void ThePointerNamesTheCurrentCatalogAndKeepPrunesTheRest()
    {
        var first = Build(Folder);
        var unchanged = Build(Folder);
        Assert.That(unchanged.Skipped, "an unchanged build writes nothing new");
        Assert.That(unchanged.Pointer, Is.Not.Null);

        File.WriteAllText(ManifestPath, File.ReadAllText(ManifestPath).Replace(
            "    flags: [low_id_rate, no_design_file]\n", "    flags: [low_id_rate, no_design_file]\n    notes: a note\n", StringComparison.Ordinal));
        var second = Build(Folder);
        Assert.Multiple(() =>
        {
            Assert.That(second.CatalogId, Is.Not.EqualTo(first.CatalogId));
            Assert.That(second.ReleaseId, Is.EqualTo(first.ReleaseId), "a note is not data");
            Assert.That(CatalogPointer.Resolve(Folder), Is.EqualTo(Path.GetFullPath(second.Path)));
            Assert.That(File.Exists(first.Path), "the older catalog stays until --keep prunes it");
        });

        var kept = Build(Folder, keep: 1);
        Assert.That(kept.Skipped);
        Assert.That(kept.Pruned, Is.EqualTo(new[] { BundleWriter.PathText(Path.GetFullPath(first.Path)) }));
        Assert.That(File.Exists(first.Path), Is.False);
        Assert.That(Directory.EnumerateFiles(Folder, "*.building*"), Is.Empty);
    }

    [Test]
    public void AServerOnAFolderKeepsItsCatalogAndSaysWhenANewerOneIsPublished()
    {
        Build(Folder);
        using var server = new CatalogServer(Folder);
        Assert.That(server.CatalogFileChanged, Is.Null);
        Assert.That(server.Box.Query("SELECT count(*) FROM peptidoforms").Rows[0][0], Is.GreaterThanOrEqualTo(2L), "the views read the store");

        File.WriteAllText(ManifestPath, File.ReadAllText(ManifestPath).Replace(
            "    flags: [low_id_rate, no_design_file]\n", "    flags: [low_id_rate, no_design_file]\n    notes: a note\n", StringComparison.Ordinal));
        var newer = Build(Folder);
        Assert.That(server.CatalogFileChanged, Does.StartWith($"A newer catalog, catalog-{newer.CatalogId}.duckdb, has been published in "));
        Assert.That(server.Box.Query("SELECT count(*) FROM peptidoforms").Rows[0][0], Is.GreaterThanOrEqualTo(2L), "and it still answers");
    }

    /// <summary>D48 step 2b: a folder build derives each deposit's rows once and later builds reuse them; a deposit whose
    /// derived files no longer hold what their record says is derived again.</summary>
    [Test]
    public void EachDepositIsDerivedOnceAndReused()
    {
        var first = Build(Folder);
        Assert.That((first.DerivedComputed, first.DerivedReused), Is.EqualTo((1, 0)));
        var root = Path.Combine(Folder, "derived", CatalogBuilder.DerivationId, "PXD999999");
        var deposit = Directory.GetDirectories(root).Single();
        Assert.That(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(Path.GetFileName), Is.EquivalentTo(new[]
        {
            "dataset_overview.parquet", "protein_datasets.parquet", "protein_partials.parquet", "search_modifications_placed.parquet",
            "peptide_partials.parquet", CatalogBuilder.DerivedRecord,
        }), "nothing staged is left behind");

        var original = File.ReadAllText(ManifestPath);
        void Note(string text) => File.WriteAllText(ManifestPath, original.Replace(
            "    flags: [low_id_rate, no_design_file]\n", $"    flags: [low_id_rate, no_design_file]\n    notes: {text}\n", StringComparison.Ordinal));
        Note("a note");
        var second = Build(Folder);
        Assert.That(second.CatalogId, Is.Not.EqualTo(first.CatalogId));
        Assert.That((second.DerivedComputed, second.DerivedReused), Is.EqualTo((0, 1)), "a note is not a deposit's rows");

        var file = Path.Combine(deposit, "protein_datasets.parquet");
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..64]);
        Note("another note");
        var third = Build(Folder);
        Assert.That((third.DerivedComputed, third.DerivedReused), Is.EqualTo((1, 0)), "a cut file is derived again, not served");
        Assert.That(third.FailedChecks, Is.Empty);
        Assert.That(CatalogBuilder.RunQuery(third.Path, "SELECT count(*) FROM protein_datasets").Rows.Single()[0],
            Is.EqualTo(CatalogBuilder.RunQuery(first.Path, "SELECT count(*) FROM protein_datasets").Rows.Single()[0]));
    }

    /// <summary>D48 step 2: an agent that reaches for PSMs is told where they are and where their counts are, whichever
    /// tool it reaches with.</summary>
    [Test]
    public void AnAgentThatAsksForPsmsIsToldWhereTheyAre()
    {
        Build(Folder);
        using var server = new CatalogServer(Folder);
        Assert.Multiple(() =>
        {
            foreach (var target in new[] { "psms", "Psms", "psms_1pct" })
                Assert.That(() => server.Describe(target), Throws.InstanceOf<ToolException>().With.Message.StartsWith("PSMs are not in this catalog"), target);
            Assert.That(() => server.Sql("SELECT count(*) FROM psms_1pct"),
                Throws.InstanceOf<CatalogException>().With.Message.Contain("psms_1pct").And.Message.Contain("PSMs are not in this catalog"));
            Assert.That(server.Sql("SELECT n_psms_1pct, n_psms_all FROM dataset_overview")["rows"],
                Is.EqualTo(new List<object?> { new List<object?> { 58L, 60L } }));
        });
    }

    /// <summary>D14's class, for a folder catalog: the views read the store, and nothing else is read.</summary>
    [Test]
    public void TheSandboxReadsTheStoreAndNothingElse()
    {
        var built = Build(Folder);
        var outside = Path.Combine(_root, "secret.csv");
        File.WriteAllText(outside, "a,b\n1,2\n");
        using var box = new Sandbox(built.Path);
        Assert.That(box.DataRoot, Is.EqualTo(CatalogBuilder.AsPosix(Path.GetFullPath(Store)) + "/"));
        Assert.That(box.Query("SELECT count(*) FROM quant_values").Rows[0][0], Is.GreaterThan(0L));
        Assert.That(box.DerivedRoot, Does.EndWith($"/derived/{CatalogBuilder.DerivationId}/"));
        Assert.That(box.Query("SELECT count(*) FROM protein_datasets").Rows[0][0], Is.GreaterThan(0L), "the derived rows read too");
        foreach (var path in new[] { CatalogBuilder.AsPosix(outside), CatalogBuilder.AsPosix(Path.GetFullPath(Store)) + "/../secret.csv" })
            Assert.That(() => box.Query($"SELECT * FROM read_csv_auto('{path}')"), Throws.InstanceOf<DataRepoException>(), path);
        Assert.That(() => box.Query("SET enable_external_access = true"), Throws.InstanceOf<DataRepoException>());
    }

    /// <summary>The site and <c>query</c> take the folder too, and read the catalog its pointer names.</summary>
    [Test]
    public void TheSiteAndQueryReadTheFolder()
    {
        var built = Build(Folder);
        var site = DataRepo.Site.SiteGenerator.BuildSite(Folder, Path.Combine(_root, "site"));
        Assert.That(site.CatalogId, Is.EqualTo(built.CatalogId));
        Assert.That(CatalogBuilder.RunQuery(Path.Combine(Folder, CatalogPointer.FileName), "SELECT n_psms_all FROM dataset_overview").Rows.Single()[0], Is.EqualTo(60L));
    }

    [Test]
    public void AParityBuildWritesOneFileOnly()
    {
        using var _ = SchemaContract.Python0320();
        var manifest = Manifest.Load(ManifestPath);
        var e = Assert.Throws<CatalogException>(() => CatalogBuilder.BuildCatalog(
            CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store), Folder));
        Assert.That(e!.Message, Does.Contain("writes one .duckdb file"));
    }
}
