using System.Text.RegularExpressions;

namespace PXReprise.Config;

/// <summary>
/// v0.4.43 (D33, design/PLAN_split-deposits.md; the user, 2026-11-08: "These dual species repos should be split and that
/// should be an option for the human"): the key a search is run, recorded and delivered under. A deposit searched once is
/// keyed by its accession (<c>PXD027754</c>); a split deposit's searches by accession and group (<c>PXD027754-rat</c>,
/// <c>PXD027754-mouse</c>; dataRepo R8/R18, agreed 2026-10-11). Everything PRIDE is asked (records, file lists, checksums,
/// MetaMorpheus's ProteomeXchange accession) takes <see cref="Accession"/>, because PRIDE holds the deposit, not the split.
/// </summary>
public static class SearchKey
{
    /// <summary>A group token: lower-case letters and digits, which dataRepo's key pattern takes.</summary>
    public static readonly Regex GroupToken = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);

    private static readonly Regex SplitKey = new("^(PXD[0-9]+)-([a-z0-9]+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The PRIDE accession of a key: <c>PXD027754</c> for <c>PXD027754-rat</c> and for <c>PXD027754</c>. Anything that is
    /// not a split key comes back as it is (a search keyword such as "anti-aging" is no key).
    /// </summary>
    public static string Accession(string key) => SplitKey.Match(key) is { Success: true } m ? m.Groups[1].Value : key;

    /// <summary>The group of a split search, or null for a whole deposit.</summary>
    public static string? Group(string key) => SplitKey.Match(key) is { Success: true } m ? m.Groups[2].Value : null;

    public static bool IsSplit(string key) => SplitKey.IsMatch(key);

    public static string Of(string accession, string group) => $"{accession}-{group}";
}
