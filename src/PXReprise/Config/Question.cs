using System.Text.RegularExpressions;

namespace PXReprise.Config;

public enum DecisionVerdict { Include, Exclude }

/// <summary>A hand call on one deposit, overriding the rules (a question's <c>decisions.tsv</c>).</summary>
public sealed record Decision(string Accession, DecisionVerdict Verdict, string Reason);

/// <summary>
/// Relevance as rules over a record's free text. A deposit is relevant when it matches <see cref="RequireAny"/>, and
/// is excluded when it matches <see cref="ExcludeIfAny"/> unless it also matches <see cref="UnlessAny"/> (e.g. exclude
/// "type 1 diabetes" unless the record also says "type 2"). Hand decisions override all of it.
/// </summary>
public sealed record RelevanceRules(
    IReadOnlyList<Regex> RequireAny,
    IReadOnlyList<Regex> ExcludeIfAny,
    IReadOnlyList<Regex> UnlessAny,
    IReadOnlyDictionary<string, Decision> Decisions);

/// <summary>
/// What a question studies, as data. It lives in the question's own project folder; the engine validates it and never
/// needs to know which question it is serving (DESIGN.md section 2).
/// </summary>
public sealed record Question(
    string Name,
    string Description,
    string SourceFile,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Organisms,
    RelevanceRules Relevance,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Overlays,
    IReadOnlyList<string> RequiredTraits,
    IReadOnlyList<string> OptionalTraits,
    string? TraitsSource,
    IReadOnlyDictionary<string, string> Holds,
    string? StudyLayer,
    BatchSettings? Batch = null,
    PublishSettings? Publish = null,
    DesignSettings? Designs = null,
    DiscoverLists? Lists = null,
    IReadOnlyDictionary<string, DepositException>? Exceptions = null)
{
    /// <summary>The question's exception for one deposit, or null.</summary>
    public DepositException? ExceptionFor(string accession) => Exceptions?.GetValueOrDefault(accession);

    /// <summary>The <c>[discover]</c> keywords beyond <see cref="Keywords"/>; empty when the question names none.</summary>
    public DiscoverLists More => Lists ?? DiscoverLists.None;
}

/// <summary>
/// Two more kinds of discovery keyword (aging 031/032, PXR-A25 to A27).
/// <see cref="Disease"/>: searched and queued like <c>keywords</c>, but a deposit found ONLY by these must name a
/// reference group (control, healthy, untreated, vehicle, young, ...), or it goes to the watch list as
/// <c>no_reference_group_found</c>: without one it holds no phenotype comparison.
/// <see cref="Watch"/>: discovered and screened, recorded in the census's <c>watch.tsv</c>, never queued.
/// </summary>
public sealed record DiscoverLists(IReadOnlyList<string> Disease, IReadOnlyList<string> Watch)
{
    public static readonly DiscoverLists None = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// A question's curated experimental designs (G15): one SDRF per deposit, <c>&lt;Dir&gt;/&lt;PXD&gt;.sdrf.tsv</c>, used by a
/// profile with <c>[quant] design = "sdrf"</c> ahead of the deposit's own. <see cref="ConditionColumns"/> names the
/// factor columns the condition is built from when a design has more than one.
/// </summary>
public sealed record DesignSettings(string Dir, IReadOnlyList<string> ConditionColumns)
{
    public string For(string accession) => Path.Combine(Dir, $"{accession}.sdrf.tsv");
}

/// <summary>
/// A question's exception for one deposit (<c>[exceptions.PXD...]</c>; PXReprise D28, the user's D-2): the deposit's own
/// value for any of the profile's <c>[deposit]</c> caps, and which of its raw files to search. Data, never code; every use
/// is recorded in provenance and the manifest note. <see cref="IncludeFiles"/> / <see cref="ExcludeFiles"/> are
/// case-insensitive regexes on raw file names: choosing runs is a design decision (S43), so it is the question's to state.
/// </summary>
public sealed record DepositException(
    string Accession,
    string Reason,
    Regex? IncludeFiles = null,
    Regex? ExcludeFiles = null,
    int? MaxFiles = null,
    int? MaxFileMb = null,
    long? MaxEstimatedMs2 = null,
    int? DeferIfMedianFileMb = null)
{
    public bool SelectsFiles => IncludeFiles is not null || ExcludeFiles is not null;

    public bool Keeps(string fileName) =>
        (IncludeFiles is null || IncludeFiles.IsMatch(fileName)) && (ExcludeFiles is null || !ExcludeFiles.IsMatch(fileName));

    /// <summary>The profile's deposit policy with this deposit's own caps.</summary>
    public DepositPolicy Apply(DepositPolicy p) => p with
    {
        MaxFiles = MaxFiles ?? p.MaxFiles,
        MaxFileMb = MaxFileMb ?? p.MaxFileMb,
        MaxEstimatedMs2 = MaxEstimatedMs2 ?? p.MaxEstimatedMs2,
        DeferIfMedianFileMb = DeferIfMedianFileMb ?? p.DeferIfMedianFileMb,
    };

    /// <summary>One line for logs, provenance and the manifest note.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (IncludeFiles is not null) parts.Add($"include_files /{IncludeFiles}/");
        if (ExcludeFiles is not null) parts.Add($"exclude_files /{ExcludeFiles}/");
        if (MaxFiles is { } mf) parts.Add($"max_files {mf}");
        if (MaxFileMb is { } mm) parts.Add($"max_file_mb {mm}");
        if (MaxEstimatedMs2 is { } me) parts.Add($"max_est_ms2 {me}");
        if (DeferIfMedianFileMb is { } dm) parts.Add($"defer_if_median_file_mb {dm}");
        return $"{string.Join(", ", parts)} ({Reason})";
    }
}

/// <summary>Where a question's batch keeps its run folders and its own state (queue, state.json, log, STOP).</summary>
public sealed record BatchSettings(string RunRoot, string StateDir, string Queue);

/// <summary>
/// How a question's searched datasets reach its repository: the dataRepo manifest the batch appends to, and the command
/// that publishes what was ingested (rebuild the catalog, regenerate the site). The command is the question's own; when
/// it runs is ours (D41 rule 6): at most every <paramref name="EveryHours"/> (0 = after each ingest), only when something
/// was delivered since the last successful publish, and killed after <paramref name="TimeoutHours"/>.
/// </summary>
public sealed record PublishSettings(string? Manifest, IReadOnlyList<string> Command, double EveryHours = 6, double TimeoutHours = 4);
