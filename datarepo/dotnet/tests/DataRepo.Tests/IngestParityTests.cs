using DataRepo.Bundle;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>Phase 2's bar on the repository's own test dataset: the C# ingester writes the rows the Python one wrote.</summary>
/// <remarks>The expected bundle is <c>Fixtures/python-0.32.0</c>, the Python 0.32.0 ingester's output for
/// <c>tests/data/manifest.yaml</c> PXD999999 (identical under pyMzLib 0.2.0 and 0.4.0, checked 2026-10-04).
/// The bundle id differs on purpose (the C# ingest path is its own version line); every row must not.</remarks>
public class IngestParityTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [Test]
    public void TheFixtureDatasetIngestsToThePythonRows()
    {
        var store = Path.Combine(Path.GetTempPath(), "datarepo-ingest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = Ingester.Ingest(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), "PXD999999", store);
            var expected = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999", "aeb10630abbcaf72");
            var differences = RoundTrip.CompareBundles(expected, result.BundlePath);
            Assert.That(differences, Is.Empty, string.Join("\n", differences));
            Assert.That(result.BundleId, Is.Not.EqualTo("aeb10630abbcaf72"), "the C# ingest path is its own version line");
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void ADatasetTheProducerExcludedIsRefused()
    {
        var manifest = Manifest.Load(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"));
        var excluded = manifest.Datasets.Values.FirstOrDefault(e => !e.Ingestable);
        Assume.That(excluded, Is.Not.Null, "the fixture manifest has no excluded dataset");
        Assert.That(() => Ingester.IngestDataset(manifest, excluded!, Path.GetTempPath()),
            Throws.TypeOf<DatasetExcludedException>().With.Message.Contains("will not be ingested"));
    }

    [Test]
    public void TheManifestDeclarationHashesAsPythonDid()
    {
        var manifest = Manifest.Load(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"));
        var declaration = PyFormat.Json(manifest.Datasets["PXD999999"].ContentDeclaration());
        var python = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "manifest_entry_declaration.json"));
        Assert.That(declaration, Is.EqualTo(python));
    }
}
