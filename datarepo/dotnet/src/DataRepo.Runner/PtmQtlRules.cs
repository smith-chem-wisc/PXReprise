using System.Text.RegularExpressions;
using DataRepo.Bundle;
using Quantification.PtmQtl;

namespace DataRepo.Runner;

/// <summary>What the operator's lists say of one deposit: its capture class and capture runs (S3, S3b; ptmQtl's
/// capture list v4, <c>results/ptm_enriched_v4</c>) and its cysteine class (S2d).</summary>
/// <param name="CaptureClass"><c>ptm_capture</c>, <c>capture_runs</c>, <c>held</c>, <c>no_ptm_capture</c>, or null
/// when the list does not name the deposit.</param>
/// <param name="CaptureRuns">For <c>capture_runs</c>: the runs it names, by run id.</param>
/// <param name="CysteineClass"><c>nem</c> (S2d (a), aging's S76 deposits), <c>cysteine_poor</c>, or null.</param>
/// <param name="CysteineReason">The list's words for the class, e.g. the reagent, for the reason column.</param>
public sealed record PtmQtlDeposit(string? CaptureClass = null, IReadOnlySet<string>? CaptureRuns = null,
    string? CysteineClass = null, string? CysteineReason = null)
{
    /// <summary>The whole deposit captures a modified form, or is held until aging rules (S3): type P only.</summary>
    public bool Enriched => CaptureClass is "ptm_capture" or "held";
}

/// <summary>ptmQtl's site rules that are not mzLib's: S2c, S2d and S3b's sample types
/// (<c>ptmQtl/design/DEFINITIONS.md</c>, 2026-10-19).</summary>
/// <remarks>
/// <para>Where the definitions are silent, these follow ptmQtl's own implementation of them, read on 2026-10-19:
/// <c>results/g40_blocking_rule_v2/measure.py</c> (S2c), <c>results/g39_cysteine_rule_v2/measure.py</c> (S2d) and
/// <c>tools/export_observations.py</c> (S3, S7). Where the two disagree, the definitions are followed and the
/// difference is asked of ptmQtl (the questions are in <c>design/PTMQTL_ENGINE.md</c>).</para>
/// <para>A site is matched on accession, position and IdWithMotif, never the residue (their 044 item 6).</para>
/// </remarks>
public static class PtmQtlRules
{
    // ---------------------------------------------------------------- S3, S3b: sample type per run

    public const string WholeProteome = "whole_proteome";
    public const string PtmCapture = "ptm_capture";
    public const string ProteinPulldown = "protein_pulldown";
    public const string Unknown = "unknown";

    /// <summary>The <c>Enrichment</c> values that capture a modified form (S3).</summary>
    public static readonly IReadOnlySet<string> PtmEnrichments = new HashSet<string>(StringComparer.Ordinal) { "phospho", "acetyl", "ubiquitin_GG", "succinyl", "glyco" };

    /// <summary>The <c>Enrichment</c> values that capture proteins by something other than a modification (S3b).</summary>
    public static readonly IReadOnlySet<string> PulldownEnrichments = new HashSet<string>(StringComparer.Ordinal)
        { "immunoprecipitation", "proximity_labelling", "affinity_purification", "chemical_probe" };

    /// <summary>A run's sample type (S3b): the capture list first, then the run's own <c>runs.enrichment</c>.</summary>
    /// <remarks>Only <c>[none]</c> is a whole proteome. <c>other</c>, NULL (nobody said) and several values are
    /// unknown, which, like a pulldown, gives no occupancy-based result. A pulldown's bait runs would enter type A and
    /// site-trait fits once the engine reads aging's <c>curated_design_factors</c>; until then none does (S3b: "until a
    /// deposit has that design, no type A rows are written for it").</remarks>
    public static string RunType(string run, IReadOnlyList<string>? enrichment, PtmQtlDeposit deposit)
    {
        if (deposit.Enriched || deposit.CaptureRuns?.Contains(run) == true) return PtmCapture;
        if (enrichment is null || enrichment.Count != 1) return Unknown;
        var label = enrichment[0];
        return label == "none" ? WholeProteome
            : PtmEnrichments.Contains(label) ? PtmCapture
            : PulldownEnrichments.Contains(label) ? ProteinPulldown
            : Unknown;
    }

    // ---------------------------------------------------------------- full sequences

    /// <summary>One modification in a MetaMorpheus full sequence.</summary>
    /// <param name="Name">The <c>Category:IdWithMotif</c> name.</param>
    /// <param name="Position">The residue it sits on, 1-based in the peptide; 0 for one before the first residue.</param>
    /// <param name="Residue">That residue, or null at 0.</param>
    public sealed record Placement(string Name, int Position, char? Residue);

    /// <summary>A full sequence's base sequence and its modifications, scanned by bracket depth
    /// (<see cref="PtmQtlCore.ModificationNames"/>): a name may hold brackets.</summary>
    public static (string Base, List<Placement> Modifications) Parse(string fullSequence)
    {
        var letters = new System.Text.StringBuilder();
        var placements = new List<Placement>();
        int depth = 0, start = -1;
        for (var i = 0; i < fullSequence.Length; i++)
        {
            var ch = fullSequence[i];
            if (ch == '[')
            {
                if (depth++ == 0) start = i + 1;
            }
            else if (ch == ']' && depth > 0)
            {
                if (--depth == 0)
                    placements.Add(new Placement(fullSequence[start..i], letters.Length, letters.Length == 0 ? null : letters[^1]));
            }
            else if (depth == 0 && char.IsLetter(ch)) letters.Append(ch);
        }
        return (letters.ToString(), placements);
    }

    // ---------------------------------------------------------------- S2c: blocking modifications at a C-terminus

    public const string S2c = "S2c";

    public const string S2cReason = "S2c: a trypsin-blocking modification on a peptide's C-terminal residue";

    /// <summary>The blocking modifications, as ptmQtl's <c>measure.py</c> recognises them: a pattern on the IdWithMotif
    /// and the residue it must sit on.</summary>
    /// <remarks>Wider than the definitions' list on one point: <c>utyryl</c> also takes hydroxybutyryl and
    /// 2-hydroxyisobutyryl lysine, which block trypsin as the other acyl groups do. Asked of ptmQtl.</remarks>
    public static readonly IReadOnlyList<(Regex Pattern, char Residue)> Blocking =
    [
        (new Regex("Citrull", RegexOptions.CultureInvariant), 'R'),
        (new Regex("^GG ", RegexOptions.CultureInvariant), 'K'),
        (new Regex("^Acetylation on K|N6-acetyllysine", RegexOptions.CultureInvariant), 'K'),
        (new Regex("uccinyl", RegexOptions.CultureInvariant), 'K'),
        (new Regex("rotonyl|utyryl|alonyl|lutaryl", RegexOptions.CultureInvariant), 'K'),
    ];

    /// <summary>Whether an IdWithMotif on a residue is one of S2c's blocking modifications.</summary>
    public static bool IsBlocking(string idWithMotif, char residue) =>
        Blocking.Any(b => b.Residue == residue && b.Pattern.IsMatch(idWithMotif));

    /// <summary>The first MetaMorpheus release that digests aware of a modification (the mzLib #1119 family), or null
    /// while none does: every release to 1.1.12 lacks it, so S2c applies to every search (ptmQtl CLEAVAGERULES-P2).</summary>
    public static readonly Version? ModificationAwareDigestionFrom = null;

    /// <summary>S2c: every site of a blocking modification that some S7 observation carries on its last residue.</summary>
    /// <remarks>Any such observation leaves the site out, mixed evidence too (the user's choice (a)). A protein's own
    /// C-terminus is not told apart. Observations are read with no run filter: every observation is evidence.</remarks>
    public static List<ExcludedSite> BlockedSites(IEnumerable<PeptidoformObservation> observations)
    {
        var found = new SortedSet<(string, int, char, string)>();
        foreach (var o in observations.Select(o => (o.FullSequence, o.ProteinAccession, o.StartResidue)).Distinct())
        {
            var (bases, mods) = Parse(o.FullSequence);
            foreach (var m in mods)
            {
                if (m.Position != bases.Length || m.Residue is not char residue) continue;
                var idm = PtmQtlCore.IdWithMotif(m.Name);
                if (IsBlocking(idm, residue)) found.Add((o.ProteinAccession, o.StartResidue + m.Position - 1, residue, idm));
            }
        }
        return found.Select(f => new ExcludedSite(f.Item1, f.Item2, f.Item3, f.Item4, S2c, S2cReason)).ToList();
    }

    // ---------------------------------------------------------------- S2d: cysteine labels the search did not declare

    public const string S2dA = "S2d (a)";
    public const string S2dB = "S2d (b)";

    /// <summary>Carbamidomethyl, the cysteine label every search fixed.</summary>
    public const double Carbamidomethyl = 57.021464;

    /// <summary>The cysteine labels S2d names, with their masses (DEFINITIONS; the d5 forms from ptmQtl's
    /// <c>measure.py</c>).</summary>
    public static readonly IReadOnlyList<(string Label, double Mass)> CysteineLabels =
    [
        ("NEM", 125.047679),
        ("NEM, ring opened by water", 143.058244),
        ("d5-NEM", 130.078983),
        ("d5-NEM, ring opened by water", 148.089548),
        ("acrylamide (propionamide)", 71.037114),
        ("carboxymethyl", 58.005479),
    ];

    /// <summary>S2d (b)'s mass match: the label whose mass less carbamidomethyl is within 0.002 Da of the
    /// modification's (strictly), or null.</summary>
    public static string? LabelOf(double modificationMass) =>
        CysteineLabels.Where(l => Math.Abs(l.Mass - Carbamidomethyl - modificationMass) < 0.002).Select(l => l.Label).FirstOrDefault();

    /// <summary>S2d (a), an NEM deposit: every site, of any modification, that a Cys-containing target PSM covers.</summary>
    /// <param name="spans">(accession, first, last residue) of every target PSM that passes S7's tier, at any ambiguity
    /// level, with a C in its sequence; a PSM shared between proteins gives its one start to each accession.</param>
    /// <param name="sites">(accession, position, residue) of the dataset's stored target sites.</param>
    public static List<ExcludedSite> CoveredSites(IEnumerable<(string Protein, int First, int Last)> spans,
        IEnumerable<(string Protein, int Position, char Residue)> sites)
    {
        var byProtein = spans.GroupBy(s => s.Protein, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return sites.Distinct()
            .Where(s => byProtein.TryGetValue(s.Protein, out var list) && list.Any(sp => s.Position >= sp.First && s.Position <= sp.Last))
            .OrderBy(s => s.Protein, StringComparer.Ordinal).ThenBy(s => s.Position)
            .Select(s => new ExcludedSite(s.Protein, s.Position, s.Residue, null, S2dA, NemReason))
            .ToList();
    }

    /// <summary>The reason S2d (a) gives an NEM deposit's sites, as ptmQtl's <c>measure.py</c> writes it.</summary>
    public const string NemReason = "S2d (a): NEM deposit (S76), covered by a Cys-containing peptidoform";

    /// <summary>The reason S2d (a) gives a cysteine-poor deposit's cysteine sites.</summary>
    public static string CysteinePoorReason(string? reagent) => $"S2d (a): cysteine-poor deposit ({reagent ?? "listed"}), cysteine site";

    /// <summary>S2d (a): every cysteine site (a residue site on C). A cysteine-poor deposit loses these only; an NEM
    /// deposit loses them as well as every covered site (aging's S76: "no cysteine site, no Cys-containing peptide";
    /// <c>measure.py</c>'s <c>covered | on_c</c>), so a cysteine site no passing PSM covers is out there too.</summary>
    public static List<ExcludedSite> CysteineSites(IEnumerable<(string Protein, int Position, char Residue, string SiteType)> sites, string reason) =>
        sites.Where(s => s.Residue == 'C' && s.SiteType == "residue").Select(s => (s.Protein, s.Position)).Distinct()
            .OrderBy(s => s.Protein, StringComparer.Ordinal).ThenBy(s => s.Position)
            .Select(s => new ExcludedSite(s.Protein, s.Position, 'C', null, S2dA, reason))
            .ToList();

    /// <summary>One modification's S2d (b) screen in one deposit.</summary>
    public sealed record Screened(string Name, long Peptidoforms, long WithC, long SequencesWithC, double Share, double BaselineShare, bool Flag);

    /// <summary>S2d (b)'s flag: per biological modification not on C, whether its peptidoforms are over-represented in
    /// Cys-containing sequences.</summary>
    /// <param name="peptidoforms">(base sequence, MetaMorpheus name) of the deposit's target peptidoforms with best
    /// q &lt; 0.01, the first name of each, as ptmQtl's screen reads them.</param>
    /// <remarks>At least 10 peptidoforms, at least 30 % with a C, at least 3 times the share among the peptidoforms
    /// with no such modification, and at least 5 distinct Cys-containing sequences. The shares are compared as computed;
    /// ptmQtl's screen rounds them to 3 places first.</remarks>
    public static List<Screened> Screen(IEnumerable<(string Base, string FullSequence)> peptidoforms)
    {
        var rows = peptidoforms.Select(p => (p.Base, HasC: p.Base.Contains('C'),
            Names: Parse(p.FullSequence).Modifications.Select(m => m.Name)
                .Where(n => PtmQtlCore.IsBiological(n) && !n.EndsWith(" on C", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal))).ToList();
        var baseline = rows.Where(r => r.Names.Count == 0).ToList();
        var baselineShare = baseline.Count == 0 ? 0 : (double)baseline.Count(r => r.HasC) / baseline.Count;
        return rows.SelectMany(r => r.Names.Select(n => (Name: n, r.Base, r.HasC)))
            .GroupBy(x => x.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                long total = g.Count(), withC = g.Count(x => x.HasC);
                long sequences = g.Where(x => x.HasC).Select(x => x.Base).Distinct(StringComparer.Ordinal).Count();
                var share = (double)withC / total;
                return new Screened(g.Key, total, withC, sequences, share, baselineShare,
                    total >= 10 && share >= 0.3 && share >= 3 * baselineShare && sequences >= 5);
            }).ToList();
    }

    /// <summary>S2d (b): the stored sites of one matched modification that a Cys-containing target PSM carries it on.</summary>
    /// <param name="carriers">(accession, peptide start, MetaMorpheus full sequence) of target PSMs passing S7's tier at
    /// ambiguity level 1 with a C in their sequence; a shared PSM's one start goes to each accession.</param>
    /// <param name="name">The <c>Category:IdWithMotif</c> name.</param>
    /// <param name="sites">(accession, position, IdWithMotif) of the dataset's stored target sites: a place the PSMs
    /// put the modification on is a site only where <c>ptm_sites</c> holds one, as <c>measure.py</c> reads it.</param>
    /// <remarks>A modification before the first residue is placed on residue 1, as ptmQtl's <c>measure.py</c> does.</remarks>
    public static List<ExcludedSite> CarriedSites(IEnumerable<(string Protein, int Start, string FullSequence)> carriers, string name, string label,
        IReadOnlySet<(string Protein, int Position, string Modification)> sites)
    {
        var idm = PtmQtlCore.IdWithMotif(name);
        var found = new SortedSet<(string, int)>();
        foreach (var c in carriers)
            foreach (var m in Parse(c.FullSequence).Modifications.Where(m => m.Name == name))
            {
                var position = c.Start + Math.Max(m.Position, 1) - 1;
                if (sites.Contains((c.Protein, position, idm))) found.Add((c.Protein, position));
            }
        return found.Select(f => new ExcludedSite(f.Item1, f.Item2, null, idm, S2dB,
            $"S2d (b): {idm} = {label} - carbamidomethyl, carried by a Cys-containing peptidoform")).ToList();
    }
}
