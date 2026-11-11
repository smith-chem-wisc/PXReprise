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
                .And.Message.Contains("not logs:DEF-GENE-RESOLUTION v1"));
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
            // A column added since Python wrote its rows is compared as empty, then set aside (schema 0.0.17's
            // ensembl_xref_agreement: NULL on every v1 row).
            foreach (var (_, table, column) in SchemaContract.AddedColumns.Where(a => a.Table == "gene_resolutions"))
                foreach (var row in actual)
                {
                    Assert.That(row.GetValueOrDefault(column), Is.Null, column);
                    row.Remove(column);
                }
            Assert.That(RoundTrip.FirstDifference(expected, actual), Is.Null);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    /// <summary>A resolver's table of one row, as <see cref="LogsEngine.RowsFrom"/> takes it; with
    /// <c>ensembl_xref_agreement</c> when <paramref name="agreement"/> is given, as mzLib #1578 would write it.</summary>
    private static ResolveResult OneRow(object? agreement = null, bool withAgreement = false)
    {
        var cells = new Dictionary<string, object?>
        {
            ["source_index"] = 0L, ["source_path"] = "db.xml", ["accession"] = "P12345", ["outcome"] = SchemaDocs.Enums["GeneResolutionOutcome"].Values[0],
            ["gene_id"] = "ENSG00000000001", ["search_database_sha256"] = "dbsha", ["gene_set_sha256"] = "gs", ["gene_set_release"] = "114",
            ["ensembl_xref_sha256"] = "xr",
        };
        if (withAgreement) cells["ensembl_xref_agreement"] = agreement;
        return new ResolveResult
        {
            ColumnNames = cells.Keys.ToList(), Columns = cells.ToDictionary(kv => kv.Key, kv => new List<object?> { kv.Value }),
            OutcomeCounts = new Dictionary<string, long>(), Caveats = [],
        };
    }

    private static readonly Dictionary<string, object?> Expected = new() { ["gene_set_sha256"] = "gs", ["ensembl_xref_sha256"] = "xr", ["gene_set_release"] = "114" };

    /// <summary>Schema 0.0.17 (logs 034/035, D59): until our mzLib carries #1578, the resolver writes no
    /// <c>ensembl_xref_agreement</c>, so the column is empty and every row is v1.</summary>
    [Test]
    public void TodaysRowsAreV1AndCarryNoXrefAgreement()
    {
        var row = LogsEngine.RowsFrom(OneRow(), "dbsha", Expected).Single();
        var empty = LogsEngine.RowsFrom(OneRow(null, withAgreement: true), "dbsha", Expected).Single();
        Assert.Multiple(() =>
        {
            Assert.That(row["definition_id"], Is.EqualTo("logs:DEF-GENE-RESOLUTION v1"));
            Assert.That(row.ContainsKey("ensembl_xref_agreement"), Is.False, "the writer stores it as NULL");
            Assert.That(empty["ensembl_xref_agreement"], Is.Null);
            Assert.That(Tables.ByName["gene_resolutions"].Columns.Single(c => c.Name == "ensembl_xref_agreement").Nullable, Is.True);
        });
    }

    /// <summary>A filled value is one of the schema's three words, and never sits on a v1 row: filling it is the runner
    /// change that labels rows v2.</summary>
    [Test]
    public void AnXrefAgreementIsOneOfThreeWordsAndNeverOnAV1Row()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SchemaDocs.Enums["XrefAgreement"].Values, Is.EqualTo(new[] { "agrees", "silent", "disagrees" }));
            Assert.That(() => LogsEngine.RowsFrom(OneRow("agree", withAgreement: true), "dbsha", Expected),
                Throws.TypeOf<RunnerException>().With.Message.Contains("ensembl_xref_agreement 'agree' is not one of ['agrees', 'silent', 'disagrees']"));
            foreach (var word in new[] { "agrees", "silent", "disagrees" })
                Assert.That(() => LogsEngine.RowsFrom(OneRow(word, withAgreement: true), "dbsha", Expected),
                    Throws.TypeOf<RunnerException>().With.Message.Contains("is logs:DEF-GENE-RESOLUTION v2's column"), word);
        });
    }
}
