using System.Text.RegularExpressions;
using PXReprise.Cli;
using PXReprise.Discovery;

namespace PXReprise.Config;

public static class ProfileLoader
{
    public static Profile Load(string file)
    {
        var root = TomlSection.Load(file);
        string id = root.RequiredString("id");
        if (!Regex.IsMatch(id, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new ConfigException(file, $"'id' must be lower-case words joined by '-', not '{id}'");
        int version = checked((int)root.RequiredInteger("version"));
        if (version < 1) throw new ConfigException(file, "'version' must be 1 or more");
        var status = ParseEnum<ProfileStatus>(file, "status", root.OptionalString("status") ?? "available");
        string description = root.OptionalString("description") ?? "";

        var acc = root.RequiredTable("accepts");
        var accepts = new ProfileAccepts(
            acc.StringList("acquisition").Select(v => ParseEnum<AcquisitionMode>(file, "accepts.acquisition", v)).ToList(),
            acc.StringList("labelling").Select(v => ParseEnum<Labelling>(file, "accepts.labelling", v)).ToList(),
            acc.StringList("instrument").Select(v => ParseEnum<InstrumentClass>(file, "accepts.instrument", v)).ToList(),
            acc.StringList("file_types"),
            acc.OptionalBool("crosslinking") ?? false,
            acc.OptionalBool("nonspecific_cleavage") ?? false,
            acc.OptionalBool("top_down") ?? false);
        acc.RefuseUnknownKeys();

        var eng = root.RequiredTable("engine");
        string mm = eng.RequiredString("metamorpheus");
        // v0.4.26: a release ("1.1.12") or a commit pin ("master@3a6a76632"), see Search.MetaMorpheusPin.
        if (!Search.MetaMorpheusPin.TryParse(mm, out _))
            throw new ConfigException(file, $"engine.metamorpheus must name a release (\"1.1.12\") or a commit pin (\"master@3a6a76632\"), not '{mm}'");
        var tasks = eng.StringList("tasks", required: true);
        // "<category>\t<IdWithMotif>" as MetaMorpheus's ListOfModsGptmd holds them; checked against the pinned
        // MetaMorpheus's Mods files when a search is prepared, not here.
        var extraMods = eng.StringList("gptmd_extra_mods");
        // One library per organism: the first search writes it, every later one updates and consumes it (aging D33).
        bool library = eng.OptionalBool("spectral_library") ?? false;
        // Read so published profiles stay valid, but no longer used: a search's limits are the machine's (search_timeout_h,
        // search_stall_minutes), because how long it takes depends on the box and its load, not on the method.
        double timeout = eng.OptionalNumber("timeout_h") ?? 6;
        string chemistry = eng.OptionalString("chemistry") ?? "fixed";
        if (!Profile.Chemistries.Contains(chemistry))
            throw new ConfigException(file, $"engine.chemistry must be one of {string.Join(", ", Profile.Chemistries)}, not '{chemistry}'");
        // MetaMorpheus 1.1.12+ (#2816, #2870; sdrf PXR-E1/E2): each search writes experiment.sdrf.tsv, stamped with the
        // deposit's accession. A key the pinned MetaMorpheus lacks fails the task-file edit, never silently.
        bool writeSdrf = eng.OptionalBool("write_sdrf") ?? false;
        // D32 (G24): "fixed" keeps the task's DissociationType for every file; "per_file" writes each file's type, read from
        // its MS2 headers at QC, into its <raw>.toml, with low_res_product_tolerance for CID read in an ion trap (LowCID).
        string fragmentation = eng.OptionalString("fragmentation") ?? "fixed";
        if (!Profile.Fragmentations.Contains(fragmentation))
            throw new ConfigException(file, $"engine.fragmentation must be one of {string.Join(", ", Profile.Fragmentations)}, not '{fragmentation}'");
        string lowResTolerance = eng.OptionalString("low_res_product_tolerance") ?? Profile.DefaultLowResProductTolerance;
        if (!System.Text.RegularExpressions.Regex.IsMatch(lowResTolerance, @"^±\d+\.\d{4} (PPM|Absolute)$"))
            throw new ConfigException(file, $"engine.low_res_product_tolerance must be written as MetaMorpheus writes one, e.g. \"±0.5000 Absolute\", not '{lowResTolerance}'");
        // PXR-A68 (aging 072): HCD read in an ion trap (a Fusion/Lumos/Eclipse "HCD-IT" method) is searched as HCD with
        // low_res_product_tolerance. Off unless a profile turns it on, so an existing profile version never changes.
        bool ionTrapHcd = eng.OptionalBool("ion_trap_hcd") ?? false;
        if (ionTrapHcd && fragmentation != "per_file")
            throw new ConfigException(file, "engine.ion_trap_hcd needs fragmentation = \"per_file\": it is decided per file from the MS2 headers");
        // v0.4.28 (lowres@4): ion-trap HCD searched as LowCID, MetaMorpheus's low-res path, in the probe and the search. Its
        // evidence was measured under the probe's settings only (PXD022393: 107 PSMs on one file as HCD, 2,442 as LowCID).
        // In the full search LowCID lost 1-16% of PSMs (2026-10-21: PXD052631, PXD016430, PXD015170), so lowres@5 takes
        // it in the probe only (ion_trap_hcd_probe_lowcid, below).
        bool ionTrapHcdLowCid = eng.OptionalBool("ion_trap_hcd_lowcid") ?? false;
        if (ionTrapHcdLowCid && !ionTrapHcd)
            throw new ConfigException(file, "engine.ion_trap_hcd_lowcid needs engine.ion_trap_hcd = true: it says how ion-trap HCD is searched");
        // G40: modifications no UniMod entry describes (NBF, aging 072 PXR-A69), in a MetaMorpheus modification file shipped
        // under lists/, read here with mzLib's own reader (the one MetaMorpheus uses), so a malformed entry fails at load.
        string? customMods = eng.OptionalString("custom_modifications");
        var customIds = customMods is null ? null : CustomModificationIds(file, customMods);
        IReadOnlyDictionary<string, string>? thiolReagents = null;
        if (root.OptionalTable("thiol_reagents") is { } tr)
        {
            if (chemistry != "deposit")
                throw new ConfigException(file, "[thiol_reagents] needs engine.chemistry = \"deposit\": a reagent is read from each deposit");
            if (customIds is null)
                throw new ConfigException(file, "[thiol_reagents] names modifications from engine.custom_modifications, which is not set");
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string reagent in tr.Keys)
            {
                if (!Discovery.ChemistryDetector.UnmodelledThiolReagentNames.Contains(reagent))
                    throw new ConfigException(file, $"[thiol_reagents] '{reagent}' is not a reagent the chemistry reads (one of {string.Join(", ", Discovery.ChemistryDetector.UnmodelledThiolReagentNames)})");
                string modId = tr.RequiredString(reagent);
                if (!customIds.Contains(modId))
                    throw new ConfigException(file, $"[thiol_reagents] '{reagent}' names '{modId.Replace("\t", " / ")}', which {customMods} does not define");
                if (!modId.EndsWith(" on C", StringComparison.Ordinal))
                    throw new ConfigException(file, $"[thiol_reagents] '{reagent}' must name a modification on C, not '{modId.Replace("\t", " / ")}'");
                map[reagent] = modId;
            }
            thiolReagents = map;
        }
        // v0.4.29 (ptmQtl PXR-P18, PXR-P19): a side reaction the chemistry reads -> a GPTMD modification, never variable (the
        // user, 2026-10-25: variable only for a modification on most peptides, as oxidised methionine is). GPTMD may put it
        // where the search holds another modification on the residue (MetaMorpheus GptmdEngine.GetPossibleMods), so ring-
        // opened NEM sits beside NEM and propionamide beside fixed carbamidomethyl.
        IReadOnlyDictionary<string, string>? artifacts = null;
        if (root.OptionalTable("chemistry_artifacts") is { } ca)
        {
            if (chemistry != "deposit")
                throw new ConfigException(file, "[chemistry_artifacts] needs engine.chemistry = \"deposit\": a side reaction is read from each deposit");
            if (!tasks.Contains("Gptmd"))
                throw new ConfigException(file, "[chemistry_artifacts] adds modifications to GPTMD, and engine.tasks has no Gptmd task");
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string kind in ca.Keys)
            {
                if (!Discovery.ChemistryDetector.ArtifactKinds.Contains(kind))
                    throw new ConfigException(file, $"[chemistry_artifacts] '{kind}' is not a side reaction the chemistry reads (one of {string.Join(", ", Discovery.ChemistryDetector.ArtifactKinds)})");
                string modId = ca.RequiredString(kind);
                if (!modId.Contains('\t') || !modId.EndsWith(" on C", StringComparison.Ordinal))
                    throw new ConfigException(file, $"[chemistry_artifacts] '{kind}' must name \"Type\\tId on C\", not '{modId.Replace("\t", " / ")}'");
                map[kind] = modId;
            }
            artifacts = map;
        }
        // D32/D34/D35 slice 3: a short search of the probe files against every species database, measuring species,
        // cysteine share and phospho/GG before the full download. Off unless a profile turns it on.
        bool probeSearch = eng.OptionalBool("probe_search") ?? false;
        double probeMinutes = eng.OptionalNumber("probe_search_minutes") ?? 60;
        if (probeMinutes <= 0) throw new ConfigException(file, "engine.probe_search_minutes must be positive");
        // A search without a Calibration task keeps MetaMorpheus's ±5 ppm precursor window and FlashLFQ's 5 ppm, which assume
        // calibrated masses: PXD045821's precursors were +8.7 ppm off, and 0.37% of its spectra were identified (2026-10-17).
        // "probe" widens both by the offset the probe search measured; a calibrated search has calibration's windows.
        string precursorWindow = eng.OptionalString("precursor_window") ?? "default";
        if (!Profile.PrecursorWindows.Contains(precursorWindow))
            throw new ConfigException(file, $"engine.precursor_window must be one of {string.Join(", ", Profile.PrecursorWindows)}, not '{precursorWindow}'");
        if (precursorWindow == "probe" && !probeSearch)
            throw new ConfigException(file, "engine.precursor_window = \"probe\" needs probe_search = true: the offset is measured by the probe search");
        // v0.4.29 (lowres@5): ion-trap HCD as LowCID in the probe search only. Under the probe's settings HCD finds far less
        // (one PXD016430 file: 3,300 PSMs as HCD, 9,173 as LowCID), so a deposit like PXD022393 parks as identifying nothing;
        // in the full search HCD finds more (that file: 15,939 under lowres@2, 13,043 as LowCID under lowres@4).
        bool ionTrapHcdProbeLowCid = eng.OptionalBool("ion_trap_hcd_probe_lowcid") ?? false;
        if (ionTrapHcdProbeLowCid && !ionTrapHcd)
            throw new ConfigException(file, "engine.ion_trap_hcd_probe_lowcid needs engine.ion_trap_hcd = true: it says how ion-trap HCD is searched in the probe");
        if (ionTrapHcdProbeLowCid && !probeSearch)
            throw new ConfigException(file, "engine.ion_trap_hcd_probe_lowcid needs probe_search = true: it is about the probe search");
        if (ionTrapHcdProbeLowCid && ionTrapHcdLowCid)
            throw new ConfigException(file, "engine.ion_trap_hcd_probe_lowcid and engine.ion_trap_hcd_lowcid are set together: ion_trap_hcd_lowcid already takes LowCID in the probe and the search");
        if (precursorWindow == "probe" && tasks.Contains("Calibration"))
            throw new ConfigException(file, "engine.precursor_window = \"probe\" is for a search without calibration; a Calibration task sets its own windows");
        eng.RefuseUnknownKeys();

        var dbs = new SortedDictionary<string, OrganismDatabase>(StringComparer.Ordinal);
        if (root.OptionalTable("databases") is { } dbTable)
        {
            foreach (string organism in dbTable.Keys.ToList())
            {
                var o = dbTable.RequiredTable(organism);
                string? proteome = o.OptionalString("proteome"), uniprot = o.OptionalString("uniprot");
                if ((proteome is null) == (uniprot is null))
                    throw new ConfigException(file, $"databases.{organism} needs exactly one of 'proteome' (a prepared file) or 'uniprot' (a proteome ID)");
                if (uniprot is not null && !Regex.IsMatch(uniprot, "^UP[0-9]{9}$"))
                    throw new ConfigException(file, $"databases.{organism}.uniprot must be a UniProt proteome ID like UP000005640, not '{uniprot}'");
                dbs[organism] = new OrganismDatabase(proteome, o.StringList("extra"),
                    o.OptionalInteger("taxon") is { } tx ? checked((int)tx) : null, uniprot);
                o.RefuseUnknownKeys();
            }
            dbTable.RefuseUnknownKeys();
        }

        var con = root.RequiredTable("contaminants");
        // "panel": a file in the pinned MetaMorpheus's Contaminants folder. "file": a panel shipped with the profiles under
        // lists/ (D35: MetaMorpheus's NoUPS panel, released after the pin). Exactly one; "exclude" reduces a "panel" only.
        string? panel = con.OptionalString("panel");
        string? shipped = con.OptionalString("file");
        string? exclude = con.OptionalString("exclude");
        if ((panel is null) == (shipped is null))
            throw new ConfigException(file, "[contaminants] needs exactly one of panel (a MetaMorpheus file) or file (shipped under lists/)");
        if (shipped is not null && exclude is not null)
            throw new ConfigException(file, "[contaminants] exclude reduces a MetaMorpheus panel; a shipped file is used as it is");
        if (shipped is not null && !shipped.Contains("Contaminants", StringComparison.Ordinal))
            throw new ConfigException(file, $"[contaminants] file '{shipped}' must keep \"Contaminants\" in its name: MetaMorpheus marks a database as contaminant by its name");
        con.RefuseUnknownKeys();

        var quant = root.RequiredTable("quant");
        string method = quant.RequiredString("method");
        bool mbr = quant.OptionalBool("mbr") ?? false;
        // "sdrf": ExperimentalDesign.tsv from the question's curated SDRF, else the deposit's (G15). Changes quantification.
        string design = quant.OptionalString("design") ?? "none";
        if (!Profile.Designs.Contains(design))
            throw new ConfigException(file, $"[quant] design must be one of {string.Join(", ", Profile.Designs)}, not '{design}'");
        quant.RefuseUnknownKeys();

        var dep = root.RequiredTable("deposit");
        var deposit = new DepositPolicy(
            dep.OptionalBool("whole") ?? true,
            checked((int)(dep.OptionalInteger("max_files") ?? 60)),
            dep.OptionalString("probe") ?? "probe_spread",
            dep.OptionalInteger("max_est_ms2") ?? 0,
            checked((int)(dep.OptionalInteger("max_file_mb") ?? 5000)),
            checked((int)(dep.OptionalInteger("defer_if_median_file_mb") ?? 0)));
        dep.RefuseUnknownKeys();

        var qc = root.RequiredTable("qc");
        var gates = new QcGates(
            qc.RequiredString("ms2_analyzer"),
            qc.OptionalNumber("min_fraction_orbitrap_hcd") ?? 0,
            checked((int)(qc.OptionalInteger("min_ms2") ?? 0)),
            qc.StringList("excludable"),
            qc.OptionalNumber("max_excluded_frac") ?? 0);
        qc.RefuseUnknownKeys();

        root.RefuseUnknownKeys();
        return new Profile(id, version, status, description, accepts, mm, tasks, extraMods, library, timeout, dbs,
            panel ?? Path.GetFileName(shipped!), exclude,
            method, mbr, deposit, gates, Path.GetDirectoryName(Path.GetFullPath(file))!, design, chemistry, writeSdrf, shipped, fragmentation, lowResTolerance, probeSearch, probeMinutes, ionTrapHcd,
            customMods, thiolReagents, precursorWindow, ionTrapHcdLowCid, ionTrapHcdProbeLowCid, artifacts);
    }

    /// <summary>
    /// G40: every <c>Type\tIdWithMotif</c> a shipped modification file defines, read by mzLib's ModificationLoader, the
    /// reader MetaMorpheus applies to its Mods folder. A file it reads with a warning, or no modification at all, is refused:
    /// MetaMorpheus would drop that entry and the search would run without it.
    /// </summary>
    private static HashSet<string> CustomModificationIds(string file, string name)
    {
        if (name != Path.GetFileName(name) || !name.EndsWith(".txt", StringComparison.Ordinal))
            throw new ConfigException(file, $"engine.custom_modifications must be a .txt file name under lists/, not '{name}'");
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "lists", name);
        if (!File.Exists(path)) throw new ConfigException(file, $"engine.custom_modifications: {path} does not exist");
        var mods = Omics.Modifications.IO.ModificationLoader.ReadModsFromFile(path, out var warnings).ToList();
        if (warnings.Count > 0)
            throw new ConfigException(file, $"{name}: mzLib reads it with warnings: {string.Join("; ", warnings.Select(w => w.Item2))}");
        if (mods.Count == 0) throw new ConfigException(file, $"{name} defines no modification");
        return mods.Select(m => $"{m.ModificationType}\t{m.IdWithMotif}").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every <c>*.toml</c> in a directory, keyed by <c>id@version</c>; a duplicate key is refused.</summary>
    public static IReadOnlyDictionary<string, Profile> LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new UsageException($"no profiles directory: {dir}");
        // v0.4.28: PXReprise's own digestion agents, merged into mzLib's dictionary before any chemistry resolves a name.
        string customProteases = Path.Combine(dir, "lists", Search.CustomProteases.FileName);
        if (File.Exists(customProteases)) Search.CustomProteases.Load(customProteases);
        var all = new SortedDictionary<string, Profile>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(dir, "*.toml").OrderBy(f => f, StringComparer.Ordinal))
        {
            var p = Load(f);
            if (!all.TryAdd(p.Key, p)) throw new ConfigException(f, $"profile {p.Key} is defined twice");
        }
        return all;
    }

    internal static T ParseEnum<T>(string file, string key, string value) where T : struct, Enum
    {
        foreach (var e in Enum.GetValues<T>())
            if (Snake(e.ToString()) == value) return e;
        throw new ConfigException(file,
            $"'{key}' has '{value}'; allowed: {string.Join(", ", Enum.GetValues<T>().Select(e => Snake(e.ToString())))}");
    }

    internal static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
