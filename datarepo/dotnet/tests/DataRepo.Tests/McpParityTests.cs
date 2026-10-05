using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using DataRepo.Ingest.Sources;
using DataRepo.Mcp;

namespace DataRepo.Tests;

/// <summary>The C# MCP tools answer every call in the corpus with the bytes the Python 0.32.0 server sends.</summary>
/// <remarks>
/// <para>Expected answers are Python's own: each call went through <c>mcp.CatalogServer</c> as <c>bound_tools</c>
/// sends it and was rendered as the Python SDK renders it for the agent (<c>pydantic_core.to_json(result,
/// fallback=str, indent=2)</c>); see <c>Fixtures/mcp/PROVENANCE.md</c>. Both sides read the same catalog file.</para>
/// <para>Normalised, and nothing else: <c>elapsed_seconds</c> (a wall clock), <c>served_by</c> (the serving
/// package's version) and the catalog's path as each side was given it.</para>
/// </remarks>
[NonParallelizable]
public class McpParityTests
{
    // Every expectation here was written by Python 0.32.0, on schema 0.0.13.
    private IDisposable? _schema;
    [SetUp] public void PinPythonSchema() => _schema = SchemaContract.Python0320();
    [TearDown] public void UnpinPythonSchema() => _schema?.Dispose();

    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");
    private const string RealCatalog = "F:/aging_data/repo/catalog.duckdb";

    private string _scratch = "";

    [OneTimeSetUp]
    public void Scratch()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "datarepo-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        // The Python answered on a machine in America/Chicago, and a TIMESTAMP WITH TIME ZONE is rendered in the
        // session's zone, so these tests answer in that zone on any machine.
        Sandbox.TimeZoneOverride = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    }

    [OneTimeTearDown]
    public void RemoveScratch()
    {
        Sandbox.TimeZoneOverride = null;
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
    }

    private string Gunzip(string gz, string name)
    {
        var path = Path.Combine(_scratch, name);
        if (File.Exists(path)) return path;
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = File.Create(path);
        input.CopyTo(output);
        return path;
    }

    private static JsonElement ReadExpected(string path)
    {
        using var stream = path.EndsWith(".gz", StringComparison.Ordinal)
            ? (Stream)new GZipStream(File.OpenRead(path), CompressionMode.Decompress)
            : File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    [Test]
    public void OnTheSiteFixtureCatalogEveryAnswerIsPythons()
    {
        var catalog = Gunzip(Path.Combine(Fixtures, "site", "catalog.duckdb.gz"), "fixture.duckdb");
        AssertParity(catalog, ReadExpected(Path.Combine(Fixtures, "mcp", "expected_fixture.json.gz")));
    }

    [Test]
    public void OnTheStudyFixtureCatalogEveryAnswerIsPythons()
    {
        var catalog = Gunzip(Path.Combine(Fixtures, "mcp", "catalog_study.duckdb.gz"), "study.duckdb");
        AssertParity(catalog, ReadExpected(Path.Combine(Fixtures, "mcp", "expected_study.json.gz")));
    }

    /// <summary>aging's serving catalog, read only. Needs <c>DATAREPO_MCP_PARITY_DIR</c> holding <c>real.json</c>,
    /// the Python's answers on that file (<c>mcp_parity.py F:/aging_data/repo/catalog.duckdb real.json --real</c>).
    /// Inconclusive, not failed, when the catalog has been rebuilt since.</summary>
    [Test, Category("RealData")]
    public void OnTheRealCatalogEveryAnswerIsPythons()
    {
        var dir = Environment.GetEnvironmentVariable("DATAREPO_MCP_PARITY_DIR");
        if (!File.Exists(RealCatalog) || string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "real.json")))
            Assert.Ignore($"needs {RealCatalog} and DATAREPO_MCP_PARITY_DIR/real.json");
        var expected = ReadExpected(Path.Combine(dir!, "real.json"));
        var pythonId = expected.GetProperty("_about").GetProperty("catalog_id").GetString();
        using (var server = new CatalogServer(RealCatalog))
            if (PyValues.Str(server.Identity.CatalogId) != pythonId)
                Assert.Inconclusive($"the catalog has been rebuilt since real.json was written from {pythonId}");
        AssertParity(RealCatalog, expected);
    }

    private void AssertParity(string catalog, JsonElement expected)
    {
        var differences = Compare(catalog, expected, out var compared);
        TestContext.Out.WriteLine($"{compared} calls compared, {differences.Count} differ");
        if (differences.Count > 0)
        {
            var report = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mcp-differences-{TestContext.CurrentContext.Test.Name}.txt");
            File.WriteAllText(report, string.Join("\n\n", differences));
            TestContext.AddTestAttachment(report);
        }
        Assert.That(differences, Is.Empty, string.Join("\n\n", differences.Take(5)));
        Assert.That(compared, Is.GreaterThanOrEqualTo(150));
    }

    /// <summary>Every call of <paramref name="expected"/> answered by the C# server; each difference as a report.</summary>
    public static List<string> Compare(string catalog, JsonElement expected, out int compared)
    {
        var about = expected.GetProperty("_about");
        var pythonPath = SourcesPy.PathStr(about.GetProperty("catalog").GetString()!);
        var differences = new List<string>();
        compared = 0;
        CatalogServer? server = null;
        string? serverKey = null;
        try
        {
            foreach (var call in expected.GetProperty("calls").EnumerateArray())
            {
                var key = call.GetProperty("server").GetRawText();
                if (key != serverKey)
                {
                    server?.Dispose();
                    server = NewServer(catalog, call.GetProperty("server"));
                    serverKey = key;
                }
                var id = call.GetProperty("id").GetString();
                var tool = call.GetProperty("tool").GetString()!;
                var args = call.GetProperty("args");
                string actual;
                try
                {
                    actual = PydanticJson.Serialize(Invoke(server!, tool, args));
                }
                catch (Exception exc) when (exc is not DataRepoException)
                {
                    actual = $"<exception {exc.GetType().Name}: {exc.Message}>";
                }
                string wanted = call.TryGetProperty("text", out var text)
                    ? text.GetString()!
                    : $"<exception {call.GetProperty("exception").GetString()}: {call.GetProperty("message").GetString()}>";
                if (!call.TryGetProperty("text", out _) && actual.StartsWith("<exception ", StringComparison.Ordinal))
                {
                    // Python raised something that is not a DataRepoError (the SDK reports it as a tool error):
                    // the C# must raise too. The type names are each language's own.
                    actual = wanted;
                }
                compared++;
                var a = Normalise(actual, SourcesPy.PathStr(catalog));
                var w = Normalise(wanted, pythonPath);
                if (a != w && Unordered(a) != Unordered(w))
                    differences.Add($"call {id} {tool}({args.GetRawText()}):\n{FirstDifference(w, a)}");
            }
        }
        finally
        {
            server?.Dispose();
        }
        return differences;
    }

    private static CatalogServer NewServer(string catalog, JsonElement kwargs)
    {
        var timeout = kwargs.TryGetProperty("timeout_seconds", out var t) ? t.GetDouble() : Sandbox.TimeoutSecondsDefault;
        var rows = kwargs.TryGetProperty("row_cap", out var r) ? r.GetInt32() : Sandbox.RowCap;
        var chars = kwargs.TryGetProperty("char_cap", out var c) ? c.GetInt32() : Sandbox.CharCap;
        return new CatalogServer(catalog, timeout, rows, chars);
    }

    private static Dictionary<string, object?> Invoke(CatalogServer server, string tool, JsonElement args)
    {
        string? Str(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? Int(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        return tool switch
        {
            "describe" => Mcp.Mcp.Call(server, "describe", target: Str("target"), detail: Str("detail") ?? "concise"),
            "search" => Mcp.Mcp.Call(server, "search", query: Str("query"), kind: Str("kind"), limit: Int("limit") ?? CatalogServer.SearchLimit),
            _ => Mcp.Mcp.Call(server, "sql", query: Str("query"), maxRows: Int("max_rows")),
        };
    }

    private static readonly Regex Elapsed = new("\"elapsed_seconds\": [^,\\n]+", RegexOptions.CultureInvariant);
    private static readonly Regex ServedBy = new("\"served_by\": \"datarepo [^\"]*\"", RegexOptions.CultureInvariant);

    /// <summary>DuckDB's <c>threads</c> setting is the machine's core count (64 where the fixtures were made, 4 on a
    /// CI runner): environment, not output. Found by CI on Linux, 2026-10-04.</summary>
    private static readonly Regex Threads = new("(\"threads\",\\s*)\"\\d+\"", RegexOptions.CultureInvariant);

    /// <summary>The descriptions schema 0.0.14 rewrote (G76's two columns; covered_zero in the occupancy state and
    /// floor) and schema 0.0.15 (G87: <c>runs.instrument_model</c> and <c>runs.acquisition_datetime</c>),
    /// by their opening words in 0.0.13 (Python's answers) and in the current schema (this server's). The server describes
    /// a column with the schema it was built against, so these, and only these, are expected to differ; a JSON
    /// string cannot hold a bare quote, so each mask ends where its string does, however the answer cut it.</summary>
    private static readonly (Regex Pattern, string Mask)[] SchemaDelta =
    [
        .. new[]
        {
            ("is_unique", "PARSIMONY-unique: `protein_accessions` holds"),
            ("is_unique", "GENE-unique, from the SEARCHED sequences"),
            ("is_isoform_specific", "NOT POPULATED: NULL on every row. The ingester does not compute it yet"),
            ("is_isoform_specific", "SEQUENCE-unique, from the searched sequences"),
            ("occupancy_state", "quantified, floor, count_only or intensity_unassigned (DEF-OCC-COUNTONLY)"),
            ("occupancy_state", "quantified, floor, covered_zero, count_only or intensity_unassigned"),
            ("intensity_is_floor", "True for `occupancy_state = floor`: a fraction of 0 whose numerator"),
        }.Select(d => (new Regex(Regex.Escape(d.Item2) + "[^\"]*", RegexOptions.CultureInvariant), $"<{d.Item1}: rewritten in schema 0.0.14>")),
        // Schema 0.0.15 (G87): the run's instrument and start time, now read per run from the QC report.
        .. new[]
        {
            ("instrument_model", "Instrument model from the raw file header."),
            ("instrument_model", "Instrument model name. From the producer's spectra QC report"),
            ("acquisition_datetime", "From the raw file header (J14 batch/date checks, H4)."),
            ("acquisition_datetime", "When acquisition started, in UTC. Filled ONLY when"),
        }.Select(d => (new Regex(Regex.Escape(d.Item2) + "[^\"]*", RegexOptions.CultureInvariant), $"<{d.Item1}: rewritten in schema 0.0.15>")),
    ];

    public static string Normalise(string text, string catalogPath)
    {
        foreach (var (pattern, mask) in SchemaDelta) text = pattern.Replace(text, mask);
        text = Threads.Replace(text, "$1\"<cores>\"");
        text = Elapsed.Replace(text, "\"elapsed_seconds\": \"<elapsed>\"");
        text = ServedBy.Replace(text, "\"served_by\": \"datarepo <version>\"");
        var escaped = PydanticJson.Serialize(catalogPath);
        return text.Replace(escaped[1..^1], "<catalog>");
    }

    /// <summary>The lists whose order the Python leaves to DuckDB: their <c>ORDER BY</c> is not total (findings by
    /// count, ptm_sites by site count, sample characteristics by sample count, metrics by dataset and name,
    /// localizations by protein count, peptides by dataset count), so DuckDB's parallel aggregation returns tied
    /// rows in a different order from one call to the next -- in the Python itself (calls 000-006 of the fixture
    /// corpus give four orders of one query). They are compared as multisets; every other list in order.</summary>
    private static readonly string[][] UnorderedPaths =
    [
        ["open_findings"],
        ["hits", "modification"], ["hits", "sample"], ["hits", "definition"], ["hits", "localization"], ["hits", "peptide"],
    ];

    /// <summary>The answer with the lists in <see cref="UnorderedPaths"/> sorted, or the text itself when it is not
    /// an object (an exception).</summary>
    private static string Unordered(string text)
    {
        if (!text.StartsWith('{')) return text;
        var node = System.Text.Json.Nodes.JsonNode.Parse(QuoteNonFinite(text))!;
        foreach (var path in UnorderedPaths)
        {
            System.Text.Json.Nodes.JsonNode? cursor = node;
            foreach (var step in path[..^1]) cursor = (cursor as System.Text.Json.Nodes.JsonObject)?[step];
            if (cursor is not System.Text.Json.Nodes.JsonObject parent || parent[path[^1]] is not System.Text.Json.Nodes.JsonArray list) continue;
            var sorted = list.Select(e => e?.ToJsonString() ?? "null").Order(StringComparer.Ordinal).ToList();
            parent[path[^1]] = new System.Text.Json.Nodes.JsonArray(sorted.Select(s => System.Text.Json.Nodes.JsonNode.Parse(s)).ToArray());
        }
        return node.ToJsonString();
    }

    /// <summary>pydantic writes NaN and the infinities as bare tokens; quote them so the text parses.</summary>
    private static string QuoteNonFinite(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\') sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            foreach (var token in new[] { "-Infinity", "Infinity", "NaN" })
            {
                if (string.CompareOrdinal(text, i, token, 0, token.Length) != 0) continue;
                sb.Append('"').Append(token).Append('"');
                i += token.Length - 1;
                goto next;
            }
            sb.Append(c);
            next:;
        }
        return sb.ToString();
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end>";
            var al = i < a.Length ? a[i] : "<end>";
            if (el == al) continue;
            var sb = new StringBuilder();
            sb.AppendLine($"  line {i + 1}");
            sb.AppendLine($"  python: {Clip(el)}");
            sb.Append($"  c#:     {Clip(al)}");
            return sb.ToString();
        }
        return "  (equal line by line; differ in line endings)";
    }

    private static string Clip(string s) => s.Length > 400 ? s[..400] + "..." : s;
}
