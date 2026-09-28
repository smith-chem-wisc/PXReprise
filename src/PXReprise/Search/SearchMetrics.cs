using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PXReprise.Search;

public sealed record IdRate(int? Psms1Pct, int Ms2, double? Rate, int? PsmsFdrEngine1Pct)
{
    public const string Definition = "aging DEF-PSM-1PCT v1";
    public const string FdrEngineDefinition = "aging DEF-PSM-FDRENGINE v1";
}

public sealed record MbrCounts(int MbrRows, int MbrRandomRtWon, int MbrKept, int MsmsPeaks, double MbrFdrThreshold, double? KeptOverMsms)
{
    public const string Definition = "QuantProject DEF-QC-MBR v1";
}

public sealed record Contamination(
    double? PsmShare, int ContaminantPsms, int TargetPlusContaminantPsms,
    IReadOnlyDictionary<string, double?> IntensitySharePerFile, double IntensityShareMedian, double? IntensityShareMin,
    double IntensityShareMax,
    IReadOnlyDictionary<string, double?> IntensityShareUpperPerFile, double? IntensityShareUpperMedian,
    double? IntensityShareUpperMax, int? SharedAccessionsN, string? SharedAccessionsSha256, IReadOnlyList<string> Top)
{
    public const string PsmShareDefinition = "aging DEF-CONTAM-PSM v1";
    public const string IntensityShareDefinition = "QuantProject DEF-QC-9 v3.5";
    public const string UpperDefinition = "aging DEF-CONTAM-INT-SHARED v1";
}

/// <summary>
/// The derived measurements of a search, computed from MetaMorpheus's own output tables. Every definition here is
/// owned by the project that named it (QuantProject DEF-*, aging DEF-*); this is their single implementation in the
/// engine, reproducing aging's search_mm.derive_metrics and contam_shared value for value (proven on the aging corpus).
/// </summary>
public static class SearchMetrics
{
    /// <summary>SearchParameters.MbrFdrThreshold's default in MetaMorpheus 1.1.11.</summary>
    public const double MbrFdrThreshold = 0.01;

    public static IdRate IdRate(SearchSummary summary, int ms2)
    {
        int? psms = summary.Psms1Pct;
        double? rate = psms is > 0 && ms2 > 0 ? PyRound((double)psms.Value / ms2, 4) : null;
        return new IdRate(psms, ms2, rate, summary.PsmsFdrEngine1Pct);
    }

    public enum PeakKind { None, Msms, MbrKept, MbrOther }

    /// <summary>
    /// QuantProject DEF-MBR-KEPT v1: the peaks table is written UNFILTERED, so a raw MBR row is not a transfer used in
    /// quantification. Kept = PIP q-value below the threshold, not a random-RT decoy and not a decoy peptide.
    /// </summary>
    public static (PeakKind Kind, bool RandomRtWon) ClassifyPeak(IReadOnlyDictionary<string, string> row, double thr = MbrFdrThreshold)
    {
        string kind = row.GetValueOrDefault("Peak Detection Type") ?? "";
        if (kind == "MSMS") return (PeakKind.Msms, false);
        if (kind != "MBR") return (PeakKind.None, false);
        bool randomRt = string.Equals(row.GetValueOrDefault("Random RT"), "true", StringComparison.OrdinalIgnoreCase);
        double q = Tsv.Number(row.GetValueOrDefault("PIP Q-Value"));
        bool decoy = string.Equals(row.GetValueOrDefault("Decoy Peptide"), "true", StringComparison.OrdinalIgnoreCase);
        bool kept = q < thr && !randomRt && !decoy;   // NaN < thr is false, as in Python
        return (kept ? PeakKind.MbrKept : PeakKind.MbrOther, randomRt);
    }

    public static MbrCounts Mbr(string peaksTsv, double thr = MbrFdrThreshold)
    {
        int rows = 0, kept = 0, random = 0, msms = 0;
        foreach (var r in Tsv.Read(peaksTsv))
        {
            var (kind, rnd) = ClassifyPeak(r, thr);
            if (kind == PeakKind.Msms) msms++;
            else if (kind != PeakKind.None)
            {
                rows++;
                if (rnd) random++;
                if (kind == PeakKind.MbrKept) kept++;
            }
        }
        return new MbrCounts(rows, random, kept, msms, thr, msms > 0 ? PyRound((double)kept / msms, 3) : null);
    }

    /// <summary>DEF-QC-9 v3.5's row filter: a protein group at Protein QValue &lt;= 0.01; blank or unparsable fails.</summary>
    public static bool ProteinGroupAt1Pct(IReadOnlyDictionary<string, string> row) => Tsv.Number(row.GetValueOrDefault("Protein QValue")) <= 0.01;

    /// <summary>
    /// Both contamination bounds (aging D53): the PSM share (aging DEF-CONTAM-PSM v1), the per-file intensity share
    /// (QuantProject DEF-QC-9 v3.5, the LOWER bound: MetaMorpheus's C) and, when the databases are given, the UPPER
    /// bound that also counts target groups made only of accessions shared between the contaminant panel and the
    /// proteome (aging DEF-CONTAM-INT-SHARED v1).
    /// </summary>
    public static Contamination Contamination(string allPsms, string? proteinGroups, IReadOnlyCollection<string>? shared, string? sharedSha)
    {
        int tgt = 0, cPsm = 0;
        foreach (var r in Tsv.Read(allPsms))
        {
            if (!(Tsv.Number(r.GetValueOrDefault("QValue") is { Length: > 0 } qv ? qv : "1") <= 0.01)) continue;
            string dct = r.GetValueOrDefault("Decoy/Contaminant/Target") ?? "";
            if (dct.StartsWith('D')) continue;
            tgt++;
            if (dct == "C") cPsm++;
        }

        var perFile = new Dictionary<string, double?>();
        var upper = new Dictionary<string, double?>();
        var top = new Dictionary<string, double>();
        if (proteinGroups is not null && File.Exists(proteinGroups))
        {
            var pgs = Tsv.Read(proteinGroups).Where(ProteinGroupAt1Pct).ToList();
            var cols = pgs.Count > 0 ? pgs[0].Keys.Where(k => k.StartsWith("Intensity_", StringComparison.Ordinal)).ToList() : new List<string>();
            var sharedSet = shared is null ? null : new HashSet<string>(shared, StringComparer.Ordinal);
            foreach (string col in cols)
            {
                double tot = 0, con = 0, conUpper = 0;
                foreach (var r in pgs)
                {
                    string td = r["Protein Decoy/Contaminant/Target"];
                    if (td is not ("T" or "C")) continue;
                    double v = Tsv.NumberOrZero(r[col]);
                    tot += v;
                    if (td == "C") con += v;
                    if (sharedSet is not null)
                    {
                        var accs = r["Protein Accession"].Split('|').Where(a => a.Length > 0).ToList();
                        if (td == "C" || (accs.Count > 0 && accs.All(sharedSet.Contains))) conUpper += v;
                    }
                }
                string file = col["Intensity_".Length..];
                perFile[file] = tot != 0 ? PyRound(con / tot, 4) : null;
                if (sharedSet is not null) upper[file] = tot != 0 ? PyRound(conUpper / tot, 4) : null;
            }
            foreach (var r in pgs.Where(r => r["Protein Decoy/Contaminant/Target"] == "C"))
                top[$"{r["Protein Full Name"]} ({r["Organism"]})"] =
                    r.Where(kv => kv.Key.StartsWith("Intensity_", StringComparison.Ordinal)).Sum(kv => Tsv.NumberOrZero(kv.Value));
        }

        var vals = perFile.Values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        var uvals = upper.Values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        return new Contamination(
            tgt > 0 ? PyRound((double)cPsm / tgt, 4) : null, cPsm, tgt,
            perFile, vals.Count > 0 ? vals[vals.Count / 2] : 0, vals.Count > 0 ? vals[0] : null, vals.Count > 0 ? vals[^1] : 0,
            upper, uvals.Count > 0 ? uvals[uvals.Count / 2] : null, uvals.Count > 0 ? uvals[^1] : null,
            shared?.Count, sharedSha,
            top.Select((kv, i) => (kv, i)).OrderByDescending(x => x.kv.Value).ThenBy(x => x.i).Take(5).Select(x => x.kv.Key).ToList());
    }

    private static readonly Regex AccessionTag = new("<accession>([^<]+)</accession>");

    /// <summary>The first &lt;accession&gt; of every &lt;entry&gt; in a UniProt-style XML: MetaMorpheus's protein accession.</summary>
    public static HashSet<string> PrimaryAccessions(string xml)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        bool want = false;
        foreach (string line in File.ReadLines(xml))
        {
            if (line.Contains("<entry")) want = true;
            if (!want) continue;
            var m = AccessionTag.Match(line);
            if (m.Success)
            {
                result.Add(m.Groups[1].Value);
                want = false;
            }
        }
        return result;
    }

    /// <summary>(sorted shared accessions, sha256 of the newline-joined list): panel accessions also in a searched proteome.</summary>
    public static (IReadOnlyList<string> Shared, string Sha256) SharedAccessions(string contaminantXml, IEnumerable<string> proteomeXmls)
    {
        var contam = PrimaryAccessions(contaminantXml);
        var target = new HashSet<string>(StringComparer.Ordinal);
        foreach (string x in proteomeXmls) target.UnionWith(PrimaryAccessions(x));
        var shared = contam.Where(target.Contains).OrderBy(a => a, StringComparer.Ordinal).ToList();
        return (shared, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", shared)))));
    }

    /// <summary>
    /// Python's round(x, n), which rounds the double's exact value, halves to even. .NET's Math.Round(double, int)
    /// scales in binary and can differ near a midpoint. The 17 significant digits of "G17" carry the double's value
    /// past any decimal midpoint that matters here, and decimal rounding then decides as Python does.
    /// </summary>
    public static double PyRound(double x, int digits) =>
        double.IsFinite(x) && Math.Abs(x) < 1e15
            ? (double)Math.Round(decimal.Parse(x.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), digits, MidpointRounding.ToEven)
            : x;
}
