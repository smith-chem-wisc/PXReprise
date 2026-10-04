using System.Text.RegularExpressions;

namespace PXReprise.Batch;

/// <summary>
/// G16 (aging 024): a deposit whose raw file names say it holds different kinds of sample. PXD077298 is 8
/// <c>Lysate_*</c>, 8 <c>IP_*</c> and 6 <c>EV_*</c> runs under one dataset-level enrichment, so a catalog reader could
/// pool lysates with pulldowns. This only raises the question: which run is which enrichment is curation
/// (<c>run_enrichment</c>, as PXD058611 and PXD077298 have it), because a file name is a hint, not a protocol.
/// Deliberately narrow: every file must carry exactly one sample-type word as a whole token, and at least two types must
/// appear. Anything less (numbered files, replicate suffixes, lab codes) says nothing.
/// </summary>
public static class MixedSamples
{
    public sealed record Group(string Kind, string Token, int Files);

    /// <summary>Whole tokens (split on <c>_ - . space</c>, case-insensitive) and the kind of sample each names.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lysate"] = "whole", ["wcl"] = "whole", ["input"] = "whole", ["global"] = "whole", ["proteome"] = "whole",
        ["ip"] = "pulldown", ["coip"] = "pulldown", ["pulldown"] = "pulldown", ["apms"] = "pulldown", ["bioid"] = "pulldown",
        ["turboid"] = "pulldown", ["streptavidin"] = "pulldown",
        ["ev"] = "vesicle", ["evs"] = "vesicle", ["exosome"] = "vesicle", ["exosomes"] = "vesicle",
        ["secretome"] = "secreted", ["conditioned"] = "secreted",
        ["phospho"] = "phospho", ["tio2"] = "phospho", ["imac"] = "phospho",
    };

    /// <summary>The groups, largest first, when the names show two or more kinds of sample; otherwise null.</summary>
    public static IReadOnlyList<Group>? Detect(IEnumerable<string> fileNames)
    {
        var tagged = new List<(string Kind, string Token)>();
        foreach (string f in fileNames)
        {
            var hits = Regex.Split(Path.GetFileNameWithoutExtension(f), @"[_\-. ]+")
                .Where(Kinds.ContainsKey).Select(t => (Kind: Kinds[t], Token: t)).DistinctBy(h => h.Kind).ToList();
            if (hits.Count != 1) return null;
            tagged.Add(hits[0]);
        }
        var groups = tagged.GroupBy(t => t.Kind)
            .Select(g => new Group(g.Key, g.GroupBy(t => t.Token).OrderByDescending(x => x.Count()).First().Key, g.Count()))
            .OrderByDescending(g => g.Files).ThenBy(g => g.Kind, StringComparer.Ordinal).ToList();
        return groups.Count >= 2 ? groups : null;
    }

    public static string Describe(IReadOnlyList<Group> groups) =>
        string.Join(", ", groups.Select(g => $"{g.Files} '{g.Token}' ({g.Kind})"));
}
