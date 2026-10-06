using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Cli;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;
using DataRepo.Runner;

namespace DataRepo.Tests;

/// <summary>The go engine (<c>go.annotate_groups</c>, G86): mzLib's annotator on a stored search, go's files read back
/// as the acceptance check, and the catalog loading the artefact.</summary>
/// <remarks>
/// <para>The fixture dataset is PXD999999 (<c>tests/data</c>), ingested here with the current rules. Its searched
/// database carries no GO, so each test works on a scratch copy of the fixture tree whose <c>test_human.xml</c> has
/// GO references added to three entries (<see cref="GoReferences"/>), with the search provenance's sha256 for that
/// file updated to match, so the ingest reads it as the database the search used. The ontology is mzLib's own
/// trimmed go.obo and the map a three-row one (<c>Fixtures/go-engine/PROVENANCE.md</c>).</para>
/// <para>The definition id is <see cref="FakeDefinition"/>: go has published none, and the engine refuses without
/// one.</para>
/// </remarks>
public class GoEngineTests
{
    private const string FakeDefinition = "test:DEF-GO-FIXTURE";

    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    private const string InnerMembrane = "GO:0005743";
    private const string Mitochondrion = "GO:0005739";
    private const string Cytoplasm = "GO:0005737";
    private const string Gapdh = "GO:0004365";
    private const string Unknown = "GO:9999999";

    /// <summary>GO references added to the scratch database, by accession. Q71U36 cites only an id the ontology
    /// lacks, so it is dropped and listed (go D35); P99999 (a group of its own) is in no database.</summary>
    private static readonly Dictionary<string, (string Id, string Term, string Evidence)[]> GoReferences = new()
    {
        ["P05141"] = [(InnerMembrane, "C:mitochondrial inner membrane", "ECO:0000314"), (Gapdh, "F:GAPDH activity", "ECO:0000250")],
        ["P68363"] = [(Cytoplasm, "C:cytoplasm", "ECO:0007005")],
        ["Q71U36"] = [(Unknown, "C:a term newer than the release", "ECO:0000314")],
    };

    private static string FixtureDir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "go-engine");
    private static string Ontology => Path.Combine(FixtureDir, "go-trimmed.obo");
    private static string Map => Path.Combine(FixtureDir, "fixture_map.tsv");
    private static Dictionary<string, string> Inputs => new() { ["ontology"] = Ontology, ["category_map"] = Map };

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string WorkRoot => Path.Combine(_root, "data", "work_root");

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-go-engine-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>The fixture tree with GO in its database, ingested into a scratch store; the bundle it wrote.</summary>
    private BundleRef IngestWithGo()
    {
        CopyTree(Path.Combine(RepoRoot(), "tests", "data"), Path.Combine(_root, "data"));
        var db = Path.Combine(WorkRoot, "db", "test_human.xml");
        var before = BundleWriter.Sha256File(db);
        var text = File.ReadAllText(db, Encoding.UTF8);
        foreach (var (accession, refs) in GoReferences)
        {
            var anchor = $"<accession>{accession}</accession>\n";
            Assert.That(text, Does.Contain(anchor), accession);
            var xml = string.Concat(refs.Select(r =>
                $"  <dbReference type=\"GO\" id=\"{r.Id}\">\n    <property type=\"term\" value=\"{r.Term}\"/>\n"
                + $"    <property type=\"evidence\" value=\"{r.Evidence}\"/>\n  </dbReference>\n"));
            text = text.Replace(anchor, anchor + xml, StringComparison.Ordinal);
        }
        File.WriteAllBytes(db, Encoding.UTF8.GetBytes(text));
        var provenance = Path.Combine(WorkRoot, "run_test", "PXD999999", "04_search", "provenance.json");
        var recorded = File.ReadAllText(provenance, Encoding.UTF8);
        Assert.That(recorded, Does.Contain(before));
        File.WriteAllBytes(provenance, Encoding.UTF8.GetBytes(recorded.Replace(before, BundleWriter.Sha256File(db), StringComparison.Ordinal)));

        var result = Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        return BundleRef.Load(result.BundlePath);
    }

    private static List<Dictionary<string, object?>> Rows(ArtefactRef artefact, string table) =>
        ArrowTables.ReadParquet(artefact.TablePath(table)!).Rows.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();

    private static IReadOnlyDictionary<string, object?> Summary(ArtefactRef artefact) =>
        (IReadOnlyDictionary<string, object?>)artefact.Record["engine_summary"]!;

    [Test]
    public void OneBundleGivesOneArtefactWithGosFilesAndTheRowsGosReaderBuilds()
    {
        var bundle = IngestWithGo();
        var result = GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall);
        var artefact = result.Written.Single();
        Assert.That(result.AlreadyDone, Is.Empty);
        Assert.That(artefact.Engine, Is.EqualTo("go.annotate_groups"));
        Assert.That(Directory.EnumerateFiles(artefact.Path).Select(Path.GetFileName).Order(),
            Is.EqualTo(new[] { "annotation_sources.parquet", "go_annotation.tsv", "go_category.tsv", "organelle_term_categories.parquet", "protein_localizations.parquet", "run.json" }));

        // The record: everything hashed, plus what was not.
        var record = artefact.Record;
        Assert.That(record["definition_id"], Is.EqualTo(FakeDefinition));
        Assert.That(record["acceptance"], Is.EqualTo("passed"));
        Assert.That(artefact.Inputs.Keys.Order(), Is.EqualTo(new[] { "annotation_database:test_human.xml", "category_map", "ontology", "protein_groups" }),
            "the contaminant panel is not annotation input");
        Assert.That(result.SkippedContaminant, Is.EqualTo(new[] { "test_contaminants.xml" }));
        var summary = Summary(artefact);
        Assert.That(summary["groups"], Is.EqualTo(4L), "every non-decoy group, the q = 0.5 one included");
        Assert.That(PyFormat.Json(summary["group_status_counts"], sortKeys: true),
            Is.EqualTo("{\"annotated\": 2, \"contaminant\": 1, \"no_entry\": 1, \"no_go_terms\": 0}"));
        Assert.That((IEnumerable<object?>)summary["unresolved_go_ids"]!, Is.EqualTo(new object?[] { Unknown }), "skipped and listed (go D35)");
        Assert.That(summary["mzlib_release"], Is.EqualTo(GoEngine.Release()["mzlib"]), "a released mzLib wrote the files");
        Assert.That(summary["annotation_db_sha256"], Is.EqualTo(artefact.Inputs["annotation_database:test_human.xml"]),
            "one database: its own sha256, as go's own run wrote it");

        // go's files are kept in go's format, and read back clean (the acceptance check, without allowPrerelease).
        var annotation = Go.ReadAnnotation(Path.Combine(artefact.Path, GoEngine.AnnotationFile));
        var categories = Go.ReadCategories(Path.Combine(artefact.Path, GoEngine.CategoryFile));
        Go.CheckCoverage(annotation, categories);
        Assert.That(annotation.Header["unresolved_go_ids"], Is.EqualTo("1"));
        Assert.That(annotation.Header["source_file_sha256"], Is.EqualTo(artefact.Inputs["protein_groups"]));
        Assert.That(categories.MapName, Is.EqualTo("fixture"));

        // The stored rows are exactly the reader's.
        var localizations = Rows(artefact, "protein_localizations");
        Assert.That(PyFormat.Json(localizations, sortKeys: false), Is.EqualTo(PyFormat.Json(Go.LocalizationRows(annotation), sortKeys: false)));
        var direct = localizations.Single(r => (string)r["protein_accession"]! == "P05141" && (string)r["compartment"]! == InnerMembrane);
        Assert.That((direct["propagated"], direct["inherited"], direct["n_members"], direct["n_with"], direct["evidence"]),
            Is.EqualTo(((object?)false, (object?)false, (object?)1L, (object?)1L, (object?)"ECO:0000314")));
        var up = localizations.Single(r => (string)r["protein_accession"]! == "P05141" && (string)r["compartment"]! == Mitochondrion);
        Assert.That(up["propagated"], Is.EqualTo(true), "mitochondrion is reached by part_of propagation");
        var tubulin = localizations.Single(r => (string)r["protein_accession"]! == "P68363" && (string)r["compartment"]! == Cytoplasm);
        Assert.That((tubulin["protein_group"], tubulin["n_members"], tubulin["n_with"]), Is.EqualTo(((object?)"P68363|Q71U36", (object?)2L, (object?)1L)));
        Assert.That(localizations.Any(r => (string)r["protein_accession"]! is "Q71U36" or "P99999" or "CONTAM_P00001"), Is.False,
            "no term, no row: a dropped unknown id, a missing entry and a contaminant");
        Assert.That(localizations.Any(r => (string)r["compartment"]! == Gapdh), Is.False, "molecular function is not a localization");

        var categoryRows = Rows(artefact, "organelle_term_categories");
        Assert.That(categoryRows.Single(r => (string)r["compartment"]! == InnerMembrane)["organelle_subcategory"], Is.EqualTo("mitochondrion:inner_membrane"));
        Assert.That(categoryRows.Single(r => (string)r["compartment"]! == Mitochondrion)["organelle_category"], Is.EqualTo("mitochondrion"));
        Assert.That(categoryRows.Any(r => (string)r["compartment"]! == Cytoplasm), Is.False, "cytoplasm is under no anchor of the map");
        var source = Rows(artefact, "annotation_sources").Single();
        Assert.That((source["owner_project"], source["source_id"]), Is.EqualTo(((object?)"go", (object?)Go.SourceId(annotation))));
    }

    [Test]
    public void TheSameInputsAreAlreadyDone()
    {
        var bundle = IngestWithGo();
        var first = GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall).Written.Single();
        var again = GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall);
        Assert.That(again.Written, Is.Empty);
        Assert.That(again.AlreadyDone.Single().ArtefactId, Is.EqualTo(first.ArtefactId));
        // Another definition id is another artefact.
        var other = GoEngine.Run(Store, [bundle], Inputs, FakeDefinition + " v2", StandInInstall).Written.Single();
        Assert.That(other.ArtefactId, Is.Not.EqualTo(first.ArtefactId));
    }

    [Test]
    public void WithoutADefinitionIdNothingRuns()
    {
        Assert.That(() => GoEngine.Run(Store, [], Inputs, null, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("no definition id was given").And.Message.Contains("could not be cited"));
        Assert.That(() => GoEngine.Run(Store, [], Inputs, "", StandInInstall), Throws.TypeOf<RunnerException>());
        Assert.That(Directory.Exists(Path.Combine(Store, DataRepo.Catalog.Runner.EngineDir)), Is.False, "nothing was written");
    }

    [Test]
    public void TheEngineRunsUnderGosPublishedDefinition()
    {
        // go 023 (GO-D4, go D40): the id is go's, copied verbatim, never one dataRepo made up (D24).
        Assert.That(GoEngine.DefinitionId, Is.EqualTo("go:DEF-GROUP-GO-ANNOTATION v1"));
        // The CLI passes it; the run gets past the definition check (this tree is a development build, which the
        // runner refuses next, by design: aging 063).
        IngestWithGo();
        Assert.That(() => RunCommand.Run(["go.annotate_groups", "PXD999999", "--store", Store,
                "--input", $"ontology={Ontology}", "--input", $"category_map={Map}"]),
            Throws.TypeOf<RunnerException>().With.Message.Not.Contains("no definition id"));
    }

    [Test]
    public void AnInputThatNoLongerHashesToTheBundlesRecordIsRefused()
    {
        var bundle = IngestWithGo();
        var groups = GoEngine.InputsOf(bundle).ProteinGroupsPath;
        File.AppendAllText(groups, "\n");
        Assert.That(() => GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("now hashes to").And.Message.Contains("AllQuantifiedProteinGroups.tsv"));
        Assert.That(DataRepo.Catalog.Runner.DiscoverArtefacts(Store), Is.Empty);
    }

    [Test]
    public void AMissingOrUnknownRoleIsRefusedBeforeAnythingRuns()
    {
        Assert.That(() => GoEngine.Run(Store, [], new Dictionary<string, string> { ["ontology"] = Ontology }, FakeDefinition, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("needs --input category_map=<path>"));
        Assert.That(() => GoEngine.Run(Store, [], new Dictionary<string, string>(Inputs) { ["protein_groups"] = Map }, FakeDefinition, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("not protein_groups").And.Message.Contains("each bundle's own record"));
    }

    [Test]
    public void ADevelopmentBuildIsRefused()
    {
        Assume.That(BundleWriter.PackageVersion, Does.Contain("-dev"));
        var bundle = IngestWithGo();
        Assert.That(() => GoEngine.Run(Store, [bundle], Inputs, FakeDefinition),
            Throws.TypeOf<RunnerException>().With.Message.Contains("development build"));
    }

    [Test]
    public void TheCatalogLoadsTheArtefactUnderItsBundleAndSaysWhichBundlesLackOne()
    {
        var bundle = IngestWithGo();
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);

        // No artefact yet: the catalog builds without go rows, and says so.
        var (none, noneChecks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        Assert.That(none, Is.Empty);
        var coverage = noneChecks.Single(c => c.Name.StartsWith("go.annotate_groups coverage", StringComparison.Ordinal));
        Assert.That((coverage.Ok, coverage.Observed, coverage.Expected), Is.EqualTo((true, (long?)0, (long?)1)));
        Assert.That(coverage.Detail, Does.Contain($"no artefact for PXD999999 ({bundle.BundleId})"));
        var without = CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, "without.duckdb"), instance: manifest.Instance, artefacts: none, engineChecks: noneChecks);

        var artefact = GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall).Written.Single();
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        Assert.That(artefacts.Single().ArtefactId, Is.EqualTo(artefact.ArtefactId));
        var output = Path.Combine(_root, "with.duckdb");
        var built = CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance, artefacts: artefacts, engineChecks: checks);
        Assert.That(built.FailedChecks, Is.Empty);
        Assert.That(built.CatalogId, Is.Not.EqualTo(without.CatalogId), "the catalog id names the artefact it serves");
        Assert.That(built.Checks.Single(c => c.Name.StartsWith("go.annotate_groups coverage", StringComparison.Ordinal)).Observed, Is.EqualTo(1));
        Assert.That(built.Checks.Count(c => c.Name.StartsWith($"go.annotate_groups/{artefact.ArtefactId}/", StringComparison.Ordinal) && c.Ok), Is.EqualTo(3));

        foreach (var table in DataRepo.Catalog.Runner.GoTables)
        {
            var rows = CatalogBuilder.RunQuery(output, $"SELECT dataset_id, bundle_id, count(*) FROM \"{table}\" GROUP BY ALL").Rows;
            Assert.That(rows.Single(), Is.EqualTo(new object?[] { "PXD999999", bundle.BundleId, artefact.RowCounts[table] }), table);
            var kind = CatalogBuilder.RunQuery(output, $"SELECT kind, rows FROM catalog_tables WHERE table_name = '{table}'").Rows.Single();
            Assert.That(kind, Is.EqualTo(new object?[] { "engine:go.annotate_groups", artefact.RowCounts[table] }), table);
        }
        var recorded = CatalogBuilder.RunQuery(output, "SELECT engine, artefact_id, definition_id FROM catalog_engine_artefacts").Rows.Single();
        Assert.That(recorded, Is.EqualTo(new object?[] { "go.annotate_groups", artefact.ArtefactId, FakeDefinition }));
        Assert.That(CatalogBuilder.RunQuery(output, "SELECT catalog_version FROM catalog_meta").Rows.Single()[0], Is.EqualTo("10"));
        // The join a consumer makes: a protein's compartment to the map's category, within one dataset and release.
        var joined = CatalogBuilder.RunQuery(output,
            "SELECT DISTINCT c.organelle_category FROM protein_localizations l JOIN organelle_term_categories c "
            + "ON c.dataset_id = l.dataset_id AND c.compartment = l.compartment AND c.go_release = l.go_release "
            + "WHERE l.protein_accession = 'P05141' ORDER BY 1").Rows;
        Assert.That(joined.Select(r => r[0]), Is.EqualTo(new object?[] { "mitochondrion" }));
    }

    [Test]
    public void TwoArtefactsForOneBundleAreRefusedNotPicked()
    {
        var bundle = IngestWithGo();
        GoEngine.Run(Store, [bundle], Inputs, FakeDefinition, StandInInstall);
        GoEngine.Run(Store, [bundle], Inputs, FakeDefinition + " v2", StandInInstall);
        var bundles = CatalogBuilder.SelectBundles(Manifest.Load(ManifestPath), ["PXD999999"], Store);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, bundles),
            Throws.TypeOf<CatalogException>().With.Message.Contains("2 artefacts").And.Message.Contains("A catalog serves one"));
    }

    private static ArtefactRef Annotated(string id, string ontology, string map) => new("unused", new Dictionary<string, object?>
    {
        ["engine"] = GoEngine.Engine, ["artefact_id"] = id,
        ["inputs"] = new Dictionary<string, object?> { ["ontology"] = ontology, ["category_map"] = map, ["protein_groups"] = id },
    });

    [TestCase("ontology")]
    [TestCase("category_map")]
    public void ACatalogServesOneOntologyAndOneMap(string differing)
    {
        // go GO-D9 (go 024): a category compared across datasets must come from one go.obo and one map.
        var a = Annotated("a", "obo1", "map1");
        var b = differing == "ontology" ? Annotated("b", "obo2", "map1") : Annotated("b", "obo1", "map2");
        CatalogBuilder.RequireOneOntologyAndMap(Store, [("PXD1", a), ("PXD2", Annotated("c", "obo1", "map1"))]);
        Assert.That(() => CatalogBuilder.RequireOneOntologyAndMap(Store, [("PXD1", a), ("PXD2", b)]),
            Throws.TypeOf<CatalogException>().With.Message.Contains($"2 different {differing} files")
                .And.Message.Contains(": PXD1").And.Message.Contains(": PXD2").And.Message.Contains("GO-D9"));
    }
}
