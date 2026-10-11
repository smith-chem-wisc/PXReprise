#:package DuckDB.NET.Data.Full@1.5.5
#:package YamlDotNet@16.3.0
// PXR-T5 (QuantProject 040): every delivered deposit with more than one value in an SDRF sample-type column (organism
// part, cell type, enrichment, sample or material type, fraction or compartment), with its manifest flags.
// Reads what was DELIVERED: each bundle's sample_characteristics.parquet in the store (the bundle named by state.json),
// not the run folder, whose SDRF may be gone after cleanup. Reserved values ("not applicable", "not available") are
// not counted. comment[fraction identifier] is left out: it numbers fractions of one sample, it is not a sample type.
//
//   dotnet run tools/sdrf-mix/SampleTypeMix.cs -- --state F:\aging_data\batch\state.json
//     --manifest F:\aging_data\batch\manifest.yaml --store F:\aging_data\repo\store --out results\sampletype-mix_<date>
//     [--runs F:\aging_data\run_2026-09-21]   (a bundle with no SDRF characteristics falls back to the run folder's SDRF)
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using YamlDotNet.RepresentationModel;

var opt = new Dictionary<string, string>();
for (int i = 0; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
foreach (string k in new[] { "state", "manifest", "store", "out" })
    if (!opt.ContainsKey(k)) { Console.Error.WriteLine($"usage: --state F --manifest F --store DIR --out DIR (missing --{k})"); return 2; }

string[] sampleTypes = ["organism part", "cell type", "enrichment process", "sample type", "material type",
    "cellular component", "compartment", "subcellular fraction", "fraction type"];

// Manifest: accession -> (status, flags, enrichment, run_enrichment classes).
var manifest = new Dictionary<string, (string Status, string Flags, string Enrichment, string RunEnrichment)>();
var yaml = new YamlStream();
using (var reader = new StreamReader(opt["manifest"])) yaml.Load(reader);
foreach (var seq in yaml.Documents[0].AllNodes.OfType<YamlSequenceNode>())
    foreach (var entry in seq.Children.OfType<YamlMappingNode>())
    {
        string Get(string key) => entry.Children.TryGetValue(new YamlScalarNode(key), out var n)
            ? n switch
            {
                YamlScalarNode s => s.Value ?? "",
                YamlSequenceNode q => string.Join(",", q.Children.OfType<YamlScalarNode>().Select(x => x.Value)),
                YamlMappingNode m => string.Join(",", m.Children.Select(c => $"{c.Key}({(c.Value as YamlSequenceNode)?.Children.Count ?? 0})")),
                _ => ""
            }
            : "";
        string acc = Get("accession");
        if (acc.Length > 0) manifest[acc] = (Get("status"), Get("flags"), Get("enrichment"), Get("run_enrichment"));
    }

var state = JsonNode.Parse(File.ReadAllText(opt["state"]))!["datasets"]!.AsObject();
// The bundle: the one state.json names (v0.4+ records it), else the store's latest for the accession by written_utc.
string? LatestBundle(string acc)
{
    string d = Path.Combine(opt["store"], acc);
    if (!Directory.Exists(d)) return null;
    return Directory.GetDirectories(d)
        .Where(b => File.Exists(Path.Combine(b, "bundle.json")))
        .Select(b => (Id: Path.GetFileName(b), Written: JsonNode.Parse(File.ReadAllText(Path.Combine(b, "bundle.json")))?["written_utc"]?.GetValue<string>() ?? ""))
        .OrderByDescending(b => b.Written, StringComparer.Ordinal)
        .Select(b => b.Id).FirstOrDefault();
}
var delivered = state
    .Where(kv => kv.Value?["ingest_rc"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number
                 && kv.Value!["ingest_rc"]!.GetValue<int>() == 0)
    .Select(kv => (Acc: kv.Key, Bundle: kv.Value!["bundle_id"]?.GetValue<string>() ?? LatestBundle(kv.Key)))
    .OrderBy(x => x.Acc, StringComparer.Ordinal).ToList();

using var con = new DuckDBConnection("DataSource=:memory:");
con.Open();

string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
string Attribute(string name)
{
    var m = Regex.Match(Norm(name), @"^(?:characteristics|factor value|comment)\s*\[\s*(.+?)\s*\]$");
    return m.Success ? m.Groups[1].Value : "";
}

var rows = new List<string[]>();
var missing = new List<string>();
foreach (var (acc, bundle) in delivered)
{
    string dir = bundle is null ? "" : Path.Combine(opt["store"], acc, bundle);
    if (bundle is null || !File.Exists(Path.Combine(dir, "bundle.json"))) { missing.Add($"{acc} {bundle ?? "(none in store)"}"); continue; }
    string chars = Path.Combine(dir, "sample_characteristics.parquet").Replace('\\', '/');
    var mf = manifest.TryGetValue(acc, out var me) ? me : ("(not in manifest)", "", "", "");
    if (!File.Exists(chars))
    {
        // Older ingests stored no SDRF characteristics. Fall back to the deposited SDRF kept in the run folder; with
        // none there either, the list cannot speak for the deposit.
        string? sdrf = opt.TryGetValue("runs", out var runs) ? DepositedSdrf(Path.Combine(runs, acc)) : null;
        if (sdrf is null) { rows.Add([acc, bundle, mf.Item1, "none", "", "unknown", "", mf.Item3, mf.Item2, mf.Item4, ""]); continue; }
        if (IsHtml(sdrf)) { rows.Add([acc, bundle, mf.Item1, "not an SDRF (EBI HTML index page)", "", "unknown", "", mf.Item3, mf.Item2, mf.Item4, ""]); continue; }
        var (n, vals) = ReadSdrf(sdrf);
        rows.Add(Row(acc, bundle, mf, "deposited (run folder; not in bundle)", n, vals));
        continue;
    }

    string sdrfKind = Directory.Exists(Path.Combine(dir, "sources"))
        ? string.Join(",", Directory.GetFiles(Path.Combine(dir, "sources"), "*.sdrf.tsv").Select(Path.GetFileName)
            .Select(f => f!.Contains("pxreprise_default") ? "default" : f.Contains("community_annotated") ? "deposited" : "curated")
            .Distinct().OrderBy(x => x))
        : "";
    if (sdrfKind.Length == 0) sdrfKind = "none";
    // EBI serves some *.sdrf.tsv paths as folders; the fetch then keeps the folder's HTML index as the "SDRF".
    if (Directory.Exists(Path.Combine(dir, "sources"))
        && Directory.GetFiles(Path.Combine(dir, "sources"), "*sdrf*").Any(IsHtml))
    {
        rows.Add([acc, bundle, mf.Item1, "not an SDRF (EBI HTML index page)", "", "unknown", "", mf.Item3, mf.Item2, mf.Item4, ""]);
        continue;
    }

    int nSamples;
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = $"SELECT count(DISTINCT sample_id) FROM read_parquet('{chars}')";
        nSamples = Convert.ToInt32(cmd.ExecuteScalar());
    }

    // attribute -> value -> samples
    var values = new SortedDictionary<string, SortedDictionary<string, HashSet<string>>>(StringComparer.Ordinal);
    using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = $"SELECT sample_id, name, value FROM read_parquet('{chars}') WHERE NOT coalesce(value_reserved, false) AND value IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string attr = Attribute(r.GetString(1));
            if (!sampleTypes.Contains(attr)) continue;
            string v = Norm(r.GetString(2));
            if (v.Length == 0) continue;
            if (!values.TryGetValue(attr, out var byValue)) values[attr] = byValue = new(StringComparer.Ordinal);
            if (!byValue.TryGetValue(v, out var samples)) byValue[v] = samples = [];
            samples.Add(r.GetString(0));
        }
    }

    rows.Add(Row(acc, bundle, mf, sdrfKind, nSamples, values));
}

string[] Row(string acc, string bundle, (string, string, string, string) mf, string sdrfKind, int nSamples,
    SortedDictionary<string, SortedDictionary<string, HashSet<string>>> values)
{
    var mixed = values.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
    string detail = string.Join("; ", values.Select(kv =>
        $"{kv.Key}: " + string.Join(" | ", kv.Value.Select(v => $"{v.Key} ({v.Value.Count})"))));
    return [acc, bundle, mf.Item1, sdrfKind, nSamples.ToString(), mixed.Count > 0 ? "yes" : "no", string.Join(",", mixed),
        mf.Item3, mf.Item2, mf.Item4, detail];
}

// A deposited SDRF in the run folder: 02_fetch/metadata or 01_chemistry, never our own default sheet.
string? DepositedSdrf(string runDir) =>
    new[] { Path.Combine(runDir, "02_fetch", "metadata"), Path.Combine(runDir, "01_chemistry") }
        .Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d))
        .Where(f => Regex.IsMatch(Path.GetFileName(f), "sdrf", RegexOptions.IgnoreCase)
                    && !Path.GetFileName(f).Contains("pxreprise_default", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();

bool IsHtml(string path)
{
    using var r = new StreamReader(path);
    var buf = new char[512];
    string head = new(buf, 0, r.Read(buf, 0, buf.Length));
    return Regex.IsMatch(head, @"<!DOCTYPE\s+HTML|<html", RegexOptions.IgnoreCase);
}

// SDRF reserved words (not counted as a value). Each row is one file; the sample is `source name`, the first column.
(int, SortedDictionary<string, SortedDictionary<string, HashSet<string>>>) ReadSdrf(string path)
{
    string[] reserved = ["not applicable", "not available", "anonymized", "pooled"];
    var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
    var head = lines[0].Split('\t').Select(Attribute).ToArray();
    var values = new SortedDictionary<string, SortedDictionary<string, HashSet<string>>>(StringComparer.Ordinal);
    var samples = new HashSet<string>();
    foreach (var line in lines.Skip(1))
    {
        var cells = line.Split('\t');
        string sample = cells[0].Trim();
        samples.Add(sample);
        for (int c = 0; c < Math.Min(cells.Length, head.Length); c++)
        {
            if (!sampleTypes.Contains(head[c])) continue;
            string v = Norm(cells[c]);
            if (v.Length == 0 || reserved.Contains(v)) continue;
            if (!values.TryGetValue(head[c], out var byValue)) values[head[c]] = byValue = new(StringComparer.Ordinal);
            if (!byValue.TryGetValue(v, out var s)) byValue[v] = s = [];
            s.Add(sample);
        }
    }
    return (samples.Count, values);
}

Directory.CreateDirectory(opt["out"]);
string[] header = ["accession", "bundle_id", "manifest_status", "sdrf", "samples", "sample_type_mixed", "mixed_columns",
    "enrichment", "flags", "run_enrichment", "sample_type_values"];
var sb = new StringBuilder();
sb.Append(string.Join('\t', header)).Append('\n');
foreach (var row in rows) sb.Append(string.Join('\t', row.Select(c => c.Replace('\t', ' ').Replace('\n', ' ')))).Append('\n');
string outFile = Path.Combine(opt["out"], "sampletype_mix.tsv");
File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));

Console.WriteLine($"delivered deposits (state.json ingest_rc 0): {delivered.Count}");
Console.WriteLine($"  read: {rows.Count}; bundle missing: {missing.Count}{(missing.Count > 0 ? " (" + string.Join(", ", missing) + ")" : "")}");
foreach (var g in rows.GroupBy(r => r[3]).OrderBy(g => g.Key)) Console.WriteLine($"  sdrf {g.Key}: {g.Count()}");
Console.WriteLine($"  more than one value in a sample-type column: {rows.Count(r => r[5] == "yes")}; one value: {rows.Count(r => r[5] == "no")}; no SDRF delivered: {rows.Count(r => r[5] == "unknown")}");
Console.WriteLine($"  flagged mixed_enrichment or carrying run_enrichment: {rows.Count(r => r[8].Contains("mixed_enrichment") || r[9].Length > 0)}");
Console.WriteLine($"  of those, manifest status include: {rows.Count(r => r[5] == "yes" && r[2] == "include")}");
Console.WriteLine($"wrote {outFile}");
return 0;
