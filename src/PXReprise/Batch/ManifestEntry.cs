using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Config;

namespace PXReprise.Batch;

/// <summary>
/// Appends a dataset's <c>include</c> entry to the question's dataRepo manifest, in the shape `datarepo ingest` reads
/// (the aging batch's entry, field for field). Idempotent: an accession already present is left alone, except once after
/// a re-search, which replaces its entry and keeps what was written in the old one.
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
        // A re-search (batch research) replaces the deposit's entry once, keeping what its owner wrote in it: the old
        // status, any flags, and the old notes, quoted as what the entry said before (aging curates notes by hand).
        var research = state?["researched"] is JsonArray rs && rs.Count > 0 ? rs[^1] as JsonObject : null;
        string marker = research is null ? "" : $"RE-SEARCHED {research["utc"]}";
        (string Status, List<string> Flags, string Notes, string Kept)? earlier = null;
        if (text.Contains($"accession: {e.Accession}\n") || text.Contains($"accession: {e.Accession}\r"))
        {
            if (research is null) return;
            var (start, end) = Block(text, e.Accession);
            string old = text[start..end];
            if (old.Contains(marker, StringComparison.Ordinal)) return;
            earlier = (Field(old, "status") ?? "include",
                       (Field(old, "flags") ?? "").Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                       Notes(old), Unowned(old));
            text = text[..start].TrimEnd('\n', '\r') + (end < text.Length ? "\n\n" : "\n") + text[end..];
        }
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
        // From what the search did (its provenance flags), no longer assumed: an SDRF design and the search's own SDRF exist
        // since label-free-dda@4 (D28).
        var searchFlags = search["flags"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new List<string> { "no_design_file", "no_output_sdrf" };
        var flags = new[] { "no_design_file", "no_output_sdrf" }.Where(f => searchFlags.Any(x => x.StartsWith(f + ":", StringComparison.Ordinal) || x == f)).ToList();
        // D28 (D-2): a question exception changed this deposit's caps or chose its runs; never silent.
        if (state?["exception"]?.GetValue<string>() is { } exception)
        {
            notes.Add($"QUESTION EXCEPTION: {exception}.");
            flags.Add("question_exception");
        }
        // aging 043 (PXR-A37): too few cysteine peptides for a whole proteome points to an alkylant the search did not use.
        if (enrichment == "none" && searchFlags.FirstOrDefault(f => f.StartsWith("cysteine_poor:", StringComparison.Ordinal)) is { } cysPoor)
        {
            flags.Add("cysteine_poor");
            // aging 098 (PXR-A99): with no alkylation, only free cysteines are found and disulfide-bonded ones stay invisible,
            // so a low share is what the chemistry predicts, not a missed alkylant.
            bool unalkylated = state?["chemistry"]?["alkylation"]?["value"]?.GetValue<string>() == "none";
            notes.Add($"CYSTEINE-POOR: {cysPoor["cysteine_poor: ".Length..]}."
                + (unalkylated ? $" Expected here: the cysteines were not alkylated (alkylation none, {state!["chemistry"]!["alkylation"]!["source"]}), so only free cysteines are identified and disulfide-bonded ones are not." : ""));
        }
        if (enrichment != "none")
        {
            // v0.4.17: the enrichment's own words (enrichment_evidence); screen_evidence is the acquisition call's, which a
            // state written before 0.4.17 holds instead, usually empty.
            string ev = (state?["enrichment_evidence"]?.GetValue<string>() ?? state?["screen_evidence"]?.GetValue<string>() ?? "").Replace('"', '\'');
            string says = ev.Length > 0 ? $"the deposit's own text says '...{ev}...'" : "the deposit's own text names an enrichment";
            notes.Add($"ENRICHED ({enrichment}): {says}. Intensities describe the enriched material, not the whole proteome; never pool them with whole proteomes as abundance.");
            flags.Add("enriched");
        }
        // PXR-A88 (aging 082): the probe search's measurement used to stop at state.json, so an enrichment the text did not
        // state reached the store as a whole proteome. It is a prose flag and a note, like G16 below: which run is which is
        // curation (`enrichment`, `run_enrichment`, `mixed_enrichment`), because three probe runs are not every run.
        if (enrichment == "none" && state?["enrichment_measured"]?.GetValue<string>() is { Length: > 0 } measured)
        {
            notes.Add($"MEASURED ENRICHMENT ({measured}): the probe search found it in at least one probe run (01_experiment), but the text states none. Curate `enrichment` and, if only some runs are enriched, `run_enrichment` before pooling.");
            flags.Add("possibly_mixed_enrichment");
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
            if (!flags.Contains("possibly_mixed_enrichment")) flags.Add("possibly_mixed_enrichment");
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
        if (research is not null)
        {
            notes.Add($"{marker} (was {research["previous_profile"] ?? "?"}; the earlier output is in {research["moved_to"]}): {research["reason"]}.");
            if (earlier is { } was)
            {
                // The two the search decides are the NEW search's, never carried over.
                foreach (string f in was.Flags) if (!flags.Contains(f) && f is not ("no_design_file" or "no_output_sdrf")) flags.Add(f);
                if (was.Notes.Length > 0) notes.Add($"BEFORE THE RE-SEARCH this entry said: \"{was.Notes.Replace('"', '\'')}\"");
            }
        }

        string run = Path.GetRelativePath(workRoot, runDir).Replace('\\', '/');
        string entry = $"""

  - accession: {e.Accession}
    status: {earlier?.Status ?? "include"}
    title: {JsonSerializer.Serialize(e.Title)}
    run: {run}
    stages:
      qc: 02b_qc
      search: 04_search
    search_results: 04_search/mm/{SearchTaskFolder(profile)}
    files: {nfiles}
    organism: NCBITaxon:{taxon}
    acquisition: DDA
    quant_method: {(profile.QuantMethod == "flashlfq" ? "label-free" : profile.QuantMethod)}
    metamorpheus: "{mm}"
    provenance_schema: aging-provenance/3
    enrichment: [{enrichment}]
    flags: [{string.Join(", ", flags)}]
{earlier?.Kept ?? ""}    notes: >
      {string.Join(" ", notes)}

""";
        // Write beside it and rename over it: a reader (dataRepo, aging's publish) must never see a half-written manifest
        // (dataRepo 010 caught one). On one volume the rename is atomic.
        string tmp = manifestPath + ".pxreprise-tmp";
        File.WriteAllText(tmp, text.TrimEnd('\n', '\r') + "\n" + entry.TrimEnd('\n') + "\n");
        File.Move(tmp, manifestPath, overwrite: true);
    }

    /// <summary>
    /// The folder MetaMorpheus writes the Search task's results to: <c>Task{N}SearchTask</c>, N the Search task's place in
    /// the profile's task list, numbered as <c>SearchStage</c> numbers the TOMLs. A Search-only profile
    /// (label-free-dda-lowres@1) writes <c>Task1SearchTask</c>, which a hard-coded Task3 sent dataRepo looking past (G34).
    /// </summary>
    internal static string SearchTaskFolder(Profile profile)
    {
        int n = profile.Tasks.ToList().LastIndexOf("Search") + 1;
        if (n == 0) throw new InvalidOperationException($"{profile.Key} has no Search task, so it has no search results to ingest");
        return $"Task{n}SearchTask";
    }

    /// <summary>The span of <paramref name="acc"/>'s entry: from its <c>- accession:</c> line to the next entry or the end.</summary>
    internal static (int Start, int End) Block(string text, string acc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, $@"(?m)^[ \t]*- accession: {acc}\r?$");
        if (!m.Success) throw new InvalidOperationException($"{acc} has no manifest entry");
        int start = m.Index;
        var next = System.Text.RegularExpressions.Regex.Match(text[(m.Index + m.Length)..], @"(?m)^[ \t]*- accession: ");
        return (start, next.Success ? m.Index + m.Length + next.Index : text.Length);
    }

    /// <summary>
    /// The deposits the manifest excludes as copies of <paramref name="acc"/>: <c>status: exclude</c> with a <c>reason</c>
    /// that opens <c>duplicate_of: &lt;acc&gt;</c>, the form of aging's exclusion of PXD012985 (PXR-A8). Such a copy is not
    /// an original that <paramref name="acc"/> duplicates. v0.4.16: PXD011740's re-search (PXR-A103) was refused as a
    /// duplicate of PXD012985, its own excluded copy, searched in September before the duplicate screen existed.
    /// </summary>
    internal static IReadOnlySet<string> ExcludedCopiesOf(string text, string acc)
    {
        var copies = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^[ \t]*- accession: (\S+?)\r?$"))
        {
            var (start, end) = Block(text, m.Groups[1].Value);
            string block = text[start..end];
            if (System.Text.RegularExpressions.Regex.IsMatch(block, @"(?m)^[ \t]+status: exclude\r?$")
                && System.Text.RegularExpressions.Regex.IsMatch(block, $@"(?m)^[ \t]+reason:[ \t]*([>|]-?)?[ \t]*(\r?\n[ \t]+)?duplicate_of: {System.Text.RegularExpressions.Regex.Escape(acc)}\b"))
                copies.Add(m.Groups[1].Value);
        }
        return copies;
    }

    /// <summary>The keys this writer produces; any other key of an entry belongs to its owner (aging's `excluded_runs`,
    /// `run_enrichment`, `restrictions`, ...) and survives a re-search verbatim.</summary>
    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal)
    {
        "accession", "status", "title", "run", "stages", "search_results", "files", "organism", "acquisition", "quant_method",
        "metamorpheus", "provenance_schema", "enrichment", "flags", "notes",
    };

    /// <summary>Every key of the entry this writer does not own, with its nested lines, as written (LF line ends).</summary>
    internal static string Unowned(string block)
    {
        var lines = block.Replace("\r", "").Split('\n');
        var key = System.Text.RegularExpressions.Regex.Match(block, @"(?m)^([ \t]+)status:");
        int indent = key.Success ? key.Groups[1].Value.Length : 4;
        var kept = new System.Text.StringBuilder();
        bool keeping = false;
        foreach (string l in lines.Skip(1))
        {
            int ind = l.Length - l.TrimStart().Length;
            if (l.Trim().Length > 0 && ind == indent && System.Text.RegularExpressions.Regex.Match(l.Trim(), @"^([A-Za-z_][\w-]*):") is { Success: true } m)
                keeping = !Owned.Contains(m.Groups[1].Value);
            else if (l.Trim().Length > 0 && ind < indent) keeping = false;
            if (keeping && l.Trim().Length > 0) kept.Append(new string(' ', 4)).Append(l[indent..]).Append('\n');
        }
        return kept.ToString();
    }

    private static string? Field(string block, string key) =>
        System.Text.RegularExpressions.Regex.Match(block, $@"(?m)^[ \t]+{key}:[ \t]*([^\r\n]*)$") is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>A folded <c>notes: &gt;</c> block (or a one-line <c>notes:</c>) as one line of text.</summary>
    private static string Notes(string block)
    {
        var lines = block.Replace("\r", "").Split('\n');
        int i = Array.FindIndex(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[ \t]+notes:"));
        if (i < 0) return "";
        string first = lines[i][(lines[i].IndexOf("notes:", StringComparison.Ordinal) + 6)..].Trim();
        if (first is not (">" or ">-" or "|" or "|-")) return first.Trim('"');
        int indent = lines[i].Length - lines[i].TrimStart().Length;
        var body = lines.Skip(i + 1).TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > indent).Select(l => l.Trim());
        return string.Join(" ", body.Where(l => l.Length > 0));
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Lower-cases a leading capital unless the word is an acronym ("Label-free" but not "DDA").</summary>
    private static string Uncapitalise(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
