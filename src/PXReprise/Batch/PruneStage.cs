using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Provenance;

namespace PXReprise.Batch;

/// <summary>
/// v0.4.18: deletes the search outputs nothing reads, once the deposit's ingest has succeeded. MetaMorpheus still writes
/// them (its per-file lines in <c>results.txt</c>, which the QC payload reads, come only with <c>WriteIndividualFiles</c>);
/// they are removed after delivery:
/// <list type="bullet">
/// <item>the search task's <c>Individual File Results</c> folder (per-file PSMs, peptides, protein groups, quantified
/// peaks, mzID): about 7 GB a deposit, 971 GB over the 144 deposits delivered by 2026-10-15, when F: filled;</item>
/// <item><c>AllPSMs_FormattedForPercolator.tab</c>;</item>
/// <item>the spectral library MetaMorpheus wrote, when the registry holds the same bytes as the version it registered
/// (SHA-256 of both files against the registered one).</item>
/// </list>
/// The combined tables, <c>results.txt</c>, the protein groups, logs and provenance are kept: they are what dataRepo's
/// ingest, the QC payload and every store correction read. Every deleted path is recorded in <c>09b_prune</c>.
/// </summary>
public static class PruneStage
{
    public const string Folder = "09b_prune";

    public static JsonObject Run(string runDir, string workRoot, string libraryRoot)
    {
        string searchProv = Path.Combine(runDir, "04_search", "provenance.json");
        var search = File.Exists(searchProv) ? JsonNode.Parse(File.ReadAllText(searchProv)) : null;
        if (search?["success"]?.GetValue<bool>() != true)
            throw new UsageException($"refusing: {searchProv} missing or not successful");

        var targets = new SortedDictionary<string, string>(StringComparer.Ordinal);   // path -> why
        string mm = Path.Combine(runDir, "04_search", "mm");
        if (Directory.Exists(mm))
            foreach (string task in Directory.EnumerateDirectories(mm, "Task*SearchTask"))
            {
                // Only beside the combined output the deposit was delivered from.
                if (!File.Exists(Path.Combine(task, "AllPSMs.psmtsv"))) continue;
                string perFile = Path.Combine(task, "Individual File Results");
                if (Directory.Exists(perFile))
                    foreach (string f in Directory.EnumerateFiles(perFile, "*", SearchOption.AllDirectories)) targets[f] = "per-file output";
                string percolator = Path.Combine(task, "AllPSMs_FormattedForPercolator.tab");
                if (File.Exists(percolator)) targets[percolator] = "Percolator input";
            }
        if (LibraryCopy(search!, libraryRoot) is { } copy) targets[copy.Path] = $"same bytes as {copy.Store}";

        var result = new JsonObject { ["files"] = 0, ["not_deleted"] = 0, ["gb"] = 0.0 };
        string outDir = Path.Combine(runDir, Folder);
        // A second run (a re-ingest after a store correction) finds nothing, and must not overwrite the first one's record.
        if (targets.Count == 0) return result;

        Directory.CreateDirectory(outDir);
        string paramsFile = Path.Combine(outDir, "params.json");
        File.WriteAllText(paramsFile, JsonSerializer.Serialize(new { run = runDir, library_root = libraryRoot }, Envelope.Json));
        var prov = new AgingProvenance("prune", workRoot, paramsFile, new JsonObject(), null);
        prov.Upstream(searchProv);
        var deleted = new JsonArray();
        var kept = new JsonArray();
        long freed = 0;
        try
        {
            foreach (var (t, why) in targets)
            {
                long size = new FileInfo(t).Length;
                try { File.Delete(t); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    kept.Add(new JsonObject { ["path"] = t, ["size_bytes"] = size, ["error"] = e.Message });
                    continue;
                }
                deleted.Add(new JsonObject { ["path"] = t, ["size_bytes"] = size, ["what"] = why });
                freed += size;
            }
            if (Directory.Exists(mm))
                foreach (string task in Directory.EnumerateDirectories(mm, "Task*SearchTask"))
                {
                    string perFile = Path.Combine(task, "Individual File Results");
                    if (Directory.Exists(perFile) && !Directory.EnumerateFiles(perFile, "*", SearchOption.AllDirectories).Any())
                        Directory.Delete(perFile, recursive: true);
                }
        }
        finally
        {
            prov.Set("deleted", deleted);
            prov.Set("bytes_freed", (JsonNode)freed);
            if (kept.Count > 0)
            {
                prov.Set("not_deleted", kept);
                prov.Note($"{kept.Count} file(s) could not be deleted and are listed in `not_deleted`.");
            }
            prov.Write(outDir);
        }
        result["files"] = deleted.Count;
        result["not_deleted"] = kept.Count;
        result["gb"] = Math.Round(freed / 1e9, 2);
        return result;
    }

    /// <summary>
    /// The library file this search wrote, when the registry's copy of the version it registered is byte-identical to it
    /// (both hashed against the recorded SHA-256). Null when the search registered nothing, or either file differs or is gone.
    /// </summary>
    internal static (string Path, string Store)? LibraryCopy(JsonNode search, string libraryRoot)
    {
        if (search["spectral_library"]?["written"] is not JsonObject w) return null;
        string? rel = w["path"]?.GetValue<string>(), sha = w["sha256"]?.GetValue<string>();
        string? src = w["produced_by"]?["metamorpheus_path"]?.GetValue<string>();
        if (rel is null || sha is null || src is null) return null;
        string store = Path.Combine(libraryRoot, rel);
        if (!File.Exists(src) || !File.Exists(store)) return null;
        if (new FileInfo(src).Length != new FileInfo(store).Length) return null;
        return Sha(src) == sha && Sha(store) == sha ? (src, store) : null;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
