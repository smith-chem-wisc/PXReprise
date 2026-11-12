#:package mzLib@1.0.598
#:property PublishAot=false
// How many of our SDRF drafts give MetaMorpheus a usable design today: each searched aging deposit's evidence draft (the
// 2026-11-06 run's draft.sdrf.tsv, or the 2026-11-07 independent draft where there is one) read by mzLib 1.0.598's
// SdrfSearchDesign against exactly the files the deposit's fetch manifest lists, with no options (the default the engine
// would use: the document's own factor columns, joined when there are several, #1587). TMT deposits are read with the kit their
// search's provenance names. Measurement for steps 3 and 4 of design/PLAN_sdrf-every-deposit.md (the user, 2026-11-12: "build
// steps 3 and 4 before the next pin"). Reads only; writes one TSV and a Markdown summary.
//
//   dotnet run tools/sdrf/MeasureDraftDesigns.cs -- --state F:/aging_data/batch/state.json --runs F:/aging_data/run_2026-09-21
//       --drafts results/evidence-aging_2026-11-06/out --independent results/sdrf-independent_2026-11-07 --out <dir>
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Omics.Modifications;
using Readers;

var a = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i + 1 < args.Length; i += 2) a[args[i].TrimStart('-')] = args[i + 1];
string runs = a["runs"], drafts = a["drafts"], outDir = a["out"];
string? independent = a.GetValueOrDefault("independent");
Directory.CreateDirectory(outDir);

var state = JsonNode.Parse(File.ReadAllText(a["state"]))!["datasets"]!.AsObject();
var rows = new List<string[]>();
foreach (var (acc, node) in state.OrderBy(kv => kv.Key, StringComparer.Ordinal))
{
    if (node!["status"]?.GetValue<string>() != "searched") continue;
    string profile = node["profile"]?.GetValue<string>() ?? "";
    string baseAcc = acc.Split('-')[0];
    string manifest = Path.Combine(runs, acc, "02_fetch", "fetch_manifest.json");
    if (!File.Exists(manifest)) { rows.Add(new[] { acc, profile, "", "", "no fetch manifest", "", "" }); continue; }
    var files = JsonNode.Parse(File.ReadAllText(manifest))!["files"]!.AsArray()
        .Select(f => f!["name"]!.GetValue<string>()).Where(n => n.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".d", StringComparison.OrdinalIgnoreCase)).ToList();
    string? draft = null, which = "";
    if (independent is not null)
    {
        string ind = Directory.Exists(Path.Combine(independent, baseAcc)) ? Path.Combine(independent, baseAcc, "draft.independent.sdrf.tsv") : "";
        if (ind.Length > 0 && File.Exists(ind)) { draft = ind; which = "independent"; }
    }
    if (draft is null && File.Exists(Path.Combine(drafts, baseAcc, "draft.sdrf.tsv"))) { draft = Path.Combine(drafts, baseAcc, "draft.sdrf.tsv"); which = "11-06 draft"; }
    if (draft is null) { rows.Add(new[] { acc, profile, "", files.Count.ToString(CultureInfo.InvariantCulture), "no draft", "", "" }); continue; }

    var options = new SdrfSearchDesignOptions();
    if (profile.StartsWith("tmt", StringComparison.Ordinal))
    {
        string prov = Path.Combine(runs, acc, "04_search", "provenance.json");
        string? kit = File.Exists(prov) ? JsonNode.Parse(File.ReadAllText(prov))?["isobaric"]?["kit"]?.GetValue<string>() : null;
        if (kit is null || !IsobaricMassTag.TryGetIsobaricMassTag(kit, out var tag) || tag is null)
        { rows.Add(new[] { acc, profile, which, files.Count.ToString(CultureInfo.InvariantCulture), "no kit in provenance", "", "" }); continue; }
        options = new SdrfSearchDesignOptions { Tag = tag };
    }
    try
    {
        var d = SdrfSearchDesign.Read(draft, files, options);
        var condList = d.LabelFree is { } lf ? lf.Files.Select(f => f.Condition).Distinct().Order(StringComparer.Ordinal).ToList()
            : d.Isobaric is { } iso ? iso.Files.SelectMany(f => f.Channels).Select(c => c.Condition).Distinct().Order(StringComparer.Ordinal).ToList()
            : new List<string>();
        string conds = string.Join(" | ", condList.Take(8)) + (condList.Count > 8 ? $" (+{condList.Count - 8})" : "");
        int nCond = condList.Count;
        rows.Add(new[] { acc, profile, which, files.Count.ToString(CultureInfo.InvariantCulture), d.IsValid ? "usable" : "refused",
            nCond.ToString(CultureInfo.InvariantCulture), d.IsValid ? conds : Clean(d.Refusals.FirstOrDefault() ?? "") });
    }
    catch (Exception e) { rows.Add(new[] { acc, profile, which, files.Count.ToString(CultureInfo.InvariantCulture), "threw", "", Clean(e.GetType().Name + ": " + e.Message) }); }
}

var tsv = new StringBuilder("accession\tprofile\tdraft\tfiles\toutcome\tconditions\tdetail\n");
foreach (var r in rows) tsv.AppendLine(string.Join('\t', r));
File.WriteAllText(Path.Combine(outDir, "designs.tsv"), tsv.ToString(), new UTF8Encoding(false));

var md = new StringBuilder("# Our SDRF drafts as designs (mzLib 1.0.598 SdrfSearchDesign, no options)\n\n| outcome | label-free | TMT/iTRAQ |\n|---|---:|---:|\n");
foreach (var g in rows.GroupBy(r => r[4]).OrderByDescending(g => g.Count()))
    md.AppendLine($"| {g.Key} | {g.Count(r => !r[1].StartsWith("tmt", StringComparison.Ordinal))} | {g.Count(r => r[1].StartsWith("tmt", StringComparison.Ordinal))} |");
md.AppendLine("\n## Usable: conditions per deposit\n\n| conditions | deposits |\n|---|---:|");
foreach (var g in rows.Where(r => r[4] == "usable").GroupBy(r => int.Parse(r[5], CultureInfo.InvariantCulture)).OrderBy(g => g.Key))
    md.AppendLine($"| {g.Key} | {g.Count()} |");
md.AppendLine("\n## Refused: the first reason, by kind\n\n| reason | deposits |\n|---|---:|");
foreach (var g in rows.Where(r => r[4] is "refused" or "threw").GroupBy(r => Kind(r[6])).OrderByDescending(g => g.Count()))
    md.AppendLine($"| {g.Key} | {g.Count()} |");
File.WriteAllText(Path.Combine(outDir, "README.md"), md.ToString(), new UTF8Encoding(false));
Console.WriteLine(md.ToString());
return 0;

static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

// A refusal with its file names and values taken out, so that like reasons count together.
static string Kind(string s)
{
    s = System.Text.RegularExpressions.Regex.Replace(s, @"^Line \d+ \([^)]*\): ", "");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"'[^']*'", "'…'");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\d+", "N");
    return s.Length > 120 ? s[..120] : s;
}
