using System.Text.Json.Nodes;
using System.Xml;
using Omics.Modifications;
using Proteomics;
using Proteomics.ProteolyticDigestion;
using PXReprise.Cli;

namespace PXReprise.Search;

/// <summary>
/// Which spectra a spectral library leaves out until clean libraries come from the search itself (the user, 2026-10-27:
/// "clean the libraries now and keep them clean until clean libraries are produced"; cleavageRules PXR-V8). Two rules,
/// each judged on the spectrum's full sequence (its "Name:" line):
/// <list type="bullet">
/// <item><b>blocked at a cut C-terminus</b>: its last residue carries a modification that blocks trypsin there, and
/// mzLib's <see cref="CleavageBlockingPolicy"/> rules out EVERY place its base sequence occurs in the organism's target
/// proteins. A spectrum is kept when any occurrence is a protein C-terminus or a residue trypsin never cut after, and when
/// the sequence occurs nowhere or names a modification we cannot resolve, because neither can be judged. This is
/// cleavageRules' rule (their MetaMorpheus branch 7d6f1b13a, <c>CleavageBlockedLibrarySpectra</c>, measured on rat v037:
/// 3,019 of 3,038 dropped, the 19 kept all protein C-termini), so the libraries agree with the search once a MetaMorpheus
/// release runs with <c>RespectCleavageBlockingModifications</c>.</item>
/// <item><b>a doubtful class</b>: it carries a GPTMD-added site of a class agingPTM-v5 removed (Hydroxylation on P or K,
/// the misplaced Met/Trp oxidation fingerprint, results/gptmd-census_2026-10-25). UniProt-annotated hydroxylations keep
/// their UniProt names and stay.</item>
/// </list>
/// Every library this engine registers is filtered, so a library cleaned once stays clean.
/// </summary>
public sealed class LibraryCleaning
{
    /// <summary>The GPTMD classes agingPTM-v5 removed, as a full sequence writes them.</summary>
    public static readonly IReadOnlyList<string> DoubtfulClasses = new[] { "Common Biological:Hydroxylation on P", "Common Biological:Hydroxylation on K" };

    /// <summary>The protease every library spectrum is judged against: every profile digests with trypsin, and a deposit
    /// searched otherwise contributes few spectra (cleavageRules PXR-V8).</summary>
    public const string Protease = "trypsin";

    private readonly Dictionary<string, Modification> _mods;
    private readonly IReadOnlyList<string> _databases;
    private readonly DigestionParams _digestion = new(Protease, respectCleavageBlockingModifications: true);

    private LibraryCleaning(Dictionary<string, Modification> mods, IReadOnlyList<string> databases)
    {
        _mods = mods;
        _databases = databases;
    }

    /// <summary>
    /// The rules for one organism: the modifications a full sequence may name ("Type:IdWithMotif"; mzLib's own first, then
    /// <paramref name="modFiles"/>, the MetaMorpheus Mods files and the profile's custom file), and the organism's target
    /// protein databases (UniProt XML; only their sequences are read).
    /// </summary>
    public static LibraryCleaning For(IEnumerable<string> modFiles, IEnumerable<string> databases)
    {
        var mods = new Dictionary<string, Modification>(StringComparer.Ordinal);
        void Add(Modification m) { if (m.ModificationType is not null && m.IdWithMotif is not null) mods.TryAdd($"{m.ModificationType}:{m.IdWithMotif}", m); }
        foreach (var m in Mods.AllKnownMods) Add(m);
        foreach (string f in modFiles)
        {
            if (!File.Exists(f) || Path.GetFileName(f).StartsWith("Rna", StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<Modification> read;
            try { read = Omics.Modifications.IO.ModificationLoader.ReadModsFromFile(f, out _).ToList(); }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or MzLibUtil.MzLibException) { continue; }
            foreach (var m in read) Add(m);
        }
        var dbs = databases.ToList();
        if (dbs.Count == 0) throw new UsageException("cleaning a spectral library needs the organism's protein databases");
        foreach (string d in dbs) if (!File.Exists(d)) throw new SearchSetupException($"cleaning a spectral library: database {d} does not exist");
        return new LibraryCleaning(mods, dbs);
    }

    public JsonObject Describe() => new()
    {
        ["rule"] = "pxreprise-library-cleaning/1: drop a spectrum blocked at a cut C-terminus (cleavageRules PXR-V8, mzLib CleavageBlockingPolicy, trypsin, every occurrence) or carrying a GPTMD Hydroxylation on P/K site (agingPTM-v5)",
        ["protease"] = Protease,
        ["doubtful_classes"] = new JsonArray(DoubtfulClasses.Select(c => (JsonNode)c).ToArray()),
        ["databases"] = new JsonArray(_databases.Select(d => (JsonNode)d.Replace('\\', '/')).ToArray()),
    };

    /// <summary>The full sequences, among <paramref name="fullSequences"/>, to leave out, each with its reason
    /// ("blocked_cterm" or "doubtful_class") and the modification that decided it.</summary>
    public Dictionary<string, (string Reason, string Modification)> Judge(IEnumerable<string> fullSequences)
    {
        var verdicts = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, (string BaseSequence, Modification Mod, string Name)>(StringComparer.Ordinal);
        foreach (string full in fullSequences.Distinct(StringComparer.Ordinal))
        {
            if (DoubtfulClasses.FirstOrDefault(c => full.Contains("[" + c + "]", StringComparison.Ordinal)) is { } cls)
            {
                verdicts[full] = ("doubtful_class", cls);
                continue;
            }
            if (TryLastResidueMod(full, out string baseSequence, out string name)
                && _mods.TryGetValue(name, out var mod)
                && CleavageBlockingModifications.BlocksCleavageBy(mod, _digestion.Protease))
                candidates[full] = (baseSequence, mod, name);
        }
        if (candidates.Count == 0) return verdicts;

        // cleavageRules' scan: each protein once, candidates indexed by a prefix as long as the shortest of them.
        int prefixLength = candidates.Values.Min(c => c.BaseSequence.Length);
        var byPrefix = candidates.GroupBy(c => c.Value.BaseSequence[..prefixLength], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        var candidateMods = candidates.Values.Select(c => c.Mod).Distinct().ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var producible = new HashSet<string>(StringComparer.Ordinal);
        foreach (string sequence in _databases.SelectMany(Sequences))
        {
            CleavageBlockingPolicy? policy = null;
            for (int start = 0; start + prefixLength <= sequence.Length; start++)
            {
                if (!byPrefix.TryGetValue(sequence.AsSpan(start, prefixLength), out var group)) continue;
                foreach (var (full, (baseSequence, mod, _)) in group)
                {
                    if (start + baseSequence.Length > sequence.Length || string.CompareOrdinal(sequence, start, baseSequence, 0, baseSequence.Length) != 0)
                        continue;
                    found.Add(full);
                    policy ??= CleavageBlockingPolicy.For(_digestion, candidateMods, new Protein(sequence, "pxreprise-library-cleaning"));
                    if (!policy.Value.Blocks(mod, start + baseSequence.Length)) producible.Add(full);
                }
            }
        }
        foreach (string full in found.Where(f => !producible.Contains(f))) verdicts[full] = ("blocked_cterm", candidates[full].Name);
        return verdicts;
    }

    /// <summary>The full sequence of a library "Name:" line ("Name: PEPT[Type:Id]IDE/2" gives "PEPT[Type:Id]IDE").</summary>
    public static string? FullSequenceOf(string nameLine)
    {
        if (!nameLine.StartsWith("Name:", StringComparison.Ordinal)) return null;
        string s = nameLine[5..].Trim();
        int slash = s.LastIndexOf('/');
        return slash > 0 ? s[..slash] : s;
    }

    /// <summary>
    /// The base sequence and the modification ("Type:IdWithMotif") on the last residue of a MetaMorpheus full sequence, when
    /// it has one. Brackets nest ("[Metal:Fe[III] on D]"); a peptide C-terminal modification ("-[...]") is not a residue's.
    /// </summary>
    public static bool TryLastResidueMod(string full, out string baseSequence, out string modName)
    {
        baseSequence = modName = "";
        if (!full.EndsWith(']')) return false;
        int depth = 0, open = -1;
        for (int i = full.Length - 1; i >= 0; i--)
        {
            if (full[i] == ']') depth++;
            else if (full[i] == '[' && --depth == 0) { open = i; break; }
        }
        if (open <= 0 || full[open - 1] == '-') return false;
        modName = full[(open + 1)..^1];
        var b = new System.Text.StringBuilder(full.Length);
        depth = 0;
        foreach (char c in full)
        {
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (depth == 0 && char.IsUpper(c)) b.Append(c);
        }
        baseSequence = b.ToString();
        return baseSequence.Length > 0;
    }

    /// <summary>Every protein sequence in a UniProt XML (the text of each entry's top-level &lt;sequence&gt;).</summary>
    private static IEnumerable<string> Sequences(string xml)
    {
        using var r = XmlReader.Create(xml, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore });
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "sequence" && r.Depth == 2)
            {
                string s = new(r.ReadElementContentAsString().Where(char.IsLetter).ToArray());
                if (s.Length > 0) yield return s;
            }
        }
    }
}
