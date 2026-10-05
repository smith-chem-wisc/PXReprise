using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

// mzLib has a global namespace called Readers, which outranks an imported type of the same name.
using Readers = DataRepo.Ingest.Readers;

/// <summary>
/// <c>Readers</c> and <c>Sources.Sdrf</c> against what Python actually got: pyMzLib 0.4.0 (mzLib 1.0.593, the
/// same mzLib the C# calls) through <c>src/datarepo/readers.py</c> and <c>sources/sdrf.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every comparison is Python's <c>json.dumps</c> of the value, so an int where Python had an int and a float
/// where it had a float are part of the check: mzLib's <c>-1.0</c> reached Python as <c>-1</c>, and a C#
/// reader that kept it a double would write a different row. See <c>Fixtures/readers-sdrf/PROVENANCE.md</c>.
/// </para>
/// <para>
/// The fixture-tree and synthetic-input comparisons run everywhere. The real aging files are compared by
/// digest in the <c>RealData</c> category and are skipped where <c>F:/aging_data</c> is absent.
/// </para>
/// </remarks>
public class ReadersSdrfTests
{
    private static readonly string FixtureDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "readers-sdrf");

    private static readonly Lazy<Dictionary<string, object?>> Expected = new(() => Load("expected_fixture.json"));
    private static readonly Lazy<Dictionary<string, object?>> ExpectedReal = new(() => Load("expected_real_digest.json"));

    private static Dictionary<string, object?> Load(string name) =>
        (Dictionary<string, object?>)Plain(JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, name))).RootElement)!;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static string InRepo(string relative) => Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> Section(Dictionary<string, object?> root, string key) =>
        (Dictionary<string, object?>)root[key]!;

    private static Dictionary<string, object?> AsDict(object? o) => (Dictionary<string, object?>)o!;

    private static List<object?> AsList(object? o) => (List<object?>)o!;

    /// <summary>Python's <c>json.dumps(value, ensure_ascii=False)</c>, insertion order kept.</summary>
    private static string Py(object? value) => PyFormat.Json(value, sortKeys: false);

    private static string Sha(object? value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Py(value))));

    /// <summary>The Python reader log said <c>pymzlib</c>; the C# one says <c>mzlib</c>, by decision.</summary>
    private static string PyLog(object? entries) => Py(entries).Replace("\"backend\": \"pymzlib\"", "\"backend\": \"mzlib\"");

    private static void SameJson(object? actual, object? expected, string what) =>
        Assert.That(Py(actual), Is.EqualTo(Py(expected)), what);

    /// <summary>Column by column, naming the first differing row, so a failure says where.</summary>
    private static void SameColumns(Dictionary<string, List<object?>> actual, Dictionary<string, object?> expected, string what)
    {
        Assert.That(actual.Keys.ToList(), Is.EqualTo(expected.Keys.ToList()), $"{what}: column names and order");
        foreach ((string name, object? values) in expected)
        {
            List<object?> want = AsList(values);
            List<object?> got = actual[name];
            Assert.That(got, Has.Count.EqualTo(want.Count), $"{what}: {name} length");
            for (int i = 0; i < want.Count; i++)
            {
                if (Py(got[i]) != Py(want[i]))
                    Assert.Fail($"{what}: column {name} row {i}: C# {Py(got[i])}, Python {Py(want[i])}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The fixture tree and the synthetic inputs (run everywhere)
    // ---------------------------------------------------------------------------------------------

    private static IEnumerable<string> Keys(string section) => Section(Expected.Value, section).Keys;

    [TestCaseSource(nameof(PsmtsvCases))]
    public void ReadPsmtsvMatchesPyMzLib(string relative)
    {
        var expected = AsDict(Section(Expected.Value, "psmtsv")[relative]);
        var log = new ReaderLog();
        var columns = Readers.ReadPsmtsv(InRepo(relative), log);
        SameColumns(columns, AsDict(expected["columns"]), relative);
        Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
    }

    private static IEnumerable<string> PsmtsvCases() => Keys("psmtsv");

    [TestCaseSource(nameof(OccupancyCases))]
    public void ReadOccupancyMatchesPyMzLib(string relative)
    {
        var expected = AsDict(Section(Expected.Value, "occupancy")[relative]);
        var log = new ReaderLog();
        var result = Readers.ReadOccupancy(InRepo(relative), log);
        CompareOccupancy(result, expected, full: true);
        Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
    }

    private static IEnumerable<string> OccupancyCases() => Keys("occupancy");

    private static void CompareOccupancy(OccupancyRecords result, Dictionary<string, object?> expected, bool full)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.RecordCount, Is.EqualTo(expected["record_count"]));
            Assert.That(result.ReturnedCount, Is.EqualTo(expected["returned_count"]));
            Assert.That(result.RowCount, Is.EqualTo(expected["row_count"]));
            Assert.That(result.TruncatedCellCount, Is.EqualTo(expected["truncated_cell_count"]));
            SameJson(result.FailedFields, expected["failed_fields"], "failed_fields");
            SameJson(result.AbsentFields, expected["absent_fields"], "absent_fields");
            SameJson(result.ColumnNames, expected["column_names"], "column_names");
            SameJson(result.SampleLabels, expected["sample_labels"], "sample_labels");
            Assert.That(result.Records, Has.Count.EqualTo(expected["n_records"]));
        });
        if (full)
            SameJson(result.Records, expected["records"], "records");
        else
        {
            SameJson(result.Records.Take(3).ToList(), expected["sample_records"], "first records");
            Assert.That(Sha(result.Records), Is.EqualTo(expected["records_sha256"]), "records digest");
        }
    }

    [TestCaseSource(nameof(SdrfReadCases))]
    public void ReadSdrfMatchesPyMzLib(string relative)
    {
        var expected = AsDict(Section(Expected.Value, "sdrf_read")[relative]);
        var log = new ReaderLog();
        var (columns, rows) = Readers.ReadSdrf(InRepo(relative), log);
        SameJson(columns, expected["columns"], "columns");
        SameJson(rows, expected["rows"], "rows");
        Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
    }

    private static IEnumerable<string> SdrfReadCases() => Keys("sdrf_read");

    [TestCaseSource(nameof(SdrfParseCases))]
    public void SdrfParseMatchesPython(int index)
    {
        var expected = AsDict(AsList(Expected.Value["sdrf_parse"])[index]);
        var log = new ReaderLog();
        var table = Sdrf.Parse(InRepo((string)expected["path"]!), (string)expected["dataset_id"]!,
            (string?)expected["default_organism"], log, IngestRules.Python0320);
        Assert.Multiple(() =>
        {
            SameJson(table.Samples, expected["samples"], "samples");
            SameJson(table.Characteristics, expected["characteristics"], "characteristics");
            SameJson(table.Assays, expected["assays"], "assays");
            SameJson(table.RunFacts, expected["run_facts"], "run_facts");
            SameJson(table.SampleOfRun, expected["sample_of_run"], "sample_of_run");
            SameJson(table.Columns, expected["columns"], "columns");
            Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
        });
    }

    private static IEnumerable<int> SdrfParseCases() => Enumerable.Range(0, AsList(Expected.Value["sdrf_parse"]).Count);

    [TestCaseSource(nameof(TsvCases))]
    public void ReadTsvMatchesPython(string relative)
    {
        var expected = AsDict(Section(Expected.Value, "tsv")[relative]);
        var log = new ReaderLog();
        var (header, rows) = Readers.ReadTsv(InRepo(relative), log, (string?)expected["note"], IngestRules.Python0320);
        SameJson(header, expected["header"], "header");
        SameJson(rows, expected["rows"], "rows");
        SameJson(Readers.IterDicts(header, rows).ToList(), expected["dicts"], "iter_dicts");
        SameJson(log.Entries, expected["log"], "log");
    }

    private static IEnumerable<string> TsvCases() => Keys("tsv");

    [TestCaseSource(nameof(ResultsCases))]
    public void ReadResultsTxtMatchesPython(string relative)
    {
        var expected = AsDict(Section(Expected.Value, "results_txt")[relative]);
        var log = new ReaderLog();
        var results = Readers.ReadResultsTxt(InRepo(relative), log);
        SameJson(results, expected["results"], "results");
        SameJson(log.Entries, expected["log"], "log");
    }

    private static IEnumerable<string> ResultsCases() => Keys("results_txt");

    [Test]
    public void ParseValueMatchesPython()
    {
        foreach ((string raw, object? parts) in Section(Expected.Value, "parse_value"))
            SameJson(Sdrf.ParseValue(raw), parts, $"parse_value({raw})");
    }

    [Test]
    public void ReaderLogKeepsPythonsShape()
    {
        var log = new ReaderLog();
        log.Record(@"C:\x\y\AllPSMs.psmtsv", "mzlib", 3);
        log.Record("a/b/results.txt", "datarepo-text", 2, "");
        log.Record("p.tsv", "datarepo-tsv", 1, "why");
        Assert.That(Py(log.Entries), Is.EqualTo(
            "[{\"file\": \"AllPSMs.psmtsv\", \"backend\": \"mzlib\", \"rows\": 3}, " +
            "{\"file\": \"results.txt\", \"backend\": \"datarepo-text\", \"rows\": 2}, " +
            "{\"file\": \"p.tsv\", \"backend\": \"datarepo-tsv\", \"rows\": 1, \"note\": \"why\"}]"));
        Assert.That(log.Backends(), Is.EqualTo(new[] { "datarepo-text", "datarepo-tsv", "mzlib" }));
    }

    // ---------------------------------------------------------------------------------------------
    // Real aging files, by digest (skipped without F:/aging_data)
    // ---------------------------------------------------------------------------------------------

    private static IEnumerable<string> RealKeys(string section) =>
        File.Exists(Path.Combine(FixtureDir, "expected_real_digest.json")) ? Section(ExpectedReal.Value, section).Keys : [];

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
            Assert.Ignore($"real data not present: {path}");
    }

    [Category("RealData")]
    [TestCaseSource(nameof(RealPsmtsvCases))]
    public void RealPsmtsvMatchesPyMzLib(string path)
    {
        RequireFile(path);
        var expected = AsDict(Section(ExpectedReal.Value, "psmtsv")[path]);
        var log = new ReaderLog();
        var columns = Readers.ReadPsmtsv(path, log);
        Assert.That(columns.Keys.ToList(), Is.EqualTo(AsList(expected["column_names"])), "column names and order");
        Assert.That(columns.Values.First(), Has.Count.EqualTo(expected["rows"]));
        var shas = AsDict(expected["column_sha256"]);
        var wrong = shas.Where(kv => Sha(columns[kv.Key]) != (string)kv.Value!).Select(kv => kv.Key).ToList();
        Assert.That(wrong, Is.Empty, "columns whose values differ from pyMzLib's");
        var samples = AsList(expected["sample_rows"]);
        int n = columns.Values.First().Count;
        int[] at = [0, 1, n - 1];
        for (int s = 0; s < samples.Count; s++)
            SameJson(columns.ToDictionary(c => c.Key, c => c.Value[at[s]]), samples[s], $"row {at[s]}");
        Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
    }

    private static IEnumerable<string> RealPsmtsvCases() => RealKeys("psmtsv");

    [Category("RealData")]
    [TestCaseSource(nameof(RealOccupancyCases))]
    public void RealOccupancyMatchesPyMzLib(string path)
    {
        RequireFile(path);
        var expected = AsDict(Section(ExpectedReal.Value, "occupancy")[path]);
        var log = new ReaderLog();
        CompareOccupancy(Readers.ReadOccupancy(path, log), expected, full: false);
        Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(expected["log"])));
    }

    private static IEnumerable<string> RealOccupancyCases() => RealKeys("occupancy");

    [Category("RealData")]
    [TestCaseSource(nameof(RealSdrfCases))]
    public void RealSdrfReadAndParseMatchPython(string path)
    {
        RequireFile(path);
        var read = AsDict(Section(ExpectedReal.Value, "sdrf_read")[path]);
        var (columns, rows) = Readers.ReadSdrf(path);
        SameJson(columns, read["columns"], "columns");
        Assert.That(rows, Has.Count.EqualTo(read["n_rows"]));
        Assert.That(Sha(rows), Is.EqualTo(read["rows_sha256"]), "rows digest");

        var parse = AsList(ExpectedReal.Value["sdrf_parse"]).Select(AsDict).Single(p => (string)p["path"]! == path);
        var log = new ReaderLog();
        var table = Sdrf.Parse(path, (string)parse["dataset_id"]!, (string?)parse["default_organism"], log, IngestRules.Python0320);
        var sha = AsDict(parse["sha256"]);
        Assert.Multiple(() =>
        {
            Assert.That(Sha(table.Samples), Is.EqualTo(sha["samples"]), "samples");
            Assert.That(Sha(table.Characteristics), Is.EqualTo(sha["characteristics"]), "characteristics");
            Assert.That(Sha(table.Assays), Is.EqualTo(sha["assays"]), "assays");
            Assert.That(Sha(table.RunFacts), Is.EqualTo(sha["run_facts"]), "run_facts");
            Assert.That(Sha(table.SampleOfRun), Is.EqualTo(sha["sample_of_run"]), "sample_of_run");
            Assert.That(Sha(table.Columns), Is.EqualTo(sha["columns"]), "columns");
            Assert.That(PyLog(log.Entries), Is.EqualTo(PyLog(parse["log"])));
        });
    }

    private static IEnumerable<string> RealSdrfCases() => RealKeys("sdrf_read");

    [Category("RealData")]
    [TestCaseSource(nameof(RealTsvCases))]
    public void RealTsvMatchesPython(string path)
    {
        RequireFile(path);
        var expected = AsDict(Section(ExpectedReal.Value, "tsv")[path]);
        var log = new ReaderLog();
        var (header, rows) = Readers.ReadTsv(path, log, (string?)expected["note"], IngestRules.Python0320);
        SameJson(header, expected["header"], "header");
        Assert.That(Sha(rows), Is.EqualTo(expected["rows_sha256"]), "rows digest");
        Assert.That(Sha(Readers.IterDicts(header, rows).ToList()), Is.EqualTo(expected["dicts_sha256"]), "iter_dicts digest");
        SameJson(log.Entries, expected["log"], "log");
    }

    private static IEnumerable<string> RealTsvCases() => RealKeys("tsv");

    [Category("RealData")]
    [TestCaseSource(nameof(RealResultsCases))]
    public void RealResultsTxtMatchesPython(string path)
    {
        RequireFile(path);
        var expected = AsDict(Section(ExpectedReal.Value, "results_txt")[path]);
        var log = new ReaderLog();
        SameJson(Readers.ReadResultsTxt(path, log), expected["results"], "results");
        SameJson(log.Entries, expected["log"], "log");
    }

    private static IEnumerable<string> RealResultsCases() => RealKeys("results_txt");
}
