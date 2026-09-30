using System.Globalization;

namespace PXReprise.Search;

/// <summary>
/// Streams a MetaMorpheus tab-separated table as rows keyed by header. AllPSMs.psmtsv reaches 1.75 GB (aging
/// DATAREPO-37), so rows are never all held at once unless the caller keeps them.
/// </summary>
public static class Tsv
{
    public static IEnumerable<Dictionary<string, string>> Read(string path)
    {
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (header is null) yield break;
        string[] cols = header.Split('\t');
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            var row = new Dictionary<string, string>(cols.Length, StringComparer.Ordinal);
            for (int i = 0; i < cols.Length; i++) row[cols[i]] = i < f.Length ? f[i] : "";
            yield return row;
        }
    }

    /// <summary>The header columns, in file order.</summary>
    public static string[] Header(string path)
    {
        using var reader = new StreamReader(path);
        return reader.ReadLine()?.Split('\t') ?? Array.Empty<string>();
    }

    /// <summary>A number as Python's <c>float()</c> reads it: blank and unparsable are NaN.</summary>
    public static double Number(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;

    /// <summary><c>float(x or 0)</c>: blank counts as zero, as the aging pipeline sums intensities.</summary>
    public static double NumberOrZero(string? s) => string.IsNullOrEmpty(s) ? 0 : Number(s);
}
