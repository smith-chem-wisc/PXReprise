using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;

namespace PXReprise.Provenance;

/// <summary>
/// A stage's provenance in the <c>aging-provenance/3</c> schema, the one dataRepo's ingester reads (and the only one it
/// accepts: <c>sources/provenance.py</c>). The engine writes this exact shape so the switch from the Python pipeline
/// changes no contract for anything downstream. Paths under the work root are stored relative to it with
/// <c>"root": "work_root"</c>; a file an upstream stage already hashed reuses that hash and names the stage.
/// </summary>
public sealed class AgingProvenance
{
    public const string Schema = "aging-provenance/3";
    public JsonObject Record { get; }
    private readonly string _workRoot;
    private readonly Dictionary<(string, string, long), (string Sha, string Stage)> _known = new();
    private readonly DateTime _started = DateTime.UtcNow;

    public AgingProvenance(string stage, string workRoot, string paramsFile, JsonNode searchSection, string? runDate)
    {
        _workRoot = Path.GetFullPath(workRoot);
        Record = new JsonObject
        {
            ["schema"] = Schema,
            ["stage"] = stage,
            ["started_utc"] = Iso(_started),
            ["host"] = new JsonObject
            {
                ["node"] = Environment.MachineName,
                ["os"] = RuntimeInformation.OSDescription,
                ["dotnet"] = Environment.Version.ToString(),
            },
            ["pipeline"] = new JsonObject
            {
                ["version"] = RunRecord.Versions()["pxreprise"],
                // The public repository, which anyone reading a record can open. Its snapshot commits name the
                // development commit they came from, so a commit below from either repository can be traced.
                ["repo"] = "https://github.com/smith-chem-wisc/PXReprise",
                ["commit"] = RunRecord.Versions()["pxreprise"].Split('+').ElementAtOrDefault(1) ?? "unknown",
            },
            ["params_file"] = Entry(paramsFile, useRoot: false),
            ["params"] = searchSection.DeepClone(),
            ["run_date"] = runDate,
            ["tools"] = new JsonObject
            {
                ["mzlib"] = new JsonObject { ["version"] = RunRecord.Versions()["mzlib"] },
            },
            ["commands"] = new JsonArray(),
            ["upstream"] = new JsonArray(),
            ["inputs"] = new JsonArray(),
            ["outputs"] = new JsonArray(),
            ["notes"] = new JsonArray(),
            ["roots"] = new JsonObject { ["work_root"] = workRoot },
        };
    }

    /// <summary>Chains this stage to the provenance of the stages it consumed, and reuses the hashes they recorded.</summary>
    public void Upstream(params string[] provenanceFiles)
    {
        foreach (string pp in provenanceFiles)
        {
            if (!File.Exists(pp)) { Note($"expected upstream provenance missing: {pp}"); continue; }
            var up = JsonNode.Parse(File.ReadAllText(pp))!.AsObject();
            var e = Entry(pp, useRoot: true);
            string stage = up["stage"]?.GetValue<string>() ?? "unknown";
            ((JsonArray)Record["upstream"]!).Add(new JsonObject { ["stage"] = stage, ["path"] = e["path"]!.DeepClone(), ["sha256"] = e["sha256"]!.DeepClone() });
            string baseDir = up["roots"]?["work_root"]?.GetValue<string>() ?? ".";
            foreach (var list in new[] { up["outputs"], up["inputs"] })
            foreach (var o in list?.AsArray() ?? new JsonArray())
            {
                if (o?["sha256"] is null || o["size_bytes"] is null) continue;
                string p = o["path"]!.GetValue<string>();
                string full = o["root"]?.GetValue<string>() == "work_root" ? Path.GetFullPath(Path.Combine(baseDir, p)) : Path.GetFullPath(p);
                long size = o["size_bytes"]!.GetValue<long>();
                string sha = o["sha256"]!.GetValue<string>();
                _known[("path", full, size)] = (sha, stage);
                _known[("name", Path.GetFileName(full), size)] = (sha, stage);
            }
        }
    }

    public void Tool(string name, JsonObject info) => Record["tools"]![name] = info;
    public void Command(IEnumerable<string> argv) => ((JsonArray)Record["commands"]!).Add(new JsonArray(argv.Select(a => (JsonNode?)a).ToArray()));
    public void Input(string path) => ((JsonArray)Record["inputs"]!).Add(Entry(path, useRoot: true));
    public void Output(string path) => ((JsonArray)Record["outputs"]!).Add(Entry(path, useRoot: true, reuse: false));
    public void Note(string text) => ((JsonArray)Record["notes"]!).Add(text);
    /// <summary>Sets a top-level block. A node that already belongs to another tree is copied (a JsonNode has one parent).</summary>
    public void Set(string key, JsonNode? value) => Record[key] = value?.Parent is null ? value : value.DeepClone();
    public void Set<T>(string key, T value) => Record[key] = JsonSerializer.SerializeToNode(value, Envelope.Json);

    public string Write(string dir)
    {
        Record["finished_utc"] = Iso(DateTime.UtcNow);
        string file = Path.Combine(dir, "provenance.json");
        File.WriteAllText(file, Record.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        return file;
    }

    private JsonObject Entry(string path, bool useRoot, bool reuse = true)
    {
        string full = Path.GetFullPath(path);
        long size = new FileInfo(full).Length;
        var e = new JsonObject();
        string rel = Path.GetRelativePath(_workRoot, full);
        if (useRoot && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
        {
            e["path"] = rel.Replace('\\', '/');
            e["root"] = "work_root";
        }
        else e["path"] = full;
        e["size_bytes"] = size;
        if (reuse && (_known.TryGetValue(("path", full, size), out var hit) || (size >= 100_000_000 && _known.TryGetValue(("name", Path.GetFileName(full), size), out hit))))
        {
            e["sha256"] = hit.Sha;
            e["sha256_from"] = hit.Stage;
        }
        else
        {
            using var s = File.OpenRead(full);
            e["sha256"] = Convert.ToHexStringLower(SHA256.HashData(s));
        }
        return e;
    }

    private static string Iso(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz");
}
