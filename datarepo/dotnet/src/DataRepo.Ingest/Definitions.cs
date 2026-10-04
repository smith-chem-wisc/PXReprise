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
