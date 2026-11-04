using Omics.Modifications;

namespace PXReprise.Search;

/// <summary>
/// v0.4.38: which PTM class a spectral-library spectrum carries, and which classes a deposit enriches, so a search
/// registers an enriched-for class into a library of its own (<c>mouse_phospho</c>) instead of the organism's regular
/// one. The user, 2026-11-03: "PTMs in the sample should closely mimic ptms in the database"; these libraries are meant
/// for label-free library-based DIA, where every library entry is a candidate. The rule is per class: "if we enrich for
/// ubiquitin, and we detect a phospho, i think it is okay to keep the phospho in the regular database and library (just
/// not the GG in the regular library). that might need to be in a gg library." Measured before building
/// (results/enrichment-split_2026-11-04): 71-79% of the regular libraries' phospho spectra were seen only in
/// phospho-enriched deposits.
///
/// A class is judged by chemistry, not by name: the modification's monoisotopic mass, its residue and an "Anywhere."
/// position, read from mzLib's <see cref="Modification"/>. So UniProt's "Phosphoserine on S" and GPTMD's
/// "Phosphorylation on S" are one class, and N-terminal acetylation is not acetyl-K. mzLib has no such classifier, and
/// its Unimod cross-references are incomplete on 1.0.596 (UniProt's N6-succinyllysine names none), so mass decides
/// (oracle, 2026-11-04, smith/master 4a183123a).
/// </summary>
public static class LibraryRouting
{
    /// <summary>One routed class: its key (the chain suffix), the modification's monoisotopic mass and residues, and the
    /// enrichment names (the screen's, the probe's, the manifest's) that mean a deposit enriches it.</summary>
    public sealed record PtmClass(string Key, double Mass, string Residues, IReadOnlyList<string> Enrichments);

    /// <summary>The classes, in the order a spectrum carrying two of them is routed (the first one its deposit enriches).</summary>
    public static readonly IReadOnlyList<PtmClass> Classes = new PtmClass[]
    {
        new("phospho", 79.966331, "STY", new[] { "phospho" }),
        new("acetylK", 42.010565, "K", new[] { "acetyl" }),
        new("succinylK", 100.016044, "K", new[] { "succinyl" }),
        new("GG", 114.042927, "K", new[] { "ubiquitin_GG", "GG" }),
    };

    /// <summary>Da. Narrow enough to keep sulfation (79.9568) out of phospho (79.9663), and trimethyl (42.0470) out of acetyl.</summary>
    public const double MassTolerance = 0.0005;

    /// <summary>The class key of a modification, or null for one in no class.</summary>
    public static string? ClassOf(Modification modification)
    {
        if (modification.MonoisotopicMass is not { } mass || modification.Target is null) return null;
        if (!string.Equals(modification.LocationRestriction, "Anywhere.", StringComparison.Ordinal)) return null;
        string residue = modification.Target.ToString();
        if (residue.Length != 1) return null;
        foreach (var c in Classes)
            if (Math.Abs(mass - c.Mass) <= MassTolerance && c.Residues.Contains(residue[0], StringComparison.Ordinal))
                return c.Key;
        return null;
    }

    /// <summary>The class keys a deposit's enrichment names enrich, in <see cref="Classes"/> order. A protein pulldown
    /// (affinity purification, immunoprecipitation, proximity labelling, a chemical probe) enriches no PTM class.</summary>
    public static IReadOnlyList<string> ClassesEnrichedBy(IEnumerable<string> enrichments)
    {
        var names = enrichments.SelectMany(e => e.Split(new[] { '+', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Classes.Where(c => c.Enrichments.Any(names.Contains)).Select(c => c.Key).ToList();
    }

    /// <summary>The library chain a class's spectra go to: the regular chain's key plus the class (<c>mouse_phospho</c>,
    /// <c>human_tmt11_phospho</c>).</summary>
    public static string ChainOf(string regularChain, string classKey) => regularChain + "_" + classKey.ToLowerInvariant();

    /// <summary>The modification names ("Type:IdWithMotif") in a MetaMorpheus full sequence. Brackets nest
    /// ("[Metal:Fe[III] on D]").</summary>
    public static IEnumerable<string> ModificationNames(string fullSequence)
    {
        for (int i = 0; i < fullSequence.Length; i++)
        {
            if (fullSequence[i] != '[') continue;
            int depth = 0, j = i;
            for (; j < fullSequence.Length; j++)
            {
                if (fullSequence[j] == '[') depth++;
                else if (fullSequence[j] == ']' && --depth == 0) break;
            }
            if (j >= fullSequence.Length) yield break;
            yield return fullSequence[(i + 1)..j];
            i = j;
        }
    }
}
