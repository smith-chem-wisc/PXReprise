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
/// PSMs with pep q-value &lt;= 0.01"). <see cref="Psms1Pct"/> stays the count its definition describes, the "q-value"
/// line; <see cref="PsmsFilter1Pct"/> is the summary line under whichever filter it names, and <see cref="Filter"/>
/// names it. The engine's 1.1.15 line ("PSMs with pep q-value &lt;= 0.01") is not the FDRENGINE definition's and is not
/// read.
/// </summary>
public sealed record SearchSummary(int? Psms1Pct, int? PsmsFdrEngine1Pct, string? Filter = null, int? PsmsFilter1Pct = null)
{
    public const string PsmDefinition = "aging DEF-PSM-1PCT v1";
    public const string FdrEngineDefinition = "aging DEF-PSM-FDRENGINE v1";

    private static readonly Regex Summary = new(@"All target PSMs with (?<filter>pep q-value|q-value notch|q-value) <= 0\.01: (?<n>\d+)");
    private static readonly Regex Engine = new(@"PSMs within 1% FDR: (?<n>\d+)");

    public static SearchSummary Parse(string resultsTxt)
    {
        var s = Summary.Match(resultsTxt);
        int? n = s.Success ? int.Parse(s.Groups["n"].Value) : null;
        string? filter = s.Success ? s.Groups["filter"].Value : null;
        return new(filter == "q-value" ? n : null, First(Engine, resultsTxt), filter, n);
    }

    public static SearchSummary Read(string path) => Parse(File.ReadAllText(path));

    private static int? First(Regex r, string text) => r.Match(text) is { Success: true } m ? int.Parse(m.Groups["n"].Value) : null;
}
