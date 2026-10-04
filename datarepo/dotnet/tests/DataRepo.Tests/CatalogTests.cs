using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;
using DuckDB.NET.Data;

namespace DataRepo.Tests;

/// <summary>The C# catalog builder against catalogs the Python 0.32.0 built from the same bundles.</summary>
/// <remarks>Rows are compared as DuckDB's own <c>to_json</c> renders them, with the same SQL on both sides
/// (<c>Fixtures/catalog/PROVENANCE.md</c>), so a float or a list is printed by the engine, not by either
/// language. Both sides run DuckDB 1.5.5.</remarks>
public class CatalogTests
{
    private static readonly string FixtureDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "catalog");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    // --- the dump: mirrors dump.py in PROVENANCE.md word for word ----------------------------------------

    private static readonly Dictionary<string, string[]> Volatile = new()
    {
        ["catalog_meta"] = ["catalog_id", "builder_version", "built_utc", "notes"],
        ["catalog_bundles"] = ["path"],
        ["catalog_study_bundles"] = ["path"],
        ["catalog_engine_artefacts"] = ["path"],
    };

    private static readonly Dictionary<string, string[]> PlatformColumns = new()
    {
        ["dataset_databases"] = ["database"],
        ["protein_genes"] = ["database"],
    };

    /// <summary>A view column DuckDB builds as <c>list(DISTINCT ...)</c> with no ORDER BY: its order can change
    /// between two evaluations of the same view, in either language, so it is sorted before comparing.</summary>
    private static readonly Dictionary<string, string[]> UnorderedLists = new()
    {
        ["ptm_sites_by_chemistry"] = ["modification_names"],
    };

    private static readonly string[] Ordered =
        ["catalog_bundles", "catalog_study_bundles", "catalog_engine_artefacts", "catalog_tables", "catalog_checks"];

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static List<object?[]> Rows(DuckDBConnection con, string sql, params object[] args)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var a in args) cmd.Parameters.Add(new DuckDBParameter(a));
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

    private sealed record TableDump(
        string Kind, List<List<string>> Columns, long Count, List<string> Rows, string? RowsHashSum, Dictionary<string, List<string>> Platform);

    private sealed record CatalogDump(
        SortedDictionary<string, TableDump> Tables, SortedDictionary<string, string> ViewsSql, List<List<string>> Indexes);

    /// <param name="fullLimit">A table with more rows is summarised as its count and the sum of DuckDB's
    /// <c>hash()</c> of each row's JSON, as <c>expected_real.json</c> was.</param>
    private static CatalogDump Dump(string path, long? fullLimit = null)
    {
        using var con = new DuckDBConnection($"Data Source={path};ACCESS_MODE=READ_ONLY");
        con.Open();
        var tables = new SortedDictionary<string, TableDump>(StringComparer.Ordinal);
        foreach (var obj in Rows(con,
            "SELECT table_name, table_type FROM information_schema.tables "
            + "WHERE table_schema = 'main' ORDER BY table_name"))
        {
            var name = (string)obj[0]!;
            var columns = Rows(con,
                "SELECT column_name, data_type FROM information_schema.columns "
                + "WHERE table_schema = 'main' AND table_name = ? ORDER BY ordinal_position", name)
                .Select(r => new List<string> { (string)r[0]!, (string)r[1]! }).ToList();
            var drop = Volatile.GetValueOrDefault(name, []).Concat(PlatformColumns.GetValueOrDefault(name, [])).ToArray();
            var star = drop.Length > 0 ? $"* EXCLUDE ({string.Join(", ", drop.Select(Q))})" : "*";
            var lists = UnorderedLists.GetValueOrDefault(name, []);
            if (lists.Length > 0) star += $" REPLACE ({string.Join(", ", lists.Select(c => $"list_sort({Q(c)}) AS {Q(c)}"))})";
            var body = $"SELECT {star} FROM {Q(name)}";
            if (Ordered.Contains(name)) body += " ORDER BY rowid";
            var count = Convert.ToInt64(Rows(con, $"SELECT count(*) FROM {Q(name)}")[0][0]);
            string? summary = null;
            List<string> rows;
            if (fullLimit is not null && count > fullLimit)
            {
                rows = [];
                summary = Convert.ToString(Rows(con, $"SELECT sum(hash(to_json(t)::VARCHAR)::HUGEINT)::VARCHAR FROM ({body}) t")[0][0]);
            }
            else
            {
                rows = Rows(con, $"SELECT to_json(t)::VARCHAR FROM ({body}) t").Select(r => (string)r[0]!).ToList();
                if (!Ordered.Contains(name)) rows.Sort(StringComparer.Ordinal);
            }
            var platform = new Dictionary<string, List<string>>();
            foreach (var c in PlatformColumns.GetValueOrDefault(name, []))
                platform[c] = summary is null
                    ? Rows(con, $"SELECT {Q(c)} FROM {Q(name)}").Select(r => (string)r[0]!).Order(StringComparer.Ordinal).ToList()
                    : [Convert.ToString(Rows(con, $"SELECT sum(hash({Q(c)})::HUGEINT)::VARCHAR FROM {Q(name)}")[0][0])!];
            tables[name] = new TableDump((string)obj[1]!, columns, count, rows, summary, platform);
        }
        var views = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Rows(con, "SELECT view_name, sql FROM duckdb_views() WHERE NOT internal ORDER BY view_name"))
            views[(string)r[0]!] = (string)r[1]!;
        var indexes = Rows(con, "SELECT index_name, table_name, sql FROM duckdb_indexes() ORDER BY index_name")
            .Select(r => r.Select(v => (string)v!).ToList()).ToList();
        return new CatalogDump(tables, views, indexes);
    }

    // --- the fixture build -------------------------------------------------------------------------------

    private static string NewScratch(string what) =>
        Path.Combine(Path.GetTempPath(), $"datarepo-catalog-{what}-" + Guid.NewGuid().ToString("N"));

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>The scratch store the Python fixture was built from: PXD999999, a study delivery and a logs
    /// artefact.</summary>
    private static string FixtureStore()
    {
        var store = NewScratch("store");
        CopyTree(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999"), Path.Combine(store, "PXD999999"));
        CopyTree(Path.Combine(FixtureDir, "store", "_study"), Path.Combine(store, "_study"));
        CopyTree(Path.Combine(FixtureDir, "store", "_engine"), Path.Combine(store, "_engine"));
        return store;
    }

    private static Manifest FixtureManifest() => Manifest.Load(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"));

    private static CatalogResult BuildFixture(string store, string output, bool overwrite = false)
    {
        var manifest = FixtureManifest();
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store);
        var study = CatalogBuilder.SelectStudyBundles(store, latest: ["aging"]);
        var (artefacts, engineChecks) = CatalogBuilder.SelectArtefacts(store, bundles);
        var notes = new Dictionary<string, object?>
        {
            ["manifest"] = manifest.Path,
            ["study"] = study.ToDictionary(r => r.Layer, r => (object?)r.BundleId),
        };
        return CatalogBuilder.BuildCatalog(bundles, output, overwrite, manifest.Instance, notes, study, artefacts, engineChecks);
    }

    private static JsonElement Expected()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "expected_fixture.json")));
        return doc.RootElement.Clone();
    }

    private static void AssertSameAsPython(CatalogDump actual, JsonElement expected, bool comparePlatform)
    {
        var tables = expected.GetProperty("tables");
        var names = tables.EnumerateObject().Select(p => p.Name).ToList();
        Assert.That(actual.Tables.Keys, Is.EqualTo(names), "the catalog's tables and views");
        foreach (var name in names)
        {
            var e = tables.GetProperty(name);
            var a = actual.Tables[name];
            Assert.That(a.Kind, Is.EqualTo(e.GetProperty("kind").GetString()), $"{name}: kind");
            var columns = e.GetProperty("columns").EnumerateArray().Select(c => c.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
            Assert.That(a.Columns, Is.EqualTo(columns), $"{name}: columns and their DuckDB types");
            Assert.That(a.Count, Is.EqualTo(e.GetProperty("count").GetInt64()), $"{name}: row count");
            var rows = e.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToList();
            Assert.That(a.Rows, Is.EqualTo(rows), $"{name}: rows");
            var hash = e.GetProperty("rows_hash_sum");
            Assert.That(a.RowsHashSum, Is.EqualTo(hash.ValueKind == JsonValueKind.Null ? null : hash.GetString()), $"{name}: rows (hash of each row's JSON, summed)");
            if (comparePlatform)
                foreach (var p in e.GetProperty("platform").EnumerateObject())
                    Assert.That(a.Platform[p.Name], Is.EqualTo(p.Value.EnumerateArray().Select(x => x.GetString()!).ToList()), $"{name}.{p.Name}");
        }
        var views = expected.GetProperty("views_sql").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.That(actual.ViewsSql, Is.EqualTo(views), "view definitions");
        var indexes = expected.GetProperty("indexes").EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
        Assert.That(actual.Indexes, Is.EqualTo(indexes), "indexes");
    }

    [Test]
    public void TheFixtureCatalogHoldsThePythonRows()
    {
        var store = FixtureStore();
        try
        {
            var output = Path.Combine(store, "..", Path.GetFileName(store) + "-cs.duckdb");
            var result = BuildFixture(store, output);
            var expected = Expected();
            var about = expected.GetProperty("_about");
            Assert.That(result.Skipped, Is.False);
            Assert.That(result.Checks, Has.Count.EqualTo(expected.GetProperty("tables").GetProperty("catalog_checks").GetProperty("rows").GetArrayLength()));
            Assert.That(result.FailedChecks, Is.Empty);
            Assert.That(result.Indexes, Is.EqualTo(about.GetProperty("indexes").GetInt32()));
            Assert.That(result.StudyBundles.Single().BundleId, Is.EqualTo(about.GetProperty("study_bundle").GetString()));
            Assert.That(result.Artefacts.Single().ArtefactId, Is.EqualTo(about.GetProperty("artefact").GetString()));
            // `database` is `Path(p).name` of a Windows path: the Python's answer depends on its platform too.
            AssertSameAsPython(Dump(output), expected, comparePlatform: OperatingSystem.IsWindows());

            var meta = CatalogBuilder.DescribeCatalog(output)["meta"] as Dictionary<string, object?>;
            Assert.That(meta!["catalog_id"], Is.EqualTo(result.CatalogId));
            Assert.That(meta["builder_version"], Is.EqualTo(CatalogBuilder.PackageVersion));
            Assert.That((string)meta["built_utc"]!, Does.Match(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$"));
            Assert.That(meta["notes"], Is.EqualTo(
                $"{{\"manifest\": {PyFormat.Json(FixtureManifest().Path, ensureAscii: true)}, \"study\": {{\"aging\": \"{result.StudyBundles[0].BundleId}\"}}}}"));
            var paths = CatalogBuilder.RunQuery(output, "SELECT path FROM catalog_bundles").Rows;
            Assert.That((string)paths.Single()[0]!, Does.EndWith("/PXD999999/aeb10630abbcaf72"));
        }
        finally
        {
            Directory.Delete(store, recursive: true);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(store)!, Path.GetFileName(store) + "-cs.duckdb*")) File.Delete(f);
        }
    }

    /// <summary>G74: <c>samples.&lt;column&gt;_name</c> from <c>sample_characteristics</c>, on the fixture bundle
    /// with its eight cells rewritten to names (<c>Fixtures/catalog/g74/</c>), against the Python's samples.</summary>
    [Test]
    public void SampleNamesComeFromTheSdrfCellsAsPythonReadsThem()
    {
        var store = NewScratch("g74");
        var output = store + ".duckdb";
        try
        {
            CopyTree(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999"), Path.Combine(store, "PXD999999"));
            File.Copy(Path.Combine(FixtureDir, "g74", "sample_characteristics.parquet"),
                Path.Combine(store, "PXD999999", "aeb10630abbcaf72", "sample_characteristics.parquet"), overwrite: true);
            var manifest = FixtureManifest();
            var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store);
            CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance,
                notes: new Dictionary<string, object?> { ["manifest"] = manifest.Path });
            var actual = Dump(output);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "expected_g74.json")));
            foreach (var table in doc.RootElement.GetProperty("tables").EnumerateObject())
            {
                var rows = table.Value.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToList();
                Assert.That(actual.Tables[table.Name].Rows, Is.EqualTo(rows), table.Name);
            }
            Assert.That(actual.Tables["samples"].Rows.Any(r => r.Contains("\"sex_name\":\"female; male\"")), "two names under one header are both kept");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + "*")) File.Delete(f);
        }
    }

    [Test]
    public void RebuildingFromTheSameBundlesIsANoOpAndOverwriteRebuilds()
    {
        var store = FixtureStore();
        var output = Path.Combine(store, "catalog.duckdb");
        try
        {
            var first = BuildFixture(store, output);
            var second = BuildFixture(store, output);
            Assert.That(second.Skipped, Is.True);
            Assert.That(second.CatalogId, Is.EqualTo(first.CatalogId));
            Assert.That(CatalogBuilder.ReadCatalogId(output), Is.EqualTo(first.CatalogId));
            var third = BuildFixture(store, output, overwrite: true);
            Assert.That(third.Skipped, Is.False);
            Assert.That(Directory.EnumerateFiles(store, "*.building*"), Is.Empty, "no staging file is left behind");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Test]
    public void TheCatalogIdHashesWhatPythonHashesWithThisBuildsVersion()
    {
        var store = FixtureStore();
        try
        {
            var bundles = CatalogBuilder.SelectBundles(FixtureManifest(), ["PXD999999"], store);
            var study = CatalogBuilder.SelectStudyBundles(store, latest: ["aging"]);
            var (artefacts, _) = CatalogBuilder.SelectArtefacts(store, bundles);
            // The Python's hash input, with `datarepo/0.32.0` replaced by this build's version.
            var text = $"datarepo/{CatalogBuilder.PackageVersion}\ncatalog/8\nschema/0.0.13\nstudy/aging/0.5.0\n"
                + "PXD999999\taeb10630abbcaf72\n"
                + $"study-bundle/aging\t{study[0].BundleId}\n"
                + $"engine/logs.resolve_genes\t{artefacts[0].ArtefactId}\n";
            var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
            Assert.That(CatalogBuilder.CatalogId(bundles, study, artefacts), Is.EqualTo(expected));
            var python = text.Replace($"datarepo/{CatalogBuilder.PackageVersion}\n", "datarepo/0.32.0\n");
            var pythonId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(python)))[..16];
            Assert.That(pythonId, Is.EqualTo(Expected().GetProperty("_about").GetProperty("catalog_id_python").GetString()),
                "the same rule, fed Python's version, gives Python's id");
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Test]
    public void ATruncatedBundleIsRefusedAndTheOldCatalogKeepsServing()
    {
        var store = FixtureStore();
        var output = Path.Combine(store, "catalog.duckdb");
        try
        {
            var first = BuildFixture(store, output);
            var manifestPath = Path.Combine(store, "PXD999999", "aeb10630abbcaf72", "bundle.json");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"psms\": 60", "\"psms\": 61"));
            var e = Assert.Throws<CatalogException>(() => BuildFixture(store, output, overwrite: true));
            Assert.That(e!.Message, Does.StartWith("the bundles do not hold together as one catalog, so nothing was written:\n  - PXD999999/psms: 60 vs 61 (bundle aeb10630abbcaf72)"));
            Assert.That(CatalogBuilder.ReadCatalogId(output), Is.EqualTo(first.CatalogId));
            Assert.That(Directory.EnumerateFiles(store, "*.building*"), Is.Empty);
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Test]
    public void SelectionRefusesToGuess()
    {
        var store = FixtureStore();
        try
        {
            var manifest = FixtureManifest();
            var original = Path.Combine(store, "PXD999999", "aeb10630abbcaf72");
            CopyTree(original, Path.Combine(store, "PXD999999", "ffff000000000000"));
            var copy = Path.Combine(store, "PXD999999", "ffff000000000000", "bundle.json");
            File.WriteAllText(copy, File.ReadAllText(copy).Replace("aeb10630abbcaf72", "ffff000000000000"));
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(Path.Combine(original, "bundle.json")).AddSeconds(5));

            var e = Assert.Throws<CatalogException>(() => CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store));
            Assert.That(e!.Message, Does.StartWith("PXD999999 has 2 bundles and nothing says which one this catalog is of:\n    aeb10630abbcaf72  written "));
            Assert.That(CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store, latest: true).Single().BundleId, Is.EqualTo("ffff000000000000"),
                "same written_utc, so the newer manifest mtime decides");
            Assert.That(CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store, new Dictionary<string, string> { ["PXD999999"] = "aeb" }).Single().BundleId,
                Is.EqualTo("aeb10630abbcaf72"));
            e = Assert.Throws<CatalogException>(() => CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store, new Dictionary<string, string> { ["PXD999999"] = "0" }));
            Assert.That(e!.Message, Is.EqualTo("PXD999999: --bundle 0 matches 0 of the bundles on disk (aeb10630abbcaf72, ffff000000000000)"));
            e = Assert.Throws<CatalogException>(() => CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store, latest: true, release: "r1"));
            Assert.That(e!.Message, Does.StartWith("--release r1 and --latest are mutually exclusive."));
            e = Assert.Throws<CatalogException>(() => CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store, release: "r1"));
            Assert.That(e!.Message, Does.StartWith("--release r1 needs every dataset pinned, and PXD999999 is not."));

            var excluded = manifest.Datasets.Values.FirstOrDefault(d => !d.Ingestable);
            if (excluded is not null)
                Assert.Throws<DatasetExcludedException>(() => CatalogBuilder.SelectBundles(manifest, [excluded.Accession], store));

            e = Assert.Throws<CatalogException>(() => CatalogBuilder.SelectStudyBundles(store, new Dictionary<string, string> { ["aging"] = "x" }, ["aging"]));
            Assert.That(e!.Message, Does.StartWith("study layer aging is both pinned with --study and asked for with --study-latest."));
            Assert.That(CatalogBuilder.SelectStudyBundles(store), Is.Empty, "study bundles are opt-in");
            Assert.That(CatalogBuilder.AvailableStudyLayers(store).Keys, Is.EqualTo(new[] { "aging" }));
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Test]
    public void AQueryCannotWriteAndTheLimitCapsIt()
    {
        var store = FixtureStore();
        var output = Path.Combine(store, "catalog.duckdb");
        try
        {
            BuildFixture(store, output);
            Assert.Throws<CatalogException>(() => CatalogBuilder.RunQuery(output, "DELETE FROM psms"));
            var (columns, rows) = CatalogBuilder.RunQuery(output, "SELECT dataset_id, n_psms_all, organisms FROM dataset_overview;", limit: 1);
            Assert.That(columns, Is.EqualTo(new[] { "dataset_id", "n_psms_all", "organisms" }));
            Assert.That(rows.Single(), Is.EqualTo(new object?[] { "PXD999999", 60L, new List<object?> { "NCBITaxon:9606" } }));
            Assert.That(CatalogBuilder.FormatRows(columns, rows, "tsv"), Is.EqualTo("dataset_id\tn_psms_all\torganisms\nPXD999999\t60\t['NCBITaxon:9606']"));
            Assert.That(CatalogBuilder.FormatRows(columns, rows, "json"), Is.EqualTo("{\"dataset_id\": \"PXD999999\", \"n_psms_all\": 60, \"organisms\": [\"NCBITaxon:9606\"]}"));
            Assert.That(CatalogBuilder.FormatRows(columns, rows), Is.EqualTo(
                "dataset_id  n_psms_all  organisms         \n"
                + "----------  ----------  ------------------\n"
                + "PXD999999   60          ['NCBITaxon:9606']"));
            Assert.Throws<CatalogException>(() => CatalogBuilder.RunQuery(Path.Combine(store, "missing.duckdb"), "SELECT 1"));
            var described = CatalogBuilder.DescribeCatalog(output);
            Assert.That(((List<Dictionary<string, object?>>)described["bundles"]!).Single()["reconciliation_ok"], Is.EqualTo(true));
            Assert.That(((List<Dictionary<string, object?>>)described["study_bundles"]!).Single()["layer"], Is.EqualTo("aging"));
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    [Test]
    public void EveryDerivedTableAndViewIsDocumented()
    {
        var documented = CatalogBuilder.DerivedDocs.Keys.Order(StringComparer.Ordinal);
        var derived = CatalogBuilder.DerivedTables
            .Concat(CatalogBuilder.AcceptedViews.Select(kv => kv.Key))
            .Concat(CatalogBuilder.GrainViews.Select(kv => kv.Key))
            .Order(StringComparer.Ordinal);
        Assert.That(documented, Is.EqualTo(derived));
        Assert.That(CatalogBuilder.DerivedColumnDocs["samples"]["organism_part_name"], Does.StartWith(
            "The NAME the SDRF gives for `organism_part`, as written (from characteristics[organism part] or factor value[organism part]), "));
    }

    // --- real data ---------------------------------------------------------------------------------------

    private const string AgingStore = "F:/aging_data/repo/store";
    private const string AgingManifest = "F:/aging_data/batch/manifest.yaml";

    /// <summary>Thirteen of aging's datasets (ten of them the ones the aging study layer delivers for), the
    /// latest study delivery and every logs artefact for their databases, built by C# and dumped exactly as
    /// <c>expected_real.json</c> was from the Python's catalog of the same bundles.</summary>
    [Test, Category("RealData")]
    public void ARealCatalogHoldsThePythonRows()
    {
        if (!Directory.Exists(AgingStore) || !File.Exists(AgingManifest)) Assert.Ignore($"{AgingStore} is not on this machine");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "expected_real.json")));
        var expected = doc.RootElement;
        var about = expected.GetProperty("_about");
        // In the order the Python build was given them, which is the order of catalog_bundles and the checks.
        var order = about.GetProperty("bundles").EnumerateArray()
            .Select(p => (Accession: p[0].GetString()!, Id: p[1].GetString()!)).ToList();
        var pins = order.ToDictionary(p => p.Accession, p => p.Id);
        foreach (var (accession, id) in order)
            if (!Directory.Exists(Path.Combine(AgingStore, accession, id))) Assert.Ignore($"{accession} bundle {id} is no longer in the store");
        var studyId = about.GetProperty("study_bundle").GetString()!;
        if (!Directory.Exists(Path.Combine(AgingStore, "_study", "aging", studyId))) Assert.Ignore($"study bundle {studyId} is no longer in the store");

        var manifest = Manifest.Load(AgingManifest);
        var output = NewScratch("real") + ".duckdb";
        try
        {
            var bundles = CatalogBuilder.SelectBundles(manifest, order.Select(p => p.Accession).ToList(), AgingStore, pins);
            var study = CatalogBuilder.SelectStudyBundles(AgingStore, new Dictionary<string, string> { ["aging"] = studyId });
            var (artefacts, engineChecks) = CatalogBuilder.SelectArtefacts(AgingStore, bundles);
            Assert.That(artefacts.Select(a => a.ArtefactId).Order(StringComparer.Ordinal),
                Is.EqualTo(about.GetProperty("artefacts").EnumerateArray().Select(a => a.GetString()!)), "the artefacts on offer changed since the fixture");
            var notes = new Dictionary<string, object?>
            {
                ["manifest"] = manifest.Path,
                ["study"] = new Dictionary<string, object?> { ["aging"] = studyId },
            };
            var result = CatalogBuilder.BuildCatalog(bundles, output, false, manifest.Instance, notes, study, artefacts, engineChecks);
            Assert.That(result.FailedChecks, Is.Empty);
            AssertSameAsPython(Dump(output, about.GetProperty("full_limit").GetInt64()), expected, comparePlatform: true);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + "*")) File.Delete(f);
        }
    }
}
