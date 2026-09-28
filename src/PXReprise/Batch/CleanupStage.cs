using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Provenance;

namespace PXReprise.Batch;

/// <summary>
/// Deletes the bulky, re-obtainable intermediates of ONE dataset run once its search has succeeded: the fetched .raw
/// files, calibration mzMLs, and any .raw MetaMorpheus copied into a task folder. PRIDE is the source of truth and the
/// fetch provenance holds every file's SHA-256, so the exact inputs stay identifiable. Results, the GPTMD database, logs
/// and provenance are kept. This is a destructive act, so its record is ALWAYS written, even when a delete fails.
/// </summary>
public static class CleanupStage
{
    public static JsonObject Run(string runDir, string workRoot, bool dryRun = false, bool force = false)
    {
        string searchProv = Path.Combine(runDir, "04_search", "provenance.json");
        if (!File.Exists(searchProv) || JsonNode.Parse(File.ReadAllText(searchProv))?["success"]?.GetValue<bool>() != true)
            throw new UsageException($"refusing: {searchProv} missing or not successful");

        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);
        string fetchProv = Path.Combine(runDir, "02_fetch", "provenance.json");
        if (File.Exists(fetchProv))
            foreach (var o in JsonNode.Parse(File.ReadAllText(fetchProv))!["outputs"]?.AsArray() ?? new JsonArray())
                fetched[Path.GetFileName(o!["path"]!.GetValue<string>())] = o["sha256"]!.GetValue<string>();

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        string spectra = Path.Combine(runDir, "02_fetch", "spectra"), mm = Path.Combine(runDir, "04_search", "mm");
        if (Directory.Exists(spectra)) targets.UnionWith(Directory.EnumerateFiles(spectra, "*.raw"));
        if (Directory.Exists(mm))
        {
            targets.UnionWith(Directory.EnumerateFiles(mm, "*-calib.mzML", SearchOption.AllDirectories));
            foreach (string task in Directory.EnumerateDirectories(mm, "Task*")) targets.UnionWith(Directory.EnumerateFiles(task, "*.raw"));
        }

        string outDir = Path.Combine(runDir, "09_cleanup");
        Directory.CreateDirectory(outDir);
        // A second real run would find nothing and overwrite the only record of what the first deleted. A dry run's
        // record is not evidence of anything, so it neither blocks nor is blocked.
        string prior = Path.Combine(outDir, "provenance.json");
        if (File.Exists(prior) && !dryRun && !force && JsonNode.Parse(File.ReadAllText(prior))?["dry_run"]?.GetValue<bool>() != true)
            throw new UsageException($"refusing: {prior} already records a real cleanup of this run");

        string paramsFile = Path.Combine(outDir, "params.json");
        File.WriteAllText(paramsFile, JsonSerializer.Serialize(new { run = runDir, dry_run = dryRun }, Envelope.Json));
        var prov = new AgingProvenance("cleanup", workRoot, paramsFile, new JsonObject(), null);
        prov.Upstream(new[] { searchProv, fetchProv }.Where(File.Exists).ToArray());
        var deleted = new JsonArray();
        var kept = new JsonArray();
        long freed = 0;
        try
        {
            foreach (string t in targets)
            {
                long size = new FileInfo(t).Length;
                if (!dryRun)
                {
                    try { File.Delete(t); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A locked file is a fact to report, not grounds to abandon the record of what was deleted.
                        kept.Add(new JsonObject { ["path"] = t, ["size_bytes"] = size, ["error"] = e.Message });
                        continue;
                    }
                }
                deleted.Add(new JsonObject { ["path"] = t, ["size_bytes"] = size, ["sha256_at_fetch"] = fetched.GetValueOrDefault(Path.GetFileName(t)) });
                freed += size;
            }
        }
        finally
        {
            prov.Set("deleted", deleted);
            prov.Set("dry_run", (JsonNode)dryRun);
            prov.Set("bytes_freed", (JsonNode)freed);
            if (kept.Count > 0)
            {
                prov.Set("not_deleted", kept);
                prov.Note($"{kept.Count} file(s) could not be deleted and are listed in `not_deleted`.");
            }
            prov.Note("Linked copies of the same .raw elsewhere are freed only when their last link goes.");
            prov.Write(outDir);
        }
        return new JsonObject { ["dry_run"] = dryRun, ["files"] = deleted.Count, ["not_deleted"] = kept.Count, ["gb"] = Math.Round(freed / 1e9, 2) };
    }
}
