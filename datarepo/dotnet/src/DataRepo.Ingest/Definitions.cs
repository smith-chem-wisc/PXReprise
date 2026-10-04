namespace DataRepo.Ingest;

/// <summary>A metric definition: the owner's text, copied word for word, under the owner's id.</summary>
public sealed record Def(string DefinitionId, string Version, string OwnerProject, string Text, string? Url = null)
{
    /// <summary>The <c>definitions</c> table row.</summary>
    public DataRepo.Bundle.Row Row() => new()
    {
        ["definition_id"] = DefinitionId,
        ["version"] = Version,
        ["owner_project"] = OwnerProject,
        ["text"] = Text,
        ["url"] = Url,
    };
}

/// <summary>Metric definitions, copied from the registers that own them.</summary>
/// <remarks>
/// Every number in the repository carries a definition id, and dataRepo owns none of them: metric
/// definitions belong to QuantProject, the pipeline's own counts to aging and, from D37, to PXReprise.
/// What is stored here is a copy of the owner's text, so a bundle can be read without reaching back into
/// another project's repository. Ids are namespaced <c>&lt;owner&gt;:&lt;ID&gt;</c>, because two registers
/// use the same short codes. The texts are in <c>Definitions.Texts.cs</c>; ported from <c>definitions.py</c>.
/// </remarks>
public static partial class Definitions
{
    /// <summary>pep's DEF-PEP. Its version is per bundle: there is no PEP method identifier, and the model
    /// is retrained on every search (pep 002).</summary>
    public const string PepId = "pep:DEF-PEP";

    /// <summary>The namespaces a provenance record may declare in its <c>definitions</c> field (D37).</summary>
    public static readonly IReadOnlyList<string> Namespaces = ["aging", "pxreprise"];

    /// <summary>The namespace of a record that declares none: every record written before PXReprise's switch.</summary>
    public const string DefaultNamespace = "aging";

    private static readonly Lazy<IReadOnlyList<Def>> AllLazy = new(() =>
    [
        Psm1pct, NotchAmbiguous, PsmFdrEngine, IdRate, Mbr, PeptideIntensity, ProteinIntensity,
        ProteinSpectralCount, Occupancy, PeptideCount1pct, ProteinGroupCount1pct, Ms2Count, RunMinutes,
        PrecursorCount, ContamPsmShare, ContamIntensityShare,
        .. PxrepriseTwins.Values,
    ]);

    /// <summary>Every definition a bundle can carry, in the order bundles list them.</summary>
    public static IReadOnlyList<Def> All => AllLazy.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, Def>> TwinsLazy = new(() =>
        new Dictionary<string, Def>(StringComparer.Ordinal)
        {
            [Psm1pct.DefinitionId] = PxrPsm1pct,
            [NotchAmbiguous.DefinitionId] = PxrNotchAmbiguous,
            [PsmFdrEngine.DefinitionId] = PxrPsmFdrEngine,
            [PeptideCount1pct.DefinitionId] = PxrPeptideCount1pct,
            [ProteinGroupCount1pct.DefinitionId] = PxrProteinGroupCount1pct,
            [IdRate.DefinitionId] = PxrIdRate,
            [Ms2Count.DefinitionId] = PxrMs2Count,
            [RunMinutes.DefinitionId] = PxrRunMinutes,
            [PrecursorCount.DefinitionId] = PxrPrecursorCount,
            [ContamPsmShare.DefinitionId] = PxrContamPsmShare,
        });

    /// <summary>aging id to its pxreprise twin. The two never differ in meaning (D37).</summary>
    public static IReadOnlyDictionary<string, Def> PxrepriseTwins => TwinsLazy.Value;

    /// <summary>The definitions by id.</summary>
    public static IReadOnlyDictionary<string, Def> ById => All.ToDictionary(d => d.DefinitionId, StringComparer.Ordinal);

    /// <summary>The id a number defined by <paramref name="definition"/> carries when its record declares
    /// <paramref name="ns"/>. Only aging's ten pipeline counts have twins; every other definition is cited by
    /// its own id whatever the namespace, because PXReprise never redefines another project's number.</summary>
    /// <exception cref="ArgumentException">A namespace this ingester has not been taught.</exception>
    public static string Cite(Def definition, string ns)
    {
        if (!Namespaces.Contains(ns))
            throw new ArgumentException($"definitions namespace '{ns}' is not one of [{string.Join(", ", Namespaces)}]");
        if (ns == "pxreprise" && PxrepriseTwins.TryGetValue(definition.DefinitionId, out var twin))
            return twin.DefinitionId;
        return definition.DefinitionId;
    }

    /// <summary>Definition rows for a bundle; <paramref name="used"/> restricts them to the ids the bundle cites.</summary>
    public static List<DataRepo.Bundle.Row> Rows(ISet<string>? used = null) =>
        All.Where(d => used is null || used.Contains(d.DefinitionId)).Select(d => d.Row()).ToList();

    /// <summary>The v3.6 replacements in QuantProject's occupancy text (QuantProject 009, 2026-10-04): DEF-OCC-ABSENT
    /// corrected at every shipped version, DEF-OCC-COUNT amended, DEF-OCC-COVERED-ZERO added. Everything else is the
    /// v3.2 copy, word for word; <see cref="OccupancyV36"/> refuses to build if a replacement stops matching.</summary>
    internal static readonly (string Old, string New)[] OccupancyV36Edits =
    [
        ("DEF-OCC-CELL (v3, grammar superseded by v3.2) with ",
         "DEF-OCC-CELL (v3, grammar superseded by v3.2; DEF-OCC-ABSENT corrected and DEF-OCC-COUNT amended by v3.6, "
         + "DEF-OCC-COVERED-ZERO added by v3.6) with "),
        ("Written for every site with at least one modified PSM. ",
         "Written for every site with at least one modified PSM; and, only in output from an mzLib carrying #1411 and a "
         + "MetaMorpheus that opts in, at 0/N for every covered site where that modification was seen in some sample "
         + "group of the search (DEF-OCC-COVERED-ZERO). "),
        ("absent (no entry: no modified form seen -- NA, never 0, DEF-OCC-ABSENT). ",
         "covered-zero (count entry with numerator 0: covered and not modified, a MEASURED zero, DEF-OCC-COVERED-ZERO; "
         + "its intensity entry, if any, prints 0.0000(0/I) exactly like a floor, so only the count numerator tells them "
         + "apart); absent (no entry: not covered, or, in shipped output, covered and not modified -- the file cannot "
         + "tell which, and SpectralCount_ > 0 is no substitute for coverage -- NA, never 0, DEF-OCC-ABSENT v3.6). "),
    ];

    private static readonly Lazy<Def> OccupancyV36Lazy = new(() =>
    {
        var text = Occupancy.Text;
        foreach (var (old, replacement) in OccupancyV36Edits)
        {
            if (!text.Contains(old, StringComparison.Ordinal))
                throw new InvalidOperationException($"QuantProject's v3.2 occupancy text no longer contains {old}");
            text = text.Replace(old, replacement, StringComparison.Ordinal);
        }
        text = text.Replace("[Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5.]",
            "[Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5; v3.6 from QuantProject thread 009, 2026-10-04.]",
            StringComparison.Ordinal);
        return Occupancy with { Version = "v3.6", Text = text };
    });

    /// <summary>QuantProject's occupancy definition at v3.6 (thread 009), carried by bundles written under
    /// <see cref="IngestRules.Current"/>; a <see cref="IngestRules.Python0320"/> bundle keeps the v3.2 copy.</summary>
    public static Def OccupancyV36 => OccupancyV36Lazy.Value;

    /// <summary>DEF-PEP for one search, versioned by the release and regime that produced its PEP values.</summary>
    /// <param name="iterative"><c>on</c>, <c>off</c> or <c>not recorded</c> (pep 005, G81); null keeps the
    /// 0.32.0 key, which has no <c>iterative</c> part.</param>
    public static Def PepDefinition(string? release, string? regime, string? iterative = null)
    {
        var version = $"MetaMorpheus {release ?? "release not recorded"}; regime {regime ?? "not recorded"}";
        if (iterative is not null) version += $"; iterative {iterative}";
        return new Def(PepId, version, "pep", PepText);
    }
}
