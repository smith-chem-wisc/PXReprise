using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>G84: the catalog takes each dataset's NON-content manifest fields (notes, flags, status, ...) from the
/// manifest it is built with, so a reworded note reaches readers at the next build, with no re-ingest.</summary>
/// <remarks>Runs on the current rules, outside any <see cref="SchemaContract.Python0320"/> scope: a parity build
/// writes no <c>dataset_annotations</c>, which <c>CatalogTests</c> proves by matching Python 0.32.0's catalogs.</remarks>
public class CatalogAnnotationTests
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

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-annotations-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Rewrites one line of the scratch manifest; fails if the line is not there, so a fixture change
    /// cannot turn an edit into a no-op.</summary>
    private void EditManifest(string line, string replacement)
    {
        var text = File.ReadAllText(ManifestPath);
        Assert.That(text, Does.Contain(line), "the fixture manifest changed under this test");
        File.WriteAllText(ManifestPath, text.Replace(line, replacement, StringComparison.Ordinal));
    }

    private const string FlagsLine = "    flags: [low_id_rate, no_design_file]\n";

    private (BundleRef Bundle, string BundleJsonSha) Ingest()
    {
        var result = Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
        var bundle = BundleRef.Load(result.BundlePath);
        return (bundle, BundleWriter.Sha256File(Path.Combine(bundle.Path, BundleWriter.ManifestName)));
    }

    private CatalogResult Build(string name)
    {
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, name), instance: manifest.Instance,
            artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    private static object?[] Annotation(CatalogResult built) =>
        CatalogBuilder.RunQuery(built.Path,
            $"SELECT in_manifest, status, notes, flags, provenance_schema FROM {CatalogBuilder.AnnotationsTable}").Rows.Single();

    [Test]
    public void EveryNonContentFieldIsAnAnnotationOrARunExclusionExceptTheRawRow()
    {
        var expected = DatasetEntry.NonContentFields.Keys.Where(k => k != "raw").Order(StringComparer.Ordinal);
        Assert.That(CatalogBuilder.AnnotationFields.Concat(CatalogBuilder.RunExclusionFields).Order(StringComparer.Ordinal), Is.EqualTo(expected),
            "a non-content manifest field must be decided: carried in dataset_annotations, in run_exclusions, or not and why");
        Assert.That(CatalogBuilder.AnnotationFields.Intersect(CatalogBuilder.RunExclusionFields), Is.Empty, "each field is served once");
        Assert.That(CatalogBuilder.AnnotationFields.Concat(CatalogBuilder.RunExclusionFields).Intersect(DatasetEntry.ContentFields), Is.Empty);
        var columns = CatalogBuilder.DerivedDocs[CatalogBuilder.AnnotationsTable].Columns.Keys;
        Assert.That(CatalogBuilder.AnnotationFields, Is.SubsetOf(columns), "every annotation column is described");
    }

    [Test]
    public void ARewordedNoteReachesTheCatalogAndMovesItsIdWithoutTouchingTheBundle()
    {
        var (bundle, bundleSha) = Ingest();

        var before = Build("before.duckdb");
        Assert.That(before.FailedChecks, Is.Empty);
        var row = Annotation(before);
        Assert.That(row[0], Is.EqualTo(true));
        Assert.That(row[1], Is.EqualTo("include"));
        Assert.That(row[2], Is.Null, "the fixture entry has no notes");
        Assert.That(row[3], Is.EqualTo(new List<string> { "low_id_rate", "no_design_file" }));
        Assert.That(row[4], Is.EqualTo("aging-provenance/2"));
        Assert.That(CatalogBuilder.RunQuery(before.Path, "SELECT catalog_version FROM catalog_meta").Rows.Single()[0], Is.EqualTo("11"));
        var check = before.Checks.Single(c => c.Kind == "manifest");
        Assert.That((check.Ok, check.Observed, check.Expected, check.Detail), Is.EqualTo((true, (long?)1, (long?)1, (string?)null)));

        EditManifest(FlagsLine, FlagsLine + "    notes: Runs VM_17 and VM_18 are excluded from analysis.\n");
        var noted = Build("noted.duckdb");
        Assert.That(Annotation(noted)[2], Is.EqualTo("Runs VM_17 and VM_18 are excluded from analysis."));
        Assert.That(noted.CatalogId, Is.Not.EqualTo(before.CatalogId), "a catalog telling a reader a different note is a different catalog");

        EditManifest("Runs VM_17 and VM_18 are excluded", "Runs VM_17 and VM_18 (failed QC) are excluded");
        var reworded = Build("reworded.duckdb");
        Assert.That(Annotation(reworded)[2], Is.EqualTo("Runs VM_17 and VM_18 (failed QC) are excluded from analysis."));
        Assert.That(reworded.CatalogId, Is.Not.EqualTo(noted.CatalogId));

        // The bundle is the same bundle, byte for byte, and still the only one: no re-ingest happened or was needed.
        foreach (var built in new[] { before, noted, reworded })
            Assert.That(built.Bundles.Single().BundleId, Is.EqualTo(bundle.BundleId));
        Assert.That(CatalogBuilder.DiscoverBundles(Store, "PXD999999").Select(b => b.BundleId), Is.EqualTo(new[] { bundle.BundleId }));
        Assert.That(BundleWriter.Sha256File(Path.Combine(bundle.Path, BundleWriter.ManifestName)), Is.EqualTo(bundleSha));

        // And the id is a function of what was built from: the recomputation agrees with the build.
        var manifest = Manifest.Load(ManifestPath);
        var (artefacts, _) = CatalogBuilder.SelectArtefacts(Store, reworded.Bundles);
        Assert.That(CatalogBuilder.CatalogId(reworded.Bundles, [], artefacts, manifest: manifest), Is.EqualTo(reworded.CatalogId));
    }

    [Test]
    public void ContentFieldsAreNotTakenFromTheBuildManifest()
    {
        Ingest();
        var before = Build("before.duckdb");
        // acquisition is a CONTENT field: it reached the bundle's rows and its hash, so only a re-ingest changes it.
        EditManifest("    acquisition: DDA\n", "    acquisition: DIA\n");
        var after = Build("after.duckdb");
        foreach (var built in new[] { before, after })
            Assert.That(CatalogBuilder.RunQuery(built.Path, "SELECT acquisition FROM datasets").Rows.Single()[0], Is.EqualTo("DDA"));
        Assert.That(after.CatalogId, Is.EqualTo(before.CatalogId), "a content field in the manifest does not move the catalog; the bundle does");
        var columns = CatalogBuilder.RunQuery(after.Path,
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{CatalogBuilder.AnnotationsTable}'").Rows.Select(r => r[0]);
        Assert.That(columns, Has.None.EqualTo("acquisition"));
    }

    [Test]
    public void ADatasetTheManifestDoesNotListKeepsNullsAndSaysSo()
    {
        var (bundle, _) = Ingest();
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        var withEntry = CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, "with.duckdb"), instance: manifest.Instance,
            artefacts: artefacts, engineChecks: checks, manifest: manifest);
        var orphan = CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, "orphan.duckdb"), instance: manifest.Instance,
            artefacts: artefacts, engineChecks: checks, manifest: manifest with { Datasets = new Dictionary<string, DatasetEntry>() });

        Assert.That(orphan.FailedChecks, Is.Empty, "a dataset without an entry builds");
        Assert.That(Annotation(orphan), Is.EqualTo(new object?[] { false, null, null, null, null }));
        var check = orphan.Checks.Single(c => c.Kind == "manifest");
        Assert.That((check.Ok, check.Observed, check.Expected), Is.EqualTo((true, (long?)0, (long?)1)));
        Assert.That(check.Detail, Does.Contain($"PXD999999 ({bundle.BundleId})"));
        Assert.That(CatalogBuilder.RunQuery(orphan.Path, "SELECT detail FROM catalog_checks WHERE kind = 'manifest'").Rows.Single()[0],
            Is.EqualTo(check.Detail));
        Assert.That(orphan.CatalogId, Is.Not.EqualTo(withEntry.CatalogId));
    }
}
