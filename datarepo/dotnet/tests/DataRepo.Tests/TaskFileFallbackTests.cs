using System.Text.Json.Nodes;
using DataRepo.Bundle;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>1.4.1: a search folder moved after it ran (PXReprise's <c>&lt;accession&gt;.superseded-&lt;stamp&gt;</c>) still
/// gives its task files, found under the entry's own search folder by their recorded sha256. Before, eleven of aging's
/// bundles silently kept none, and so declared no modification and knew no target/contaminant rule.</summary>
public class TaskFileFallbackTests
{
    private const string Accession = "PXD999999";
    private const string Moved = "PXD999999.superseded-20261022T180103Z";
    private string _root = "";

    private string Manifest => Path.Combine(_root, "manifest.yaml");
    private string SearchDir => Path.Combine(_root, "work_root", "run_test", Moved, "04_search");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [SetUp]
    public void CopyAndMoveTheSearch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-taskfallback-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(from, file)));
        // As PXReprise keeps a replaced search: the folder renamed, its provenance unchanged, the manifest repointed.
        Directory.Move(Path.Combine(_root, "work_root", "run_test", Accession), Path.Combine(_root, "work_root", "run_test", Moved));
        var text = File.ReadAllText(Manifest);
        Assert.That(text, Does.Contain($"run: run_test/{Accession}\n"));
        File.WriteAllText(Manifest, text.Replace($"run: run_test/{Accession}\n", $"run: run_test/{Moved}\n"), new System.Text.UTF8Encoding(false));
    }

    [TearDown]
    public void Remove()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private IngestResult Ingest() => Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);

    private static List<string> Codes(IngestResult result) =>
        ArrowTables.ReadParquet(Path.Combine(result.BundlePath, "findings.parquet")).Rows.Select(r => (string)r["code"]!).ToList();

    private static List<string> TaskSources(IngestResult result) =>
        Directory.EnumerateFiles(Path.Combine(result.BundlePath, "sources"), "*Task.toml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()!;

    [Test]
    public void AMovedTaskFileWithItsRecordedSha256IsFound()
    {
        // The fixture records placeholder hashes; record the files' real ones, as a real search does.
        var provenancePath = Path.Combine(SearchDir, "provenance.json");
        var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
        foreach (var input in provenance["inputs"]!.AsArray())
        {
            var path = (string)input!["path"]!;
            if (path.EndsWith("Task.toml", StringComparison.OrdinalIgnoreCase))
                input["sha256"] = BundleWriter.Sha256File(Path.Combine(SearchDir, "tasks", Path.GetFileName(path)));
        }
        File.WriteAllText(provenancePath, provenance.ToJsonString());

        var result = Ingest();
        Assert.Multiple(() =>
        {
            Assert.That(TaskSources(result), Is.EqualTo(new[] { "2_GptmdTask.toml", "3_SearchTask.toml" }));
            Assert.That(Codes(result), Has.No.Member("task_files_missing"));
            Assert.That(File.Exists(Path.Combine(result.BundlePath, "search_modifications_declared.parquet")), Is.True,
                "the declared modifications come from the found task files");
        });
    }

    [Test]
    public void AMovedTaskFileWhoseSha256DiffersIsNotTakenAndAFindingSaysSo()
    {
        // The fixture's placeholder hashes match no file: a file of the same name is not the one the search ran.
        var result = Ingest();
        Assert.Multiple(() =>
        {
            Assert.That(Directory.EnumerateFiles(Path.Combine(result.BundlePath, "sources"), "*Task.toml"), Is.Empty);
            Assert.That(Codes(result), Has.Member("task_files_missing"));
        });
    }
}
