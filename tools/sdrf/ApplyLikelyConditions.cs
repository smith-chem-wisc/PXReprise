#:package mzLib@1.0.598
#:property PublishAot=false
// What the paid evidence reading buys the designs (the user, 2026-11-12: "we spent lots of claude API money to download and search
// manuscripts and supplements to improve the quality of sdrfs. i want to see the results of all that effort and money pay off now").
//
// mzLib's SdrfDrafter takes a condition only from a Certain claim (a design table or an SDRF naming the run; sdrf MASSIVE-SDRF.md
// MV3). The model's quoted, checked claims are Likely, so the drafter set them aside in evidence.notes.tsv ("a condition is taken
// only from a Certain claim"). This applies them, PXReprise-side (D65: PXReprise makes the SDRF; sdrf owns the drafter), under
// these checks, and nothing else:
//   - a factor column is applied only when EVERY searched file has a claim for it, and all of one file's claims agree (one value);
//   - when one or more such columns exist, they become the draft's factors: the factor columns read from file names (and their
//     provenance columns) are dropped, because the paper names the groups and the names only hint at them;
//   - biological replicates are renumbered 1..n within each new condition, by distinct source name in row order (QuantProject 107:
//     replicates 1 to n within a group); a file's fraction and technical replicate are left as drafted;
//   - each applied cell's provenance says publication / model, so the SDRF shows where the grouping came from.
// Then every draft, as drafted and as resolved, is read by mzLib 1.0.598's SdrfSearchDesign against the deposit's fetched raws.
// Label-free deposits only. Reads only; writes <out>/resolved/<acc>.sdrf.tsv, <out>/resolved.tsv and <out>/resolved.md.
//
//   dotnet run tools/sdrf/ApplyLikelyConditions.cs -- --state F:/aging_data/batch/state.json --runs F:/aging_data/run_2026-09-21
//       --drafts results/evidence-aging_2026-11-06/out --out results/sdrf-designs_2026-11-12
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Readers;

var a = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i + 1 < args.Length; i += 2) a[args[i].TrimStart('-')] = args[i + 1];
string runs = a["runs"], drafts = a["drafts"], outDir = a["out"];
Directory.CreateDirectory(Path.Combine(outDir, "resolved"));
const string HeldBack = "a condition is taken only from a Certain claim: a design table or an SDRF that names the run";
var inv = CultureInfo.InvariantCulture;

var state = JsonNode.Parse(File.ReadAllText(a["state"]))!["datasets"]!.AsObject();
var rows = new List<string[]>();
foreach (var (acc, node) in state.OrderBy(kv => kv.Key, StringComparer.Ordinal))
{
    if (node!["status"]?.GetValue<string>() != "searched") continue;
    string profile = node["profile"]?.GetValue<string>() ?? "";
    if (profile.StartsWith("tmt", StringComparison.Ordinal)) continue;
    string baseAcc = acc.Split('-')[0];
    string draftPath = Path.Combine(drafts, baseAcc, "draft.sdrf.tsv"), notesPath = Path.Combine(drafts, baseAcc, "evidence.notes.tsv");
    string manifest = Path.Combine(runs, acc, "02_fetch", "fetch_manifest.json");
    if (!File.Exists(draftPath) || !File.Exists(manifest)) continue;
    var files = JsonNode.Parse(File.ReadAllText(manifest))!["files"]!.AsArray().Select(f => f!["name"]!.GetValue<string>())
        .Where(n => n.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".d", StringComparison.OrdinalIgnoreCase)).ToList();
    var stems = files.Select(Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);

    var before = SdrfSearchDesign.Read(draftPath, files);
    // The held-back condition claims, per column: file stem -> its distinct values.
    var claims = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
    if (File.Exists(notesPath))
        foreach (var line in File.ReadLines(notesPath).Skip(1))
        {
            var c = line.Split('\t');
            if (c.Length < 5 || c[4] != HeldBack || c[3].Trim().Length == 0) continue;
            string stem = Stem(c[0]);
            if (!stems.Contains(stem)) continue;
            var col = claims.TryGetValue(c[1], out var m) ? m : claims[c[1]] = new(StringComparer.OrdinalIgnoreCase);
            (col.TryGetValue(stem, out var v) ? v : col[stem] = new(StringComparer.OrdinalIgnoreCase)).Add(c[3].Trim());
        }
    // The controls rule: the paper often names every group but the controls (PXD033430: "6h AmAc", "24h AmAc", and nothing for
    // Control-1..3). A column whose claims cover some files, with one agreeing value each, is completed when the files it does
    // not cover share one name once their replicate number is taken off ("Control"), and no covered file has that name: those
    // files are one more group, named from their file names. Anything less clean is left alone.
    var completedByName = new List<string>();
    foreach (var (column, byStem) in claims)
    {
        if (byStem.Values.Any(v => v.Count != 1)) continue;
        var uncovered = stems.Where(s => !byStem.ContainsKey(s)).ToList();
        // At least two files: one leftover run is usually a QC or blank run (PXD027734's HeLa QC), not a group.
        if (uncovered.Count < 2 || uncovered.Count == stems.Count) continue;
        var names = uncovered.Select(GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count != 1 || names[0].Length < 2) continue;
        if (byStem.Keys.Any(s => GroupName(s).Equals(names[0], StringComparison.OrdinalIgnoreCase))) continue;
        if (byStem.Values.Any(v => v.First().Equals(names[0], StringComparison.OrdinalIgnoreCase))) continue;
        foreach (var s in uncovered) byStem[s] = new(StringComparer.OrdinalIgnoreCase) { names[0] };
        completedByName.Add(column);
    }
    var complete = claims.Where(kv => stems.All(s => kv.Value.TryGetValue(s, out var v) && v.Count == 1)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
    string incomplete = string.Join("; ", claims.Where(kv => !complete.Contains(kv.Key)).Select(kv =>
        $"{kv.Key}: {kv.Value.Count(x => x.Value.Count == 1)} of {stems.Count} files, {kv.Value.Count(x => x.Value.Count > 1)} disagree"));

    string outcomeAfter = "", detailAfter = "", condsAfter = "";
    if (complete.Count > 0)
    {
        string resolved = Path.Combine(outDir, "resolved", acc + ".sdrf.tsv");
        File.WriteAllLines(resolved, Resolve(File.ReadAllLines(draftPath), complete, claims), new UTF8Encoding(false));
        var after = SdrfSearchDesign.Read(resolved, files);
        outcomeAfter = after.IsValid ? "usable" : "refused";
        detailAfter = after.IsValid ? "" : Clean(after.Refusals.FirstOrDefault() ?? "");
        condsAfter = after.LabelFree is { } lf ? string.Join(" | ", lf.Files.Select(f => f.Condition).Distinct().Order(StringComparer.Ordinal).Take(8)) : "";
    }
    rows.Add(new[] { acc, files.Count.ToString(inv), before.IsValid ? "usable" : "refused",
        string.Join(", ", complete.Select(c => completedByName.Contains(c) ? c + " (+ group from file names)" : c)), outcomeAfter, condsAfter,
        before.IsValid ? "" : Clean(before.Refusals.FirstOrDefault() ?? ""), detailAfter, incomplete });
}

var tsv = new StringBuilder("accession\tfiles\tas_drafted\tapplied_columns\tresolved\tresolved_conditions\tdrafted_refusal\tresolved_refusal\tclaims_not_complete\n");
foreach (var r in rows) tsv.AppendLine(string.Join('\t', r));
File.WriteAllText(Path.Combine(outDir, "resolved.tsv"), tsv.ToString(), new UTF8Encoding(false));

int n = rows.Count;
int usableBefore = rows.Count(r => r[2] == "usable");
int applied = rows.Count(r => r[3].Length > 0);
int usableAfter = rows.Count(r => r[4] == "usable" || (r[4].Length == 0 && r[2] == "usable"));
var md = new StringBuilder();
md.AppendLine("# The paid readings' conditions applied (label-free, mzLib 1.0.598 SdrfSearchDesign)\n");
md.AppendLine($"- label-free deposits with a draft: {n}");
md.AppendLine($"- usable as drafted: {usableBefore}");
md.AppendLine($"- the held-back claims give a complete condition column: {applied} (of which usable as drafted already: {rows.Count(r => r[3].Length > 0 && r[2] == "usable")})");
md.AppendLine($"- usable after applying them (or usable as drafted where none applied): {usableAfter}");
md.AppendLine($"- refused before, usable after: {rows.Count(r => r[2] == "refused" && r[4] == "usable")}");
md.AppendLine($"- usable before, refused after: {rows.Count(r => r[2] == "usable" && r[4] == "refused")}\n");
md.AppendLine("## Still refused, by first reason\n\n| reason | deposits |\n|---|---:|");
foreach (var g in rows.Where(r => (r[4].Length > 0 ? r[4] : r[2]) == "refused").GroupBy(r => Kind(r[4].Length > 0 ? r[7] : r[6])).OrderByDescending(g => g.Count()))
    md.AppendLine($"| {g.Key} | {g.Count()} |");
File.WriteAllText(Path.Combine(outDir, "resolved.md"), md.ToString(), new UTF8Encoding(false));
Console.WriteLine(md.ToString());
return 0;

// A file's group name: its stem without a trailing replicate number ("Control-1" -> "Control", "1_t3t_4" -> "1_t3t",
// "WT_rep2" -> "WT", "Con3" -> "Con").
static string GroupName(string stem)
{
    string g = System.Text.RegularExpressions.Regex.Replace(stem, @"([_\-. ]?(rep|r|n|s|bio|br|tr)?\d+)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return g.TrimEnd('_', '-', '.', ' ');
}

static string Stem(string file)
{
    string f = file.Replace('\\', '/');
    f = f[(f.LastIndexOf('/') + 1)..];
    foreach (var ext in new[] { ".raw", ".mzml", ".d", ".wiff", ".mgf" })
        if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return f[..^ext.Length];
    return f;
}

// The draft with its factor columns (and their provenance) replaced by the complete claim columns, and biological replicates
// renumbered 1..n within each new condition.
static IEnumerable<string> Resolve(string[] lines, List<string> columns, Dictionary<string, Dictionary<string, HashSet<string>>> claims)
{
    var h = lines[0].Split('\t');
    bool Drop(string c) => c.StartsWith("factor value[", StringComparison.OrdinalIgnoreCase)
        || (c.StartsWith("comment[factor value ", StringComparison.OrdinalIgnoreCase) && c.Contains(" source", StringComparison.OrdinalIgnoreCase));
    var keep = Enumerable.Range(0, h.Length).Where(i => !Drop(h[i])).ToList();
    int iFile = Array.FindIndex(h, c => c.Equals("comment[data file]", StringComparison.OrdinalIgnoreCase));
    int iSource = Array.FindIndex(h, c => c.Equals("source name", StringComparison.OrdinalIgnoreCase));
    int iBio = Array.FindIndex(h, c => c.Equals("characteristics[biological replicate]", StringComparison.OrdinalIgnoreCase));
    string Inner(string col) => col[(col.IndexOf('[') + 1)..^1];
    var header = keep.Select(i => h[i]).Concat(columns)
        .Concat(columns.SelectMany(c => new[] { $"comment[factor value {Inner(c)} source]", $"comment[factor value {Inner(c)} source method]" }));
    yield return string.Join('\t', header);

    var body = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();
    var condOf = body.Select(r => string.Join(" | ", columns.Select(c => claims[c].TryGetValue(Stem(r[iFile]), out var v) ? v.First() : "not available"))).ToList();
    var bio = new Dictionary<(string Cond, string Source), int>();
    var next = new Dictionary<string, int>(StringComparer.Ordinal);
    for (int k = 0; k < body.Count; k++)
    {
        var r = body[k];
        if (iBio >= 0 && iSource >= 0 && !r[iBio].Equals("pooled", StringComparison.OrdinalIgnoreCase))
        {
            var key = (condOf[k], r[iSource]);
            if (!bio.TryGetValue(key, out int b)) bio[key] = b = next[condOf[k]] = next.GetValueOrDefault(condOf[k]) + 1;
            r[iBio] = b.ToString(CultureInfo.InvariantCulture);
        }
        string stem = Stem(r[iFile]);
        var values = columns.Select(c => claims[c].TryGetValue(stem, out var v) ? v.First() : "not available").ToList();
        yield return string.Join('\t', keep.Select(i => r[i]).Concat(values)
            .Concat(columns.SelectMany(c => claims[c].ContainsKey(stem) ? new[] { "publication", "model" } : new[] { "", "" })));
    }
}

static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

static string Kind(string s)
{
    s = System.Text.RegularExpressions.Regex.Replace(s, @"^Line \d+ \([^)]*\): ", "");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"'[^']*'", "'…'");
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\d+", "N");
    return s.Length > 120 ? s[..120] : s;
}
