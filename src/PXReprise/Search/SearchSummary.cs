using System.Text.RegularExpressions;

namespace PXReprise.Search;

/// <summary>
/// The headline counts MetaMorpheus prints in a search task's <c>results.txt</c>.
///
/// Two PSM counts appear, and they differ (aging S21): the summary line "All target PSMs with q-value &lt;= 0.01" is
/// canonical (<c>aging DEF-PSM-1PCT v1</c>, target only); the FDR engine's "PSMs within 1% FDR" is higher (it appears
/// to include contaminants) and is printed once per file, so only its FIRST occurrence is the dataset's
/// (<c>aging DEF-PSM-FDRENGINE v1</c>). A reader for this file belongs in mzLib Readers (PXReprise G1d); this is the
/// engine's minimal one until that lands, and the corpus test holds it to the aging pipeline's numbers.
///
/// D84: MetaMorpheus 1.1.15 (#2982, #2989) names the search's identification filter on the summary line ("All target
/// PSMs with pep q-value &lt;= 0.01"). <see cref="Psms1Pct"/> stays the count its definition describes: the "q-value"
/// line, where that line means the q-value and the notch together, as it does up to 1.1.14 (PXR-R80,
/// <see cref="QValueIsTheOldRule"/>). <see cref="PsmsFilter1Pct"/> is the summary line under whichever filter it names,
/// and <see cref="Filter"/> names it. The engine's 1.1.15 line ("PSMs with pep q-value &lt;= 0.01") is not the FDRENGINE
/// definition's and is not read.
/// </summary>
public sealed record SearchSummary(int? Psms1Pct, int? PsmsFdrEngine1Pct, string? Filter = null, int? PsmsFilter1Pct = null)
{
    public const string PsmDefinition = "aging DEF-PSM-1PCT v1";
    public const string FdrEngineDefinition = "aging DEF-PSM-FDRENGINE v1";

    private static readonly Regex Summary = new(@"All target PSMs with (?<filter>pep q-value|q-value notch|q-value) <= 0\.01: (?<n>\d+)");
    private static readonly Regex Engine = new(@"PSMs within 1% FDR: (?<n>\d+)");

    // The FDR engine's own line (FdrAnalysisResults): "PSMs within 1% FDR: N" up to 1.1.14, "PSMs with <filter> <= t: N"
    // from 1.1.15. Its first occurrence says which wording the count lines were written in.
    private static readonly Regex EngineLine = new(@"(?m)^[ \t]*PSMs (?:(?<within>within 1% FDR)|with (?:pep q-value|q-value notch|q-value) <= [0-9.eE+-]+):\s*\d+");

    /// <param name="resultsTxt">The text of a search task's results.txt.</param>
    /// <param name="metamorpheusRelease">The release that wrote it, as the search's provenance records it (null for a commit
    /// pin). Read only when results.txt has no FDR-engine line.</param>
    public static SearchSummary Parse(string resultsTxt, string? metamorpheusRelease = null)
    {
        var s = Summary.Match(resultsTxt);
        int? n = s.Success ? int.Parse(s.Groups["n"].Value) : null;
        string? filter = s.Success ? s.Groups["filter"].Value : null;
        bool definitions = filter == "q-value" && QValueIsTheOldRule(resultsTxt, metamorpheusRelease);
        return new(definitions ? n : null, First(Engine, resultsTxt), filter, n);
    }

    public static SearchSummary Read(string path, string? metamorpheusRelease = null) => Parse(File.ReadAllText(path), metamorpheusRelease);

    /// <summary>
    /// PXR-R80: whether a bare "q-value" on results.txt's count lines means the rule <see cref="PsmDefinition"/> describes,
    /// the q-value and the notch together (every MetaMorpheus up to 1.1.14), rather than 1.1.15's q-value alone (#2989).
    /// Decided as datarepo decides it (<c>Identifications.FilterOf</c>): the FDR-engine line's wording wherever results.txt
    /// has that line, whatever the release string says ("v1.1.15", "1.1.15-rc" and a commit pin after #2989 do not parse as
    /// a release). Without the line, a release of 1.1.15 or later is the new rule, and anything else, a commit pin included,
    /// the old one.
    /// </summary>
    internal static bool QValueIsTheOldRule(string resultsTxt, string? metamorpheusRelease)
    {
        bool? engineLineNamesFilter = EngineLine.Match(resultsTxt) is { Success: true } m ? !m.Groups["within"].Success : null;
        return !(engineLineNamesFilter ?? (Version.TryParse(metamorpheusRelease, out var v) && v >= new Version(1, 1, 15)));
    }

    private static int? First(Regex r, string text) => r.Match(text) is { Success: true } m ? int.Parse(m.Groups["n"].Value) : null;
}
