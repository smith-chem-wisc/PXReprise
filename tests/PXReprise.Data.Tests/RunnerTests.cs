using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Runner;

namespace PXReprise.Data.Tests;

/// <summary>The runner and the logs engine (runner.py, engines/logs.py).</summary>
public class RunnerTests
{
    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    [Test]
    public void TheArtefactIdIsPythonsRule()
    {
        // runner.artefact_id("e", {"pymzlib": "0.2.0", "mzlib": "x"}, {"db": "1", "gene_set": "2"}, "d v1"), from
        // the Python 0.32.0 module: sorted release, sorted inputs, then the definition.
        var release = new Dictionary<string, string> { ["pymzlib"] = "0.2.0", ["mzlib"] = "x" };
        var a = EngineRunner.ArtefactId("e", release, new Dictionary<string, string> { ["db"] = "1", ["gene_set"] = "2" }, "d v1");
        var b = EngineRunner.ArtefactId("e", release, new Dictionary<string, string> { ["gene_set"] = "2", ["db"] = "1" }, "d v1");
        Assert.That(a, Is.EqualTo(b), "input order must not matter");
        Assert.That(EngineRunner.ArtefactId("e", release, new Dictionary<string, string> { ["db"] = "9", ["gene_set"] = "2" }, "d v1"), Is.Not.EqualTo(a));
        Assert.That(EngineRunner.ArtefactId("e", release, new Dictionary<string, string> { ["db"] = "1", ["gene_set"] = "2" }, "d v2"), Is.Not.EqualTo(a));
    }

    [Test]
    public void ADevelopmentBuildIsRefused()
    {
        Assume.That(BundleWriter.PackageVersion, Does.Contain("-dev"));
        Assert.That(() => EngineRunner.InstallIdentity(), Throws.TypeOf<RunnerException>().With.Message.Contains("development build"));
    }

    [Test]
    public void AMissingInputIsRefusedBeforeAnythingRuns()
    {
        Assert.That(() => LogsEngine.Run(Path.GetTempPath(), [], new Dictionary<string, string> { ["gene_set"] = "x" }, StandInInstall),
            Throws.TypeOf<RunnerException>().With.Message.Contains("needs --input xref=<path>, --input logs_manifest=<path>")
                .And.Message.Contains("not logs:DEF-GENE-RESOLUTION v2"));
    }

    /// <summary>Re-runs a resolution Python stored in aging's store, with its own recorded inputs, and compares rows.</summary>
    [Test, Category("RealData")]
    public void ARealResolutionReproducesThePythonRows()
    {
        const string stored = "F:/aging_data/repo/store/_engine/logs.resolve_genes/0138c5d37074ead8";
        if (!Directory.Exists(stored)) Assert.Ignore("aging's store is not on this machine");
        var record = CatalogBuilder.ReadJsonObject(Path.Combine(stored, "run.json"));
        var files = (IReadOnlyDictionary<string, object?>)record["input_files"]!;
        var checkedAgainst = (IReadOnlyDictionary<string, object?>)record["checked_against"]!;
        var inputs = new Dictionary<string, string>
        {
            ["gene_set"] = (string)files["gene_set"]!,
            ["xref"] = (string)files["xref"]!,
            ["logs_manifest"] = (string)checkedAgainst["logs_manifest"]!,
        };
        if (inputs.Values.Any(p => !File.Exists(p))) Assert.Ignore("the recorded inputs are not on this machine");
        var requested = ((IEnumerable<object?>)record["requested_for"]!).Cast<IReadOnlyDictionary<string, object?>>().First();
        var bundle = BundleRef.Load(Path.Combine("F:/aging_data/repo/store", (string)requested["dataset_id"]!, (string)requested["bundle_id"]!));

        var scratch = Path.Combine(Path.GetTempPath(), "datarepo-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = LogsEngine.Run(scratch, [bundle], inputs, StandInInstall);
            Assert.That(result.Written, Has.Count.GreaterThanOrEqualTo(1));
            var mine = result.Written.Single(w => w.Inputs["search_database"] == ((IReadOnlyDictionary<string, object?>)record["inputs"]!)["search_database"] as string);
            var (_, expected) = ArrowTables.ReadParquet(Path.Combine(stored, "gene_resolutions.parquet"));
            var (_, actual) = ArrowTables.ReadParquet(mine.TablePath("gene_resolutions")!);
            // Python wrote v1 rows; this runner writes v2 (logs 034): the same rows, with ensembl_xref_agreement filled.
            // The new column is checked against ensembl_xref_agrees as logs 034 pairs them, then set aside with the id.
            foreach (var row in actual)
            {
                var agrees = row.GetValueOrDefault("ensembl_xref_agrees");
                Assert.That(row.GetValueOrDefault("ensembl_xref_agreement"),
                    agrees is null ? Is.Null : (bool)agrees ? Is.EqualTo("agrees") : (NUnit.Framework.Constraints.IResolveConstraint)Is.AnyOf("silent", "disagrees"));
                Assert.That(row["definition_id"], Is.EqualTo(LogsEngine.DefinitionId));
                row.Remove("ensembl_xref_agreement");
                row["definition_id"] = LogsEngine.DefinitionIdV1;
            }
            Assert.That(RoundTrip.FirstDifference(expected, actual), Is.Null);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    /// <summary>A resolver's table of one row, as <see cref="LogsEngine.RowsFrom"/> takes it: with
    /// <c>ensembl_xref_agrees</c> and, unless <paramref name="withAgreement"/> is false, <c>ensembl_xref_agreement</c>, as
    /// mzLib #1578 writes them.</summary>
    private static ResolveResult OneRow(string? agreement = "agrees", bool? agrees = true, bool withAgreement = true)
    {
        var cells = new Dictionary<string, object?>
        {
            ["source_index"] = 0L, ["source_path"] = "db.xml", ["accession"] = "P12345", ["outcome"] = SchemaDocs.Enums["GeneResolutionOutcome"].Values[0],
            ["gene_id"] = "ENSG00000000001", ["search_database_sha256"] = "dbsha", ["gene_set_sha256"] = "gs", ["gene_set_release"] = "114",
            ["ensembl_xref_agrees"] = agrees, ["ensembl_xref_sha256"] = "xr",
        };
        if (withAgreement) cells["ensembl_xref_agreement"] = agreement;
        return new ResolveResult
        {
            ColumnNames = cells.Keys.ToList(), Columns = cells.ToDictionary(kv => kv.Key, kv => new List<object?> { kv.Value }),
            OutcomeCounts = new Dictionary<string, long>(), Caveats = [],
        };
    }

    private static readonly Dictionary<string, object?> Expected = new() { ["gene_set_sha256"] = "gs", ["ensembl_xref_sha256"] = "xr", ["gene_set_release"] = "114" };

    /// <summary>Schema 0.0.17 with mzLib 1.0.598 (logs 034/035, D59): every row is v2 and carries
    /// <c>ensembl_xref_agreement</c>, NULL only on a row with no gene; a resolver that writes no such column is refused,
    /// so a v1 row is never labelled v2.</summary>
    [Test]
    public void RowsAreV2AndCarryTheXrefAgreement()
    {
        var row = LogsEngine.RowsFrom(OneRow(), "dbsha", Expected).Single();
        var noGene = LogsEngine.RowsFrom(OneRow(null, agrees: null), "dbsha", Expected).Single();
        Assert.Multiple(() =>
        {
            Assert.That(LogsEngine.DefinitionId, Is.EqualTo("logs:DEF-GENE-RESOLUTION v2"));
            Assert.That(row["definition_id"], Is.EqualTo("logs:DEF-GENE-RESOLUTION v2"));
            Assert.That(row["ensembl_xref_agreement"], Is.EqualTo("agrees"));
            Assert.That(noGene["ensembl_xref_agreement"], Is.Null);
            Assert.That(Tables.ByName["gene_resolutions"].Columns.Single(c => c.Name == "ensembl_xref_agreement").Nullable, Is.True);
            Assert.That(LogsEngine.GeneResolutionColumns[^1], Is.EqualTo("ensembl_xref_agreement"), "mzLib #1578's last column");
            Assert.That(() => LogsEngine.RowsFrom(OneRow(withAgreement: false), "dbsha", Expected),
                Throws.TypeOf<RunnerException>().With.Message.Contains("wrote no ensembl_xref_agreement")
                    .And.Message.Contains("not logs:DEF-GENE-RESOLUTION v2"));
        });
    }

    /// <summary>A value is one of the schema's three words and pairs with <c>ensembl_xref_agrees</c> as logs 034's table
    /// says: <c>agrees</c> exactly where that is true, <c>silent</c> or <c>disagrees</c> where it is false, nothing where it
    /// has nothing.</summary>
    [Test]
    public void AnXrefAgreementIsOneOfThreeWordsAndPairsWithAgrees()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SchemaDocs.Enums["XrefAgreement"].Values, Is.EqualTo(new[] { "agrees", "silent", "disagrees" }));
            Assert.That(() => LogsEngine.RowsFrom(OneRow("agree"), "dbsha", Expected),
                Throws.TypeOf<RunnerException>().With.Message.Contains("ensembl_xref_agreement 'agree' is not one of ['agrees', 'silent', 'disagrees']"));
            foreach (var (word, agrees) in new (string?, bool?)[] { ("agrees", true), ("silent", false), ("disagrees", false), (null, null) })
                Assert.That(LogsEngine.RowsFrom(OneRow(word, agrees), "dbsha", Expected).Single()["ensembl_xref_agreement"], Is.EqualTo(word),
                    $"{word} beside {agrees}");
            foreach (var (word, agrees) in new (string?, bool?)[] { ("agrees", false), ("silent", true), ("disagrees", true), (null, true), (null, false), ("agrees", null), ("silent", null) })
                Assert.That(() => LogsEngine.RowsFrom(OneRow(word, agrees), "dbsha", Expected),
                    Throws.TypeOf<RunnerException>().With.Message.Contains("logs 034 has `agrees` exactly where"), $"{word} beside {agrees}");
        });
    }
}
