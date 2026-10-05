using System.Text.RegularExpressions;
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
}

/// <summary>One fact (a protease, an alkylation, a label), where it came from, and the words it was read from.</summary>
public sealed record ChemistryFact(string Value, string Source, string? Evidence = null)
{
    public bool Guessed => Source == ChemistrySource.ProtocolText;
}

/// <summary>A cysteine modification as UniMod names it, and whether the search should hold it fixed or variable.</summary>
public sealed record CysMod(string Name, string Unimod, bool Fixed);

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
    };

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
        ("dimethyl", new(@"dimethyl label|reductive dimethyl|stable isotope dimethyl", Opt)),
        ("o18", new(@"\b(16O/)?18O\b|\bO-?18\b|oxygen-18|H2 ?18O", Opt)),
        ("dileu", new(@"\bi?DiLeu\b|N,N-dimethyl(ated)? leucine", Opt)),
        ("heavy_water", new(@"heavy[- ]?(labell?ed )?water|\bD2O\b|deuterated water", Opt)),
        ("15n", new(@"\b15N\b", Opt)),
    };
    private static readonly Regex LabelFreeWords = new(@"label[- ]?free", Opt);

    private static string? LabelIn(string text, out string? evidence)
    {
        foreach (var (name, pattern) in Labels)
            if (pattern.Match(text) is { Success: true } m) { evidence = Snip(text, m); return name; }
        evidence = null;
        return null;
    }

    // ------------------------------------------------------------------ the sources, combined

    /// <summary>What one SDRF says: its proteases (per file), alkylants and label; nulls when it says nothing.</summary>
    internal static (IReadOnlyDictionary<string, string>? ProteasePerFile, IReadOnlyList<string> Alkylants, string? Label) FromSdrf(SdrfDocument doc)
    {
        var perFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var alkylants = new HashSet<string>(StringComparer.Ordinal);
        string? label = null;
        foreach (var row in doc.Results)
        {
            string file = row["comment[data file]"]?.Trim() ?? "";
            var agents = row.All("comment[cleavage agent details]").Select(c => CanonicalProtease(Field(c, "NT") ?? c)).OfType<string>().Distinct().ToList();
            if (agents.Count > 1 && agents.Contains(Trypsin)) agents.Remove(LysC);
            if (agents.Count == 1 && file.Length > 0) perFile[file] = agents[0];
            else if (agents.Count > 1 && file.Length > 0) perFile[file] = string.Join(" + ", agents);
            foreach (string cell in row.All("comment[modification parameters]"))
            {
                string nt = Field(cell, "NT") ?? "", ac = Field(cell, "AC") ?? "", ta = Field(cell, "TA") ?? "";
                if (ta.Length > 0 && !ta.Contains('C', StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var a in Alkylants.Where(a => a.Sdrf.IsMatch(nt) || a.Sdrf.IsMatch(ac))) { alkylants.Add(a.Name); break; }
            }
            if (label is null && row["comment[label]"] is { Length: > 0 } l && !LabelFreeWords.IsMatch(l)) label = LabelIn(l, out _) ?? l.Trim();
        }
        return (perFile.Count > 0 ? perFile : null, alkylants.ToList(), label);
    }

    /// <summary>The deposit's chemistry, from every source given, by D22's order. Files are the raw files to be searched.</summary>
    public static DepositChemistry Detect(PrideProject? project, SdrfDocument? curated, SdrfDocument? deposited, IReadOnlyList<string> rawFiles)
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
        var sdrfAlk = sdrfs.FirstOrDefault(s => s.Read.Alkylants.Count > 0);
        var pridePtms = project?.IdentifiedPTMStrings.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        var fromPride = Alkylants.Where(a => pridePtms.Any(p => a.Pride.IsMatch(p))).Select(a => a.Name).ToList();
        // NEM in a lysis buffer is a deubiquitinase inhibitor (PXD047288 and eight siblings, ubiquitin studies), not the
        // alkylation: in the text, NEM counts only near an alkylation, blocking, thiol or cysteine word.
        var fromText = Alkylants.Select(a => (a.Name, M: a.Text.Matches(text).FirstOrDefault(m => !a.Name.StartsWith("N", StringComparison.Ordinal)
                || AlkylationContext.IsMatch(Window(text, m, 80))))).Where(x => x.M is not null).Select(x => (x.Name, M: x.M!)).ToList();
        if (sdrfAlk.Read.Alkylants is { Count: > 0 } sa)
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

        // Label: an SDRF's comment[label], else PRIDE's quantification methods, else the text, else label-free.
        ChemistryFact label;
        var quant = project?.QuantificationMethods.Select(q => q.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
        string? quantLabel = quant.Select(q => LabelIn(q, out _)).FirstOrDefault(l => l is not null);
        if (sdrfs.FirstOrDefault(s => s.Read.Label is not null) is { Read.Label: { } sl } sdrfLabel) label = new(sl, sdrfLabel.Source);
        else if (quantLabel is not null) label = new(quantLabel, ChemistrySource.PrideQuant, string.Join("; ", quant));
        else if (LabelIn(text, out string? ev) is { } tl) label = new(tl, ChemistrySource.ProtocolText, ev);
        else label = new("label_free", ChemistrySource.Default);

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
