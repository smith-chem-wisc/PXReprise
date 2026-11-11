using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PXReprise.Config;
using PXReprise.Discovery;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

/// <summary>The gate's outcomes, decided in this order (aging PXR-A179, part A3).</summary>
public static class GateOutcome
{
    /// <summary>No strong and no weak term anywhere in the deposit's own text.</summary>
    public const string NotQueued = "not_queued";
    /// <summary>A watch condition holds, or only weak terms match: the watch list, for a person to read.</summary>
    public const string Watch = "watch";
    /// <summary>A strong term matches, the reference-group rule passes, and no watch condition holds.</summary>
    public const string Queue = "queue";
}

/// <summary>Where a deposit's gate text came from.</summary>
public static class GateTextSource
{
    /// <summary>PRIDE's project record (<c>/projects/{accession}</c>).</summary>
    public const string Project = "pride_project";
    /// <summary>ProteomeXchange's GetDataset XML, when PRIDE's record cannot be had (aging PXR-A179, part A1).</summary>
    public const string ProteomeXchange = "proteomexchange";
    /// <summary>PRIDE's search index, when neither record can be had.</summary>
    public const string SearchIndex = "pride_search_index";
    /// <summary>No text could be had at all.</summary>
    public const string None = "none";
}

/// <param name="Class">strong, weak, other_sense, watch_if_any or watch_without_strong.</param>
/// <param name="Field">The text field it matched in (title, description, sample_protocol, keywords, organism_parts,
/// diseases, reference_titles).</param>
/// <param name="Context">About 160 characters around the match.</param>
public sealed record GateMatch(string Term, string Class, string Field, string Context);

public sealed record GateDecision(string Outcome, string Reason, string TextSource, IReadOnlyList<GateMatch> Matches);

/// <summary>
/// A deposit's own text, field by field: the title, project description, sample processing protocol, keywords, PRIDE's
/// organism parts and diseases, and the titles of its references. Never the submitters, lab heads, affiliations, countries,
/// instruments or software: 19 of 40 deposits aging found queued on 2026-11-09 matched only on a lab's institute ("Max Planck
/// Institute for Biology of Ageing").
/// </summary>
public sealed record GateText(string Source, IReadOnlyList<(string Field, string Text)> Fields)
{
    public static GateText From(PrideProject p) => new(GateTextSource.Project, Gate.Fields(p.Title, p.ProjectDescription, p.SampleProcessingProtocol,
        p.Keywords, p.OrganismParts.Select(c => c.Name), p.Diseases.Select(c => c.Name), p.References.Select(r => r.ReferenceLine)));

    public static GateText From(PrideProjectSearchResult r) => new(GateTextSource.SearchIndex, Gate.Fields(r.Title, r.ProjectDescription,
        r.SampleProcessingProtocol, r.Keywords, r.OrganismParts, r.Diseases, r.References));

    /// <summary>
    /// ProteomeXchange's GetDataset XML: the DatasetSummary title, the Description, any cvParam whose name says "protocol",
    /// "tissue" or "disease", the KeywordList values, and the titles of the PublicationList's references. The ContactList is
    /// never read. As aging's screen read it (aging results/selection_screen_2026-11-09/screen.cs).
    /// </summary>
    public static GateText FromProteomeXchange(string xml)
    {
        var x = XDocument.Parse(xml);
        var all = x.Descendants().ToList();
        string title = all.FirstOrDefault(e => e.Name.LocalName == "DatasetSummary")?.Attribute("title")?.Value ?? "";
        string description = all.FirstOrDefault(e => e.Name.LocalName == "Description")?.Value ?? "";
        IEnumerable<string> Values(Func<XElement, bool> keep) =>
            all.Where(keep).Select(e => e.Attribute("value")?.Value).OfType<string>().Where(v => v.Length > 0);
        bool Named(XElement e, string word) => (e.Attribute("name")?.Value ?? "").Contains(word, StringComparison.OrdinalIgnoreCase);
        bool Under(XElement e, string list) => e.Ancestors().Any(a => a.Name.LocalName == list);
        return new(GateTextSource.ProteomeXchange, Gate.Fields(title, description,
            string.Join(" ", Values(e => Named(e, "protocol"))),
            Values(e => Under(e, "KeywordList")),
            Values(e => Named(e, "tissue") && !Under(e, "ContactList")),
            Values(e => Named(e, "disease") && !Under(e, "ContactList")),
            Values(e => Under(e, "PublicationList"))));
    }
}

/// <summary>
/// v0.4.46 (aging PXR-A179, their D171: "the dataset should directly address an aging project factor"; their user: "it should be
/// part of the code, not part of the project AI"). Every deposit the census would queue passes this gate first: rules over its
/// own text, with the question's terms (<see cref="GateTerms"/>), no model call. Aging's free screen of 2026-11-09 found 87
/// deposits queued or served that never should have been.
/// </summary>
public static class Gate
{
    private const int ContextChars = 80;

    internal static IReadOnlyList<(string Field, string Text)> Fields(string? title, string? description, string? protocol,
        IEnumerable<string> keywords, IEnumerable<string> organismParts, IEnumerable<string> diseases, IEnumerable<string> referenceLines) =>
        new List<(string, string)>
        {
            ("title", title ?? ""), ("description", description ?? ""), ("sample_protocol", protocol ?? ""),
            ("keywords", string.Join("; ", keywords.Where(k => !string.IsNullOrWhiteSpace(k)))),
            ("organism_parts", string.Join("; ", organismParts.Where(k => !string.IsNullOrWhiteSpace(k)))),
            ("diseases", string.Join("; ", diseases.Where(k => !string.IsNullOrWhiteSpace(k)))),
            ("reference_titles", string.Join("; ", referenceLines.Select(ReferenceTitle).Where(t => t.Length > 0))),
        };

    private static readonly Regex Dashes = new(@"[‐-―−﹘﹣－]", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Every Unicode dash read as a hyphen, and every run of whitespace as one space (part A2).</summary>
    public static string Normalize(string text) => Spaces.Replace(Dashes.Replace(text, "-"), " ").Trim();

    /// <summary>
    /// The title of a PRIDE reference line, never its authors: "Keele GR, Zhang JG, ... Gygi SP. Global and tissue-specific aging
    /// effects on murine proteomes. Cell Rep. 2023 ..." gives the second sentence. An author named Young or Old must not count
    /// as an age group, so a line whose author list cannot be told apart (initials with periods, one author) gives nothing.
    /// </summary>
    public static string ReferenceTitle(string line)
    {
        var m = Regex.Match(Normalize(line ?? ""), @"^(?<authors>[^.]*?(,|\bet al\b)[^.]*?)\.\s+(?<title>[^.]{12,}?)[.?!](\s|$)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["title"].Value.Trim() : "";
    }

    /// <summary>
    /// The gate's decision (part A3), in order: <see cref="GateOutcome.NotQueued"/> when no strong, no weak and no
    /// <c>watch_without_strong</c> term matches;
    /// <see cref="GateOutcome.Watch"/> when a term in another sense, a <c>watch_if_any</c> term, or (with no strong term) a
    /// <c>watch_without_strong</c> term matches, or only weak terms do, or the reference-group rule fails;
    /// <see cref="GateOutcome.Queue"/> otherwise. <paramref name="noReferenceGroup"/>: the deposit was found only by the
    /// question's <c>disease_keywords</c> and its text names no reference group (PXR-A27).
    /// </summary>
    public static GateDecision Evaluate(GateTerms g, GateText text, bool noReferenceGroup)
    {
        var fields = text.Fields.Select(f => (f.Field, Text: Normalize(f.Text))).Where(f => f.Text.Length > 0).ToList();
        List<GateMatch> Find(IEnumerable<GateTerm> terms, string cls)
        {
            var found = new List<GateMatch>();
            foreach (var t in terms)
                foreach (var (field, s) in fields)
                    if (t.Pattern.Match(s) is { Success: true } m) found.Add(new GateMatch(t.Text, cls, field, Context(s, m)));
            return found;
        }
        var strong = Find(g.Strong, "strong");
        var weak = Find(g.Weak, "weak");
        var other = Find(g.OtherSense, "other_sense");
        var watchAny = Find(g.WatchIfAny, "watch_if_any");
        var watchNoStrong = strong.Count == 0 ? Find(g.WatchWithoutStrong, "watch_without_strong") : new List<GateMatch>();
        var matches = strong.Concat(weak).Concat(other).Concat(watchAny).Concat(watchNoStrong).ToList();
        if (fields.Count == 0) return new(GateOutcome.Watch, "no text of the deposit could be read", text.Source, matches);
        // A later disease (watch_without_strong) is watched even with no aging word at all: aging's D70 and its fixtures
        // PXD005753 (Huntington's) and PXD021788 want the watch list, which "no strong and no weak term" alone would skip.
        // A cancer term alone (watch_if_any) is no aging factor: not queued.
        if (strong.Count == 0 && weak.Count == 0 && watchNoStrong.Count == 0)
            return new(GateOutcome.NotQueued, "no strong or weak term in the deposit's own text", text.Source, matches);
        var why = new List<string>();
        if (other.Count > 0) why.Add($"a term in another sense ('{other[0].Term}' in {other[0].Field})");
        if (watchAny.Count > 0) why.Add($"watch_if_any '{watchAny[0].Term}' in {watchAny[0].Field}");
        if (watchNoStrong.Count > 0) why.Add($"watch_without_strong '{watchNoStrong[0].Term}' in {watchNoStrong[0].Field}, and no strong term");
        if (strong.Count == 0 && weak.Count > 0) why.Add($"only weak terms ({string.Join(", ", weak.Select(w => w.Term).Distinct())})");
        if (g.ReferenceGroupRequired && noReferenceGroup) why.Add("found only by disease_keywords, and its text names no reference group (PXR-A27)");
        return why.Count > 0
            ? new(GateOutcome.Watch, string.Join("; ", why), text.Source, matches)
            : new(GateOutcome.Queue, $"strong term '{strong[0].Term}' in {strong[0].Field}", text.Source, matches);
    }

    /// <summary>
    /// v0.4.46 (aging PXR-A180; the user, 2026-11-11): a hand decision in the question's decisions file is a person's reading of
    /// the deposit, which is what the watch list asks for. An include is queued without the gate, so a word in passing (a cancer
    /// named as an age-related disease) cannot park a deposit someone approved; an exclude is not queued. Null with no decision.
    /// </summary>
    public static GateDecision? Decided(Question q, string accession) =>
        q.Relevance.Decisions.TryGetValue(accession, out var d)
            ? new GateDecision(d.Verdict == DecisionVerdict.Include ? GateOutcome.Queue : GateOutcome.NotQueued,
                $"a hand decision {(d.Verdict == DecisionVerdict.Include ? "includes" : "excludes")} it ({d.Reason}); the gate is not applied",
                GateTextSource.None, Array.Empty<GateMatch>())
            : null;

    private static string Context(string s, Match m)
    {
        int start = Math.Max(0, m.Index - ContextChars), end = Math.Min(s.Length, m.Index + m.Length + ContextChars);
        return (start > 0 ? "..." : "") + s[start..end] + (end < s.Length ? "..." : "");
    }

    /// <summary>The question's keywords (all three lists) that occur in <paramref name="text"/> as words, for the report mode.</summary>
    public static IReadOnlySet<string> KeywordsIn(Question q, GateText text)
    {
        string all = Normalize(string.Join(" \n ", text.Fields.Select(f => f.Text)));
        return q.Keywords.Concat(q.More.Disease).Concat(q.More.Watch)
            .Where(k => Regex.IsMatch(all, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(Normalize(k))}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The reference-group rule over plain text: found only by disease keywords, and no reference group named.</summary>
    public static bool NoReferenceGroup(Question q, IReadOnlySet<string> keywordsHit, GateText text) =>
        !q.Keywords.Any(keywordsHit.Contains) && q.More.Disease.Any(keywordsHit.Contains)
        && ReferenceGroup.FindIn(string.Join(" \n ", text.Fields.Select(f => f.Text))) is null;

    /// <summary>One row per deposit: its outcome and why, where its text came from, and every match with its context.</summary>
    public static void WriteTsv(string path, IEnumerable<(string Accession, IReadOnlyCollection<string> KeywordsHit, GateDecision Decision)> rows)
    {
        static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var sb = new StringBuilder("accession\toutcome\treason\ttext_source\tkeywords_hit\tstrong\tweak\twatch_terms\tmatches\n");
        foreach (var (acc, kws, d) in rows)
        {
            string Terms(params string[] classes) => string.Join("; ", d.Matches.Where(m => classes.Contains(m.Class)).Select(m => m.Term).Distinct());
            sb.Append(string.Join('\t', new[]
            {
                acc, d.Outcome, d.Reason, d.TextSource, string.Join("; ", kws.Order(StringComparer.Ordinal)),
                Terms("strong"), Terms("weak"), Terms("other_sense", "watch_if_any", "watch_without_strong"),
                string.Join(" | ", d.Matches.Select(m => $"{m.Class}:{m.Term}@{m.Field}: {m.Context}")),
            }.Select(Clean))).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
