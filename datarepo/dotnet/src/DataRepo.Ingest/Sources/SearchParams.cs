using System.Text;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using static DataRepo.Ingest.Sources.SourcesPy;

namespace DataRepo.Ingest.Sources;

/// <summary>MetaMorpheus task <c>.toml</c> files -> SearchModification rows and the searched database.</summary>
/// <remarks>
/// Which modifications a search <em>considered</em> is not recoverable from its results: a modification that
/// was searched for and never found leaves no trace in the PSM table. It is the difference between
/// "this dataset has no phosphorylation" and "this dataset was never searched for phosphorylation",
/// which is exactly the trap question P1 is built on, so the search's own parameter files are read.
///
/// The task files are read with Nett, the TOML library MetaMorpheus writes them with (it reaches this
/// assembly through mzLib); the Python read them with <c>tomllib</c>. A file either parser refuses is skipped,
/// as the Python skipped a <c>TOMLDecodeError</c>. Ported from <c>sources/search_params.py</c>, with G81's
/// <see cref="PepIterative"/> from <c>wip/d37-g81-python</c> (3e68781).
/// </remarks>
public static class SearchParams
{
    /// <summary>MetaMorpheus packs its mod lists as <c>Category&lt;TAB&gt;Name</c> pairs joined by a double tab.</summary>
    private const string PairSeparator = "\t\t";

    /// <summary>Where each list lives, and what usage it means.</summary>
    public static readonly IReadOnlyList<(string Section, string Key, string Usage)> ModLists =
    [
        ("CommonParameters", "ListOfModsFixed", "fixed"),
        ("CommonParameters", "ListOfModsVariable", "variable"),
        ("GptmdParameters", "ListOfModsGptmd", "gptmd"),
    ];

    public static readonly IReadOnlyList<string> DatabaseSuffixes = [".xml", ".xml.gz", ".fasta", ".fasta.gz", ".fa"];

    /// <summary>MetaMorpheus's <c>SearchParameters.TCAmbiguity</c> default (<c>TaskLayer/SearchTask/SearchParameters.cs:50</c>).</summary>
    public const string TcAmbiguityDefault = "RemoveContaminant";

    /// <summary>The last MetaMorpheus release before iterative PEP training (#2844, merged at <c>c4515a797</c> on
    /// 2026-09-29). No release contains it yet (1.1.11 is the latest, 2026-09-18).</summary>
    public static readonly IReadOnlyList<int> LastReleaseWithoutIterativePep = [1, 1, 11];

    private static IEnumerable<(string Category, string Name)> Pairs(object? packed)
    {
        foreach (var chunk in (Truthy(packed) ? Str(packed) : "").Split(PairSeparator))
        {
            if (Strip(chunk).Length == 0) continue;
            var (category, _, name) = Partition(chunk, "\t");
            yield return (Strip(category), Strip(name));
        }
    }

    /// <summary>A task file as <c>tomllib.loads(path.read_text(encoding="utf-8-sig"))</c>, or null for a file that
    /// is not there or does not parse (the Python's <c>continue</c>).</summary>
    /// <exception cref="DecoderFallbackException">The file is not UTF-8, which the Python did not catch either.</exception>
    internal static Dictionary<string, object?>? ReadTask(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        try
        {
            return Plain(Nett.Toml.ReadString(text).ToDictionary()) as Dictionary<string, object?>;
        }
        catch (Exception e) when (e is not DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Nett's values as the plain values the rest of the port uses.</summary>
    private static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool or long or double => value,
        int i => (long)i,
        float f => (double)f,
        IDictionary<string, object> d => d.ToDictionary(kv => kv.Key, kv => Plain(kv.Value), StringComparer.Ordinal),
        System.Collections.IEnumerable e => e.Cast<object?>().Select(Plain).ToList(),
        _ => value,
    };

    private static IReadOnlyDictionary<string, object?> Section(IReadOnlyDictionary<string, object?> doc, string name) =>
        DictOrEmpty(Get(doc, name), name);

    /// <summary>Every modification the search could have assigned, with its UNIMOD accession where known.</summary>
    /// <param name="taskFiles">The task <c>.toml</c> files the run used.</param>
    /// <param name="datasetId">ProteomeXchange accession.</param>
    /// <param name="unimodCurie">The UNIMOD CURIE of a modification name in the registry of the MetaMorpheus
    /// install that did the search (<c>registry.Lookup(name)?.UnimodCurie</c>), or null.</param>
    /// <returns>One row per (modification, usage), deduplicated and sorted.</returns>
    /// <remarks>Takes the lookup as a function so this module compiles before <c>ModRegistry</c> is ported;
    /// <c>ModificationRows(taskFiles, datasetId, registry)</c> is the one-line overload to add beside it.</remarks>
    /// <param name="rules">Under <see cref="IngestRules.Current"/> a name with no <c> on </c> part has
    /// <c>residues</c> <c>unspecified</c>; Python 0.32.0's <c>rpartition</c> returned the whole name when the
    /// separator was absent, so the column held the modification's name (G83 item 14).</param>
    public static List<Row> ModificationRows(IEnumerable<string> taskFiles, string datasetId, Func<string, string?> unimodCurie,
        IngestRules rules = IngestRules.Current)
    {
        var seen = new Dictionary<(string Name, string Usage), Row>();
        foreach (var path in taskFiles)
        {
            var doc = ReadTask(path);
            if (doc is null) continue;
            foreach (var (section, key, usage) in ModLists)
            {
                var packed = Get(Section(doc, section), key);
                if (!Truthy(packed)) continue;
                foreach (var (_, name) in Pairs(packed))
                {
                    if (name.Length == 0) continue;
                    var curie = unimodCurie(name);
                    var (_, separator, residues) = RPartition(name, " on ");
                    if (separator.Length == 0 && rules == IngestRules.Current) residues = "";
                    if (seen.ContainsKey((name, usage))) continue;
                    seen[(name, usage)] = new Row
                    {
                        ["dataset_id"] = datasetId,
                        ["modification"] = curie,
                        ["name"] = name,
                        ["residues"] = residues.Length > 0 ? residues : "unspecified",
                        ["usage"] = usage,
                    };
                }
            }
        }
        return seen.Keys
            .OrderBy(k => k.Name, CodePointOrder).ThenBy(k => k.Usage, CodePointOrder)
            .Select(k => seen[k]).ToList();
    }

    /// <summary>Name and SHA-256 of the protein database the search used, from its provenance inputs.</summary>
    /// <returns>Either may be null when the provenance does not list one.</returns>
    public static (string? Name, string? Sha256) SearchedDatabase(IReadOnlyDictionary<string, object?> provenance)
    {
        foreach (var item in ListOrEmpty(Get(provenance, "inputs"), "inputs"))
        {
            var entry = item as IReadOnlyDictionary<string, object?> ?? EmptyDict;
            var path = Str(Get(entry, "path", ""));
            if (EndsWithDatabaseSuffix(path))
            {
                var sha = Get(entry, "sha256");
                return (PathName(path), sha is null ? null : Str(sha));
            }
        }
        return (null, null);
    }

    internal static bool EndsWithDatabaseSuffix(string path)
    {
        var lower = path.ToLowerInvariant();
        return DatabaseSuffixes.Any(s => lower.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>Which MetaMorpheus tasks ran, from the search stage's params.</summary>
    public static List<string> TaskNames(IReadOnlyDictionary<string, object?> provenance) =>
        ListOrEmpty(Get(DictOrEmpty(Get(provenance, "params"), "params"), "tasks"), "params.tasks").Select(Str).ToList();

    /// <summary>Which of MetaMorpheus's PEP feature sets trained the search's PEP model.</summary>
    /// <remarks>
    /// MetaMorpheus picks it from the first PSM (<c>FdrAnalysisEngine.Compute_PEPValue</c>, at
    /// <c>FdrAnalysisEngine.cs:410-416</c>): <c>crosslink</c> for a crosslink search, <c>top-down</c> when the
    /// protease is "top-down", <c>RNA</c> for oligos, and <c>standard</c> otherwise. Read here from the task
    /// files -- <c>TaskType</c> and <c>CommonParameters.DigestionParams.Protease</c> -- and claimed only for
    /// task types whose mapping is certain: a <c>Search</c> task is <c>top-down</c> or <c>standard</c> by protease,
    /// and an <c>XLSearch</c> is <c>crosslink</c>. Anything else (glyco, RNA, a type not seen yet) is null
    /// rather than a guess, and so are two search tasks that disagree.
    /// </remarks>
    public static string? PepRegime(IEnumerable<string> taskFiles)
    {
        var regimes = new HashSet<string?>();
        foreach (var path in taskFiles)
        {
            var doc = ReadTask(path);
            if (doc is null) continue;
            var taskType = TaskType(doc);
            if (taskType == "XLSearch")
                regimes.Add("crosslink");
            else if (taskType == "Search")
            {
                var digestion = DictOrEmpty(Get(Section(doc, "CommonParameters"), "DigestionParams"), "DigestionParams");
                regimes.Add(Str(Get(digestion, "Protease")) == "top-down" ? "top-down" : "standard");
            }
            else if (taskType.ToLowerInvariant().Contains("search"))
                regimes.Add(null);
        }
        return regimes.Count == 1 ? regimes.First() : null;
    }

    private static string TaskType(IReadOnlyDictionary<string, object?> doc)
    {
        var value = Get(doc, "TaskType");
        return Truthy(value) ? Str(value) : "";
    }

    private static readonly Regex ReleaseRe = new(@"^\s*(\d+)\.(\d+)\.(\d+)\s*$", RegexOptions.CultureInvariant);

    /// <summary><c>"1.1.11"</c> -> <c>[1, 1, 11]</c>; null for anything else.</summary>
    private static List<long>? ReleaseTuple(string? release)
    {
        var m = ReleaseRe.Match(release ?? "");
        if (!m.Success) return null;
        var parts = new List<long>();
        for (var g = 1; g <= 3; g++)
        {
            long value = 0;
            foreach (var c in m.Groups[g].Value)
            {
                value = value * 10 + (long)char.GetNumericValue(c);
                if (value > int.MaxValue) value = int.MaxValue;  // only the ordering against 1.1.11 matters
            }
            parts.Add(value);
        }
        return parts;
    }

    private static bool AtOrBefore(IReadOnlyList<long> version, IReadOnlyList<int> limit)
    {
        for (var i = 0; i < limit.Count; i++)
            if (version[i] != limit[i]) return version[i] < limit[i];
        return true;
    }

    /// <summary>Whether the search's PEP model was trained iteratively: <c>on</c>, <c>off</c> or <c>not recorded</c> (G81).</summary>
    /// <remarks>
    /// pep 005's rule. <c>SearchParameters.IterativePepTraining</c> in the search task's <c>.toml</c> is the
    /// answer when present. When absent, a release up to <see cref="LastReleaseWithoutIterativePep"/> predates
    /// the setting, so the search trained once (<c>off</c>); a later or unreadable release would load the
    /// missing key as its default, and that is not inferred here (<c>not recorded</c>, as pep offered).
    /// Glyco, crosslink and non-specific searches always train once whatever the key says (<c>off</c>).
    /// Two search tasks that disagree give <c>not recorded</c>. Very small searches that never iterate
    /// are not detected: only <c>results.txt</c> says so, and it is not read for this.
    /// </remarks>
    public static string PepIterative(IEnumerable<string> taskFiles, string? release)
    {
        var answers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in taskFiles)
        {
            var doc = ReadTask(path);
            if (doc is null) continue;
            var taskType = TaskType(doc);
            if (!taskType.ToLowerInvariant().Contains("search")) continue;
            var parameters = Section(doc, "SearchParameters");
            var searchType = Get(parameters, "SearchType");
            if (taskType != "Search" || (Truthy(searchType) ? Str(searchType) : "") == "NonSpecific")
                answers.Add("off");
            else if (parameters.TryGetValue("IterativePepTraining", out var iterative))
                answers.Add(iterative is true ? "on" : "off");
            else
            {
                var version = ReleaseTuple(release);
                answers.Add(version is not null && AtOrBefore(version, LastReleaseWithoutIterativePep) ? "off" : "not recorded");
            }
        }
        return answers.Count == 1 ? answers.First() : "not recorded";
    }

    /// <summary>How the search resolved an accession present in both a target and a contaminant database.</summary>
    /// <remarks>
    /// <c>SearchParameters.TCAmbiguity</c> in the search task's <c>.toml</c>: <c>RemoveContaminant</c> (the default)
    /// drops the contaminant entry, <c>RemoveTarget</c> drops the target entry, <c>RenameProtein</c> keeps both
    /// under renamed accessions (MetaMorpheus <c>DatabaseLoadingEngine.cs:190-278</c> at <c>6e152da70</c>). The
    /// setting decides what a protein in both databases IS in the results, so it is read, not assumed.
    /// </remarks>
    /// <returns>The one value every search task that states it agrees on; the default when a search task
    /// exists but does not state it; null when there is no search task or two disagree.</returns>
    /// <param name="rules">Which tasks are search tasks. Under <see cref="IngestRules.Current"/> a task's
    /// <c>TaskType</c> decides, as it does for <see cref="PepRegime"/>: a <c>Search</c> task answers with its
    /// <c>TCAmbiguity</c> (or the default), and a <c>GlycoSearch</c> or <c>XLSearch</c> task with
    /// <c>RemoveContaminant</c>, because MetaMorpheus loads their databases with <c>DatabaseLoadingEngine</c>'s
    /// default and no setting (<c>GlycoSearchTask.cs:53</c>, <c>XLSearchTask.cs:51</c> at <c>6e152da70</c>). Python
    /// 0.32.0 went by the file name, so a search task whose file name lacked "search" was not read, and a
    /// <c>GlycoSearch</c> task's <c>SearchParameters</c> table (which MetaMorpheus never reads) was (G83 item 15).</param>
    public static string? TcAmbiguity(IEnumerable<string> taskFiles, IngestRules rules = IngestRules.Current)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var searched = false;
        foreach (var path in taskFiles)
        {
            if (!File.Exists(path)) continue;
            // 0.32.0: the file's stem, not its TaskType, decides what a search task is.
            if (rules == IngestRules.Python0320 && !PathStem(path).ToLowerInvariant().Contains("search")) continue;
            var doc = ReadTask(path);
            if (doc is null) continue;
            if (rules == IngestRules.Current)
            {
                var taskType = TaskType(doc);
                if (taskType is "GlycoSearch" or "XLSearch")
                {
                    searched = true;
                    values.Add(TcAmbiguityDefault);
                    continue;
                }
                if (taskType != "Search") continue;
            }
            searched = true;
            var value = Get(Section(doc, "SearchParameters"), "TCAmbiguity");
            values.Add(Truthy(value) ? Str(value) : TcAmbiguityDefault);
        }
        return !searched || values.Count != 1 ? null : values.First();
    }
}
