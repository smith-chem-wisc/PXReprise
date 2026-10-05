using MassSpectrometry;
using PXReprise.Config;
using Readers;

namespace PXReprise.Qc;

/// <summary>
/// One raw file's QC verdict. <see cref="FailReasons"/> names each failure separately, because a search may waive
/// <c>low_res_ms2</c> under an explicit acquisition exception but never <c>too_few_ms2</c> or <c>unreadable</c>.
/// </summary>
public sealed record QcFileResult(
    bool Pass,
    IReadOnlyList<string> FailReasons,
    int? Scans,
    int Ms2,
    double? FractionOrbitrapHcd,
    IReadOnlyDictionary<string, int>? Ms2AnalyzerDissociation,
    double? RunMinutes,
    IReadOnlyDictionary<string, int>? ChargeStates,
    string? Error = null,
    ReporterEvidence? Reporters = null,
    RunMetadata? Run = null);

/// <summary>
/// What the raw file says about its own acquisition (DATAREPO-72), recorded while the file still exists: the search's
/// cleanup deletes it, and nothing downstream re-reads spectra. Each value is the reader's, unconverted; null means
/// the reader did not give it. A Thermo RAW start time is the instrument's local clock with no zone (mzLib #1349).
/// </summary>
public sealed record RunMetadata(string? StartTime, string? InstrumentModel, string? InstrumentModelAccession, string? InstrumentSerial);

/// <summary>
/// The pre-search gate: keep only high-resolution HCD with MS2 read in the Orbitrap (aging's v1 rule, now the profile's
/// <c>qc</c> table). The instrument name is not enough: hybrids can read MS2 in the ion trap, so the file is checked
/// (aging S37, PXD060431). Scans are read through mzLib; this reproduces aging's qc_spectra.py field for field.
/// </summary>
public static class SpectraQc
{
    public const string LowResMs2 = "low_res_ms2";
    public const string TooFewMs2 = "too_few_ms2";
    public const string Unreadable = "unreadable";

    /// <summary>The spectra carry isobaric reporter ions, under a profile that does not take isobaric labels (G4).</summary>
    public const string IsobaricLabelled = "isobaric_reporters";

    public static QcFileResult Check(string path, QcGates gates, bool refuseIsobaric = false)
    {
        List<MsDataScan> scans;
        SourceFile? source;
        try
        {
            var file = MsDataFileReader.GetDataFile(path).LoadAllStaticData();
            scans = file.GetAllScansList();
            source = file.SourceFile;
        }
        catch (Exception e)
        {
            // A third kind of failure, alongside low_res_ms2 and too_few_ms2. It is a verdict, not an exception: one
            // corrupt file must not throw away the other files' QC.
            string msg = e.Message.Length > 500 ? e.Message[..500] : e.Message;
            return new QcFileResult(false, new[] { Unreadable }, null, 0, null, null, null, null, msg);
        }
        return Evaluate(scans, gates, refuseIsobaric) with { Run = Metadata(source) };
    }

    /// <summary>The source file's acquisition facts; never empty strings, and never a reason to fail QC.</summary>
    public static RunMetadata? Metadata(SourceFile? source)
    {
        if (source is null) return null;
        static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        // K: nothing for the zone-less value a RAW file holds, Z for a UTC one. Never converted.
        string? start = source.AcquisitionStartTime is { } t
            ? t.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", System.Globalization.CultureInfo.InvariantCulture) : null;
        var m = new RunMetadata(start, Text(source.InstrumentModel?.Name), Text(source.InstrumentModel?.Accession), Text(source.InstrumentSerialNumber));
        return m is { StartTime: null, InstrumentModel: null, InstrumentModelAccession: null, InstrumentSerial: null } ? null : m;
    }

    /// <summary>The verdict from a file's scans; separated from reading so it can be tested without a raw file.</summary>
    public static QcFileResult Evaluate(IReadOnlyList<MsDataScan> scans, QcGates gates, bool refuseIsobaric = false)
    {
        var ms2 = scans.Where(s => s.MsnOrder == 2).ToList();
        var pairs = MostCommon(ms2.Select(s => $"{s.MzAnalyzer}/{(s.DissociationType?.ToString() ?? "None")}"));
        int hi = pairs.GetValueOrDefault("Orbitrap/HCD");
        double frac = ms2.Count > 0 ? (double)hi / ms2.Count : 0.0;
        var reasons = new List<string>();
        if (frac < gates.MinFractionOrbitrapHcd) reasons.Add(LowResMs2);
        if (ms2.Count < gates.MinMs2) reasons.Add(TooFewMs2);
        double runMinutes = scans.Count > 0 ? scans.Max(s => s.RetentionTime) : 0;
        var charges = MostCommon(ms2.Select(s => s.SelectedIonChargeStateGuess?.ToString() ?? "None"), top: 6);
        var reporters = refuseIsobaric ? IsobaricReporters.Detect(scans) : null;
        if (reporters?.Tag is not null) reasons.Add(IsobaricLabelled);
        return new QcFileResult(reasons.Count == 0, reasons, scans.Count, ms2.Count, Search.SearchMetrics.PyRound(frac, 4), pairs,
            Search.SearchMetrics.PyRound(runMinutes, 2), charges, Reporters: reporters);
    }

    /// <summary>Counts by value, most common first; ties keep first-seen order (Python's Counter.most_common).</summary>
    internal static IReadOnlyDictionary<string, int> MostCommon(IEnumerable<string> values, int? top = null)
    {
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (string v in values)
        {
            if (!counts.TryGetValue(v, out int n)) order.Add(v);
            counts[v] = n + 1;
        }
        var ranked = order.Select((k, i) => (k, i)).OrderByDescending(x => counts[x.k]).ThenBy(x => x.i).Select(x => x.k);
        if (top is { } t) ranked = ranked.Take(t);
        var result = new Dictionary<string, int>();   // insertion-ordered: serialises in rank order
        foreach (string k in ranked) result[k] = counts[k];
        return result;
    }
}
