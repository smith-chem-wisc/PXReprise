using System.Text.RegularExpressions;

namespace PXReprise.Search;

/// <summary>One database a MetaMorpheus task reports it loaded (MetaMorpheus #2898, in 1.1.13 and later).</summary>
/// <param name="Sha256">Lower-case hex, or null when MetaMorpheus wrote "SHA-256 unavailable".</param>
/// <param name="DerivedFrom">A GPTMD output's parents: (file name, SHA-256 or null). Empty for a database we supplied.</param>
public sealed record LoadedDatabase(string Task, string File, string? Path, string? Sha256, long? Bytes,
    IReadOnlyList<(string File, string? Sha256)> DerivedFrom);

/// <summary>
/// What MetaMorpheus states about its own run in results.txt and on the console, read for provenance (reader fixes R3
/// and R4 of design/ADOPTION_post-1.1.12_2026-10-28.md). Older releases write none of these lines; every reader then
/// returns empty, never fails.
/// </summary>
public static partial class MetaMorpheusResults
{
    [GeneratedRegex(@"^\t\tSHA-256 (?:(?<sha>[0-9a-f]{64}), (?<bytes>\d+) bytes|unavailable.*)$")]
    private static partial Regex ShaLine();

    [GeneratedRegex(@"(?<file>[^;]+?) \(SHA-256 (?:(?<sha>[0-9a-f]{64}), \d+ bytes|unavailable[^)]*)\)")]
    private static partial Regex Parent();

    [GeneratedRegex(@"^All target (?<what>.+?) with (?<filter>pep q-value|q-value notch|q-value) <= (?<t>[0-9.eE-]+)")]
    private static partial Regex CountLine();

    /// <summary>The "Databases loaded:" blocks of one task's results.txt.</summary>
    public static List<LoadedDatabase> Databases(string task, IEnumerable<string> resultsLines)
    {
        var list = new List<LoadedDatabase>();
        string? file = null, path = null, sha = null; long? bytes = null;
        var parents = new List<(string, string?)>();
        bool inBlock = false;
        void Flush()
        {
            if (file is not null) list.Add(new LoadedDatabase(task, file, path, sha, bytes, parents.ToList()));
            file = path = sha = null; bytes = null; parents.Clear();
        }
        foreach (string raw in resultsLines)
        {
            string line = raw.TrimEnd('\r');
            if (line == "Databases loaded:") { inBlock = true; continue; }
            if (!inBlock) continue;
            if (line.StartsWith('\t') && !line.StartsWith("\t\t", StringComparison.Ordinal))
            {
                Flush();
                int colon = line.LastIndexOf(':');
                file = (colon > 0 ? line[1..colon] : line[1..]).Trim();
            }
            else if (line.StartsWith("\t\tPath: ", StringComparison.Ordinal)) path = line["\t\tPath: ".Length..].Trim();
            else if (ShaLine().Match(line) is { Success: true, Groups: var g } && g["sha"].Success)
            {
                sha = g["sha"].Value;
                bytes = long.Parse(g["bytes"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("\t\tDerived from: ", StringComparison.Ordinal))
                foreach (Match p in Parent().Matches(line["\t\tDerived from: ".Length..]))
                    parents.Add((p.Groups["file"].Value.Trim(), p.Groups["sha"].Success ? p.Groups["sha"].Value : null));
            else if (line.StartsWith("\t\t", StringComparison.Ordinal)) { }
            else { Flush(); inBlock = false; }
        }
        Flush();
        return list;
    }

    /// <summary>
    /// Each loaded database checked against the bytes we gave the search: a database we passed must carry our SHA-256, and
    /// a GPTMD output's parents that share a name with one of ours must too. Null when everything agrees; otherwise why
    /// not. A database MetaMorpheus could not hash, or one it loaded that we neither passed nor derived, is not a
    /// mismatch: the record keeps it for whoever reads it.
    /// </summary>
    public static string? Mismatch(IEnumerable<LoadedDatabase> loaded, IReadOnlyDictionary<string, string> oursByPath)
    {
        var byName = oursByPath
            .GroupBy(kv => System.IO.Path.GetFileName(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var db in loaded)
        {
            if (db.Path is not null && db.Sha256 is not null
                && oursByPath.FirstOrDefault(kv => string.Equals(System.IO.Path.GetFullPath(kv.Key), System.IO.Path.GetFullPath(db.Path), StringComparison.OrdinalIgnoreCase)) is { Key: not null } ours
                && ours.Value != db.Sha256)
                problems.Add($"{db.Task} loaded {db.Path} as SHA-256 {db.Sha256[..12]}, but the search was given {ours.Value[..12]}");
            foreach (var (parent, sha) in db.DerivedFrom)
                if (sha is not null && byName.TryGetValue(parent, out var shas) && !shas.Contains(sha))
                    problems.Add($"{db.Task}'s {db.File} was derived from {parent} SHA-256 {sha[..12]}, not the file the search was given ({string.Join(", ", shas.Select(s => s[..12]))})");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// The identification filter results.txt states: the filter and threshold of its "All target ..." count lines, and
    /// any sentence MetaMorpheus put above them because PEP filtering was asked for but not done (#2919).
    /// </summary>
    public static (List<(string What, string Filter, double Threshold)> Counts, List<string> Notes) Filter(IEnumerable<string> resultsLines)
    {
        var counts = new List<(string, string, double)>();
        var notes = new List<string>();
        foreach (string raw in resultsLines)
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Engine Results:", StringComparison.Ordinal)) break;
            if (CountLine().Match(line) is { Success: true } m
                && double.TryParse(m.Groups["t"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t))
                counts.Add((m.Groups["what"].Value, m.Groups["filter"].Value, t));
            else if (line.StartsWith("PEP ", StringComparison.Ordinal)) notes.Add(line.Trim());
        }
        return (counts, notes);
    }

    /// <summary>The experimental-design gap warnings MetaMorpheus 1.1.13 logs when it quantifies a gapped numbering (#2928).</summary>
    public static List<string> DesignGaps(IEnumerable<string> logLines) => logLines
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.Contains("quantified as numbered;", StringComparison.Ordinal) && l.Contains("not in the design", StringComparison.Ordinal))
        .Select(l => l.Contains("WARN: ", StringComparison.Ordinal) ? l[(l.IndexOf("WARN: ", StringComparison.Ordinal) + 6)..].Trim() : l.Trim())
        .Distinct()
        .ToList();
}
