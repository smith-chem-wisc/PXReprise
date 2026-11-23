using PXReprise.Bundle;
using PXReprise.Ingest;

namespace PXReprise.Data.Tests;

/// <summary>
/// A protein database or MetaMorpheus settings folder the search used, missing from disk, is a finding of its own
/// (cs-1.4.0). Before, a missing database was named only at the tail of <c>unplaced_ptm_sites</c>, and a missing
/// settings folder only showed as a longer <c>unresolved_modifications</c> list. A manifest naming the wrong work_root
/// loses both at once, and the ingest still exits 0 (PXReprise #46, found by the seam test).
/// </summary>
[NonParallelizable]
public class SearchInputsMissingTests
{
    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string WorkRoot => Path.Combine(_root, "data", "work_root");

    [SetUp]
    public void CopyInstance()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-missing-inputs-" + Guid.NewGuid().ToString("N"));
        var to = Path.Combine(_root, "data");
        foreach (var dir in Directory.EnumerateDirectories(DataPaths.Instance, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(DataPaths.Instance, dir)));
        foreach (var file in Directory.EnumerateFiles(DataPaths.Instance, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(DataPaths.Instance, file)));
    }

    [TearDown]
    public void RemoveScratch()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    private IngestResult Ingest(IngestRules rules = IngestRules.Current) =>
        Ingester.Ingest(ManifestPath, "PXD999999", Store, overwrite: true, rules: rules);

    private static List<string> Codes(IngestResult result) => result.Findings.Select(f => (string)f["code"]!).ToList();

    private static string Message(IngestResult result, string code) => (string)result.Findings.Single(f => (string)f["code"]! == code)["message"]!;

    [Test]
    public void AnIntactInstanceRaisesNeither()
    {
        var codes = Codes(Ingest());
        Assert.That(codes, Does.Not.Contain("search_database_missing").And.Not.Contain("search_settings_missing"));
    }

    [Test]
    public void AMissingDatabaseIsNamedWithTheSha256TheSearchRecorded()
    {
        var database = Path.Combine(WorkRoot, "db", "test_human.xml");
        var sha = BundleWriter.Sha256File(database);
        File.Delete(database);

        var result = Ingest();

        Assert.That(Codes(result), Does.Contain("search_database_missing"));
        var message = Message(result, "search_database_missing");
        Assert.That(message, Does.StartWith("1 protein database(s) the search provenance names are not on disk"));
        Assert.That(message, Does.Contain("test_human.xml").And.Contain($"(sha256 {sha})"), "where it was looked for, and the hash that finds a kept copy");
        Assert.That(message, Does.Not.Contain("test_contaminants.xml"), "only the missing one is named");
    }

    [Test]
    public void AMissingSettingsFolderSaysWhyModificationsAreUnresolved()
    {
        var resolved = Ingest().UnresolvedModifications.Count;
        Directory.Delete(Path.Combine(WorkRoot, "mm_settings", "1.1.11"), recursive: true);

        var result = Ingest();

        Assert.That(Codes(result), Does.Contain("search_settings_missing"));
        Assert.That(Message(result, "search_settings_missing"), Does.Contain(Path.Combine("mm_settings", "1.1.11")).And.Contain("the default"));
        Assert.That(result.UnresolvedModifications.Count, Is.GreaterThan(resolved), "the effect the finding explains");
    }

    [Test]
    public void ASettingsFolderNamedByTheCallerIsSaidToBeTheirs()
    {
        var result = Ingester.Ingest(ManifestPath, "PXD999999", Store, mmSettings: Path.Combine(_root, "no-such-install"), overwrite: true);

        Assert.That(Message(result, "search_settings_missing"), Does.Contain("no-such-install").And.Contain("given by --mm-settings"));
    }

    [Test]
    public void TheFrozenPythonRulesRaiseNeither()
    {
        File.Delete(Path.Combine(WorkRoot, "db", "test_human.xml"));
        Directory.Delete(Path.Combine(WorkRoot, "mm_settings", "1.1.11"), recursive: true);

        var codes = Codes(Ingest(IngestRules.Python0320));

        Assert.That(codes, Does.Not.Contain("search_database_missing").And.Not.Contain("search_settings_missing"),
            "Python 0.32.0 raised neither, and its rows stay reproducible (IngestRules.Python0320)");
    }
}
