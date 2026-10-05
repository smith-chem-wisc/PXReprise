using System.Text.RegularExpressions;
using Readers;

namespace PXReprise.Census;

/// <summary>
/// A deposit's experimental design, as far as can be read without downloading its spectra: groups and how many
/// biological replicates each has. <see cref="Power"/> is the replicate count of the second-largest group, so a deposit
/// scores only when at least two groups are well replicated (the user, 2026-10-05: favour sample groups with many
/// replicates, not small deposits).
/// </summary>
public sealed record DesignGuess(string Source, IReadOnlyDictionary<string, int> Replicates, int Fractions, string? Note = null)
{
    public int Groups => Replicates.Count;
    public int Power => Replicates.Values.OrderByDescending(v => v).Skip(1).FirstOrDefault();
    public string Describe() => string.Join("; ", Replicates.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}:{kv.Value}"));

    public static readonly DesignGuess Unknown = new("none", new Dictionary<string, int>(), 1, "no design could be read");
}

/// <summary>Reading designs, and the ranking the user asked for (2026-10-05).</summary>
public static class Ranking
{
    private static readonly Regex Split = new(@"[_\-\. ]+", RegexOptions.CultureInvariant);
    private static readonly Regex Fraction = new(@"^(f|fr|frac|fraction)\d{1,2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Numeric = new(@"^\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingDigits = new(@"^(.*?[A-Za-z])\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The design a deposited SDRF states: a group per distinct combination of its <c>factor value[...]</c> columns,
    /// replicates counted as distinct <c>source name</c>s. Null when no factor value varies, which for design purposes is
    /// no SDRF at all (mzLib's <see cref="SdrfSampleInformativeness"/>).
    /// </summary>
    public static DesignGuess? FromSdrf(SdrfDocument doc)
    {
        if (!SdrfSampleInformativeness.Assess(doc).FactorValueVaries) return null;
        var factors = doc.Header.Where(h => h.StartsWith("factor value[", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fractions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doc.Results)
        {
            string key = string.Join(" | ", factors.Select(f => row[f]?.Trim() ?? ""));
            string sample = row["source name"]?.Trim() ?? "";
            if (!groups.TryGetValue(key, out var set)) groups[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(sample);
            if (row["comment[fraction identifier]"] is { Length: > 0 } fr) fractions.Add(fr.Trim());
        }
        return new DesignGuess("sdrf", groups.ToDictionary(g => g.Key, g => g.Value.Count), Math.Max(1, fractions.Count));
    }

    /// <summary>
    /// The design raw-file names suggest, e.g. <c>SCR096_X002_WT1_M1</c>: names are split into tokens; a position that
    /// holds between 2 and n/2 distinct values names a group (a token's trailing replicate number is set aside first,
    /// so WT1 and WT2 are one group); a position whose every value is unique, or is purely numeric (dates, run numbers),
    /// is not a group; F1..F12-style tokens are fractions, so replicates are files per group over fractions.
    /// A guess, labelled as one: only the largest set of names with the same number of tokens is read.
    /// </summary>
    public static DesignGuess FromNames(IEnumerable<string> rawNames)
    {
        var names = rawNames.Select(n => Path.GetFileNameWithoutExtension(n)).Where(n => n.Length > 0).ToList();
        if (names.Count < 2) return DesignGuess.Unknown;
        var tokenised = names.Select(n => Split.Split(n).Where(t => t.Length > 0).ToArray()).ToList();
        var block = tokenised.GroupBy(t => t.Length).OrderByDescending(g => g.Count()).First().ToList();
        string? note = block.Count < tokenised.Count ? $"{tokenised.Count - block.Count} of {tokenised.Count} names follow another pattern and were not read" : null;
        int n = block.Count, width = block[0].Length;

        var fractionPositions = Enumerable.Range(0, width).Where(i => block.All(t => Fraction.IsMatch(t[i])) && block.Select(t => t[i]).Distinct().Count() > 1).ToList();
        int fractions = fractionPositions.Count == 0 ? 1 : block.Select(t => string.Join("|", fractionPositions.Select(i => t[i].ToLowerInvariant()))).Distinct().Count();

        var factorPositions = new List<(int Pos, bool Prefix)>();
        for (int i = 0; i < width; i++)
        {
            if (fractionPositions.Contains(i) || block.All(t => Numeric.IsMatch(t[i]) || ReplicateMarker.IsMatch(t[i]))) continue;
            int distinct = block.Select(t => t[i]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int prefixes = block.Select(t => Prefix(t[i])).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (prefixes == 1 && distinct > 1) continue;   // X000, X001, S12: one letter prefix, a running number: an ID, not a group
            // WT1, WT2, KO1: the prefix names the group and the number the replicate, whenever stripping it merges values.
            if (prefixes >= 2 && prefixes < distinct && prefixes <= n / 2) factorPositions.Add((i, true));
            else if (distinct >= 2 && distinct <= n / 2) factorPositions.Add((i, false));
        }
        if (factorPositions.Count == 0) return DesignGuess.Unknown with { Source = "file_names", Note = note ?? "the file names name no groups" };

        var prefixed = factorPositions.Where(p => p.Prefix).Select(p => p.Pos).ToList();
        var groups = block.GroupBy(t => string.Join("_", factorPositions.Select(p => p.Prefix ? Prefix(t[p.Pos]) : t[p.Pos])), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => prefixed.Count > 0
                // The replicate number is in the group token: the replicates are its distinct values (WT1, WT2, WT3: 3).
                ? g.Select(t => string.Join("|", prefixed.Select(i => t[i].ToLowerInvariant()))).Distinct().Count()
                : Math.Max(1, (int)Math.Round(g.Count() / (double)fractions)), StringComparer.OrdinalIgnoreCase);
        return new DesignGuess("file_names", groups, fractions, note);
    }

    /// <summary>Tokens that number a replicate, injection or run rather than name a group: M1, R2, rep3, run1, inj2, T1, and a lone letter (a, b, c). A design whose groups really are "A" and "B" is then read as no design: the cautious side.</summary>
    private static readonly Regex ReplicateMarker = new(@"^((rep|run|inj|tech|br|tr|[a-z])\d{1,2}|[a-z])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Prefix(string token) => TrailingDigits.Match(token) is { Success: true } m ? m.Groups[1].Value : token;

    /// <summary>
    /// The user's rule (2026-10-05): bigger is better, within limits. Deposits rarely state their design well enough to
    /// rank on it, so the design guess is information only. 1: at most <paramref name="maxFiles"/> raw files and at most
    /// <paramref name="largeGb"/> GB. 2: larger than either (the batch defers more than max_files anyway).
    /// </summary>
    public static int Tier(int rawFiles, double totalGb, int maxFiles, double largeGb) => rawFiles > maxFiles || totalGb > largeGb ? 2 : 1;

    /// <summary>Within a tier: more raw files first, then the smaller download.</summary>
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, int> tier, Func<T, int> rawFiles, Func<T, double> gb) =>
        items.OrderBy(tier).ThenByDescending(rawFiles).ThenBy(gb);
}
