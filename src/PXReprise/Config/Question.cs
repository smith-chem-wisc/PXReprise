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
    string? StudyLayer);
