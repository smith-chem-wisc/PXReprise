using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest;
using PXReprise.Runner;

namespace PXReprise.Data.Tests;

/// <summary><c>differential.register</c> and the differential layer of a catalog (our 145, PXR-R85).</summary>
/// <remarks>
/// <para>The bundle is the fixture deposit PXD999999, ingested into a scratch store. The stage is cut from aging's real
/// PXD036557 and PXD003111 stage folders (<c>Fixtures/differential-stage/PROVENANCE.md</c>): mzLib's own rows, with the
/// input sha256s left as placeholders that <see cref="Stage"/> fills from the fixture bundle's sources, so the stage is bound
/// to it as a real stage is bound to its search.</para>
/// </remarks>
public class DifferentialEngineTests
{
    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    private static string FixtureDir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "differential-stage");

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string SearchDir => Path.Combine(_root, "data", "work_root", "run_test", "PXD999999", "04_search");

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-differential-" + Guid.NewGuid().ToString("N")));

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

    /// <summary>The fixture deposit ingested into the scratch store, after <paramref name="before"/> changes its search.</summary>
    private BundleRef Ingest(Action? before = null)
    {
        CopyTree(DataPaths.Instance, Path.Combine(_root, "data"));
        before?.Invoke();
        return BundleRef.Load(Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current).BundlePath);
    }

    private static string ShaOf(BundleRef bundle, string role) =>
        ((IEnumerable<object?>)bundle.Manifest["sources"]!).Cast<IReadOnlyDictionary<string, object?>>()
            .Single(s => (string)s["role"]! == role)["sha256"] as string ?? throw new InvalidOperationException(role);

    /// <summary>A scratch copy of a fixture stage, its sha256 placeholders filled from the bundle's sources, after
    /// <paramref name="edit"/> changes it (paths relative to the stage).</summary>
    private string Stage(BundleRef bundle, string which = "lfq", Action<string>? edit = null)
    {
        var stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N")[..8], "06_differential");
        CopyTree(Path.Combine(FixtureDir, which), stage);
        foreach (var file in Directory.EnumerateFiles(stage, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (var role in new[] { "provenance:04_search", "peptide_quant", "protein_group_quant" })
                text = text.Replace("{{sha:" + role + "}}", ShaOf(bundle, role), StringComparison.Ordinal);
            File.WriteAllText(file, text, new UTF8Encoding(false));
        }
        edit?.Invoke(stage);
        return stage;
    }

    private RunResult Register(BundleRef bundle, string stage) =>
        DifferentialEngine.Run(Store, [bundle], new Dictionary<string, string> { ["stage"] = stage }, StandInInstall);

    private static void EditJson(string path, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static string Metadata(string stage) => Path.Combine(stage, "mbr_kept", DifferentialEngine.MetadataFile);

    private static string Results(string stage) => Path.Combine(stage, "mbr_kept", DifferentialEngine.ResultsFile);

    // --- registration ----------------------------------------------------------------------------------------

    [Test]
    public void AStageBoundToItsBundleIsRegisteredWithEveryRowAndFile()
    {
        var bundle = Ingest();
        var stage = Stage(bundle);
        var artefact = Register(bundle, stage).Written.Single();
        Assert.That(artefact.Inputs["bundle"], Is.EqualTo(bundle.BundleId));
        Assert.That(artefact.RowCounts, Is.EquivalentTo(new Dictionary<string, long>
        {
            ["differential_stages"] = 1, ["differential_analyses"] = 1, ["differential_results"] = 12, ["differential_decisions"] = 6,
            ["differential_exclusions"] = 0, ["differential_findings"] = 0, ["differential_files"] = 8,
        }));

        var results = ArrowTables.ReadParquet(artefact.TablePath("differential_results")!).Rows;
        var fitted = results.First(r => (string?)r["status"] == "fitted");
        Assert.That(fitted["log2_effect"], Is.TypeOf<double>());
        Assert.That(fitted["family_size"], Is.TypeOf<long>());
        Assert.That(fitted["robust"], Is.TypeOf<bool>());
        Assert.That(fitted["covariate"], Is.Null, "a blank cell is NULL");
        var absent = results.First(r => (string?)r["status"] == "absent_in_denominator");
        Assert.That((absent["log2_effect"], absent["p_value"]), Is.EqualTo(((object?)null, (object?)null)), "an absent side has no number");
        Assert.That((string?)absent["status_detail"], Does.StartWith("samples with a value:"), "and says why");

        var analysis = ArrowTables.ReadParquet(artefact.TablePath("differential_analyses")!).Rows.Single();
        Assert.That(analysis["name"], Is.EqualTo("mbr_kept"));
        Assert.That((IEnumerable<object?>)analysis["bound_roles"]!, Is.EqualTo(new[] { "peptide_quant", "protein_group_quant" }));
        Assert.That(analysis["observation_table_sha256"], Is.Not.Null, "recorded, not bound (option A)");
        Assert.That((string)analysis["settings_json"]!, Does.Contain("\"method\": \"moderated\""), "the metadata's settings, verbatim");

        var stageRow = ArrowTables.ReadParquet(artefact.TablePath("differential_stages")!).Rows.Single();
        Assert.That((stageRow["analysed"], stageRow["n_analyses"], stageRow["quant_style"], stageRow["identification_filter"]),
            Is.EqualTo(((object?)true, (object?)1L, (object?)"lfq", (object?)"pep q-value")));

        // Every file of the stage is kept, byte for byte, at its path in the stage.
        var files = ArrowTables.ReadParquet(artefact.TablePath("differential_files")!).Rows;
        Assert.That(files.Select(f => $"{f["path"]}:{f["role"]}"), Is.EqualTo(new[]
        {
            "mbr_kept/DifferentialAssumptions.md:assumptions", "mbr_kept/DifferentialDecisions.tsv:decisions",
            "mbr_kept/DifferentialResults.metadata.json:metadata", "mbr_kept/DifferentialResults.tsv:results",
            "mbr_kept/StatisticalReport.md:report", "mbr_kept/figures/figure_07_global_shift.svg:figure",
            "mbr_kept/figures/figure_07_global_shift.tsv:figure_table", "provenance.json:provenance",
        }));
        foreach (var f in files)
        {
            var kept = Path.Combine(artefact.Path, DifferentialEngine.FilesDir, (string)f["path"]!);
            Assert.That(BundleWriter.Sha256File(kept), Is.EqualTo(f["sha256"]), (string)f["path"]!);
            Assert.That(BundleWriter.Sha256File(kept), Is.EqualTo(BundleWriter.Sha256File(Path.Combine(stage, (string)f["path"]!))));
        }

        var again = Register(bundle, stage);
        Assert.That((again.Written.Count, again.AlreadyDone.Single().ArtefactId), Is.EqualTo((0, artefact.ArtefactId)), "the same folder is already done");
    }

    [Test]
    public void AStageThatAnalysedNothingIsRegisteredWithItsFinding()
    {
        var bundle = Ingest();
        var artefact = Register(bundle, Stage(bundle, "one_condition")).Written.Single();
        Assert.That((artefact.RowCounts["differential_stages"], artefact.RowCounts["differential_results"], artefact.RowCounts["differential_findings"]),
            Is.EqualTo((1L, 0L, 1L)));
        var row = ArrowTables.ReadParquet(artefact.TablePath("differential_stages")!).Rows.Single();
        Assert.That((row["analysed"], row["quant_style"]), Is.EqualTo(((object?)false, (object?)"tmt")));
        Assert.That((IEnumerable<object?>)row["finding_kinds"]!, Is.EqualTo(new[] { "one_condition" }));
        var finding = ArrowTables.ReadParquet(artefact.TablePath("differential_findings")!).Rows.Single();
        Assert.That((string)finding["detail"]!, Does.Contain("there is nothing to compare"));
    }

    [Test]
    public void AStageThatDidNotReadTheBundlesSearchIsRefused()
    {
        var bundle = Ingest();
        var stage = Stage(bundle, edit: s => EditJson(Path.Combine(s, DifferentialEngine.ProvenanceFile),
            p => p["inputs"]![1]!["sha256"] = new string('0', 64)));
        Assert.That(() => Register(bundle, stage), Throws.TypeOf<RunnerException>()
            .With.Message.Contains("was not computed from this bundle's search").And.Message.Contains("No bundle in the store holds its inputs"));
        Assert.That(PXReprise.Catalog.Runner.DiscoverArtefacts(Store, DifferentialEngine.Engine), Is.Empty, "nothing written");
    }

    [Test]
    public void AnAnalysisInputThatIsNotTheBundlesFileIsRefused()
    {
        var bundle = Ingest();
        var stage = Stage(bundle, edit: s => EditJson(Metadata(s), m =>
        {
            foreach (var input in m["inputs"]!.AsArray())
                if ((string?)input!["role"] == "peptide_quant") input["sha256"] = new string('1', 64);
        }));
        Assert.That(() => Register(bundle, stage), Throws.TypeOf<RunnerException>()
            .With.Message.Contains("input peptide_quant is AllQuantifiedPeptides.tsv").And.Message.Contains("not computed from this bundle's files"));
    }

    [Test]
    public void OnlyTheObservationTableMayNameNoFile()
    {
        var bundle = Ingest();
        var stage = Stage(bundle, edit: s => EditJson(Metadata(s), m =>
            m["inputs"]!.AsArray().Add(new JsonObject { ["role"] = "imputation_table", ["file"] = "", ["sha256"] = new string('2', 64) })));
        Assert.That(() => Register(bundle, stage), Throws.TypeOf<RunnerException>().With.Message.Contains("Only observation_table may"));
    }

    /// <summary>MetaMorpheus's written SDRF is no source of the bundle, and the bundle's copy of the search record lists it
    /// by name and sha256: the stage's input binds through the record, and another file of that name does not.</summary>
    [Test]
    public void AFileTheSearchRecordListsBindsThroughTheRecord()
    {
        const string sdrf = "experiment.sdrf.tsv";
        var written = Path.Combine(_root, "data", "work_root", "run_test", "PXD999999", "04_search", "mm", "Task3SearchTask", sdrf);
        var bundle = Ingest(() =>
        {
            File.WriteAllText(written, "source name\tcharacteristics[individual]\nS1\tD1\n", new UTF8Encoding(false));
            EditJson(Path.Combine(SearchDir, "provenance.json"), p => p["outputs"]!.AsArray().Add(new JsonObject
            {
                ["path"] = $"run_test/PXD999999/04_search/mm/Task3SearchTask/{sdrf}", ["root"] = "work_root",
                ["size_bytes"] = new FileInfo(written).Length, ["sha256"] = BundleWriter.Sha256File(written),
            }));
        });
        var sha = BundleWriter.Sha256File(written);
        string WithSdrf(string inputSha) => Stage(bundle, edit: s =>
        {
            EditJson(Path.Combine(s, DifferentialEngine.ProvenanceFile), p => p["inputs"]!.AsArray().Add(new JsonObject { ["path"] = written, ["sha256"] = inputSha }));
            EditJson(Metadata(s), m => m["inputs"]!.AsArray().Add(new JsonObject { ["role"] = "written_sdrf", ["file"] = sdrf, ["sha256"] = inputSha }));
        });
        var artefact = Register(bundle, WithSdrf(sha)).Written.Single();
        var analysis = ArrowTables.ReadParquet(artefact.TablePath("differential_analyses")!).Rows.Single();
        Assert.That((IEnumerable<object?>)analysis["bound_roles"]!, Is.EqualTo(new[] { "peptide_quant", "protein_group_quant", "written_sdrf" }));
        Assert.That(() => Register(bundle, WithSdrf(new string('3', 64))), Throws.TypeOf<RunnerException>().With.Message.Contains("was not computed"));
    }

    [Test]
    public void ACellThatDoesNotReadOrAnotherHeaderIsRefused()
    {
        var bundle = Ingest();
        var bad = Stage(bundle, edit: s =>
        {
            var lines = File.ReadAllLines(Results(s));
            var p = Array.IndexOf(lines[0].Split('\t'), "family_size");
            var cells = lines[1].Split('\t');
            cells[p] = "many";
            lines[1] = string.Join('\t', cells);
            File.WriteAllLines(Results(s), lines);
        });
        Assert.That(() => Register(bundle, bad), Throws.TypeOf<RunnerException>().With.Message.Contains("family_size is 'many', which is not a Int64"));
        var header = Stage(bundle, edit: s =>
        {
            var lines = File.ReadAllLines(Results(s)).Select(l => l + "\tx").ToArray();
            lines[0] = lines[0][..^1] + "new_column";
            File.WriteAllLines(Results(s), lines);
        });
        Assert.That(() => Register(bundle, header), Throws.TypeOf<RunnerException>().With.Message.Contains("not known: new_column"));
    }

    [Test]
    public void TheCountsTheStageRecordedMustBeTheFiles()
    {
        var bundle = Ingest();
        var total = Stage(bundle, edit: s => EditJson(Metadata(s), m => m["row_count"]!["total"] = 13));
        Assert.That(() => Register(bundle, total), Throws.TypeOf<RunnerException>().With.Message.Contains("row_count.total 13"));
        var byStatus = Stage(bundle, edit: s => EditJson(Metadata(s), m =>
        {
            m["row_count"]!["by_status"]!["fitted"] = 3;
            m["row_count"]!["by_status"]!["absent_in_numerator"] = 1;
        }));
        Assert.That(() => Register(bundle, byStatus), Throws.TypeOf<RunnerException>().With.Message.Contains("are not the metadata's row_count.by_status"));
        var decisions = Stage(bundle, edit: s => EditJson(Path.Combine(s, DifferentialEngine.ProvenanceFile), p => p["analyses"]![0]!["decisions"] = 7));
        Assert.That(() => Register(bundle, decisions), Throws.TypeOf<RunnerException>().With.Message.Contains("holds 6 rows; the stage's record says 7"));
    }

    [Test]
    public void AnUnfinishedOrForeignStageIsRefused()
    {
        var bundle = Ingest();
        var unfinished = Stage(bundle, edit: s => File.Delete(Path.Combine(s, DifferentialEngine.ProvenanceFile)));
        Assert.That(() => Register(bundle, unfinished), Throws.TypeOf<RunnerException>().With.Message.Contains("the stage has not finished"));
        var later = Stage(bundle, edit: s => EditJson(Path.Combine(s, DifferentialEngine.ProvenanceFile), p => p["schema"] = "pxreprise-differential/2"));
        Assert.That(() => Register(bundle, later), Throws.TypeOf<RunnerException>().With.Message.Contains("reads pxreprise-differential/1 only"));
        Assert.That(() => DifferentialEngine.Run(Store, [bundle], new Dictionary<string, string> { ["stage"] = Stage(bundle), ["extra"] = "x" }, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("not extra"));
    }

    // --- the catalog ------------------------------------------------------------------------------------------

    private CatalogResult Build(string output)
    {
        var bundles = CatalogBuilder.SelectBundles(Manifest.Load(ManifestPath), ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, output, instance: Manifest.Load(ManifestPath).Instance, artefacts: artefacts, engineChecks: checks);
    }

    private static List<string> Rows(string catalog, string sql) =>
        CatalogBuilder.RunQuery(catalog, sql).Rows.Select(r => string.Join("|", r.Select(v => v switch
        {
            null => "NULL",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        }))).ToList();

    [Test]
    public void ACatalogServesTheLayerAndRefusesTwoStagesForOneBundle()
    {
        var bundle = Ingest();
        var empty = Build(Path.Combine(_root, "empty.duckdb"));
        Assert.That(Rows(empty.Path, "SELECT table_name, rows, kind FROM catalog_tables WHERE table_name LIKE 'differential%' ORDER BY 1"),
            Is.EqualTo(CatalogBuilder.DifferentialTables.Order(StringComparer.Ordinal).Select(t => $"{t}|0|engine:differential.register")),
            "every table, typed and empty, when no stage is registered");
        Assert.That(Rows(empty.Path, "SELECT data_type FROM information_schema.columns WHERE table_name = 'differential_results' AND column_name IN ('robust', 'p_value', 'artefact_id') ORDER BY column_name"),
            Is.EqualTo(new[] { "VARCHAR", "DOUBLE", "BOOLEAN" }));
        Assert.That(empty.Checks.Single(c => c.Name.StartsWith("differential.register coverage", StringComparison.Ordinal)).Detail, Is.EqualTo("no stage registered for PXD999999"));

        var artefact = Register(bundle, Stage(bundle)).Written.Single();
        var built = Build(Path.Combine(_root, "catalog.duckdb"));
        Assert.That(Rows(built.Path, "SELECT table_name, rows FROM catalog_tables WHERE table_name LIKE 'differential%' AND rows > 0 ORDER BY 1"), Is.EqualTo(new[]
        {
            "differential_analyses|1", "differential_decisions|6", "differential_files|8", "differential_results|12", "differential_stages|1",
        }));
        Assert.That(Rows(built.Path, "SELECT DISTINCT dataset_id, bundle_id, artefact_id FROM differential_results"),
            Is.EqualTo(new[] { $"PXD999999|{bundle.BundleId}|{artefact.ArtefactId}" }));
        Assert.That(Rows(built.Path, "SELECT engine, artefact_id FROM catalog_engine_artefacts WHERE engine = 'differential.register'"),
            Is.EqualTo(new[] { $"differential.register|{artefact.ArtefactId}" }));
        Assert.That(built.Checks.Where(c => c.Name.StartsWith($"differential.register/{artefact.ArtefactId}/", StringComparison.Ordinal)).Select(c => c.Ok).ToList(),
            Has.Count.EqualTo(7).And.All.True, "each of the artefact's tables whole");
        Assert.That(Rows(built.Path, "SELECT catalog_version FROM catalog_meta").Single(), Is.EqualTo("18"));

        var folder = Build(Path.Combine(_root, "folder"));
        Assert.That(Rows(folder.Path, "SELECT count(*) FROM differential_results").Single(), Is.EqualTo("12"), "the folder layout's view reads the artefact's file");

        Register(bundle, Stage(bundle, "one_condition"));
        Assert.That(() => Build(Path.Combine(_root, "two.duckdb")), Throws.TypeOf<CatalogException>().With.Message.Contains("the stage was registered more than once"));
    }

    [Test]
    public void EveryDifferentialColumnIsDocumented()
    {
        foreach (var table in CatalogBuilder.DifferentialTables)
            Assert.That(CatalogBuilder.DerivedDocs[table].Columns.Keys, Is.EquivalentTo(
                new[] { "dataset_id", "bundle_id", "artefact_id" }.Concat(CatalogBuilder.DifferentialColumns[table].Select(c => c.Name))),
                $"{table}: every column is documented, and only those");
    }
}
