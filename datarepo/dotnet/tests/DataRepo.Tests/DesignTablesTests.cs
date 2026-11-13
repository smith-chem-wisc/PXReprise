using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;
using DataRepo.Study;

namespace DataRepo.Tests;

/// <summary>aging DATAREPO-79 as amended by DATAREPO-82 (aging 108): the aging layer's curated design tables,
/// <c>curated_run_design</c> and <c>curated_design_factors</c> (layer 0.6.0), and the rules each delivery keeps.</summary>
/// <remarks>Runs on the current rules, outside any <see cref="SchemaContract.Python0320"/> scope: a parity build holds
/// aging 0.5.0's tables only, which <c>StudyTests</c>, <c>CatalogTests</c> and <c>McpParityTests</c> prove.</remarks>
public class DesignTablesTests
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
    private string Delivery => Path.Combine(_root, "delivery");

    private const string RunA = "PXD999999:QE-002106_GM1_a";
    private const string RunB = "PXD999999:QE-002107_GM1_b";

    private static readonly string[] DesignColumns =
    [
        "run_id", "individual", "injection", "injection_reserved", "fraction", "fraction_reserved", "pool_size",
        "pool_size_reserved", "acquisition_batch", "source_reference", "evidence", "confidence", "curation_version",
    ];

    private static readonly string[] FactorColumns =
    [
        "run_id", "factor", "role", "level", "level_value", "level_unit", "is_reference", "is_primary", "term",
        "source_reference", "evidence", "confidence", "curation_version",
    ];

    /// <summary>Two runs of two individuals; run B's individual is a reserved word, which is an answer.</summary>
    private List<string[]> _design = [];

    /// <summary>An age-group condition (3 months the reference) and an organism-part stratum, on both runs.</summary>
    private List<string[]> _factors = [];

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-design-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        _design =
        [
            [RunA, "mouse 1", "1", "", "", "not applicable", "1", "", "batch 1", "PMID 1", "", "high", "aging-curation/1"],
            [RunB, "not available", "1", "", "", "not applicable", "1", "", "batch 1", "PMID 1", "", "high", "aging-curation/1"],
        ];
        _factors =
        [
            [RunA, "age group", "condition", "3 months", "0.25", "UO:0000036", "true", "true", "", "PMID 1", "", "high", "aging-curation/1"],
            [RunB, "age group", "condition", "24 months", "2", "UO:0000036", "false", "true", "", "PMID 1", "", "high", "aging-curation/1"],
            [RunA, "organism part", "stratum", "liver", "", "", "", "", "UBERON:0002107", "PMID 1", "", "high", "aging-curation/1"],
            [RunB, "organism part", "stratum", "heart", "", "", "", "", "UBERON:0000948", "PMID 1", "", "high", "aging-curation/1"],
        ];
    }

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static void WriteTsv(string path, string[] header, IEnumerable<string[]> rows) =>
        File.WriteAllText(path, string.Join("\n", new[] { header }.Concat(rows).Select(r => string.Join("\t", r))) + "\n");

    private StudyResult Deliver()
    {
        Directory.CreateDirectory(Delivery);
        WriteTsv(Path.Combine(Delivery, "run_design.tsv"), DesignColumns, _design);
        WriteTsv(Path.Combine(Delivery, "factors.tsv"), FactorColumns, _factors);
        var yaml = Path.Combine(Delivery, "study.yaml");
        File.WriteAllText(yaml,
            "study_manifest_version: 1\nlayer: aging\nstore: ../store\n"
            + "tables:\n  curated_run_design: run_design.tsv\n  curated_design_factors: factors.tsv\n");
        return StudyWriter.WriteStudyBundle(StudyWriter.LoadStudyManifest(yaml));
    }

    private CatalogResult Build()
    {
        Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var study = CatalogBuilder.SelectStudyBundles(Store, latest: ["aging"]);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, "catalog.duckdb"), instance: manifest.Instance,
            studyBundles: study, artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    [Test]
    public void ADesignIsWrittenAndServed()
    {
        var delivered = Deliver();
        Assert.That(delivered.RowCounts, Is.EquivalentTo(new Dictionary<string, long> { ["curated_run_design"] = 2, ["curated_design_factors"] = 4 }));
        var built = Build();
        Assert.Multiple(() =>
        {
            Assert.That(CatalogBuilder.RunQuery(built.Path,
                "SELECT run_id, individual, individual_reserved, fraction, fraction_reserved FROM curated_run_design ORDER BY run_id").Rows,
                Is.EqualTo(new[]
                {
                    new object?[] { RunA, "mouse 1", false, null, "not applicable" },
                    new object?[] { RunB, "not available", true, null, "not applicable" },
                }), "the reserved flag is computed, and a reserved number lives in its companion");
            Assert.That(CatalogBuilder.RunQuery(built.Path,
                $"SELECT factor, role, level, level_value, is_reference FROM curated_design_factors WHERE run_id = '{RunA}' ORDER BY factor").Rows,
                Is.EqualTo(new[]
                {
                    new object?[] { "age group", "condition", "3 months", 0.25, true },
                    new object?[] { "organism part", "stratum", "liver", null, null },
                }));
            Assert.That(built.Checks.Single(c => c.Kind == "study-design").Ok, "the two runs are two samples");
        });
    }

    /// <summary>Each rule refuses the whole delivery, named, from one changed cell of an otherwise valid one.</summary>
    [TestCase("design", 0, "fraction_reserved", "3", "`fraction_reserved` holds only an SDRF reserved word")]
    [TestCase("design", 0, "fraction", "2", "a run has `fraction` or `fraction_reserved`, never both")]
    [TestCase("design", 0, "pool_size", "0", "`pool_size` is at least 1")]
    [TestCase("factors", 0, "run_id", "PXD999999:QE-not-designed", "every run is in `curated_run_design`")]
    [TestCase("factors", 0, "role", "comparison", "`role` is `condition` or `stratum`")]
    [TestCase("factors", 0, "level", "not applicable", "`level` is never an SDRF reserved word")]
    [TestCase("factors", 0, "level_value", "", "`level_unit` is set exactly when `level_value` is")]
    [TestCase("factors", 2, "role", "condition", "every row of one factor in one dataset has the same role")]
    [TestCase("factors", 0, "is_reference", "false", "a condition has a reference level (`is_reference` true) and another level (false)")]
    [TestCase("factors", 1, "level", "3 months", "no level of a condition is both reference and not")]
    [TestCase("factors", 3, "level", "liver", "a stratum has at least two levels")]
    [TestCase("factors", 2, "is_reference", "true", "a stratum has no reference level")]
    public void ADesignThatBreaksARuleIsRefused(string table, int row, string column, string value, string rule)
    {
        var (rows, columns) = table == "design" ? (_design, DesignColumns) : (_factors, FactorColumns);
        rows[row][Array.IndexOf(columns, column)] = value;
        var e = Assert.Throws<IngestException>(() => Deliver());
        Assert.That(e!.Message, Does.Contain(rule));
        Assert.That(Directory.Exists(Path.Combine(Store, "_study")), Is.False, "nothing was written");
    }

    /// <summary>A factor describes a sample, so two runs of one sample with different factor rows are refused, and only
    /// the catalog knows which runs share a sample.</summary>
    [Test]
    public void TwoRunsOfOneSampleWithDifferentFactorsAreRefusedAtBuild()
    {
        var sdrf = Path.Combine(_root, "data", "work_root", "run_test", "PXD999999", "02_fetch", "metadata", "PXD999999.sdrf.tsv");
        var text = File.ReadAllText(sdrf);
        Assert.That(text, Does.Contain("PXD999999-Sample-2\t"));
        File.WriteAllText(sdrf, text.Replace("PXD999999-Sample-2\t", "PXD999999-Sample-1\t", StringComparison.Ordinal));
        Deliver();
        var e = Assert.Throws<CatalogException>(() => Build());
        Assert.That(e!.Message, Does.Contain("aging.curated_design_factors: one sample's runs carry the same factor rows: 1 vs 0 (e.g. sample PXD999999:PXD999999-Sample-1)"));
    }

    [Test]
    public void ADesignedRunTheCatalogDoesNotHoldIsRefusedAtBuild()
    {
        _design.Add(["PXD999999:QE-not-searched", "mouse 3", "1", "", "", "not applicable", "1", "", "", "PMID 1", "", "", "aging-curation/1"]);
        Deliver();
        var e = Assert.Throws<CatalogException>(() => Build());
        Assert.That(e!.Message, Does.Contain("aging.curated_run_design.run_id -> runs.run_id: 1 vs 0 (e.g. PXD999999:QE-not-searched)"));
    }
}
