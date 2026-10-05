using PXReprise.Discovery;

namespace PXReprise.Config;

public enum ProfileStatus { Available, Pending }

/// <summary>
/// What a profile accepts. An empty list means "any value". <see cref="Crosslinking"/> is opt-in (<c>crosslinking =
/// true</c>): no profile searches linked peptides, so none takes an XL-MS deposit unless it says so. So is
/// <see cref="NonspecificCleavage"/> (<c>nonspecific_cleavage = true</c>): every profile searches tryptic peptides. And
/// <see cref="TopDown"/> (<c>top_down = true</c>): every profile searches digested peptides, not intact proteins (D19).
/// </summary>
public sealed record ProfileAccepts(
    IReadOnlyList<AcquisitionMode> Modes,
    IReadOnlyList<Labelling> Labellings,
    IReadOnlyList<InstrumentClass> Instruments,
    IReadOnlyList<string> FileTypes,
    bool Crosslinking = false,
    bool NonspecificCleavage = false,
    bool TopDown = false)
{
    /// <summary>Null when accepted, otherwise the first reason it is not.</summary>
    public string? Refusal(Acquisition a)
    {
        if (a.NonspecificCleavage && !NonspecificCleavage) return "nonspecific_cleavage";
        if (a.TopDown && !TopDown) return "top_down";
        if (a.Crosslinked && !Crosslinking) return "crosslinking";
        if (Modes.Count > 0 && !Modes.Contains(a.Mode)) return $"acquisition {Name(a.Mode)}";
        if (Labellings.Count > 0 && !Labellings.Contains(a.Labelling)) return $"labelling {Name(a.Labelling)}";
        if (Instruments.Count > 0 && !Instruments.Contains(a.Instrument)) return $"instrument {Name(a.Instrument)}";
        if (FileTypes.Count > 0 && !FileTypes.Any(t => a.MsFiles.GetValueOrDefault(t) > 0))
            return $"no {string.Join("/", FileTypes)} files listed";
        return null;
    }

    internal static string Name<T>(T value) where T : struct, Enum => ProfileLoader.Snake(value.ToString());
}

/// <summary>
/// An organism's search database: EITHER a prepared file under the machine's database_dir (<see cref="Proteome"/>), OR a
/// UniProt proteome ID (<see cref="UniProt"/>) whose reviewed entries the engine downloads once and caches, so a new
/// machine needs no database preparation. <see cref="Taxon"/> is the NCBI taxon the repository records for it.
/// </summary>
public sealed record OrganismDatabase(string? Proteome, IReadOnlyList<string> Extra, int? Taxon = null, string? UniProt = null);

/// <summary>
/// Which deposits a profile takes whole, and which it defers. Deferral is never subsampling: choosing runs of an
/// experiment is a design decision (aging S43). <see cref="DeferIfMedianFileMb"/>: EBI drops very large transfers and a
/// retry re-pays from zero (S40), so such deposits wait for resume. 0 disables a gate.
/// </summary>
public sealed record DepositPolicy(bool WholeDeposit, int MaxFiles, string Probe, long MaxEstimatedMs2, int MaxFileMb,
    int DeferIfMedianFileMb = 0);

public sealed record QcGates(
    string Ms2Analyzer, double MinFractionOrbitrapHcd, int MinMs2, IReadOnlyList<string> Excludable, double MaxExcludedFraction);

/// <summary>
/// How one kind of data is searched: versioned, shared by every question, and knowing nothing about biology
/// (DESIGN.md section 2). <c>label-free-dda/1</c> is the aging pipeline's v1.
/// </summary>
public sealed record Profile(
    string Id,
    int Version,
    ProfileStatus Status,
    string Description,
    ProfileAccepts Accepts,
    string MetaMorpheus,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> GptmdExtraMods,
    bool SpectralLibrary,
    double TimeoutHours,
    IReadOnlyDictionary<string, OrganismDatabase> Databases,
    string ContaminantPanel,
    string? ContaminantExclude,
    string QuantMethod,
    bool MatchBetweenRuns,
    DepositPolicy Deposit,
    QcGates Qc,
    string SourceDir = ".",
    string Design = "none",
    string Chemistry = "fixed")
{
    /// <summary>The values of <c>[quant] design</c>: none, or an experimental design from an SDRF (G15).</summary>
    public static readonly string[] Designs = { "none", "sdrf" };

    /// <summary>
    /// The values of <c>[engine] chemistry</c> (G19, D22-D27): "fixed" searches every deposit with MetaMorpheus's
    /// defaults (trypsin, fixed carbamidomethyl), as label-free-dda@1 did; "deposit" reads each deposit's protease and
    /// cysteine chemistry first and searches with them, or parks it.
    /// </summary>
    public static readonly string[] Chemistries = { "fixed", "deposit" };

    public string Key => $"{Id}@{Version}";

    /// <summary>A list a profile names (e.g. its contaminant exclusions), shipped under <c>lists/</c> beside the profile.</summary>
    public string ListPath(string name) => Path.Combine(SourceDir, "lists", name);
}
