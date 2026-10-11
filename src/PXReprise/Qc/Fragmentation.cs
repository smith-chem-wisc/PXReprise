namespace PXReprise.Qc;

/// <summary>
/// One file's fragmentation as MetaMorpheus should be told it (D32, G24): <see cref="DissociationType"/> goes into the
/// file's <c>&lt;raw&gt;.toml</c>, or <see cref="Unsupported"/> says why the file cannot be searched per file.
/// </summary>
public sealed record FragmentationCall(string? DissociationType, bool LowRes, string? Unsupported, string Evidence);

/// <summary>
/// Decides a file's fragmentation from its MS2 scan headers (QC's analyzer/dissociation counts), never from what the
/// depositor wrote (D34). The oracle of 2026-10-07 (G24) found:
/// <list type="bullet">
/// <item>mzLib reports ion-trap CID as CID, never LowCID, so the analyzer decides: CID read in an ion trap is LowCID;</item>
/// <item>MetaMorpheus 1.1.12 resolves Autodetect per scan only for CID, ECD, ETD, HCD and EThcD, silently skips any other
/// type, and treats every scan as high resolution, so Autodetect is used only when every type is one of those and every
/// scan is read at high resolution;</item>
/// <item>HCD read in an ion trap is searched as HCD at the low-res tolerance, only when the profile turns
/// <c>ion_trap_hcd</c> on (PXR-A68); otherwise it is refused, as before;</item>
/// <item>an unknown or unsupported type, ion-trap and high-resolution MS2 mixed in one file, or ion-trap CID and
/// ion-trap HCD mixed in one file, is refused: a file gets one DissociationType.</item>
/// </list>
/// Pairs under <see cref="MinorShare"/> of a file's MS2 scans are ignored and named in the evidence.
/// </summary>
public static class Fragmentation
{
    public const double MinorShare = 0.01;

    private static readonly HashSet<string> HighResolution = new(StringComparer.Ordinal) { "Orbitrap", "FTICR" };
    private static readonly HashSet<string> IonTrap = new(StringComparer.Ordinal) { "IonTrap2D", "IonTrap3D" };

    /// <summary>The types MetaMorpheus 1.1.12 resolves under Autodetect (GlobalVariables.AllSupportedDissociationTypes).</summary>
    public static readonly IReadOnlySet<string> AutodetectTypes = new HashSet<string>(StringComparer.Ordinal) { "CID", "ECD", "ETD", "HCD", "EThcD" };

    /// <summary>The internal name of HCD read in an ion trap, kept apart from Orbitrap HCD so the two never merge.</summary>
    private const string IonTrapHcdType = "HCD (ion trap)";

    /// <param name="ms2AnalyzerDissociation">Counts keyed "<c>analyzer/dissociation</c>", as SpectraQc writes them.</param>
    /// <param name="ionTrapHcd">The profile's <c>ion_trap_hcd</c>: search HCD read in an ion trap as low-res HCD.</param>
    public static FragmentationCall Decide(IReadOnlyDictionary<string, int> ms2AnalyzerDissociation, bool ionTrapHcd = false)
    {
        int total = ms2AnalyzerDissociation.Values.Sum();
        if (total == 0) return new(null, false, "no MS2 scans", "");
        var kept = ms2AnalyzerDissociation.Where(kv => (double)kv.Value / total >= MinorShare).ToList();
        string evidence = string.Join("; ", kept.Select(kv => $"{kv.Key} {kv.Value} of {total}"));
        var minor = ms2AnalyzerDissociation.Where(kv => (double)kv.Value / total < MinorShare).ToList();
        if (minor.Count > 0)
            evidence += $"; ignored, each under {MinorShare:P0}: {string.Join(", ", minor.Select(kv => $"{kv.Key} {kv.Value}"))}";

        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (pair, n) in kept)
        {
            string[] parts = pair.Split('/', 2);
            string analyzer = parts[0], dissociation = parts.Length > 1 ? parts[1] : "None";
            string? type = HighResolution.Contains(analyzer) && AutodetectTypes.Contains(dissociation) ? dissociation
                : IonTrap.Contains(analyzer) && dissociation == "CID" ? "LowCID"
                : ionTrapHcd && IonTrap.Contains(analyzer) && dissociation == "HCD" ? IonTrapHcdType
                : null;
            if (type is null)
                return new(null, false, $"{pair} in {n} of {total} MS2 scans is not a fragmentation MetaMorpheus can be told per file", evidence);
            types.Add(type);
        }
        if (types.Count == 1)
        {
            string only = types.Min!;
            return only == IonTrapHcdType ? new("HCD", true, null, evidence) : new(only, only == "LowCID", null, evidence);
        }
        if (types.All(t => t is "LowCID" or IonTrapHcdType))
            return new(null, false, "ion-trap CID and ion-trap HCD are mixed in one file", evidence);
        if (types.Contains("LowCID") || types.Contains(IonTrapHcdType))
            return new(null, false, "ion-trap and high-resolution MS2 are mixed in one file", evidence);
        return new("Autodetect", false, null, evidence);
    }
}
