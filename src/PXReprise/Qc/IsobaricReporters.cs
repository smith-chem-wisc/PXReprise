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

    /// <summary>The kits a family's reporters can come from, smallest first: each kit's channels contain the one before's.</summary>
    private static readonly IReadOnlyDictionary<string, IsobaricMassTagType[]> FamilyKits = new Dictionary<string, IsobaricMassTagType[]>
    {
        ["TMT"] = new[] { IsobaricMassTagType.TMT6, IsobaricMassTagType.TMT10, IsobaricMassTagType.TMT11, IsobaricMassTagType.TMT16, IsobaricMassTagType.TMT18 },
        ["iTRAQ"] = new[] { IsobaricMassTagType.iTRAQ4, IsobaricMassTagType.iTRAQ8 },
        ["DiLeu"] = new[] { IsobaricMassTagType.diLeu4, IsobaricMassTagType.diLeu12 },
    };

    /// <summary>
    /// The modification MetaMorpheus searches a kit with, as its <c>MultiplexModId</c> and as "&lt;id&gt; on K" / "on X"
    /// fixed mods. mzLib's IsobaricMassTag.SourceModificationId (private in 1.0.596), copied: TMTpro 16 is searched under
    /// TMT18's label, whose first sixteen channels it is.
    /// </summary>
    public static string ModificationId(IsobaricMassTagType kit) => kit switch
    {
        IsobaricMassTagType.TMT6 => "TMT6-plex",
        IsobaricMassTagType.TMT10 => "TMT10",
        IsobaricMassTagType.TMT11 => "TMT11",
        IsobaricMassTagType.TMT16 or IsobaricMassTagType.TMT18 => "TMT18",
        IsobaricMassTagType.iTRAQ4 => "iTRAQ-4plex",
        IsobaricMassTagType.iTRAQ8 => "iTRAQ-8plex",
        IsobaricMassTagType.diLeu4 => "DiLeu-4plex",
        IsobaricMassTagType.diLeu12 => "DiLeu-12plex",
        _ => throw new ArgumentOutOfRangeException(nameof(kit), kit, null),
    };

    /// <summary>A channel counts as seen when it is present in at least this share of the typical channel's spectra.</summary>
    public const double MinChannelShare = 0.5;

    /// <summary>
    /// v0.4.41: a channel whose median height is below this share of the channel one carbon-13 below it is that channel's
    /// isotope, not a channel. Every reporter's m/z is the channel two below it plus 1.00335, so a strong channel's isotope lands
    /// exactly on the next kit's channel, and the brighter the file the more spectra show it. Presence alone cannot tell them
    /// apart. Measured 2026-11-06 (results/mm-1.1.14-trial_2026-11-06/KitShares.cs): isotopes 0.03 to 0.08, real channels 0.37 to
    /// 1.39, on PXD034564, PXD043296 (TMTpro), PXD051031 and PXD027548.
    /// </summary>
    public const double MaxIsotopeHeight = 0.15;

    /// <summary>
    /// v0.4.34 (TMT trial, results/tmt-trial_2026-10-29): the KIT, not just the family, for the search's
    /// <c>MultiplexModId</c>. A channel is SEEN when it is present (mzLib's own reporter lookup, 3 mDa) in at least
    /// <see cref="MinChannelShare"/> of the spectra the typical channel is present in. Impurity and isotope peaks put a few
    /// hits on a missing channel; the share rule leaves them out. PXD034564's MS3: TMT10's channels in 2,500-5,000 of 3,000
    /// spectra each, 131C (TMT11's extra) in a fifth as many: TMT10.
    /// v0.4.36: the kit is the SMALLEST of <paramref name="family"/> holding every channel seen. v0.4.34 wanted every
    /// channel of the kit seen, so a deposit that left one channel unused had no kit (PXD027548: 130C in 42% of 1.raw's
    /// reporter spectra, 131N in 34% of Z9423TPSTB2_1.raw's; TMT6 holds both) and failed QC as no_isobaric_reporters.
    /// v0.4.41: a channel that is only its carbon-13 parent's isotope (<see cref="MaxIsotopeHeight"/>) is not seen. Under v0.4.36's
    /// rule 131N's isotope on 132N, in 42-60% of PXD034564's spectra around a threshold of 47%, made 9 of its 32 TMT10 files read
    /// as TMTpro, and the search refused the deposit for mixing kits.
    /// <c>Seen</c> is those channels, in reporter m/z order and in the largest kit's labels: what a default design calls a sample
    /// (<see cref="DepositChannels"/>).
    /// Null when the family is unknown or no channel is seen.
    /// </summary>
    public static (IsobaricMassTagType Kit, IReadOnlyDictionary<string, double> ChannelShares, IReadOnlyList<string> Seen)? Kit(IEnumerable<MzSpectrum> spectra, string family)
    {
        if (!FamilyKits.TryGetValue(family, out var kits)) return null;
        var largest = kits[^1];
        if (!IsobaricMassTag.TryGetIsobaricMassTag(largest, out var tag) || tag is null) return null;
        // Presence of every channel of the family's largest kit, over spectra carrying at least MinChannels of them.
        var present = new int[tag.ReporterIonMzs.Length];
        // Each channel's carbon-13 parent, and its height against that parent in the spectra holding both.
        var mzs = tag.ReporterIonMzs;
        var parent = mzs.Select(mz => Array.FindIndex(mzs, p => Math.Abs(p + Constants.C13MinusC12 - mz) <= ToleranceDa)).ToArray();
        var heights = mzs.Select(_ => new List<double>()).ToArray();
        int counted = 0;
        foreach (var sp in spectra)
        {
            var inten = tag.GetReporterIonIntensities(sp);
            if (inten is null || inten.Count(x => x > 0) < MinChannels) continue;
            counted++;
            for (int i = 0; i < inten.Length; i++)
            {
                if (inten[i] <= 0) continue;
                present[i]++;
                if (parent[i] >= 0 && inten[parent[i]] > 0) heights[i].Add(inten[i] / inten[parent[i]]);
            }
        }
        if (counted == 0) return null;
        var shares = tag.ChannelLabels.Select((label, i) => (label, share: (double)present[i] / counted)).ToDictionary(x => x.label, x => x.share);
        // The typical channel: the median of the channels present at all (the largest kit's unused ones are zeros).
        double typical = Median(shares.Values.Where(s => s > 0));
        var seen = shares.Where(kv => kv.Value >= MinChannelShare * typical).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var labels = tag.ChannelLabels.ToArray();
        var seenBeforeIsotopes = seen.ToHashSet(StringComparer.Ordinal);
        seen.RemoveWhere(label =>
        {
            int i = Array.IndexOf(labels, label);
            return parent[i] >= 0 && seenBeforeIsotopes.Contains(labels[parent[i]]) && heights[i].Count > 0 && Median(heights[i]) < MaxIsotopeHeight;
        });
        foreach (var kit in kits)
        {
            // A kit's labels are a subset of the largest kit's, except TMT6's plain "127".."131", which are 127N, 128C,
            // 129N, 130C and 131N by mass.
            var mapped = IsobaricMassTag.GetReporterIonLabels(kit).Select(l => shares.ContainsKey(l) ? l : Tmt6Alias.GetValueOrDefault(l, l))
                .ToHashSet(StringComparer.Ordinal);
            if (seen.IsSubsetOf(mapped)) return (kit, shares, labels.Where(seen.Contains).ToList());
        }
        return null;
    }

    /// <summary>
    /// v0.4.41: the channels of a deposit that hold a sample, from each file's <c>isobaric_channels</c>: those seen in ANY of its
    /// files, because a real channel weak in one file still holds its sample in the others. Null when a file has no list (a QC
    /// report from before v0.4.41) or there are no files: then nothing is left out.
    /// </summary>
    public static IReadOnlyCollection<string>? DepositChannels(IEnumerable<IReadOnlyList<string>?> fileChannels)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        bool any = false;
        foreach (var file in fileChannels)
        {
            if (file is null) return null;
            channels.UnionWith(file);
            any = true;
        }
        return any ? channels : null;
    }

    /// <summary>A kit's channel in the largest kit's labels, which <see cref="Kit"/> reports: TMT6's plain 127 is 127N by mass.</summary>
    public static string LargestKitLabel(string channel) => Tmt6Alias.GetValueOrDefault(channel, channel);

    /// <summary>TMT6, TMT10 and TMT11 are one reagent (229.163 Da), smallest first: each kit's channels contain the one before's.</summary>
    private static readonly string[] OneReagentTmt = { "TMT6-plex", "TMT10", "TMT11" };

    /// <summary>
    /// v0.4.37: the one kit a deposit is searched with, from the kit QC read in each of its files. A channel weak in one
    /// file is not seen there, so files of one TMT10/TMT11 experiment can read different kits (v0.4.35 refused the search).
    /// Kits of one reagent give the largest of them, which holds every file's channels. Any other mix, or a file with no kit,
    /// gives null: the search refuses it. PXD051031, which prompted this, turned out to be an isotope (v0.4.41,
    /// <see cref="MaxIsotopeHeight"/>): it is TMT10 in every file now, and this rule stays for a real channel weak in one file.
    /// </summary>
    public static string? DepositKit(IEnumerable<string?> fileKits)
    {
        var kits = fileKits.Distinct(StringComparer.Ordinal).ToList();
        if (kits.Count == 1) return kits[0];
        if (kits.Count == 0 || kits.Any(k => k is null || Array.IndexOf(OneReagentTmt, k) < 0)) return null;
        return kits.MaxBy(k => Array.IndexOf(OneReagentTmt, k));
    }

    /// <summary>
    /// v0.4.37 (dataRepo 076/078): the manifest's <c>labelling</c> and <c>labelling_plex</c> for a kit's modification id
    /// (<see cref="ModificationId"/>): the family and the number of channels the kit has, used or not. TMT18 is 18
    /// (a TMTpro 16 deposit is searched under TMT18's label). Null for an id no kit has.
    /// </summary>
    public static (string Labelling, int Plex)? Labelling(string modificationId)
    {
        foreach (var kit in FamilyKits.Values.SelectMany(k => k).Reverse())
            if (ModificationId(kit) == modificationId)
                return (Family(modificationId), IsobaricMassTag.GetReporterIonLabels(kit).Count);
        return null;
    }

    private static readonly IReadOnlyDictionary<string, string> Tmt6Alias = new Dictionary<string, string>
    {
        ["127"] = "127N", ["128"] = "128C", ["129"] = "129N", ["130"] = "130C", ["131"] = "131N",
    };

    private static double Median(IEnumerable<double> xs)
    {
        var s = xs.Order().ToList();
        return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
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
