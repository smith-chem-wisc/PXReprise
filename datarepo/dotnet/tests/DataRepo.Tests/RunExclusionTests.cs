using System.Globalization;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>G85: runs the producer excludes from ANALYSIS, as a manifest field <c>excluded_runs: {run: reason}</c>.
/// The runs were searched and stay in the bundle; the field is not content, so it reaches the catalog at build
/// (<c>run_exclusions</c>), is checked by <c>datarepo manifest</c> and <c>datarepo build</c>, and never by ingest.</summary>
[NonParallelizable]
public class RunExclusionTests
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

    // The fixture dataset's two runs.
    private const string RunA = "QE-002106_GM1_a";
    private const string RunB = "QE-002107_GM1_b";
    private const string FlagsLine = "    flags: [low_id_rate, no_design_file]\n";

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-exclusions-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Gives the fixture dataset an <c>excluded_runs</c> block (or replaces the one it has).</summary>
    private void Exclude(string block)
    {
        var text = File.ReadAllText(ManifestPath).Replace("\r\n", "\n");
        var at = text.IndexOf(FlagsLine, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "the fixture manifest changed under this test");
        var after = at + FlagsLine.Length;
        var end = text.IndexOf("\n  - accession:", after, StringComparison.Ordinal);
        File.WriteAllText(ManifestPath, text[..after] + block + text[end..]);
    }

    private static string Block(params (string Run, string Reason)[] runs) =>
        "    excluded_runs:\n" + string.Concat(runs.Select(r => $"      {r.Run}: {r.Reason}\n"));

    private BundleRef Ingest() =>
        BundleRef.Load(Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current).BundlePath);

    private CatalogResult Build(string name)
    {
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, name), instance: manifest.Instance,
            artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    private static List<object?[]> Rows(CatalogResult built, string sql) => CatalogBuilder.RunQuery(built.Path, sql).Rows.ToList();

    // --- the field -------------------------------------------------------------------------------------

    [Test]
    public void EveryDatasetEntryFieldIsClassifiedContentOrNot()
    {
        static string Snake(string name) => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
        var fields = typeof(DatasetEntry).GetProperties()
            .Where(p => p.SetMethod is not null)
            .Select(p => Snake(p.Name)).ToList();
        var classified = DatasetEntry.ContentFields.Concat(DatasetEntry.NonContentFields.Keys).ToList();
        Assert.That(fields, Is.SubsetOf(classified), "adding a field means deciding whether it is content");
        Assert.That(DatasetEntry.ContentFields.Intersect(DatasetEntry.NonContentFields.Keys), Is.Empty);
        Assert.That(DatasetEntry.NonContentFields.Keys, Does.Contain("excluded_runs"));
        Assert.That(DatasetEntry.ContentFields, Does.Not.Contain("excluded_runs"));
    }

    [Test]
    public void TheMapIsReadAsRunNameToReasonSortedByRun()
    {
        Exclude(Block((RunB, "\"failed QC: 40% of the MS2 of its neighbours\""), (RunA, "a blank, by the sample sheet"), ("2017_03", "a run name YAML would read as a number")));
        var entry = Manifest.Load(ManifestPath).Datasets["PXD999999"];
        Assert.That(entry.ExcludedRuns, Is.EqualTo(new[]
        {
            ("2017_03", "a run name YAML would read as a number"),
            (RunA, "a blank, by the sample sheet"),
            (RunB, "failed QC: 40% of the MS2 of its neighbours"),
        }), "the key's TEXT is the run name: PyYAML would have read 2017_03 as 201703");
        Assert.That(entry.ContentDeclaration().Keys, Does.Not.Contain("excluded_runs"));

        Exclude("    excluded_runs: {}\n");
        Assert.That(Manifest.Load(ManifestPath).Datasets["PXD999999"].ExcludedRuns, Is.Empty);
    }

    [TestCase("    excluded_runs:\n      QE-002106_GM1_a:\n", "excluded_runs['QE-002106_GM1_a'] must be a non-empty reason, found NoneType")]
    [TestCase("    excluded_runs:\n      QE-002106_GM1_a: \"  \"\n", "excluded_runs['QE-002106_GM1_a'] must be a non-empty reason, found an empty str")]
    [TestCase("    excluded_runs:\n      QE-002106_GM1_a: 3\n", "excluded_runs['QE-002106_GM1_a'] must be a non-empty reason, found int")]
    [TestCase("    excluded_runs:\n      QE-002106_GM1_a: [failed, QC]\n", "excluded_runs['QE-002106_GM1_a'] must be a non-empty reason, found list")]
    [TestCase("    excluded_runs: [QE-002106_GM1_a]\n", "excluded_runs must map a run name to the reason it is excluded from analysis, found list")]
    public void AMapThatIsNotRunToReasonIsRefusedAtLoad(string block, string message)
    {
        Exclude(block);
        Assert.That(() => Manifest.Load(ManifestPath),
            Throws.TypeOf<ManifestException>().With.Message.StartsWith($"{ManifestPath}: PXD999999: {message}"));
    }

    /// <summary>Two reasons for one run is a contradiction the producer resolves; it is never reduced to one.</summary>
    [Test]
    public void ARunListedTwiceIsRefused()
    {
        Exclude("    excluded_runs:\n      QE-002106_GM1_a: failed QC\n      QE-002106_GM1_a: a blank\n");
        Assert.That(() => Manifest.Load(ManifestPath),
            Throws.TypeOf<ManifestException>().With.Message.EqualTo($"{ManifestPath} is not valid YAML: Duplicate key QE-002106_GM1_a"));
    }

    // --- checked by `datarepo manifest`, never by ingest -----------------------------------------------

    [Test]
    public void ManifestChecksTheNamesAgainstTheRunsIngestReads()
    {
        Exclude(Block((RunB, "failed QC")));
        var manifest = Manifest.Load(ManifestPath);
        var (none, read) = Ingester.ExcludedRunProblems(manifest, manifest.Datasets["PXD999999"]);
        Assert.That(read, "the fixture's runs are reachable");
        Assert.That(none, Is.Empty);

        Exclude(Block((RunB + ".raw", "failed QC"), ("VM13", "failed QC")));
        manifest = Manifest.Load(ManifestPath);
        var (problems, _) = Ingester.ExcludedRunProblems(manifest, manifest.Datasets["PXD999999"]);
        Assert.That(problems, Is.EqualTo(new[]
        {
            $"PXD999999: excluded_runs names 2 run(s) that are not runs of this dataset: {RunB}.raw, VM13. Runs are the "
            + $"deposited raw file names without their extension, e.g. {RunA}, {RunB}. An exclusion that matches no run "
            + "would leave the run it meant looking fit for analysis.",
        }));
    }

    [Test]
    public void ARunTheSearchLeftOutIsNamedAsSuch()
    {
        var problems = Runs.ExclusionProblems([RunA], new HashSet<string> { RunB }, "PXD1", [(RunA, "x"), (RunB, "y")]);
        Assert.That(problems.Single(), Does.StartWith($"PXD1: excluded_runs names 1 run(s) the search left out: {RunB}."));
        Assert.That(Runs.ExclusionProblems(null, new HashSet<string>(), "PXD1", [("anything", "x")]), Is.Empty,
            "runs not readable: nothing checked, and the caller says so");
    }

    [Test]
    public void TheManifestCommandReportsAnUnknownRunAndExitsOne()
    {
        Exclude(Block((RunB, "failed QC")));
        var ok = Datarepo("manifest", ManifestPath);
        Assert.That(ok.Code, Is.EqualTo(0), ok.Stderr);
        Assert.That(ok.Stdout, Does.Contain($"     excluded {RunB}: failed QC\n"));
        Assert.That(ok.Stdout, Does.Contain("     excluded_runs  ok against the runs (1 excluded from analysis)\n"));

        Exclude(Block((RunB, "failed QC"), ("VM13", "failed QC")));
        var bad = Datarepo("manifest", ManifestPath);
        Assert.That(bad.Code, Is.EqualTo(1));
        Assert.That(bad.Stdout, Does.Contain("     UNMATCHED PXD999999: excluded_runs names 1 run(s) that are not runs of this dataset: VM13."));
        Assert.That(bad.Stderr, Does.Contain("1 dataset(s) have excluded_runs that are not runs of the dataset; build would refuse them"));
    }

    [Test]
    public void IngestNeitherReadsNorRefusesTheField()
    {
        var plain = Ingest();
        Exclude(Block((RunB, "failed QC"), ("VM13", "a run this dataset does not have")));
        var excluded = Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        Assert.That(excluded.BundleId, Is.EqualTo(plain.BundleId), "not content: the same bundle, unknown run or not");
        Assert.That(CatalogBuilder.DiscoverBundles(Store, "PXD999999"), Has.Count.EqualTo(1));
    }

    // --- served by the catalog --------------------------------------------------------------------------

    [Test]
    public void TheCatalogListsTheExclusionsAndTheirRunIdsJoinRuns()
    {
        var bundle = Ingest();
        var plain = Build("plain.duckdb");
        Assert.That(Rows(plain, $"SELECT count(*) FROM {CatalogBuilder.RunExclusionsTable}").Single()[0], Is.EqualTo(0L),
            "the table exists when nothing is excluded, so a query against it never fails");
        Assert.That(plain.Checks.Where(c => c.Name.StartsWith(CatalogBuilder.RunExclusionsTable)), Is.Empty);

        Exclude(Block((RunB, "failed QC")));
        var built = Build("excluded.duckdb");
        Assert.That(built.FailedChecks, Is.Empty);
        Assert.That(Rows(built, $"SELECT dataset_id, bundle_id, run_id, run_name, reason FROM {CatalogBuilder.RunExclusionsTable}"),
            Is.EqualTo(new[] { new object?[] { "PXD999999", bundle.BundleId, $"PXD999999:{RunB}", RunB, "failed QC" } }));

        // The flag an analysis needs, by the anti-join the table's description prescribes: every run is still in
        // `runs`, and exactly the excluded one drops out.
        Assert.That(Rows(built, "SELECT count(*) FROM runs").Single()[0], Is.EqualTo(2L), "excluded from analysis, not from the bundle");
        Assert.That(Rows(built,
                $"SELECT run_id, run_id IN (SELECT run_id FROM {CatalogBuilder.RunExclusionsTable}) AS excluded_from_analysis FROM runs ORDER BY run_id"),
            Is.EqualTo(new[] { new object?[] { $"PXD999999:{RunA}", false }, new object?[] { $"PXD999999:{RunB}", true } }));
        var check = built.Checks.Single(c => c.Name.StartsWith(CatalogBuilder.RunExclusionsTable));
        Assert.That((check.Kind, check.Ok, check.Observed, check.Expected), Is.EqualTo(("manifest", true, (long?)1, (long?)1)));
        Assert.That(Rows(built, $"SELECT kind FROM catalog_tables WHERE table_name = '{CatalogBuilder.RunExclusionsTable}'").Single()[0],
            Is.EqualTo("derived"));
        Assert.That(CatalogBuilder.DerivedDocs[CatalogBuilder.RunExclusionsTable].Description, Does.Contain("NOT IN (SELECT run_id FROM run_exclusions)"));
    }

    [Test]
    public void AChangedReasonMovesTheCatalogIdAndLeavesTheBundleAlone()
    {
        var bundle = Ingest();
        var bundleSha = BundleWriter.Sha256File(Path.Combine(bundle.Path, BundleWriter.ManifestName));
        var plain = Build("plain.duckdb");

        Exclude("    excluded_runs: {}\n");
        Assert.That(Build("empty.duckdb").CatalogId, Is.EqualTo(plain.CatalogId), "excluding nothing is the catalog it was");

        Exclude(Block((RunB, "failed QC")));
        var excluded = Build("excluded.duckdb");
        Assert.That(excluded.CatalogId, Is.Not.EqualTo(plain.CatalogId), "a catalog excluding a run is a different catalog");

        Exclude(Block((RunB, "failed QC (40% of the MS2 of its neighbours)")));
        var reworded = Build("reworded.duckdb");
        Assert.That(reworded.CatalogId, Is.Not.EqualTo(excluded.CatalogId));
        Assert.That(Rows(reworded, $"SELECT reason FROM {CatalogBuilder.RunExclusionsTable}").Single()[0],
            Is.EqualTo("failed QC (40% of the MS2 of its neighbours)"));

        foreach (var built in new[] { plain, excluded, reworded })
            Assert.That(built.Bundles.Single().BundleId, Is.EqualTo(bundle.BundleId));
        Assert.That(CatalogBuilder.DiscoverBundles(Store, "PXD999999").Select(b => b.BundleId), Is.EqualTo(new[] { bundle.BundleId }));
        Assert.That(BundleWriter.Sha256File(Path.Combine(bundle.Path, BundleWriter.ManifestName)), Is.EqualTo(bundleSha));

        var manifest = Manifest.Load(ManifestPath);
        var (artefacts, _) = CatalogBuilder.SelectArtefacts(Store, reworded.Bundles);
        Assert.That(CatalogBuilder.CatalogId(reworded.Bundles, [], artefacts, manifest: manifest), Is.EqualTo(reworded.CatalogId),
            "the id a caller recomputes is the id the build wrote");
    }

    [Test]
    public void TheBuildRefusesAnExclusionThatMatchesNoRun()
    {
        Ingest();
        Exclude(Block((RunB + ".raw", "failed QC")));
        var output = Path.Combine(_root, "refused.duckdb");
        Assert.That(() => Build("refused.duckdb"),
            Throws.TypeOf<CatalogException>().With.Message.Contains(
                $"PXD999999: excluded_runs names 1 run(s) that are not runs of this dataset: {RunB}.raw."));
        Assert.That(File.Exists(output), Is.False, "nothing is written when a check fails");
    }

    [Test]
    public void AParityBuildWritesNoTableAndHashesNoExclusion()
    {
        Ingest();
        Exclude(Block((RunB, "failed QC")));
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        using (SchemaContract.Python0320())
        {
            Assert.That(CatalogBuilder.CatalogId(bundles, manifest: manifest),
                Is.EqualTo(CatalogBuilder.CatalogId(bundles, manifest: manifest with { Datasets = new Dictionary<string, DatasetEntry>() })),
                "Python 0.32.0's catalogs carried no manifest prose, and a parity id must stay theirs");
        }
    }

    /// <summary>Runs one <c>datarepo</c> command line in-process.</summary>
    private static (int Code, string Stdout, string Stderr) Datarepo(params string[] argv)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (oldOut, oldErr, oldCulture) = (Console.Out, Console.Error, CultureInfo.CurrentCulture);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var code = DataRepo.Cli.Cli.Run(argv);
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            CultureInfo.CurrentCulture = oldCulture;
        }
    }
}
