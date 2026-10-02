using Chemistry;
using MassSpectrometry;
using Omics.Modifications;

namespace PXReprise.Qc;

/// <summary>What a file's spectra say about isobaric labelling: the best-supported tag, or none.</summary>
/// <param name="Tag">The label family (<c>TMT</c>, <c>iTRAQ</c>, <c>DiLeu</c>), or null when no family is supported.</param>
/// <param name="Fraction">Of the MSn spectra checked, the fraction carrying at least <see cref="IsobaricReporters.MinChannels"/> of one of that family's kits' reporters.</param>
public sealed record ReporterEvidence(string? Tag, double Fraction, int SpectraChecked);

/// <summary>
/// Finds isobaric reporter ions in a file's own spectra, because a deposit's text can fail to say it is labelled:
/// PXD052189 never mentions TMT and a TMT six-plex was searched as label-free (aging S64, G4). The reporter m/z values
/// are mzLib's (<see cref="Mods.IsobaricLabelModifications"/>, the HCD diagnostic ions of each kit, as MetaMorpheus's
/// IsobaricMassTag reads them), matched within MetaMorpheus's 3 mDa. mzLib #1375 moves IsobaricMassTag into mzLib;
/// when a release MetaMorpheus also pins carries it, the matching here should call it instead.
/// MS3 spectra count too: SPS-MS3 TMT reads its reporters there, not in MS2.
/// </summary>
public static class IsobaricReporters
{
    public const double ToleranceDa = 0.003;
    public const int MinChannels = 3;

    /// <summary>A file is labelled when at least this fraction of its MSn spectra carry a kit's reporters.</summary>
    public const double MinFraction = 0.25;

    /// <summary>Each kit's reporter m/z values, ascending; one entry per kit (its K and N-terminal mods share them).</summary>
    public static readonly IReadOnlyList<(string Tag, double[] Mzs)> Kits = Mods.IsobaricLabelModifications
        .Where(m => m.DiagnosticIons is not null && m.DiagnosticIons.TryGetValue(DissociationType.HCD, out var d) && d.Count >= MinChannels)
        .GroupBy(m => m.OriginalId, StringComparer.Ordinal)
        .Select(g => (g.Key, g.First().DiagnosticIons[DissociationType.HCD].Select(mass => mass.ToMz(1)).Distinct().OrderBy(x => x).ToArray()))
        .OrderBy(k => k.Key, StringComparer.Ordinal)
        .ToList();

    public static ReporterEvidence Detect(IEnumerable<MsDataScan> scans)
    {
        var spectra = scans.Where(s => s.MsnOrder >= 2 && s.MassSpectrum is { Size: > 0 }).Select(s => s.MassSpectrum).ToList();
        if (spectra.Count == 0) return new(null, 0, 0);
        // Kits of one family share reporters (TMT6, 10, 11 and 18 all hold 126.1277), and impurity peaks blur them: a
        // TMT11 file shows 13 of TMT18's 18 channels. So the evidence names the FAMILY, which is all routing needs.
        string? best = null;
        double bestFraction = 0;
        foreach (var family in Kits.GroupBy(k => Family(k.Tag)))
        {
            int hits = spectra.Count(sp => family.Any(k => Channels(sp.XArray, k.Mzs) >= MinChannels));
            double f = (double)hits / spectra.Count;
            if (f > bestFraction) (best, bestFraction) = (family.Key, f);
        }
        return bestFraction >= MinFraction
            ? new(best, Search.SearchMetrics.PyRound(bestFraction, 4), spectra.Count)
            : new(null, Search.SearchMetrics.PyRound(bestFraction, 4), spectra.Count);
    }

    /// <summary>A kit's family from mzLib's id: TMT6-plex, TMT10, TMT11, TMT18 are TMT; iTRAQ-4plex is iTRAQ.</summary>
    public static string Family(string kit) =>
        kit.StartsWith("TMT", StringComparison.Ordinal) ? "TMT"
        : kit.StartsWith("iTRAQ", StringComparison.Ordinal) ? "iTRAQ"
        : kit.StartsWith("DiLeu", StringComparison.Ordinal) ? "DiLeu" : kit;

    /// <summary>How many of <paramref name="reporters"/> have a peak within the tolerance in a sorted m/z array.</summary>
    public static int Channels(double[] mz, double[] reporters)
    {
        int n = 0;
        foreach (double r in reporters)
        {
            int i = Array.BinarySearch(mz, r - ToleranceDa);
            if (i < 0) i = ~i;
            if (i < mz.Length && mz[i] <= r + ToleranceDa) n++;
        }
        return n;
    }
}
