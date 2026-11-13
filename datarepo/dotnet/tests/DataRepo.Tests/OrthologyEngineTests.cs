using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Runner;
using DuckDB.NET.Data;

namespace DataRepo.Tests;

/// <summary><c>logs.register_orthology</c> (DATAREPO-75, logs 031): verify a released snapshot, register it, cite it.</summary>
/// <remarks>The snapshot here is built in logs' file contract (<c>logs:DEF-ORTHOLOGY v1</c>): the real columns, the
/// layout, and the release's own <c>views.sql</c> (Fixtures/orthology). Two species, <c>sp_a</c> and <c>sp_b</c>,
/// with genes in each of the four <c>pair_status</c> classes. <see cref="TheReleasedSnapshotRegisters"/> runs the
/// real release when it is on this machine.</remarks>
public class OrthologyEngineTests
{
    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    private const string GeneSetA = "aaaa000000000000000000000000000000000000000000000000000000000000";
    private const string GeneSetB = "bbbb000000000000000000000000000000000000000000000000000000000000";

    private string _root = "";
    private string Store => Path.Combine(_root, "store");

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-orthology-test-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void RemoveScratch()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { /* a DuckDB file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    private static string ViewsSql => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "orthology", "views.sql");

    private static string Sha(string path) => BundleWriter.Sha256File(path);

    /// <summary>A snapshot directory in the contract's layout. <paramref name="extraMember"/> puts gene A1 in a second
    /// tree, which makes <c>pair_status</c> give it two rows.</summary>
    private string MakeSnapshot(string name, bool extraMember = false)
    {
        var dir = Path.Combine(_root, "src-" + name, name);
        foreach (var sub in new[] { "genes", "members", "pairs" }) Directory.CreateDirectory(Path.Combine(dir, sub));
        using (var con = new DuckDBConnection("DataSource=:memory:"))
        {
            con.Open();
            void Exec(string sql)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            string P(string rel) => "'" + Path.Combine(dir, rel).Replace('\\', '/') + "'";
            const string gene = "gene_id VARCHAR, gene_version INTEGER, gene_biotype VARCHAR, gene_name VARCHAR, seq_region VARCHAR, species VARCHAR";
            const string member = "gene_id VARCHAR, species VARCHAR, group_id VARCHAR, source_group_id VARCHAR, canonical_protein_id VARCHAR";
            Exec($"COPY (SELECT * FROM (VALUES ('A1',1,'protein_coding','a1','1','sp_a'),('A2',1,'protein_coding','a2','1','sp_a'),"
                 + $"('A3',1,'protein_coding','a3','1','sp_a'),('A4',1,'lncRNA','a4','1','sp_a')) t({gene.Replace(" VARCHAR", "").Replace(" INTEGER", "")})) TO {P("genes/sp_a.parquet")}");
            Exec($"COPY (SELECT * FROM (VALUES ('B1',1,'protein_coding','b1','1','sp_b'),('B2',1,'protein_coding','b2','1','sp_b')) "
                 + $"t({gene.Replace(" VARCHAR", "").Replace(" INTEGER", "")})) TO {P("genes/sp_b.parquet")}");
            var cols = member.Replace(" VARCHAR", "");
            // A1 has an ortholog; A2 shares tree T2 with B2 but no edge; A3's tree holds no sp_b gene; A4 is in no tree.
            Exec($"COPY (SELECT * FROM (VALUES ('A1','sp_a','T1','s1','PA1'),('A2','sp_a','T2','s2','PA2'),('A3','sp_a','T3','s3','PA3')"
                 + (extraMember ? ",('A1','sp_a','T2','s2','PA1')" : "") + $") t({cols})) TO {P("members/sp_a.parquet")}");
            Exec($"COPY (SELECT * FROM (VALUES ('B1','sp_b','T1','s1','PB1'),('B2','sp_b','T2','s2','PB2')) t({cols})) TO {P("members/sp_b.parquet")}");
            const string pair = "SELECT homology_id::VARCHAR AS homology_id, relationship_type::VARCHAR AS relationship_type, "
                + "relationship_class::VARCHAR AS relationship_class, species_a::VARCHAR AS species_a, species_b::VARCHAR AS species_b, "
                + "gene_a::VARCHAR AS gene_a, protein_a::VARCHAR AS protein_a, identity_a::DOUBLE AS identity_a, gene_b::VARCHAR AS gene_b, "
                + "protein_b::VARCHAR AS protein_b, identity_b::DOUBLE AS identity_b, NULL::DOUBLE AS dn, NULL::DOUBLE AS ds, "
                + "75::INTEGER AS goc_score, 99.0::DOUBLE AS wga_coverage, true AS is_high_confidence, 'test'::VARCHAR AS source_dump FROM ";
            const string pcols = "t(homology_id, relationship_type, relationship_class, species_a, species_b, gene_a, protein_a, identity_a, gene_b, protein_b, identity_b)";
            Exec($"COPY ({pair}(VALUES ('1','ortholog_one2one','ortholog','sp_a','sp_b','A1','PA1',70.0,'B1','PB1',69.0)) {pcols}) TO {P("pairs/sp_a__sp_b.parquet")}");
            Exec($"COPY ({pair}(VALUES ('2','within_species_paralog','paralog','sp_a','sp_a','A2','PA2',40.0,'A3','PA3',41.0)) {pcols}) TO {P("pairs/sp_a__sp_a.parquet")}");
            Exec($"COPY ({pair}(VALUES ('3','within_species_paralog','paralog','sp_b','sp_b','B1','PB1',40.0,'B2','PB2',41.0)) {pcols}) TO {P("pairs/sp_b__sp_b.parquet")}");
        }
        File.Copy(ViewsSql, Path.Combine(dir, "views.sql"));
        WriteManifest(dir);
        return dir;
    }

    private static readonly Dictionary<string, long> Rows = new()
    {
        ["genes/sp_a.parquet"] = 4, ["genes/sp_b.parquet"] = 2, ["members/sp_b.parquet"] = 2,
        ["pairs/sp_a__sp_b.parquet"] = 1, ["pairs/sp_a__sp_a.parquet"] = 1, ["pairs/sp_b__sp_b.parquet"] = 1,
    };

    /// <summary>Writes manifest.json listing every file as it now is (members/sp_a rows counted from the file).</summary>
    private static void WriteManifest(string dir, Action<JsonObject>? edit = null)
    {
        var files = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(f => f != "manifest.json").Order(StringComparer.Ordinal))
        {
            var full = Path.Combine(dir, path);
            var entry = new JsonObject { ["path"] = path };
            if (path.EndsWith(".parquet", StringComparison.Ordinal))
                entry["rows"] = Rows.TryGetValue(path, out var r) ? r : CountRows(full);
            entry["bytes"] = new FileInfo(full).Length;
            entry["sha256"] = BundleWriter.Sha256File(full);
            files.Add(entry);
        }
        var manifest = new JsonObject
        {
            ["format"] = "ensembl-orthology-snapshot", ["format_version"] = 1, ["snapshot_id"] = "test-" + Path.GetFileName(dir),
            ["source"] = "ensembl_compara", ["release"] = "116", ["species"] = new JsonArray("sp_a", "sp_b"),
            ["gene_set_sha256"] = new JsonObject { ["sp_a"] = GeneSetA, ["sp_b"] = GeneSetB },
            ["files"] = files,
        };
        edit?.Invoke(manifest);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static long CountRows(string parquet)
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM read_parquet('{parquet.Replace('\\', '/')}')";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>The tar and the manifest beside it, as logs releases them, and a release entry naming both.</summary>
    private (Dictionary<string, string> Inputs, SnapshotRelease Release) Release(string dir)
    {
        var tar = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, Path.GetFileName(dir) + ".tar");
        if (File.Exists(tar)) File.Delete(tar);
        TarFile.CreateFromDirectory(dir, tar, includeBaseDirectory: true);
        var manifest = Path.Combine(Path.GetDirectoryName(tar)!, Path.GetFileName(dir) + ".manifest.json");
        File.Copy(Path.Combine(dir, "manifest.json"), manifest, overwrite: true);
        var release = new SnapshotRelease("orthology-test-" + Path.GetFileName(dir), Sha(tar), Sha(manifest), "test attribution");
        return (new Dictionary<string, string> { ["snapshot_tar"] = tar, ["snapshot_manifest"] = manifest }, release);
    }

    private RunResult Register(Dictionary<string, string> inputs, SnapshotRelease release) =>
        OrthologyEngine.Run(Store, inputs, [release], StandInInstall);

    [Test]
    public void ASnapshotRegistersReadInPlaceWithEveryGeneGivenOneStatus()
    {
        var (inputs, release) = Release(MakeSnapshot("snap1"));
        var artefact = Register(inputs, release).Written.Single();

        Assert.That(artefact.Engine, Is.EqualTo("logs.register_orthology"));
        Assert.That(artefact.RowCounts, Is.Empty, "no orthology rows are copied");
        var snapshot = (IReadOnlyDictionary<string, object?>)artefact.Record["snapshot"]!;
        Assert.That(snapshot["tag"], Is.EqualTo(release.Tag));
        Assert.That(snapshot["snapshot_id"], Is.EqualTo("test-snap1"));
        Assert.That((snapshot["format"], snapshot["format_version"]), Is.EqualTo(((object?)"ensembl-orthology-snapshot", (object?)1L)));
        Assert.That(artefact.Record["definition_id"], Is.EqualTo("logs:DEF-ORTHOLOGY v1"));
        Assert.That(artefact.Inputs, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["snapshot_tar"] = release.TarSha256, ["snapshot_manifest"] = release.ManifestSha256,
        }));

        // The four statuses, one per gene of sp_a, as the release's own views.sql gives them.
        var summary = (IReadOnlyDictionary<string, object?>)artefact.Record["engine_summary"]!;
        var aToB = ((IEnumerable<object?>)summary["pair_status"]!).Cast<IReadOnlyDictionary<string, object?>>()
            .Single(d => (string)d["from"]! == "sp_a");
        Assert.That(aToB["status_counts"], Is.EquivalentTo(new Dictionary<string, object?>
        {
            ["has_ortholog"] = 1L, ["no_edge_in_shared_tree"] = 1L, ["tree_lacks_target_species"] = 1L, ["not_in_any_tree"] = 1L,
        }));

        // Read in place: the extracted snapshot answers views.sql from the artefact.
        var root = Path.Combine(artefact.Path, "snapshot").Replace('\\', '/');
        Assert.That(File.Exists(Path.Combine(root, "manifest.json")));
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using (var load = con.CreateCommand())
        {
            load.CommandText = File.ReadAllText(Path.Combine(root, "views.sql"));
            load.ExecuteNonQuery();
        }
        using var query = con.CreateCommand();
        query.CommandText = $"SELECT partner_gene FROM orthologs('{root}', 'sp_a', 'sp_b') WHERE gene = 'A1'";
        Assert.That(query.ExecuteScalar(), Is.EqualTo("B1"));
    }

    [Test]
    public void TheSameSnapshotIsAlreadyDone()
    {
        var (inputs, release) = Release(MakeSnapshot("snap1"));
        var first = Register(inputs, release).Written.Single();
        var again = Register(inputs, release);
        Assert.That(again.Written, Is.Empty);
        Assert.That(again.AlreadyDone.Single().ArtefactId, Is.EqualTo(first.ArtefactId));
    }

    [Test]
    public void ASnapshotLogsDidNotReleaseIsRefused()
    {
        var (inputs, release) = Release(MakeSnapshot("snap1"));
        Assert.That(() => OrthologyEngine.Run(Store, inputs, [release with { TarSha256 = new string('0', 64) }], StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("is not a snapshot logs has released"));
        Assert.That(() => OrthologyEngine.Run(Store, inputs, [release with { ManifestSha256 = new string('0', 64) }], StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("is not the manifest of"));
        Assert.That(Directory.Exists(Path.Combine(Store, "_engine")), Is.False, "nothing was written");
    }

    [Test]
    public void AnotherFormatVersionIsRefusedNotReadAsOne()
    {
        var dir = MakeSnapshot("snap1");
        WriteManifest(dir, m => m["format_version"] = 2);
        var (inputs, release) = Release(dir);
        Assert.That(() => Register(inputs, release),
            Throws.TypeOf<RunnerException>().With.Message.Contains("format_version 2").And.Message.Contains("reads 1 only"));
    }

    [Test]
    public void AFileThatDoesNotMatchTheManifestIsRefused()
    {
        var dir = MakeSnapshot("snap1");
        File.AppendAllText(Path.Combine(dir, "views.sql"), "\n-- edited after the manifest was written\n");
        var (inputs, release) = Release(dir);
        Assert.That(() => Register(inputs, release),
            Throws.TypeOf<RunnerException>().With.Message.Contains("views.sql hashes to").And.Message.Contains("the manifest says"));
    }

    [Test]
    public void AFileTheManifestDoesNotListIsRefused()
    {
        var dir = MakeSnapshot("snap1");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not in the contract");
        var (inputs, release) = Release(dir);  // the manifest was written before the file existed
        Assert.That(() => Register(inputs, release),
            Throws.TypeOf<RunnerException>().With.Message.Contains("the tar and the manifest disagree").And.Message.Contains("notes.txt"));
    }

    [Test]
    public void AGeneWithTwoStatusesIsRefused()
    {
        // A1 in two trees: pair_status then gives it two rows, which the contract says cannot happen.
        var (inputs, release) = Release(MakeSnapshot("snap1", extraMember: true));
        Assert.That(() => Register(inputs, release),
            Throws.TypeOf<RunnerException>().With.Message.Contains("does not give each of sp_a's 4 genes exactly one status"));
    }

    [Test]
    public void ACatalogCitesOneSnapshotAndRefusesTwo()
    {
        var (inputs, release) = Release(MakeSnapshot("snap1"));
        var artefact = Register(inputs, release).Written.Single();
        var (chosen, checks) = CatalogBuilder.SelectArtefacts(Store, []);
        Assert.That(chosen.Select(c => c.ArtefactId), Is.EqualTo(new[] { artefact.ArtefactId }));
        Assert.That(checks.Single(c => c.Name.StartsWith("logs.register_orthology")).Observed, Is.EqualTo(1));

        var (inputs2, release2) = Release(MakeSnapshot("snap2"));
        Register(inputs2, release2);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, []),
            Throws.TypeOf<CatalogException>().With.Message.Contains("2 snapshots are registered").And.Message.Contains("A catalog serves one"));
    }

    [Test]
    public void TheCatalogFailsWhenGeneResolutionsUsedAnotherGeneSet()
    {
        var (inputs, release) = Release(MakeSnapshot("snap1"));
        var artefact = Register(inputs, release).Written.Single();
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        void Exec(string sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec($"CREATE TABLE gene_resolutions AS SELECT * FROM (VALUES ('{GeneSetA}'), ('{GeneSetB}'), (NULL)) t(gene_set_sha256)");
        var ok = CatalogBuilder.CheckOrthology(con, [artefact]).Single();
        Assert.That((ok.Ok, ok.Observed), Is.EqualTo((true, (long?)0)));

        Exec("INSERT INTO gene_resolutions VALUES ('cccc00000000000000')");
        var bad = CatalogBuilder.CheckOrthology(con, [artefact]).Single();
        Assert.That((bad.Ok, bad.Observed), Is.EqualTo((false, (long?)1)));
        Assert.That(bad.Detail, Does.Contain("cccc00000000").And.Contains("not built from"));
        Assert.That(CatalogBuilder.CheckOrthology(con, []), Is.Empty, "no snapshot, nothing to check");
    }

    [Test]
    public void TheCliTakesNoAccessionForASnapshot()
    {
        Assert.That(() => DataRepo.Cli.RunCommand.Run(["logs.register_orthology", "PXD000001", "--store", Store]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("takes no accession"));
    }

    /// <summary>logs' real release, when it is on this machine (scratch: a stand-in install, a temp store).</summary>
    [Test, Category("RealData")]
    public void TheReleasedSnapshotRegisters()
    {
        const string dir = "E:/CodeReview/logs/snapshots/_release";
        var inputs = new Dictionary<string, string>
        {
            ["snapshot_tar"] = Path.Combine(dir, "compara-116.tar"), ["snapshot_manifest"] = Path.Combine(dir, "compara-116.manifest.json"),
        };
        if (!File.Exists(inputs["snapshot_tar"])) Assert.Ignore("logs' release is not on this machine");
        var artefact = OrthologyEngine.Run(Store, inputs, install: StandInInstall).Written.Single();
        var snapshot = (IReadOnlyDictionary<string, object?>)artefact.Record["snapshot"]!;
        Assert.That(snapshot["tag"], Is.EqualTo("orthology-compara-116-b63a3331"));
        Assert.That(snapshot["snapshot_id"], Is.EqualTo("b63a3331eb87c93eee02d7ec26c9bdcc7a1479e0003b041bb33382d6810bc3c7"));
        var summary = (IReadOnlyDictionary<string, object?>)artefact.Record["engine_summary"]!;
        Assert.That(((IEnumerable<object?>)summary["pair_status"]!).Count(), Is.EqualTo(6), "three species, six directions");
        TestContext.Out.WriteLine(PyFormat.JsonIndented(summary, 1));
    }
}
