using System.Text.Json.Nodes;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Provenance;
using PXReprise.Qc;

namespace PXReprise.Search;

/// <summary>
/// A short MetaMorpheus search of a deposit's probe files, before the full download (D32/D34/D35; PLAN_search-settings.md
/// slice 3). It searches EVERY species database the profile has, plus its contaminant panel, so an unlisted or wrong
/// species shows, with variable phospho, GG and acetyl-K, so an unstated enrichment shows, and with the deposit's chemistry, so a
/// cysteine chemistry the text did not state shows as missing cysteine peptides. Its only output is the measurements: it
/// is never delivered.
/// <para>
/// The settings follow the oracle of 2026-10-07 on MetaMorpheus 1.1.12: Classic search (no index build); no
/// quantification, individual files, mzIdentML, pepXML, library or pruned database; decoys and high-q PSMs not written;
/// contaminants written (they must be seen to be excluded); one PEP pass with SSRCalc when PEP runs at all (over 1,000
/// PSMs; a smaller probe skips it on its own). MaxModsForPeptide stays at MetaMorpheus's 2.
/// </para>
/// </summary>
public static class ProbeSearch
{
    /// <summary>The variable modifications added to the probe's search, as MetaMorpheus 1.1.12's Mods.txt names them.</summary>
    public static readonly string[] EnrichmentMods =
    {
        "Common Biological\tPhosphorylation on S", "Common Biological\tPhosphorylation on T", "Common Biological\tPhosphorylation on Y",
        "Trypsin Digested\tGG (Ubiquitination Site) on K",
        // PXR-A88 (aging 082): an acetyl-lysine capture whose text and file names say nothing (PXD061654) shows only here.
        "Common Biological\tAcetylation on K",
    };

    /// <summary>The settings a probe search sets on the pinned MetaMorpheus's default Search task (key, TOML value).</summary>
    public static readonly (string Key, string Value)[] FastSettings =
    {
        ("DoLabelFreeQuantification", "false"), ("WriteIndividualFiles", "false"), ("WriteMzId", "false"),
        ("WritePepXml", "false"), ("WriteSpectralLibrary", "false"), ("UpdateSpectralLibrary", "false"),
        ("WritePrunedDatabase", "false"), ("WriteDecoys", "false"), ("WriteHighQValuePsms", "false"),
        ("WriteContaminants", "true"), ("IterativePepTraining", "false"), ("RTPredictorName", "\"SSRCalc\""),
    };

    /// <summary>
    /// The probe's precursor window. The probe runs no calibration, and MetaMorpheus's default ±5 ppm missed nearly every
    /// match on an instrument off by +7.6 ppm (PXD051644, 2026-10-17: 12-31 PSMs a run, parked waiting_no_identifications;
    /// its calibrated search found 24,449). ±20 ppm takes such an offset; the probe only counts, it delivers nothing.
    /// </summary>
    public const string PrecursorTolerance = "±20.0000 PPM";

    /// <summary>The probe's Search task text, from the pinned MetaMorpheus's default (pure; tested without MetaMorpheus).</summary>
    public static string TaskText(string defaultSearchToml, Machine m, DepositChemistry? chem, IReadOnlySet<(string, string)> known)
    {
        var settings = new TaskSettings(m.MaxThreads, MatchBetweenRuns: false, GptmdExtraMods: Array.Empty<string>(), SearchType: "Classic",
            Protease: chem is { ProteasePerFile: null } ? chem.Protease.Value : null,
            FixedCysMods: chem?.CysMods.Where(c => c.Fixed).Select(c => c.MetaMorpheusId!).ToList(),
            VariableCysMods: chem?.CysMods.Where(c => !c.Fixed).Select(c => c.MetaMorpheusId!).ToList(),
            PrecursorMassTolerance: PrecursorTolerance);
        var (text, _) = TaskFiles.Edit(defaultSearchToml, TaskKind.Search, settings, known);
        foreach (var (key, value) in FastSettings) text = TaskFiles.Set(text, key, value);
        return TaskFiles.AddVariableMods(text, EnrichmentMods, known);
    }

    /// <summary>
    /// Runs the probe search over <paramref name="spectra"/> into <paramref name="outDir"/> and returns its
    /// <c>AllPSMs.psmtsv</c>. Throws <see cref="SearchSetupException"/> when it cannot be set up and
    /// <see cref="InvalidOperationException"/> when MetaMorpheus fails: a probe that did not run says nothing.
    /// </summary>
    public static async Task<string> RunAsync(Profile p, Machine m, IReadOnlyList<string> spectra, DepositChemistry? chem,
        IReadOnlyDictionary<string, FragmentationCall>? fragmentation, string outDir, CancellationToken ct)
    {
        outDir = Path.GetFullPath(outDir);
        ClearDesign(spectra);
        if (Directory.Exists(Path.Combine(outDir, "mm"))) Directory.Delete(Path.Combine(outDir, "mm"), recursive: true);
        string tasksDir = Path.Combine(outDir, "tasks");
        Directory.CreateDirectory(tasksDir);
        string cmd = m.CmdFor(p.MetaMorpheus);
        var runner = new MetaMorpheusRunner(cmd, m.Dotnet, m.DotnetRoot);
        var (banner, _) = runner.GenerateDefaults(tasksDir);
        var pin = MetaMorpheusPin.Parse(p.MetaMorpheus);
        pin.Check(banner, runner.Commit(), p.Key);
        string release = pin.Name;
        var known = TaskFiles.KnownMods(Path.Combine(Path.GetDirectoryName(cmd)!, "Mods"));
        string toml = Path.Combine(tasksDir, "probe_SearchTask.toml");
        File.WriteAllText(toml, TaskText(File.ReadAllText(Path.Combine(tasksDir, TaskFiles.FileName(TaskKind.Search))), m, chem, known));

        // Per-file settings (protease, fragmentation) apply to the probe files as to the search.
        var prov = new AgingProvenance("probe_search", m.WorkRoot, toml, new JsonObject(), null);
        SearchStage.WritePerFileSettings(spectra, chem?.ProteasePerFile, fragmentation, p.LowResProductTolerance, prov);

        var dbs = new List<string>();
        foreach (var (organism, db) in p.Databases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            dbs.Add((await ProteomeCache.ResolveAsync(db, m.DatabaseDir, ct).ConfigureAwait(false)).Item1);
        foreach (string d in dbs.Where(d => Path.GetFileName(d).Contains("contaminant", StringComparison.OrdinalIgnoreCase)
                                             || Path.GetFileName(d).Contains("crap", StringComparison.OrdinalIgnoreCase)))
            throw new SearchSetupException($"{d}: MetaMorpheus would treat a species database named like this as contaminants");
        dbs.Add(SearchStage.Contaminants(p, m).Path);

        string settingsDir = Path.Combine(m.MetaMorpheusSettingsRoot, release);
        Directory.CreateDirectory(m.MetaMorpheusSettingsRoot);
        SearchStage.InstallCustomModifications(p, chem, settingsDir, runner, prov);
        var args = new List<string> { "-t", toml, "-s" };
        args.AddRange(spectra);
        args.Add("-d"); args.AddRange(dbs);
        args.AddRange(new[] { "-o", Path.Combine(outDir, "mm"), "--mmsettings", settingsDir, "-v", "minimal" });
        if (m.AcceptThermoLicence) args.Add("--acceptThermoLicence");
        var run = await runner.RunAsync(args, Path.Combine(outDir, "metamorpheus.log"), TimeSpan.FromMinutes(p.ProbeSearchMinutes), ct,
            TimeSpan.FromMinutes(m.SearchStallMinutes)).ConfigureAwait(false);
        string psms = Path.Combine(outDir, "mm", "Task1SearchTask", "AllPSMs.psmtsv");
        if (run.ExitCode != 0 || run.TimedOut || !File.Exists(psms))
            throw new InvalidOperationException($"the probe search did not finish (exit {run.ExitCode}{(run.TimedOut ? ", timed out" : "")}); see {Path.Combine(outDir, "metamorpheus.log")}");
        return psms;
    }

    /// <summary>
    /// The probe quantifies nothing and brings no design, but MetaMorpheus reads any design beside the first spectra file
    /// all the same. The deposit's full search leaves its own there (written for every file), and a later probe of three
    /// of them fails on it: a re-probe after an interrupted search, a retry or a re-search (v0.4.17, PXD044227). The full
    /// search writes its design again before it runs.
    /// </summary>
    /// <returns>True when a stale design was removed.</returns>
    internal static bool ClearDesign(IReadOnlyList<string> spectra) =>
        spectra.Count > 0 && DesignStage.RemoveStale(Path.GetDirectoryName(Path.GetFullPath(spectra[0]))!);
}
