using System.Text.RegularExpressions;
using UsefulProteomicsDatabases;

namespace PXReprise.Discovery;

public enum AcquisitionMode { Dda, Dia }

/// <summary>
/// <see cref="Mixed"/>: the record names both an isobaric and a metabolic label (e.g. PXD047864, PXD054682). The text
/// cannot say which the search needs, so no profile takes it and it waits for a hand decision.
/// </summary>
public enum Labelling { LabelFree, Isobaric, Metabolic, Mixed }

public enum InstrumentClass { OrbitrapHcdOnly, OrbitrapHybrid, Astral, Timstof, ThermoLowRes, Sciex, Waters, BrukerOther, Unknown }

/// <summary>What a deposit's acquisition is, as far as its PRIDE record says. Routing reads this; nothing else does.</summary>
public sealed record Acquisition(
    AcquisitionMode Mode,
    Labelling Labelling,
    InstrumentClass Instrument,
    string Enrichment,
    IReadOnlyDictionary<string, int> MsFiles,
    string? Evidence)
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
        var labelling = (iso.Success, met.Success) switch
        {
            (true, true) => Labelling.Mixed,
            (true, false) => Labelling.Isobaric,
            (false, true) => Labelling.Metabolic,
            _ => Labelling.LabelFree,
        };
        string enrichment = Enriched.IsMatch(text)
            ? EnrichmentKinds.FirstOrDefault(k => k.Pattern.IsMatch(text)).Kind ?? "other"
            : "none";
        var evidence = new[] { dia, iso.Success ? iso : met }.FirstOrDefault(m => m.Success);
        return new Acquisition(
            dia.Success ? AcquisitionMode.Dia : AcquisitionMode.Dda,
            labelling,
            ClassifyInstrument(r.Instruments),
            enrichment,
            CountMsFiles(r.ProjectFileNames),
            evidence is null ? null : Snippet(text, evidence));
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
