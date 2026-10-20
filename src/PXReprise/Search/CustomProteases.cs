using Proteomics.ProteolyticDigestion;

namespace PXReprise.Search;

/// <summary>
/// v0.4.28: digestion agents mzLib's <c>proteases.tsv</c> does not have, from PXReprise's own list shipped under
/// <c>profiles/lists/</c> (same columns; see its PROVENANCE.md). The first is <see cref="TrypsinChymotrypsin"/>, for the
/// chymotrypsin fraction of a MED-FASP digest (PXD006815).
/// <list type="bullet">
/// <item>The list is merged into mzLib's <see cref="ProteaseDictionary"/> in this process when the profiles load
/// (<see cref="Config.ProfileLoader.LoadDirectory"/>), so the chemistry resolves its names like any other.</item>
/// <item>Before a search that uses one, its entries are installed into MetaMorpheus's settings folder as
/// <c>proteases_custom.tsv</c>. MetaMorpheus 1.1.12 reads that file at start (GlobalVariables.LoadDigestionAgents), and a
/// per-file <c>DigestionAgent</c> resolves through the same dictionary (FileSpecificParameters).</item>
/// <item>A name mzLib already has is refused: mzLib would keep its own definition and skip ours.</item>
/// </list>
/// </summary>
public static class CustomProteases
{
    public const string FileName = "PXReprise_custom_proteases_v1.tsv";
    public const string MetaMorpheusFileName = "proteases_custom.tsv";

    /// <summary>Cuts after K and R, and after F, W, Y and L except before P.</summary>
    public const string TrypsinChymotrypsin = "trypsin+chymotrypsin";

    private const string Header = "Name\tMotif\tSpecificity\tPSI-MS Accession\tPSI-MS Name\tCleavage Modification";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal);   // name -> its line in our list
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names merged so far in this process.</summary>
    public static IReadOnlyCollection<string> Names { get { lock (Gate) return Lines.Keys.ToList(); } }

    public static bool IsCustom(string name) { lock (Gate) return Lines.ContainsKey(name); }

    /// <summary>
    /// Merges <paramref name="path"/>'s entries into mzLib's dictionary. Loading the same entries again (a second profiles
    /// folder, a test) is a no-op; a name already loaded with another definition, or one mzLib has built in, is refused.
    /// </summary>
    public static void Load(string path)
    {
        lock (Gate)
        {
            if (!Files.Add(Path.GetFullPath(path)) && Lines.Count > 0) return;
            var entries = Entries(File.ReadAllLines(path));
            var fresh = new List<string>();
            foreach (var (name, line) in entries)
            {
                if (Lines.TryGetValue(name, out string? had))
                {
                    if (had != line) throw new Config.ConfigException(path, $"custom protease '{name}' is already loaded with another definition");
                    continue;
                }
                if (ProteaseDictionary.Dictionary.ContainsKey(name))
                    throw new Config.ConfigException(path, $"custom protease '{name}' is a name mzLib already has: mzLib would keep its own and skip ours");
                fresh.Add(name);
            }
            if (fresh.Count == 0) return;
            // mzLib parses the whole file and merges what is new; names it skips are ours already (checked above).
            var result = ProteaseDictionary.LoadAndMergeCustomProteases(path);
            foreach (string name in fresh)
            {
                if (!result.Added.Contains(name)) throw new Config.ConfigException(path, $"mzLib did not add custom protease '{name}'");
                Lines[name] = entries.First(e => e.Name == name).Line;
            }
        }
    }

    /// <summary>The custom proteases a deposit's chemistry names: its whole-deposit protease or any file's.</summary>
    public static IReadOnlyList<string> UsedBy(Discovery.DepositChemistry? chem)
    {
        if (chem is null) return Array.Empty<string>();
        var names = (chem.ProteasePerFile?.Values ?? Enumerable.Empty<string>()).Append(chem.Protease.Value);
        return names.Where(IsCustom).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Writes <paramref name="needed"/>'s entries into <paramref name="settingsDir"/>'s <c>proteases_custom.tsv</c>, calling
    /// <paramref name="seed"/> first when MetaMorpheus has not created that settings folder yet, and returns the file's path.
    /// Entries already there are kept as they are; one of our names with another definition there is refused, never
    /// replaced (searches already ran against it). The file's own line ends are kept.
    /// </summary>
    public static string Install(string settingsDir, IReadOnlyCollection<string> needed, Action seed)
    {
        lock (Gate)
        {
            var unknown = needed.Where(n => !Lines.ContainsKey(n)).ToList();
            if (unknown.Count > 0) throw new SearchSetupException($"{string.Join(", ", unknown)}: not a custom protease PXReprise loaded ({FileName})");
            if (!Directory.Exists(settingsDir)) seed();
            if (!Directory.Exists(settingsDir)) throw new SearchSetupException($"MetaMorpheus did not create its settings folder {settingsDir}");
            string target = Path.Combine(settingsDir, MetaMorpheusFileName);
            string text = File.Exists(target) ? File.ReadAllText(target) : "";
            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var there = Entries(text.Replace("\r\n", "\n").Split('\n')).ToDictionary(e => e.Name, e => e.Line, StringComparer.Ordinal);
            var add = new List<string>();
            foreach (string name in needed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (there.TryGetValue(name, out string? line))
                {
                    if (line != Lines[name])
                        throw new SearchSetupException($"{target} defines '{name}' otherwise than {FileName}: a changed protease must ship under a new name, never replace one searches used");
                    continue;
                }
                add.Add(Lines[name]);
            }
            if (add.Count == 0) return target;
            var sb = new System.Text.StringBuilder(text);
            if (sb.Length > 0 && !text.EndsWith('\n')) sb.Append(nl);
            if (!HasHeader(text)) sb.Append(Header).Append(nl);
            foreach (string line in add) sb.Append(line).Append(nl);
            // Written beside, then renamed over: MetaMorpheus reads the file at every start.
            string staging = target + $".{Guid.NewGuid():N}.partial";
            File.WriteAllText(staging, sb.ToString());
            File.Move(staging, target, overwrite: true);
            return target;
        }
    }

    /// <summary>Each entry's name and its line (tabs as written, the line's ends trimmed), after the header.</summary>
    private static List<(string Name, string Line)> Entries(IEnumerable<string> lines)
    {
        var o = new List<(string, string)>();
        bool header = false;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r').Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!header) { header = true; continue; }
            o.Add((line.Split('\t')[0].Trim(), line));
        }
        return o;
    }

    private static bool HasHeader(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.StartsWith('#'));
}
