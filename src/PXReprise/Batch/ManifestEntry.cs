using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Config;

namespace PXReprise.Batch;

/// <summary>
/// Appends a dataset's <c>include</c> entry to the question's dataRepo manifest, in the shape `datarepo ingest` reads
/// (the aging batch's entry, field for field). Idempotent: an accession already present is left alone.
/// </summary>
public static class ManifestEntry
{
    /// <summary>
    /// A new question's manifest, written the first time a deposit is delivered: the header dataRepo requires
    /// (<c>work_root</c>, <c>store</c>, <c>datasets</c>), with roots relative to the manifest so the folder can move.
    /// An existing manifest (aging's, written by hand) is never touched.
    /// </summary>
    public static void EnsureExists(string manifestPath, string instance, string runRoot)
    {
        if (File.Exists(manifestPath)) return;
        string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        Directory.CreateDirectory(dir);
        string workRoot = Path.GetRelativePath(dir, Path.GetFullPath(runRoot)).Replace('\\', '/');
        File.WriteAllText(manifestPath, $"""
            # dataRepo manifest for the question '{instance}', written by PXReprise. `datarepo ingest` reads it; the batch
            # appends one `include` entry per searched deposit. Roots are relative to this file.
            manifest_version: 1
            instance: {instance}
            work_root: {workRoot}
            store: store

            datasets:

            """);
    }

    /// <summary>The manifest's own <c>work_root</c>, resolved as dataRepo resolves it (relative to the manifest), or null.</summary>
    public static string? WorkRootOf(string manifestPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifestPath), @"(?m)^work_root:\s*([^#\r\n]+)");
        if (!m.Success) return null;
        string v = m.Groups[1].Value.Trim().Trim('"', '\'');
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, v));
    }

    /// <summary>
    /// <paramref name="workRoot"/> is the fallback for a manifest that names none; an entry's <c>run</c> is relative to the
    /// manifest's own <c>work_root</c>, because that is what dataRepo joins it to.
    /// </summary>
    public static void Append(string manifestPath, QueueEntry e, Profile profile, string runDir, JsonObject? state, string workRoot)
    {
        workRoot = WorkRootOf(manifestPath) ?? workRoot;
        string text = File.ReadAllText(manifestPath);
        if (text.Contains($"accession: {e.Accession}\n") || text.Contains($"accession: {e.Accession}\r")) return;
        // Count from qc_report.json, never from the raw files: cleanup has already deleted them (dataRepo hashes `files`
        // into the bundle id and reconciles it against the runs, and caught `files: 0` on the very first dataset).
        var qc = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "02b_qc", "qc_report.json")))!.AsObject();
        var excluded = state?["qc_excluded"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new List<string>();
        int nfiles = qc.Count - excluded.Count;
        int taxon = profile.Databases[e.Organism].Taxon
                    ?? throw new InvalidOperationException($"{profile.Key} gives no taxon for '{e.Organism}'");
        string enrichment = state?["enrichment"]?.GetValue<string>() ?? "none";
        var search = JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "04_search", "provenance.json")))!;
        string mm = search["tools"]?["MetaMorpheus"]?["release"]?.GetValue<string>() ?? profile.MetaMorpheus;
        var extras = search["extra_databases"]?.AsArray().Select(x => Path.GetFileName(x!.GetValue<string>())).ToList() ?? new List<string>();

        var notes = new List<string>();
        var flags = new List<string> { "no_design_file", "no_output_sdrf" };
        if (enrichment != "none")
        {
            string ev = (state?["screen_evidence"]?.GetValue<string>() ?? "").Replace('"', '\'');
            notes.Add($"ENRICHED ({enrichment}): the deposit's own text says '...{ev}...'. Intensities describe the enriched material, not the whole proteome; never pool them with whole proteomes as abundance.");
            flags.Add("enriched");
        }
        if (excluded.Count > 0)
        {
            notes.Add($"QC-EXCLUDED FILES: {string.Join(", ", excluded)} failed QC on {string.Join("/", profile.Qc.Excludable)} (a blank or failed injection) and were not searched; the deposit has {qc.Count} raw files.");
            flags.Add("qc_excluded_files");
        }
        // G16: a prose flag and a note only. `mixed_enrichment` itself changes what the ingest does with every run, so it
        // is set by hand with the per-run map (aging D51), never guessed from file names.
        if (MixedSamples.Detect(qc.Select(kv => kv.Key)) is { } mixed)
        {
            notes.Add($"POSSIBLY MIXED SAMPLES: the raw file names hold {MixedSamples.Describe(mixed)}. If these are different enrichments, the dataset-level `enrichment` cannot say which run is which; curate `run_enrichment` and the `mixed_enrichment` flag before pooling.");
            flags.Add("possibly_mixed_enrichment");
        }
        // G19: a deposit searched with its own chemistry says what was used and where each fact came from (D22). A fact read
        // from protocol text is a guess, flagged as one; the flag is prose (no ingest change).
        if (state?["chemistry"] is JsonObject chem)
        {
            string F(string k) => $"{chem[k]?["value"]} ({chem[k]?["source"]})";
            var cys = chem["cysteine_mods"]?.AsArray().Select(m => $"{m!["name"]} {(m["fixed"]!.GetValue<bool>() ? "fixed" : "variable")}").ToList() ?? new List<string>();
            notes.Add($"CHEMISTRY (G19): protease {F("protease")}; cysteine {(cys.Count == 0 ? "unmodified" : string.Join(" + ", cys))} ({chem["alkylation"]?["source"]}); label {F("label")}.");
            if (chem["guessed"]?.GetValue<bool>() == true) flags.Add("chemistry_guessed");
        }
        var qcFlags = state?["qc_payload_flags"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new List<string>();
        if (qcFlags.Count > 0)
            // A note, not a manifest flag: QC reporting is not a property of the data dataRepo stores (05_qc is not ingested).
            notes.Add($"QC REPORT: the qc-payload stage recorded {string.Join(", ", qcFlags)}; see 05_qc/provenance.json. The search and its results are unaffected.");
        notes.Add($"Searched by PXReprise under profile {profile.Key}" + (extras.Count > 0 ? $", with extra databases {string.Join(", ", extras)}" : "") + ".");
        notes.Add($"{Capitalise(e.Organism)}; {Uncapitalise(profile.Description)}. Raw spectra were deleted after the search; PRIDE is the source of truth and the fetch provenance holds every SHA-256.");

        string run = Path.GetRelativePath(workRoot, runDir).Replace('\\', '/');
        string entry = $"""

  - accession: {e.Accession}
    status: include
    title: {JsonSerializer.Serialize(e.Title)}
    run: {run}
    stages:
      qc: 02b_qc
      search: 04_search
    search_results: 04_search/mm/Task3SearchTask
    files: {nfiles}
    organism: NCBITaxon:{taxon}
    acquisition: DDA
    quant_method: {(profile.QuantMethod == "flashlfq" ? "label-free" : profile.QuantMethod)}
    metamorpheus: "{mm}"
    provenance_schema: aging-provenance/3
    enrichment: [{enrichment}]
    flags: [{string.Join(", ", flags)}]
    notes: >
      {string.Join(" ", notes)}

""";
        // Write beside it and rename over it: a reader (dataRepo, aging's publish) must never see a half-written manifest
        // (dataRepo 010 caught one). On one volume the rename is atomic.
        string tmp = manifestPath + ".pxreprise-tmp";
        File.WriteAllText(tmp, text.TrimEnd('\n', '\r') + "\n" + entry.TrimEnd('\n') + "\n");
        File.Move(tmp, manifestPath, overwrite: true);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Lower-cases a leading capital unless the word is an acronym ("Label-free" but not "DDA").</summary>
    private static string Uncapitalise(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
