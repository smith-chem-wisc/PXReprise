using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Runner;

namespace DataRepo.Tests;

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
            Assert.That(RoundTrip.FirstDifference(expected, actual), Is.Null);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
}
