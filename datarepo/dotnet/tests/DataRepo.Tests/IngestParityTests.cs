using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

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
            string SchemaOf(IngestResult r) => System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(r.BundlePath, "bundle.json"))).RootElement.GetProperty("schema_version").GetString()!;
            Assert.That(SchemaOf(current), Is.EqualTo(Tables.SchemaVersion));
            Assert.That(SchemaOf(parity), Is.EqualTo(SchemaContract.Python0320SchemaVersion), "a parity bundle is written as Python 0.32.0 wrote it");
            Assert.That(SchemaContract.Version, Is.EqualTo(Tables.SchemaVersion), "the parity ingest's schema scope ends with the ingest");
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    /// <summary>QuantProject 009 (DATAREPO-Q1): a count entry of 0/N is a covered, unmodified site, a measured zero.
    /// Its intensity entry prints 0.0000(0/I) exactly like a floor, so only the count numerator tells them apart.</summary>
    [Test]
    public void ACountNumeratorOfZeroIsCoveredZeroNotAFloor()
    {
        var copy = Path.Combine(Path.GetTempPath(), "datarepo-covered-zero-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Path.Combine(RepoRoot(), "tests", "data"), copy);
            var table = Path.Combine(copy, "work_root", "run_test", "PXD999999", "04_search", "mm", "Task3SearchTask", "AllQuantifiedProteinGroups.tsv");
            var text = File.ReadAllText(table);
            // Row 2's second segment is a floor today: count 2/2, intensity 0/2500. Make its count 0/2.
            const string floor = "pos129[Carbamidomethyl on C,info:fraction=1.00(2/2)]";
            Assert.That(text, Does.Contain(floor));
            File.WriteAllText(table, text.Replace(floor, "pos129[Carbamidomethyl on C,info:fraction=0.00(0/2)]"));

            var store = Path.Combine(copy, "out");
            var manifest = Path.Combine(copy, "manifest.yaml");
            string StateOf(IngestRules rules)
            {
                var result = Ingester.Ingest(manifest, "PXD999999", store, rules: rules, overwrite: true);
                var (_, rows) = ArrowTables.ReadParquet(Path.Combine(result.BundlePath, "ptm_stoichiometry.parquet"));
                var row = rows.Single(r => r["n_modified_psms"] is 0L && r["n_covering_psms"] is 2L);
                return $"{row["occupancy_state"]}/{row["intensity_is_floor"]}";
            }
            Assert.That(StateOf(IngestRules.Current), Is.EqualTo("covered_zero/False"));
            Assert.That(StateOf(IngestRules.Python0320), Is.EqualTo("floor/True"), "0.32.0 had no fifth state; parity mode keeps its rows");

            var current = Ingester.Ingest(manifest, "PXD999999", store, rules: IngestRules.Current, overwrite: true);
            var (_, definitions) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "definitions.parquet"));
            var occupancy = definitions.Single(r => (string)r["definition_id"]! == Definitions.Occupancy.DefinitionId);
            Assert.That(occupancy["version"], Is.EqualTo("v3.6"));
            Assert.That((string)occupancy["text"]!, Does.Contain("DEF-OCC-COVERED-ZERO").And.Contain("DEF-OCC-ABSENT v3.6"));
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    /// <summary>PXReprise 009: <c>datarepo manifest</c> refuses a run map exactly where ingest would, with ingest's
    /// own words, reading the runs as ingest reads them.</summary>
    [Test]
    public void ManifestChecksRefuseTheRunMapsIngestRefuses()
    {
        var manifest = Manifest.Load(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"));
        var entry = manifest.Datasets["PXD999999"];
        var (_, runs) = ArrowTables.ReadParquet(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0", "PXD999999", "aeb10630abbcaf72", "runs.parquet"));
        var names = runs.Select(r => Path.GetFileNameWithoutExtension((string)r["file_name"]!)).Order(StringComparer.Ordinal).ToList();
        Assume.That(names, Has.Count.GreaterThan(1));

        var good = entry with { RunEnrichment = names.Select(n => (n, "none")).ToList() };
        var (none, read) = Ingester.RunEnrichmentProblems(manifest, good);
        Assert.That(read, "the fixture's runs are reachable");
        Assert.That(none, Is.Empty);

        var bad = entry with { RunEnrichment = names.Skip(1).Select(n => (n, "none")).Append(("not_a_run", "none")).ToList() };
        var (problems, _) = Ingester.RunEnrichmentProblems(manifest, bad);
        Assert.That(problems, Has.Count.EqualTo(2));
        Assert.That(problems[0], Does.Contain("names 1 run(s) that are not runs of this dataset: not_a_run"));
        Assert.That(problems[1], Does.Contain($"Missing: {names[0]}"));

        var store = Path.Combine(Path.GetTempPath(), "datarepo-runmap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.That(() => Ingester.IngestDataset(manifest, bad, store),
                Throws.TypeOf<IngestException>().With.Message.EqualTo(problems[0]), "manifest and ingest must refuse in the same words");
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }

        // With no runs readable, the rules that need none still apply.
        var unreadable = Runs.EnrichmentProblems(null, "PXD999999", ["other"], mixed: true, [("a", "other"), ("b", "other")]);
        Assert.That(unreadable, Has.Count.EqualTo(1).And.Some.Contains("flagged mixed_enrichment"));
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
