using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;
using PXReprise.Qc;

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
    bool UseLibrary = true,
    string? CuratedDesign = null,
    IReadOnlyList<string>? DesignConditionColumns = null,
    Discovery.DepositChemistry? Chemistry = null);   // G19: required by a profile with [engine] chemistry = "deposit"

public sealed record SearchOutcome(bool Success, int ExitCode, bool TimedOut, int? Psms1Pct, IReadOnlyList<string> Flags, string ProvenanceFile);

/// <summary>
/// One dataset's MetaMorpheus search under a profile: the QC gate, the pinned engine's default task files edited with
/// only the profile's settings, one CMD process, the success checks, the derived measurements and an
/// <c>aging-provenance/3</c> record. It reproduces aging's search_mm.py; the pieces are proven against the aging corpus.
/// </summary>
public static class SearchStage
{
    public const string SkipQuantSuffix = ". Skipping quantification";

    /// <summary>The cysteine-containing share of 1% target peptides below which a search is flagged cysteine_poor.</summary>
    public const double CysteinePoorBelow = 0.05;

    /// <summary>
    /// The share of target peptides at q &lt;= 0.01 in <c>AllPeptides.psmtsv</c> whose base sequence holds a C, or null
    /// when the table lacks the columns or has no such peptide.
    /// </summary>
    public static double? CysteineShare(string allPeptides)
    {
        var header = Tsv.Header(allPeptides);
        if (!new[] { "QValue", "Decoy/Contaminant/Target", "Base Sequence" }.All(header.Contains)) return null;
        int n = 0, c = 0;
        foreach (var row in Tsv.Read(allPeptides))
        {
            if (row["Decoy/Contaminant/Target"] != "T" || !(Tsv.Number(row["QValue"]) <= 0.01)) continue;
            n++;
            if (row["Base Sequence"].Contains('C')) c++;
        }
        return n == 0 ? null : SearchMetrics.PyRound((double)c / n, 4);
    }

    /// <summary>
    /// Null when the protein-group table was quantified with the design: one <c>Intensity_</c> sample column per
    /// distinct (condition, biorep) of <c>ExperimentalDesign.tsv</c>, each condition named in at least one. Design-less,
    /// MetaMorpheus writes one column per FILE instead. Otherwise the reason.
    /// </summary>
    public static string? QuantMissesDesign(string designFile, string proteinGroupsFile)
    {
        var rows = Tsv.Read(designFile).Select(d => (Condition: d.GetValueOrDefault("Condition", ""), Biorep: d.GetValueOrDefault("Biorep", ""))).ToList();
        int samples = rows.Distinct().Count();
        var columns = Tsv.Header(proteinGroupsFile).Where(c => c.StartsWith("Intensity_", StringComparison.Ordinal)).ToList();
        var unnamed = rows.Select(x => x.Condition).Distinct(StringComparer.Ordinal)
            .Where(c => !columns.Any(col => col.StartsWith($"Intensity_{c}_", StringComparison.Ordinal))).ToList();
        if (columns.Count != samples || unnamed.Count > 0)
            return $"{columns.Count} Intensity_ columns for {samples} design samples"
                   + (unnamed.Count > 0 ? $"; condition(s) with no column: {string.Join(", ", unnamed)}" : "");
        return null;
    }
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
        // MetaMorpheus reads ExperimentalDesign.tsv from beside the first spectra file; written here, before any task runs.
        var design = p.Design == "sdrf" ? DesignStage.Prepare(r.SpectraDir, files, r.CuratedDesign, r.DesignConditionColumns, r.Organism, r.Accession) : null;

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
        if (r.ExcludeFiles.Count > 0) prov.Set("excluded_files", ExcludedFilesRecord(r.ExcludeFiles, qc));
        if (design is not null)
        {
            prov.Set("experimental_design", design);
            if (design["written"]!.GetValue<bool>())
            {
                prov.Input(design["sdrf"]!.GetValue<string>());
                prov.Note($"experimental design from the {design["source"]} SDRF: {design["files"]} files, conditions {string.Join(", ", design["conditions"]!.AsArray().Select(c => c!.GetValue<string>()))}");
            }
        }
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
        var chem = p.Chemistry == "deposit"
            ? r.Chemistry ?? throw new UsageException($"{p.Key} reads each deposit's chemistry (G19), and none was given for this search")
            : null;
        if (chem?.Park is { } park) throw new SearchSetupException($"the deposit's chemistry cannot be searched as read ({park})");
        var settings = new TaskSettings(m.MaxThreads, p.MatchBetweenRuns, p.GptmdExtraMods, SpectralLibraryMode: lib?.Mode,
            Protease: chem is { ProteasePerFile: null } ? chem.Protease.Value : null,
            FixedCysMods: chem?.CysMods.Where(c => c.Fixed).Select(c => c.MetaMorpheusId!).ToList(),
            VariableCysMods: chem?.CysMods.Where(c => !c.Fixed).Select(c => c.MetaMorpheusId!).ToList(),
            WriteSdrf: p.WriteSdrf, ProteomeXchangeAccession: p.WriteSdrf ? r.Accession : null);
        if (chem is not null)
        {
            prov.Set("chemistry", Chemistry(chem));
            if (chem.AnyGuessed) prov.Note("chemistry includes a guess read from protocol text (D22); see chemistry.*.source");
        }
        var fragmentation = FragmentationFromQc(qc, files, p);
        if (fragmentation is not null)
            prov.Set("fragmentation", new JsonObject(fragmentation.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject
                { ["dissociation_type"] = kv.Value.DissociationType, ["low_res"] = kv.Value.LowRes, ["evidence"] = kv.Value.Evidence }))));
        WritePerFileSettings(files, chem?.ProteasePerFile, fragmentation, p.LowResProductTolerance, prov);
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
        var (contaminants, panelRecord) = Contaminants(p, m);
        if (panelRecord is not null)
        {
            prov.Set("contaminant_panel", panelRecord);
            prov.Note($"contaminant panel reduced by {panelRecord["excluded"]!.AsArray().Count} entries ({Path.GetFileName(p.ContaminantExclude)}, D54)");
        }
        else if (p.ContaminantFile is not null)
        {
            prov.Set("contaminant_panel", ContaminantPanel.ShippedRecord(contaminants));
            prov.Note($"contaminant panel {Path.GetFileName(contaminants)}, shipped with the profile and used as it is (D35)");
        }
        // Never pre-create the settings dir: MetaMorpheus seeds it only when it does not exist (an empty one crashes it).
        string settingsDir = Path.Combine(m.MetaMorpheusSettingsRoot, release);
        Directory.CreateDirectory(m.MetaMorpheusSettingsRoot);
        InstallCustomModifications(p, chem, settingsDir, runner, prov);
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
        // D27: MetaMorpheus only WARNS when it cannot use a modification or a per-file protease, and searches on without it.
        foreach (string silent in new[] { "Unrecognized mod", "Problem parsing the file-specific toml" })
            if (log.Contains(silent, StringComparison.Ordinal))
            {
                ok = false;
                prov.Note($"FAILED (G19/D27): MetaMorpheus logged \"{silent}\", so the search did not use the chemistry it was given: {log.Split('\n').First(l => l.Contains(silent, StringComparison.Ordinal)).Trim()}");
            }
        // QuantProject U15: an ExperimentalDesign.tsv that fails MetaMorpheus's validation costs one Warn, and the search
        // quantifies design-less with no marker. The table must carry one sample column per design sample.
        string designFile = Path.Combine(r.SpectraDir, "ExperimentalDesign.tsv");
        if (ok && hasDesign && key["AllQuantifiedProteinGroups.tsv"] is { } pgFile && QuantMissesDesign(designFile, pgFile) is { } why)
        {
            ok = false;
            prov.Note($"FAILED (QuantProject U15): the run carries ExperimentalDesign.tsv but quantification did not use it: {why}");
        }
        prov.Set("success", (JsonNode)ok);
        if (run.ExitCode == 0 && key["AllQuantifiedProteinGroups.tsv"] is null)
            prov.Note("exit 0 but no AllQuantifiedProteinGroups.tsv: FlashLFQ failed silently (pyMM 003 Q4)");
        foreach (string? v in key.Values) if (v is not null) prov.Output(v);
        // Every database GPTMD wrote, each with its sha256 (G33): the search task loads these, not the inputs. Taking only
        // the first one found recorded the contaminant file and never the proteome the search actually used.
        foreach (string gptmd in Directory.Exists(mm) ? Directory.EnumerateFiles(mm, "*GPTMD.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
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
        // MetaMorpheus #2816: a failed SDRF write never fails the search; it leaves SdrfWriter_crash.txt (sdrf PXR-E1).
        string? sdrfCrash = Directory.Exists(mm) ? Directory.EnumerateFiles(mm, "SdrfWriter_crash.txt", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (sdrfCrash is not null)
        {
            flags.Add($"output_sdrf_failed: MetaMorpheus could not write the search's SDRF; the results stand ({Path.GetRelativePath(outDir, sdrfCrash)})");
            prov.Output(sdrfCrash);
        }
        if (!Directory.Exists(mm) || !Directory.EnumerateFiles(mm, "*.sdrf.tsv", SearchOption.AllDirectories).Any())
            flags.Add(p.WriteSdrf ? "no_output_sdrf: the profile asked for the search's SDRF and none was written"
                                  : "no_output_sdrf: no reanalysis SDRF written (the profile does not set write_sdrf)");
        else
            foreach (string sdrf in Directory.EnumerateFiles(mm, "*.sdrf.tsv", SearchOption.AllDirectories)) prov.Output(sdrf);
        // aging 043 (PXR-A37): a whole proteome runs 12-26% cysteine-containing target peptides at 1%; every chemistry
        // mismatch found so far (NEM, MMTS searched as carbamidomethyl) sat below 5%. A measurement and a flag, never a gate:
        // an enrichment is cysteine-poor by design, so the manifest keeps the flag only for non-enriched deposits.
        if (key["AllPeptides.psmtsv"] is { } peptides && CysteineShare(peptides) is double cys)
        {
            prov.Set("cysteine_peptide_share", (JsonNode)cys);
            if (cys < CysteinePoorBelow)
                flags.Add($"cysteine_poor: {Pct(cys)} of 1% target peptides contain C (whole proteomes: 12-26%); check the alkylant (aging PXR-A37)");
        }
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

    /// <summary>
    /// <c>excluded_files</c> in the shape aging's batch_runner.py wrote and dataRepo reads (DATAREPO-51, aging D52):
    /// <c>{files, reason}</c>. dataRepo drops these files from the deposit's runs; without the block, a QC-excluded blank
    /// injection came back as a run (PXD034059: 44 runs against the manifest's 43).
    /// </summary>
    internal static JsonObject ExcludedFilesRecord(IReadOnlyCollection<string> excluded, JsonObject qc)
    {
        var why = excluded.SelectMany(f => qc[f]?["fail_reasons"]?.AsArray().Select(x => x?.GetValue<string>()) ?? Enumerable.Empty<string?>())
                          .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        bool allFailedQc = excluded.All(f => qc[f]?["pass"]?.GetValue<bool>() == false);
        return new JsonObject
        {
            ["files"] = new JsonArray(excluded.OrderBy(f => f, StringComparer.Ordinal).Select(f => (JsonNode?)f).ToArray()),
            ["reason"] = allFailedQc && why.Count > 0
                ? $"D52: failed qc_spectra on {string.Join(", ", why)} (a blank or failed injection); excluded with a record instead of dropping the deposit"
                : "excluded from the search by --exclude",
        };
    }

    /// <summary>The marker on a per-file toml PXReprise writes, so a stale one is recognised and a hand-written one is never touched.</summary>
    internal const string PerFileTomlMarker = "# written by PXReprise for a per-file protease (G19); MetaMorpheus file-specific parameters";

    /// <summary>
    /// One <c>&lt;raw&gt;.toml</c> beside each raw file with the settings that file needs alone (MetaMorpheus's
    /// file-specific parameters; calibration carries them into X-calib.toml): its protease (G19/D27) and its
    /// fragmentation from its MS2 headers (D32, G24), with <paramref name="lowResProductTolerance"/> for MS2 read in an ion
    /// trap (LowCID, and HCD under <c>ion_trap_hcd</c>). Only keys
    /// MetaMorpheus's FileSpecificParameters accepts are written; one unknown key would make it drop the whole file.
    /// Our stale tomls are removed; a toml we did not write is refused.
    /// </summary>
    internal static void WritePerFileSettings(IEnumerable<string> rawFiles, IReadOnlyDictionary<string, string>? proteasePerFile,
        IReadOnlyDictionary<string, FragmentationCall>? fragmentationPerFile, string lowResProductTolerance, AgingProvenance prov)
    {
        foreach (string raw in rawFiles)
        {
            string name = Path.GetFileName(raw);
            string toml = Path.Combine(Path.GetDirectoryName(raw)!, Path.GetFileNameWithoutExtension(raw) + ".toml");
            bool ours = File.Exists(toml) && File.ReadLines(toml).FirstOrDefault() == PerFileTomlMarker;
            if (File.Exists(toml) && !ours)
                throw new SearchSetupException($"{toml} exists and was not written by PXReprise: MetaMorpheus would apply it to {name}");
            var lines = new List<string>();
            if (proteasePerFile?.TryGetValue(name, out string? protease) == true) lines.Add($"DigestionAgent = \"{protease}\"");
            if (fragmentationPerFile?.TryGetValue(name, out var call) == true)
            {
                if (call.DissociationType is null)
                    throw new SearchSetupException($"{name}: its fragmentation cannot be searched per file ({call.Unsupported}); QC should have failed it");
                lines.Add($"DissociationType = \"{call.DissociationType}\"");
                if (call.LowRes) lines.Add($"ProductMassTolerance = \"{lowResProductTolerance}\"");
            }
            if (lines.Count > 0)
            {
                File.WriteAllText(toml, PerFileTomlMarker + "\n" + string.Join("\n", lines) + "\n");
                prov.Input(toml);
            }
            else if (ours) File.Delete(toml);
        }
        if (proteasePerFile is not null)
            prov.Note($"per-file proteases (MetaMorpheus file-specific parameters): {string.Join("; ", proteasePerFile.GroupBy(kv => kv.Value).Select(g => $"{g.Key} x{g.Count()}"))}");
        if (fragmentationPerFile is not null)
            prov.Note($"per-file fragmentation from the MS2 headers (D32): {string.Join("; ", fragmentationPerFile.GroupBy(kv => kv.Value.LowRes ? $"{kv.Value.DissociationType} at {lowResProductTolerance}" : kv.Value.DissociationType).Select(g => $"{g.Key} x{g.Count()}"))}");
    }

    /// <summary>
    /// The contaminant database a search uses: the profile's shipped panel (D35), or MetaMorpheus's own, reduced by an
    /// exclusion list when the profile names one (aging D54). The file name keeps "Contaminants": MetaMorpheus marks a
    /// -d database as contaminant by its name. Shared by the search and the probe search, so both use the same panel.
    /// </summary>
    internal static (string Path, JsonObject? Record) Contaminants(Profile p, Machine m)
    {
        string shippedPanel = p.ContaminantFile is { } own
            ? p.ListPath(own)
            : Path.Combine(Path.GetDirectoryName(m.CmdFor(p.MetaMorpheus))!, "Contaminants", p.ContaminantPanel);
        if (!File.Exists(shippedPanel)) throw new UsageException($"contaminant database missing at {shippedPanel}");
        return ContaminantPanel.Resolve(shippedPanel,
            p.ContaminantExclude is null ? null : p.ListPath(p.ContaminantExclude), Path.Combine(m.DatabaseDir, "contaminants"));
    }

    /// <summary>
    /// G40: when the deposit's chemistry uses a modification from the profile's own file, installs that file into the
    /// MetaMorpheus settings folder the run reads, and records it as an input. Shared by the search and the probe search.
    /// </summary>
    internal static void InstallCustomModifications(Profile p, Discovery.DepositChemistry? chem, string settingsDir,
        MetaMorpheusRunner runner, AgingProvenance? prov)
    {
        var needed = chem?.CysMods.Where(c => c.CustomId is not null).Select(c => c.CustomId!).Distinct().ToList();
        if (needed is not { Count: > 0 }) return;
        string installed = CustomModificationFile.Install(p, settingsDir, needed, () => runner.SeedSettings(settingsDir));
        prov?.Input(installed);
        prov?.Note($"custom modification(s) {string.Join(", ", needed.Select(i => i.Replace('\t', ' ')))} from the profile's {p.CustomModifications}, installed into MetaMorpheus's settings folder (G40)");
    }

    /// <summary>D32: each searched file's fragmentation as QC decided it; null unless the profile writes it per file.</summary>
    internal static IReadOnlyDictionary<string, FragmentationCall>? FragmentationFromQc(JsonObject qc, IEnumerable<string> files, Profile p)
    {
        if (p.Fragmentation != "per_file") return null;
        var o = new Dictionary<string, FragmentationCall>(StringComparer.Ordinal);
        foreach (string f in files.Select(Path.GetFileName)!)
        {
            if (qc[f!]?["fragmentation"] is not JsonObject fr)
                throw new SearchSetupException($"{f}: the profile writes fragmentation per file, but QC recorded none for it: run `pxreprise qc` again");
            o[f!] = new FragmentationCall(fr["dissociation_type"]?.GetValue<string>(), fr["low_res"]?.GetValue<bool>() ?? false,
                fr["unsupported"]?.GetValue<string>(), fr["evidence"]?.GetValue<string>() ?? "");
        }
        return o;
    }

    /// <summary>The chemistry record in a search's provenance: each fact with its source and evidence.</summary>
    internal static JsonObject Chemistry(Discovery.DepositChemistry c)
    {
        static JsonObject Fact(Discovery.ChemistryFact f) => new() { ["value"] = f.Value, ["source"] = f.Source, ["evidence"] = f.Evidence };
        return new JsonObject
        {
            ["protease"] = Fact(c.Protease),
            ["protease_per_file"] = c.ProteasePerFile is null ? null : new JsonObject(c.ProteasePerFile.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["alkylation"] = Fact(c.Alkylation),
            ["cysteine_mods"] = new JsonArray(c.CysMods.Select(m => (JsonNode?)new JsonObject
                { ["name"] = m.Name, ["unimod"] = m.CustomId is null ? m.Unimod : null, ["fixed"] = m.Fixed, ["metamorpheus"] = m.MetaMorpheusId }).ToArray()),
            ["label"] = Fact(c.Label),
            ["guessed"] = c.AnyGuessed,
        };
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
