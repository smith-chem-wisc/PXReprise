#:property PublishAot=false
// Compares the label-free quantification of two searches of ONE deposit (the user, 2026-10-19: "let's focus on getting
// good quant into the results"). Reads AllQuantifiedPeptides.tsv and AllQuantifiedProteinGroups.tsv in each search's
// Task*SearchTask and writes a Markdown report. Companion to CompareSearches.cs, which compares the identifications.
//
//   dotnet run CompareQuant.cs -- --old <run>/04_search --new <run>/04_search --label-old 1.1.12 --label-new master --out report.md
//
// Per sample column (a file for peptides, a design sample for protein groups):
//   - how many are quantified (intensity > 0) in each search, and in both;
//   - over those quantified in both: Pearson r of log2 intensity, and the median log2 ratio new/old;
//   - peptides: how many were detected by MS/MS and how many by match-between-runs (Detection Type_);
//   - protein groups: targets with Protein QValue <= 0.01 only, and occupancy cells written 0/N (MetaMorpheus #2889).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

var a = Args(args);
string lo = a.GetValueOrDefault("label-old", "old"), ln = a.GetValueOrDefault("label-new", "new");
var O = Load(a["old"]);
var N = Load(a["new"]);
var md = new StringBuilder();
md.AppendLine($"# Quantification, {lo} vs {ln}\n");
md.AppendLine($"- old: `{a["old"]}`\n- new: `{a["new"]}`\n");

md.AppendLine("## Peptides (AllQuantifiedPeptides.tsv, by full sequence)\n");
md.AppendLine($"| file | quantified {lo} | quantified {ln} | both | r (log2) | median log2 {ln}/{lo} | MS/MS {lo} | MS/MS {ln} | MBR {lo} | MBR {ln} |");
md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach (string s in O.PeptideSamples.Union(N.PeptideSamples).Order(StringComparer.Ordinal))
{
    var (no, nn, both, r, ratio) = Compare(O.Peptides, N.Peptides, s);
    md.AppendLine($"| {s} | {no} | {nn} | {both} | {F(r)} | {F(ratio)} | {O.Detected(s, "MSMS")} | {N.Detected(s, "MSMS")} | {O.Detected(s, "MBR")} | {N.Detected(s, "MBR")} |");
}

md.AppendLine("\n## Protein groups (AllQuantifiedProteinGroups.tsv, target, Protein QValue <= 0.01)\n");
md.AppendLine($"| sample | quantified {lo} | quantified {ln} | both | r (log2) | median log2 {ln}/{lo} |");
md.AppendLine("|---|---:|---:|---:|---:|---:|");
foreach (string s in O.GroupSamples.Union(N.GroupSamples).Order(StringComparer.Ordinal))
{
    var (no, nn, both, r, ratio) = Compare(O.Groups, N.Groups, s);
    md.AppendLine($"| {s} | {no} | {nn} | {both} | {F(r)} | {F(ratio)} |");
}
md.AppendLine($"\n| measure | {lo} | {ln} |\n|---|---:|---:|");
md.AppendLine($"| protein groups at 1% (target) | {O.Groups.Count} | {N.Groups.Count} |");
md.AppendLine($"| occupancy cells, all | {O.OccupancyCells} | {N.OccupancyCells} |");
md.AppendLine($"| occupancy entries written 0/N (#2889) | {O.ZeroOfN} | {N.ZeroOfN} |");

File.WriteAllText(a["out"], md.ToString());
Console.WriteLine(md.ToString());

static (int Old, int New, int Both, double R, double MedianRatio) Compare(
    Dictionary<string, Dictionary<string, double>> o, Dictionary<string, Dictionary<string, double>> n, string sample)
{
    var qo = o.Where(kv => kv.Value.GetValueOrDefault(sample) > 0).ToDictionary(kv => kv.Key, kv => kv.Value[sample], StringComparer.Ordinal);
    var qn = n.Where(kv => kv.Value.GetValueOrDefault(sample) > 0).ToDictionary(kv => kv.Key, kv => kv.Value[sample], StringComparer.Ordinal);
    var both = qo.Keys.Where(qn.ContainsKey).ToList();
    var x = both.Select(k => Math.Log2(qo[k])).ToList();
    var y = both.Select(k => Math.Log2(qn[k])).ToList();
    var ratios = both.Select(k => Math.Log2(qn[k] / qo[k])).Order().ToList();
    return (qo.Count, qn.Count, both.Count, Pearson(x, y), ratios.Count == 0 ? double.NaN : ratios[ratios.Count / 2]);
}

static double Pearson(List<double> x, List<double> y)
{
    if (x.Count < 3) return double.NaN;
    double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
    for (int i = 0; i < x.Count; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
    return sxx == 0 || syy == 0 ? double.NaN : sxy / Math.Sqrt(sxx * syy);
}

static Quant Load(string searchDir)
{
    string task = Directory.EnumerateDirectories(Path.Combine(searchDir, "mm"), "Task*SearchTask").Order(StringComparer.Ordinal).Last();
    var q = new Quant();
    foreach (var row in Read(Path.Combine(task, "AllQuantifiedPeptides.tsv")))
    {
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, val) in row)
        {
            if (k.StartsWith("Intensity_", StringComparison.Ordinal)) { string s = k["Intensity_".Length..]; v[s] = Num(val); q.PeptideSamples.Add(s); }
            else if (k.StartsWith("Detection Type_", StringComparison.Ordinal))
            {
                var key = (k["Detection Type_".Length..], val);
                q.Detection[key] = q.Detection.GetValueOrDefault(key) + 1;
            }
        }
        q.Peptides[row["Sequence"]] = v;
    }
    foreach (var row in Read(Path.Combine(task, "AllQuantifiedProteinGroups.tsv")))
    {
        if (row.GetValueOrDefault("Protein Decoy/Contaminant/Target") != "T" || !(Num(row.GetValueOrDefault("Protein QValue", "")) <= 0.01)) continue;
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, val) in row)
        {
            if (k.StartsWith("Intensity_", StringComparison.Ordinal)) { string s = k["Intensity_".Length..]; v[s] = Num(val); q.GroupSamples.Add(s); }
            else if (k.StartsWith("CountOccupancy_", StringComparison.Ordinal) && val.Length > 0)
            {
                q.OccupancyCells++;
                q.ZeroOfN += Regex.Matches(val, @"(?<![0-9])0/[0-9]+").Count;
            }
        }
        q.Groups[row["Protein Accession"]] = v;
    }
    return q;
}

static IEnumerable<Dictionary<string, string>> Read(string path)
{
    using var sr = new StreamReader(path);   // ReadLine ends a line at \n, \r or \r\n
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

static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
static string F(double d) => double.IsNaN(d) ? "n/a" : d.ToString("0.000", CultureInfo.InvariantCulture);
static Dictionary<string, string> Args(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
    foreach (string k in new[] { "old", "new", "out" }) if (!d.ContainsKey(k)) throw new ArgumentException($"--{k} is required");
    return d;
}

sealed class Quant
{
    public Dictionary<string, Dictionary<string, double>> Peptides = new(StringComparer.Ordinal), Groups = new(StringComparer.Ordinal);
    public HashSet<string> PeptideSamples = new(StringComparer.Ordinal), GroupSamples = new(StringComparer.Ordinal);
    public Dictionary<(string Sample, string Type), int> Detection = new();
    public long OccupancyCells, ZeroOfN;
    public int Detected(string sample, string type) => Detection.GetValueOrDefault((sample, type));
}
