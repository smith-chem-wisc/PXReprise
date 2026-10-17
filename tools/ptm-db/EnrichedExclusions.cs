#:package YamlDotNet@16.3.0
#:property PublishAot=false
// The declared enriched-for exclusions for BuildPtmDb.cs --exclude-enriched (D29; the user, 2026-10-07: "we will be
// inundated with false positives"): a site of a PTM class is never counted from a deposit that enriches that class.
// agingPTM-v2 used a one-off list seeded from aging's enrichment targets of 2026-10-07 (16 deposits). Since then the
// batch records each deposit's enrichment itself (D30): the manifest's `enrichment` (per deposit, the screen's call and
// curation) and `run_enrichment`, and state.json's `enrichment_measured` (the probe search, D34). This builds the list
// from those, so a rebuild sees every enriched deposit the batch delivered (agingPTM-v4, 2026-10-17: 67 enriched).
//
// A class is excluded for a deposit when:
//   - its enrichment (any run's) is phospho, acetyl or ubiquitin_GG (-> phospho, acetylK, GG); or
//   - its title or notes name a polyubiquitin capture (TUBE, polyubiquitin, ubiquitin remnant, K-e-GG): -> GG. Such
//     pulldowns are filed as affinity_purification or other (PXD056433), not ubiquitin_GG; or
//   - the probe measured that enrichment (state.json enrichment_measured).
// A bait-protein pulldown, proximity labelling or a chemical probe enriches proteins, not a PTM class: nothing excluded.
// --also FILE adds an earlier list's rows (kept with their source). BuildPtmDb adds its own measured rows on top
// (phospho >= 5% or GG >= 2% of a run's 1% target PSMs).
//
//   dotnet run EnrichedExclusions.cs -- --manifest F:/aging_data/batch/manifest.yaml --state F:/aging_data/batch/state.json
//                                       [--also data/ptm-db/enriched_ptm_exclusions_2026-10-07.tsv] --out FILE.tsv
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

var opt = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
if (!opt.ContainsKey("manifest") || !opt.ContainsKey("out"))
{
    Console.Error.WriteLine("usage: --manifest manifest.yaml [--state state.json] [--also earlier.tsv] --out FILE.tsv");
    return 2;
}

// The enrichment calls whose class this builder selects (BuildPtmDb's Bio classes).
var byCategory = new Dictionary<string, string>(StringComparer.Ordinal) { ["phospho"] = "phospho", ["acetyl"] = "acetylK", ["ubiquitin_GG"] = "GG" };
// TUBE (tandem ubiquitin-binding entities) only in capitals: "tube" is in every protocol.
var ubiquitinCapture = new Regex(@"(?-i:\bTUBEs?\b)|polyubiquitin|ubiquitin remnant|K-(ε|epsilon)-GG|\bdi-?gly(cine)?\b", RegexOptions.IgnoreCase);

var rows = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);   // dataset -> class -> why
void Add(string ds, string cls, string why)
{
    if (!rows.TryGetValue(ds, out var c)) rows[ds] = c = new SortedDictionary<string, string>(StringComparer.Ordinal);
    c[cls] = c.TryGetValue(cls, out string? had) && had != why ? had + "; " + why : why;
}

var yaml = new YamlStream();
using (var reader = new StreamReader(opt["manifest"])) yaml.Load(reader);
var root = (YamlMappingNode)yaml.Documents[0].RootNode;
var entries = root.Children.Values.OfType<YamlSequenceNode>().Single();
int n = 0;
foreach (var e in entries.Children.OfType<YamlMappingNode>())
{
    n++;
    string Scalar(string key) => e.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value ?? "" : "";
    IEnumerable<string> Calls(string key) => e.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v switch
    {
        YamlSequenceNode q => q.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? ""),
        YamlMappingNode m => m.Children.Values.SelectMany(x => x is YamlSequenceNode q2 ? q2.Children.OfType<YamlScalarNode>().Select(y => y.Value ?? "") : x is YamlScalarNode s2 ? new[] { s2.Value ?? "" } : Array.Empty<string>()),
        YamlScalarNode s => new[] { s.Value ?? "" },
        _ => Array.Empty<string>(),
    } : Array.Empty<string>();
    string acc = Scalar("accession");
    foreach (string call in Calls("enrichment").Distinct())
        if (byCategory.TryGetValue(call, out string? cls)) Add(acc, cls, $"manifest enrichment: {call}");
    foreach (string call in Calls("run_enrichment").Distinct())
        if (byCategory.TryGetValue(call, out string? cls)) Add(acc, cls, $"manifest run_enrichment: {call} in some runs");
    if (ubiquitinCapture.Match(Scalar("title") + " " + Scalar("notes")) is { Success: true } m1)
        Add(acc, "GG", $"manifest text names a polyubiquitin capture ('{m1.Value}')");
}

if (opt.TryGetValue("state", out string? statePath))
{
    var ds = JsonNode.Parse(File.ReadAllText(statePath))!["datasets"]!.AsObject();
    foreach (var (acc, entry) in ds)
        if (entry?["enrichment_measured"]?.GetValue<string>() is { } measured && byCategory.TryGetValue(measured, out string? cls))
            Add(acc, cls, $"probe measured: {measured}");
}

if (opt.TryGetValue("also", out string? also))
    foreach (var p in File.ReadLines(also).Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')))
        foreach (string cls in p[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            Add(p[0], cls, p.Length > 2 && p[2].Length > 0 ? p[2] : $"declared ({Path.GetFileName(also)})");

using (var w = new StreamWriter(opt["out"]) { NewLine = "\n" })
{
    w.WriteLine("dataset\texcluded_classes\tsource");
    foreach (var (acc, classes) in rows)
        w.WriteLine($"{acc}\t{string.Join(",", classes.Keys)}\t{string.Join(" | ", classes.Select(kv => $"{kv.Key}: {kv.Value}"))}");
}
Console.WriteLine($"{n} manifest entries; {rows.Count} deposits with an excluded class: " +
    string.Join(", ", rows.SelectMany(r => r.Value.Keys).GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")) + $" -> {opt["out"]}");
return 0;
