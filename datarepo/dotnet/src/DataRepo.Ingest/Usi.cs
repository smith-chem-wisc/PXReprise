using System.Text.RegularExpressions;

namespace DataRepo.Ingest;

/// <summary>Mints Universal Spectrum Identifiers, and maps a search's run names onto deposited files.</summary>
/// <remarks>
/// Every PSM carries a USI, which lets a person or an agent pull up the actual spectrum behind a claim
/// from PRIDE's PROXI service. The trap is the run name: a USI must name the file as deposited, but the
/// search ran on calibrated copies (<c>X-calib</c>), so names are resolved against the fetch manifest's
/// own file list, and a run that cannot be matched gets no USI rather than one that fails to resolve.
/// Ported from <c>usi.py</c>.
/// </remarks>
public static class Usi
{
    /// <summary>Suffixes the pipeline's calibration stage appends to a run's base name.</summary>
    public static readonly IReadOnlyList<string> CalibrationSuffixes = ["-calib", "-averaged", "-calibrated"];

    /// <summary>mzLib's <c>SpectrumMatchFromTsvHeader.AcceptedSpectraFormats</c>, in its order (mzLib 33f9e618).</summary>
    public static readonly IReadOnlyList<string> MzLibTsvSpectraFormats = [".raw", ".mzML", ".mgf", ".d", "ms1.msalign", "ms2.msalign"];

    /// <summary>The run name mzLib's PSM reader reports for a file named <paramref name="name"/>.</summary>
    /// <remarks><c>SpectrumMatchFromTsv</c> deletes every occurrence of each known spectra extension
    /// ANYWHERE in the <c>File Name</c> cell, case-insensitively, so PRIDE's <c>X.raw.thermo.raw</c>, searched
    /// as <c>X.raw.thermo</c>, reads back as <c>X.thermo</c> (aging 081, REQ-DATAREPO-7). Mirrored exactly.
    /// Reporting it upstream is G80.</remarks>
    public static string MzLibTsvFileName(string name)
    {
        foreach (var ext in MzLibTsvSpectraFormats)
        {
            name = Regex.Replace(name, Regex.Escape(ext), "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            name = Regex.Split(name, @"[\\/]")[^1];
        }
        return name;
    }

    /// <summary>Removes the calibration suffixes MetaMorpheus adds to a run name.</summary>
    public static string StripPipelineSuffix(string name)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in CalibrationSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    name = name[..^suffix.Length];
                    changed = true;
                }
            }
        }
        return name;
    }

    /// <summary>A USI for one PSM: <c>mzspec:&lt;dataset&gt;:&lt;run&gt;:scan:&lt;scan&gt;:&lt;proforma&gt;/&lt;charge&gt;</c>.</summary>
    public static string Mint(string datasetId, string runName, long scan, string proforma, long charge) =>
        $"mzspec:{datasetId}:{runName}:scan:{scan}:{proforma}/{charge}";
}

/// <summary>Maps the names a search reports onto the run ids of deposited files.</summary>
/// <param name="deposited">Run base names as the archive holds them, e.g. <c>QE-002123_GM7_b</c>.</param>
public sealed class RunNameMap(IReadOnlyList<string> deposited)
{
    private readonly Dictionary<string, string> _index = Build(deposited, n => n);

    // The same names as mzLib's PSM reader would report them. Two deposited names that the reader
    // collapses to one are left out: a match there would be a guess.
    private readonly Dictionary<string, string> _mangled = deposited
        .GroupBy(n => Fold(Usi.MzLibTsvFileName(n)))
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyList<string> Deposited { get; } = deposited;

    /// <summary>Reported names that resolved to no deposited run, with how often each was seen.</summary>
    public Dictionary<string, long> Unmatched { get; } = new(StringComparer.Ordinal);

    private static Dictionary<string, string> Build(IEnumerable<string> names, Func<string, string> key)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names) index[Fold(key(name))] = name;  // later wins, as a Python dict comprehension
        return index;
    }

    /// <summary>Python's <c>str.casefold</c> for the names a run can have.</summary>
    private static string Fold(string s) => s.ToLowerInvariant().Replace("ß", "ss");

    /// <summary>The deposited run name for a name a search reported, or null (and counted as unmatched).</summary>
    public string? Resolve(string reported)
    {
        string[] candidates = [reported, Usi.StripPipelineSuffix(reported)];
        foreach (var index in new[] { _index, _mangled })
            foreach (var candidate in candidates)
                if (index.TryGetValue(Fold(candidate), out var hit))
                    return hit;
        Unmatched[reported] = Unmatched.GetValueOrDefault(reported) + 1;
        return null;
    }
}
