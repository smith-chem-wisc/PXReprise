using System.Text.Json.Nodes;

namespace PXReprise.Batch;

/// <summary>
/// Refuses a deposit whose raw files are already searched under another accession, before any download: PRIDE lists
/// names and sizes for free. PXD012985 is PXD011740 deposited again (the same 12 raw files, byte-identical sizes), and
/// was fetched and searched in full before aging's QC saw it (aging 015, PXR-A9). A file is its name and its size;
/// a deposit is a duplicate when EVERY one of its raw files is in one searched deposit. Sharing some files is logged,
/// never refused (PXD026955 and PXD027754 share one probe file and are different experiments).
/// </summary>
public static class DuplicateScreen
{
    public sealed record Searched(string Accession, IReadOnlySet<(string Name, long Bytes)> Raws);

    public sealed record Verdict(string? DuplicateOf, IReadOnlyList<(string Accession, int Shared)> Partial);

    /// <summary>
    /// Every searched deposit under <paramref name="roots"/>: a run folder (one or two levels down, as aging's
    /// <c>run_&lt;date&gt;/&lt;accession&gt;</c>) with a search provenance and the whole fetch's manifest. The fetch
    /// manifest of a searched deposit lists the whole deposit, because only whole deposits are searched (S43).
    /// </summary>
    /// <param name="inFlight">
    /// Run folders fetched in full and being searched now, with no search provenance yet. The next deposit is screened
    /// while the current one searches, so without them a deposit is never compared with the one just before it:
    /// PXD042302 (PXD042301 deposited again) was screened 7 s after PXD042301's search started (2026-10-04, PXR-A21).
    /// </param>
    public static List<Searched> Index(IEnumerable<string> roots, IEnumerable<string>? inFlight = null)
    {
        var found = new Dictionary<string, Searched>(StringComparer.Ordinal);
        foreach (string dir in inFlight ?? Array.Empty<string>())
            if (Read(Path.Combine(dir, "02_fetch", "fetch_manifest.json")) is { } s) found.TryAdd(s.Accession, s);
        foreach (string root in roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string dir in Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateDirectories(d).Prepend(d)))
            {
                string manifest = Path.Combine(dir, "02_fetch", "fetch_manifest.json");
                if (!File.Exists(manifest) || !File.Exists(Path.Combine(dir, "04_search", "provenance.json"))) continue;
                if (Read(manifest) is { } s) found.TryAdd(s.Accession, s);
            }
        return found.Values.ToList();
    }

    private static Searched? Read(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        try
        {
            var m = JsonNode.Parse(File.ReadAllText(manifest))!;
            string? acc = m["accession"]?.GetValue<string>();
            var raws = m["files"]?.AsArray()
                .Select(f => (Name: f!["name"]?.GetValue<string>(), Bytes: f["pride_size_bytes"]?.GetValue<long>() ?? -1))
                .Where(f => f.Name is not null && f.Bytes >= 0)
                .Select(f => (f.Name!, f.Bytes)).ToHashSet();
            return acc is null || raws is null || raws.Count == 0 ? null : new Searched(acc, raws);
        }
        catch (System.Text.Json.JsonException) { return null; }   // an older layout: not evidence either way
    }

    public static Verdict Check(string accession, IReadOnlyCollection<(string Name, long Bytes)> raws, IEnumerable<Searched> index)
    {
        if (raws.Count == 0) return new(null, Array.Empty<(string, int)>());
        var partial = new List<(string, int)>();
        foreach (var s in index.Where(s => s.Accession != accession).OrderBy(s => s.Accession, StringComparer.Ordinal))
        {
            int shared = raws.Count(s.Raws.Contains);
            if (shared == raws.Count) return new(s.Accession, partial);
            if (shared > 0) partial.Add((s.Accession, shared));
        }
        return new(null, partial);
    }
}
