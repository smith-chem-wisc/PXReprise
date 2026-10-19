using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Omics.SequenceConversion;
using Proteomics.ProteolyticDigestion;
using Readers;
using UsefulProteomicsDatabases;

namespace PXReprise.Discovery;

/// <summary>Where a chemistry fact came from, in the order D22 trusts them.</summary>
public static class ChemistrySource
{
    public const string CuratedSdrf = "curated_sdrf";
    public const string DepositedSdrf = "deposited_sdrf";
    public const string PridePtm = "pride_identified_ptms";
    public const string PrideQuant = "pride_quantification";
    public const string ProtocolText = "protocol_text";
    public const string Default = "default";
    /// <summary>The question's <c>[exceptions]</c> for the deposit (v0.4.15: <c>alkylation = "none"</c>); it outranks every other source.</summary>
    public const string QuestionException = "question_exception";
}

/// <summary>One fact (a protease, an alkylation, a label), where it came from, and the words it was read from.</summary>
public sealed record ChemistryFact(string Value, string Source, string? Evidence = null)
{
    public bool Guessed => Source == ChemistrySource.ProtocolText;
}

/// <summary>
/// A cysteine modification as UniMod names it, and whether the search should hold it fixed or variable.
/// <see cref="MetaMorpheusId"/> is the <c>ModificationType\tIdWithMotif</c> string a MetaMorpheus task file takes,
/// resolved by mzLib (oracle 2026-10-05), or null when mzLib knows no such modification on C. A modification no
/// UniMod entry describes (G40: NBF) carries its <see cref="CustomId"/> from the profile's own modification file instead.
/// </summary>
public sealed record CysMod(string Name, string Unimod, bool Fixed, string? CustomId = null)
{
    public string? MetaMorpheusId => CustomId ?? ChemistryDetector.MetaMorpheusModId(Unimod, 'C');
}

/// <summary>
/// A deposit's chemistry (G19, decided 2026-10-05 as D22 to D24): which protease made its peptides, how its cysteines
/// were alkylated, and what label it carries. Read before any download. <see cref="Park"/> is set when the search cannot
/// proceed as is (e.g. <c>waiting_multi_protease</c>).
/// </summary>
public sealed record DepositChemistry(
    ChemistryFact Protease,
    IReadOnlyDictionary<string, string>? ProteasePerFile,
    ChemistryFact Alkylation,
    IReadOnlyList<CysMod> CysMods,
    ChemistryFact Label,
    string? Park)
{
    public bool AnyGuessed => Protease.Guessed || Alkylation.Guessed || Label.Guessed;

    /// <summary>
    /// The question says the cysteines were never alkylated (v0.4.15, PXR-A99): no cysteine modification, whatever was
    /// read. A wait on an unmodelled thiol reagent goes with it; any other wait (a protease, a label) stays.
    /// </summary>
    public DepositChemistry Unalkylated(string evidence) => this with
    {
        Alkylation = new("none", ChemistrySource.QuestionException, evidence),
        CysMods = Array.Empty<CysMod>(),
        Park = Park == "waiting_unknown_alkylant" ? null : Park,
    };
}

/// <summary>
/// Reads a deposit's chemistry from, in order (D22): the question's curated SDRF, the deposit's own or community SDRF,
/// PRIDE's identified PTMs and quantification methods, the protocol text, else the default (trypsin, fixed
/// carbamidomethyl, label-free). The first source that says something wins, per fact. Any clear mention is enough;
/// every fact keeps its source, so a text guess is visible as one.
/// </summary>
public static class ChemistryDetector
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // ------------------------------------------------------------------ protease (D24)

    /// <summary>Canonical protease names, exactly as mzLib's proteases.tsv spells them (MetaMorpheus reads that list), with the words that name each. Pepsin and proteinase K are not in it: they cut almost anywhere, so they are non-specific.</summary>
    private static readonly (string Name, Regex Pattern)[] Proteases =
    {
        ("trypsin", new(@"\btrypsin\b(?![- ]?EDTA)(?!i[sz])", Opt)),
        ("Lys-C|P", new(@"\bLys-?C\b|endoproteinase Lys-?C|\blysyl endopeptidase", Opt)),
        ("Glu-C", new(@"\bGlu-?C\b|\bV8 protease", Opt)),
        ("Asp-N", new(@"\bAsp-?N\b", Opt)),
        ("chymotrypsin|P", new(@"\bchymotrypsin\b", Opt)),
        ("Arg-C", new(@"\bArg-?C\b|\bclostripain\b", Opt)),
        ("Lys-N", new(@"\bLys-?N\b", Opt)),
        ("non-specific", new(@"\bpepsin\b|\bproteinase K\b", Opt)),
        ("elastase|P", new(@"\belastase\b", Opt)),
    };
    private const string Trypsin = "trypsin";
    private const string LysC = "Lys-C|P";
    private const string NonSpecific = "non-specific";

    /// <summary>The wait of a deposit whose protease cuts almost anywhere (v0.4.19); the screen gives the same status to an immunopeptidome.</summary>
    public const string NonspecificCleavagePark = "waiting_nonspecific_cleavage";

    /// <summary>The label of a deposit whose SDRF names a label we do not know, and nothing else settles it (v0.4.25).</summary>
    public const string UnknownLabel = "unknown";

    /// <summary>The wait of such a deposit: a person says what the label is (<see cref="DecisionsNeeded"/>).</summary>
    public const string UnknownLabelPark = "waiting_unknown_label";

    /// <summary>
    /// The spellings each protease may have in the pinned mzLib, preferred first. mzLib #1186 (open) renames the |P
    /// variants, so the name is resolved through <see cref="ProteaseDictionary"/> at run time, never hard-coded.
    /// </summary>
    private static readonly Dictionary<string, string[]> Spellings = new(StringComparer.Ordinal)
    {
        ["trypsin"] = new[] { "trypsin" },
        ["Lys-C|P"] = new[] { "Lys-C|P", "Lys-C (don't cleave before proline)", "Lys-C" },
        ["chymotrypsin|P"] = new[] { "chymotrypsin|P", "chymotrypsin (don't cleave before proline)", "chymotrypsin" },
        ["elastase|P"] = new[] { "elastase|P", "elastase" },
        ["Glu-C"] = new[] { "Glu-C" }, ["Asp-N"] = new[] { "Asp-N" }, ["Arg-C"] = new[] { "Arg-C" }, ["Lys-N"] = new[] { "Lys-N" },
        ["non-specific"] = new[] { "non-specific" },
    };

    /// <summary>The name mzLib's <see cref="ProteaseDictionary"/> knows for a protease, or null when it knows none of its spellings.</summary>
    public static string? ResolveProtease(string name) =>
        (Spellings.TryGetValue(name, out var s) ? s : new[] { name }).FirstOrDefault(n => ProteaseDictionary.TryGetProtease(n, out _));

    private static readonly ConcurrentDictionary<(string, char), string?> ModIds = new();

    /// <summary>
    /// MetaMorpheus's <c>ModificationType\tIdWithMotif</c> for a UNIMOD accession on a residue: mzLib's own mods first
    /// (Carbamidomethyl on C is "Common Fixed"), then its embedded unimod.xml ("Unimod\tNethylmaleimide on C"). Reused from
    /// mzLib's sequence conversion (MzLibModificationLookup, UnimodModificationLookup), not a table of ours.
    /// </summary>
    public static string? MetaMorpheusModId(string unimod, char residue) => ModIds.GetOrAdd((unimod, residue), key =>
    {
        if (UnimodNumber(key.Item1) is not int id) return null;
        var cm = CanonicalModification.AtResidue(0, key.Item2, $"UNIMOD:{id}", unimodId: id);
        var ours = MzLibModificationLookup.ProteinOnly.TryResolve(cm)?.MzLibModification;
        var unimod = UnimodModificationLookup.Instance.TryResolve(cm)?.MzLibModification;
        // bridge 002 (PXR-B6, mzLib issue #1418): a lookup that cannot narrow its candidates returns the first one on the
        // residue, whatever its mass, without a warning. Two resolutions of one UNIMOD id must agree on mass, or the mod is
        // unknown and the deposit waits (waiting_unknown_modification) instead of being searched with the wrong chemistry.
        if (ours is not null && unimod is not null && ours.MonoisotopicMass is double a && unimod.MonoisotopicMass is double b && Math.Abs(a - b) > MassAgreement)
            return null;
        return (ours ?? unimod) is { } m ? $"{m.ModificationType}\t{m.IdWithMotif}" : null;
    });

    /// <summary>Daltons within which two resolutions of one UNIMOD id are the same chemistry.</summary>
    internal const double MassAgreement = 0.001;

    /// <summary>The number in "UNIMOD:108", "Unimod:35" or a bare "4" (the SDRF corpus writes all three).</summary>
    internal static int? UnimodNumber(string s) =>
        Regex.Match(s.Trim(), @"^(?:unimod:)?(\d+)$", Opt) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    // Cell culture: "detached with trypsin", "trypsin-EDTA", "trypsinized" are not digestion.
    private static readonly Regex CultureContext = new(@"detach|harvest|passag|split|subcultur|dissociat", Opt);

    /// <summary>The proteases a protocol names for digestion; trypsin + Lys-C is trypsin (D24).</summary>
    internal static IReadOnlyList<(string Name, string Evidence)> ProteasesInText(string text)
    {
        var found = new List<(string, string)>();
        foreach (var (name, pattern) in Proteases)
        {
            var m = pattern.Matches(text).FirstOrDefault(m => name != Trypsin || !CultureContext.IsMatch(Window(text, m, 60)));
            if (m is not null) found.Add((name, Snip(text, m)));
        }
        if (found.Count > 1 && found.Any(f => f.Item1 == Trypsin)) found.RemoveAll(f => f.Item1 == LysC);
        return found;
    }

    /// <summary>The canonical name for an SDRF or free-text protease name, or null.</summary>
    internal static string? CanonicalProtease(string name) => Proteases.FirstOrDefault(p => p.Pattern.IsMatch(name)).Name;

    /// <summary>File-name words for a protease: _GluC_, -AspN-, Chymo.</summary>
    private static readonly (string Name, Regex Pattern)[] ProteaseInFileName =
    {
        (Trypsin, new(@"(^|[_\-\. ])(tryp|trypsin|tryptic)([_\-\. ]|\d|$)", Opt)),
        (LysC, new(@"(^|[_\-\. ])lys-?c([_\-\. ]|\d|$)", Opt)),
        ("Glu-C", new(@"(^|[_\-\. ])glu-?c([_\-\. ]|\d|$)", Opt)),
        ("Asp-N", new(@"(^|[_\-\. ])asp-?n([_\-\. ]|\d|$)", Opt)),
        ("chymotrypsin|P", new(@"(^|[_\-\. ])chymo(trypsin)?([_\-\. ]|\d|$)", Opt)),
        ("Arg-C", new(@"(^|[_\-\. ])arg-?c([_\-\. ]|\d|$)", Opt)),
        ("Lys-N", new(@"(^|[_\-\. ])lys-?n([_\-\. ]|\d|$)", Opt)),
    };

    /// <summary>Each file's protease from a word in its name, or null when any file has none (D24: guess per file, else park).</summary>
    internal static IReadOnlyDictionary<string, string>? ProteasePerFileFromNames(IReadOnlyList<string> files)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in files)
        {
            var hits = ProteaseInFileName.Where(p => p.Pattern.IsMatch(Path.GetFileNameWithoutExtension(f))).Select(p => p.Name).Distinct().ToList();
            if (hits.Count != 1) return null;
            map[f] = hits[0];
        }
        return map.Count > 0 ? map : null;
    }

    // ------------------------------------------------------------------ alkylation (D23)

    private static readonly CysMod Carbamidomethyl = new("Carbamidomethyl", "UNIMOD:4", true);

    /// <summary>Alkylants by UniMod name, with how SDRFs, PRIDE's PTM names and protocols say them.</summary>
    private static readonly (string Name, string Unimod, Regex Sdrf, Regex Pride, Regex Text)[] Alkylants =
    {
        ("Carbamidomethyl", "UNIMOD:4", new(@"carbamidomethyl|UNIMOD:4\b", Opt), new(@"iodoacetamide|carbamidomethyl", Opt),
            new(@"\biodoacetamide\b|\bchloroacetamide\b|\b2-chloroacetamide\b|\bIA[AM]\b|\bCAA\b", Opt)),
        ("Nethylmaleimide", "UNIMOD:108", new(@"N-?ethylmaleimide(?!.*2H)|UNIMOD:108\b", Opt), new(@"(?<!deuterated )N-?ethylmaleimide(?!.*(deuter|2H))", Opt),
            new(@"(?<!d5-|d\(5\) |heavy |deuterated )\b(N-ethylmaleimide|NEM)\b(?![- ]?d\(?5)", Opt)),
        ("NEM:2H(5)", "UNIMOD:776", new(@"NEM:2H\(5\)|UNIMOD:776\b", Opt), new(@"deuterated N-?ethylmaleimide|N-?ethylmaleimide.*(deuter|2H)|d5-?N-?ethylmaleimide|NEM:2H", Opt),
            new(@"\bd\(?5\)?[- ]?NEM\b|\bNEM-?d\(?5\)?|(heavy|deuterated) (NEM|N-ethylmaleimide)|d\(?5\)?-?N-ethylmaleimide", Opt)),
        ("Propionamide", "UNIMOD:24", new(@"propionamide|UNIMOD:24\b", Opt), new(@"acrylamide adduct|propionamide", Opt),
            new(@"(?<!poly)(?<!bis-)\bacrylamide\b(?! gel)(?!/)", Opt)),
        ("Methylthio", "UNIMOD:39", new(@"methylthio|UNIMOD:39\b", Opt), new(@"methylthiolated", Opt),
            new(@"\bMMTS\b|methyl methanethiosulfonate", Opt)),
        ("Carboxymethyl", "UNIMOD:6", new(@"(?<!amido)carboxymethyl|UNIMOD:6\b", Opt), new(@"S-carboxymethyl", Opt),
            new(@"\biodoacetic acid\b", Opt)),
        ("Pyridylethyl", "UNIMOD:31", new(@"pyridylethyl|UNIMOD:31\b", Opt), new(@"pyridylethyl", Opt),
            new(@"\b4-vinylpyridine\b|\bvinylpyridine\b", Opt)),
        // aging 089 (PXR-A94, their S87): PXD057499 "alkylated with iodoethanol" (S-hydroxyethyl cysteine), searched with
        // carbamidomethyl fixed, 0.5% cysteine peptides.
        ("Ethanolyl", "UNIMOD:278", new(@"ethanolyl|UNIMOD:278\b", Opt), new(@"ethanolyl|hydroxyethyl", Opt),
            new(@"\b(2-)?iodoethanol\b", Opt)),
    };

    /// <summary>
    /// Thiol reagents a protocol can name that have no UniMod cysteine modification (aging 061, S82: PXD058611 blocks
    /// thiols with NBF-Cl). Named with no modelled alkylant beside them, the deposit WAITS (waiting_unknown_alkylant),
    /// never searched with the default carbamidomethyl, unless the profile models the reagent with a modification of its
    /// own (G40, <c>[thiol_reagents]</c>): then it is searched with that modification, variable.
    /// </summary>
    private static readonly (string Name, Regex Text)[] UnmodelledThiolReagents =
    {
        ("NBF-Cl", new(@"\bNBF-?Cl\b|4-chloro-7-nitrobenzofurazan|7-chloro-4-nitrobenz(o-2-oxa-1,3-)?diazole", Opt)),
    };

    /// <summary>"NBF" from <c>"PXReprise\tNBF on C"</c>: the modification's ID, its short name in logs and records.</summary>
    internal static string CustomModName(string metaMorpheusId)
    {
        string idWithMotif = metaMorpheusId[(metaMorpheusId.IndexOf('\t') + 1)..];
        int on = idWithMotif.LastIndexOf(" on ", StringComparison.Ordinal);
        return on > 0 ? idWithMotif[..on] : idWithMotif;
    }

    /// <summary>The names a profile's <c>[thiol_reagents]</c> may map (G40).</summary>
    public static IReadOnlyList<string> UnmodelledThiolReagentNames { get; } = UnmodelledThiolReagents.Select(r => r.Name).ToArray();

    private static readonly Regex AlkylationContext = new(@"alkylat|block|thiol|cystein|\bCys\b|derivati[sz]", Opt);

    private static readonly Regex NoAlkylation = new(@"without (reduction and )?alkylation|\bnot (reduced and )?alkylated\b|\bno alkylation\b|alkylation (step )?was omitted|omitt?ing (the )?alkylation", Opt);

    /// <summary>
    /// The cysteine modifications from a list of alkylant names (D23): one alkylant is held fixed; more than one (a
    /// light/heavy NEM pair, or NEM then iodoacetamide in a redox design) are all variable.
    /// </summary>
    internal static IReadOnlyList<CysMod> CysModsFor(IReadOnlyCollection<string> names)
    {
        var mods = Alkylants.Where(a => names.Contains(a.Name)).Select(a => new CysMod(a.Name, a.Unimod, names.Count == 1)).ToList();
        return mods;
    }

    // ------------------------------------------------------------------ labels (D25)

    private static readonly (string Name, Regex Pattern)[] Labels =
    {
        ("tmt", new(@"\bTMT|tandem mass tag", Opt)),
        ("itraq", new(@"\biTRAQ", Opt)),
        ("silac", new(@"\bSILAC\b|\b13C6\b|heavy (lysine|arginine)|\bLys8\b|\bArg10\b", Opt)),
        // v0.4.14: PXD000046 says "labeled using dimethyl isotope labeling"; "dimethyl label" alone let it through as label-free.
        ("dimethyl", new(DimethylLabel, Opt)),
        ("o18", new(@"\b(16O/)?18O\b|\bO-?18\b|oxygen-18|H2 ?18O", Opt)),
        ("dileu", new(@"\bi?DiLeu\b|N,N-dimethyl(ated)? leucine", Opt)),
        ("heavy_water", new(@"heavy[- ]?(labell?ed )?water|\bD2O\b|deuterated water", Opt)),
        ("15n", new(@"\b15N\b", Opt)),
    };
    private static readonly Regex LabelFreeWords = new(@"label[- ]?free", Opt);
    private static readonly Regex SpectralCounting = new(@"spectr(um|al) count", Opt);

    /// <summary>Dimethyl labelling in a deposit's words, shared with the screen: "dimethyl label(l)ed", "dimethyl isotope
    /// labeling", "dimethylation labeling", "reductive dimethylation", "stable isotope dimethyl". Never DiLeu's "N,N-dimethyl
    /// leucine", which has no "label" right after it.</summary>
    internal const string DimethylLabel = @"\bdimethyl(ation)?[- ]((stable[- ])?isotop(e|es|ic)[- ])?label|reductive dimethyl|isotope dimethyl";

    /// <summary>An isotope-labelled dimethyl in PRIDE's identified PTMs, as PSI-MOD names it ("4x(2)H labeled dimethylated
    /// residue"). v0.4.16: PXD000572 says it nowhere else (its description names no label, its protocols are "Not
    /// available"), and was searched label-free: 0.2% of its spectra identified. A plain "dimethylated residue" is the
    /// natural PTM (dimethyl-arginine, -lysine) and is no label.</summary>
    private static readonly Regex LabelledDimethylPtm = new(@"labell?ed dimethylated", Opt);

    private static string? LabelIn(string text, out string? evidence)
    {
        foreach (var (name, pattern) in Labels)
            if (pattern.Match(text) is { Success: true } m) { evidence = Snip(text, m); return name; }
        evidence = null;
        return null;
    }

    // ------------------------------------------------------------------ the sources, combined

    /// <summary>What one SDRF says: its proteases (per file), alkylants and label; nulls when it says nothing.</summary>
    internal static (IReadOnlyDictionary<string, string>? ProteasePerFile, IReadOnlyList<string> Alkylants, string? Label, IReadOnlyList<CysMod> CysMods) FromSdrf(SdrfDocument doc)
    {
        var perFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var alkylants = new HashSet<string>(StringComparer.Ordinal);
        var cysMods = new Dictionary<string, CysMod>(StringComparer.Ordinal);
        string? label = null;
        foreach (var row in doc.Results)
        {
            string file = row["comment[data file]"]?.Trim() ?? "";
            var agents = row.All("comment[cleavage agent details]").Select(c => CanonicalProtease(Field(c, "NT") ?? c) ?? ProteaseByAccession(Field(c, "AC")))
                .OfType<string>().Distinct().ToList();
            if (agents.Count > 1 && agents.Contains(Trypsin)) agents.Remove(LysC);
            if (agents.Count == 1 && file.Length > 0) perFile[file] = agents[0];
            else if (agents.Count > 1 && file.Length > 0) perFile[file] = string.Join(" + ", agents);
            foreach (string cell in row.All("comment[modification parameters]"))
            {
                string nt = Field(cell, "NT") ?? "", ac = Field(cell, "AC") ?? "", ta = Field(cell, "TA") ?? "";
                if (ta.Length > 0 && !ta.Contains('C', StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var a in Alkylants.Where(a => a.Sdrf.IsMatch(nt) || a.Sdrf.IsMatch(ac))) { alkylants.Add(a.Name); break; }
                // Any cysteine modification the SDRF names by UNIMOD id, with the SDRF's own fixed or variable (oracle: the
                // id resolves through mzLib, so this is not limited to the alkylants listed above).
                if (ta.Contains('C', StringComparison.OrdinalIgnoreCase) && UnimodNumber(ac) is int id && MetaMorpheusModId($"UNIMOD:{id}", 'C') is not null)
                    cysMods[$"UNIMOD:{id}"] = new CysMod(nt.Length > 0 ? nt : $"UNIMOD:{id}", $"UNIMOD:{id}",
                        !string.Equals(Field(cell, "MT"), "variable", StringComparison.OrdinalIgnoreCase));
            }
            if (label is null && row["comment[label]"] is { Length: > 0 } l && !LabelFreeWords.IsMatch(l)) label = LabelIn(l, out _) ?? l.Trim();
        }
        // An isobaric label the label column states (TMT, TMTpro, iTRAQ), read by mzLib's auditor (oracle 2026-10-05).
        if (label is null && SdrfQuantAuditor.Audit(doc) is { Kind: not SdrfQuantKind.NotIsobaric } audit && audit.Families.Count > 0)
            label = audit.Families[0].StartsWith("itraq", StringComparison.OrdinalIgnoreCase) ? "itraq" : "tmt";
        return (perFile.Count > 0 ? perFile : null, alkylants.ToList(), label, cysMods.Values.ToList());
    }

    /// <summary>A protease named by its PSI-MS accession alone ("MS:1001251", or bare "1001251"), via mzLib's dictionary.</summary>
    private static string? ProteaseByAccession(string? ac)
    {
        if (ac is null || Regex.Match(ac, @"(\d{7})") is not { Success: true } m) return null;
        string acc = "MS:" + m.Groups[1].Value;
        return ProteaseDictionary.Dictionary.Values.Where(p => p.PsiMsAccessionNumber == acc).Select(p => CanonicalProtease(p.Name)).FirstOrDefault(n => n is not null);
    }

    /// <summary>The deposit's chemistry, from every source given, by D22's order. Files are the raw files to be searched.</summary>
    /// <param name="thiolReagents">The profile's <c>[thiol_reagents]</c> (G40): a reagent name from
    /// <see cref="UnmodelledThiolReagentNames"/> to the <c>Type\tIdWithMotif</c> of the profile's own modification for it.
    /// Null: every such reagent parks the deposit, as before.</param>
    public static DepositChemistry Detect(PrideProject? project, SdrfDocument? curated, SdrfDocument? deposited, IReadOnlyList<string> rawFiles,
        IReadOnlyDictionary<string, string>? thiolReagents = null)
    {
        var sdrfs = new[] { (curated, ChemistrySource.CuratedSdrf), (deposited, ChemistrySource.DepositedSdrf) }
            .Where(s => s.Item1 is not null).Select(s => (Read: FromSdrf(s.Item1!), Source: s.Item2)).ToList();
        string text = project is null ? "" : string.Join(" ", project.Title, project.ProjectDescription, project.SampleProcessingProtocol, project.DataProcessingProtocol);

        // Protease: an SDRF that names one per file, else the protocol text, else trypsin.
        ChemistryFact protease;
        IReadOnlyDictionary<string, string>? perFile = null;
        string? park = null;
        var fromSdrf = sdrfs.FirstOrDefault(s => s.Read.ProteasePerFile is not null);
        if (fromSdrf.Read.ProteasePerFile is { } map)
        {
            var distinct = map.Values.Distinct().ToList();
            protease = new(distinct.Count == 1 ? distinct[0] : "multiple", fromSdrf.Source, string.Join("; ", distinct));
            if (distinct.Count > 1) perFile = map;
        }
        else
        {
            var named = ProteasesInText(text);
            if (named.Count == 0) protease = new(Trypsin, ChemistrySource.Default);
            else if (named.Count == 1) protease = new(named[0].Name, ChemistrySource.ProtocolText, named[0].Evidence);
            else
            {
                protease = new("multiple", ChemistrySource.ProtocolText, string.Join(" | ", named.Select(n => n.Evidence)));
                perFile = ProteasePerFileFromNames(rawFiles);
                if (perFile is null) park = "waiting_multi_protease";
            }
        }

        // Alkylation: an SDRF's cysteine modifications, else PRIDE's identified PTMs, else the text, else carbamidomethyl.
        ChemistryFact alkylation;
        IReadOnlyList<CysMod> mods;
        var sdrfAlk = sdrfs.FirstOrDefault(s => s.Read.CysMods.Count > 0 || s.Read.Alkylants.Count > 0);
        var pridePtms = project?.IdentifiedPTMStrings.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        var fromPride = Alkylants.Where(a => pridePtms.Any(p => a.Pride.IsMatch(p))).Select(a => a.Name).ToList();
        // NEM in a lysis buffer is a deubiquitinase inhibitor (PXD047288 and eight siblings, ubiquitin studies), not the
        // alkylation: in the text, NEM counts only near an alkylation, blocking, thiol or cysteine word.
        var fromText = Alkylants.Select(a => (a.Name, M: a.Text.Matches(text).FirstOrDefault(m => !a.Name.StartsWith("N", StringComparison.Ordinal)
                || AlkylationContext.IsMatch(Window(text, m, 80))))).Where(x => x.M is not null).Select(x => (x.Name, M: x.M!)).ToList();
        // aging 043 (PXR-A37, the user's D-3, 2026-10-07): NEM named ANYWHERE with no other alkylant named IS the alkylant.
        // 70 mM NEM in a denaturing lysis buffer caps every free cysteine (the ten Jarome deposits are 0.2-3.2% Cys). Searched
        // VARIABLE: cysteines disulfide-bonded at lysis escape it.
        Match? lysisNem = fromText.Count == 0 && Alkylants.First(a => a.Name == "Nethylmaleimide").Text.Match(text) is { Success: true } nem ? nem : null;
        if (sdrfAlk.Read.CysMods is { Count: > 0 } sm)
        {
            mods = sm;   // as the SDRF states them, fixed or variable
            alkylation = new(string.Join(" + ", sm.Select(m => m.Name)), sdrfAlk.Source);
        }
        else if (sdrfAlk.Read.Alkylants is { Count: > 0 } sa)
        {
            mods = CysModsFor(sa);
            alkylation = new(string.Join(" + ", sa), sdrfAlk.Source);
        }
        else if (fromPride.Count > 0)
        {
            mods = CysModsFor(fromPride);
            alkylation = new(string.Join(" + ", fromPride), ChemistrySource.PridePtm, string.Join("; ", pridePtms.Where(p => Alkylants.Any(a => a.Pride.IsMatch(p)))));
        }
        else if (NoAlkylation.Match(text) is { Success: true } none)
        {
            mods = Array.Empty<CysMod>();
            alkylation = new("none", ChemistrySource.ProtocolText, Snip(text, none));
        }
        else if (fromText.Count > 0)
        {
            mods = CysModsFor(fromText.Select(x => x.Name).ToList());
            alkylation = new(string.Join(" + ", fromText.Select(x => x.Name)), ChemistrySource.ProtocolText, string.Join(" | ", fromText.Select(x => Snip(text, x.M))));
        }
        else if (lysisNem is not null)
        {
            var a = Alkylants.First(x => x.Name == "Nethylmaleimide");
            mods = new[] { new CysMod(a.Name, a.Unimod, false) };
            alkylation = new(a.Name, ChemistrySource.ProtocolText, $"the only alkylant named, outside an alkylation step; variable (aging PXR-A37): {Snip(text, lysisNem)}");
        }
        else if (UnmodelledThiolReagents.Select(r => (r.Name, M: r.Text.Match(text))).FirstOrDefault(x => x.M.Success) is { M: not null } unmodelled)
        {
            if (thiolReagents?.TryGetValue(unmodelled.Name, out string? custom) == true)
            {
                // G40: the profile ships a modification for this reagent. Variable, never fixed: the PXD058611 probe found
                // 14-24% of identified cysteines unmodified (results/nbf-probe_2026-10-11).
                mods = new[] { new CysMod(CustomModName(custom), "", false, custom) };
                alkylation = new(unmodelled.Name, ChemistrySource.ProtocolText,
                    $"a thiol reagent the profile models as variable {custom.Replace('\t', ' ')}: {Snip(text, unmodelled.M)}");
            }
            else
            {
                mods = Array.Empty<CysMod>();
                alkylation = new(unmodelled.Name, ChemistrySource.ProtocolText, $"a thiol reagent with no modelled modification: {Snip(text, unmodelled.M)}");
                park ??= "waiting_unknown_alkylant";
            }
        }
        else
        {
            mods = new[] { Carbamidomethyl };
            alkylation = new("Carbamidomethyl", ChemistrySource.Default);
        }
        // PRIDE lists light NEM only (PXD001054: "Nethylmaleimide"); a protocol that names d5-NEM makes it the D23 pair.
        if (mods.Any(m => m.Name == "Nethylmaleimide") && mods.All(m => m.Name != "NEM:2H(5)")
            && Alkylants.First(a => a.Name == "NEM:2H(5)").Text.Match(text) is { Success: true } heavy)
        {
            mods = CysModsFor(mods.Select(m => m.Name).Append("NEM:2H(5)").ToList());
            alkylation = alkylation with { Value = alkylation.Value + " + NEM:2H(5)", Evidence = $"{alkylation.Evidence} | heavy partner from the protocol: {Snip(text, heavy)}" };
        }

        // Label: an SDRF's comment[label], else PRIDE's quantification methods, else an isotope-labelled dimethyl among
        // PRIDE's identified PTMs, else the text, else label-free.
        // v0.4.25 (PXD022954, PXD022956): a community-annotated SDRF said "NT=isotope labeling;AC=PRIDE:0000310" on every row,
        // a term that names no label, while PRIDE's record said MS1 label-free and spectrum counting; the deposit was parked
        // under a status made of the cell's text. A label cell naming no label we know decides nothing: a curated SDRF, PRIDE's
        // quantification methods (a label, or label-free), PRIDE's PTMs or the text decide, and with none of them the deposit
        // waits for a person.
        ChemistryFact label;
        var quant = project?.QuantificationMethods.Select(q => q.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        string? quantLabel = quant.Select(q => LabelIn(q, out _)).FirstOrDefault(l => l is not null);
        var quantFree = quant.Where(q => LabelFreeWords.IsMatch(q) || SpectralCounting.IsMatch(q)).ToList();
        var dimethylPtms = pridePtms.Where(p => LabelledDimethylPtm.IsMatch(p)).ToList();
        var sdrfLabel = sdrfs.FirstOrDefault(s => s.Read.Label is not null);
        string? unrecognised = sdrfLabel.Read.Label is { } raw && Labels.All(l => l.Name != raw) ? raw : null;
        string unrecognisedNote = unrecognised is null ? "" : $"; the SDRF's comment[label] \"{unrecognised}\" names no label";
        if (sdrfLabel.Read.Label is { } sl && unrecognised is null) label = new(sl, sdrfLabel.Source);
        else if (unrecognised is not null && curated is not null && sdrfLabel.Source != ChemistrySource.CuratedSdrf)
            label = new("label_free", ChemistrySource.CuratedSdrf, "the curated SDRF names no label" + unrecognisedNote);
        else if (quantLabel is not null) label = new(quantLabel, ChemistrySource.PrideQuant, string.Join("; ", quant) + unrecognisedNote);
        else if (unrecognised is not null && quantFree.Count > 0) label = new("label_free", ChemistrySource.PrideQuant, string.Join("; ", quantFree) + unrecognisedNote);
        else if (dimethylPtms.Count > 0) label = new("dimethyl", ChemistrySource.PridePtm, string.Join("; ", dimethylPtms) + unrecognisedNote);
        else if (LabelIn(text, out string? ev) is { } tl) label = new(tl, ChemistrySource.ProtocolText, ev + unrecognisedNote);
        else if (unrecognised is not null)
        {
            label = new(UnknownLabel, sdrfLabel.Source, unrecognised);
            park ??= UnknownLabelPark;
        }
        else label = new("label_free", ChemistrySource.Default);

        // Every protease name as the pinned mzLib spells it (ProteaseDictionary). A per-file map is searched through
        // MetaMorpheus's file-specific parameters (the user, 2026-10-05: calibration carries the protease into X-calib.toml).
        // A row with two proteases at once, or a protease mzLib does not know, waits.
        if (perFile is not null)
        {
            var resolved = perFile.ToDictionary(kv => kv.Key, kv => ResolveProtease(kv.Value), StringComparer.Ordinal);
            if (resolved.Values.Any(v => v is null)) park ??= "waiting_multi_protease";
            else perFile = resolved.ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
        }
        else if (ResolveProtease(protease.Value) is { } name) protease = protease with { Value = name };
        else park ??= "waiting_unknown_protease";
        // v0.4.19 (PXD036532, pepsin): MetaMorpheus's non-specific protease in a Classic search makes no peptide of the
        // profile's minimum length, so the probe found 0 PSMs in 25 s. Such a deposit waits, as an immunopeptidome does,
        // for a profile that searches non-specific cleavage (a capability, not a person's decision).
        if (protease.Value == NonSpecific || perFile?.Values.Contains(NonSpecific) == true) park ??= NonspecificCleavagePark;
        if (mods.Any(m => m.MetaMorpheusId is null)) park ??= "waiting_unknown_modification";

        return new DepositChemistry(protease, perFile, alkylation, mods, label, park);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An SDRF key=value cell's field, e.g. NT in "NT=Carbamidomethyl;AC=UNIMOD:4;TA=C;MT=Fixed".</summary>
    internal static string? Field(string cell, string key) =>
        cell.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) is { } kv
            ? kv[(key.Length + 1)..].Trim() : null;

    private static string Window(string text, Match m, int around) =>
        text[Math.Max(0, m.Index - around)..Math.Min(text.Length, m.Index + m.Length + around)];

    private static string Snip(string text, Match m) => AcquisitionClassifier.Snippet(text, m);
}
