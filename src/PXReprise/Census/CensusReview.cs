using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PXReprise.Config;
using PXReprise.Discovery;

namespace PXReprise.Census;

/// <summary>
/// <c>review.md</c>: a census made readable, for checking a question's rules in minutes rather than by reading every row
/// of census.tsv. It says how much each keyword and each rule contributed, and shows a fixed sample of every outcome with
/// the text that decided it: relevant (are these right?), excluded (all of them: this is where good deposits are lost),
/// and keyword hits no rule matched (a missed deposit hides here). Samples are chosen by a hash of the accession, so a
/// deposit stays in or out of the sample when the rules change and two censuses can be compared line by line.
/// </summary>
public static class CensusReview
{
    public const int SampleSize = 15, ExcludedShown = 60;

    public static void Write(string path, Question q, IReadOnlyList<CensusRow> rows, string snapshotUtc)
    {
        var sb = new StringBuilder();
        var relevant = rows.Where(x => x.Relevance.Verdict == RelevanceVerdict.Relevant).ToList();
        var excluded = rows.Where(x => x.Relevance.Verdict == RelevanceVerdict.Excluded).ToList();
        var decided = rows.Where(x => x.Relevance.Verdict is RelevanceVerdict.DecidedInclude or RelevanceVerdict.DecidedExclude).ToList();
        var otherOrganism = rows.Where(x => x.Relevance.Verdict == RelevanceVerdict.NotRelevant && x.Relevance.Evidence?.StartsWith("organism: ") == true).ToList();
        var noRule = rows.Where(x => x.Relevance.Verdict == RelevanceVerdict.NotRelevant && x.Relevance.Evidence?.StartsWith("organism: ") != true).ToList();

        sb.Append($"# Census review: {q.Name}\n\n");
        sb.Append($"{q.Description}\n\nPRIDE snapshot {snapshotUtc}. Every number below is a count of PRIDE deposits.\n\n");
        sb.Append("## Outcome\n\n| | deposits |\n|---|---:|\n");
        sb.Append($"| found by the keywords | {rows.Count} |\n");
        sb.Append($"| **relevant** (a `require_any` rule matched, no exclusion) | **{relevant.Count}** |\n");
        sb.Append($"| excluded (an `exclude_if_any` rule matched) | {excluded.Count} |\n");
        sb.Append($"| not relevant: no `require_any` rule matched | {noRule.Count} |\n");
        sb.Append($"| not relevant: organism not in `[discover] organisms` | {otherOrganism.Count} |\n");
        sb.Append($"| decided by hand (`decisions`) | {decided.Count} |\n\n");

        var inScope = rows.Where(x => x.Relevance.IsIn).ToList();
        sb.Append($"Of the {inScope.Count} in scope: ");
        sb.Append(string.Join(", ", inScope.GroupBy(x => x.Route.Kind switch
            {
                RouteKind.Search => $"**{x.Route.Profile} would search**",
                RouteKind.WaitingOnCapability => $"waiting on {x.Route.Profile ?? x.Route.Reason}",
                RouteKind.Held => "held",
                _ => "out of scope",
            }).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        sb.Append(".\n\n");

        // Keywords: which ones find relevant deposits, and which only add noise.
        sb.Append("## Keywords\n\nA keyword that finds many deposits and few relevant ones adds reading, not results.\n\n");
        sb.Append("| keyword | found | relevant | found only by this keyword |\n|---|---:|---:|---:|\n");
        foreach (string kw in q.Keywords)
        {
            var hits = rows.Where(x => x.KeywordsHit.Contains(kw)).ToList();
            sb.Append($"| {Cell(kw)} | {hits.Count} | {hits.Count(x => x.Relevance.IsIn)} | {hits.Count(x => x.KeywordsHit.Count == 1)} |\n");
        }

        // Rules: how much each one does. A require rule matching nothing is dead; one matching alone is load-bearing.
        var texts = rows.ToDictionary(x => x.Accession, x => AcquisitionClassifier.Text(x.Record), StringComparer.Ordinal);
        sb.Append("\n## Rules\n\n| rule | pattern | deposits it matched | only this rule matched |\n|---|---|---:|---:|\n");
        var inRows = relevant.Concat(excluded).ToList();   // everything a require rule let in
        foreach (var r in q.Relevance.RequireAny)
        {
            var m = inRows.Where(x => r.IsMatch(texts[x.Accession])).ToList();
            int alone = m.Count(x => q.Relevance.RequireAny.Count(o => o.IsMatch(texts[x.Accession])) == 1);
            sb.Append($"| require_any | `{Cell(r.ToString())}` | {m.Count} | {alone} |\n");
        }
        foreach (var r in q.Relevance.ExcludeIfAny)
        {
            int m = excluded.Count(x => r.IsMatch(texts[x.Accession]));
            sb.Append($"| exclude_if_any | `{Cell(r.ToString())}` | {m} excluded | |\n");
        }
        foreach (var r in q.Relevance.UnlessAny)
        {
            int rescued = relevant.Count(x => r.IsMatch(texts[x.Accession]) && q.Relevance.ExcludeIfAny.Any(e => e.IsMatch(texts[x.Accession])));
            sb.Append($"| unless_any | `{Cell(r.ToString())}` | {rescued} kept despite an exclusion | |\n");
        }

        Section(sb, "Relevant: are these right?",
            "A sample. Each row quotes the text that made it relevant. A wrong one means a `require_any` rule is too loose, " +
            "or an `exclude_if_any` rule is missing.", Sample(relevant), withEvidence: true);
        Section(sb, $"Excluded: {(excluded.Count > ExcludedShown ? $"the first {ExcludedShown} of {excluded.Count}" : "all of them")}",
            "Every exclusion loses a deposit, so all are shown. A wrong one means an `exclude_if_any` rule is too broad, " +
            "an `unless_any` rule is missing, or it needs an `include` decision.",
            excluded.OrderBy(x => x.Accession, StringComparer.Ordinal).Take(ExcludedShown).ToList(), withEvidence: true);
        Section(sb, "Found by a keyword, but no rule matched: anything missed?",
            "A sample. A deposit that belongs here means a `require_any` rule is missing, or it needs an `include` decision.",
            Sample(noRule), withEvidence: false);
        if (decided.Count > 0)
            Section(sb, "Decided by hand", "From the question's decisions file, with its reasons.",
                decided.OrderBy(x => x.Accession, StringComparer.Ordinal).ToList(), withEvidence: true);
        if (otherOrganism.Count > 0)
        {
            sb.Append("\n## Other organisms\n\nRelevant by the rules, but not in `[discover] organisms`:\n\n| organism | deposits |\n|---|---:|\n");
            foreach (var g in otherOrganism.GroupBy(x => string.Join("; ", x.Organisms)).OrderByDescending(g => g.Count()))
                sb.Append($"| {Cell(g.Key)} | {g.Count()} |\n");
        }
        sb.Append("\nEvery deposit, with every column, is in `census.tsv`.\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>A stable sample: the deposits whose accession hashes lowest, so the same ones are shown census after census.</summary>
    public static List<CensusRow> Sample(IEnumerable<CensusRow> rows) =>
        rows.OrderBy(x => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(x.Accession))), StringComparer.Ordinal)
            .Take(SampleSize).OrderBy(x => x.Accession, StringComparer.Ordinal).ToList();

    private static void Section(StringBuilder sb, string title, string help, IReadOnlyList<CensusRow> rows, bool withEvidence)
    {
        sb.Append($"\n## {title}\n\n{help}\n\n");
        if (rows.Count == 0) { sb.Append("(none)\n"); return; }
        sb.Append(withEvidence ? "| accession | title | organisms | decided by |\n|---|---|---|---|\n" : "| accession | title | organisms | keywords |\n|---|---|---|---|\n");
        foreach (var x in rows)
            sb.Append($"| [{x.Accession}](https://www.ebi.ac.uk/pride/archive/projects/{x.Accession}) | {Cell(Trim(x.Record.Title, 110))} | " +
                      $"{Cell(string.Join("; ", x.Organisms.Select(o => Regex.Replace(o, @"\s*\(.*\)$", ""))))} | " +
                      $"{Cell(withEvidence ? "..." + Trim(x.Relevance.Evidence ?? "", 90) + "..." : string.Join("; ", x.KeywordsHit))} |\n");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "...";

    private static string Cell(string s) => s.Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
}
