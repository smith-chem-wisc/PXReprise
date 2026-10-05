using System.Text;

namespace DataRepo.Ingest;

/// <summary>Translate MetaMorpheus full-sequence notation into ProForma 2.</summary>
/// <remarks>
/// One peptidoform notation everywhere, with UNIMOD accessions (FRAMEWORK section 3). MetaMorpheus
/// writes <c>KLADQC[Common Fixed:Carbamidomethyl on C]TGLQ</c>; the repository stores
/// <c>KLADQC[UNIMOD:4]TGLQ</c>, which is what ProForma 2 readers, USIs and the benchmark's questions are
/// written in.
/// <para>This module is a stop-gap and is meant to be deleted. pyMzLib's <c>psmtsv</c> records already carry a
/// <c>pro_forma</c> field; it is null for MetaMorpheus files in 0.1.x, which is why the translation happens
/// here (DATAREPO-12 to pyMzLib). When that field is populated, this becomes a fallback and then goes.</para>
/// <para>The one thing it will not do is guess. A modification the registry cannot resolve keeps its name in
/// a ProForma <c>[Info:...]</c> tag and is reported, so an unresolved modification shows up as a Finding on
/// the dataset rather than as a plausible-looking wrong accession.</para>
/// Ported from <c>proforma.py</c>. Python iterates a string by code point, so a residue here is one code
/// point (a surrogate pair stays together), never half of one.
/// </remarks>
public static class Proforma
{
    /// <summary>Position marker for <see cref="ModPlacement.Position"/> of an N-terminal modification.</summary>
    public const long NTerminus = 0;

    /// <summary>Position marker for <see cref="ModPlacement.Position"/> of a C-terminal modification.</summary>
    public const long CTerminus = -1;

    /// <summary>Python's <c>MOD_TOKEN</c>, re-exported here because <c>proforma.py</c> imports it.</summary>
    public static System.Text.RegularExpressions.Regex ModToken => ModList.ModToken;

    private static string Tag(string modName, string? entryUnimod, double? mass)
    {
        if (entryUnimod is not null) return $"[{entryUnimod}]";
        if (mass is not null) return "[" + ModList.PyFixed(mass.Value, 6, forceSign: true) + "]";
        return $"[Info:{modName}]";
    }

    /// <summary>Split a full sequence into (residue, bracket-content) pairs, plus a leading pair.</summary>
    /// <remarks>A bracket before the first residue is returned with an empty residue, which is how an
    /// N-terminal modification announces itself in MetaMorpheus notation. Brackets nest; an unclosed one runs
    /// to the end. Under <see cref="IngestRules.Python0320"/> its content then loses the final code point (Python's
    /// <c>[i + 1 : j - 1]</c> slice assumed a closing bracket at <c>j - 1</c>); under <see cref="IngestRules.Current"/>
    /// the content is everything after the bracket, as written (G83 item 3).</remarks>
    internal static List<(string Residue, string? Bracket)> Split(string fullSequence, IngestRules rules = IngestRules.Current)
    {
        var output = new List<(string Residue, string? Bracket)>();
        var i = 0;
        string? pendingLeading = null;
        var n = fullSequence.Length;
        while (i < n)
        {
            var ch = fullSequence[i];
            if (ch == '[')
            {
                var depth = 1;
                var j = i + 1;
                while (j < n && depth != 0)
                {
                    if (fullSequence[j] == '[') depth++;
                    else if (fullSequence[j] == ']') depth--;
                    j++;
                }
                // Python's full_sequence[i + 1 : j - 1], where j - 1 drops one code point.
                var end = j - 1;
                if (depth != 0 && rules == IngestRules.Current)
                    end = j;  // unclosed: nothing to drop
                else if (depth != 0 && end > i + 1 && char.IsLowSurrogate(fullSequence[end]) && char.IsHighSurrogate(fullSequence[end - 1]))
                    end--;
                var content = end > i + 1 ? fullSequence[(i + 1)..end] : "";
                if (output.Count > 0)
                {
                    var (residue, existing) = output[^1];
                    output[^1] = (residue, existing is null ? content : $"{existing}|{content}");
                }
                else
                    pendingLeading = pendingLeading is null ? content : $"{pendingLeading}|{content}";
                i = j;
                continue;
            }
            var width = i + 1 < n && char.IsSurrogatePair(ch, fullSequence[i + 1]) ? 2 : 1;
            output.Add((fullSequence.Substring(i, width), null));
            i += width;
        }
        if (pendingLeading is not null) output.Insert(0, ("", pendingLeading));
        return output;
    }

    /// <summary>Does a token's <c>on &lt;residue&gt;</c> fit the residue it sits on? Read as a <c>TG</c> line is read, so
    /// a motif (<c>Nxs</c>) names its first residue; <c>X</c> fits anything.</summary>
    /// <remarks><b>A terminal bracket is not checked.</b> mzLib builds a reversed decoy with the target's protein
    /// N-terminal modification still at the N-terminus, now on whatever residue the reversal put there, so
    /// MetaMorpheus writes <c>[UniProt:N-acetylglycine on G]SVAA...</c> for a decoy. Measured on aging's corpus
    /// (2026-10-05): 10,601 distinct sequences, 17,667 PSM rows in 87 datasets disagree at the N-terminus, every one
    /// of them a decoy, and none on a residue. The modification is the one the engine scored, so it keeps its
    /// accession there.</remarks>
    private static bool ResidueAgrees(string tokenResidue, long position, string placedOn)
    {
        if (position is NTerminus or CTerminus) return true;
        var named = ModList.Targets(tokenResidue);
        if (named.Count == 0 || named.Contains("X")) return true;
        return named.Contains(ModList.PyUpper(placedOn));
    }

    /// <summary>Convert one MetaMorpheus full sequence to ProForma 2.</summary>
    /// <param name="fullSequence">the <c>Full Sequence</c> column of a <c>.psmtsv</c>.</param>
    /// <param name="registry">modifications from the MetaMorpheus install that did the search.</param>
    /// <returns>A <see cref="Peptidoform"/> carrying the ProForma string, the unmodified sequence, where each
    /// modification sits, and the names nothing could resolve.</returns>
    /// <remarks>Under <see cref="IngestRules.Current"/> a token on a residue must name that residue (<c>on S</c> on
    /// an S), or <c>X</c>. A token that names another residue contradicts the sequence, so it is not resolved: it
    /// keeps its name in an <c>[Info:...]</c> tag and is reported, never given the accession of a modification it
    /// does not describe. Terminal brackets are exempt (see <see cref="ResidueAgrees"/>). Python 0.32.0 never
    /// checked (G83 item 1).</remarks>
    public static Peptidoform Parse(string fullSequence, ModRegistry registry, IngestRules rules = IngestRules.Current)
    {
        if (string.IsNullOrEmpty(fullSequence)) return new Peptidoform("", "");

        var pieces = Split(fullSequence, rules);
        var baseResidues = new List<string>();
        var mods = new List<ModPlacement>();
        var unresolved = new List<string>();
        var nTermTags = new List<string>();
        var cTermTags = new List<string>();
        var residueTags = new Dictionary<long, List<string>>();

        for (var index = 0; index < pieces.Count; index++)
        {
            var (residue, bracket) = pieces[index];
            // MetaMorpheus writes a C-terminal modification as `...L-[mod]`: the `-` is ProForma's
            // terminus marker, not a residue. It used to be appended to the base sequence, so the one
            // C-terminal site in the corpus was keyed on residue `-` one past the protein's end
            // (DATAREPO-33). Only a trailing `-` is a terminus; anywhere else it is kept as written.
            var cTerminal = residue == "-" && index == pieces.Count - 1 && bracket is not null;
            if (residue.Length > 0 && !cTerminal) baseResidues.Add(residue);
            var position = cTerminal ? CTerminus : (residue.Length > 0 ? baseResidues.Count : NTerminus);
            if (bracket is null) continue;
            foreach (var token in bracket.Split('|'))
            {
                var m = ModList.ModToken.Match(token);
                var category = m.Success ? m.Groups["category"].Value : "";
                var name = m.Success ? $"{m.Groups["name"].Value} on {m.Groups["residue"].Value}" : token;
                var target = m.Success ? m.Groups["residue"].Value : null;
                var placedOn = position switch
                {
                    NTerminus => "N-term",
                    CTerminus => "C-term",
                    _ => baseResidues[(int)position - 1],
                };
                var entry = rules == IngestRules.Current && target is not null && !ResidueAgrees(target, position, placedOn)
                    ? null
                    : registry.Lookup(name, target, rules);
                var unimod = entry?.UnimodCurie;
                var mass = entry?.MonoisotopicMass;
                if (unimod is null && mass is null) unresolved.Add(token);
                mods.Add(new ModPlacement(position, placedOn, name, category, unimod, mass));
                var tag = Tag(name, unimod, mass);
                if (position == NTerminus) nTermTags.Add(tag);
                else if (position == CTerminus) cTermTags.Add(tag);
                else
                {
                    if (!residueTags.TryGetValue(position, out var tags)) residueTags[position] = tags = [];
                    tags.Add(tag);
                }
            }
        }

        var proforma = new StringBuilder();
        if (nTermTags.Count > 0) proforma.Append(string.Concat(nTermTags)).Append('-');
        for (var i = 0; i < baseResidues.Count; i++)
        {
            proforma.Append(baseResidues[i]);
            if (residueTags.TryGetValue(i + 1, out var tags)) proforma.Append(string.Concat(tags));
        }
        if (cTermTags.Count > 0) proforma.Append('-').Append(string.Concat(cTermTags));

        return new Peptidoform(
            proforma.ToString(),
            string.Concat(baseResidues),
            mods,
            unresolved.Distinct(StringComparer.Ordinal).ToList());
    }
}

/// <summary>One modification on one residue of a peptide.</summary>
/// <param name="Position">1-based residue index in the peptide; <see cref="Proforma.NTerminus"/> or
/// <see cref="Proforma.CTerminus"/> for a terminus.</param>
/// <param name="Residue">The modified residue's one-letter code, or <c>N-term</c> / <c>C-term</c>.</param>
/// <param name="Name">MetaMorpheus's name for the modification, verbatim.</param>
/// <param name="Category">MetaMorpheus's group, e.g. <c>Common Biological</c>, <c>UniProt</c>.</param>
/// <param name="Unimod"><c>UNIMOD:&lt;n&gt;</c> when the registry resolved it.</param>
/// <param name="Mass">Monoisotopic mass shift when the registry knows one.</param>
public sealed record ModPlacement(long Position, string Residue, string Name, string Category, string? Unimod, double? Mass)
{
    public bool Resolved => Unimod is not null;
}

/// <summary>A parsed MetaMorpheus full sequence.</summary>
/// <remarks>Equality is by value, the lists compared element by element, as Python's frozen dataclass of tuples.</remarks>
public sealed record Peptidoform(string Proforma, string BaseSequence)
{
    public Peptidoform(string proforma, string baseSequence, IReadOnlyList<ModPlacement> mods, IReadOnlyList<string> unresolved)
        : this(proforma, baseSequence)
    {
        Mods = mods;
        Unresolved = unresolved;
    }

    public IReadOnlyList<ModPlacement> Mods { get; init; } = [];

    /// <summary>Tokens nothing could resolve, each once, in first-seen order.</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    public bool IsModified => Mods.Count > 0;

    public bool Equals(Peptidoform? other) =>
        other is not null && Proforma == other.Proforma && BaseSequence == other.BaseSequence
        && Mods.SequenceEqual(other.Mods) && Unresolved.SequenceEqual(other.Unresolved, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Proforma, BaseSequence, Mods.Count, Unresolved.Count);
}

/// <summary><see cref="Proforma.Parse"/> memoized over one dataset.</summary>
/// <remarks>A dataset's 43k PSMs collapse to a few thousand distinct full sequences, so the same string is
/// translated over and over. The cache also accumulates the unresolved names, which become one
/// Finding per dataset rather than one per PSM.</remarks>
public sealed class ProformaCache(ModRegistry registry, IngestRules rules = IngestRules.Current)
{
    private readonly Dictionary<string, Peptidoform> _cache = new(StringComparer.Ordinal);

    public ModRegistry Registry { get; } = registry;

    public IngestRules Rules { get; } = rules;

    /// <summary>Each unresolved token with how many sequences carried it, in first-seen order.</summary>
    /// <remarks>Under <see cref="IngestRules.Current"/> the count is of distinct full sequences. Python 0.32.0
    /// counted every call, so one sequence was counted once per PSM row, again per peptide row, per site pass and
    /// per quantified peptide, and the number meant nothing a reader could name (G83 item 4).</remarks>
    public Dictionary<string, long> Unresolved { get; } = new(StringComparer.Ordinal);

    /// <summary>Python's <c>cache(full_sequence)</c>.</summary>
    public Peptidoform Get(string fullSequence)
    {
        var miss = !_cache.TryGetValue(fullSequence, out var hit);
        if (miss)
        {
            hit = Proforma.Parse(fullSequence, Registry, Rules);
            _cache[fullSequence] = hit;
            foreach (var name in hit.Unresolved)
                Unresolved[name] = Unresolved.GetValueOrDefault(name);
        }
        if (miss || Rules == IngestRules.Python0320)
            foreach (var name in hit!.Unresolved)
                Unresolved[name] += 1;
        return hit!;
    }

    /// <summary>How many distinct full sequences have been parsed.</summary>
    public int Size => _cache.Count;
}
