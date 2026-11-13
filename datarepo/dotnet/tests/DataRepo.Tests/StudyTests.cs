using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using DataRepo.Study;

namespace DataRepo.Tests;

/// <summary>Phase 3's bar for <c>study.py</c>: the C# study writer gives Python 0.32.0's bundle id, rows,
/// <c>study.json</c> and refusals for the same delivery.</summary>
/// <remarks>Expected outputs are <c>Fixtures/study/expected.json</c> and <c>Fixtures/study/python-0.32.0</c>, written by
/// Python's <c>study.load_study_manifest</c> + <c>write_study_bundle</c> on the deliveries under
/// <c>Fixtures/study/cases</c> (see its PROVENANCE.md).</remarks>
public class StudyTests
{
    // Every expectation here was written by Python 0.32.0, on schema 0.0.13.
    private IDisposable? _schema;
    [SetUp] public void PinPythonSchema() => _schema = SchemaContract.Python0320();
    [TearDown] public void UnpinPythonSchema() => _schema?.Dispose();

    private static readonly string Root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "study");

    private static JsonObject Expected() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "expected.json")))!["cases"]!.AsObject();

    public static IEnumerable<string> Cases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(typeof(StudyTests).Assembly.Location)!, "Fixtures", "study", "expected.json")))!["cases"]!
            .AsObject().Select(kv => kv.Key);

    private static string CaseDir(string name) => Path.Combine(Root, "cases", name);

    private static string Normalize(string text, string directory) => text.Replace(directory, "<dir>").Replace('\\', '/');

    private static string TempStore() => Path.Combine(Path.GetTempPath(), "datarepo-study-" + Guid.NewGuid().ToString("N"));

    /// <summary>A study.json with the fields that differ by design masked: when it was written, which package
    /// wrote it, and where the delivered files sat.</summary>
    private static string Masked(string text) =>
        Regex.Replace(
            Regex.Replace(text.Replace("\r\n", "\n"), "\"written_utc\": \"[^\"]*\"", "\"written_utc\": <masked>"),
            "\"(version|path)\": \"(?!<declaration)[^\"]*\"", "\"$1\": <masked>");

    private static string MaskedNotes(string text) =>
        Regex.Replace(text, "\"notes\": \"(?:[^\"\\\\]|\\\\.)*\"", "\"notes\": <masked>");

    [TestCaseSource(nameof(Cases))]
    public void EachDeliveryGivesWhatPythonGave(string name)
    {
        var expected = Expected()[name]!.AsObject();
        var directory = CaseDir(name);
        var store = TempStore();
        try
        {
            StudyResult result;
            try
            {
                result = StudyWriter.WriteStudyBundle(StudyWriter.LoadStudyManifest(Path.Combine(directory, "study.yaml")), store);
            }
            catch (Exception exc) when ((bool)expected["ok"]! == false)
            {
                var type = (string)expected["type"]!;
                var csType = type switch
                {
                    "ManifestError" => typeof(ManifestException),
                    "IngestError" => typeof(IngestException),
                    "TypeError" => typeof(InvalidOperationException),
                    "ValueError" => typeof(FormatException),
                    _ => throw new AssertionException($"unmapped Python exception {type}"),
                };
                Assert.That(exc, Is.TypeOf(csType), exc.ToString());
                var message = (string)expected["message"]!;
                if (type == "ValueError") return;  // Python's float() wording; the C# parser's differs (reported)
                if (name == "bad_yaml")
                {
                    // PyYAML's parser message is its own; the refusal around it is kept word for word.
                    var prefix = message[..(message.IndexOf("YAML: ", StringComparison.Ordinal) + 6)];
                    Assert.That(Normalize(exc.Message, directory), Does.StartWith(prefix));
                    return;
                }
                Assert.That(Normalize(exc.Message, directory), Is.EqualTo(message));
                return;
            }
            Assert.That((bool)expected["ok"]!, Is.True, $"Python refused this delivery: {expected["message"]}");

            var id = (string)expected["bundle_id"]!;
            Assert.That(result.BundleId, Is.EqualTo(id), "the study bundle id hashes only inputs and versions, so it must match Python's");
            Assert.That(result.Skipped, Is.False);
            var counts = expected["row_counts"]!.AsObject().ToDictionary(kv => kv.Key, kv => (long)kv.Value!);
            Assert.That(result.RowCounts, Is.EquivalentTo(counts));

            var pythonBundle = Path.Combine(Root, "python-0.32.0", name, id);
            var differences = RoundTrip.CompareBundles(pythonBundle, result.BundlePath);
            Assert.That(differences, Is.Empty, string.Join("\n", differences));

            // study.json: the same document field by field (paths relative to the delivery) ...
            var written = JsonNode.Parse(File.ReadAllText(Path.Combine(result.BundlePath, StudyWriter.StudyBundleManifest)))!.AsObject();
            Assert.That(Regex.IsMatch((string)written["written_utc"]!, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$"));
            written.Remove("written_utc");
            written["ingester"]!.AsObject().Remove("version");
            foreach (var source in written["sources"]!.AsArray())
                source!["path"] = Normalize((string)source["path"]!, directory);
            Assert.That(written.ToJsonString(), Is.EqualTo(expected["study_json"]!.ToJsonString()));
            // ... and the same text: Python's json.dumps(indent=2) layout and ASCII escapes.
            Assert.That(Masked(File.ReadAllText(Path.Combine(result.BundlePath, StudyWriter.StudyBundleManifest))),
                Is.EqualTo(Masked(File.ReadAllText(Path.Combine(pythonBundle, StudyWriter.StudyBundleManifest)))));

            // An unchanged delivery is a no-op, and says what it holds.
            var again = StudyWriter.WriteStudyBundle(StudyWriter.LoadStudyManifest(Path.Combine(directory, "study.yaml")), store);
            Assert.That(again.Skipped, Is.True);
            Assert.That(again.RowCounts, Is.EquivalentTo(counts));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void TheManifestFieldsAreAllClassified()
    {
        var all = StudyWriter.StudyContentFields.Concat(StudyWriter.StudyNonContentFields.Keys).ToList();
        Assert.That(all, Is.Unique);
        Assert.That(all.Order(), Is.EqualTo(new[]
        {
            "definitions", "delivery", "instance", "layer", "layer_version", "notes", "path", "raw", "store",
            "study_manifest_version", "tables",
        }));
    }

    [Test]
    public void ProseDoesNotMoveTheIdAndAByteDoes()
    {
        var work = TempStore();
        try
        {
            CopyDirectory(CaseDir("example"), work);
            var manifest = Path.Combine(work, "study.yaml");
            var id = StudyWriter.StudyBundleId(StudyWriter.LoadStudyManifest(manifest), StudyWriter.Sources(StudyWriter.LoadStudyManifest(manifest)));
            Assert.That(id, Is.EqualTo((string)Expected()["example"]!["bundle_id"]!));

            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("delivery: stage7-example", "delivery: renamed")
                .Replace("instance: ncems-aging", "instance: elsewhere"));
            var loaded = StudyWriter.LoadStudyManifest(manifest);
            Assert.That(StudyWriter.StudyBundleId(loaded, StudyWriter.Sources(loaded)), Is.EqualTo(id));

            File.AppendAllText(Path.Combine(work, "sample_ages.tsv"), "PXD999999:PXD999999-Sample-3\t80Y\t80\t\t\tsdrf-age/0.1.0\tsdrf\n");
            loaded = StudyWriter.LoadStudyManifest(manifest);
            Assert.That(StudyWriter.StudyBundleId(loaded, StudyWriter.Sources(loaded)), Is.Not.EqualTo(id));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    [Test]
    public void TheRepositorysExampleDeliveryWritesThePythonRows()
    {
        // examples/study_delivery as checked out: its bytes (and so its id) follow the checkout's line endings,
        // its rows do not.
        var example = Path.Combine(RepoRoot(), "examples", "study_delivery", "study.yaml");
        var store = TempStore();
        try
        {
            var result = StudyWriter.WriteStudyBundle(StudyWriter.LoadStudyManifest(example), store);
            var pythonBundle = Path.Combine(Root, "python-0.32.0", "example", (string)Expected()["example"]!["bundle_id"]!);
            var differences = RoundTrip.CompareBundles(pythonBundle, result.BundlePath);
            Assert.That(differences, Is.Empty, string.Join("\n", differences));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void PythonsCsvReaderIsReproduced()
    {
        static string Show(IEnumerable<List<string>> records) =>
            string.Join(" | ", records.Select(r => "[" + string.Join(",", r.Select(f => $"<{f}>")) + "]"));
        // Each expectation is Python 3.13's list(csv.reader(io.StringIO(text, newline=''), delimiter=d)).
        Assert.That(Show(PyCsv.Records("a\t\"b\tc\"\td\n", '\t')), Is.EqualTo("[<a>,<b\tc>,<d>]"));
        Assert.That(Show(PyCsv.Records("a,\"x\"y,z\r\n\r\nq", ',')), Is.EqualTo("[<a>,<xy>,<z>] | [] | [<q>]"));
        Assert.That(Show(PyCsv.Records("a,b\"c,\"d\ne\"", ',')), Is.EqualTo("[<a>,<b\"c>,<d\ne>]"));
        Assert.That(Show(PyCsv.Records("\"open", ',')), Is.EqualTo("[<open>]"));
        Assert.That(Show(PyCsv.Records("a\rb\r\n", ',')), Is.EqualTo("[<a>] | [<b>]"));
        Assert.That(Show(PyCsv.Records(",\n", ',')), Is.EqualTo("[<>,<>]"));
    }

    [Test, Category("RealData")]
    public void AgingsSampleAgesDeliveryGivesTheStoredBundle()
    {
        // aging's real delivery, and the bundle Python 0.32.0 wrote from it into aging's store (read only).
        const string delivery = "E:/CodeReview/aging/instance/ages/delivery/study.yaml";
        const string stored = "F:/aging_data/repo/store/_study/aging";
        if (!File.Exists(delivery) || !Directory.Exists(stored)) Assert.Ignore("aging's delivery or store is not on this machine");
        var store = TempStore();
        try
        {
            var manifest = StudyWriter.LoadStudyManifest(delivery);
            var result = StudyWriter.WriteStudyBundle(manifest, store);
            var python = Path.Combine(stored, result.BundleId);
            if (!Directory.Exists(python))
                Assert.Ignore($"no stored bundle {result.BundleId}: the delivery changed after Python last wrote it");
            var differences = RoundTrip.CompareBundles(python, result.BundlePath);
            Assert.That(differences, Is.Empty, string.Join("\n", differences));
            // `notes` is prose: rewording it keeps the bundle id (ProseDoesNotMoveTheIdAndAByteDoes), so aging can
            // reword the live delivery after Python wrote the stored bundle (they did, 2026-10-05, aging 427fc31).
            // Compare everything else to Python's bundle, and the notes to the delivery as it is now.
            var written = File.ReadAllText(Path.Combine(result.BundlePath, StudyWriter.StudyBundleManifest));
            Assert.That(MaskedNotes(Masked(written)),
                Is.EqualTo(MaskedNotes(Masked(File.ReadAllText(Path.Combine(python, StudyWriter.StudyBundleManifest))))));
            Assert.That(System.Text.Json.JsonDocument.Parse(written).RootElement.GetProperty("notes").GetString(),
                Is.EqualTo(manifest.Notes as string), "study.json carries the delivery's notes as written now");
            Assert.That(result.RowCounts["sample_ages"], Is.GreaterThan(0));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }
}
