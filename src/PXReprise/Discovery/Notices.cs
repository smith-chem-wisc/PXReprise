namespace PXReprise.Discovery;

/// <summary>
/// Things the batch found that a person must be told, though nothing waits on them: the run's <c>notices.tsv</c>, one row
/// per deposit, notice and detail. The session that runs the batch reads it and tells whoever the notice names. Unlike
/// <see cref="DecisionsNeeded"/>, no deposit is parked.
/// </summary>
/// <remarks>
/// D84 (QuantProject 155, PXR-T19): a search file that lost every match-between-runs transfer. QuantProject's user wants
/// to be told each time one is found, in the run's report, and QuantProject in its thread.
/// </remarks>
public static class Notices
{
    public const string FileName = "notices.tsv";

    /// <summary>A search file that lost every match-between-runs transfer (<see cref="Differential.DifferentialStage.LostTransfers"/>).</summary>
    public const string LostTransfers = "lost_transfers";

    private const string Header = "utc\taccession\tnotice\tdetail";

    /// <summary>
    /// Appends the row unless the same deposit, notice and detail are already listed (a re-run of the same search announces
    /// nothing new). Returns whether a row was written.
    /// </summary>
    public static bool Announce(string runRoot, string accession, string notice, string detail)
    {
        Directory.CreateDirectory(runRoot);
        string path = Path.Combine(runRoot, FileName);
        detail = Clean(detail);
        if (File.Exists(path) && File.ReadLines(path).Skip(1).Any(l => l.Split('\t') is { Length: > 3 } c && c[1] == accession && c[2] == notice && c[3] == detail))
            return false;
        if (!File.Exists(path)) File.WriteAllText(path, Header + "\n");
        File.AppendAllText(path, string.Join('\t', DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), accession, notice, detail) + "\n");
        return true;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
