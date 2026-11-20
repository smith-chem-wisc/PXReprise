using System.Globalization;
using System.Text;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.DataCli;
using PXReprise.Ingest;
using PXReprise.Runner;
using Proteomics;
using UsefulProteomicsDatabases.Pathways;

namespace PXReprise.Data.Tests;

/// <summary>kegg's two engines (<c>kegg.reactome_membership</c>, <c>kegg.reactome_release</c>) on a stored search, mzLib's
/// files read back as the acceptance check, and the catalog joining them, with go's member resolution, into
/// <c>protein_reactome</c>, <c>reactome_group_status</c> and <c>reactome_group_pathways</c>.</summary>
/// <remarks>
/// <para>The fixture dataset is PXD999999 (<c>tests/datarepo-instance</c>), ingested here with the current rules. Its
/// searched database names no organism and carries no Reactome, so each test works on a scratch copy of the fixture tree
/// whose <c>test_human.xml</c> gives eight entries the human taxon and seven of them Reactome and KEGG references
/// (<see cref="References"/>), with the search provenance's sha256 for that file updated to match. 12 of the 13 Reactome
/// references are HSA, so the file reads human's code and drops the one MMU id; one id is absent from the release. The
/// release is mzLib's trimmed release 96 (<c>Fixtures/reactome/PROVENANCE.md</c>), passed as a release of the tests' own.</para>
/// <para>The definition ids are <see cref="FakeDefinition"/>s: the engines refuse a call without one, and the CLI passes
/// kegg's.</para>
/// </remarks>
public class ReactomeEngineTests
{
    private const string FakeDefinition = "test:DEF-REACTOME-FIXTURE";

    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    private const string Drift = "R-HSA-9999999";

    /// <summary>Reactome and KEGG references added to the scratch database, by accession; each listed entry also gets the
    /// human taxon. Q71U36 has none, so it reads <c>no_reference</c>; O75390's R-MMU-77289 is dropped; <see cref="Drift"/>
    /// is in no release.</summary>
    private static readonly Dictionary<string, string[]> References = new()
    {
        ["P68363"] = ["R-HSA-77289", "R-HSA-354192", "KEGG:hsa:10376"],
        ["P05141"] = ["R-HSA-5602358", Drift, "KEGG:hsa:292"],
        ["Q71U36"] = [],
        ["O75390"] = ["R-HSA-77289", "R-HSA-8978868", "R-MMU-77289"],
        ["O75396"] = ["R-HSA-109582", "R-HSA-162582"],
        ["P04406"] = ["R-HSA-1430728", "R-HSA-556833"],
        ["P04843"] = ["R-HSA-76002", "R-HSA-76009"],
    };

    private static string FixtureDir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "reactome");
    private static string PathwaysFile => Path.Combine(FixtureDir, "ReactomePathways-96-trimmed.txt");
    private static string RelationFile => Path.Combine(FixtureDir, "ReactomePathwaysRelation-96-trimmed.txt");
    private static Dictionary<string, string> ReleaseInputs => new() { ["pathways"] = PathwaysFile, ["relation"] = RelationFile };
    private static readonly Dictionary<string, string> NoInputs = new();

    /// <summary>The fixture release, as the tests' own list of releases.</summary>
    private static IReadOnlyList<ReactomeReleaseFiles> FixtureReleases =>
        [new("96", BundleWriter.Sha256File(PathwaysFile), BundleWriter.Sha256File(RelationFile), "mzLib's trimmed release 96 (test fixture)")];

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string WorkRoot => Path.Combine(_root, "data", "work_root");

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-reactome-engine-" + Guid.NewGuid().ToString("N")));

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

    /// <summary>The fixture tree with Reactome in its database, ingested into a scratch store; the bundle it wrote.</summary>
    private BundleRef IngestWithReactome()
    {
        CopyTree(DataPaths.Instance, Path.Combine(_root, "data"));
        var db = Path.Combine(WorkRoot, "db", "test_human.xml");
        var before = BundleWriter.Sha256File(db);
        var text = File.ReadAllText(db, Encoding.UTF8);
        foreach (var (accession, refs) in References)
        {
            var anchor = $"<accession>{accession}</accession>\n";
            Assert.That(text, Does.Contain(anchor), accession);
            var xml = new StringBuilder("  <organism>\n    <name type=\"scientific\">Homo sapiens</name>\n"
                + "    <dbReference type=\"NCBI Taxonomy\" id=\"9606\">\n    </dbReference>\n  </organism>\n");
            foreach (var r in refs)
                xml.Append(r.StartsWith("KEGG:", StringComparison.Ordinal)
                    ? $"  <dbReference type=\"KEGG\" id=\"{r["KEGG:".Length..]}\">\n  </dbReference>\n"
                    : $"  <dbReference type=\"Reactome\" id=\"{r}\">\n    <property type=\"{ReactomePathway.PathwayNamePropertyType}\" value=\"name of {r}\"/>\n  </dbReference>\n");
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

    /// <summary>kegg's two engines and go's member resolution on the bundle.</summary>
    private (ArtefactRef Release, ArtefactRef Membership, ArtefactRef Members) RunAll(BundleRef bundle, string definition = FakeDefinition) =>
    (
        ReactomeEngines.ReleaseRun(Store, ReleaseInputs, definition, FixtureReleases, StandInInstall).Written.Single(),
        ReactomeEngines.MembershipRun(Store, [bundle], NoInputs, definition, StandInInstall).Written.Single(),
        GoReferenceEngines.ResolveMembersRun(Store, [bundle], NoInputs, definition, StandInInstall).Written.Single()
    );

    private static List<Dictionary<string, object?>> Rows(ArtefactRef artefact, string table) =>
        ArrowTables.ReadParquet(artefact.TablePath(table)!).Rows.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();

    private static IReadOnlyDictionary<string, object?> Summary(ArtefactRef artefact) =>
        (IReadOnlyDictionary<string, object?>)artefact.Record["engine_summary"]!;

    private static string[] Files(ArtefactRef artefact) => Directory.EnumerateFiles(artefact.Path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    private static string[] RecordedFiles(ArtefactRef artefact)
    {
        var files = (IReadOnlyDictionary<string, object?>)artefact.Record["files"]!;
        foreach (var (name, value) in files)
            Assert.That((string)((IReadOnlyDictionary<string, object?>)value!)["sha256"]!, Does.Match("^[0-9a-f]{64}$"), name);
        return files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
        {
            var f = (IReadOnlyDictionary<string, object?>)kv.Value!;
            return $"{kv.Key} {f["rows"]} {((bool)f["kept"]! ? "kept" : "dropped")}";
        }).ToArray();
    }

    // --- the engines -------------------------------------------------------------------------------------

    [Test]
    public void EachEngineWritesMzLibsFilesAndTheRowsMzLibsReaderGives()
    {
        var bundle = IngestWithReactome();
        var (release, membership, _) = RunAll(bundle);
        var human = CatalogBuilder.GoInputsOf(bundle).Targets.Single();

        Assert.Multiple(() =>
        {
            // The release: once per pair of files, no bundle.
            Assert.That(release.Engine, Is.EqualTo("kegg.reactome_release"));
            Assert.That(Files(release), Is.EqualTo(new[] { "reactome_ancestors.parquet", "reactome_hierarchy.parquet", "reactome_pathways.parquet", "run.json" }));
            var pairs = Rows(release, "reactome_ancestors").Count;
            Assert.That(RecordedFiles(release), Is.EqualTo(new[]
            {
                $"reactome_ancestors.tsv {pairs} dropped", "reactome_hierarchy.tsv 19 dropped", "reactome_pathways.tsv 25 dropped",
            }));
            Assert.That(release.Inputs.Keys.Order(), Is.EqualTo(new[] { "pathways", "relation" }));
            Assert.That(Summary(release)["reactome_release"], Is.EqualTo("96"), "the number comes from the list of releases, never a file name");
            Assert.That(Summary(release)["roots"], Is.EqualTo(7L));
            Assert.That(Rows(release, "reactome_ancestors").Count(r => Equals(r["pathway_id"], r["ancestor_pathway_id"])), Is.EqualTo(25), "the pathway itself is always a pair");
            Assert.That(Rows(release, "reactome_pathways").Single(r => Equals(r["pathway_id"], "R-MMU-77289"))["human_equivalent_id"], Is.EqualTo("R-HSA-77289"));
            Assert.That(Rows(release, "reactome_pathways").Single(r => Equals(r["pathway_id"], "R-GGA-351470"))["human_equivalent_id"], Is.Null);

            // The membership: one per database FILE; the contaminant panel is never annotation input.
            Assert.That(membership.Engine, Is.EqualTo("kegg.reactome_membership"));
            Assert.That(Files(membership), Is.EqualTo(new[] { "kegg_gene_links.parquet", "reactome_membership.parquet", "reactome_species.parquet", "run.json" }));
            var rows = Rows(membership, "reactome_membership");
            Assert.That(RecordedFiles(membership), Is.EqualTo(new[] { "kegg_gene_links.tsv 2 dropped", $"reactome_membership.tsv {rows.Count} dropped" }));
            Assert.That(membership.Inputs, Is.EquivalentTo(new Dictionary<string, string> { ["annotation_database"] = human.Sha256 }));
            Assert.That(Summary(membership)["dropped_foreign_references"], Is.EqualTo(1L), "R-MMU-77289 on a human entry");
            Assert.That(Summary(membership)["mzlib_release"], Is.EqualTo(ReactomeEngines.Release()["mzlib"]), "a released mzLib wrote the files");
            Assert.That(Summary(membership)["definition"], Is.EqualTo(ReactomeEngines.DefinitionId));
            Assert.That(Rows(membership, "reactome_species").Select(r => $"{r["organism"]} {r["species_code"]} {r["unvalidated_references"] ?? "NULL"}"),
                Is.EqualTo(new[] { "NCBITaxon:9606 HSA NULL" }), "12 of 13 references HSA: the code is read; taxon-less entries have none");
            Assert.That(rows.Select(r => r["accession"]).Distinct().Count(), Is.EqualTo(Summary(membership)["entries"]), "every entry has a row");
            Assert.That(rows.Where(r => Equals(r["accession"], "P68363")).Select(r => $"{r["pathway_id"]} {r["pathway_species"]} {r["status"]} {r["inherited"]}"),
                Is.EqualTo(new[] { "R-HSA-354192 HSA annotated False", "R-HSA-77289 HSA annotated False" }));
            Assert.That(rows.Where(r => Equals(r["accession"], "O75390")).Select(r => r["pathway_id"]), Is.EqualTo(new[] { "R-HSA-77289", "R-HSA-8978868" }),
                "the foreign id is dropped");
            Assert.That(rows.Single(r => Equals(r["accession"], "Q71U36"))["status"], Is.EqualTo("no_reference"), "human is covered");
            Assert.That(rows.Single(r => Equals(r["accession"], "P07437"))["status"], Is.EqualTo("species_not_covered"), "no taxon, no reference among the taxon-less");
            Assert.That(rows.All(r => Equals(r["definition_id"], ReactomeEngines.DefinitionId)), "every row carries its definition");
            Assert.That(Rows(membership, "kegg_gene_links").Select(r => $"{r["accession"]} {r["kegg_gene_id"]}"), Is.EqualTo(new[] { "P05141 hsa:292", "P68363 hsa:10376" }));
            Assert.That(ReactomeEngines.PathwaySpecies("R-HSA-70171"), Is.EqualTo("HSA"));
            Assert.That(ReactomeEngines.PathwaySpecies("not-an-id"), Is.Null);
            Assert.That(ReactomeEngines.DefinitionId, Is.EqualTo($"{ReactomeAccessionAnnotation.DefinitionId} v{ReactomeAccessionAnnotation.DefinitionVersion}"),
                "kegg's id, as mzLib writes it");
        });
    }

    [Test]
    public void TheSameInputsAreAlreadyDoneAndWhatIsNotAllowedIsRefusedBeforeAnythingRuns()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => ReactomeEngines.ReleaseRun(Store, ReleaseInputs, null, FixtureReleases, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("no definition id was given").And.Message.Contains(ReactomeEngines.DefinitionId));
            Assert.That(() => ReactomeEngines.MembershipRun(Store, [], NoInputs, "", StandInInstall), Throws.TypeOf<RunnerException>());
            Assert.That(() => ReactomeEngines.ReleaseRun(Store, new Dictionary<string, string> { ["pathways"] = PathwaysFile }, FakeDefinition, FixtureReleases, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("needs --input relation=<path>"));
            Assert.That(() => ReactomeEngines.MembershipRun(Store, [], ReleaseInputs, FakeDefinition, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("takes no --input"));
            Assert.That(() => ReactomeEngines.ReleaseRun(Store, ReleaseInputs, FakeDefinition, null, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("not a Reactome release this runner reads").And.Message.Contains("release 96"),
                "the trimmed files are not release 96's own");
        });
        Assert.That(Directory.Exists(Path.Combine(Store, PXReprise.Catalog.Runner.EngineDir)), Is.False, "nothing was written");

        var bundle = IngestWithReactome();
        var first = RunAll(bundle);
        Assert.That(ReactomeEngines.ReleaseRun(Store, ReleaseInputs, FakeDefinition, FixtureReleases, StandInInstall).AlreadyDone.Single().ArtefactId,
            Is.EqualTo(first.Release.ArtefactId));
        Assert.That(ReactomeEngines.MembershipRun(Store, [bundle], NoInputs, FakeDefinition, StandInInstall).AlreadyDone.Single().ArtefactId,
            Is.EqualTo(first.Membership.ArtefactId));
        File.AppendAllText(CatalogBuilder.GoInputsOf(bundle).Targets.Single().Path, "\n");
        Assert.That(() => ReactomeEngines.MembershipRun(Store, [bundle], NoInputs, FakeDefinition + " v2", StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("now hashes to").And.Message.Contains("test_human.xml"));
    }

    [Test]
    public void TheCliRunsUnderKeggsDefinition()
    {
        Assert.That(EngineRunner.Engines, Does.Contain("kegg.reactome_membership").And.Contain("kegg.reactome_release"));
        IngestWithReactome();
        // The CLI passes kegg's id; the membership run gets past the definition check (this tree is a development build,
        // which the runner refuses next, by design: aging 063), and the release run reads only a listed release.
        Assert.That(() => RunCommand.Run(["kegg.reactome_membership", "PXD999999", "--store", Store]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("development build"));
        Assert.That(() => RunCommand.Run(["kegg.reactome_release", "--store", Store, "--input", $"pathways={PathwaysFile}", "--input", $"relation={RelationFile}"]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("not a Reactome release this runner reads"));
        Assert.That(() => RunCommand.Run(["kegg.reactome_release", "PXD999999", "--store", Store, "--input", $"pathways={PathwaysFile}", "--input", $"relation={RelationFile}"]),
            Throws.TypeOf<RunnerException>().With.Message.Contains("takes no accession"));
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
            System.Collections.IEnumerable e and not string => "[" + string.Join(",", e.Cast<object?>()) + "]",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        }))).ToList();

    private static readonly string ReactomeTableList = string.Join(", ", CatalogBuilder.ReactomeTables.Select(t => $"'{t}'"));

    [Test]
    public void WithNoArtefactsTheReactomeTablesAreThereEmptyAndTheCoverageSaysWhy()
    {
        IngestWithReactome();
        foreach (var output in new[] { Path.Combine(_root, "catalog.duckdb"), Path.Combine(_root, "catalogs") })
        {
            var built = Build(output);
            Assert.Multiple(() =>
            {
                Assert.That(Rows(built.Path, $"SELECT table_name, rows, kind FROM catalog_tables WHERE table_name IN ({ReactomeTableList}) ORDER BY table_name"),
                    Is.EqualTo(new[]
                    {
                        "kegg_gene_links|0|engine:kegg.reactome_membership",
                        "protein_reactome|0|derived",
                        "reactome_ancestors|0|engine:kegg.reactome_release",
                        "reactome_group_pathways|0|derived",
                        "reactome_group_status|0|derived",
                        "reactome_hierarchy|0|engine:kegg.reactome_release",
                        "reactome_membership|0|engine:kegg.reactome_membership",
                        "reactome_pathways|0|engine:kegg.reactome_release",
                        "reactome_species|0|engine:kegg.reactome_membership",
                    }), output);
                foreach (var table in CatalogBuilder.ReactomeTables)
                    Assert.That(Rows(built.Path, $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"),
                        Is.EquivalentTo(CatalogBuilder.DerivedDocs[table].Columns.Keys), $"{table}: every column is documented, and only those");
                var notes = built.Checks.Where(c => c.Name.StartsWith("kegg.", StringComparison.Ordinal)).Select(c => $"{c.Name}: {c.Observed} of {c.Expected}; {c.Detail}").ToList();
                Assert.That(notes, Is.EqualTo(new[]
                {
                    "kegg.reactome_release (Reactome release served): 0 of 1; no artefact, so no Reactome is served",
                    $"kegg.reactome_membership coverage (target databases with an artefact): 0 of 1; none served without a release; no artefact for {CatalogBuilder.GoInputsOf(Bundles()[0]).Targets[0].Sha256[..12]} (PXD999999)",
                    $"kegg.reactome_membership served (bundles served Reactome): 0 of 1; not served: PXD999999 ({Bundles()[0].BundleId}): no kegg.reactome_release artefact",
                }));
                Assert.That(built.Checks.Where(c => c.Name.StartsWith("kegg.", StringComparison.Ordinal)).All(c => c.Ok));
            });
        }
    }

    [Test]
    public void TheCatalogJoinsTheEnginesInBothLayouts()
    {
        var bundle = IngestWithReactome();
        var without = Build(Path.Combine(_root, "without.duckdb"));
        var (release, membership, members) = RunAll(bundle);
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Path.Combine(_root, "catalogs"));
        Assert.That(folder.Layout, Is.EqualTo(CatalogLayout.Folder));
        Assert.That(file.CatalogId, Is.Not.EqualTo(without.CatalogId), "the catalog id names the artefacts it serves");
        foreach (var built in new[] { file, folder })
            Assert.Multiple(() =>
            {
                Assert.That(built.FailedChecks, Is.Empty);
                Assert.That(Rows(built.Path, "SELECT protein_accession, reactome_status, pathway_id, inherited, release_status FROM protein_reactome ORDER BY ALL"),
                    Is.EqualTo(new[]
                    {
                        "CONTAM_P00001|no_entry|NULL|NULL|NULL",
                        "P05141|annotated|R-HSA-5602358|false|in_release",
                        $"P05141|annotated|{Drift}|false|not_in_release",
                        "P68363|annotated|R-HSA-354192|false|in_release",
                        "P68363|annotated|R-HSA-77289|false|in_release",
                        "P99999|no_entry|NULL|NULL|NULL",
                        "Q71U36|no_reference|NULL|NULL|NULL",
                    }), "every member of every non-decoy group: its pathways, or one row saying why it has none");
                Assert.That(Rows(built.Path, "SELECT protein_group, reactome_status, members, n_pathways FROM reactome_group_status ORDER BY ALL"),
                    Is.EqualTo(new[] { "CONTAM_P00001|contaminant|1|0", "P05141|annotated|1|2", "P68363|Q71U36|annotated|2|2", "P99999|no_entry|1|0" }),
                    "every non-decoy group once, with kegg's status");
                Assert.That(Rows(built.Path, "SELECT p.protein_group, g.pathway_id, g.members, g.members_in_pathway, g.carriers FROM reactome_group_pathways g "
                    + "JOIN reactome_group_status p USING (bundle_id, protein_group_id) ORDER BY ALL"),
                    Is.EqualTo(new[]
                    {
                        "P05141|R-HSA-5602358|1|1|[P05141]", $"P05141|{Drift}|1|1|[P05141]",
                        "P68363|Q71U36|R-HSA-354192|2|1|[P68363]", "P68363|Q71U36|R-HSA-77289|2|1|[P68363]",
                    }));
                Assert.That(Rows(built.Path, "SELECT pathway_id FROM reactome_pathways WHERE in_disease ORDER BY 1"),
                    Is.EqualTo(new[] { "R-HSA-1643685", "R-HSA-5260271", "R-HSA-5602358" }), "Disease and everything under it");
                Assert.That(Rows(built.Path, "SELECT DISTINCT reactome_release FROM reactome_pathways"), Is.EqualTo(new[] { "96" }));
                // The roll-up a consumer makes: P68363's pathways up to their top-level areas.
                Assert.That(Rows(built.Path, "SELECT DISTINCT p.name FROM protein_reactome r JOIN reactome_ancestors a ON a.pathway_id = r.pathway_id "
                    + "JOIN reactome_pathways p ON p.pathway_id = a.ancestor_pathway_id "
                    + "WHERE r.protein_accession = 'P68363' AND p.pathway_id NOT IN (SELECT child_id FROM reactome_hierarchy) ORDER BY 1"),
                    Is.EqualTo(new[] { "Hemostasis", "Metabolism", "Signal Transduction" }), "R-HSA-354192 has two parents under two roots");
                var source = Rows(built.Path, "SELECT owner_project, source_id FROM annotation_sources").Single();
                Assert.That(source, Does.StartWith("kegg|reactome:96:"));
                Assert.That(Rows(built.Path, "SELECT DISTINCT source_id FROM protein_reactome UNION SELECT DISTINCT source_id FROM reactome_group_status"),
                    Is.EqualTo(new[] { source.Split('|')[1] }), "every row cites the search's source");
                Assert.That(Rows(built.Path, "SELECT engine, artefact_id FROM catalog_engine_artefacts WHERE engine LIKE 'kegg.%' OR engine = 'go.resolve_members' ORDER BY 1"),
                    Is.EqualTo(new[]
                    {
                        $"go.resolve_members|{members.ArtefactId}", $"kegg.reactome_membership|{membership.ArtefactId}", $"kegg.reactome_release|{release.ArtefactId}",
                    }), "go's member resolution is served for Reactome although no GO is");
                Assert.That(Rows(built.Path, "SELECT count(*) FROM go_group_members"), Is.EqualTo(new[] { "0" }), "GO's own tables are untouched");
                Assert.That(Rows(built.Path, $"SELECT table_name FROM information_schema.tables WHERE table_name IN ({ReactomeTableList}) AND table_type <> 'BASE TABLE'"),
                    Is.Empty, "copied and derived in both layouts");
                Assert.That(Rows(built.Path, "SELECT organism, species_code FROM reactome_species"), Is.EqualTo(new[] { "NCBITaxon:9606|HSA" }));
                Assert.That(Rows(built.Path, "SELECT accession, kegg_url FROM kegg_gene_links ORDER BY 1"),
                    Is.EqualTo(new[] { "P05141|https://www.kegg.jp/entry/hsa:292", "P68363|https://www.kegg.jp/entry/hsa:10376" }));
                var drift = built.Checks.Single(c => c.Name.StartsWith("kegg.reactome_membership drift", StringComparison.Ordinal));
                Assert.That((drift.Ok, drift.Observed, drift.Expected), Is.EqualTo((true, 10L, 11L)));
                Assert.That(drift.Detail, Does.Contain(Drift));
                Assert.That(built.Checks.Where(c => c.Kind == "engine-consistency" && c.Name.StartsWith("kegg.", StringComparison.Ordinal)).Select(c => c.Name.Split(' ')[1]),
                    Is.EquivalentTo(new[] { "members", "entries", "species", "exactness" }));
                Assert.That(built.Checks.Count(c => c.Kind == "row_count" && c.Name.StartsWith("kegg.", StringComparison.Ordinal)), Is.EqualTo(6),
                    "the release's three tables and the membership's three, each whole");
            });

        // The worked Reactome queries in querying.md run in both layouts; the one about a protein finds P68363's rows.
        var documented = DocumentedQueries().Select(sql => sql.Replace("'P04406'", "'P68363'")).ToList();
        Assert.That(documented, Has.Count.GreaterThanOrEqualTo(2));
        foreach (var built in new[] { file, folder })
            for (var i = 0; i < documented.Count; i++)
            {
                var rows = CatalogBuilder.RunQuery(built.Path, documented[i]).Rows;
                if (i == 0) Assert.That(rows, Is.Not.Empty, documented[i]);
            }
    }

    /// <summary>The SQL blocks of <c>docs/data/querying.md</c> that read the Reactome tables.</summary>
    private static List<string> DocumentedQueries()
    {
        var page = File.ReadAllText(Path.Combine(DataPaths.Docs, "querying.md")).Replace("\r\n", "\n");
        return System.Text.RegularExpressions.Regex.Matches(page, "```sql\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(sql => sql.Contains("protein_reactome", StringComparison.Ordinal) || sql.Contains("reactome_group_", StringComparison.Ordinal))
            .ToList();
    }

    [Test]
    public void TwoReleasesAreRefusedNotPicked()
    {
        var bundle = IngestWithReactome();
        RunAll(bundle);
        ReactomeEngines.ReleaseRun(Store, ReleaseInputs, FakeDefinition + " v2", FixtureReleases, StandInInstall);
        Assert.That(() => CatalogBuilder.SelectArtefacts(Store, Bundles()),
            Throws.TypeOf<CatalogException>().With.Message.Contains("2 artefacts").And.Message.Contains("A catalog serves one"));
    }
}
