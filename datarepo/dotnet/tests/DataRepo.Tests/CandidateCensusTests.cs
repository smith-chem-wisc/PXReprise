using System.Globalization;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>The discovery census (<c>dataset_candidates</c>, aging 088 AGING-P11): a TSV the manifest names as
/// <c>candidates</c>, read at build (not ingest), loaded into the catalog, hashed into its id and checked against the
/// datasets the catalog holds.</summary>
[NonParallelizable]
public class CandidateCensusTests
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
    private string CensusPath => Path.Combine(_root, "data", "candidates.tsv");
    private const string CreditLine = "credit: Test Working Group\n";

    private const string Header = "census_version\taccession\tincluded\texclusion_reason\tdefinition_id\n";
    private const string Census =
        Header
        + "2026-10-05\tPXD999999\ttrue\t\tDEF-AGING-SCREEN\n"
        + "2026-10-05\tPXD000001\ttrue\t\tDEF-AGING-SCREEN\n"
        + "2026-10-05\tPXD000002\tfalse\tspectra gate: not HCD Orbitrap\tDEF-AGING-SCREEN\n";

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-candidates-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Names <paramref name="value"/> as the manifest's <c>candidates</c> (a YAML scalar, written as given).</summary>
    private void NameCensus(string value = "candidates.tsv")
    {
        var text = File.ReadAllText(ManifestPath).Replace("\r\n", "\n");
        Assert.That(text, Does.Contain(CreditLine), "the fixture manifest changed under this test");
        File.WriteAllText(ManifestPath, text.Replace(CreditLine, CreditLine + $"candidates: {value}\n"));
    }

    private void WriteCensus(string text) => File.WriteAllBytes(CensusPath, new UTF8Encoding(false).GetBytes(text));

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

    // --- the file ---------------------------------------------------------------------------------------

    [Test]
    public void TheColumnsAreTheSchemasAndTheAccessionPatternIsTheSchemas()
    {
        Assert.That(CandidateCensus.Columns, Is.EqualTo(new[] { "census_version", "accession", "included", "exclusion_reason", "definition_id" }));
        Assert.That(CandidateCensus.Required, Is.EqualTo(new[] { "census_version", "accession", "included" }));
        var schema = File.ReadAllText(Path.Combine(RepoRoot(), "schema", "datarepo.yaml")).Replace("\r\n", "\n");
        var at = schema.IndexOf("\n  DatasetCandidate:\n", StringComparison.Ordinal);
        var accession = schema[schema.IndexOf("      accession:", at, StringComparison.Ordinal)..];
        Assert.That(accession[..accession.IndexOf('\n')], Does.Contain($"pattern: \"{CandidateCensus.AccessionPattern}\""));
    }

    [Test]
    public void ARowIsReadVerbatimWithEmptyOptionalCellsAsNull()
    {
        NameCensus();
        // A byte-order mark and CRLF, as a Windows editor writes them; a reason with its own spacing, kept.
        WriteCensus("﻿accession\tincluded\tcensus_version\texclusion_reason\r\n"
            + "PXD000002\tfalse\tv1\t  spectra gate:  not HCD Orbitrap \r\n"
            + "PXD999999\ttrue\tv1\t\r\n");
        var manifest = Manifest.Load(ManifestPath);
        Assert.That(manifest.CandidatesPath, Is.EqualTo(CensusPath), "relative to the manifest's folder");
        var census = manifest.Candidates()!;
        Assert.That(census.Rows, Is.EqualTo(new[]
        {
            new CandidateRow("v1", "PXD000002", false, "  spectra gate:  not HCD Orbitrap ", null),
            new CandidateRow("v1", "PXD999999", true, null, null),
        }));
        Assert.That(census.Sha256, Is.EqualTo(BundleWriter.Sha256File(CensusPath)), "the bytes as stored");
    }

    [TestCase("", "is empty; a census starts with a header row")]
    [TestCase("census_version\taccession\tincluded\tnotes\n", ":1: column 'notes' is not a dataset_candidates column")]
    [TestCase("census_version\taccession\tincluded\taccession\n", ":1: column 'accession' appears twice")]
    [TestCase("census_version\taccession\n", ":1: the header lacks the required column(s) included")]
    [TestCase("census_version\taccession\tincluded\nv1\tPXD1\n", ":2: 2 cell(s) where the header names 3")]
    [TestCase("census_version\taccession\tincluded\n\tPXD1\ttrue\n", ":2: census_version is empty, and every row must give one")]
    [TestCase("census_version\taccession\tincluded\nv1\t PXD1\ttrue\n", ":2: accession ' PXD1' is not a ProteomeXchange accession")]
    [TestCase("census_version\taccession\tincluded\nv1\tPXD1\tTrue\n", ":2: included is 'True'; it must be true or false")]
    [TestCase("census_version\taccession\tincluded\nv1\tPXD1\tyes\n", ":2: included is 'yes'; it must be true or false")]
    [TestCase("census_version\taccession\tincluded\nv1\tPXD1\ttrue\nv1\tPXD2\tfalse\nv2\tPXD1\tfalse\n", ":4: PXD1 is listed twice (also on line 2). One accession has one verdict.")]
    public void AMalformedCensusIsRefused(string text, string message)
    {
        NameCensus();
        WriteCensus(text);
        var manifest = Manifest.Load(ManifestPath);  // the path only: the file is read where it is used
        Assert.That(() => manifest.Candidates(),
            Throws.TypeOf<ManifestException>().With.Message.StartsWith(CensusPath).And.Message.Contains(message));
    }

    [Test]
    public void AMissingCensusAndAKeyThatIsNotAPathAreRefused()
    {
        NameCensus();
        Assert.That(() => Manifest.Load(ManifestPath).Candidates(),
            Throws.TypeOf<ManifestException>().With.Message.EqualTo($"no candidates census at {CensusPath}"));

        File.Copy(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), ManifestPath, overwrite: true);
        NameCensus("[a.tsv]");
        Assert.That(() => Manifest.Load(ManifestPath), Throws.TypeOf<ManifestException>().With.Message.EqualTo(
            $"{ManifestPath}: candidates must be the path of the discovery census TSV, found list"));
    }

    // --- never read by ingest --------------------------------------------------------------------------

    [Test]
    public void IngestNeitherReadsNorRefusesTheCensus()
    {
        var plain = Ingest();
        NameCensus();
        WriteCensus("not a census\n");
        var withCensus = Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        Assert.That(withCensus.BundleId, Is.EqualTo(plain.BundleId), "an instance-level fact: the same bundle, malformed census or not");
    }

    // --- served by the catalog --------------------------------------------------------------------------

    [Test]
    public void TheBuildLoadsTheCensusAndRecordsWhereItCameFrom()
    {
        var bundle = Ingest();
        var plain = Build("plain.duckdb");
        Assert.That(Rows(plain, $"SELECT count(*) FROM {CandidateCensus.Table}").Single()[0], Is.EqualTo(0L), "no census: the empty core table");
        Assert.That(Rows(plain, $"SELECT kind FROM catalog_tables WHERE table_name = '{CandidateCensus.Table}'").Single()[0], Is.EqualTo("bundle"));
        Assert.That(plain.Checks.Where(c => c.Kind == "census"), Is.Empty);
        Assert.That((string)Rows(plain, "SELECT CAST(notes AS VARCHAR) FROM catalog_meta").Single()[0]!, Does.Not.Contain("candidates"));

        NameCensus();
        WriteCensus(Census);
        var built = Build("census.duckdb");
        Assert.That(built.FailedChecks, Is.Empty);
        Assert.That(Rows(built,
                $"SELECT dataset_id, bundle_id, census_version, accession, included, exclusion_reason, definition_id FROM {CandidateCensus.Table} ORDER BY accession"),
            Is.EqualTo(new[]
            {
                new object?[] { null, null, "2026-10-05", "PXD000001", true, null, "DEF-AGING-SCREEN" },
                new object?[] { null, null, "2026-10-05", "PXD000002", false, "spectra gate: not HCD Orbitrap", "DEF-AGING-SCREEN" },
                new object?[] { "PXD999999", bundle.BundleId, "2026-10-05", "PXD999999", true, null, "DEF-AGING-SCREEN" },
            }), "the catalog's dataset and bundle where it holds the accession, NULL where it does not");
        Assert.That(built.RowCounts[CandidateCensus.Table], Is.EqualTo(3L));
        Assert.That(Rows(built, $"SELECT kind, rows FROM catalog_tables WHERE table_name = '{CandidateCensus.Table}'").Single(),
            Is.EqualTo(new object?[] { "manifest", 3L }));
        var notes = (string)Rows(built, "SELECT CAST(notes -> 'candidates' AS VARCHAR) FROM catalog_meta").Single()[0]!;
        Assert.That(notes, Does.Contain(BundleWriter.Sha256File(CensusPath)).And.Contain("candidates.tsv").And.Contain("\"rows\":3"));

        // `describe` says what the provenance columns mean HERE, and only where a census was loaded.
        static string Means(string catalog)
        {
            using var server = new DataRepo.Mcp.CatalogServer(catalog);
            var columns = (IEnumerable<object?>)server.Describe(CandidateCensus.Table, "detailed")["columns"]!;
            return (string)columns.Cast<Dictionary<string, object?>>().Single(c => Equals(c["column"], "dataset_id"))["means"]!;
        }
        Assert.That(Means(built.Path), Does.StartWith("The catalog's dataset for this accession, or NULL when the catalog holds none"));
        Assert.That(Means(plain.Path), Does.StartWith("provenance added by `datarepo build`"));
    }

    [Test]
    public void TheCatalogIdMovesWithTheCensusAndNotWithoutOne()
    {
        Ingest();
        var plain = Build("plain.duckdb");
        var manifest = Manifest.Load(ManifestPath);
        Assert.That(manifest.CandidatesPath, Is.Null);
        Assert.That(CatalogBuilder.CatalogId(plain.Bundles, [], plain.Artefacts, manifest: manifest), Is.EqualTo(plain.CatalogId));

        NameCensus();
        WriteCensus(Census);
        var census = Build("census.duckdb");
        Assert.That(census.CatalogId, Is.Not.EqualTo(plain.CatalogId), "a catalog with a census is a different catalog");

        WriteCensus(Census.Replace("not HCD Orbitrap", "not HCD Orbitrap (Q Exactive HF-X only)"));
        var edited = Build("edited.duckdb");
        Assert.That(edited.CatalogId, Is.Not.EqualTo(census.CatalogId), "an edited reason moves the id");
        Assert.That(edited.Bundles.Single().BundleId, Is.EqualTo(plain.Bundles.Single().BundleId), "and no bundle");

        File.Move(CensusPath, Path.Combine(_root, "elsewhere.tsv"));
        File.Copy(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), ManifestPath, overwrite: true);
        NameCensus(Path.Combine(_root, "elsewhere.tsv").Replace('\\', '/'));
        var moved = Build("moved.duckdb");
        Assert.That(moved.CatalogId, Is.EqualTo(edited.CatalogId), "the census's bytes, not its path");
        Assert.That(CatalogBuilder.CatalogId(moved.Bundles, [], moved.Artefacts, manifest: Manifest.Load(ManifestPath)), Is.EqualTo(moved.CatalogId),
            "the id a caller recomputes is the id the build wrote");
    }

    [Test]
    public void TheChecksNameIncludedAccessionsNotHeldAndDatasetsTheCensusDoesNotInclude()
    {
        Ingest();
        NameCensus();
        WriteCensus(Census);
        var built = Build("census.duckdb");
        var checks = built.Checks.Where(c => c.Kind == "census").ToList();
        Assert.That(checks.Select(c => (c.Name, c.Ok, c.Observed, c.Expected, c.Detail)), Is.EqualTo(new[]
        {
            ("dataset_candidates (included accessions the catalog holds)", true, (long?)1, (long?)2,
                "the census includes, and the catalog holds no dataset for: PXD000001 (the census may run ahead of ingest)"),
            ("dataset_candidates (catalog datasets the census includes)", true, (long?)1, (long?)1, (string?)null),
        }));

        WriteCensus(Header + "v1\tPXD000002\tfalse\tspectra gate\t\n");
        var absent = Build("absent.duckdb").Checks.Where(c => c.Kind == "census").ToList();
        Assert.That(absent.Select(c => (c.Observed, c.Expected, c.Detail)), Is.EqualTo(new[]
        {
            ((long?)0, (long?)0, (string?)null),
            ((long?)0, (long?)1, (string?)"not in the census: PXD999999"),
        }));

        WriteCensus(Header + "v1\tPXD999999\tfalse\tlow ID rate\t\n");
        var excluded = Build("excluded.duckdb");
        Assert.That(excluded.FailedChecks, Is.Empty, "named, never refused");
        Assert.That(excluded.Checks.Single(c => c.Name.Contains("catalog datasets")).Detail,
            Is.EqualTo("in the census as not included: PXD999999 (low ID rate)"));
    }

    [Test]
    public void ABuildRefusesAMalformedCensusAndWritesNothing()
    {
        Ingest();
        NameCensus();
        WriteCensus(Header + "v1\tPXD999999\tmaybe\t\t\n");
        Assert.That(() => Build("refused.duckdb"), Throws.TypeOf<ManifestException>().With.Message.Contains("included is 'maybe'"));
        Assert.That(File.Exists(Path.Combine(_root, "refused.duckdb")), Is.False);
    }

    [Test]
    public void AParityBuildReadsNoCensus()
    {
        Ingest();
        NameCensus();
        WriteCensus(Census);
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        using (SchemaContract.Python0320())
        {
            Assert.That(CatalogBuilder.Census(manifest), Is.Null);
            Assert.That(CatalogBuilder.CatalogId(bundles, manifest: manifest),
                Is.EqualTo(CatalogBuilder.CatalogId(bundles, manifest: manifest with { CandidatesPath = null })),
                "Python 0.32.0's catalogs had no census, and a parity id must stay theirs");
        }
    }

    // --- `datarepo manifest` ----------------------------------------------------------------------------

    [Test]
    public void TheManifestCommandReadsTheCensusAndExitsOneOnAMalformedOne()
    {
        NameCensus();
        WriteCensus(Census);
        var ok = Datarepo("manifest", ManifestPath);
        Assert.That(ok.Code, Is.EqualTo(0), ok.Stderr);
        Assert.That(ok.Stdout, Does.Contain($"candidates {CensusPath}\n     3 accession(s) screened: 2 included, 1 not (census 2026-10-05)\n"));

        WriteCensus(Census + "2026-10-05\tPXD000001\tfalse\t\t\n");
        var bad = Datarepo("manifest", ManifestPath);
        Assert.That(bad.Code, Is.EqualTo(1));
        Assert.That(bad.Stdout, Does.Contain($"     REFUSED {CensusPath}:5: PXD000001 is listed twice (also on line 3)."));
        Assert.That(bad.Stderr, Does.Contain("the candidates census is malformed; build would refuse it"));
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
