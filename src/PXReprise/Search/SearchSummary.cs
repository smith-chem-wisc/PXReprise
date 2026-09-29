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
/// </summary>
public sealed record SearchSummary(int? Psms1Pct, int? PsmsFdrEngine1Pct)
{
    public const string PsmDefinition = "aging DEF-PSM-1PCT v1";
    public const string FdrEngineDefinition = "aging DEF-PSM-FDRENGINE v1";

    private static readonly Regex Summary = new(@"All target PSMs with q-value <= 0\.01: (\d+)");
    private static readonly Regex Engine = new(@"PSMs within 1% FDR: (\d+)");

    public static SearchSummary Parse(string resultsTxt) => new(First(Summary, resultsTxt), First(Engine, resultsTxt));

    public static SearchSummary Read(string path) => Parse(File.ReadAllText(path));

    private static int? First(Regex r, string text) => r.Match(text) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
}
