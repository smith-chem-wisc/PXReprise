using System.Text.RegularExpressions;
using DataRepo.Bundle;
using Omics.SequenceConversion;
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

/// <summary>ptmQtl's site rules that are not mzLib's: S2c, S2d, S2e and S3b's sample types
/// (<c>ptmQtl/design/DEFINITIONS.md</c>, 2026-10-20).</summary>
/// <remarks>
/// <para>Where the definitions are silent, these follow ptmQtl's own implementation of them, read on 2026-10-19 and
/// 2026-10-20: <c>results/g40_blocking_rule_v3/measure.py</c> with the runner's <c>blocking</c> mode (S2c),
/// <c>results/g39_cysteine_rule_v2/measure.py</c> (S2d), <c>results/g39_carbamyl_methyl_v1</c> (S2e) and
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

    // ---------------------------------------------------------------- S7 "Name": several names (ptmQtl D57)

    public const string SameByUnimod = "unimod";
    public const string SameByFormula = "formula";

    /// <summary>A MetaMorpheus full sequence in mzLib's converter's Unimod form, or null when it has none.</summary>
    public static string? UnimodForm(string fullSequence) =>
        Omics.SequenceConversion.SequenceConversionService.Default.Convert(fullSequence, "mzLib", "Unimod",
            new Omics.SequenceConversion.ConversionWarnings(), Omics.SequenceConversion.SequenceConversionHandlingMode.ReturnNull);

    /// <summary>A full sequence as its base sequence and each modification's position and chemical formula, resolved
    /// through <c>MzLibModificationLookup</c> and then <c>GlobalModificationLookup</c>; null when any does not resolve.</summary>
    public static string? FormulaForm(string fullSequence)
    {
        if (Omics.SequenceConversion.SequenceConversionService.Default.Parse(fullSequence, "mzLib", new Omics.SequenceConversion.ConversionWarnings(),
                Omics.SequenceConversion.SequenceConversionHandlingMode.ReturnNull) is not Omics.SequenceConversion.CanonicalSequence s) return null;
        var parts = new List<string> { s.BaseSequence };
        foreach (var m in s.Modifications)
        {
            var r = Omics.SequenceConversion.MzLibModificationLookup.Instance.TryResolve(m) ?? Omics.SequenceConversion.GlobalModificationLookup.Instance.TryResolve(m);
            var formula = r?.MzLibModification?.ChemicalFormula ?? r?.ChemicalFormula;
            if (formula is null) return null;
            parts.Add($"{m.PositionType}{m.ResidueIndex}:{formula.Formula}");
        }
        return string.Join(";", parts);
    }

    /// <summary>S7 (ptmQtl D57, their user, 2026-10-19): whether a peptidoform's several MetaMorpheus names are the same
    /// modifications at the same positions, as ptmQtl's runner decides it (<c>tools/PtmQtlRunner</c>, 19a8340).</summary>
    /// <returns><see cref="SameByUnimod"/> when every name has a Unimod form and they agree; else
    /// <see cref="SameByFormula"/> when every name's modifications resolve to one formula at each position (isomers pass,
    /// and so do synonyms one of which has no Unimod form); else null, and the peptidoform is left out.</returns>
    public static string? SameModifications(IReadOnlyList<string> names)
    {
        var unimod = names.Select(UnimodForm).ToList();
        if (unimod.All(u => u is not null)) return unimod.Distinct(StringComparer.Ordinal).Count() == 1 ? SameByUnimod : null;
        var formula = names.Select(FormulaForm).ToList();
        return formula.All(f => f is not null) && formula.Distinct(StringComparer.Ordinal).Count() == 1 ? SameByFormula : null;
    }

    // ---------------------------------------------------------------- S2c: blocking modifications at a C-terminus

    public const string S2c = "S2c";

    /// <summary>The reason S2c gives a site, naming whose list made its modification blocking.</summary>
    public static string S2cReason(string source) =>
        $"S2c: a trypsin-blocking modification on a peptide's C-terminal residue ({(source == BlockingByMzLib ? "mzLib's list" : "an acyl amide mzLib's list misses")})";

    /// <summary>A blocking modification on mzLib's own list (<c>Modification.BlocksCleavage</c>, mzLib #1119, released in
    /// 1.0.595): the set a search with modification-aware digestion respects.</summary>
    public const string BlockingByMzLib = "mzLib";

    /// <summary>A blocking modification ptmQtl adds: an acyl amide on the lysine epsilon-amine that mzLib's list misses.</summary>
    public const string BlockingByPtmQtl = "ptmQtl: acyl amide mzLib's list misses";

    /// <summary>The lysine acyl amides ptmQtl adds to mzLib's list (S2c v3, their D59; <c>measure.py</c>'s BEYOND),
    /// asked of cleavageRules as CLEAVAGERULES-P3.</summary>
    public static readonly Regex AcylAmidesMzLibMisses = new("isonicotinyl|lipoyl|methacryl|myristoyl|palmitoyl", RegexOptions.CultureInvariant);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> BlockingSources = new(StringComparer.Ordinal);

    /// <summary>S2c v3 (ptmQtl D59, 2026-10-20): whose list makes a MetaMorpheus name (<c>Category:IdWithMotif</c>) a
    /// blocking modification, or null when it is not one, as ptmQtl's runner's <c>blocking</c> mode classifies it.</summary>
    /// <remarks>The name is resolved by <c>MzLibModificationLookup</c>, then <c>GlobalModificationLookup</c>, and
    /// classified by mzLib's <c>BlocksCleavage</c>. A name neither lookup resolves is not blocking: it is never guessed.
    /// The five acyl amides count only on K and only when the name resolves.</remarks>
    public static string? BlockingSource(string name) => BlockingSources.GetOrAdd(name, n =>
    {
        var probe = new CanonicalModification(ModificationPositionType.Residue, 0, n[^1], n);
        var modification = (MzLibModificationLookup.Instance.TryResolve(probe) ?? GlobalModificationLookup.Instance.TryResolve(probe))?.MzLibModification;
        if (modification is null) return null;
        if (modification.BlocksCleavage) return BlockingByMzLib;
        return n[^1] == 'K' && AcylAmidesMzLibMisses.IsMatch(PtmQtlCore.IdWithMotif(n)) ? BlockingByPtmQtl : null;
    });

    /// <summary>The first MetaMorpheus release that digests aware of a modification (the mzLib #1119 family), or null
    /// while none does: every release to 1.1.12 lacks it, so S2c applies to every search (ptmQtl CLEAVAGERULES-P2).</summary>
    public static readonly Version? ModificationAwareDigestionFrom = null;

    /// <summary>S2c: every site of a blocking modification that some S7 observation carries on its last residue.</summary>
    /// <remarks>Any such observation leaves the site out, mixed evidence too (the user's choice (a)). A protein's own
    /// C-terminus is not told apart. The modification must sit on the residue its IdWithMotif names. Observations are
    /// read with no run filter: every observation is evidence.</remarks>
    public static List<ExcludedSite> BlockedSites(IEnumerable<PeptidoformObservation> observations)
    {
        // A site's source is mzLib's when any of its names is on mzLib's list.
        var found = new SortedDictionary<(string, int, char, string), string>();
        foreach (var o in observations.Select(o => (o.FullSequence, o.ProteinAccession, o.StartResidue)).Distinct())
        {
            var (bases, mods) = Parse(o.FullSequence);
            foreach (var m in mods)
            {
                if (m.Position != bases.Length || m.Residue is not char residue) continue;
                var idm = PtmQtlCore.IdWithMotif(m.Name);
                if (idm[^1] != residue || BlockingSource(m.Name) is not { } source) continue;
                var key = (o.ProteinAccession, o.StartResidue + m.Position - 1, residue, idm);
                if (!found.TryGetValue(key, out var known) || known != BlockingByMzLib) found[key] = source;
            }
        }
        return found.Select(f => new ExcludedSite(f.Key.Item1, f.Key.Item2, f.Key.Item3, f.Key.Item4, S2c, S2cReason(f.Value))).ToList();
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

    // ---------------------------------------------------------------- S2e: carbamidomethyl written as carbamyl + methyl

    public const string S2e = "S2e";

    /// <summary>Methyl, dimethyl and trimethyl: S2e's methyl-K/R, under any name.</summary>
    public static readonly IReadOnlySet<string> Methyls = new HashSet<string>(StringComparer.Ordinal) { "UNIMOD:34", "UNIMOD:36", "UNIMOD:37" };

    public const string Carbamyl = "UNIMOD:5";

    /// <summary>A site is left out when at least this share of the PSMs placing its methyl also carry a carbamyl (ptmQtl
    /// D58).</summary>
    public const double S2eShare = 0.5;

    /// <summary>The SQL test for an S2e methyl PSM, as ptmQtl's <c>measure.py</c> writes it: a K or R whose first
    /// modification in the ProForma is UNIMOD 34, 36 or 37.</summary>
    public const string MethylPsmSql = @"regexp_matches(peptidoform, '[KR]\[UNIMOD:(34|36|37)\]')";

    /// <summary>One methyl-K/R site's S2e count.</summary>
    /// <param name="Position">The protein position: the PSM's start plus the residue's place in the peptide.</param>
    /// <param name="Modification">The methyl's UNIMOD accession.</param>
    public sealed record MethylSite(string Protein, char Residue, int Position, string Modification, long MethylPsms, long WithCarbamyl)
    {
        public double Share => (double)WithCarbamyl / MethylPsms;
    }

    /// <summary>S2e's counts: per methyl-K/R site, the PSMs that place the methyl there and those of them that also carry a
    /// carbamyl anywhere.</summary>
    /// <param name="psms">(ProForma peptidoform, accession, start, PSMs) of the methyl PSMs (<see cref="MethylPsmSql"/>):
    /// target, passing S7's tier, ambiguity level 1, one accession, a start.</param>
    /// <remarks>Read off the ProForma's UNIMOD accessions, so a UniProt name and a Common Biological one for the same
    /// methyl count as one site, as in ptmQtl's <c>measure.py</c>. The ProForma is scanned by bracket depth.</remarks>
    public static List<MethylSite> MethylSites(IEnumerable<(string Peptidoform, string Protein, int Start, long Psms)> psms)
    {
        var sites = new SortedDictionary<(string, char, int, string), (long Methyl, long Carbamyl)>();
        foreach (var p in psms)
        {
            var (sequence, mods) = ParseProforma(p.Peptidoform);
            var carbamyl = mods.Any(m => m.Accession == Carbamyl);
            foreach (var (index, accession) in mods)
            {
                if (!Methyls.Contains(accession) || index < 0 || index >= sequence.Length || sequence[index] is not ('K' or 'R')) continue;
                var key = (p.Protein, sequence[index], p.Start + index, accession);
                var (methyl, carried) = sites.GetValueOrDefault(key);
                sites[key] = (methyl + p.Psms, carried + (carbamyl ? p.Psms : 0));
            }
        }
        return sites.Select(s => new MethylSite(s.Key.Item1, s.Key.Item2, s.Key.Item3, s.Key.Item4, s.Value.Methyl, s.Value.Carbamyl)).ToList();
    }

    /// <summary>A ProForma's base sequence and its modifications, each at its residue's 0-based index: -1 for the
    /// N-terminus, the sequence's length for the C-terminus.</summary>
    public static (string Sequence, List<(int Index, string Accession)> Modifications) ParseProforma(string proforma)
    {
        var letters = new System.Text.StringBuilder();
        var mods = new List<(int, string)>();
        int depth = 0, start = -1;
        bool nterm = false, cterm = false;
        for (var i = 0; i < proforma.Length; i++)
        {
            var ch = proforma[i];
            if (ch == '[')
            {
                if (depth++ == 0) start = i + 1;
            }
            else if (ch == ']' && depth > 0)
            {
                if (--depth != 0) continue;
                var text = proforma[start..i];
                nterm = letters.Length == 0 && i + 1 < proforma.Length && proforma[i + 1] == '-';
                mods.Add((nterm ? -1 : cterm ? letters.Length : letters.Length - 1, text));
            }
            else if (depth == 0 && ch == '-') cterm = letters.Length > 0;
            else if (depth == 0 && char.IsLetter(ch)) letters.Append(ch);
        }
        return (letters.ToString(), mods);
    }

    /// <summary>The reason S2e gives a site, with its carbamyl share, as ptmQtl's <c>make_list.py</c> writes it.</summary>
    public static string S2eReason(MethylSite site) =>
        $"S2e: methyl-K/R whose PSMs mostly also carry a carbamyl ({Math.Round(Math.Round(site.Share, 4) * 100, MidpointRounding.ToEven):0} % of {site.MethylPsms}): carbamidomethyl written as carbamyl + methyl";

    /// <summary>S2e: the stored sites of each methyl-K/R site whose PSMs mostly also carry a carbamyl, under every name the
    /// deposit stores for that methyl there.</summary>
    /// <param name="stored">(accession, position, residue, IdWithMotif, UNIMOD accession) of the dataset's target sites.</param>
    public static List<ExcludedSite> CarbamylSites(IEnumerable<MethylSite> methylSites,
        IEnumerable<(string Protein, int Position, char Residue, string Name, string Modification)> stored)
    {
        var names = stored.GroupBy(s => (s.Protein, s.Position, s.Residue, s.Modification))
            .ToDictionary(g => g.Key, g => g.Select(s => s.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        return methylSites.Where(s => s.Share >= S2eShare)
            .SelectMany(s => names.GetValueOrDefault((s.Protein, s.Position, s.Residue, s.Modification), [])
                .Select(name => new ExcludedSite(s.Protein, s.Position, s.Residue, name, S2e, S2eReason(s))))
            .ToList();
    }

    /// <summary>S2e's gate: whether the search offered carbamidomethyl (UNIMOD:4) on Met, Lys and the N-terminus
    /// (<c>X</c>), so that it can name the event itself. No stored search does (measured 2026-10-20).</summary>
    public static bool CarbamidomethylOffered(IEnumerable<(string? Modification, string Residues)> declared)
    {
        var residues = declared.Where(d => d.Modification == "UNIMOD:4").SelectMany(d => d.Residues).ToHashSet();
        return residues.Contains('M') && residues.Contains('K') && residues.Contains('X');
    }
}
