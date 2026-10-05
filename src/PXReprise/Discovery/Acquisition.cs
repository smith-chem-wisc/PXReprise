using System.Text.RegularExpressions;
using UsefulProteomicsDatabases;

namespace PXReprise.Discovery;

public enum AcquisitionMode { Dda, Dia }

/// <summary>
/// <see cref="Mixed"/>: the record names both an isobaric and a metabolic label (e.g. PXD047864, PXD054682). The text
/// cannot say which the search needs, so no profile takes it and it waits for a hand decision.
/// <see cref="O18"/>: enzymatic 16O/18O labelling at the C-terminus (PXD028282, aging 017); searched label-free, most
/// spectra carry a mass shift no label-free profile looks for.
/// </summary>
public enum Labelling { LabelFree, Isobaric, Metabolic, O18, Mixed }

public enum InstrumentClass { OrbitrapHcdOnly, OrbitrapHybrid, Astral, Timstof, ThermoLowRes, Sciex, Waters, BrukerOther, Unknown }

/// <summary>
/// What a deposit's acquisition is, as far as its PRIDE record says. Routing reads this; nothing else does.
/// <see cref="Crosslinked"/>: crosslinking MS (XL-MS). Its peptides are linked pairs, which a linear search cannot
/// identify (PXD062841: calibration failed on 35 of 35 runs, aging 017).
/// <see cref="NonspecificCleavage"/>: the peptides were not made by a protease the profiles search with: MHC/HLA
/// immunopeptidomes and endogenous peptidomes (PXD034059 and PXD058775: about 0.0015 identification rate under a tryptic
/// search, aging 030).
/// </summary>
public sealed record Acquisition(
    AcquisitionMode Mode,
    Labelling Labelling,
    InstrumentClass Instrument,
    string Enrichment,
    IReadOnlyDictionary<string, int> MsFiles,
    string? Evidence,
    bool Crosslinked = false,
    bool NonspecificCleavage = false)
{
    public int MsFileCount => MsFiles.Values.Sum();
}

/// <summary>
/// Classifies a PRIDE record's acquisition from its free text and instrument list.
///
/// The patterns are the aging pipeline's (`pipeline/params.json`, discover block, 2026-09-27), so "DIA" and "labelled"
/// mean the same thing here as in the batch that produced the aging corpus. They are policy, not library code: the
/// oracle of 2026-09-27 put free-text heuristics in the app and a CV-accession mapping (instrument model to class) in
/// mzLib, which does not exist yet (PXReprise G1b). When it lands, <see cref="ClassifyInstrument"/> moves onto it.
/// </summary>
public static class AcquisitionClassifier
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Dia = new(string.Join("|", new[]
        { "data-independent", "DIA-NN", "DIANN", "SWATH", "Spectronaut", "diaPASEF", "dia-PASEF", @"\bDIA\b", "Astral" }), Opt);

    private static readonly Regex Isobaric = new(@"\bTMT|tandem mass tag|iTRAQ|isobaric|TMTpro", Opt);

    private static readonly Regex Metabolic = new(string.Join("|", new[]
    {
        "SILAC", "dimethyl label", @"heavy[- ]?(labell?ed )?water", @"\bD2O\b", "deuterium", "deuterated water", @"\b15N\b",
        @"pulsed?[- ]?SILAC", @"metabolic(ally)? label", @"\b13C6\b", "heavy (lysine|arginine)",
    }), Opt);

    // Aging 017 (PXR-A11). PXD028282's keywords say "O18 labelling"; its authors searched "Label 18O (1 or 2) C-term".
    private static readonly Regex O18 = new(@"\b(16O/)?18O\b|\bO-?18\b|oxygen-18|H2 ?18O", Opt);

    // Aging 017 (PXR-A11). The names that only mean crosslinking MS are enough alone. DSS (also dextran sulfate sodium,
    // a colitis model) and PIR (also the Protein Information Resource) count only within 80 characters of a crosslinking
    // word. "Crosslink" alone never counts: hydrogels are crosslinked, and ChIP samples are crosslinked with formaldehyde.
    private static readonly Regex CrosslinkingMs = new(string.Join("|", new[]
    {
        @"\bXL-?MS\b", @"cross-?link(ing|ed)?[- ]mass spectrometry", @"cross-?linking[- ]MS\b", @"\biqPIR\b", @"\bDSSO\b",
        @"\bDSBU\b", @"\bBS3\b", @"\bcross-?linked peptides\b",
    }), Opt);
    private static readonly Regex AmbiguousReagent = new(@"\b(DSS|PIR)\b", Opt);

    // Aging 030 (PXR-A23). PXD034059 (MHC class I, hupo-hipp tag) and PXD058775 (MHC-II) passed the screen and were
    // searched as tryptic. MHC/HLA counts only when it names peptides or ligands: "MHC class I expression" is proteome
    // biology. The enzyme phrases are how authors say they searched without one.
    private static readonly Regex NonspecificCleavageText = new(string.Join("|", new[]
    {
        @"immuno-?peptidom", @"\bligandom", @"\bpeptidom(e|ics)\b", @"endogenous peptides",
        @"\b(MHC|HLA)[- ]?(class[- ]?)?(II|I|1|2)?[- ]?(-?(bound|associated|presented|eluted)[- ])?(peptide|ligand)",
        @"unspecified (enzyme|cleavage|peptide cleavage|digestion)", @"\bno[- ]enzyme\b", @"enzyme:? ?(unspecific|unspecified|none)\b",
        @"non-?specific (enzyme|cleavage|digestion)", @"unspecific (cleavage|digestion)",
    }), Opt);
    private static readonly Regex NonspecificCleavageTag = new(@"hupo-hipp|immuno-?peptidom", Opt);
    private static readonly Regex CrosslinkingWord = new(@"cross-?link", Opt);

    private static readonly Regex Enriched = new(string.Join("|", new[]
    {
        "immunoprecipitat", @"\bco-?IP\b", @"\bIP-MS\b", "pull-?down", "(?<!without the use of )(?<!without )affinity (purif|enrich|capture)",
        "streptavidin", @"\bBioID\b", @"\bTurboID\b", @"\bAPEX2\b|\bAPEX[- ](based|mediated|tagged|fusion|labell?ing|proximity)",
        @"proximity[- ]labell?ing", "kinobead", @"phospho(peptide)?[- ]?enrich", @"\bTiO2\b", @"\bIMAC\b", "Fe-NTA", @"\bK-?GG\b",
        @"di-?gly(cine)? remnant", "ubiquitin remnant", @"glyco(peptide)?[- ]?enrich", "crosslinking mass spectrometry",
        "cross-linking mass spectrometry", @"\bXL-MS\b", @"\bDSSO\b", "interactome", "GFP-?trap", @"\bLyso-?IP\b",
        "organelle (isolation|purification|enrichment)",
    }), Opt);

    // dataRepo's Enrichment vocabulary. ORDER MATTERS: the first kind whose pattern appears anywhere wins. PTM
    // enrichments come first; proximity labelling and chemical probes precede affinity purification because they ARE
    // streptavidin pulldowns (aging discover.py).
    private static readonly (string Kind, Regex Pattern)[] EnrichmentKinds =
    {
        ("phospho", new(@"phospho(peptide)?[- ]?enrich|\btio2\b|\bimac\b|fe-nta", Opt)),
        ("ubiquitin_GG", new(@"\bk-?gg\b|di-?gly(cine)? remnant|ubiquitin remnant", Opt)),
        ("glyco", new(@"glyco(peptide)?[- ]?enrich|lectin", Opt)),
        ("proximity_labelling", new(@"\bturboid\b|\bbioid\b|\bapex2\b|proximity[- ]labell?ing", Opt)),
        ("chemical_probe", new(@"kinobead|chemical probe|activity-based probe|\bdcp-?bio", Opt)),
        ("immunoprecipitation", new(@"immunoprecipitat|\bco-?ip\b|\bip-ms\b|\blyso-?ip\b", Opt)),
        ("affinity_purification", new(@"affinity (purif|enrich|capture)|pull-?down|gfp-?trap|streptavidin|\bflag\b", Opt)),
    };

    private static readonly Regex OrbitrapOnly = new("Q Exactive|Exploris", Opt);
    private static readonly Regex Hybrid = new("Velos|Elite|Fusion|Lumos|Eclipse|Orbitrap XL|Orbitrap Tribrid|Orbitrap", Opt);
    private static readonly Regex Thermo = new("Q Exactive|Orbitrap|Exploris|Fusion|Lumos|Eclipse|LTQ|Velos|Elite", Opt);

    private static readonly string[] MsExtensions = { ".raw", ".d.zip", ".d.tar", ".wiff", ".mzml", ".mgf", ".tdf", ".d" };

    /// <summary>Every free-text field PRIDE gives for a project (the protocols alone miss labelling, aging S46).</summary>
    public static string Text(PrideProjectSearchResult r) => string.Join(" ", new[]
        { r.Title, r.ProjectDescription, r.SampleProcessingProtocol, r.DataProcessingProtocol }
        .Concat(r.Keywords).Concat(r.ExperimentTypes).Concat(r.QuantificationMethods));

    public static Acquisition Classify(PrideProjectSearchResult r)
    {
        string text = Text(r);
        var dia = Dia.Match(text);
        var iso = Isobaric.Match(text);
        var met = Metabolic.Match(text);
        var o18 = O18.Match(text);
        var labels = new[] { (iso, Labelling.Isobaric), (met, Labelling.Metabolic), (o18, Labelling.O18) }.Where(l => l.Item1.Success).ToList();
        var labelling = labels.Count switch { 0 => Labelling.LabelFree, 1 => labels[0].Item2, _ => Labelling.Mixed };
        var xl = Crosslink(text);
        var nonspecific = NonspecificCleavageText.Match(text);
        string? tag = r.ProjectTags.FirstOrDefault(t => NonspecificCleavageTag.IsMatch(t));
        string enrichment = Enriched.IsMatch(text)
            ? EnrichmentKinds.FirstOrDefault(k => k.Pattern.IsMatch(text)).Kind ?? "other"
            : "none";
        var evidence = new[] { nonspecific, dia, xl ?? Match.Empty }.Concat(labels.Select(l => l.Item1)).FirstOrDefault(m => m.Success);
        return new Acquisition(
            dia.Success ? AcquisitionMode.Dia : AcquisitionMode.Dda,
            labelling,
            ClassifyInstrument(r.Instruments),
            enrichment,
            CountMsFiles(r.ProjectFileNames),
            evidence is not null ? Snippet(text, evidence) : tag is not null ? $"project tag: {tag}" : null,
            xl is not null,
            nonspecific.Success || tag is not null);
    }

    /// <summary>The text that says the deposit is crosslinking MS, or null.</summary>
    internal static Match? Crosslink(string text)
    {
        var strong = CrosslinkingMs.Match(text);
        if (strong.Success) return strong;
        return AmbiguousReagent.Matches(text).FirstOrDefault(m =>
        {
            int a = Math.Max(0, m.Index - 80), b = Math.Min(text.Length, m.Index + m.Length + 80);
            return CrosslinkingWord.IsMatch(text[a..b]);
        });
    }

    public static InstrumentClass ClassifyInstrument(IEnumerable<string> instruments)
    {
        string s = string.Join(" ", instruments);
        if (Regex.IsMatch(s, @"timstof|tims tof|\btims\b", Opt)) return InstrumentClass.Timstof;
        if (Regex.IsMatch(s, "astral", Opt)) return InstrumentClass.Astral;
        if (OrbitrapOnly.IsMatch(s)) return InstrumentClass.OrbitrapHcdOnly;
        if (Hybrid.IsMatch(s)) return InstrumentClass.OrbitrapHybrid;
        if (Thermo.IsMatch(s)) return InstrumentClass.ThermoLowRes;
        if (Regex.IsMatch(s, @"sciex|triple ?tof|qtrap|zenotof", Opt)) return InstrumentClass.Sciex;
        if (Regex.IsMatch(s, "waters|synapt|xevo", Opt)) return InstrumentClass.Waters;
        if (Regex.IsMatch(s, "bruker|maxis|impact|ultraflex|rapiflex", Opt)) return InstrumentClass.BrukerOther;
        return InstrumentClass.Unknown;
    }

    public static IReadOnlyDictionary<string, int> CountMsFiles(IEnumerable<string> names)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string n in names)
        {
            string lower = n.ToLowerInvariant();
            string? ext = MsExtensions.FirstOrDefault(e => lower.EndsWith(e, StringComparison.Ordinal));
            if (ext is not null) counts[ext] = counts.GetValueOrDefault(ext) + 1;
        }
        return counts;
    }

    internal static string Snippet(string text, Match m)
    {
        int a = Math.Max(0, m.Index - 40), b = Math.Min(text.Length, m.Index + m.Length + 40);
        return Regex.Replace(text[a..b], @"\s+", " ").Trim();
    }
}
