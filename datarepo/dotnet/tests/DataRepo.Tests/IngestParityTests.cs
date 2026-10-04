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
            var result = Ingester.Ingest(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), "PXD999999", store, rules: IngestRules.Python0320);
            var expected = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999", "aeb10630abbcaf72");
            var differences = RoundTrip.CompareBundles(expected, result.BundlePath);
            Assert.That(differences, Is.Empty, string.Join("\n", differences));
            Assert.That(result.BundleId, Is.Not.EqualTo("aeb10630abbcaf72"), "the C# ingest path is its own version line");
            Assert.That(File.ReadAllText(Path.Combine(result.BundlePath, "bundle.json")), Does.Contain(Ingester.Python0320ParityPath));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void CurrentRulesComputeSpecificityFromTheSearchedSequencesAndKeyPepOnIteration()
    {
        var store = Path.Combine(Path.GetTempPath(), "datarepo-ingest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var current = Ingester.Ingest(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), "PXD999999", store, rules: IngestRules.Current);
            var (_, peptidoforms) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "peptidoforms.parquet"));
            // D40: is_isoform_specific is filled wherever the sequence is in the searched databases, and never
            // claims more than is_unique does.
            Assert.That(peptidoforms.Any(r => r["is_isoform_specific"] is not null), "G76 left is_isoform_specific NULL everywhere");
            Assert.That(peptidoforms.Where(r => r["is_isoform_specific"] is true).All(r => r["is_unique"] is true));
            Assert.That(peptidoforms.Where(r => r["is_unique"] is null).All(r => r["is_isoform_specific"] is null));

            var (_, definitions) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "definitions.parquet"));
            var pep = definitions.Single(r => (string)r["definition_id"]! == Definitions.PepId);
            Assert.That((string)pep["version"]!, Does.EndWith("; iterative off"), "G81: MetaMorpheus 1.1.11 predates iterative PEP");

            var parity = Ingester.Ingest(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"), "PXD999999", store, rules: IngestRules.Python0320);
            Assert.That(parity.BundleId, Is.Not.EqualTo(current.BundleId), "a parity bundle must never share an id with a real one");
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
