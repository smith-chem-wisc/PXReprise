using System.Globalization;
using System.Text;

/// <summary>
/// Each draft against the curated SDRF of the same deposit. Rows are matched on (data file, label), so a TMT deposit is
/// compared channel by channel; the curated file may leave runs out (a question's exclusions), so only shared rows count.
/// Values that are names a curator chose (sample names, group names) are compared as PARTITIONS: the Rand index over all
/// pairs of shared rows says how often the two files agree that two rows belong together. Values with a fixed vocabulary
/// (organism, label, fraction, technical replicate, other characteristics) are compared as values, case- and space-blind.
/// </summary>
internal static class Grading
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string DataFile = "comment[data file]", Label = "comment[label]", Source = "source name";
    private static readonly string[] ValueColumns =
        { "characteristics[organism]", Label, "comment[fraction identifier]", "comment[technical replicate]" };

    internal static int Run(string outRoot, string curatedDir, IReadOnlyList<string> accs)
    {
        var lines = new List<string>
        {
            string.Join('\t', "accession", "draft", "rows_curated", "rows_draft", "rows_shared", "organism", "label", "fraction",
                "technical_replicate", "samples_curated", "samples_draft", "samples_ari", "factor", "groups_curated", "best_draft_column",
                "groups_draft", "factor_ari", "other_characteristics"),
        };
        foreach (string acc in accs)
        {
            string curated = Path.Combine(curatedDir, acc + ".sdrf.tsv");
            if (!File.Exists(curated)) { Console.WriteLine($"{acc}: no curated SDRF"); continue; }
            var c = Read(curated);
            foreach (string variant in new[] { "draft.noevidence.sdrf.tsv", "draft.sdrf.tsv" })
            {
                string path = Path.Combine(outRoot, acc, variant);
                if (!File.Exists(path)) { Console.WriteLine($"{acc}: no {variant}"); continue; }
                var d = Read(path);
                foreach (string line in Compare(acc, variant.Replace(".sdrf.tsv", ""), c, d)) lines.Add(line);
            }
        }
        string outFile = Path.Combine(outRoot, "grade.tsv");
        File.WriteAllLines(outFile, lines);
        foreach (string l in lines) Console.WriteLine(l);
        Console.WriteLine($"-> {outFile}");
        return 0;
    }

    private static IEnumerable<string> Compare(string acc, string variant, Table c, Table d)
    {
        // A draft may carry a deposit-level label for a label-free file and the curated one "label free sample": match on
        // the data file alone when every curated file has one row, else on (data file, label).
        bool byFileOnly = c.Rows.GroupBy(r => Get(r, c, DataFile)).All(g => g.Count() == 1);
        string Key(Dictionary<string, string> r, Table t) => byFileOnly ? Norm(Get(r, t, DataFile)) : Norm(Get(r, t, DataFile)) + "|" + Norm(Get(r, t, Label));
        var dByKey = d.Rows.GroupBy(r => Key(r, d)).ToDictionary(g => g.Key, g => g.First());
        var pairs = c.Rows.Select(r => (C: r, D: dByKey.GetValueOrDefault(Key(r, c)))).Where(p => p.D is not null).Select(p => (p.C, D: p.D!)).ToList();

        string Pct(string col) => pairs.Count == 0 || !c.Has(col) ? "" : !d.Has(col) ? "absent"
            : (100.0 * pairs.Count(p => Norm(Get(p.C, c, col)) == Norm(Get(p.D, d, col))) / pairs.Count).ToString("0", Inv) + "%";
        var cSamples = pairs.Select(p => Get(p.C, c, Source)).ToList();
        var dSamples = pairs.Select(p => Get(p.D, d, Source)).ToList();
        string samplesRand = Rand(cSamples, dSamples);

        // Characteristics the curator filled other than organism: value agreement where the draft has the column.
        var others = c.Columns.Where(h => h.StartsWith("characteristics[", StringComparison.Ordinal) && h != "characteristics[organism]"
                                          && pairs.Any(p => Informative(Get(p.C, c, h))))
            .Select(h => $"{h[16..^1]} {Pct(h)}").ToList();

        string head = string.Join('\t', acc, variant, c.Rows.Count.ToString(Inv), d.Rows.Count.ToString(Inv), pairs.Count.ToString(Inv),
            Pct(ValueColumns[0]), Pct(ValueColumns[1]), Pct(ValueColumns[2]), Pct(ValueColumns[3]),
            cSamples.Distinct().Count().ToString(Inv), dSamples.Distinct().Count().ToString(Inv), samplesRand);

        // Every curated factor, against the draft column (factor value or characteristic) that partitions the rows most alike.
        var factors = c.Columns.Where(h => h.StartsWith("factor value[", StringComparison.Ordinal)).ToList();
        var candidates = d.Columns.Where(h => (h.StartsWith("factor value[", StringComparison.Ordinal) || h.StartsWith("characteristics[", StringComparison.Ordinal))
                                              && h is not "characteristics[organism]" and not "characteristics[biological replicate]").ToList();
        if (factors.Count == 0)
        {
            yield return string.Join('\t', head, "", "", "", "", "", string.Join("; ", others));
            yield break;
        }
        foreach (string f in factors)
        {
            var cf = pairs.Select(p => Get(p.C, c, f)).ToList();
            var best = candidates.Select(h => (Col: h, Values: pairs.Select(p => Get(p.D, d, h)).ToList()))
                .Select(x => (x.Col, x.Values, R: RandValue(cf, x.Values)))
                .OrderByDescending(x => x.R).ThenBy(x => x.Col, StringComparer.Ordinal).FirstOrDefault();
            yield return string.Join('\t', head, f[13..^1], cf.Distinct().Count().ToString(Inv),
                best.Col ?? "(none)", best.Values?.Distinct().Count().ToString(Inv) ?? "", best.Col is null ? "" : best.R.ToString("0.000", Inv),
                string.Join("; ", others));
        }
    }

    /// <summary>The adjusted Rand index (Hubert and Arabie) of two labellings of the same rows: 1 is the same partition, about
    /// 0 is what chance gives. The plain Rand index is near 1 whenever both have many small groups (most pairs are "apart" in
    /// both), which says nothing; this one does not. Two trivial partitions (each all-one-group or all-singletons) score 1
    /// only when they are the same.</summary>
    internal static double RandValue(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count;
        if (n < 2) return 1;
        static double Pairs(long k) => k * (k - 1) / 2.0;
        double index = a.Zip(b).GroupBy(p => p).Sum(g => Pairs(g.Count()));
        double sa = a.GroupBy(x => x).Sum(g => Pairs(g.Count())), sb = b.GroupBy(x => x).Sum(g => Pairs(g.Count()));
        double expected = sa * sb / Pairs(n), max = (sa + sb) / 2;
        if (Math.Abs(max - expected) < 1e-12)
            return SamePartition(a, b) ? 1 : 0;
        return (index - expected) / (max - expected);
    }

    internal static bool SamePartition(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Zip(b).GroupBy(p => p.First).All(g => g.Select(p => p.Second).Distinct().Count() == 1)
        && a.Zip(b).GroupBy(p => p.Second).All(g => g.Select(p => p.First).Distinct().Count() == 1);

    private static string Rand(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.Count < 2 ? "" : RandValue(a, b).ToString("0.000", Inv);

    private static bool Informative(string v) => Norm(v) is { Length: > 0 } s && s != "not available" && s != "not applicable";

    /// <summary>Case- and space-blind; an ontology-tagged value ("NT=homo sapiens;AC=NCBITaxon:9606") compares by its name,
    /// since an SDRF may write either form.</summary>
    private static string Norm(string v)
    {
        string s = v.Trim();
        int nt = s.IndexOf("NT=", StringComparison.OrdinalIgnoreCase);
        if (nt >= 0)
        {
            int end = s.IndexOf(';', nt);
            s = end < 0 ? s[(nt + 3)..] : s[(nt + 3)..end];
        }
        return string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Get(Dictionary<string, string> row, Table t, string column) => row.GetValueOrDefault(column, "");

    internal sealed record Table(List<string> Columns, List<Dictionary<string, string>> Rows)
    {
        public bool Has(string column) => Columns.Contains(column);
    }

    /// <summary>A TSV with a header; the first column of a repeated header wins (an SDRF may repeat comment columns).</summary>
    internal static Table Read(string path)
    {
        var lines = File.ReadAllText(path, Encoding.UTF8).ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).ToList();
        var header = lines[0].Split('\t').Select(h => h.Trim().ToLowerInvariant()).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (string l in lines.Skip(1))
        {
            var cells = l.Split('\t');
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Count; i++) row.TryAdd(header[i], i < cells.Length ? cells[i].Trim() : "");
            rows.Add(row);
        }
        return new Table(header.Distinct().ToList(), rows);
    }
}
