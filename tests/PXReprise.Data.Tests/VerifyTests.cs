using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Bundle;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 6 (D61): a bundle records each table file's sha256 in <c>bundle.json</c>
/// (<c>table_sha256</c>, beside <c>tables</c>), and <c>datarepo verify</c> re-hashes the tables and the copied sources on
/// demand: changed, missing and extra files are problems; a bundle with no table checksums is "not verified".</summary>
public class VerifyTests
{
    private static readonly string Fixture = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999", "aeb10630abbcaf72");

    /// <summary>A real dataset row (Python's for the fixture, with 0.0.17's confidence_rule) and one copied source, in a
    /// new store; returns the bundle folder.</summary>
    private static string WriteBundle(string store)
    {
        var (_, datasets) = ArrowTables.ReadParquet(Path.Combine(Fixture, "datasets.parquet"));
        foreach (var row in datasets) row["confidence_rule"] = "pep_q_value";
        var results = Path.Combine(store, "results.txt");
        Directory.CreateDirectory(store);
        File.WriteAllText(results, "All target PSMs with q-value <= 0.01: 3\n");
        var writer = new BundleWriter(store, "PXD999999");
        writer.AddDeclaration("manifest_entry", new Dictionary<string, object?> { ["accession"] = "PXD999999" });
        writer.AddSource(results, "results_txt", "results.txt");
        writer.Add("datasets", datasets);
        return writer.Write();
    }

    private static void InStore(Action<string, string> test)
    {
        var store = Path.Combine(Path.GetTempPath(), "datarepo-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            test(store, WriteBundle(store));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void ABundleRecordsEachTableFilesSha256BesideItsRowCounts() => InStore((_, bundle) =>
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, BundleWriter.ManifestName)))!.AsObject();
        var keys = manifest.Select(kv => kv.Key).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(manifest["tables"]!["datasets"]!.GetValue<long>(), Is.EqualTo(1), "tables keeps its shape: row counts");
            Assert.That(manifest[BundleVerifier.TableHashesKey]!["datasets"]!.GetValue<string>(),
                Is.EqualTo(BundleWriter.Sha256File(Path.Combine(bundle, "datasets.parquet"))));
            Assert.That(keys.IndexOf(BundleVerifier.TableHashesKey), Is.EqualTo(keys.IndexOf("tables") + 1));
            Assert.That(manifest[BundleVerifier.TableHashesKey]!.AsObject().Select(kv => kv.Key), Is.EqualTo(manifest["tables"]!.AsObject().Select(kv => kv.Key)));
        });
    });

    [Test]
    public void AFreshBundleVerifies() => InStore((_, bundle) =>
    {
        var result = BundleVerifier.Verify(bundle);
        Assert.That((result.Ok, result.TablesRecorded, result.TablesChecked, result.SourcesChecked, result.DatasetId),
            Is.EqualTo((true, true, 1, 1, "PXD999999")));
    });

    /// <summary>Every way a file can stop being what the bundle recorded: a table or a copied source rewritten, a table
    /// deleted, a file added beside the tables or among the sources.</summary>
    [Test]
    public void EveryChangedMissingOrExtraFileIsAProblem() => InStore((_, bundle) =>
    {
        File.AppendAllText(Path.Combine(bundle, "datasets.parquet"), "x");
        File.AppendAllText(Path.Combine(bundle, "sources", "results.txt"), "edited\n");
        File.WriteAllText(Path.Combine(bundle, "stray.parquet"), "");
        File.WriteAllText(Path.Combine(bundle, "sources", "stray.txt"), "");
        var problems = BundleVerifier.Verify(bundle).Problems.Select(p => (p.Kind, p.File)).ToList();
        Assert.That(problems, Is.EquivalentTo(new[]
        {
            ("changed", "datasets.parquet"), ("extra", "stray.parquet"), ("changed", "sources/results.txt"), ("extra", "sources/stray.txt"),
        }));
        File.Delete(Path.Combine(bundle, "datasets.parquet"));
        Assert.That(BundleVerifier.Verify(bundle).Problems.Select(p => (p.Kind, p.File)), Does.Contain(("missing", "datasets.parquet")));
        Assert.That(BundleVerifier.Verify(bundle).Problems.First(p => p.Kind == "changed").Detail, Does.StartWith("recorded "));
    });

    /// <summary>A bundle written before 1.5.0 records no table checksums: its tables are not verified, which is never
    /// reported as passed, while its copied sources, recorded since the first ingest, still are.</summary>
    [Test]
    public void ABundleWithoutTableChecksumsIsNotVerified() => InStore((_, bundle) =>
    {
        var path = Path.Combine(bundle, BundleWriter.ManifestName);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest.Remove(BundleVerifier.TableHashesKey);
        File.WriteAllText(path, manifest.ToJsonString());
        File.AppendAllText(Path.Combine(bundle, "datasets.parquet"), "x");
        var result = BundleVerifier.Verify(bundle);
        Assert.That((result.Ok, result.TablesRecorded, result.TablesChecked, result.SourcesChecked), Is.EqualTo((true, false, 0, 1)));
    });

    [Test]
    public void AStoreListsItsBundlesAndNotItsEngineOrStudyFolders() => InStore((store, bundle) =>
    {
        Directory.CreateDirectory(Path.Combine(store, "_engine", "logs.resolve_genes", "abc"));
        File.WriteAllText(Path.Combine(store, "_engine", "logs.resolve_genes", "abc", BundleWriter.ManifestName), "{}");
        Directory.CreateDirectory(Path.Combine(store, "_study", "aging"));
        Assert.That(BundleVerifier.BundlesIn(store), Is.EqualTo(new[] { bundle }));
    });

    /// <summary>The command: exit 0 when nothing differs (a bundle with no checksums is listed as not verified, and
    /// counted), 1 when any file does.</summary>
    [Test]
    public void TheCommandFailsOnlyWhenAFileDiffers() => InStore((store, bundle) =>
    {
        var ok = new StringWriter();
        var code = PXReprise.DataCli.Cli.Verify(["--store", store], ok);
        File.AppendAllText(Path.Combine(bundle, "datasets.parquet"), "x");
        var bad = new StringWriter();
        var failed = PXReprise.DataCli.Cli.Verify([bundle], bad);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Zero);
            Assert.That(ok.ToString(), Does.Contain("ok: 1 table(s), 1 copied source(s)").And.Contain("verified 1 bundle(s): 1 ok, 0 without table checksums (not verified), 0 with problems"));
            Assert.That(failed, Is.EqualTo(1));
            Assert.That(bad.ToString(), Does.Contain("1 problem(s)").And.Contain("changed  datasets.parquet  recorded "));
        });
    });
}
