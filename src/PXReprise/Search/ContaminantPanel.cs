using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PXReprise.Search;

/// <summary>
/// The contaminant database a search uses, reduced when a profile excludes entries (aging D54, S58). MetaMorpheus ships a
/// panel that also carries a human spike-in protein standard; in rodent searches those entries captured native proteins'
/// peptides and labelled them contaminant. The reduced copy is content-addressed: the same panel plus the same list always
/// gives the same file, byte for byte what aging's contam_panel.py builds.
/// </summary>
public static class ContaminantPanel
{
    private static readonly Regex Entry = new(@"<entry\b.*?</entry>\s*", RegexOptions.Singleline);
    private static readonly Regex Accession = new("<accession>([^<]+)</accession>");

    /// <summary>The accessions a list excludes: a TSV with an <c>accession</c> column; '#' lines are comments.</summary>
    public static IReadOnlySet<string> Excluded(string listPath)
    {
        var rows = File.ReadAllLines(listPath).Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).ToList();
        int i = Array.IndexOf(rows[0].Split('\t'), "accession");
        if (i < 0) throw new SearchSetupException($"{Path.GetFileName(listPath)} has no 'accession' column");
        return rows.Skip(1).Select(r => r.Split('\t')[i].Trim()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the panel to search and, when a list is given, the provenance of the reduction. Every listed accession must
    /// be in the panel: a list naming an entry the panel lacks was written for a different panel.
    /// </summary>
    public static (string Panel, JsonObject? Record) Resolve(string sourcePanel, string? excludeList, string outDir)
    {
        if (excludeList is null) return (sourcePanel, null);
        var drop = Excluded(excludeList);
        // Python's read_text/write_text: universal newlines in, the platform's newline out.
        string text = File.ReadAllText(sourcePanel, Encoding.UTF8).Replace("\r\n", "\n");
        var present = Accession.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = drop.Where(a => !present.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            throw new SearchSetupException($"the exclude list names {missing.Count} accession(s) not in {Path.GetFileName(sourcePanel)}: {string.Join(", ", missing.Take(5))}");

        string srcSha = Sha(sourcePanel), lstSha = Sha(excludeList);
        string tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(srcSha + lstSha)))[..12];
        string outFile = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(sourcePanel)}.minus-{Path.GetFileNameWithoutExtension(excludeList)}.{tag}.xml");
        var removed = new SortedSet<string>(StringComparer.Ordinal);
        if (!File.Exists(outFile))
        {
            Directory.CreateDirectory(outDir);
            string reduced = Entry.Replace(text, m =>
            {
                var acc = Accession.Match(m.Value);
                if (acc.Success && drop.Contains(acc.Groups[1].Value)) { removed.Add(acc.Groups[1].Value); return ""; }
                return m.Value;
            });
            string tmp = Path.ChangeExtension(outFile, ".partial");
            File.WriteAllText(tmp, reduced.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            File.Move(tmp, outFile, overwrite: true);
        }
        else removed.UnionWith(drop);

        return (outFile, new JsonObject
        {
            ["source"] = sourcePanel, ["source_sha256"] = srcSha, ["exclude_list"] = excludeList, ["exclude_list_sha256"] = lstSha,
            ["excluded"] = new JsonArray(removed.Select(a => (JsonNode?)a).ToArray()), ["searched"] = outFile, ["searched_sha256"] = Sha(outFile),
            ["decision"] = "aging D54 (S58): a human spike-in standard is not a contaminant",
        });
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
