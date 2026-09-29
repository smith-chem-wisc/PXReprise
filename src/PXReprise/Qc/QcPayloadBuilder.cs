using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PXReprise.Search;

namespace PXReprise.Qc;

/// <summary>
/// The histogram bin rule for the payload's distributions. The rule is qc's (<c>qctemplates.spec</c>, qc 007 §4): when a
/// Python with qctemplates installed is configured, its constants are read from there; otherwise a vendored copy is
/// used and the payload SAYS so in <c>dataset.notes</c> (the fallback announces itself, never passes silently).
/// </summary>
public sealed record QcBins(double[] Precursor, double[] Fragment, int RtBins, string Source, bool FromQc)
{
    public static QcBins Vendored(string why = "qctemplates not installed") =>
        new(new[] { -10.0, 10.0, 0.5 }, new[] { -30.0, 30.0, 1.0 }, 36, $"vendored copy ({why})", false);

    /// <summary>qc's constants, read from <paramref name="python"/>'s qctemplates; the vendored copy when that fails.</summary>
    public static QcBins FromQcTemplates(string? python)
    {
        if (string.IsNullOrEmpty(python)) return Vendored();
        const string code = "import json; from qctemplates import spec; print(json.dumps({'p': list(spec.PRECURSOR_PPM_BINS), "
                            + "'f': list(spec.FRAGMENT_PPM_BINS), 'n': spec.IDS_OVER_RT_NBINS}))";
        try
        {
            var (rc, output) = QcTemplates.Run(python, new[] { "-c", code }, TimeSpan.FromMinutes(2));
            if (rc != 0) return Vendored($"qctemplates unavailable from {python}: exit {rc}");
            var o = JsonNode.Parse(output.Trim().Split('\n')[^1])!;
            return new QcBins(o["p"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(),
                o["f"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(), o["n"]!.GetValue<int>(),
                $"qctemplates.spec constants read via {python}", true);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or NullReferenceException)
        {
            return Vendored($"qctemplates unavailable from {python}: {e.Message}");
        }
    }

    /// <summary><c>qctemplates.spec.linear_edges</c>: <c>n = int(round((high - low) / width))</c>, each edge rounded to 9 places.</summary>
    public static double[] LinearEdges(double low, double high, double width)
    {
        int n = (int)Math.Round((high - low) / width, MidpointRounding.ToEven);
        var e = new double[n + 1];
        for (int i = 0; i <= n; i++) e[i] = SearchMetrics.PyRound(low + i * width, 9);
        return e;
    }

    public double[]? EdgesFor(string key, double? runMinutes) => key switch
    {
        "precursor_ppm" => LinearEdges(Precursor[0], Precursor[1], Precursor[2]),
        "fragment_ppm" => LinearEdges(Fragment[0], Fragment[1], Fragment[2]),
        "ids_over_rt" => runMinutes is { } m && m != 0 ? LinearEdges(0.0, m, m / RtBins) : null,
        _ => throw new KeyNotFoundException(key),
    };
}

/// <summary>What one payload build produced.</summary>
public sealed record QcPayloadResult(JsonObject Payload, int Files, int? ProteinGroupsQuantified);

/// <summary>
/// Builds qc's <c>qc-payload/1</c> from a finished MetaMorpheus search: a port of aging's
/// <c>pipeline/bin/qc_payload.py</c>, value for value (proven on the aging corpus). qc owns the contract
/// (<c>qc/design/CONTRACT.md</c>); this supplies numbers, never verdicts: gates, outliers and derived ratios
/// (<c>id_rate</c>, <c>mbr_msms_ratio</c>) are qc's to compute. Every value carries the definition it was computed under.
/// The shapes that drive the code:
/// <list type="bullet">
/// <item><c>pg_missing_frac_msms</c> needs the DATASET first, so the build is two-pass. It is DEF-QC-13's <c>_msms</c>
/// variant (SpectralCount_ &gt; 0), and it is OMITTED, never zeroed, when the protein-group table has no SpectralCount_
/// columns (qc's QC-Q8 null rule: absent means not measured). The <c>_any</c> variant is never written.</item>
/// <item>The whole-search counts are results.txt's summary lines, never a sum of the per-file lines.</item>
/// <item><c>id_rt_coverage</c> divides by run minutes, a stage-2b fact, so the build takes the search AND the qc directory.</item>
/// </list>
/// Three bugs the aging render and tests caught are pinned by tests here: <see cref="Stem"/> strips <c>.toml</c> (else
/// <c>calibration_ok</c> is false for every file of a run that calibrated), the RT coverage uses Python's nearest-rank
/// indices (truncation understated coverage ninefold on small n), and a file whose every MBR candidate was rejected gets
/// <c>mbr_kept = 0</c>, not an absent key (qc renders absent as a dash).
/// </summary>
public static class QcPayloadBuilder
{
    public const string Schema = "qc-payload/1";

    /// <summary>The definition each value was computed under; the same block, key for key, as aging's builder.</summary>
    public static JsonObject Definitions() => new()
    {
        ["psms"] = Def("aging:DEF-PSM-1PCT-RUN", "v1", "pipeline/docs/provenance.md"),
        ["peptides"] = Def("aging:DEF-PEPTIDE-1PCT-RUN", "v1", "pipeline/docs/provenance.md"),
        ["protein_groups"] = Def("aging:DEF-PROTEINGROUP-1PCT-RUN", "v1", "pipeline/docs/provenance.md"),
        ["ms2_scans"] = Def("aging:DEF-MS2", "v1", "pipeline/docs/provenance.md"),
        ["msms_peaks"] = Def("QuantProject:DEF-QC-MBR", "v1", "QuantProject/design/DATA-DEFINITIONS.md"),
        ["mbr_kept"] = Def("QuantProject:DEF-MBR-KEPT", "v1", "QuantProject/design/DATA-DEFINITIONS.md"),
        ["pg_missing_frac_msms"] = Def("QuantProject:DEF-QC-13", "v1",
            "QuantProject/design/DATA-DEFINITIONS.md - the _msms variant (SpectralCount_ > 0), per file"),
        ["dataset_psms"] = Def("aging:DEF-PSM-1PCT", "v1", "pipeline/docs/provenance.md"),
        ["dataset_peptides"] = Def("aging:DEF-PEPTIDE-1PCT", "v1", "pipeline/docs/provenance.md"),
        ["dataset_protein_groups"] = Def("aging:DEF-PROTEINGROUP-1PCT", "v1", "pipeline/docs/provenance.md"),
        // Run grain, NOT the dataset-grain aging:DEF-CONTAM-PSM (aging D31): a number is stored at the grain it was measured at.
        ["contaminant_psm_share"] = Def("aging:DEF-CONTAM-PSM-RUN", "v2", "pipeline/docs/provenance.md"),
        ["contaminant_intensity_frac"] = Def("QuantProject:DEF-QC-9", "v3.5", "QuantProject/design/DATA-DEFINITIONS.md"),
    };

    private static JsonObject Def(string id, string version, string source) => new() { ["id"] = id, ["version"] = version, ["source"] = source };

    // ------------------------------------------------------------------ small rules, each pinned by a test

    private static readonly string[] Exts = { ".raw", ".mzml", ".toml", ".mzxml" };

    /// <summary>The contract's file key: no extension (one of .raw/.mzML/.toml/.mzXML, any case), no <c>-calib</c> suffix.
    /// Not Path.GetFileNameWithoutExtension: a run name may contain a dot.</summary>
    public static string Stem(string? name)
    {
        // Both separators, not Path.GetFileName: a results file written on Windows is read on Linux too.
        string n = (name ?? "None").Split('\\', '/')[^1];
        foreach (string ext in Exts)
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n[..^ext.Length]; break; }
        return n.EndsWith("-calib", StringComparison.Ordinal) ? n[..^"-calib".Length] : n;
    }

    /// <summary>A number or null: blank, unparsable, NaN and ambiguous <c>a|b</c> cells have no single value.</summary>
    public static double? Num(string? v)
    {
        if (v is null) return null;
        string s = v.Trim();
        if (s.Length == 0 || s.Contains('|')) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) ? d : null;
    }

    /// <summary>Python's <c>bisect.bisect_right</c>.</summary>
    private static int BisectRight(double[] a, double x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (x < a[mid]) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Counts over <paramref name="edges"/>, values CLIPPED into range (a tail is still a count).</summary>
    public static JsonObject Histogram(IEnumerable<double> values, double[] edges)
    {
        double lo = edges[0], hi = edges[^1];
        var counts = new int[edges.Length - 1];
        foreach (double v in values)
        {
            int i = BisectRight(edges, Math.Min(Math.Max(v, lo), hi)) - 1;
            counts[Math.Min(Math.Max(i, 0), counts.Length - 1)]++;
        }
        return new JsonObject
        {
            ["edges"] = new JsonArray(edges.Select(e => (JsonNode?)e).ToArray()),
            ["counts"] = new JsonArray(counts.Select(c => (JsonNode?)c).ToArray()),
        };
    }

    /// <summary><c>statistics.median</c>.</summary>
    public static double Median(IReadOnlyList<double> xs)
    {
        var d = xs.OrderBy(x => x).ToList();
        int n = d.Count;
        return n % 2 == 1 ? d[n / 2] : (d[n / 2 - 1] + d[n / 2]) / 2;
    }

    /// <summary>Q3 - Q1 by <c>statistics.quantiles(xs, n=4, method="inclusive")</c>, rounded to 4; null below two values.</summary>
    public static double? Iqr(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return null;
        var d = xs.OrderBy(x => x).ToList();
        int m = d.Count - 1;
        double Q(int i)
        {
            int j = Math.DivRem(i * m, 4, out int delta);
            return (d[j] * (4 - delta) + d[j + 1] * delta) / 4;
        }
        return SearchMetrics.PyRound(Q(3) - Q(1), 4);
    }

    /// <summary>
    /// The span of retention times that holds the IDs, as a fraction of the run: nearest-rank 1st and 99th percentiles
    /// (<c>ceil(p*n/100) - 1</c>). Truncating <c>int(0.99*(n-1))</c> collapses towards the median on small n.
    /// </summary>
    public static double IdRtCoverage(IReadOnlyList<double> rts, double runMinutes)
    {
        var s = rts.OrderBy(x => x).ToList();
        int n = s.Count;
        double lo = s[Math.Max(0, (n + 99) / 100 - 1)];
        double hi = s[Math.Min(n - 1, (99 * n + 99) / 100 - 1)];
        return SearchMetrics.PyRound(Math.Max(0.0, (hi - lo) / runMinutes), 4);
    }

    private static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n');

    // ------------------------------------------------------------------ parsers

    private static readonly (string Key, Regex Re)[] PerFileLines =
    {
        ("ms2_scans", new Regex(@"^(.+?) - MS2 Scans: (\d+)$", RegexOptions.Multiline)),
        ("psms", new Regex(@"^(.+?) - Target PSMs with q-value <= 0\.01: (\d+)$", RegexOptions.Multiline)),
        ("peptides", new Regex(@"^(.+?) - Target peptides with q-value <= 0\.01: (\d+)$", RegexOptions.Multiline)),
        ("protein_groups", new Regex(@"^(.+?) - Target protein groups with q-value <= 0\.01: (\d+)$", RegexOptions.Multiline)),
    };

    /// <summary>Per-file counts from results.txt: each from an FDR recomputed on that file alone, never summed (the -RUN definitions).</summary>
    public static Dictionary<string, JsonObject> ParseResultsTxt(string path)
    {
        string text = ReadText(path);
        var o = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (key, re) in PerFileLines)
            foreach (Match m in re.Matches(text))
            {
                string f = Stem(m.Groups[1].Value);
                if (!o.TryGetValue(f, out var rec)) o[f] = rec = new JsonObject();
                rec[key] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        return o;
    }

    private static readonly (string Key, Regex Re)[] DatasetLines =
    {
        ("dataset_psms", new Regex(@"^All target PSMs with q-value <= 0\.01: (\d+)", RegexOptions.Multiline)),
        ("dataset_peptides", new Regex(@"^All target peptides with q-value <= 0\.01: (\d+)", RegexOptions.Multiline)),
        ("dataset_protein_groups", new Regex(@"^All target protein groups with q-value <= 0\.01[^:]*: (\d+)", RegexOptions.Multiline)),
    };

    /// <summary>The whole-search counts: results.txt's SUMMARY lines. An absent line gives no key, not a zero.</summary>
    public static JsonObject ParseDatasetCounts(string path)
    {
        string text = ReadText(path);
        var o = new JsonObject();
        foreach (var (key, re) in DatasetLines)
            if (re.Match(text) is { Success: true } m) o[key] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return o;
    }

    /// <summary>
    /// <c>calibration_ok</c> keys on the <c>-calib.toml</c> existing (MetaMorpheus writes it only on success). The tolerances
    /// are strings like <c>"±3.1000 PPM"</c>: a U+00B1 in UTF-8, so the file is read as UTF-8 and the NUMBER is matched,
    /// never the sign (a decode without the encoding turns the sign into mojibake on Windows).
    /// </summary>
    public static Dictionary<string, JsonObject> ParseCalibration(string calDir)
    {
        var o = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!Directory.Exists(calDir)) return o;
        foreach (string toml in Directory.EnumerateFiles(calDir, "*-calib.toml").Where(f => f.EndsWith("-calib.toml", StringComparison.Ordinal)))
        {
            string body = ReadText(toml);
            var rec = new JsonObject { ["calibration_ok"] = true };
            foreach (var (key, field) in new[] { ("cal_precursor_tol_ppm", "PrecursorMassTolerance"), ("cal_product_tol_ppm", "ProductMassTolerance") })
                if (new Regex($"^{field}\\s*=\\s*\"[^0-9-]*(-?[0-9.]+)", RegexOptions.Multiline).Match(body) is { Success: true } m)
                    rec[key] = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            o[Stem(Path.GetFileName(toml))] = rec;
        }
        return o;
    }

    /// <summary>
    /// The whole-search 1% set as a file-side test reproduces it (aging:DEF-PSM-1PCT-INFILE v1): QValue &lt;= 0.01 and
    /// QValue Notch &lt;= 0.01, and an unambiguous Notch (an ambiguous one fails in memory, S22).
    /// </summary>
    public static bool Accepted1Pct(IReadOnlyDictionary<string, string> row)
    {
        double? q = Num(row.GetValueOrDefault("QValue")), qn = Num(row.GetValueOrDefault("QValue Notch"));
        string notch = (row.GetValueOrDefault("Notch") ?? "").Trim();
        return q is not null && qn is not null && q <= 0.01 && qn <= 0.01 && !notch.Contains('|');
    }

    private sealed class PsmAcc
    {
        public readonly Dictionary<int, int> Charges = new();
        public int Missed, MissedN, Notch, NotchN, Contam, ContamN;
        public readonly List<double> Prec = new(), Frag = new(), Rts = new();
    }

    private static readonly Regex IonPpm = new(@":\s*(-?\d+\.?\d*)");

    /// <summary>Per-file PSM metrics and distributions over the accepted 1% target PSMs; mass errors from Notch 0 only.</summary>
    public static (Dictionary<string, JsonObject> Metrics, Dictionary<string, JsonObject> Dists) ParsePsms(
        string path, IReadOnlyDictionary<string, double?> runMinutes, QcBins bins)
    {
        if (!Tsv.Header(path).Contains("QValue Notch"))
            // Without it the population silently widens to QValue alone (qc 010 QC-Q12). Refuse rather than describe other PSMs.
            throw new InvalidDataException($"{path} has no `QValue Notch` column: cannot apply the 1% filter MetaMorpheus uses");
        var per = new Dictionary<string, PsmAcc>(StringComparer.Ordinal);
        PsmAcc For(string? file)
        {
            string k = Stem(file);
            if (!per.TryGetValue(k, out var a)) per[k] = a = new PsmAcc();
            return a;
        }
        foreach (var row in Tsv.Read(path))
        {
            string td = (row.GetValueOrDefault("Decoy/Contaminant/Target") ?? "").Trim();
            if (!Accepted1Pct(row)) continue;
            // The contaminant share (M13) is counted BEFORE the target-only filter, over target + contaminant; an ambiguous
            // C|T counts as not-contaminant and stays in the denominator.
            if (td.Length > 0 && !td.ToUpperInvariant().Contains('D'))
            {
                var cf = For(row.GetValueOrDefault("File Name"));
                if (td == "C") cf.Contam++;
                cf.ContamN++;
            }
            if (td != "T") continue;
            var f = For(row.GetValueOrDefault("File Name"));
            if (Num(row.GetValueOrDefault("Precursor Charge")) is { } ch)
            {
                int c = (int)ch;
                f.Charges[c] = f.Charges.GetValueOrDefault(c) + 1;
            }
            if (Num(row.GetValueOrDefault("Missed Cleavages")) is { } mc)
            {
                if (mc >= 1) f.Missed++;
                f.MissedN++;
            }
            string notch = (row.GetValueOrDefault("Notch") ?? "").Trim();
            if (notch.Length > 0 && !notch.Contains('|'))
            {
                if (notch != "0") f.Notch++;
                f.NotchN++;
            }
            if (Num(row.GetValueOrDefault("Scan Retention Time")) is { } rt) f.Rts.Add(rt);
            if (notch != "0") continue;
            if (Num(row.GetValueOrDefault("Mass Diff (ppm)")) is { } p) f.Prec.Add(p);
            foreach (Match m in IonPpm.Matches(row.GetValueOrDefault("Matched Ion Mass Diff (Ppm)") ?? ""))
                f.Frag.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var metrics = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var dists = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, f) in per)
        {
            int total = f.Charges.Values.Sum();
            var m = new JsonObject();
            if (total > 0)
            {
                m["charge_1_frac"] = SearchMetrics.PyRound((double)f.Charges.GetValueOrDefault(1) / total, 4);
                m["charge_2_frac"] = SearchMetrics.PyRound((double)f.Charges.GetValueOrDefault(2) / total, 4);
                m["charge_3_frac"] = SearchMetrics.PyRound((double)f.Charges.GetValueOrDefault(3) / total, 4);
                m["charge_4plus_frac"] = SearchMetrics.PyRound((double)f.Charges.Where(kv => kv.Key >= 4).Sum(kv => kv.Value) / total, 4);
            }
            if (f.MissedN > 0) m["missed_cleavage_frac"] = SearchMetrics.PyRound((double)f.Missed / f.MissedN, 4);
            if (f.NotchN > 0) m["notch_frac"] = SearchMetrics.PyRound((double)f.Notch / f.NotchN, 4);
            if (f.ContamN > 0) m["contaminant_psm_share"] = SearchMetrics.PyRound((double)f.Contam / f.ContamN, 4);
            if (f.Prec.Count > 0)
            {
                m["precursor_ppm_median"] = SearchMetrics.PyRound(Median(f.Prec), 4);
                m["precursor_ppm_iqr"] = Iqr(f.Prec);
            }
            if (f.Frag.Count > 0)
            {
                m["fragment_ppm_median"] = SearchMetrics.PyRound(Median(f.Frag), 4);
                m["fragment_ppm_iqr"] = Iqr(f.Frag);
            }
            double? minutes = runMinutes.GetValueOrDefault(name);
            bool hasMinutes = minutes is { } mm && mm != 0;
            if (f.Rts.Count > 0 && hasMinutes) m["id_rt_coverage"] = IdRtCoverage(f.Rts, minutes!.Value);
            metrics[name] = m;
            var d = new JsonObject
            {
                ["precursor_ppm"] = Histogram(f.Prec, bins.EdgesFor("precursor_ppm", null)!),
                ["fragment_ppm"] = Histogram(f.Frag, bins.EdgesFor("fragment_ppm", null)!),
            };
            if (hasMinutes)
            {
                d["ids_over_rt"] = Histogram(f.Rts, bins.EdgesFor("ids_over_rt", minutes)!);
                d["run_minutes"] = minutes;
            }
            dists[name] = d;
        }
        return (metrics, dists);
    }

    /// <summary><c>msms_peaks</c> and <c>mbr_kept</c> per file through the ONE DEF-MBR-KEPT rule (<see cref="SearchMetrics.ClassifyPeak"/>).
    /// A file with any MSMS or MBR row gets both keys, so "no transfer survived" is 0, not "not measured".</summary>
    public static Dictionary<string, JsonObject> ParsePeaks(string path)
    {
        var per = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!File.Exists(path)) return per;
        foreach (var row in Tsv.Read(path))
        {
            var (kind, _) = SearchMetrics.ClassifyPeak(row, SearchMetrics.MbrFdrThreshold);
            if (kind == SearchMetrics.PeakKind.None) continue;
            string k = Stem(row.GetValueOrDefault("File Name"));
            if (!per.TryGetValue(k, out var f)) per[k] = f = new JsonObject { ["msms_peaks"] = 0, ["mbr_kept"] = 0 };
            if (kind == SearchMetrics.PeakKind.Msms) f["msms_peaks"] = f["msms_peaks"]!.GetValue<int>() + 1;
            else if (kind == SearchMetrics.PeakKind.MbrKept) f["mbr_kept"] = f["mbr_kept"]!.GetValue<int>() + 1;
        }
        return per;
    }

    /// <summary>What pass one of the protein-group table gives: null <see cref="Quantified"/> means not measurable.</summary>
    public sealed record ProteinGroupPass(int? Quantified, IReadOnlyDictionary<string, int> Present, JsonObject RunsPer,
        IReadOnlyDictionary<string, double> ContaminantIntensity);

    /// <summary>
    /// Pass one: the dataset's quantified protein groups (not decoy, Protein QValue &lt;= 0.01, the DEF-PROTEINGROUP-1PCT
    /// predicate, which INCLUDES contaminants), the files each is present in by the <c>_msms</c> variant (SpectralCount_ &gt; 0,
    /// the file identified it itself; the <c>_any</c> variant would depend on the other files through MBR), and the per-file
    /// contaminant intensity fraction (QuantProject DEF-QC-9 v3.5, run grain, same filter). No SpectralCount_ columns:
    /// not measurable, so everything is empty rather than a quantified count of zero.
    /// </summary>
    public static ProteinGroupPass ParseProteinGroups(string path)
    {
        var empty = new ProteinGroupPass(null, new Dictionary<string, int>(), new JsonObject(), new Dictionary<string, double>());
        if (!File.Exists(path)) return empty;
        var fields = Tsv.Header(path);
        var counts = fields.Where(c => c.StartsWith("SpectralCount_", StringComparison.Ordinal)).Select(c => (c, Stem(c["SpectralCount_".Length..]))).ToList();
        var inten = fields.Where(c => c.StartsWith("Intensity_", StringComparison.Ordinal)).Select(c => (c, Stem(c["Intensity_".Length..]))).ToList();
        if (counts.Count == 0) return empty;
        var present = new Dictionary<string, int>(StringComparer.Ordinal);
        var runsPer = new SortedDictionary<int, int>();
        var contamInt = new Dictionary<string, double>(StringComparer.Ordinal);
        var totalInt = new Dictionary<string, double>(StringComparer.Ordinal);
        int quantified = 0;
        foreach (var row in Tsv.Read(path))
        {
            string td = (row.GetValueOrDefault("Protein Decoy/Contaminant/Target") ?? "").Trim().ToUpperInvariant();
            if (td.StartsWith('D')) continue;
            if (Num(row.GetValueOrDefault("Protein QValue")) is not { } q || q > 0.01) continue;
            // A not-quantified cell is BLANK at MM 1.1.9+, not 0: it reads as absent and contributes nothing.
            foreach (var (c, f) in inten)
            {
                if (Num(row.GetValueOrDefault(c)) is not { } v || v <= 0) continue;
                totalInt[f] = totalInt.GetValueOrDefault(f) + v;
                if (td == "C") contamInt[f] = contamInt.GetValueOrDefault(f) + v;
            }
            var hits = counts.Where(x => (Num(row.GetValueOrDefault(x.c)) ?? 0) > 0).Select(x => x.Item2).ToList();
            if (hits.Count == 0) continue;
            quantified++;
            runsPer[hits.Count] = runsPer.GetValueOrDefault(hits.Count) + 1;
            foreach (string f in hits) present[f] = present.GetValueOrDefault(f) + 1;
        }
        var contam = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (f, t) in totalInt)
            if (t > 0) contam[f] = SearchMetrics.PyRound(contamInt.GetValueOrDefault(f) / t, 4);
        var rp = new JsonObject();
        foreach (var (k, v) in runsPer) rp[k.ToString(CultureInfo.InvariantCulture)] = v;
        return new ProteinGroupPass(quantified, present, rp, contam);
    }

    // ------------------------------------------------------------------ the payload

    /// <summary>
    /// Builds the payload for one dataset. <paramref name="searchDir"/> holds <c>mm/Task*SearchTask</c>;
    /// <paramref name="qcDir"/> holds <c>qc_report.json</c> (its <c>run_minutes</c>).
    /// </summary>
    public static QcPayloadResult Build(string searchDir, string qcDir, string accession, string runLabel,
        string? metamorpheusVersion, QcBins bins, IEnumerable<string>? extraNotes = null)
    {
        if (string.IsNullOrEmpty(accession)) throw new ArgumentException("an accession is required: an unnamed payload is useless once two datasets exist");
        string mm = Path.Combine(searchDir, "mm");
        string task = (Directory.Exists(mm) ? Directory.EnumerateDirectories(mm, "Task*SearchTask").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault() : null)
                      ?? throw new InvalidDataException($"no Task*SearchTask under {mm}: the search has not run");
        string qcReport = Path.Combine(qcDir, "qc_report.json");
        if (!File.Exists(qcReport)) throw new InvalidDataException($"no QC report at {qcReport}: it carries run_minutes");
        var report = JsonNode.Parse(File.ReadAllText(qcReport, Encoding.UTF8))!.AsObject();
        var runMinutes = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var (k, v) in report)
            runMinutes[Stem(k)] = v?["run_minutes"] is JsonValue jv && jv.TryGetValue<double>(out double rm) ? rm : null;

        string results = Path.Combine(task, "results.txt");
        var counts = ParseResultsTxt(results);
        var datasetCounts = ParseDatasetCounts(results);
        var calib = ParseCalibration(Path.Combine(mm, "Task1CalibrationTask"));
        var (psmMetrics, dists) = ParsePsms(Path.Combine(task, "AllPSMs.psmtsv"), runMinutes, bins);
        var peaks = ParsePeaks(Path.Combine(task, "AllQuantifiedPeaks.tsv"));
        var pg = ParseProteinGroups(Path.Combine(task, "AllQuantifiedProteinGroups.tsv"));

        var names = new SortedSet<string>(counts.Keys, StringComparer.Ordinal);
        names.UnionWith(psmMetrics.Keys);
        names.UnionWith(runMinutes.Keys);
        var files = new JsonArray();
        foreach (string name in names)
        {
            var m = new JsonObject();
            void Merge(JsonObject? src)
            {
                if (src is null) return;
                foreach (var (k, v) in src) m[k] = v?.DeepClone();
            }
            Merge(counts.GetValueOrDefault(name));
            Merge(calib.GetValueOrDefault(name) ?? new JsonObject { ["calibration_ok"] = false });
            Merge(psmMetrics.GetValueOrDefault(name));
            Merge(peaks.GetValueOrDefault(name));
            // Pass two: per-file metrics that needed the dataset first. Omitted, never zeroed, when not measurable.
            if (pg.Quantified is int qn and > 0)
                m["pg_missing_frac_msms"] = SearchMetrics.PyRound((double)(qn - pg.Present.GetValueOrDefault(name)) / qn, 4);
            if (pg.ContaminantIntensity.TryGetValue(name, out double ci)) m["contaminant_intensity_frac"] = ci;
            files.Add(new JsonObject { ["file"] = name, ["metrics"] = m, ["distributions"] = dists.GetValueOrDefault(name)?.DeepClone() ?? new JsonObject() });
        }

        var notes = new JsonArray
        {
            "Two PSM populations appear per file, on purpose (qc 010 QC-Q12). The psms / peptides / "
            + "protein_groups counts are MetaMorpheus's per-file lines, each from an FDR recomputed on that "
            + "file alone (the -RUN definitions). The PSM-derived metrics and distributions describe the "
            + "WHOLE-SEARCH 1% set restricted to the file: QValue <= 0.01 and QValue Notch <= 0.01 at "
            + "whole-search q, unambiguous notch (aging:DEF-PSM-1PCT-INFILE v1). Neither is a sum of the other.",
        };
        notes.Add(pg.Quantified is > 0
            ? "pg_missing_frac_msms is DEF-QC-13's _msms variant (SpectralCount_ > 0). The _any variant "
              + "(pg_missing_frac) is not computed and is absent: MBR is on in every run, so an "
              + "intensity-based completeness for one file would depend on the other files in the run. "
              + "See mbr_kept for the transfer contribution."
            : "pg_missing_frac_msms is NOT REPORTED for this run: the protein-group table carries no "
              + "SpectralCount_ columns, so the _msms variant is not measurable. Absent means not "
              + "measured, not zero.");
        if (!bins.FromQc)
            notes.Add("Histogram bins came from a VENDORED copy of qc's canonical edges, because qctemplates "
                      + "is not installed here. The payload is valid and renders, but its figures may not line "
                      + "up bin-for-bin with reports built where qctemplates is installed.");
        foreach (string n in extraNotes ?? Array.Empty<string>()) notes.Add(n);

        var dm = datasetCounts.DeepClone().AsObject();
        dm["protein_groups_quantified"] = pg.Quantified;
        dm["runs_per_protein_group"] = pg.RunsPer.DeepClone();
        var payload = new JsonObject
        {
            ["schema"] = Schema,
            ["dataset"] = new JsonObject
            {
                ["accession"] = accession,
                ["run_label"] = runLabel,
                ["search_engine"] = new JsonObject { ["name"] = "MetaMorpheus", ["version"] = metamorpheusVersion },
                ["notes"] = notes,
            },
            ["files"] = files,
            ["definitions"] = Definitions(),
            ["dataset_metrics"] = dm,
        };
        return new QcPayloadResult(payload, files.Count, pg.Quantified);
    }
}

/// <summary>qc's own tool, run as a process: <c>&lt;python&gt; -m qctemplates validate|render</c>. qc owns it (it ships a pyproject.toml, qc 007).</summary>
public static class QcTemplates
{
    public static (int Rc, string Output) Run(string python, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {python}");
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            p.Kill(entireProcessTree: true);
            return (124, $"TIMEOUT after {timeout.TotalSeconds:0} s");
        }
        p.WaitForExit();
        string all = (so.Result + se.Result).Trim();
        return (p.ExitCode, all.Length > 2000 ? all[^2000..] : all);
    }
}
