using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;

namespace PXReprise.Search;

public sealed record SearchRequest(
    Profile Profile,
    string Organism,
    Machine Machine,
    string SpectraDir,
    string OutDir,
    string QcDir,
    IReadOnlyList<string> ExtraDatabases,
    IReadOnlyList<string> ExcludeFiles,
    string? RunDate,
    string? Accession = null,
    bool UseLibrary = true);

public sealed record SearchOutcome(bool Success, int ExitCode, bool TimedOut, int? Psms1Pct, IReadOnlyList<string> Flags, string ProvenanceFile);

/// <summary>
/// One dataset's MetaMorpheus search under a profile: the QC gate, the pinned engine's default task files edited with
/// only the profile's settings, one CMD process, the success checks, the derived measurements and an
/// <c>aging-provenance/3</c> record. It reproduces aging's search_mm.py; the pieces are proven against the aging corpus.
/// </summary>
public static class SearchStage
{
    public const string SkipQuantSuffix = ". Skipping quantification";
    private const double FlagMinIdRate = 0.15, FlagMaxContaminantIntensityShare = 0.05;

    public static async Task<SearchOutcome> RunAsync(SearchRequest r, CancellationToken ct)
    {
        var p = r.Profile;
        var m = r.Machine;
        string outDir = Path.GetFullPath(r.OutDir);
        if (Directory.Exists(Path.Combine(outDir, "mm")))
            throw new UsageException($"refusing to reuse {Path.Combine(outDir, "mm")}: MetaMorpheus needs a fresh output directory");
        if (p.ContaminantExclude is not null && !File.Exists(p.ListPath(p.ContaminantExclude)))
            throw new UsageException($"{p.Key} names contaminant exclusions {p.ListPath(p.ContaminantExclude)}, which is missing");
        if (!p.Databases.TryGetValue(r.Organism, out var db))
            throw new UsageException($"{p.Key} has no database for '{r.Organism}' (has: {string.Join(", ", p.Databases.Keys)})");
        Directory.CreateDirectory(outDir);

        var (proteome, retrieval) = await ProteomeCache.ResolveAsync(db, m.DatabaseDir, ct).ConfigureAwait(false);
        var extras = db.Extra.Concat(r.ExtraDatabases).Select(x => Path.IsPathRooted(x) ? x : Path.Combine(m.DatabaseDir, x)).ToList();
        foreach (string x in extras.Where(x => !File.Exists(x))) throw new UsageException($"extra database {x} missing");

        // The QC gate: every searched file must have passed.
        string qcReport = Path.Combine(r.QcDir, "qc_report.json");
        if (!File.Exists(qcReport)) throw new UsageException($"no QC report at {qcReport}: run `pxreprise qc` first");
        var qc = JsonNode.Parse(File.ReadAllText(qcReport))!.AsObject();
        var files = Directory.EnumerateFiles(r.SpectraDir, "*.raw").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        var missing = r.ExcludeFiles.Where(x => files.All(f => Path.GetFileName(f) != x)).ToList();
        if (missing.Count > 0) throw new UsageException($"--exclude names {string.Join(", ", missing)}, which are not in {r.SpectraDir}");
        files = files.Where(f => !r.ExcludeFiles.Contains(Path.GetFileName(f))).ToList();
        if (files.Count == 0) throw new UsageException("every file is excluded");
        var failed = files.Where(f => qc[Path.GetFileName(f)]?["pass"]?.GetValue<bool>() != true).Select(Path.GetFileName).ToList();
        if (failed.Count > 0) throw new SearchSetupException($"QC failed or is missing for {string.Join(", ", failed)}");

        // The effective parameters, in the aging pipeline's shape, so provenance's params_file is a real file.
        var runner = new MetaMorpheusRunner(m.CmdFor(p.MetaMorpheus), m.Dotnet, m.DotnetRoot);
        string tasksDir = Path.Combine(outDir, "tasks");
        Directory.CreateDirectory(tasksDir);
        var search = new JsonObject
        {
            ["metamorpheus_cmd"] = m.CmdFor(p.MetaMorpheus),
            ["metamorpheus_version"] = p.MetaMorpheus,
            ["accept_thermo_licence"] = m.AcceptThermoLicence,
            ["tasks"] = new JsonArray(p.Tasks.Select(t => (JsonNode?)t).ToArray()),
            ["max_threads"] = m.MaxThreads,
            ["match_between_runs"] = p.MatchBetweenRuns,
            // The machine's limits, not the profile's timeout_h: how long a search takes depends on the box and its load.
            ["timeout_s"] = (int)(m.SearchTimeoutHours * 3600),
            ["stall_s"] = (int)(m.SearchStallMinutes * 60),
        };
        if (p.GptmdExtraMods.Count > 0) search["gptmd_extra_mods"] = new JsonArray(p.GptmdExtraMods.Select(x => (JsonNode?)x).ToArray());
        if (r.ExcludeFiles.Count > 0) search["exclude_files"] = new JsonArray(r.ExcludeFiles.Select(x => (JsonNode?)x).ToArray());
        string paramsFile = Path.Combine(outDir, "params.json");
        File.WriteAllText(paramsFile, new JsonObject
        {
            ["profile"] = p.Key, ["organism"] = r.Organism, ["run_date"] = r.RunDate, ["work_root"] = m.WorkRoot,
            ["search"] = search.DeepClone(),
            ["database"] = new JsonObject { ["prepared"] = proteome, ["extra_prepared"] = new JsonArray(extras.Select(x => (JsonNode?)x).ToArray()), ["include_contaminants"] = true },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var prov = new AgingProvenance("search_metamorpheus", m.WorkRoot, paramsFile, search, r.RunDate);
        var upstream = new List<string> { Path.Combine(r.QcDir, "provenance.json") };
        string fetchProv = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(r.SpectraDir))!, "provenance.json");
        if (File.Exists(fetchProv)) upstream.Add(fetchProv);
        prov.Upstream(upstream.ToArray());
        prov.Note($"profile {p.Key}, organism {r.Organism}; searched by PXReprise");
        if (retrieval is not null) prov.Set("database_retrieval", retrieval.DeepClone());
        string runtimeRoot = DotnetRuntimes.Root(m.DotnetRoot);
        prov.Set("dotnet_runtime", new JsonObject
        {
            ["root"] = runtimeRoot, ["private"] = m.DotnetRoot is not null,
            ["netcore_app_versions"] = new JsonArray(DotnetRuntimes.Snapshot(runtimeRoot).Select(v => (JsonNode?)v).ToArray()),
        });

        // 1. the pinned engine's defaults, then only the profile's settings changed
        var (release, genCmd) = runner.GenerateDefaults(tasksDir);
        prov.Command(genCmd);
        string cmd = m.CmdFor(p.MetaMorpheus);
        string dll = Path.ChangeExtension(cmd, ".dll");
        prov.Tool("MetaMorpheus", new JsonObject
        {
            ["release"] = release, ["commit"] = runner.Commit(), ["expected_release"] = p.MetaMorpheus,
            ["cmd"] = string.Join(" ", runner.Launch), ["cmd_dll_sha256"] = File.Exists(dll) ? Sha(dll) : null,
        });
        if (release != p.MetaMorpheus) throw new SearchSetupException($"MetaMorpheus is {release}, {p.Key} expects {p.MetaMorpheus}");

        // The organism's spectral library, resolved before the task files because it decides two of their values.
        LibraryPlan? lib = p.SpectralLibrary && r.UseLibrary ? SpectralLibrary.Plan(m.SpectralLibraryRoot, r.Organism) : null;
        if (p.SpectralLibrary && !r.UseLibrary)
            prov.Note("spectral library switched OFF for this search by the batch (a previous update-mode search failed)");
        if (lib is not null)
            prov.Set("spectral_library", new JsonObject
            {
                ["organism"] = lib.Organism, ["mode"] = lib.Mode, ["library_in"] = lib.LibraryIn,
                ["parent_version"] = lib.ParentVersion, ["registry"] = SpectralLibrary.RegistryPath(lib.Root),
            });

        var known = TaskFiles.KnownMods(Path.Combine(Path.GetDirectoryName(cmd)!, "Mods"));
        var settings = new TaskSettings(m.MaxThreads, p.MatchBetweenRuns, p.GptmdExtraMods, SpectralLibraryMode: lib?.Mode);
        var tomls = new List<string>();
        foreach (var (task, i) in p.Tasks.Select((t, i) => (Enum.Parse<TaskKind>(t), i + 1)))
        {
            string src = Path.Combine(tasksDir, TaskFiles.FileName(task));
            var (text, added) = TaskFiles.Edit(File.ReadAllText(src), task, settings, known);
            if (added.Count > 0)
                prov.Note($"GPTMD list extended with {added.Count} modification(s) beyond MetaMorpheus's default: {string.Join("; ", added.Select(a => a.Replace("\t", " / ")))}");
            string dst = Path.Combine(tasksDir, $"{i}_{TaskFiles.FileName(task)}");
            File.WriteAllText(dst, text);
            tomls.Add(dst);
        }
        foreach (string t in tomls) prov.Input(t);
        if (p.GptmdExtraMods.Count > 0) prov.Set("gptmd_extra_mods", new JsonArray(p.GptmdExtraMods.Select(x => (JsonNode?)x).ToArray()));

        // 2. one invocation for the whole chain
        string shippedPanel = Path.Combine(Path.GetDirectoryName(cmd)!, "Contaminants", p.ContaminantPanel);
        if (!File.Exists(shippedPanel)) throw new UsageException($"contaminant database missing at {shippedPanel}");
        // The panel as searched: the shipped one, or its content-addressed reduced copy (aging D54). The file name must
        // keep "Contaminants": MetaMorpheus marks a -d database as contaminant by its name.
        var (contaminants, panelRecord) = ContaminantPanel.Resolve(shippedPanel,
            p.ContaminantExclude is null ? null : p.ListPath(p.ContaminantExclude), Path.Combine(m.DatabaseDir, "contaminants"));
        if (panelRecord is not null)
        {
            prov.Set("contaminant_panel", panelRecord);
            prov.Note($"contaminant panel reduced by {panelRecord["excluded"]!.AsArray().Count} entries ({Path.GetFileName(p.ContaminantExclude)}, D54)");
        }
        // Never pre-create the settings dir: MetaMorpheus seeds it only when it does not exist (an empty one crashes it).
        string settingsDir = Path.Combine(m.MetaMorpheusSettingsRoot, release);
        Directory.CreateDirectory(m.MetaMorpheusSettingsRoot);
        var dbs = new List<string> { proteome, contaminants };
        dbs.AddRange(extras);   // after the proteome and the contaminants: the first -d stays "the search database"
        prov.Set("extra_databases", new JsonArray(extras.Select(x => (JsonNode?)x).ToArray()));
        if (lib?.LibraryIn is { } libIn)
        {
            // A library is another -d; MetaMorpheus recognises it by extension, and GPTMD forwards it down the chain.
            dbs.Add(libIn);
            prov.Note($"spectral library in use: {libIn} ({lib.Organism}, parent version {lib.ParentVersion})");
        }
        prov.Input(qcReport);
        foreach (string f in files) prov.Input(f);
        foreach (string d in dbs) prov.Input(d);
        var args = new List<string> { "-t" };
        args.AddRange(tomls);
        args.Add("-s"); args.AddRange(files);
        args.Add("-d"); args.AddRange(dbs);
        args.AddRange(new[] { "-o", Path.Combine(outDir, "mm"), "--mmsettings", settingsDir, "-v", "normal" });
        if (m.AcceptThermoLicence)
        {
            args.Add("--acceptThermoLicence");
            prov.Note("Thermo RawFileReader licence accepted via the machine configuration (operator's recorded choice).");
        }
        prov.Command(runner.Launch.Concat(args));
        var run = await runner.RunAsync(args, Path.Combine(outDir, "metamorpheus.log"), TimeSpan.FromHours(m.SearchTimeoutHours), ct,
            TimeSpan.FromMinutes(m.SearchStallMinutes)).ConfigureAwait(false);
        prov.Set("exit_code", (JsonNode)run.ExitCode);
        if (run.Stalled)
        {
            prov.Set("stalled_after_s", (JsonNode)(int)run.WallSeconds);
            prov.Note($"KILLED: MetaMorpheus used no CPU for {m.SearchStallMinutes} min (search_stall_minutes), so it was hung, and the process tree was terminated. Partial outputs are NOT a completed search.");
        }
        else if (run.TimedOut)
        {
            prov.Set("timed_out_after_s", (JsonNode)(int)(m.SearchTimeoutHours * 3600));
            prov.Note($"KILLED: the search exceeded the machine's ceiling ({m.SearchTimeoutHours} h, search_timeout_h) and the process tree was terminated. Partial outputs are NOT a completed search.");
        }
        var starts = run.Marks.Where(x => x.Event == "start").GroupBy(x => x.Task).ToDictionary(g => g.Key, g => g.First().Seconds);
        var perTask = new JsonObject();
        foreach (var e in run.Marks.Where(x => x.Event == "end" && starts.ContainsKey(x.Task)))
            perTask[e.Task] = new JsonObject { ["wall_seconds"] = Math.Round(e.Seconds - starts[e.Task], 1) };
        prov.Set("per_task_resources", perTask);

        // 3. success checks
        string mm = Path.Combine(outDir, "mm");
        string? sd = Directory.Exists(mm) ? Directory.EnumerateDirectories(mm, "Task*SearchTask").OrderBy(d => d).LastOrDefault() : null;
        var key = new[] { "AllPSMs.psmtsv", "AllPeptides.psmtsv", "AllQuantifiedProteinGroups.tsv", "AllQuantifiedPeptides.tsv" }
            .ToDictionary(n => n, n => sd is not null && File.Exists(Path.Combine(sd, n)) ? Path.Combine(sd, n) : null);
        bool ok = run.ExitCode == 0 && key.Values.All(v => v is not null);
        string log = File.ReadAllText(Path.Combine(outDir, "metamorpheus.log"));
        bool hasDesign = File.Exists(Path.Combine(r.SpectraDir, "ExperimentalDesign.tsv"));
        var skipped = log.Split('\n').Where(l => l.Contains(SkipQuantSuffix)).Select(l => l.Split('\t', 2)[^1].Trim()).ToList();
        if (skipped.Count > 0 && hasDesign)
        {
            ok = false;
            prov.Note($"FAILED (D48): the run carries ExperimentalDesign.tsv but MetaMorpheus skipped quantification with it: {skipped[0]}");
        }
        prov.Set("success", (JsonNode)ok);
        if (run.ExitCode == 0 && key["AllQuantifiedProteinGroups.tsv"] is null)
            prov.Note("exit 0 but no AllQuantifiedProteinGroups.tsv: FlashLFQ failed silently (pyMM 003 Q4)");
        foreach (string? v in key.Values) if (v is not null) prov.Output(v);
        foreach (string gptmd in Directory.Exists(mm) ? Directory.EnumerateFiles(mm, "*GPTMD.xml", SearchOption.AllDirectories).Take(1) : Enumerable.Empty<string>())
            prov.Output(gptmd);
        prov.Output(Path.Combine(outDir, "metamorpheus.log"));
        if (File.Exists(Path.Combine(mm, "allResults.txt"))) prov.Output(Path.Combine(mm, "allResults.txt"));

        // 4. derived measurements and automatic flags (they never fail the stage by themselves)
        var flags = new List<string>();

        // Register the library only when the search succeeded: a failed run's partial library must not become the
        // parent of the next one. A registration problem never fails a search that otherwise succeeded.
        if (lib is not null)
        {
            var libRec = (JsonObject)prov.Record["spectral_library"]!;
            if (ok && sd is not null)
            {
                try
                {
                    var written = SpectralLibrary.Register(lib, sd, $"{r.RunDate}/{r.Accession}".Trim('/'), r.Accession ?? "", release);
                    // A copy: the registry's own node already has a parent there.
                    libRec["written"] = written.DeepClone();
                    prov.Output(Path.Combine(lib.Root, written["path"]!.GetValue<string>()));
                    prov.Note($"spectral library {lib.Organism} v{(int)written["version"]!:000}: {(int)written["n_spectra"]!:N0} spectra, {written["path"]}");
                }
                catch (SearchSetupException e)
                {
                    libRec["error"] = e.Message;
                    prov.Note(e.Message);
                    flags.Add(e.Message);
                }
            }
            else prov.Note("spectral library NOT registered: the search did not succeed, so this run's library cannot become the parent of the next one");
        }
        if (log.Contains("Calibration failure")) flags.Add("calibration_failed: GPTMD/search ran on uncalibrated spectra (S7)");
        if (skipped.Count > 0)
            flags.Add("quantification_skipped: MetaMorpheus could not use an experimental design and quantified without it (fails the stage when the run carries ExperimentalDesign.tsv, D48)");
        int? psms = null;
        if (sd is not null && File.Exists(Path.Combine(sd, "results.txt")))
        {
            var summary = SearchSummary.Read(Path.Combine(sd, "results.txt"));
            int ms2 = qc.Where(kv => !r.ExcludeFiles.Contains(kv.Key)).Sum(kv => kv.Value!["ms2"]!.GetValue<int>());
            var idr = SearchMetrics.IdRate(summary, ms2);
            psms = idr.Psms1Pct;
            prov.Set("id_rate", new JsonObject
            {
                ["definition"] = Search.IdRate.Definition, ["psms_1pct"] = idr.Psms1Pct, ["ms2"] = idr.Ms2, ["rate"] = idr.Rate,
                ["psms_fdr_engine_1pct"] = idr.PsmsFdrEngine1Pct, ["psms_fdr_engine_definition"] = Search.IdRate.FdrEngineDefinition,
            });
            if (idr.Psms1Pct is { } n && ms2 > 0 && (double)n / ms2 < FlagMinIdRate)
                flags.Add($"low_id_rate: {n}/{ms2} = {Pct((double)n / ms2)} of MS2 identified (S3)");
        }
        if (sd is not null && File.Exists(Path.Combine(sd, "AllQuantifiedPeaks.tsv")))
        {
            var mb = SearchMetrics.Mbr(Path.Combine(sd, "AllQuantifiedPeaks.tsv"));
            prov.Set("mbr", new JsonObject
            {
                ["definition"] = MbrCounts.Definition, ["mbr_rows"] = mb.MbrRows, ["mbr_random_rt_won"] = mb.MbrRandomRtWon,
                ["mbr_kept"] = mb.MbrKept, ["msms_peaks"] = mb.MsmsPeaks, ["mbr_fdr_threshold"] = mb.MbrFdrThreshold, ["kept_over_msms"] = mb.KeptOverMsms,
            });
            if (mb.MsmsPeaks > 0 && mb.MbrKept > mb.MsmsPeaks)
                flags.Add($"mbr_kept_exceeds_msms: kept MBR {mb.MbrKept} > MSMS {mb.MsmsPeaks} (DEF-MBR-KEPT v1)");
        }
        if (key["AllPSMs.psmtsv"] is { } allPsms)
        {
            var (shared, sha) = SearchMetrics.SharedAccessions(contaminants, new[] { proteome }.Concat(extras));
            var c = SearchMetrics.Contamination(allPsms, key["AllQuantifiedProteinGroups.tsv"], shared, sha);
            prov.Set("contamination", new JsonObject
            {
                ["psm_share"] = c.PsmShare, ["psm_share_definition"] = Contamination.PsmShareDefinition,
                ["contaminant_psms"] = c.ContaminantPsms, ["target_plus_contaminant_psms"] = c.TargetPlusContaminantPsms,
                ["intensity_share_per_file"] = Obj(c.IntensitySharePerFile), ["intensity_share_definition"] = Contamination.IntensityShareDefinition,
                ["intensity_share_median"] = c.IntensityShareMedian, ["intensity_share_min"] = c.IntensityShareMin, ["intensity_share_max"] = c.IntensityShareMax,
                ["intensity_share_upper_per_file"] = Obj(c.IntensityShareUpperPerFile), ["intensity_share_upper_definition"] = Contamination.UpperDefinition,
                ["intensity_share_upper_median"] = c.IntensityShareUpperMedian, ["intensity_share_upper_max"] = c.IntensityShareUpperMax,
                ["shared_accessions_n"] = c.SharedAccessionsN, ["shared_accessions_sha256"] = c.SharedAccessionsSha256,
                ["top"] = new JsonArray(c.Top.Select(t => (JsonNode?)t).ToArray()),
            });
            if (c.IntensityShareMax > FlagMaxContaminantIntensityShare)
                flags.Add($"high_contamination: {Pct(c.IntensityShareMax)} of protein intensity in the worst file, {Pct(c.IntensityShareMedian)} median across {c.IntensitySharePerFile.Count} files (DEF-QC-9 v3.5)");
        }
        if (!hasDesign)
            flags.Add("no_design_file: FlashLFQ treated each file as its own biorep under one blank condition; no normalization; not usable for condition comparisons (owner: QuantProject projection)");
        if (!Directory.Exists(mm) || !Directory.EnumerateFiles(mm, "*.sdrf.tsv", SearchOption.AllDirectories).Any())
            flags.Add("no_output_sdrf: no reanalysis SDRF written (WriteSdrf is MetaMorpheus #2816, unreleased)");
        if (p.MatchBetweenRuns && files.Count < 2)
            prov.Note("MatchBetweenRuns is on but only one spectra file was searched: MBR has nothing to transfer.");

        double avgCores = run.WallSeconds > 0 ? Math.Round(run.CpuSeconds / run.WallSeconds, 2) : 0;
        prov.Set("expected_cores", (JsonNode)m.MaxThreads);
        if (avgCores > 0 && avgCores < 0.5 * m.MaxThreads) flags.Add($"low_core_use: {avgCores} avg cores of {m.MaxThreads} allowed (S5)");
        prov.Set("flags", new JsonArray(flags.Select(f => (JsonNode?)f).ToArray()));
        prov.Set("resources", new JsonObject
        {
            ["wall_seconds"] = Math.Round(run.WallSeconds, 1), ["cpu_seconds"] = Math.Round(run.CpuSeconds, 1),
            ["avg_cores_used"] = avgCores, ["peak_rss_gb"] = run.PeakRssGb,
        });
        string provFile = prov.Write(outDir);
        return new SearchOutcome(ok, run.ExitCode, run.TimedOut, psms, flags, provFile);
    }

    private static string Pct(double x) => (x * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static JsonObject Obj(IReadOnlyDictionary<string, double?> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v;
        return o;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
