using System.Text.Json;
using System.Text.RegularExpressions;

namespace PXReprise.Batch;

/// <summary>
/// v0.4.46 (aging PXR-A51, DATAREPO-74; the user, 2026-11-11, "yes b"): a manifest entry's <c>restrictions</c>, the uses
/// datarepo 1.3.4+ marks a whole dataset unfit for in the catalog (<c>dataset_restrictions</c>). The engine owns the key.
/// It is written from recorded facts and the question, nothing else, so it never goes stale the way the hand list of
/// 2026-10-07 did (15 of its 41 rows no longer held a month later, and it missed every deposit delivered since):
/// <list type="bullet">
/// <item>the rule: an entry whose <c>enrichment</c> or <c>run_enrichment</c> names anything but <c>none</c> is restricted for
/// <c>occupancy</c>, since the material was selected on purpose and a site's occupancy in it is not the proteome's;</item>
/// <item>the design rule (G69): an isobaric entry whose search ran on the default design (each file its own plex) is
/// restricted for <c>quantitation</c> (<see cref="NoChannelDesignReason"/>), from its search's provenance;</item>
/// <item>the question's <c>[restrictions.&lt;key&gt;]</c>, the judgments no recorded fact shows. A question use replaces the
/// rule's reason for the same use.</item>
/// </list>
/// A restriction written by hand into the manifest is replaced at the next sync: the question is where one is declared.
/// dataRepo needs only a rebuild for a changed restriction, never a re-ingest (their 044).
/// </summary>
public static class Restrictions
{
    /// <summary>Escapes only what a double-quoted string must (quote, backslash, control characters), so a reason reads as
    /// written ("a site's", not "a site's").</summary>
    private static readonly JsonSerializerOptions Quoted = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// v0.4.46 (G69; the user and QuantProject 120, 2026-11-11): the <c>quantitation</c> reason of an isobaric entry searched
    /// on the default design v0.4.34 to v0.4.45 made when no SDRF stated one (provenance flag
    /// <see cref="Search.DesignStage.NoChannelDesign"/>). Each file was its own plex, so fractions and repeat injections of one
    /// plex are separate samples. QuantProject asked that the note say what a sample is there.
    /// </summary>
    public const string NoChannelDesignReason =
        "no stated design: no SDRF gave this deposit's plexes, fractions or technical replicates, so each file was searched as its own plex. "
        + "A sample here is one file's channel, not a biological sample: fractions and repeat injections of one plex appear as separate samples. "
        + "Reporter values compare only within a file; do not use the TMT values until the deposit is re-searched with an SDRF that states its design";

    /// <summary>The restrictions of an entry whose enrichment names are <paramref name="enrichments"/> (its <c>enrichment</c>
    /// list and its <c>run_enrichment</c> keys, <see cref="ManifestEntry.EnrichmentsOf"/>), sorted by use.
    /// <paramref name="noChannelDesign"/>: its search ran on the default isobaric design (<see cref="NoChannelDesignReason"/>).</summary>
    public static SortedDictionary<string, string> Of(IReadOnlyList<string> enrichments, IReadOnlyDictionary<string, string>? declared, bool noChannelDesign = false)
    {
        var uses = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var enriched = enrichments.Where(n => n != "none").Distinct(StringComparer.Ordinal).ToList();
        if (enriched.Count > 0)
            uses["occupancy"] = $"enriched ({string.Join(", ", enriched)})"
                + (enrichments.Contains("none") ? " in some runs (run_enrichment names which)" : "")
                + ": the material was selected on purpose, so a site's occupancy here describes the enriched material, not the proteome";
        if (noChannelDesign) uses["quantitation"] = NoChannelDesignReason;
        foreach (var (use, reason) in declared ?? new Dictionary<string, string>())
            uses[use] = reason;
        return uses;
    }

    /// <summary>
    /// <paramref name="textLf"/> with the entry of <paramref name="key"/> carrying exactly <see cref="Of"/>'s restrictions;
    /// the same string when it already does. The entry's other lines are left as they are.
    /// </summary>
    public static string ApplyTo(string textLf, string key, IReadOnlyDictionary<string, string>? declared, bool noChannelDesign = false)
    {
        var enrichments = ManifestEntry.EnrichmentsOf(textLf, key) ?? throw new InvalidOperationException($"{key} has no manifest entry");
        var (start, end) = ManifestEntry.Block(textLf, key);
        string block = textLf[start..end];
        string rewritten = Rewrite(block, Of(enrichments, declared, noChannelDesign));
        return rewritten == block ? textLf : textLf[..start] + rewritten + textLf[end..];
    }

    /// <summary>
    /// v0.4.46 (G69): the keys of the isobaric entries (<c>quant_method</c> TMT or iTRAQ) of <paramref name="textLf"/> whose
    /// search ran on the default design, read from each one's <c>04_search/provenance.json</c> under
    /// <paramref name="workRoot"/> (an entry's <c>run</c> is relative to it). <paramref name="unread"/> names the isobaric
    /// entries whose provenance could not be read: they are not restricted for it, and the caller says so.
    /// </summary>
    public static HashSet<string> NoChannelDesignKeys(string textLf, string workRoot, out List<string> unread)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        unread = [];
        foreach (Match m in Regex.Matches(textLf, @"(?m)^[ \t]*- accession: (\S+)$"))
        {
            string key = m.Groups[1].Value;
            var (start, end) = ManifestEntry.Block(textLf, key);
            string block = textLf[start..end];
            if (!Regex.IsMatch(block, @"(?m)^[ \t]+quant_method:[ \t]+(TMT|iTRAQ)[ \t]*$")) continue;
            string? run = Regex.Match(block, @"(?m)^[ \t]+run:[ \t]+(\S+)[ \t]*$") is { Success: true } r ? r.Groups[1].Value : null;
            string? provenance = run is null ? null : Path.Combine(workRoot, run, "04_search", "provenance.json");
            try
            {
                if (provenance is null || !File.Exists(provenance)) { unread.Add(key); continue; }
                var flag = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(provenance))?["experimental_design"]?["flag"]?.GetValue<string>();
                if (flag == Search.DesignStage.NoChannelDesign) keys.Add(key);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                unread.Add(key);
            }
        }
        return keys;
    }

    /// <summary>What a sync changed: each rewritten entry's restrictions after it (empty: removed), and the question's keys
    /// that have no entry yet (written when they are delivered).</summary>
    public sealed record SyncResult(IReadOnlyDictionary<string, SortedDictionary<string, string>> Changed, int Restricted, IReadOnlyList<string> Waiting);

    /// <summary>Every entry of <paramref name="textLf"/> brought to <see cref="Of"/>'s restrictions;
    /// <paramref name="noChannelDesign"/> from <see cref="NoChannelDesignKeys"/>.</summary>
    public static (string Text, SyncResult Result) SyncText(string textLf, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? declared,
        IReadOnlySet<string>? noChannelDesign = null)
    {
        var keys = Regex.Matches(textLf, @"(?m)^[ \t]*- accession: (\S+)$").Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).ToList();
        var changed = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        int restricted = 0;
        foreach (string key in keys)
        {
            var mine = declared?.GetValueOrDefault(key);
            bool noDesign = noChannelDesign?.Contains(key) == true;
            string next = ApplyTo(textLf, key, mine, noDesign);
            var uses = Of(ManifestEntry.EnrichmentsOf(next, key)!, mine, noDesign);
            if (uses.Count > 0) restricted++;
            if (next != textLf) changed[key] = uses;
            textLf = next;
        }
        var waiting = (declared?.Keys ?? Enumerable.Empty<string>()).Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return (textLf, new SyncResult(changed, restricted, waiting));
    }

    /// <summary>
    /// Syncs the manifest at <paramref name="manifestPath"/> (or only reports, when <paramref name="write"/> is false). Read
    /// as LF and written back in the file's own line ends, beside it then renamed over it, as <see cref="ManifestEntry.Append"/>
    /// writes: a reader never sees half a manifest. Nothing is written when nothing changes.
    /// </summary>
    public static SyncResult Sync(string manifestPath, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? declared, bool write = true,
        IReadOnlySet<string>? noChannelDesign = null)
    {
        bool crlf = File.ReadAllText(manifestPath).Contains("\r\n");
        var (text, result) = SyncText(ManifestEntry.ReadLf(manifestPath), declared, noChannelDesign);
        if (write && result.Changed.Count > 0)
        {
            string tmp = manifestPath + ".pxreprise-tmp";
            File.WriteAllText(tmp, crlf ? text.ReplaceLineEndings("\r\n") : text);
            File.Move(tmp, manifestPath, overwrite: true);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="block"/> (one entry, LF) with its <c>restrictions</c> key replaced by <paramref name="uses"/>, or
    /// removed when there are none. A new key goes where the old one was, else before <c>notes</c>, else after the entry's
    /// last line. Each reason is written double-quoted (JSON's escapes are YAML's).
    /// </summary>
    internal static string Rewrite(string block, IReadOnlyDictionary<string, string> uses)
    {
        var lines = block.Split('\n').ToList();
        int indent = lines[0].IndexOf("accession:", StringComparison.Ordinal);
        if (indent < 0) throw new InvalidOperationException($"not a manifest entry: {lines[0]}");
        string pad = new(' ', indent);
        bool IsKey(string l, string key) => l.Length > indent && l[..indent].Trim().Length == 0 && l[indent] != ' '
            && Regex.IsMatch(l[indent..], $@"^{key}:([ \t]|$)");
        int Indent(string l) => l.Length - l.TrimStart(' ', '\t').Length;

        int at = lines.FindIndex(1, l => IsKey(l, "restrictions"));
        if (at >= 0)
        {
            int stop = at + 1;
            while (stop < lines.Count && lines[stop].Trim().Length > 0 && Indent(lines[stop]) > indent) stop++;
            lines.RemoveRange(at, stop - at);
        }
        else
        {
            at = lines.FindIndex(1, l => IsKey(l, "notes"));
            if (at < 0)
            {
                at = lines.Count;
                while (at > 1 && lines[at - 1].Trim().Length == 0) at--;
            }
        }
        if (uses.Count > 0)
            lines.InsertRange(at, new[] { $"{pad}restrictions:" }
                .Concat(uses.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{pad}  {kv.Key}: {JsonSerializer.Serialize(kv.Value, Quoted)}")));
        return string.Join('\n', lines);
    }
}
