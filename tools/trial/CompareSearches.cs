#:property PublishAot=false
// Compares two searches of ONE deposit, an old and a new MetaMorpheus (PXR-A41: "record what changed between the two
// versions' results"). Reads only the producer files and both searches' provenance.json; writes a Markdown report.
//
//   dotnet run CompareSearches.cs -- --old <run>/04_search --new <run>/04_search --label-old 1.1.11 --label-new 1.1.12 --out report.md
//
// What it compares, each at the whole-search 1% target set (QValue <= 0.01, target, as results.txt counts them):
//   - results.txt counts, overall and per file;
//   - PSMs per file; distinct full sequences (peptidoforms) and base sequences; protein groups; their overlap (Jaccard);
//   - every output table's header: columns added or removed;
//   - modifications seen (Full Sequence brackets), per name, with the five families dataRepo 021 named called out;
//   - PEP and PEP q-value of the 1% target PSMs (median, and the share with PEP_QValue <= 0.01);
//   - protein N-terminal occupancy entries (pos0/pos1 in CountOccupancy_), mzLib #1337 (logs 001);
//   - cysteine-containing share of 1% target peptides (aging PXR-A37);
//   - per-task wall time from provenance, and whether a design and a search SDRF were used.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var a = Args(args);
string oldDir = a["old"], newDir = a["new"], lo = a.GetValueOrDefault("label-old", "old"), ln = a.GetValueOrDefault("label-new", "new");
var O = Load(oldDir);
var N = Load(newDir);
var md = new StringBuilder();
md.AppendLine($"# {lo} vs {ln}: {O.Accession ?? N.Accession}\n");
md.AppendLine($"- old: `{oldDir}`\n- new: `{newDir}`\n");

md.AppendLine("## Counts at 1% (results.txt)\n");
md.AppendLine($"| count | {lo} | {ln} | change |\n|---|---:|---:|---:|");
foreach (string k in O.Results.Keys.Union(N.Results.Keys).Where(k => !k.Contains(" - ", StringComparison.Ordinal)))
    Row(md, k, O.Results.GetValueOrDefault(k), N.Results.GetValueOrDefault(k));
md.AppendLine("\n### Per file (results.txt, target PSMs at 1%)\n");
md.AppendLine($"| file | {lo} | {ln} | change |\n|---|---:|---:|---:|");
foreach (string k in O.Results.Keys.Union(N.Results.Keys).Where(k => k.EndsWith("Target PSMs with q-value <= 0.01", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    Row(md, k.Split(" - ")[0], O.Results.GetValueOrDefault(k), N.Results.GetValueOrDefault(k));

md.AppendLine("\n## Identifications at 1% (AllPSMs.psmtsv, target, QValue <= 0.01)\n");
md.AppendLine($"| set | {lo} | {ln} | shared | Jaccard |\n|---|---:|---:|---:|---:|");
Overlap(md, "PSMs (file, scan)", O.PsmKeys, N.PsmKeys);
Overlap(md, "peptidoforms (Full Sequence)", O.Peptidoforms, N.Peptidoforms);
Overlap(md, "base sequences", O.BaseSeqs, N.BaseSeqs);
Overlap(md, "protein groups (AllQuantifiedProteinGroups, else AllProteinGroups; T, q <= 0.01)", O.Groups, N.Groups);

md.AppendLine("\n## PEP of the 1% target PSMs\n");
md.AppendLine($"| | {lo} | {ln} |\n|---|---:|---:|");
md.AppendLine($"| median PEP | {Fmt(Median(O.Pep))} | {Fmt(Median(N.Pep))} |");
md.AppendLine($"| share with PEP_QValue <= 0.01 | {Fmt(Share(O.PepQ, x => x <= 0.01))} | {Fmt(Share(N.PepQ, x => x <= 0.01))} |");
md.AppendLine("\nPEP is not comparable across the boundary: 1.1.12 trains PEP iteratively by default (MetaMorpheus #2844).");

md.AppendLine("\n## Modifications in 1% target PSMs (Full Sequence)\n");
string[] watch = { "GG (Ubiquitination Site)", "Myristoylation", "EQIGG", "Decarboxylation", "Acetylation on X" };
md.AppendLine($"| modification | {lo} | {ln} |\n|---|---:|---:|");
foreach (string m in O.Mods.Keys.Union(N.Mods.Keys).OrderByDescending(m => Math.Max(O.Mods.GetValueOrDefault(m), N.Mods.GetValueOrDefault(m))).Take(30)
             .Union(O.Mods.Keys.Union(N.Mods.Keys).Where(m => watch.Any(w => m.Contains(w, StringComparison.Ordinal)))))
    md.AppendLine($"| {m} | {O.Mods.GetValueOrDefault(m)} | {N.Mods.GetValueOrDefault(m)} |");

md.AppendLine("\n## Other measures\n");
md.AppendLine($"| measure | {lo} | {ln} |\n|---|---:|---:|");
md.AppendLine($"| N-terminal occupancy entries (pos0/pos1, CountOccupancy_) | {O.NtermOcc} | {N.NtermOcc} |");
md.AppendLine($"| cysteine-containing share of 1% target peptides | {Fmt(O.CysShare)} | {Fmt(N.CysShare)} |");
md.AppendLine($"| experimental design used | {O.Design} | {N.Design} |");
md.AppendLine($"| search SDRF written | {O.Sdrf} | {N.Sdrf} |");
foreach (string t in O.TaskSeconds.Keys.Union(N.TaskSeconds.Keys).Order(StringComparer.Ordinal))
    md.AppendLine($"| wall time {t} (min) | {Min(O.TaskSeconds, t)} | {Min(N.TaskSeconds, t)} |");

md.AppendLine("\n## Output tables: header changes\n");
foreach (string f in O.Headers.Keys.Union(N.Headers.Keys).Order(StringComparer.Ordinal))
{
    var ho = O.Headers.GetValueOrDefault(f);
    var hn = N.Headers.GetValueOrDefault(f);
    if (ho is null || hn is null) { md.AppendLine($"- `{f}`: only in {(ho is null ? ln : lo)}"); continue; }
    // Sample columns are named by file or by design sample; compare their non-sample part only.
    var so = ho.Select(Generic).ToHashSet();
    var sn = hn.Select(Generic).ToHashSet();
    var added = sn.Except(so).Order().ToList();
    var removed = so.Except(sn).Order().ToList();
    md.AppendLine(added.Count + removed.Count == 0 ? $"- `{f}`: same columns" :
        $"- `{f}`: added {(added.Count == 0 ? "none" : string.Join(", ", added.Select(c => $"`{c}`")))}; removed {(removed.Count == 0 ? "none" : string.Join(", ", removed.Select(c => $"`{c}`")))}");
}
File.WriteAllText(a["out"], md.ToString());
Console.WriteLine($"wrote {a["out"]}");
return 0;

static Run Load(string searchDir)
{
    string task = Directory.EnumerateDirectories(Path.Combine(searchDir, "mm"), "Task*SearchTask").Order(StringComparer.Ordinal).Last();
    var r = new Run();
    var prov = JsonNode.Parse(File.ReadAllText(Path.Combine(searchDir, "provenance.json")))!;
    r.Accession = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(searchDir).TrimEnd('\\', '/')));   // <run root>/<PXD>/04_search
    if (prov["per_task_resources"] is JsonObject pt)
        foreach (var (k, v) in pt) r.TaskSeconds[k] = v?["wall_seconds"]?.GetValue<double>() ?? 0;
    r.Design = prov["experimental_design"]?["written"]?.GetValue<bool>() == true
        ? $"{prov["experimental_design"]!["source"]}: {prov["experimental_design"]!["conditions"]!.AsArray().Count} conditions" : "none";
    r.Sdrf = Directory.EnumerateFiles(Path.Combine(searchDir, "mm"), "*.sdrf.tsv", SearchOption.AllDirectories).Any() ? "yes" : "no";
    foreach (string line in File.ReadLines(Path.Combine(task, "results.txt")))
        if (Regex.Match(line, @"^(.*?):\s*(\d+)\s*$") is { Success: true } m && line.Contains("q-value", StringComparison.Ordinal))
            r.Results[m.Groups[1].Value.Trim()] = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    foreach (string f in Directory.EnumerateFiles(task, "*.ps*").Concat(Directory.EnumerateFiles(task, "*.tsv")))
        r.Headers[Path.GetFileName(f)] = File.ReadLines(f).First().Split('\t');
    foreach (var row in Read(Path.Combine(task, "AllPSMs.psmtsv")))
    {
        if (row["Decoy/Contaminant/Target"] != "T" || !(Num(row["QValue"]) <= 0.01)) continue;
        r.PsmKeys.Add(row["File Name"] + "|" + row.GetValueOrDefault("Scan Number", ""));
        r.Peptidoforms.Add(row["Full Sequence"]);
        r.BaseSeqs.Add(row["Base Sequence"]);
        r.Pep.Add(Num(row.GetValueOrDefault("PEP", "")));
        r.PepQ.Add(Num(row.GetValueOrDefault("PEP_QValue", "")));
        foreach (Match m in Regex.Matches(row["Full Sequence"], @"\[([^\[\]:]+):((?:[^\[\]]|\[[^\]]*\])+)\]"))
            r.Mods[m.Groups[2].Value] = r.Mods.GetValueOrDefault(m.Groups[2].Value) + 1;
    }
    var bases = r.BaseSeqs.Where(s => !s.Contains('|')).ToList();
    r.CysShare = bases.Count == 0 ? double.NaN : (double)bases.Count(s => s.Contains('C')) / bases.Count;
    // An isobaric search writes no AllQuantifiedProteinGroups.tsv, only AllProteinGroups.tsv with the same Protein QValue and
    // target columns (dataRepo 110, PXR-R60): take that one, or a TMT search's groups would read as none.
    string pg = Path.Combine(task, "AllQuantifiedProteinGroups.tsv");
    if (!File.Exists(pg)) pg = Path.Combine(task, "AllProteinGroups.tsv");
    if (File.Exists(pg))
        foreach (var row in Read(pg))
        {
            if (row.GetValueOrDefault("Protein Decoy/Contaminant/Target") == "T" && Num(row.GetValueOrDefault("Protein QValue", "")) <= 0.01)
                r.Groups.Add(row["Protein Accession"]);
            foreach (var (k, v) in row)
                if (k.StartsWith("CountOccupancy_", StringComparison.Ordinal) && v.Length > 0)
                    r.NtermOcc += Regex.Matches(v, @"(^|;)pos[01]\[").Count;
        }
    return r;
}

static IEnumerable<Dictionary<string, string>> Read(string path)
{
    using var sr = new StreamReader(path);
    string[] h = sr.ReadLine()!.Split('\t');
    string? line;
    while ((line = sr.ReadLine()) is not null)
    {
        var f = line.Split('\t');
        var d = new Dictionary<string, string>(h.Length, StringComparer.Ordinal);
        for (int i = 0; i < h.Length; i++) d[h[i]] = i < f.Length ? f[i] : "";
        yield return d;
    }
}

static string Generic(string col) => Regex.Replace(col, @"^((?:Intensity|SpectralCount|CountOccupancy|IntensityOccupancy|Detection Type|Peak Detection Type)_).*$", "$1<sample>");
static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
static double Median(List<double> x) { var v = x.Where(d => !double.IsNaN(d)).Order().ToList(); return v.Count == 0 ? double.NaN : v[v.Count / 2]; }
static double Share(List<double> x, Func<double, bool> p) { var v = x.Where(d => !double.IsNaN(d)).ToList(); return v.Count == 0 ? double.NaN : (double)v.Count(p) / v.Count; }
static string Fmt(double d) => double.IsNaN(d) ? "n/a" : d.ToString("0.0000", CultureInfo.InvariantCulture);
static string Min(Dictionary<string, double> t, string k) => t.TryGetValue(k, out double s) ? (s / 60).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
static void Row(StringBuilder md, string k, long? o, long? n) =>
    md.AppendLine($"| {k} | {o?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {n?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | {(o is > 0 && n is not null ? ((double)(n - o) / o.Value).ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture) : "")} |");
static void Overlap(StringBuilder md, string name, HashSet<string> o, HashSet<string> n)
{
    int shared = o.Intersect(n).Count(), union = o.Union(n).Count();
    md.AppendLine($"| {name} | {o.Count} | {n.Count} | {shared} | {(union == 0 ? "n/a" : ((double)shared / union).ToString("0.000", CultureInfo.InvariantCulture))} |");
}
static Dictionary<string, string> Args(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
    foreach (string k in new[] { "old", "new", "out" }) if (!d.ContainsKey(k)) throw new ArgumentException($"--{k} is required");
    return d;
}

sealed class Run
{
    public string? Accession;
    public Dictionary<string, long> Results = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> Headers = new(StringComparer.Ordinal);
    public HashSet<string> PsmKeys = new(StringComparer.Ordinal), Peptidoforms = new(StringComparer.Ordinal), BaseSeqs = new(StringComparer.Ordinal), Groups = new(StringComparer.Ordinal);
    public List<double> Pep = new(), PepQ = new();
    public Dictionary<string, int> Mods = new(StringComparer.Ordinal);
    public Dictionary<string, double> TaskSeconds = new(StringComparer.Ordinal);
    public long NtermOcc;
    public double CysShare;
    public string Design = "none", Sdrf = "no";
}
