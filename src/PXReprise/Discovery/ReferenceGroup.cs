using System.Text.RegularExpressions;
using UsefulProteomicsDatabases;

namespace PXReprise.Discovery;

/// <summary>
/// Whether a PRIDE record names a reference group beside its disease group (aging 032, PXR-A27, aging D71): control,
/// healthy, untreated, vehicle, sham, wild type, placebo, mock, age-matched, cognitively normal, young, and the like.
/// A guess from text for the screen, with the words it read as evidence; curation decides the real design later.
/// "Control" counts only as a group: not quality control, a loading control, or control of the FDR.
/// </summary>
public static class ReferenceGroup
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Pattern = new(string.Join("|", new[]
    {
        @"(?<!quality[- ])(?<!loading )(?<!internal )(?<!process )(?<!positive )(?<!negative )\bcontrols?\b(?!\s+(of|for|the|over|by)\b)(?![- ]?(ed|ling|lable)\b)",
        @"\bhealthy\b", @"\buntreated\b", @"\bvehicle\b", @"\bsham\b", @"\bwild[- ]?type\b", @"\bWT\b", @"\bplacebo\b", @"\bmock\b",
        @"\bnon-?(diseased|demented|affected|carriers?)\b", @"\bage-? ?matched\b", @"\bcognitively (normal|unimpaired|healthy|intact)\b",
        @"\bunaffected\b", @"\byoung(er)?\b", @"\bnormal (subjects|individuals|donors|tissue|brains?|mice|rats)\b",
    }), Opt);

    /// <summary>A snippet around the first reference-group word, or null when the record names none.</summary>
    public static string? Find(PrideProjectSearchResult r)
    {
        string text = AcquisitionClassifier.Text(r);
        var m = Pattern.Match(text);
        return m.Success ? AcquisitionClassifier.Snippet(text, m) : null;
    }
}
