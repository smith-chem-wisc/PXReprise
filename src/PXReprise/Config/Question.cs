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
    DiscoverLists? Lists = null)
{
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

/// <summary>Where a question's batch keeps its run folders and its own state (queue, state.json, log, STOP).</summary>
public sealed record BatchSettings(string RunRoot, string StateDir, string Queue);

/// <summary>
/// How a question's searched datasets reach its repository: the dataRepo manifest the batch appends to, and the command
/// run after every ingest (rebuild the catalog, regenerate the site). The command is the question's own.
/// </summary>
public sealed record PublishSettings(string? Manifest, IReadOnlyList<string> Command);
