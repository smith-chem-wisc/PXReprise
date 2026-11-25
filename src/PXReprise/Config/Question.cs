using System.Text.RegularExpressions;

namespace PXReprise.Config;

public enum DecisionVerdict { Include, Exclude }

/// <summary>
/// D96: where one trait's value per run comes from in the question's curated tables (<c>[traits.&lt;id&gt;]</c>), for
/// ptmQtl's per-site trait fit. <see cref="DesignFactor"/> names a <c>curated_design_factors</c> factor (its
/// <c>level_value</c>); <see cref="SampleValue"/> a per-sample <c>table.column</c>, which wins where both give one.
/// <see cref="Scale"/> multiplies the value (0.1 turns years into decades), and <see cref="TraitId"/> names it in the
/// file, so the unit travels with it (<c>age_decades</c>). <see cref="SampleExcludeWhen"/> are true/false columns of the
/// sample table that leave a sample out (a range, a lower bound). <see cref="Covariates"/> are categorical: a
/// <c>characteristics[...]</c> name from <c>curated_sample_characteristics</c>, or a <c>curated_run_design</c> column;
/// with <see cref="CovariateFactorRole"/>, every other <c>curated_design_factors</c> factor of that role is one too. A
/// factor of <see cref="StratumFactorRole"/> (a tissue) is never a covariate: its levels are fitted apart, so a deposit
/// where one varies is left out until that fit is built. A covariate in <see cref="NeverDrop"/> (a batch) is never left
/// out to get a fit: it is written whenever it has two levels, and a deposit where it is incomplete is left out.
/// </summary>
public sealed record TraitSource(string Id, string? DesignFactor, string? SampleValue, double Scale, IReadOnlyList<string> Covariates,
    string? TraitIdOverride = null, IReadOnlyList<string>? SampleExcludeWhen = null, string? CovariateFactorRole = null,
    string? StratumFactorRole = null, IReadOnlyList<string>? NeverDropCovariates = null)
{
    public string TraitId => TraitIdOverride ?? Id;
    public IReadOnlyList<string> SampleExclusions => SampleExcludeWhen ?? [];
    public IReadOnlyList<string> NeverDrop => NeverDropCovariates ?? [];
}

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
    IReadOnlyDictionary<string, DepositException>? Exceptions = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Splits = null,
    GateTerms? Gate = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Restrictions = null,
    IReadOnlyDictionary<string, TraitSource>? TraitSources = null)
{
    /// <summary>
    /// v0.4.46: the uses a manifest <c>restrictions</c> entry may name, as datarepo 1.3.4+
    /// reads them (<c>DatasetEntry.RestrictionUses</c>). dataRepo refuses the whole manifest over any other key, so the
    /// question's <c>[restrictions]</c> is checked against this list when it is loaded, not at the next build.
    /// </summary>
    public static readonly IReadOnlyList<string> RestrictionUses =
        new[] { "protein_statistics", "quantitation", "age_analysis", "any_analysis", "occupancy", "cysteine" };

    /// <summary>The uses the question itself declares for one manifest key (<c>[restrictions.&lt;key&gt;]</c>), or null.</summary>
    public IReadOnlyDictionary<string, string>? RestrictionsFor(string key) => Restrictions?.GetValueOrDefault(key);

    /// <summary>
    /// The question's exception for one search key, or null: a deposit's <c>[exceptions]</c> entry, or for a split search
    /// (<c>PXD027754-mouse</c>) its group's settings, which take the same keys.
    /// </summary>
    public DepositException? ExceptionFor(string key) => Exceptions?.GetValueOrDefault(key);

    /// <summary>v0.4.43: the search keys a deposit is split into (<c>[splits.PXD...]</c>), or null when it is searched whole.</summary>
    public IReadOnlyList<string>? SplitKeysOf(string accession) => Splits?.GetValueOrDefault(accession);

    /// <summary>
    /// What the question says about a key, for the batch's one-more-look rule (<c>BatchRunner.Settled</c>): the exception's
    /// <see cref="DepositException.Describe"/>, or for a split search every group's, so a change to any group's file
    /// selection reopens each of them (two groups that overlap are fixed by editing either).
    /// </summary>
    public string? ExceptionStamp(string key) =>
        ExceptionFor(key) is not { } e ? null
        : e.Split is { } s ? string.Join(" | ", s.Keys.Select(k => $"{SearchKey.Group(k)}: {ExceptionFor(k)?.Describe()}"))
        : e.Describe();

    /// <summary>The <c>[discover]</c> keywords beyond <see cref="Keywords"/>; empty when the question names none.</summary>
    public DiscoverLists More => Lists ?? DiscoverLists.None;
}

/// <summary>
/// Two more kinds of discovery keyword (PXR-A25 to A27).
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
    /// <summary>
    /// The design file for a search key. A split search (v0.4.43) takes its own (<c>PXD027754-mouse.sdrf.tsv</c>) when
    /// there is one, else the deposit's: the design stage keeps only the rows of the files the search takes.
    /// </summary>
    public string For(string key)
    {
        string own = Path.Combine(Dir, $"{key}.sdrf.tsv");
        string deposit = SearchKey.Accession(key);
        return deposit == key || File.Exists(own) ? own : Path.Combine(Dir, $"{deposit}.sdrf.tsv");
    }
}

/// <summary>
/// A question's exception for one deposit (<c>[exceptions.PXD...]</c>, the maintainer's D-2): the deposit's own
/// value for any of the profile's <c>[deposit]</c> caps, and which of its raw files to search. Data, never code; every use
/// is recorded in provenance and the manifest note. <see cref="IncludeFiles"/> / <see cref="ExcludeFiles"/> are
/// case-insensitive regexes on raw file names: choosing runs is a design decision (S43), so it is the question's to state.
/// <see cref="Alkylation"/> (v0.4.15): <c>"none"</c> states that the deposit's cysteines were never alkylated, so
/// it is searched with no cysteine modification, whatever its text or PRIDE's list says (PXD015239: no reduction or
/// alkylation step, 0.2-0.4% cysteine peptides). It is the only value: another alkylant is named in a curated SDRF.
/// <see cref="Species"/> (v0.4.19): the organism the deposit is searched as, a key of the profiles'
/// <c>[databases]</c> (<c>"rat"</c>), over the one the queue read from PRIDE's record (PXD068498: rat, filed as mouse).
/// <see cref="MinIdentificationRate"/> (v0.4.44): the deposit's own identification floor, under which its full search is
/// held instead of delivered (<c>BatchRunner.MinIdentificationRate</c>); <c>0</c> delivers it whatever it found.
/// <see cref="MinMs2"/> (2026-11-20; aging 316, PXR-A217): the deposit's own MS2 floor for QC's <c>too_few_ms2</c>, in place of
/// the profile's <c>[qc] min_ms2</c>, for files that are thin but real (PXD016039's high-pH fractions: 2,790 to 4,978 MS2
/// under a floor of 5,000).
/// </summary>
public sealed record DepositException(
    string Accession,
    string Reason,
    Regex? IncludeFiles = null,
    Regex? ExcludeFiles = null,
    int? MaxFiles = null,
    int? MaxFileMb = null,
    long? MaxEstimatedMs2 = null,
    int? DeferIfMedianFileMb = null,
    string? Alkylation = null,
    string? Species = null,
    IReadOnlyDictionary<string, FileReplacement>? ReplaceFiles = null,
    SplitGroup? Split = null,
    double? MinIdentificationRate = null,
    int? MinMs2 = null)
{
    public const string NoAlkylation = "none";

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

    /// <summary>The profile's QC gates with this deposit's own MS2 floor.</summary>
    public QcGates Apply(QcGates g) => MinMs2 is { } floor ? g with { MinMs2 = floor } : g;

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
        if (Alkylation is { } alk) parts.Add($"alkylation {alk}");
        if (Species is { } sp) parts.Add($"species {sp}");
        if (ReplaceFiles is { Count: > 0 } rf) parts.Add($"replace_files {rf.Count} ({string.Join(", ", rf.Keys.Order(StringComparer.Ordinal))})");
        if (MinIdentificationRate is { } floor) parts.Add($"min_identification_rate {floor.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (MinMs2 is { } minMs2) parts.Add($"min_ms2 {minMs2}");
        return $"{string.Join(", ", parts)} ({Reason})";
    }
}

/// <summary>
/// v0.4.39 (the maintainer, 2026-11-04): a deposit raw file the question replaces with a local copy, such as a
/// byte-level repair of a file damaged at PRIDE (PXD025315: six files with one range written twice). The fetch does not
/// download the file: it checks that PRIDE still lists <paramref name="PrideSha1"/> for it (so the replacement is for the
/// file PRIDE serves), copies <paramref name="Path"/> into the deposit, checks <paramref name="Sha256"/>, and records the
/// replacement in the fetch's provenance and the delivery's manifest note: a reader can see which inputs are not PRIDE's
/// bytes, and why (<paramref name="Note"/>).
/// </summary>
public sealed record FileReplacement(string Path, string PrideSha1, string Sha256, string Note);

/// <summary>
/// v0.4.43 (D33): one group of a split deposit, <c>[splits.&lt;accession&gt;.&lt;group&gt;]</c>.
/// <paramref name="By"/> is what the split separates (<see cref="Reasons"/>; written to the manifest as dataRepo's
/// <c>split_reason</c>, their 093); <paramref name="Keys"/> are every group's search key, this one's included, in the
/// question's order.
/// </summary>
public sealed record SplitGroup(string Accession, string Group, string By, string Reason, IReadOnlyList<string> Keys)
{
    /// <remarks><c>tissue</c> (2026-11-15, the maintainer: "split them the same way"): a deposit
    /// whose runs come from different tissues (a group may also be one sex: one TMT plex each, as in PXD067637 and its two
    /// sisters), searched apart so that protein inference and site localisation stay within a tissue. <c>plex</c>
    /// (2026-11-20, aging PXR-A214): a deposit of many isobaric plexes of one design (PXD011967: twenty TMT6 sets of five age
    /// groups, 1.9 TB), searched one plex or set of plexes at a time because it is too large to search whole. Calling such a
    /// split <c>tissue</c> would write an untrue <c>split_reason</c>. Only <c>species</c> changes what a group must state; the
    /// others are reasons, written to the manifest's <c>split_reason</c>, whose list datarepo keeps too
    /// (<c>DatasetEntry.SplitReasons</c>).</remarks>
    public static readonly IReadOnlyList<string> Reasons = new[] { "species", "enrichment", "label", "chemistry", "tissue", "plex" };

    public string Key => SearchKey.Of(Accession, Group);

    public IEnumerable<string> Siblings => Keys.Where(k => k != Key);
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

/// <summary>
/// v0.4.46 (their user: "it should be part of the code, not part of the project AI"): the
/// question's <c>[gate]</c>, rules over a deposit's own text that every deposit passes before the census queues it
/// (<see cref="Census.Gate"/>). The terms are the question's, never the engine's: a question is data.
/// </summary>
/// <param name="Strong">A match queues the deposit, unless a watch condition holds.</param>
/// <param name="Weak">Matches alone send it to the watch list.</param>
/// <param name="OtherSense">A strong term used in another sense (an implant's longevity): watch.</param>
/// <param name="WatchIfAny">Any match sends it to the watch list (a cancer model).</param>
/// <param name="WatchWithoutStrong">A match with no strong term sends it to the watch list (a later disease).</param>
/// <param name="ReferenceGroupRequired">A deposit found only by <c>disease_keywords</c> must name a reference group.</param>
public sealed record GateTerms(
    IReadOnlyList<GateTerm> Strong,
    IReadOnlyList<GateTerm> Weak,
    IReadOnlyList<GateTerm> OtherSense,
    IReadOnlyList<GateTerm> WatchIfAny,
    IReadOnlyList<GateTerm> WatchWithoutStrong,
    bool ReferenceGroupRequired);

/// <summary>One gate term as the question wrote it (a phrase or a regular expression), and its pattern on word boundaries.</summary>
public sealed record GateTerm(string Text, Regex Pattern);
