using System.Text.Json.Nodes;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Runner;
using DuckDB.NET.Data;

namespace PXReprise.Data.Tests;

/// <summary><c>logs.register_orthology</c> (DATAREPO-75, logs 031): verify a released snapshot, register it, cite it.</summary>
/// <remarks>The snapshot here is built in logs' file contract (<c>logs:DEF-ORTHOLOGY v1</c>) by
/// <see cref="OrthologySnapshots"/>: the real columns, the layout, and the release's own <c>views.sql</c>
/// (Fixtures/orthology). Two species, <c>sp_a</c> and <c>sp_b</c>, with genes in each of the four <c>pair_status</c>
/// classes. <see cref="TheReleasedSnapshotRegisters"/> runs the real release when it is on this machine.</remarks>
public class OrthologyEngineTests
{
    private static Dictionary<string, object?> StandInInstall => OrthologySnapshots.StandInInstall;

    private const string GeneSetA = OrthologySnapshots.GeneSetA;
    private const string GeneSetB = OrthologySnapshots.GeneSetB;

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

    // The snapshot, its release and its registration are OrthologySnapshots', shared with OrthologyCatalogTests.
    private string MakeSnapshot(string name, bool extraMember = false) => OrthologySnapshots.Make(_root, name, extraMember);

    private static void WriteManifest(string dir, Action<JsonObject>? edit = null) => OrthologySnapshots.WriteManifest(dir, edit);

    private static (Dictionary<string, string> Inputs, SnapshotRelease Release) Release(string dir) => OrthologySnapshots.Release(dir);

    private RunResult Register(Dictionary<string, string> inputs, SnapshotRelease release) => OrthologySnapshots.Register(Store, inputs, release);

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
        Assert.That(() => PXReprise.DataCli.RunCommand.Run(["logs.register_orthology", "PXD000001", "--store", Store]),
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
