using PXReprise.Discovery;

namespace PXReprise.Config;

public enum ProfileStatus { Available, Pending }

/// <summary>What a profile accepts. An empty list means "any value".</summary>
public sealed record ProfileAccepts(
    IReadOnlyList<AcquisitionMode> Modes,
    IReadOnlyList<Labelling> Labellings,
    IReadOnlyList<InstrumentClass> Instruments,
    IReadOnlyList<string> FileTypes)
{
    /// <summary>Null when accepted, otherwise the first reason it is not.</summary>
    public string? Refusal(Acquisition a)
    {
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
    string SourceDir = ".")
{
    public string Key => $"{Id}@{Version}";

    /// <summary>A list a profile names (e.g. its contaminant exclusions), shipped under <c>lists/</c> beside the profile.</summary>
    public string ListPath(string name) => Path.Combine(SourceDir, "lists", name);
}
