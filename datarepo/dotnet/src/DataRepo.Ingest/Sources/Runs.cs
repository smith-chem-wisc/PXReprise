using DataRepo.Bundle;
using static DataRepo.Ingest.Sources.SourcesPy;

namespace DataRepo.Ingest.Sources;

/// <summary><c>fetch_manifest.json</c> + <c>qc_report.json</c> (+ the SDRF) -> Run rows and run-level Metrics.</summary>
/// <remarks>
/// A run is one deposited raw file. Which files exist, and what the archive said about them, comes
/// from the fetch manifest; what is in them comes from the spectra QC stage.
///
/// The raw file header's facts (start time, instrument model, its PSI-MS term, serial) come from the QC report,
/// which PXReprise writes from mzLib's <c>SourceFile</c> (DATAREPO-72, their 52cd138); dataRepo never reads a raw
/// file itself. Under <see cref="IngestRules.Python0320"/> they are not read, as 0.32.0 did not: the instrument
/// comes from the SDRF and the date stays NA (<see cref="ApplyRunHeader"/>, G87).
/// Ported from <c>sources/runs.py</c>.
/// </remarks>
public static class Runs
{
    /// <summary>Where a run's enrichment came from (the schema's <c>RunEnrichmentSource</c>).</summary>
    public const string FromDataset = "dataset_declaration";
    public const string FromManifest = "manifest_run_enrichment";

    /// <summary>Where a run's instrument model came from (the schema's <c>InstrumentModelSource</c>).</summary>
    public const string ModelFromQcReport = "qc_report";
    public const string ModelFromSdrf = "sdrf";

    /// <summary>An ISO 8601 date-time as PXReprise writes one (.NET <c>yyyy-MM-ddTHH:mm:ss.FFFFFFFK</c>): to the
    /// second, an optional fraction, and a zone only when the reader gave one.</summary>
    private static readonly System.Text.RegularExpressions.Regex StartTimePattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(?<zone>Z|[+-]\d{2}:\d{2})?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The schema's <c>Enrichment</c> vocabulary, in the schema's order (core 0.0.13).</summary>
    /// <remarks>Python reads it from the generated <c>_schema_docs.ENUMS</c>; the C# schema types do not carry
    /// enums yet, so it is copied here and a test checks it against the Python's.</remarks>
    public static readonly IReadOnlyList<string> EnrichmentVocabulary =
    [
        "none", "phospho", "acetyl", "ubiquitin_GG", "succinyl", "glyco", "immunoprecipitation",
        "proximity_labelling", "affinity_purification", "chemical_probe", "other",
    ];

    public static Dictionary<string, object?> LoadFetchManifest(string path) =>
        LoadJson(path) as Dictionary<string, object?> ?? throw new InvalidDataException($"{path} is not a JSON object");

    public static Dictionary<string, object?> LoadQcReport(string path) =>
        LoadJson(path) as Dictionary<string, object?> ?? throw new InvalidDataException($"{path} is not a JSON object");

    /// <summary>The raw files the search left out, and the producer's reason (DATAREPO-51, aging D52).</summary>
    /// <remarks>aging excludes a file that fails QC only on <c>too_few_ms2</c> (a blank or failed injection) and
    /// records it in the search's <c>excluded_files</c>. The QC report still lists it, truthfully, so a run
    /// built from the QC report would describe a measurement that is not in the data.</remarks>
    /// <returns>File names (bare, empty when nothing was excluded) and the reason.</returns>
    public static (IReadOnlySet<string> Names, string? Reason) ExcludedFiles(IReadOnlyDictionary<string, object?> searchProvenance)
    {
        var block = DictOrEmpty(Get(searchProvenance, "excluded_files"), "excluded_files");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ListOrEmpty(Get(block, "files"), "excluded_files.files"))
            if (Strip(Str(f)).Length > 0) names.Add(PathName(Str(f)));
        var reason = Get(block, "reason");
        return (names, Truthy(reason) ? Str(reason) : null);
    }

    /// <summary><c>{"Orbitrap/HCD": 16197}</c> -> <c>["HCD"]</c>.</summary>
    private static List<object?> Dissociation(IReadOnlyDictionary<string, object?> qc)
    {
        var modes = new List<object?>();
        foreach (var key in DictOrEmpty(Get(qc, "ms2_analyzer_dissociation"), "ms2_analyzer_dissociation").Keys)
        {
            var (_, _, dissociation) = RPartition(key, "/");
            var value = Strip(dissociation.Length > 0 ? dissociation : key);
            if (value.Length > 0 && !modes.Contains(value)) modes.Add(value);
        }
        return modes;
    }

    /// <summary>Build Run rows and their Metric rows.</summary>
    /// <param name="datasetId">ProteomeXchange accession.</param>
    /// <param name="fetch">Parsed <c>fetch_manifest.json</c>, or null when the stage was not kept.</param>
    /// <param name="qc">Parsed <c>qc_report.json</c>, keyed by raw file name, or null.</param>
    /// <param name="runFacts">Per-run instrument/fraction facts from the SDRF, by run base name.</param>
    /// <param name="excluded">File names the search left out (<see cref="ExcludedFiles"/>). They are deposited
    /// and QC'd but not searched, so they are not runs of this dataset's results.</param>
    /// <param name="ns">The search record's definitions namespace (D37): the two run metrics are pipeline
    /// counts and cite its id. The default gives the 0.32.0 rows.</param>
    /// <param name="rules">Under <see cref="IngestRules.Current"/> the QC report's run-header keys are read
    /// (<see cref="ApplyRunHeader"/>); under <see cref="IngestRules.Python0320"/> they are not, as 0.32.0 did not.</param>
    /// <returns>Runs, ordered by file name so a bundle is byte-stable, and their metrics.</returns>
    /// <exception cref="IngestException">A QC report's <c>start_time</c> or header text is malformed.</exception>
    public static (List<Row> Runs, List<Row> Metrics) Build(
        string datasetId,
        IReadOnlyDictionary<string, object?>? fetch,
        IReadOnlyDictionary<string, object?>? qc,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runFacts,
        IReadOnlySet<string>? excluded = null,
        string ns = Definitions.DefaultNamespace,
        IngestRules rules = IngestRules.Current)
    {
        excluded ??= new HashSet<string>();
        var qcReport = Truthy(qc) ? qc! : EmptyDict;
        var files = new List<IReadOnlyDictionary<string, object?>>();
        if (Truthy(fetch))
        {
            foreach (var f in ListOrEmpty(Get(fetch!, "files", new List<object?>()), "fetch_manifest files"))
            {
                var file = f as IReadOnlyDictionary<string, object?> ?? throw new InvalidOperationException("a fetch manifest file entry is not a mapping");
                if (Str(Get(file, "category", "RAW")).ToUpperInvariant() == "RAW") files.Add(file);
            }
        }

        var names = new HashSet<string>(files.Select(f => PathName(Str(Get(f, "name", "")))), StringComparer.Ordinal);
        names.UnionWith(qcReport.Keys);
        var excludedStems = excluded.Select(PathStem).ToHashSet(StringComparer.Ordinal);
        names.RemoveWhere(n => excluded.Contains(n) || excludedStems.Contains(PathStem(n)));
        var byName = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var f in files) byName[PathName(Str(Get(f, "name", "")))] = f;  // later wins, as the dict comprehension
        var aliases = rules == IngestRules.Current ? OneNamePerRun(names, byName.Keys, datasetId) : [];

        var ms2Definition = Definitions.Cite(Definitions.Ms2Count, ns);
        var minutesDefinition = Definitions.Cite(Definitions.RunMinutes, ns);
        var runs = new List<Row>();
        var metrics = new List<Row>();
        foreach (var fileName in names.Where(n => n.Length > 0).Order(CodePointOrder))
        {
            var runBase = PathStem(fileName);
            var runId = $"{datasetId}:{runBase}";
            var entry = byName.GetValueOrDefault(fileName) ?? EmptyDict;
            var qcEntryValue = Get(qcReport, fileName);
            if (!Truthy(qcEntryValue)) qcEntryValue = Get(qcReport, runBase);
            foreach (var alias in aliases.GetValueOrDefault(fileName) ?? [])
                if (!Truthy(qcEntryValue)) qcEntryValue = Get(qcReport, alias);
            var qcEntry = DictOrEmpty(qcEntryValue, $"qc_report {fileName}");
            var facts = runFacts.GetValueOrDefault(runBase) ?? EmptyDict;
            var run = new Row
            {
                ["run_id"] = runId,
                ["dataset_id"] = datasetId,
                ["file_name"] = fileName,
                ["sha256"] = Get(entry, "sha256"),
                ["pride_checksum_sha1"] = Get(entry, "pride_checksum"),
                ["fraction"] = Get(facts, "fraction"),
                ["fraction_source"] = Get(facts, "fraction_source"),
                ["technical_replicate"] = Get(facts, "technical_replicate"),
                ["technical_replicate_source"] = Get(facts, "technical_replicate_source"),
                ["fragmentation"] = Dissociation(qcEntry),
                ["ms2_spectra"] = Get(qcEntry, "ms2"),
                ["run_minutes"] = Get(qcEntry, "run_minutes"),
                ["qc_pass"] = Get(qcEntry, "pass"),
                ["instrument_model"] = Get(facts, "instrument_model"),
                ["acquisition_datetime"] = null,
                // Filled by AssignEnrichment, which needs every run at once to check coverage.
                ["enrichment"] = null,
                ["enrichment_source"] = null,
            };
            if (rules == IngestRules.Current) ApplyRunHeader(run, qcEntry, facts, fileName);
            runs.Add(run);
            foreach (var (name, value, definition) in new[]
            {
                ("ms2", Get(qcEntry, "ms2"), ms2Definition),
                ("run_minutes", Get(qcEntry, "run_minutes"), minutesDefinition),
            })
            {
                if (value is not null)
                    metrics.Add(new Row
                    {
                        ["scope"] = "run",
                        ["scope_id"] = runId,
                        ["name"] = name,
                        ["value"] = value,
                        ["definition_id"] = definition,
                        ["source"] = "qc_report.json",
                    });
            }
        }
        return (runs, metrics);
    }

    /// <summary>Fill a run's header columns from its QC entry, and its instrument from the SDRF where the QC entry has
    /// none (G87, PXReprise DATAREPO-72).</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>A missing key means "not read"</b> (PXReprise 013): the column is NULL, never a default.</item>
    /// <item><b>The model and its PSI-MS term come from one source</b>, recorded in <c>instrument_model_source</c>:
    /// the QC report when it names a model (this file's header), else the SDRF (<c>comment[instrument]</c>'s NT and
    /// AC). A QC model is never paired with an SDRF term, which may describe another instrument.</item>
    /// <item><b>A start time with no zone is never a UTC timestamp.</b> A Thermo RAW header's time is the
    /// instrument's local clock (aging 055, mzLib #1349), so it is kept verbatim in <c>acquisition_start_local</c>.
    /// Only a value carrying <c>Z</c> or an offset reaches <c>acquisition_datetime</c>, converted to UTC; it is
    /// passed as a <see cref="DateTimeOffset"/>, because the writer reads a zoneless string as UTC.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="IngestException"><c>start_time</c> is not an ISO 8601 date-time, or a header key is not text.
    /// A malformed time would otherwise be stored as one, so the producer's file is refused, not guessed at.</exception>
    public static void ApplyRunHeader(
        Row run,
        IReadOnlyDictionary<string, object?> qcEntry,
        IReadOnlyDictionary<string, object?> facts,
        string fileName)
    {
        var qcModel = HeaderText(qcEntry, "instrument_model", fileName);
        var sdrfModel = Get(facts, "instrument_model");
        if (qcModel is not null)
        {
            run["instrument_model"] = qcModel;
            run["instrument_model_source"] = ModelFromQcReport;
            run["instrument_model_accession"] = HeaderText(qcEntry, "instrument_model_accession", fileName);
        }
        else if (Truthy(sdrfModel))
        {
            run["instrument_model"] = sdrfModel;
            run["instrument_model_source"] = ModelFromSdrf;
            run["instrument_model_accession"] = Get(facts, "instrument_term");
        }
        else
        {
            run["instrument_model"] = null;
            run["instrument_model_source"] = null;
            run["instrument_model_accession"] = null;
        }
        run["instrument_serial"] = HeaderText(qcEntry, "instrument_serial", fileName);
        var (utc, local) = StartTime(HeaderText(qcEntry, "start_time", fileName), fileName);
        run["acquisition_datetime"] = utc;
        run["acquisition_start_local"] = local;
    }

    /// <summary>A QC report's <c>start_time</c> split by whether it carries a zone.</summary>
    /// <returns>The UTC instant when the value has <c>Z</c> or an offset; otherwise the value verbatim as a local
    /// time with the zone unknown. Both null when it was not read.</returns>
    /// <exception cref="IngestException">The value is not an ISO 8601 date-time.</exception>
    public static (DateTimeOffset? Utc, string? Local) StartTime(string? value, string fileName)
    {
        if (value is null) return (null, null);
        var match = StartTimePattern.Match(value);
        if (!match.Success || !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            throw new IngestException(
                $"qc_report.json gives {fileName} the start_time {Repr(value)}, which is not an ISO 8601 date-time "
                + "(yyyy-MM-ddTHH:mm:ss, an optional fraction, and Z or an offset only when the reader gave a zone). "
                + "It is refused rather than stored as a time.");
        return match.Groups["zone"].Success ? (parsed.ToUniversalTime(), null) : (null, value);
    }

    /// <summary>A QC entry's text key: null when missing (not read), the string verbatim otherwise.</summary>
    private static string? HeaderText(IReadOnlyDictionary<string, object?> qcEntry, string key, string fileName) =>
        Get(qcEntry, key) switch
        {
            null => null,
            string s when s.Trim().Length == 0 => null,
            string s => s,
            var other => throw new IngestException(
                $"qc_report.json gives {fileName} a {key} of {Repr(other)}, which is not text."),
        };

    /// <summary>One file name per run id (G83 item 11, <see cref="IngestRules.Current"/> only).</summary>
    /// <remarks>A run id is <c>dataset:stem</c>, so two names with one stem would be two Run rows under one id. The
    /// QC report can key a run by its stem (<c>b</c>) or by another name for the same file beside the fetch
    /// manifest's <c>b.raw</c>; those are one run, written under the deposited name, with the others kept as aliases
    /// for finding its QC entry. Python 0.32.0 wrote a row for each, and the ingest then refused on rows that share
    /// an id and differ. Two DEPOSITED files with one stem (<c>c.raw</c> and <c>c.mzML</c>) are two files the run id
    /// cannot tell apart, and that is refused here, by name, rather than by the duplicate check.</remarks>
    /// <returns>The kept name -> the names folded into it.</returns>
    /// <exception cref="IngestException">Two deposited files, or two QC-only names that both carry an extension,
    /// share a stem.</exception>
    private static Dictionary<string, List<string>> OneNamePerRun(HashSet<string> names, IEnumerable<string> deposited, string datasetId)
    {
        var fetched = deposited.ToHashSet(StringComparer.Ordinal);
        var aliases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var group in names.Where(n => n.Length > 0).GroupBy(PathStem).Where(g => g.Count() > 1).ToList())
        {
            var all = group.Order(CodePointOrder).ToList();
            var candidates = all.Where(fetched.Contains).ToList();
            if (candidates.Count == 0) candidates = all.Where(n => n != group.Key).ToList();  // a file name over a bare stem
            if (candidates.Count != 1)
                throw new IngestException(
                    $"{datasetId}: {string.Join(", ", candidates)} share the run id {datasetId}:{group.Key}. A run is "
                    + "identified by its file name without the extension, so these cannot be told apart, and which one "
                    + "the search read is not recorded where the ingest can see it. The producer has to resolve it.");
            var keep = candidates[0];
            aliases[keep] = all.Where(n => n != keep).ToList();
            names.ExceptWith(aliases[keep]);
        }
        return aliases;
    }

    private static string Examples(IReadOnlyList<string> names, int n = 5)
    {
        var shown = string.Join(", ", names.Take(n));
        return shown + (names.Count > n ? $" and {names.Count - n} more" : "");
    }

    /// <summary>Fill each run's <c>enrichment</c> and <c>enrichment_source</c>, and say whether the runs differ (G63).</summary>
    /// <remarks>
    /// The rules are the ones agreed with aging (thread 054 section 2, their 058), and each failure
    /// refuses the ingest rather than writing a guess:
    /// <list type="bullet">
    /// <item><b>A per-run map must cover every run.</b> A partial map would leave some runs NULL beside some
    /// filled, and NULL would come to mean "probably the other one".</item>
    /// <item><b>A name that is not a run is refused</b>, and so is an enrichment value outside the schema's
    /// vocabulary. (A run named twice is refused when the manifest is read.)</item>
    /// <item><b>Every run value other than <c>none</c> must be in the dataset's declaration.</b> A run that says
    /// <c>chemical_probe</c> in a dataset declared <c>[immunoprecipitation]</c> is a curation error.</item>
    /// <item><b>A map with one value on a dataset flagged mixed</b> contradicts the flag, and is refused.</item>
    /// <item><b>No map:</b> a dataset not flagged mixed gives its declaration to every run, because it is true
    /// of every run. A mixed dataset gives NULL to every run, never its declaration, which is true of
    /// only some of them.</item>
    /// </list>
    /// </remarks>
    /// <param name="runs">Run rows from <see cref="Build"/>, modified in place.</param>
    /// <param name="datasetId">For messages.</param>
    /// <param name="declared">The dataset's <c>enrichment</c> from the manifest.</param>
    /// <param name="mixed">The producer's <c>mixed_enrichment</c> flag.</param>
    /// <param name="runEnrichment">The manifest's sorted <c>(run base name, value)</c> pairs.</param>
    /// <returns><c>Dataset.enrichment_mixed</c>: the flag, or more than one distinct per-run value.</returns>
    /// <exception cref="IngestException">Any rule above.</exception>
    public static bool AssignEnrichment(
        IReadOnlyList<Row> runs,
        string datasetId,
        IReadOnlyList<string> declared,
        bool mixed,
        IReadOnlyList<(string Run, string Value)> runEnrichment,
        IngestRules rules = IngestRules.Current)
    {
        if (runEnrichment.Count == 0)
        {
            foreach (var run in runs)
            {
                run["enrichment"] = mixed ? null : declared.Cast<object?>().ToList();
                run["enrichment_source"] = mixed ? null : FromDataset;
            }
            return mixed;
        }

        var baseNames = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var run in runs) baseNames[PathStem(Str(run["file_name"]))] = run;  // later wins, as the dict comprehension
        var problems = EnrichmentProblems(baseNames.Keys, datasetId, declared, mixed, runEnrichment);
        if (problems.Count > 0) throw new IngestException(problems[0]);
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (run, value) in runEnrichment) given[run] = value;
        var distinct = given.Values.ToHashSet(StringComparer.Ordinal);
        // 0.32.0 filled one run per base name, the last; two runs with one base name left the earlier NULL.
        // Current fills every run (G83 item 12).
        var targets = rules == IngestRules.Current ? runs.ToList() : baseNames.Values.ToList();
        foreach (var run in targets)
        {
            run["enrichment"] = new List<object?> { given[PathStem(Str(run["file_name"]))] };
            run["enrichment_source"] = FromManifest;
        }
        return mixed || distinct.Count > 1;
    }

    /// <summary>The run base names <see cref="AssignEnrichment"/> matches a run map against.</summary>
    public static IReadOnlyList<string> RunBaseNames(IReadOnlyList<Row> runs) =>
        runs.Select(r => PathStem(Str(r["file_name"]))).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Why a manifest's <c>excluded_runs</c> (G85) would not apply to the dataset's bundle: a named run that
    /// is not one of its runs. <c>datarepo manifest</c> reports these and <c>datarepo build</c> refuses on them; ingest
    /// does not read the field.</summary>
    /// <param name="runBaseNames">The dataset's run base names (ingest's runs, which leave out what the search left
    /// out), or null when its runs cannot be read, which checks nothing.</param>
    /// <param name="leftOut">Base names of files the search left out, which are therefore not runs of the bundle.</param>
    public static List<string> ExclusionProblems(
        IEnumerable<string>? runBaseNames,
        IReadOnlySet<string> leftOut,
        string datasetId,
        IReadOnlyList<(string Run, string Reason)> excludedRuns)
    {
        var problems = new List<string>();
        if (runBaseNames is null || excludedRuns.Count == 0) return problems;
        var baseNames = runBaseNames.ToHashSet(StringComparer.Ordinal);
        var named = excludedRuns.Select(p => p.Run).Where(r => !baseNames.Contains(r)).Order(CodePointOrder).ToList();
        var notSearched = named.Where(leftOut.Contains).ToList();
        var unknown = named.Where(r => !leftOut.Contains(r)).ToList();
        if (unknown.Count > 0)
            problems.Add(
                $"{datasetId}: excluded_runs names {unknown.Count} run(s) that are not runs of this dataset: " +
                $"{Examples(unknown)}. Runs are the deposited raw file names without their extension, e.g. " +
                $"{Examples(baseNames.Order(CodePointOrder).ToList(), 3)}. An exclusion that matches no run would " +
                "leave the run it meant looking fit for analysis.");
        if (notSearched.Count > 0)
            problems.Add(
                $"{datasetId}: excluded_runs names {notSearched.Count} run(s) the search left out: " +
                $"{Examples(notSearched)}. They are not in the bundle, so there is nothing to exclude from analysis; " +
                "remove them from excluded_runs.");
        return problems;
    }

    /// <summary>Every rule <see cref="AssignEnrichment"/> refuses a run map on, in the order it applies them; ingest
    /// refuses on the first, <c>datarepo manifest</c> reports them all (PXReprise 009).</summary>
    /// <param name="runBaseNames">The dataset's run base names, or null when its runs cannot be read, which skips
    /// the two rules that need them (unknown runs, coverage).</param>
    public static List<string> EnrichmentProblems(
        IEnumerable<string>? runBaseNames,
        string datasetId,
        IReadOnlyList<string> declared,
        bool mixed,
        IReadOnlyList<(string Run, string Value)> runEnrichment)
    {
        var problems = new List<string>();
        if (runEnrichment.Count == 0) return problems;
        var vocabulary = EnrichmentVocabulary.ToHashSet(StringComparer.Ordinal);
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (run, value) in runEnrichment) given[run] = value;
        var badValues = given.Values.Where(v => !vocabulary.Contains(v)).Distinct().Order(CodePointOrder).ToList();
        if (badValues.Count > 0)
            problems.Add(
                $"{datasetId}: run_enrichment uses {string.Join(", ", badValues)}, which the schema's Enrichment " +
                $"vocabulary does not have ({string.Join(", ", vocabulary.Order(CodePointOrder))}).");
        if (runBaseNames is not null)
        {
            var baseNames = runBaseNames.ToHashSet(StringComparer.Ordinal);
            var unknown = given.Keys.Where(k => !baseNames.Contains(k)).Order(CodePointOrder).ToList();
            if (unknown.Count > 0)
                problems.Add(
                    $"{datasetId}: run_enrichment names {unknown.Count} run(s) that are not runs of this " +
                    $"dataset: {Examples(unknown)}. Runs are the deposited raw file names without their " +
                    $"extension, e.g. {Examples(baseNames.Order(CodePointOrder).ToList(), 3)}.");
            var missing = baseNames.Where(k => !given.ContainsKey(k)).Order(CodePointOrder).ToList();
            if (missing.Count > 0)
                problems.Add(
                    $"{datasetId}: run_enrichment covers {given.Count} of {baseNames.Count} runs and must cover " +
                    $"every one. Missing: {Examples(missing)}. A partial map would leave NULL meaning " +
                    "'probably the other one'.");
        }
        var undeclared = given.Values.Where(v => v != "none" && !declared.Contains(v)).Distinct().Order(CodePointOrder).ToList();
        if (undeclared.Count > 0)
            problems.Add(
                $"{datasetId}: run_enrichment assigns {string.Join(", ", undeclared)}, which the dataset's " +
                $"enrichment declaration ({string.Join(", ", declared)}) does not include. Either the run map or " +
                "the declaration is wrong, and the producer has to say which.");
        // A Python set's iteration order is not insertion order, but with one element there is only one.
        var distinct = given.Values.ToHashSet(StringComparer.Ordinal);
        if (mixed && distinct.Count == 1)
            problems.Add(
                $"{datasetId}: flagged mixed_enrichment, but run_enrichment gives every run " +
                $"{Repr(distinct.First())}. The flag and the map contradict each other.");
        return problems;
    }
}
