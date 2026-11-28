using System.Globalization;
using System.Text;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.DataCli;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;
using PXReprise.Runner;
using Readers;
using UsefulProteomicsDatabases.GeneOntology;

namespace PXReprise.Data.Tests;

/// <summary>go's three engines (<c>go.annotate_databases</c>, <c>go.categorize_terms</c>, <c>go.resolve_members</c>) on
/// a stored search, mzLib's files read back as the acceptance check, and the catalog joining them into the rows
/// <c>go.annotate_groups</c> stored per bundle up to 1.4.5.</summary>
/// <remarks>
/// <para>The fixture dataset is PXD999999 (<c>tests/datarepo-instance</c>), ingested here with the current rules. Its
/// searched database carries no GO, so each test works on a scratch copy of the fixture tree whose <c>test_human.xml</c>
/// has GO references added to three entries (<see cref="GoReferences"/>), with the search provenance's sha256 for that
/// file updated to match. The ontology is mzLib's trimmed go.obo and the map a three-row one
/// (<c>Fixtures/go-engine/PROVENANCE.md</c>).</para>
/// <para>The definition ids are <see cref="FakeDefinition"/>s: the engines refuse a call without one, and the CLI passes
/// go's.</para>
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
    private static Dictionary<string, string> OntologyAndMap => new() { ["ontology"] = Ontology, ["category_map"] = Map };
    private static Dictionary<string, string> OntologyOnly => new() { ["ontology"] = Ontology };
    private static readonly Dictionary<string, string> NoInputs = new();

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
        CopyTree(DataPaths.Instance, Path.Combine(_root, "data"));
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

    /// <summary>The three engines on the bundle, under one definition id.</summary>
    private (ArtefactRef Ontology, ArtefactRef Database, ArtefactRef Members) RunAll(BundleRef bundle, string definition = FakeDefinition) =>
    (
        GoReferenceEngines.CategorizeTermsRun(Store, OntologyAndMap, definition, StandInInstall).Written.Single(),
        GoReferenceEngines.AnnotateDatabasesRun(Store, [bundle], OntologyOnly, definition, StandInInstall).Written.Single(),
        GoReferenceEngines.ResolveMembersRun(Store, [bundle], NoInputs, definition, StandInInstall).Written.Single()
    );

    private static List<Dictionary<string, object?>> Rows(ArtefactRef artefact, string table) =>
        ArrowTables.ReadParquet(artefact.TablePath(table)!).Rows.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();

    private static IReadOnlyDictionary<string, object?> Summary(ArtefactRef artefact) =>
        (IReadOnlyDictionary<string, object?>)artefact.Record["engine_summary"]!;

    private static string[] Files(ArtefactRef artefact) => Directory.EnumerateFiles(artefact.Path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    /// <summary>mzLib's files as <c>run.json</c> records them: name, row count and whether the artefact keeps it; each
    /// sha256 is 64 hex digits, and a kept file hashes to it.</summary>
    private static string[] RecordedFiles(ArtefactRef artefact)
    {
        var files = (IReadOnlyDictionary<string, object?>)artefact.Record["files"]!;
        foreach (var (name, value) in files)
        {
            var sha = (string)((IReadOnlyDictionary<string, object?>)value!)["sha256"]!;
            Assert.That(sha, Does.Match("^[0-9a-f]{64}$"), name);
            if (File.Exists(Path.Combine(artefact.Path, name))) Assert.That(BundleWriter.Sha256File(Path.Combine(artefact.Path, name)), Is.EqualTo(sha), name);
        }
        return files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        {
            var f = (IReadOnlyDictionary<string, object?>)kv.Value!;
            return $"{kv.Key} {f["rows"]} {((bool)f["kept"]! ? "kept" : "dropped")}";
        }).ToArray();
    }

    // --- the engines -------------------------------------------------------------------------------------

    [Test]
    public void EachEngineWritesMzLibsFileAndTheRowsMzLibsReaderGives()
    {
        var bundle = IngestWithGo();
        var (ontology, database, members) = RunAll(bundle);
        var search = CatalogBuilder.GoInputsOf(bundle);
        var human = search.Targets.Single();
        Assert.That(human.Name, Is.EqualTo("test_human.xml"));

        Assert.Multiple(() =>
        {
            // (b) and the ontology: once per (go.obo, map), no bundle.
            Assert.That(ontology.Engine, Is.EqualTo("go.categorize_terms"));
            Assert.That(Files(ontology), Is.EqualTo(new[] { "go_category.tsv", "go_closure.parquet", "go_term_categories.parquet", "go_terms.parquet", "run.json" }),
                "the category file is kept, as go.annotate_groups kept it; the term and closure files are read back and dropped (G110)");
            var closurePairs = Rows(ontology, "go_closure").Count;
            Assert.That(RecordedFiles(ontology), Is.EqualTo(new[]
            {
                $"go_category.tsv {Rows(ontology, "go_term_categories").Count} kept", $"go_closure.tsv {closurePairs} dropped", "go_terms.tsv 29 dropped",
            }));
            Assert.That(ontology.Inputs.Keys.Order(), Is.EqualTo(new[] { "category_map", "ontology" }));
            Assert.That(Summary(ontology)["go_release"], Is.EqualTo("releases/2026-07-26"));
            Assert.That(Summary(ontology)["terms"], Is.EqualTo(29L));
            Assert.That(Rows(ontology, "go_closure").Count(r => Equals(r["go_id"], r["ancestor_go_id"])), Is.EqualTo(29), "the term itself is always a pair");
            Assert.That(Rows(ontology, "go_term_categories").Single(r => Equals(r["go_id"], InnerMembrane))["subcategory"], Is.EqualTo("mitochondrion:inner_membrane"));

            // (a): one per database FILE; the contaminant panel is never annotation input.
            Assert.That(database.Engine, Is.EqualTo("go.annotate_databases"));
            Assert.That(Files(database), Is.EqualTo(new[] { "go_accession_terms.parquet", "run.json" }));
            Assert.That(RecordedFiles(database), Is.EqualTo(new[] { $"go_accession_terms.tsv {Rows(database, "go_accession_terms").Count} dropped" }));
            Assert.That(database.Inputs, Is.EquivalentTo(new Dictionary<string, string> { ["annotation_database"] = human.Sha256, ["ontology"] = ontology.Inputs["ontology"] }));
            Assert.That((IEnumerable<object?>)Summary(database)["unresolved_go_ids"]!, Is.EqualTo(new object?[] { Unknown }), "skipped and listed (go D35)");
            Assert.That(Summary(database)["mzlib_release"], Is.EqualTo(GoReferenceEngines.Release()["mzlib"]), "a released mzLib wrote the file");
            var terms = Rows(database, "go_accession_terms");
            Assert.That(terms.Select(r => r["accession"]).Distinct().Count(), Is.EqualTo(Summary(database)["entries"]), "every entry has a row");
            Assert.That(terms.Where(r => Equals(r["accession"], "P05141")).Select(r => (r["go_id"], r["aspect"], r["evidence"], r["inherited"])),
                Is.EqualTo(new[] { ((object?)Gapdh, (object?)"molecular_function", (object?)"ECO:0000250", (object?)false), (InnerMembrane, "cellular_component", "ECO:0000314", false) }),
                "direct terms only, all aspects");
            Assert.That(terms.Single(r => Equals(r["accession"], "Q71U36"))["go_id"], Is.Null, "its only term was unknown: one term-less row");
            Assert.That(terms.Any(r => Equals(r["go_id"], Mitochondrion)), Is.False, "ancestors are the closure's, never rows");
            Assert.That(GoReferenceEngines.IsoformOf("P04406-2"), Is.EqualTo("P04406"));
            Assert.That(GoReferenceEngines.IsoformOf("P04406"), Is.Null);
            Assert.That(GoReferenceEngines.IsoformOf("P04406-2_A20T"), Is.Null, "a variant is (c)'s to resolve");

            // (c): one per search, no GO.
            Assert.That(members.Engine, Is.EqualTo("go.resolve_members"));
            Assert.That(Files(members), Is.EqualTo(new[] { "go_group_members.parquet", "run.json" }),
                "no member file: go.annotate_groups kept none for a search either");
            Assert.That(RecordedFiles(members), Is.EqualTo(new[] { $"member_resolution.tsv {Rows(members, "go_group_members").Count} dropped" }));
            Assert.That(members.Inputs.Keys.Order(), Is.EqualTo(new[] { "annotation_database:test_human.xml", "protein_groups" }),
                "the contaminant panel is not annotation input");
            Assert.That(Summary(members)["annotation_db_sha256"], Is.EqualTo(human.Sha256), "one database: its own sha256 (GO-D6)");
            var resolved = Rows(members, "go_group_members").ToDictionary(r => (string)r["accession"]!, r => (string)r["resolution"]!);
            Assert.That(resolved["P05141"], Is.EqualTo("self"));
            Assert.That(resolved["P99999"], Is.EqualTo("no_entry"), "in no database");
            Assert.That(Summary(members)["groups"], Is.EqualTo(4L), "every non-decoy group, the q = 0.5 one included");
        });
    }

    [Test]
    public void TheSameInputsAreAlreadyDoneAndAnotherDefinitionIsAnotherArtefact()
    {
        var bundle = IngestWithGo();
        var first = RunAll(bundle);
        Assert.That(GoReferenceEngines.CategorizeTermsRun(Store, OntologyAndMap, FakeDefinition, StandInInstall).AlreadyDone.Single().ArtefactId, Is.EqualTo(first.Ontology.ArtefactId));
        Assert.That(GoReferenceEngines.AnnotateDatabasesRun(Store, [bundle], OntologyOnly, FakeDefinition, StandInInstall).AlreadyDone.Single().ArtefactId, Is.EqualTo(first.Database.ArtefactId));
        Assert.That(GoReferenceEngines.ResolveMembersRun(Store, [bundle], NoInputs, FakeDefinition, StandInInstall).AlreadyDone.Single().ArtefactId, Is.EqualTo(first.Members.ArtefactId));
        var other = RunAll(bundle, FakeDefinition + " v2");
        Assert.That(new[] { other.Ontology, other.Database, other.Members }.Select(a => a.ArtefactId),
            Is.Not.EquivalentTo(new[] { first.Ontology, first.Database, first.Members }.Select(a => a.ArtefactId)));
    }

    [Test]
    public void WithoutADefinitionIdNothingRuns()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => GoReferenceEngines.CategorizeTermsRun(Store, OntologyAndMap, null, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("no definition id was given").And.Message.Contains("could not be cited"));
            Assert.That(() => GoReferenceEngines.AnnotateDatabasesRun(Store, [], OntologyOnly, "", StandInInstall), Throws.TypeOf<RunnerException>());
            Assert.That(() => GoReferenceEngines.ResolveMembersRun(Store, [], NoInputs, null, StandInInstall), Throws.TypeOf<RunnerException>());
        });
        Assert.That(Directory.Exists(Path.Combine(Store, PXReprise.Catalog.Runner.EngineDir)), Is.False, "nothing was written");
    }

    [Test]
    public void TheCliRunsUnderGosDefinitionsAndRefusesTheRetiredEngine()
    {
        Assert.That(GoReferenceEngines.AccessionDefinitionId, Is.EqualTo("go:DEF-ACCESSION-GO-ANNOTATION v1"));
        Assert.That(GoReferenceEngines.CategoryDefinitionId, Is.EqualTo("go:DEF-GROUP-GO-ANNOTATION v1"));
        Assert.That(EngineRunner.Engines, Does.Not.Contain("go.annotate_groups"));
        IngestWithGo();
        Assert.That(() => RunCommand.Run(["go.annotate_groups", "PXD999999", "--store", Store]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("retired").And.Message.Contains("go.annotate_databases")
                .And.Message.Contains("go.categorize_terms").And.Message.Contains("go.resolve_members"));
        // The CLI passes go's ids; each run gets past the definition check (this tree is a development build, which the
        // runner refuses next, by design: aging 063).
        foreach (var argv in new[]
                 {
                     new[] { "go.categorize_terms", "--store", Store, "--input", $"ontology={Ontology}", "--input", $"category_map={Map}" },
                     new[] { "go.annotate_databases", "PXD999999", "--store", Store, "--input", $"ontology={Ontology}" },
                     new[] { "go.resolve_members", "PXD999999", "--store", Store },
                 })
            Assert.That(() => RunCommand.Run(argv), Throws.TypeOf<RunnerException>().With.Message.Contains("development build"), argv[0]);
        Assert.That(() => RunCommand.Run(["go.categorize_terms", "PXD999999", "--store", Store, "--input", $"ontology={Ontology}", "--input", $"category_map={Map}"]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("takes no accession"));
    }

    [Test]
    public void AnInputThatNoLongerHashesToTheBundlesRecordIsRefused()
    {
        var bundle = IngestWithGo();
        var search = CatalogBuilder.GoInputsOf(bundle);
        File.AppendAllText(search.ProteinGroupsPath!, "\n");
        Assert.That(() => GoReferenceEngines.ResolveMembersRun(Store, [bundle], NoInputs, FakeDefinition, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("now hashes to").And.Message.Contains("AllQuantifiedProteinGroups.tsv"));
        File.AppendAllText(search.Targets.Single().Path, "\n");
        Assert.That(() => GoReferenceEngines.AnnotateDatabasesRun(Store, [bundle], OntologyOnly, FakeDefinition, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("now hashes to").And.Message.Contains("test_human.xml"));
        Assert.That(PXReprise.Catalog.Runner.DiscoverArtefacts(Store), Is.Empty);
    }

    [Test]
    public void AMissingOrUnknownRoleIsRefusedBeforeAnythingRuns()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => GoReferenceEngines.CategorizeTermsRun(Store, OntologyOnly, FakeDefinition, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("needs --input category_map=<path>"));
            Assert.That(() => GoReferenceEngines.AnnotateDatabasesRun(Store, [], OntologyAndMap, FakeDefinition, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("not category_map").And.Message.Contains("each bundle's own record"));
            Assert.That(() => GoReferenceEngines.ResolveMembersRun(Store, [], OntologyOnly, FakeDefinition, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("takes no --input"));
        });
    }

    [Test]
    public void ADevelopmentBuildIsRefused()
    {
        Assume.That(BundleWriter.PackageVersion, Does.Contain("-dev"));
        var bundle = IngestWithGo();
        Assert.That(() => GoReferenceEngines.ResolveMembersRun(Store, [bundle], NoInputs, FakeDefinition),
            Throws.TypeOf<RunnerException>().With.Message.Contains("development build"));
    }

    // --- the catalog -------------------------------------------------------------------------------------

    private List<BundleRef> Bundles() => CatalogBuilder.SelectBundles(Manifest.Load(ManifestPath), ["PXD999999"], Store);

    private CatalogResult Build(string output)
    {
        var bundles = Bundles();
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, output, instance: Manifest.Load(ManifestPath).Instance, artefacts: artefacts, engineChecks: checks);
    }

    private static List<string> Rows(string catalog, string sql) =>
        CatalogBuilder.RunQuery(catalog, sql).Rows.Select(r => string.Join("|", r.Select(v => v switch
        {
            null => "NULL",
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        }))).ToList();

    private static readonly string[] AllGoTables = [.. CatalogBuilder.GoReferenceTables, .. PXReprise.Catalog.Runner.GoTables];
    private static readonly string GoTableList = string.Join(", ", AllGoTables.Select(t => $"'{t}'"));

    [Test]
    public void WithNoArtefactsTheGoTablesAreThereEmptyAndTheCoverageSaysWhy()
    {
        IngestWithGo();
        foreach (var output in new[] { Path.Combine(_root, "catalog.duckdb"), Path.Combine(_root, "catalogs") })
        {
            var built = Build(output);
            Assert.Multiple(() =>
            {
                Assert.That(Rows(built.Path, $"SELECT table_name, rows, kind FROM catalog_tables WHERE table_name IN ({GoTableList}) ORDER BY table_name"),
                    Is.EqualTo(new[]
                    {
                        "annotation_sources|0|derived",
                        "go_accession_terms|0|engine:go.annotate_databases",
                        "go_closure|0|engine:go.categorize_terms",
                        "go_group_members|0|engine:go.resolve_members",
                        "go_group_status|0|derived",
                        "go_term_categories|0|engine:go.categorize_terms",
                        "go_terms|0|engine:go.categorize_terms",
                        "organelle_term_categories|0|derived",
                        "protein_localizations|0|derived",
                    }), output);
                foreach (var table in CatalogBuilder.GoReferenceTables)
                    Assert.That(Rows(built.Path, $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"),
                        Is.EquivalentTo(CatalogBuilder.DerivedDocs[table].Columns.Keys), $"{table}: every column is documented, and only those");
                foreach (var table in PXReprise.Catalog.Runner.GoTables)
                    Assert.That(Rows(built.Path, $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"),
                        Is.EqualTo(new[] { "dataset_id", "bundle_id" }.Concat(SchemaContract.Columns(Tables.ByName[table]).Select(c => c.Name).Where(c => c != "dataset_id"))),
                        $"{table}: its core columns, as up to 1.4.5");
                var notes = built.Checks.Where(c => c.Name.StartsWith("go.", StringComparison.Ordinal)).Select(c => $"{c.Name}: {c.Observed} of {c.Expected}; {c.Detail}").ToList();
                Assert.That(notes, Is.EqualTo(new[]
                {
                    "go.categorize_terms (ontology and category map served): 0 of 1; no artefact, so no GO is served",
                    $"go.annotate_databases coverage (target databases with an artefact): 0 of 1; none served without an ontology; no artefact for {CatalogBuilder.GoInputsOf(Bundles()[0]).Targets[0].Sha256[..12]} (PXD999999)",
                    $"go.resolve_members coverage (bundles served GO): 0 of 1; not served: PXD999999 ({Bundles()[0].BundleId}): no go.resolve_members artefact",
                }));
                Assert.That(built.Checks.Where(c => c.Name.StartsWith("go.", StringComparison.Ordinal)).All(c => c.Ok));
            });
        }
    }

    /// <summary>The rows <c>go.annotate_groups</c> stored for this bundle: mzLib's annotator over the bundle's own files,
    /// read back by go's reader (<see cref="Go"/>), without the source id (which named the per-bundle file).</summary>
    private (List<string> Localizations, List<string> Categories, Dictionary<string, string> Header) V1(BundleRef bundle)
    {
        var search = CatalogBuilder.GoInputsOf(bundle);
        var file = new ProteinGroupFromTsvFile(search.ProteinGroupsPath!);
        file.LoadResults();
        var groups = file.Results.ToGoAnnotationGroups().ToList();
        var ontology = GeneOntologyGraph.Load(Ontology);
        var annotator = new GoGroupAnnotator(ontology, Specificity.LoadProteins(search.Targets.Select(t => (t.Path, t.Sha256, false))),
            search.AnnotationDbSha256!, skipUnknownGoIds: true);
        var rows = annotator.AnnotateAll(groups);
        var (annotationPath, categoryPath) = (Path.Combine(_root, "v1_annotation.tsv"), Path.Combine(_root, "v1_category.tsv"));
        using (var w = new StreamWriter(annotationPath, false, new UTF8Encoding(false)))
            GoAnnotationTsv.Write(w, rows, ontology, search.AnnotationDbSha256!, search.ProteinGroupsSha256, annotator.UnresolvedGoIds);
        using (var w = new StreamWriter(categoryPath, false, new UTF8Encoding(false)))
            GoCategoryTsv.Write(w, new GoCategoryResolver(GoCategoryMap.Load(Map), ontology), rows);
        var annotation = Go.ReadAnnotation(annotationPath);
        var categories = Go.ReadCategories(categoryPath);
        static string Cells(IReadOnlyDictionary<string, object?> row, IEnumerable<string> columns) => string.Join("|", columns.Select(c => row.GetValueOrDefault(c) switch
        {
            null => "NULL",
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            var v => Convert.ToString(v, CultureInfo.InvariantCulture),
        }));
        return (Go.LocalizationRows(annotation).Select(r => Cells(r, LocalizationColumns)).Order(StringComparer.Ordinal).ToList(),
            Go.CategoryRows(categories, annotation).Select(r => Cells(r, CategoryColumns)).Order(StringComparer.Ordinal).ToList(),
            annotation.Header);
    }

    private static readonly string[] LocalizationColumns =
        ["protein_accession", "compartment", "go_release", "evidence", "protein_group", "q_value", "n_members", "n_with", "inherited", "propagated"];

    private static readonly string[] CategoryColumns =
        ["compartment", "category_map_name", "organelle_map_version", "go_release", "organelle_category", "organelle_subcategory"];

    [Test]
    public void TheCatalogJoinsTheThreeEnginesIntoV1sRowsInBothLayouts()
    {
        var bundle = IngestWithGo();
        var without = Build(Path.Combine(_root, "without.duckdb"));
        var (ontology, database, members) = RunAll(bundle);
        var (localizations, categories, header) = V1(bundle);
        Assert.That(localizations, Has.Count.GreaterThan(5));
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Path.Combine(_root, "catalogs"));
        Assert.That(folder.Layout, Is.EqualTo(CatalogLayout.Folder));
        Assert.That(file.CatalogId, Is.Not.EqualTo(without.CatalogId), "the catalog id names the artefacts it serves");
        foreach (var built in new[] { file, folder })
            Assert.Multiple(() =>
            {
                Assert.That(built.FailedChecks, Is.Empty);
                Assert.That(Rows(built.Path, $"SELECT {string.Join(", ", LocalizationColumns)} FROM protein_localizations ORDER BY ALL"), Is.EqualTo(localizations),
                    "protein_localizations: go.annotate_groups' rows for this search");
                Assert.That(Rows(built.Path, $"SELECT {string.Join(", ", CategoryColumns)} FROM organelle_term_categories ORDER BY ALL"), Is.EqualTo(categories),
                    "organelle_term_categories: the map's rows for the terms its groups carry");
                Assert.That(Rows(built.Path, "SELECT DISTINCT dataset_id, bundle_id FROM protein_localizations"), Is.EqualTo(new[] { $"PXD999999|{bundle.BundleId}" }));
                var source = Rows(built.Path, "SELECT dataset_id, owner_project, source_id FROM annotation_sources").Single();
                Assert.That(source, Does.StartWith("PXD999999|go|go:releases/2026-07-26:"));
                Assert.That(Rows(built.Path, "SELECT DISTINCT source_id FROM protein_localizations UNION SELECT DISTINCT source_id FROM organelle_term_categories"),
                    Is.EqualTo(new[] { source.Split('|')[2] }), "every row cites the search's source");
                Assert.That(Rows(built.Path, "SELECT annotation_status, count(*) FROM go_group_status GROUP BY ALL ORDER BY 1"),
                    Is.EqualTo(new[] { "annotated|2", "contaminant|1", "no_entry|1" }), "every non-decoy group once, with go's status");
                foreach (var key in new[] { "status_annotated", "status_contaminant", "status_no_entry", "status_no_go_terms" })
                    Assert.That(Rows(built.Path, $"SELECT count(*) FROM go_group_status WHERE q_value <= 0.01 AND annotation_status = '{key["status_".Length..]}'").Single(),
                        Is.EqualTo(header[key]), $"go D26 counter {key}");
                Assert.That(Rows(built.Path, "SELECT engine, artefact_id FROM catalog_engine_artefacts WHERE engine LIKE 'go.%' ORDER BY 1"), Is.EqualTo(new[]
                {
                    $"go.annotate_databases|{database.ArtefactId}", $"go.categorize_terms|{ontology.ArtefactId}", $"go.resolve_members|{members.ArtefactId}",
                }));
                Assert.That(Rows(built.Path, $"SELECT table_name, kind FROM catalog_tables WHERE table_name IN ({GoTableList}) AND rows > 0 ORDER BY 1"), Is.EqualTo(new[]
                {
                    "annotation_sources|derived", "go_accession_terms|engine:go.annotate_databases", "go_closure|engine:go.categorize_terms",
                    "go_group_members|engine:go.resolve_members", "go_group_status|derived", "go_term_categories|engine:go.categorize_terms",
                    "go_terms|engine:go.categorize_terms", "organelle_term_categories|derived", "protein_localizations|derived",
                }));
                Assert.That(Rows(built.Path, $"SELECT table_name, table_type FROM information_schema.tables WHERE table_name IN ({GoTableList}) AND table_type <> 'BASE TABLE'"),
                    Is.Empty, "copied and derived in both layouts");
                Assert.That(built.Checks.Where(c => c.Kind == "engine-consistency" && c.Name.StartsWith("go.", StringComparison.Ordinal)).Select(c => c.Name.Split(' ')[0] + " " + c.Name.Split(' ')[1]),
                    Is.EquivalentTo(new[] { "go.annotate_databases go.obo", "go.resolve_members members", "go.resolve_members entries" }));
                Assert.That(built.Checks.Count(c => c.Kind == "row_count" && c.Name.StartsWith("go.", StringComparison.Ordinal)), Is.EqualTo(5),
                    "go_terms, go_closure, go_term_categories, go_accession_terms and go_group_members, each whole");
                Assert.That(Rows(built.Path, "SELECT catalog_version FROM catalog_meta").Single(), Is.EqualTo("18"));
                // The join a consumer makes: a protein's compartment to the map's category, within one dataset and release.
                Assert.That(Rows(built.Path,
                    "SELECT DISTINCT c.organelle_category FROM protein_localizations l JOIN organelle_term_categories c "
                    + "ON c.dataset_id = l.dataset_id AND c.compartment = l.compartment AND c.go_release = l.go_release "
                    + "WHERE l.protein_accession = 'P05141' ORDER BY 1"), Is.EqualTo(new[] { "mitochondrion" }));
            });
        Assert.That(Rows(file.Path, "SELECT * EXCLUDE (artefact_id) FROM go_accession_terms ORDER BY ALL"),
            Is.EqualTo(Rows(folder.Path, "SELECT * EXCLUDE (artefact_id) FROM go_accession_terms ORDER BY ALL")));

        // The worked GO queries in querying.md run in both layouts; the two about one protein find P05141's rows.
        var documented = DocumentedQueries().Select(sql => sql.Replace("'P04406'", "'P05141'")).ToList();
        Assert.That(documented, Has.Count.EqualTo(5));
        foreach (var built in new[] { file, folder })
            for (var i = 0; i < documented.Count; i++)
            {
                var rows = CatalogBuilder.RunQuery(built.Path, documented[i]).Rows;
                if (i < 2) Assert.That(rows, Is.Not.Empty, documented[i]);
            }
    }

    /// <summary>The SQL blocks of <c>docs/data/querying.md</c> that read go's tables.</summary>
    private static List<string> DocumentedQueries()
    {
        var page = File.ReadAllText(Path.Combine(DataPaths.Docs, "querying.md")).Replace("\r\n", "\n");
        return System.Text.RegularExpressions.Regex.Matches(page, "```sql\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(sql => sql.Contains("go_accession_terms", StringComparison.Ordinal) || sql.Contains("FROM protein_localizations", StringComparison.Ordinal))
            .ToList();
    }

    [Test]
    public void TwoOntologiesOrTwoDatabaseArtefactsAreRefusedNotPicked()
    {
        var bundle = IngestWithGo();
        RunAll(bundle);
        GoReferenceEngines.AnnotateDatabasesRun(Store, [bundle], OntologyOnly, FakeDefinition + " v2", StandInInstall);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, Bundles()),
            Throws.TypeOf<CatalogException>().With.Message.Contains("2 artefacts").And.Message.Contains("A catalog serves one"));
        GoReferenceEngines.CategorizeTermsRun(Store, OntologyAndMap, FakeDefinition + " v2", StandInInstall);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, Bundles()),
            Throws.TypeOf<CatalogException>().With.Message.Contains("go.categorize_terms: 2 artefacts").And.Message.Contains("GO-D9"));
    }

    [Test]
    public void ADatabaseAnnotatedUnderAnotherGoOboIsRefused()
    {
        var bundle = IngestWithGo();
        RunAll(bundle);
        var engineDir = Path.Combine(Store, PXReprise.Catalog.Runner.EngineDir, "go.annotate_databases");
        foreach (var dir in Directory.EnumerateDirectories(engineDir)) Directory.Delete(dir, true);
        var other = Path.Combine(_root, "go-other.obo");
        File.WriteAllText(other, File.ReadAllText(Ontology) + "\n");
        GoReferenceEngines.AnnotateDatabasesRun(Store, [bundle], new Dictionary<string, string> { ["ontology"] = other }, FakeDefinition, StandInInstall);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, Bundles()),
            Throws.TypeOf<CatalogException>().With.Message.Contains("under another go.obo").And.Message.Contains("GO-D9"));
    }

    [Test]
    public void AMemberWithoutAResolutionFailsTheBuild()
    {
        var bundle = IngestWithGo();
        var (_, _, members) = RunAll(bundle);
        // The member artefact loses P05141's row, its record recounted: only dataRepo 028's check can see it.
        var path = members.TablePath("go_group_members")!;
        var kept = Rows(members, "go_group_members").Where(r => !Equals(r["accession"], "P05141"))
            .Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();
        ArrowTables.WriteParquet(ArrowTables.FromRows("go_group_members", GoReferenceEngines.Columns["go_group_members"], kept), path);
        var record = Path.Combine(members.Path, "run.json");
        var text = File.ReadAllText(record);
        File.WriteAllText(record, text.Replace($"\"go_group_members\": {kept.Count + 1}", $"\"go_group_members\": {kept.Count}", StringComparison.Ordinal));
        Assert.That(File.ReadAllText(record), Is.Not.EqualTo(text));
        Assert.That(() => Build(Path.Combine(_root, "catalog.duckdb")),
            Throws.TypeOf<CatalogException>().With.Message.Contains("go.resolve_members members").And.Message.Contains("PXD999999: P05141"));
    }

    [Test]
    public void ABundleWithoutItsDatabaseArtefactIsNamedAndServedNoGo()
    {
        var bundle = IngestWithGo();
        RunAll(bundle);
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(Store, PXReprise.Catalog.Runner.EngineDir, "go.annotate_databases"))) Directory.Delete(dir, true);
        // A retired per-bundle artefact in the store is counted and never served.
        var retired = Path.Combine(Store, PXReprise.Catalog.Runner.EngineDir, "go.annotate_groups", "0123456789abcdef");
        Directory.CreateDirectory(retired);
        File.WriteAllText(Path.Combine(retired, "run.json"), "{\"engine\": \"go.annotate_groups\", \"artefact_id\": \"0123456789abcdef\", \"schema_version\": \"0.0.17\"}");
        var built = Build(Path.Combine(_root, "catalog.duckdb"));
        Assert.That(built.FailedChecks, Is.Empty);
        Assert.That(built.Checks.Single(c => c.Name.StartsWith("go.resolve_members coverage", StringComparison.Ordinal)).Detail,
            Is.EqualTo($"not served: PXD999999 ({bundle.BundleId}): no go.annotate_databases artefact for test_human.xml; "
                + "1 go.annotate_groups artefact(s) in the store are retired and never served"));
        Assert.That(Rows(built.Path, "SELECT count(*) FROM protein_localizations").Single(), Is.EqualTo("0"));
        Assert.That(Rows(built.Path, "SELECT count(*) FROM go_group_members").Single(), Is.EqualTo("0"), "a search not served is not half-served");
        Assert.That(Rows(built.Path, "SELECT count(*) FROM go_terms").Single(), Is.EqualTo("29"), "the ontology is served");
        Assert.That(Rows(built.Path, "SELECT count(*) FROM catalog_engine_artefacts WHERE engine = 'go.annotate_groups'").Single(), Is.EqualTo("0"));
    }
}
