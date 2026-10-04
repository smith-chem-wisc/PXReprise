using DataRepo.Bundle;
using DataRepo.Ingest.Sources;

namespace DataRepo.Ingest;

/// <summary>Which rules an ingest applies.</summary>
public enum IngestRules
{
    /// <summary>The C# ingester's own rules: Python 0.32.0's, plus G76 (sequence-level specificity, D40) and G81
    /// (DEF-PEP's <c>iterative</c> key part). Bundle ids are computed from <see cref="BundleWriter.IngesterVersion"/>.</summary>
    Current,

    /// <summary>Exactly the rows Python 0.32.0 wrote, for the port's parity runs (D41). Ids are computed from
    /// <see cref="Ingester.Python0320ParityPath"/>, so such a bundle can never be mistaken for a real one.</summary>
    Python0320,
}

/// <summary>What one ingest produced, for the CLI and for tests.</summary>
public sealed record IngestResult(
    string DatasetId,
    string BundlePath,
    string BundleId,
    IReadOnlyDictionary<string, long> RowCounts,
    IReadOnlyList<OrderedDictionary<string, object?>> Checks,
    IReadOnlyList<Row> Findings,
    IReadOnlyDictionary<string, long> UnresolvedModifications,
    IReadOnlyDictionary<string, long> UnmatchedRuns,
    bool Skipped)
{
    public IEnumerable<OrderedDictionary<string, object?>> Mismatches => Checks.Where(c => c["ok"] is false);
}

/// <summary><c>datarepo ingest</c>: one dataset's pipeline output to one immutable Parquet bundle.</summary>
/// <remarks>
/// The order of work is the order of trust. The manifest decides whether the dataset may be ingested at all;
/// the provenance decides how its numbers are read; only then are the result files parsed. Nothing is inferred
/// from directory names, and nothing the producer marked unfit is loaded. Ported from <c>ingest.py</c>.
/// </remarks>
public static class Ingester
{
    /// <summary>The ingest path a <see cref="IngestRules.Python0320"/> run hashes into its ids: marked, never a release.</summary>
    public const string Python0320ParityPath = "parity-python-0.32.0";

    /// <summary>Manifest <c>quant_method</c> spellings mapped onto the schema's enum.</summary>
    public static readonly IReadOnlyDictionary<string, string> QuantMethods = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["label-free"] = "label_free",
        ["label free"] = "label_free",
        ["labelfree"] = "label_free",
        ["lfq"] = "label_free",
        ["tmt"] = "isobaric",
        ["itraq"] = "isobaric",
        ["silac"] = "metabolic",
    };

    /// <summary>Enough instrument-name to vendor mapping for the instruments in scope.</summary>
    private static readonly (string[] Needles, string Vendor)[] Vendors =
    [
        (["q exactive", "orbitrap", "lumos", "eclipse", "astral", "exploris", "velos", "elite", "ltq"], "Thermo"),
        (["timstof", "maxis", "impact"], "Bruker"),
        (["triple tof", "tripletof", "qtrap", "zenotof"], "SCIEX"),
        (["synapt", "xevo"], "Waters"),
    ];

    private static string? Vendor(IEnumerable<string> instruments)
    {
        foreach (var name in instruments)
        {
            var lowered = name.ToLowerInvariant();
            foreach (var (needles, vendor) in Vendors)
                if (needles.Any(n => lowered.Contains(n, StringComparison.Ordinal)))
                    return vendor;
        }
        return null;
    }

    private static string? FindFile(string dir, params string[] relative)
    {
        foreach (var rel in relative)
        {
            var candidate = Path.Combine(dir, rel);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static object? Get(IReadOnlyDictionary<string, object?>? doc, string key) =>
        doc is not null && doc.TryGetValue(key, out var v) ? v : null;

    private static IReadOnlyDictionary<string, object?>? Map(object? value) => value as IReadOnlyDictionary<string, object?>;

    private static IEnumerable<object?> List(object? value) => value as IEnumerable<object?> ?? [];

    private static string S(object? value) => value is null ? "None" : PyFormat.Str(value);

    /// <summary>The path a Python <c>Path(a) / b</c> gives, so recorded paths look as they did.</summary>
    private static string Join(string root, string relative) => BundleWriter.PathText(Path.Combine(root, relative));

    /// <summary>The search stage's provenance and every stage it declares upstream of itself.</summary>
    /// <remarks>Globbing the run folder would be wrong: a run folder can hold more than one search of the same
    /// data, and only one is the manifest's canonical run. <b>An upstream file is read only if it is still the
    /// file the search recorded</b> (0.19.0): aging's shared <c>db/provenance.json</c> was overwritten under
    /// 33 of 38 searches, so a file whose sha256 no longer matches is left out and reported.</remarks>
    private static (List<string> Paths, List<Row> Mismatches) Lineage(string workRoot, string searchProvenancePath, IReadOnlyDictionary<string, object?> searchProvenance)
    {
        var paths = new List<string> { searchProvenancePath };
        var mismatches = new List<Row>();
        foreach (var e in List(Get(searchProvenance, "upstream")))
        {
            var entry = Map(e);
            var candidate = Join(workRoot, PyFormat.Str(Get(entry, "path") ?? ""));
            if (!File.Exists(candidate) || paths.Contains(candidate)) continue;
            var recorded = Get(entry, "sha256");
            if (recorded is string r && r.Length > 0)
            {
                var actual = BundleWriter.Sha256File(candidate);
                if (actual != r)
                {
                    mismatches.Add(new Row { ["stage"] = Get(entry, "stage"), ["path"] = S(Get(entry, "path")), ["recorded"] = r, ["actual"] = actual });
                    continue;
                }
            }
            paths.Add(candidate);
        }
        return (paths, mismatches);
    }

    /// <summary>The MetaMorpheus task <c>.toml</c> files the run actually executed (its provenance inputs), never
    /// the shipped templates for tasks that did not run.</summary>
    private static List<string> TaskFiles(string workRoot, IReadOnlyDictionary<string, object?> searchProvenance)
    {
        var output = new List<string>();
        foreach (var e in List(Get(searchProvenance, "inputs")))
        {
            var path = PyFormat.Str(Get(Map(e), "path") ?? "");
            if (path.ToLowerInvariant().EndsWith("task.toml", StringComparison.Ordinal))
            {
                var candidate = Join(workRoot, path);
                if (File.Exists(candidate)) output.Add(candidate);
            }
        }
        return output;
    }

    /// <summary>Loads a manifest and ingests one dataset from it.</summary>
    public static IngestResult Ingest(string manifestPath, string accession, string? store = null, string? mmSettings = null, bool overwrite = false, IngestRules rules = IngestRules.Current)
    {
        var manifest = Manifest.Load(manifestPath);
        var entry = manifest.Dataset(accession);
        return IngestDataset(manifest, entry, store, mmSettings, overwrite, rules);
    }

    /// <summary>The <c>run_enrichment</c> rules ingest would refuse <paramref name="entry"/> on, checked without
    /// ingesting (PXReprise 009). The runs are read as ingest reads them (fetch manifest, QC report, minus the files
    /// the search left out); when they cannot be read, only the rules that need no runs are applied.</summary>
    /// <returns>The problems, in ingest's order, and whether the runs were read.</returns>
    public static (List<string> Problems, bool RunsRead) RunEnrichmentProblems(Manifest manifest, DatasetEntry entry)
    {
        IReadOnlyList<string>? baseNames = null;
        IReadOnlySet<string> excluded = new HashSet<string>();
        try
        {
            var runDir = manifest.RunDir(entry);
            var searchDir = manifest.StageDir(entry, "search");
            if (Directory.Exists(runDir) && searchDir is not null && File.Exists(Join(searchDir, "provenance.json")))
            {
                (excluded, _) = Runs.ExcludedFiles(Provenance.Load(Join(searchDir, "provenance.json")));
                var fetchPath = Directory.GetDirectories(runDir).Order(StringComparer.Ordinal)
                    .Select(d => Path.Combine(d, "fetch_manifest.json")).Where(File.Exists).Order(StringComparer.Ordinal).FirstOrDefault();
                var qcDir = manifest.StageDir(entry, "qc");
                var qcPath = qcDir is not null ? FindFile(qcDir, "qc_report.json") : null;
                var (runRows, _) = Runs.Build(entry.Accession,
                    fetchPath is not null ? Runs.LoadFetchManifest(fetchPath) : null,
                    qcPath is not null ? Runs.LoadQcReport(qcPath) : null,
                    new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal), excluded);
                if (runRows.Count > 0) baseNames = Runs.RunBaseNames(runRows);
            }
        }
        catch (DataRepoException) { baseNames = null; }
        catch (IOException) { baseNames = null; }
        var excludedStems = excluded.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var problems = Runs.EnrichmentProblems(baseNames, entry.Accession, entry.Enrichment, entry.MixedEnrichment,
            entry.RunEnrichment.Where(p => !excludedStems.Contains(p.Run)).ToList());
        return (problems, baseNames is not null);
    }

    /// <summary>Builds the bundle for one dataset.</summary>
    /// <param name="mmSettings">The MetaMorpheus install that did the search, for the modification registry;
    /// defaults to <c>&lt;work_root&gt;/mm_settings/&lt;version from the manifest&gt;</c>.</param>
    /// <exception cref="DatasetExcludedException">The manifest does not mark the dataset <c>include</c>.</exception>
    /// <exception cref="IngestException">A file the ingest cannot do without is missing.</exception>
    /// <exception cref="UnsupportedProvenanceException">The run's provenance cannot be mapped safely.</exception>
    public static IngestResult IngestDataset(Manifest manifest, DatasetEntry entry, string? store = null, string? mmSettings = null, bool overwrite = false, IngestRules rules = IngestRules.Current)
    {
        // mzLib parses numbers with the current culture; pyMzLib's bridge ran with InvariantGlobalization. A
        // comma-decimal host culture would otherwise read "0.01" differently from the producer's intent.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

        // The refusal lives here, not only in the CLI: loading a run the producer marked unfit is the one thing
        // this must never do, however it is called.
        if (!entry.Ingestable)
            throw new DatasetExcludedException(
                $"{entry.Accession} has status '{entry.Status}' in {manifest.Path} and will not be ingested. "
                + $"The producer's reason: {(DatasetEntry.Text(entry.Reason) ?? "no reason given").Trim()}");

        var datasetId = entry.Accession;
        var runDir = manifest.RunDir(entry);
        if (!Directory.Exists(runDir)) throw new IngestException($"{datasetId}: run folder {runDir} does not exist");
        var searchDir = manifest.StageDir(entry, "search");
        if (searchDir is null || !Directory.Exists(searchDir))
            throw new IngestException($"{datasetId}: the manifest's search stage folder is missing ({S(searchDir)})");
        var resultsDir = manifest.SearchResultsDir(entry);
        if (!Directory.Exists(resultsDir)) throw new IngestException($"{datasetId}: search results folder {resultsDir} does not exist");

        var writer = new BundleWriter(store ?? manifest.Store, datasetId, rules == IngestRules.Python0320 ? Python0320ParityPath : null);
        // The manifest entry is an input like any other: it supplies the title and the D5 axes, so it is in the
        // content hash; only the fields that shape content, never the producer's prose (manifest.ContentFields).
        writer.AddDeclaration("manifest_entry", entry.ContentDeclaration());
        var log = new ReaderLog();

        // --- provenance first: it tells us how to read every number that follows ---------------------------
        var searchProvenancePath = Join(searchDir, "provenance.json");
        if (!File.Exists(searchProvenancePath)) throw new IngestException($"{datasetId}: no provenance.json in {searchDir}");
        var searchProvenance = Provenance.Load(searchProvenancePath);
        var provenanceVersion = Provenance.SchemaVersion(searchProvenance);
        // D37: each stage's record says whose definitions its numbers are; none declared reads as aging's.
        var searchNamespace = Provenance.DefinitionsNamespace(searchProvenance);

        var provenanceRows = new List<Row>();
        var findings = new List<Row>();
        var (lineage, lineageMismatches) = Lineage(manifest.WorkRoot, searchProvenancePath, searchProvenance);
        findings.AddRange(LineageFindings(lineageMismatches, datasetId));
        var namespaceByStageDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in lineage)
        {
            var doc = Provenance.Load(path);
            var stageName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
            namespaceByStageDir[Path.GetFullPath(Path.GetDirectoryName(path)!)] = Provenance.DefinitionsNamespace(doc);
            var source = writer.AddSource(path, $"provenance:{stageName}", $"provenance_{stageName}.json");
            provenanceRows.Add(Provenance.RecordRow(doc, datasetId, stageName, (string)source["bundle_path"]!, (string)source["sha256"]!));
            if (path == searchProvenancePath)
                findings.AddRange(Provenance.FindingRows(doc, datasetId, $"provenance.json flags ({stageName})"));
        }

        var metrics = Provenance.MetricRows(searchProvenance, datasetId, provenanceVersion, searchNamespace);

        // --- the modification registry from the build that did the search --------------------------------
        var settings = mmSettings;
        if (settings is null && entry.Metamorpheus is not null && S(entry.Metamorpheus).Length > 0)
            settings = Join(Join(manifest.WorkRoot, "mm_settings"), S(entry.Metamorpheus));
        var registry = settings is not null ? ModRegistry.FromMetaMorpheus(settings) : new ModRegistry();
        var proforma = new ProformaCache(registry);

        // --- samples and runs -----------------------------------------------------------------------------
        var fetchPath = Directory.GetDirectories(runDir).Order(StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "fetch_manifest.json")).Where(File.Exists).Order(StringComparer.Ordinal).FirstOrDefault();
        var fetch = fetchPath is not null ? Runs.LoadFetchManifest(fetchPath) : null;
        if (fetchPath is not null) writer.AddSource(BundleWriter.PathText(fetchPath), "fetch_manifest");

        var qcDir = manifest.StageDir(entry, "qc");
        var qcPath = qcDir is not null ? FindFile(qcDir, "qc_report.json") : null;
        var qc = qcPath is not null ? Runs.LoadQcReport(qcPath) : null;
        if (qcPath is not null) writer.AddSource(BundleWriter.PathText(qcPath), "qc_report", "qc_report.json");
        // The QC numbers are the QC stage's, so they cite the namespace its own record declares (D37).
        var qcNamespace = qcDir is not null && namespaceByStageDir.TryGetValue(Path.GetFullPath(qcDir), out var qns) ? qns : searchNamespace;

        var sdrfPath = Directory.GetDirectories(runDir)
            .SelectMany(d => Directory.Exists(Path.Combine(d, "metadata")) ? Directory.GetFiles(Path.Combine(d, "metadata"), "*.sdrf.tsv") : [])
            .Order(StringComparer.Ordinal).FirstOrDefault();
        SdrfTable sdrf;
        if (sdrfPath is not null)
        {
            writer.AddSource(BundleWriter.PathText(sdrfPath), "sdrf", Path.GetFileName(sdrfPath));
            sdrf = Sdrf.Parse(sdrfPath, datasetId, DatasetEntry.Text(entry.Organism), log);
        }
        else sdrf = new SdrfTable { Samples = [], Characteristics = [], Assays = [], RunFacts = [], SampleOfRun = [], Columns = [] };

        var (excluded, excludedReason) = Runs.ExcludedFiles(searchProvenance);
        var runFacts = sdrf.RunFacts.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)kv.Value, StringComparer.Ordinal);
        var (runRows, runMetrics) = Runs.Build(datasetId, fetch, qc, runFacts, excluded, qcNamespace);
        if (runRows.Count == 0)
            throw new IngestException(
                $"{datasetId}: no runs found. Neither a fetch manifest nor a QC report was readable "
                + $"under {runDir}, so there is nothing to attach measurements to.");
        metrics.AddRange(runMetrics);
        findings.AddRange(ExcludedFileFindings(excluded, excludedReason, fetch, qc, datasetId));
        // A file the search left out is not a run, so a run map or SDRF assay naming it is dropped with it rather
        // than refused: both were written about the deposit, before the search chose.
        var excludedStems = excluded.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var enrichmentMixed = Runs.AssignEnrichment(
            runRows, datasetId, entry.Enrichment, entry.MixedEnrichment,
            entry.RunEnrichment.Where(p => !excludedStems.Contains(p.Run)).ToList());

        var samples = sdrf.Samples.ToList();
        var characteristics = sdrf.Characteristics.ToList();
        var excludedRunIds = excludedStems.Select(s => $"{datasetId}:{s}").ToHashSet(StringComparer.Ordinal);
        var assays = sdrf.Assays.Where(a => !excludedRunIds.Contains((string)a["run_id"]!)).ToList();
        var sdrfStatus = sdrfPath is not null ? "trusted" : "absent";

        void Synthetic(IEnumerable<Row> runs)
        {
            // One synthetic sample per run, clearly flagged, so quantities still have something to hang on.
            // Nothing biological is invented, only the identity of the sample.
            var taken = samples.Select(s => (string)s["sample_id"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                var stem = Path.GetFileNameWithoutExtension((string)run["file_name"]!);
                var sampleId = $"{datasetId}:{stem}";
                if (taken.Contains(sampleId)) sampleId = $"{datasetId}:{stem}#run";  // an SDRF source name equal to a run name
                samples.Add(new Row { ["sample_id"] = sampleId, ["dataset_id"] = datasetId, ["source_name"] = stem, ["organism"] = DatasetEntry.Text(entry.Organism) });
                assays.Add(new Row { ["assay_id"] = $"{datasetId}:{stem}:label_free", ["run_id"] = run["run_id"], ["channel"] = "label_free", ["sample_id"] = sampleId });
            }
        }

        // The data-file gate (G62): rows for searched runs are used, the rest are named, and the status says which.
        if (sdrfPath is not null)
        {
            var searched = runRows.Select(r => (string)r["run_id"]!).ToHashSet(StringComparer.Ordinal);
            var namedUnsearched = assays.Select(a => (string)a["run_id"]!).Where(r => !searched.Contains(r))
                .Select(r => r.Split(':', 2)[1]).Distinct().Order(StringComparer.Ordinal).ToList();
            assays = assays.Where(a => searched.Contains((string)a["run_id"]!)).ToList();
            var assayRuns = assays.Select(a => (string)a["run_id"]!).ToHashSet(StringComparer.Ordinal);
            var unnamed = runRows.Where(r => !assayRuns.Contains((string)r["run_id"]!)).ToList();
            if (assays.Count == 0)
            {
                sdrfStatus = "unmatched";
                samples = [];
                characteristics = [];
                findings.Add(SdrfGateFinding(datasetId, "sdrf_unmatched", namedUnsearched, []));
            }
            else if (namedUnsearched.Count > 0 || unnamed.Count > 0)
            {
                sdrfStatus = "partial";
                var usedSamples = assays.Select(a => (string)a["sample_id"]!).ToHashSet(StringComparer.Ordinal);
                samples = samples.Where(s => usedSamples.Contains((string)s["sample_id"]!)).ToList();
                characteristics = characteristics.Where(c => usedSamples.Contains((string)c["sample_id"]!)).ToList();
                Synthetic(unnamed);
                findings.Add(SdrfGateFinding(datasetId, "sdrf_partial", namedUnsearched,
                    unnamed.Select(r => Path.GetFileNameWithoutExtension((string)r["file_name"]!)).ToList()));
            }
        }
        if (samples.Count == 0) Synthetic(runRows);
        static bool Annotated(List<Row> samples) => samples.Any(s =>
            new[] { "organism_part", "cell_type", "disease", "individual_id" }.Any(c => s.TryGetValue(c, out var v) && !IsFalsy(v)));
        string uncoded;
        if (sdrfPath is null)
        {
            findings.Add(Finding(datasetId, "no_sdrf", "warning",
                "No SDRF was found for this dataset, so each run was given a synthetic sample "
                + "of its own. There is no sample metadata: treat every sample as unannotated."));
        }
        else if (sdrfStatus != "unmatched" && !Annotated(samples) && (uncoded = UncodedAnnotation(characteristics)).Length > 0)
        {
            // The curated columns want an ontology term; an SDRF that writes `Blood serum` with no term leaves them
            // empty while the text sits in sample_characteristics (PXD010115, PXD034432, 0.18.0).
            findings.Add(Finding(datasetId, "sdrf_uncoded", "warning",
                "The deposited SDRF describes its samples only as text with no ontology term, so "
                + "the curated organism part, cell type, disease and individual columns are empty. "
                + $"The text is in sample_characteristics: {uncoded}. Match on it as text; it has "
                + "not been mapped to a term."));
        }
        else if (sdrfStatus != "unmatched" && !Annotated(samples))
        {
            findings.Add(Finding(datasetId, "sdrf_skeleton", "warning",
                "The deposited SDRF carries no biological annotation: organism part, cell "
                + "type, disease and individual are all absent. Sample-level questions cannot "
                + "be answered for this dataset until it is curated."));
        }

        var runNames = new RunNameMap(runRows.Select(r => Path.GetFileNameWithoutExtension((string)r["file_name"]!)).ToList());

        // --- identifications ------------------------------------------------------------------------------
        var allPsmsPath = Join(resultsDir, "AllPSMs.psmtsv");
        var allPeptidesPath = Join(resultsDir, "AllPeptides.psmtsv");
        if (!File.Exists(allPsmsPath)) throw new IngestException($"{datasetId}: no AllPSMs.psmtsv in {resultsDir}");
        writer.AddSource(allPsmsPath, "psms");

        var proteinGroups = new List<Row>();
        long proteinGroupCount = 0;
        var proteinGroupQuant = new List<Row>();
        var pgPath = Join(resultsDir, "AllQuantifiedProteinGroups.tsv");
        if (File.Exists(pgPath))
        {
            writer.AddSource(pgPath, "protein_group_quant");
            (proteinGroups, proteinGroupQuant, proteinGroupCount) = Quant.ProteinGroupRows(pgPath, datasetId, runNames, log);
        }

        var searches = SearchParams.TaskNames(searchProvenance).Select(t => t.ToLowerInvariant()).ToList();
        var searchLabel = searches.Contains("gptmd") ? "gptmd" : "standard";

        var psmColumns = Readers.ReadPsmtsv(allPsmsPath, log);
        var psmRows = Identifications.PsmRows(psmColumns, datasetId, proforma, runNames, searchLabel);
        // Sites are placed by finding each peptide in the searched proteins, because the producer's spans cannot
        // be paired with its accessions (aging 043, DATAREPO-32). The databases are inputs to every ptm_sites
        // row, so they are in the content hash; they are not copied.
        var sequences = ProteinDb.Load(searchProvenance, manifest.WorkRoot);
        foreach (var db in sequences.Files)
        {
            // The role carries the file name: sources are hashed in (role, path) order, and a path is absolute and
            // site-specific, so two databases under one role could hash in a different order elsewhere.
            var dbPath = PyFormat.Str(db["path"]!);
            writer.AddHashedSource(dbPath, $"protein_database:{Path.GetFileName(dbPath)}", PyFormat.Str(db["sha256"]!));
        }
        var unplaced = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        var ptmSites = Identifications.PtmSiteRows(psmColumns, datasetId, proforma, sequences, unplaced);
        var siteCheck = Identifications.VerifySiteResidues(ptmSites, sequences);

        var peptideColumns = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        if (File.Exists(allPeptidesPath))
        {
            writer.AddSource(allPeptidesPath, "peptides");
            peptideColumns = Readers.ReadPsmtsv(allPeptidesPath, log);
        }
        var peptidoforms = Identifications.PeptidoformRows(
            peptideColumns.Count > 0 ? peptideColumns : psmColumns, datasetId, proforma,
            Identifications.PsmCountsByPeptidoform(psmRows),
            proteinGroups.Select(g => (string)g["protein_group_id"]!).ToHashSet(StringComparer.Ordinal));
        if (rules == IngestRules.Current)
        {
            // G76 (D40): specificity from the searched sequences, by mzLib's classifier, replacing parsimony-unique.
            var databases = sequences.Files.Select(f => (PyFormat.Str(f["path"]!), PyFormat.Str(f["sha256"] ?? ""), ProteinDb.IsContaminantDatabase(PyFormat.Str(f["path"]!)))).ToList();
            var sharing = Specificity.Classify(peptidoforms.Select(r => r["base_sequence"] as string ?? ""), Specificity.LoadProteins(databases));
            foreach (var row in peptidoforms)
            {
                var (isUnique, isIsoformSpecific) = sharing.TryGetValue(row["base_sequence"] as string ?? "", out var cls)
                    ? Specificity.Columns(cls) : (null, null);
                row["is_unique"] = isUnique;
                row["is_isoform_specific"] = isIsoformSpecific;
            }
        }
        // A protein's contaminant label comes from the database it was read from, not from the PSM row it shares
        // with a contaminant (G66). An accession in BOTH is whatever the search's TCAmbiguity made it.
        var tc = SearchParams.TcAmbiguity(TaskFiles(manifest.WorkRoot, searchProvenance));
        bool? ContaminantOf(string accession)
        {
            var status = sequences.DatabaseStatus(accession);
            if (status == "both") return tc switch { "RemoveContaminant" => false, "RemoveTarget" => true, _ => null };
            return status switch { "contaminant" => true, "target" => false, _ => null };
        }
        var unresolvedLabels = new Dictionary<string, long>(StringComparer.Ordinal);
        var proteins = Identifications.AddGroupProteins(
            Identifications.ProteinRows(
                new[] { psmColumns, peptideColumns }.Where(c => c.Count > 0).ToList(),
                datasetId, DatasetEntry.Text(entry.Organism), ContaminantOf, unresolvedLabels),
            proteinGroups, DatasetEntry.Text(entry.Organism));

        // --- quantities -----------------------------------------------------------------------------------
        var mbrBlock = Map(Get(searchProvenance, "mbr"));
        var mbrThreshold = mbrBlock is not null && mbrBlock.TryGetValue("mbr_fdr_threshold", out var th)
            ? Convert.ToDouble(th is string ts ? double.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) : th, System.Globalization.CultureInfo.InvariantCulture)
            : 0.01;
        var peaksPath = Join(resultsDir, "AllQuantifiedPeaks.tsv");
        if (File.Exists(peaksPath)) writer.AddSource(peaksPath, "peaks");
        var peakQuality = Quant.PeakQuality(peaksPath, datasetId, runNames, mbrThreshold, log);

        var quantValues = new List<Row>(proteinGroupQuant);
        var peptideQuantPath = Join(resultsDir, "AllQuantifiedPeptides.tsv");
        if (File.Exists(peptideQuantPath))
        {
            writer.AddSource(peptideQuantPath, "peptide_quant");
            quantValues.AddRange(Quant.PeptideQuantRows(peptideQuantPath, datasetId, runNames, s => proforma.Get(s).Proforma, peakQuality, log));
        }

        // --- search parameters and reported totals --------------------------------------------------------
        var taskFiles = TaskFiles(manifest.WorkRoot, searchProvenance);
        foreach (var path in taskFiles)
            writer.AddSource(path, $"task:{Path.GetFileNameWithoutExtension(path)}", Path.GetFileName(path));
        var searchModifications = SearchParams.ModificationRows(taskFiles, datasetId, name => registry.Lookup(name)?.UnimodCurie);

        var resultsPath = Join(resultsDir, "results.txt");
        var results = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        if (File.Exists(resultsPath))
        {
            writer.AddSource(resultsPath, "results_txt", "results.txt");
            results = Readers.ReadResultsTxt(resultsPath, log);
            metrics.AddRange(ResultsMetrics(results, datasetId, runNames, searchNamespace));
        }
        // Here rather than with the other provenance metrics because the per-run rows need the deposited run
        // names, which only exist once the runs are built (aging 014 section 3).
        metrics.AddRange(Provenance.ContaminationMetricRows(searchProvenance, datasetId, runNames, searchNamespace));

        // --- the dataset row ------------------------------------------------------------------------------
        var instruments = sdrf.RunFacts.Values
            .Select(f => f.GetValueOrDefault("instrument_model"))
            .Where(v => !IsFalsy(v)).Select(v => PyFormat.Str(v!)).Distinct().Order(StringComparer.Ordinal).ToList();
        var (databaseName, databaseSha) = SearchParams.SearchedDatabase(searchProvenance);
        var tools = Map(Get(searchProvenance, "tools"));
        var mmRelease = Get(Map(Get(tools, "MetaMorpheus")), "release");
        var engineVersion = !IsFalsy(mmRelease) ? S(mmRelease) : DatasetEntry.Text(entry.Metamorpheus);
        var pipeline = Map(Get(searchProvenance, "pipeline"));
        var quantMethod = DatasetEntry.Text(entry.QuantMethod);
        var datasetRow = new Row
        {
            ["dataset_id"] = datasetId,
            // The producer holds it (they fetch it from PRIDE); dataRepo does not call PRIDE itself.
            ["title"] = entry.Title,
            ["organisms"] = IsFalsy(entry.Organism) ? new List<object?>() : new List<object?> { entry.Organism },
            ["acquisition"] = entry.Acquisition,
            ["quant_method"] = QuantMethods.TryGetValue((quantMethod ?? "").ToLowerInvariant(), out var qm) ? qm : entry.QuantMethod,
            ["labelling"] = entry.Labelling,
            ["labelling_plex"] = entry.LabellingPlex,
            ["enrichment"] = entry.Enrichment.Cast<object?>().ToList(),
            ["enrichment_mixed"] = enrichmentMixed,
            ["instrument_vendor"] = Vendor(instruments),
            ["instruments"] = instruments.Cast<object?>().ToList(),
            // An allow-list, so an empty declaration means unrestricted (aging 024 section 7).
            ["permitted_responses"] = entry.PermittedResponses.Cast<object?>().ToList(),
            ["axis_source"] = "provenance",
            ["submission_type"] = null,
            ["search_engine"] = "MetaMorpheus",
            ["search_engine_version"] = engineVersion,
            ["search_database"] = databaseName,
            ["search_database_sha256"] = databaseSha,
            ["sdrf_status"] = sdrfStatus,
            ["pipeline_repo"] = Get(pipeline, "repo"),
            ["pipeline_commit"] = Get(pipeline, "commit"),
            ["searches"] = new List<object?> { searchLabel },
            ["first_release_id"] = null,
            ["latest_release_id"] = null,
        };

        // --- exact duplicates the producer wrote ----------------------------------------------------------
        // MetaMorpheus can write one row twice, identical in every column (aging 015). Collapsing is lossless and
        // happens before anything counts the rows, so every number below describes what the bundle holds.
        var collapsible = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
        {
            ["samples"] = samples.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["runs"] = runRows.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["assays"] = assays.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["peptidoforms"] = peptidoforms.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["protein_groups"] = proteinGroups.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["proteins"] = proteins.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["ptm_sites"] = ptmSites.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
            ["quant_values"] = quantValues.Cast<IReadOnlyDictionary<string, object?>>().ToList(),
        };
        var collapsed = Integrity.CollapseExactDuplicates(collapsible);
        samples = collapsible["samples"].Cast<Row>().ToList();
        runRows = collapsible["runs"].Cast<Row>().ToList();
        assays = collapsible["assays"].Cast<Row>().ToList();
        peptidoforms = collapsible["peptidoforms"].Cast<Row>().ToList();
        proteinGroups = collapsible["protein_groups"].Cast<Row>().ToList();
        proteins = collapsible["proteins"].Cast<Row>().ToList();
        ptmSites = collapsible["ptm_sites"].Cast<Row>().ToList();
        quantValues = collapsible["quant_values"].Cast<Row>().ToList();
        if (collapsed.Count > 0)
        {
            // The group count has to describe the rows, not the file.
            proteinGroupCount = Quant.AcceptedGroupCount(proteinGroups);
            findings.AddRange(DuplicateFindings(collapsed, datasetId));
        }

        // --- PTM site occupancy (D29) ---------------------------------------------------------------------
        // After the collapse, so it keys on the sites and groups the bundle will hold. Label-free only:
        // MetaMorpheus computes no intensity occupancy for TMT or SILAC, and its TMT count cell is per file
        // repeated on every channel (DEF-OCC-INT), which has no assay to sit on.
        var stoichiometry = new List<Row>();
        var occupancyNote = new OrderedDictionary<string, object?> { ["read"] = false };
        if (File.Exists(pgPath) && S(entry.QuantMethod) == "label-free")
        {
            var occ = Occupancy.Rows(pgPath, datasetId, runNames, sequences,
                ptmSites.Select(s => (string)s["ptm_site_id"]!).ToHashSet(StringComparer.Ordinal),
                proteinGroups.Select(g => (string)g["protein_group_id"]!).ToHashSet(StringComparer.Ordinal),
                assays.Select(a => (string)a["assay_id"]!).ToHashSet(StringComparer.Ordinal), log, rules);
            stoichiometry = occ.Rows;
            occupancyNote = new OrderedDictionary<string, object?>
            {
                ["read"] = true,
                ["entries"] = occ.Entries,
                ["rows"] = (long)occ.Rows.Count,
                ["not_stored"] = occ.NotStored,
                ["truncated_cells"] = occ.TruncatedCells,
                ["failed_fields"] = occ.FailedFields,
                ["realigned_cells"] = occ.RealignedCells,
                ["differing_duplicates"] = occ.DifferingDuplicates,
            };
            findings.AddRange(OccupancyFindings(occ, datasetId));
        }
        else if (File.Exists(pgPath))
        {
            occupancyNote = new OrderedDictionary<string, object?> { ["read"] = false, ["reason"] = $"quant_method {S(entry.QuantMethod)}" };
            findings.Add(new Row
            {
                ["finding_id"] = $"{datasetId}:occupancy_not_ingested",
                ["dataset_id"] = datasetId,
                ["run_id"] = null,
                ["code"] = "occupancy_not_ingested",
                ["severity"] = "info",
                ["status"] = "open",
                ["message"] =
                    $"PTM site occupancy was not stored: this is a {S(entry.QuantMethod)} dataset, and "
                    + "MetaMorpheus computes intensity occupancy for label-free searches only; its "
                    + "count occupancy is per file, repeated on every channel (QuantProject DEF-OCC-INT). "
                    + "`ptm_stoichiometry` has no rows here because of that, not because no site was "
                    + "modified.",
                ["source"] = "datarepo ingest",
            });
        }

        // --- reconciliation -------------------------------------------------------------------------------
        var searchMs2 = Get(Map(Get(searchProvenance, "id_rate")), "ms2");
        var checks = Reconcile.Build(
            Identifications.ProducerCounts(psmColumns),
            // No notch clause for peptidoforms: it is a PSM rule (aging 008), and applying it cost exactly 3 on
            // two datasets, making one count_mismatch spurious (DATAREPO-27).
            Identifications.ProducerCounts(peptideColumns.Count > 0 ? peptideColumns : psmColumns, requireResolvedNotch: false),
            proteinGroupCount,
            runRows,
            results,
            entry.Files is null ? null : Convert.ToInt64(entry.Files),
            searchMs2 is null ? null : Convert.ToInt64(searchMs2),
            Definitions.ById[Definitions.Cite(Definitions.Psm1pct, searchNamespace)]);
        findings.AddRange(Reconcile.FindingRows(checks, datasetId));
        findings.AddRange(Reconcile.MetricConflicts(metrics, datasetId));
        findings.AddRange(ModificationFindings(proforma, datasetId));
        findings.AddRange(UsiFindings(runNames, datasetId));
        findings.AddRange(EnrichmentFindings(runRows, datasetId, enrichmentMixed));
        findings.AddRange(ContaminantLabelFindings(unresolvedLabels, tc, datasetId));
        findings.AddRange(UnplacedSiteFindings(unplaced, sequences, datasetId));
        findings.AddRange(SiteResidueFindings(siteCheck, datasetId));

        // --- assemble -------------------------------------------------------------------------------------
        writer.Add("datasets", [datasetRow]);
        writer.Add("samples", samples);
        writer.Add("sample_characteristics", characteristics);
        writer.Add("runs", runRows);
        writer.Add("assays", assays);
        writer.Add("psms", psmRows);
        writer.Add("peptidoforms", peptidoforms);
        writer.Add("protein_groups", proteinGroups);
        writer.Add("proteins", proteins);
        writer.Add("ptm_sites", ptmSites);
        writer.Add("ptm_stoichiometry", stoichiometry);
        writer.Add("quant_values", quantValues);
        writer.Add("search_modifications_declared", searchModifications);
        writer.Add("metrics", metrics);
        writer.Add("provenance_records", provenanceRows);
        writer.Add("findings", findings);
        // A definition is carried when something in the bundle depends on it: every metric and quantity, plus the
        // notch rule, because Psm.notch_ambiguous is a stored conclusion and its text must travel with the rows.
        var used = metrics.Select(m => (string)m["definition_id"]!)
            .Concat(quantValues.Select(q => (string)q["definition_id"]!))
            .Concat(stoichiometry.Select(s => (string)s["definition_id"]!))
            .ToHashSet(StringComparer.Ordinal);
        if (psmRows.Count > 0) used.Add(Definitions.Cite(Definitions.NotchAmbiguous, searchNamespace));
        var definitionRows = Definitions.Rows(used);
        if (rules == IngestRules.Current)
        {
            // QuantProject 009: bundles written under the current rules carry the occupancy definition at v3.6.
            var at = definitionRows.FindIndex(r => (string)r["definition_id"]! == Definitions.Occupancy.DefinitionId);
            if (at >= 0) definitionRows[at] = Definitions.OccupancyV36.Row();
        }
        // `pep` is comparable only inside the search that wrote it (pep 002), so the text saying so travels with
        // the rows, versioned by the release and regime behind them.
        if (psmRows.Count > 0)
            definitionRows.Add(Definitions.PepDefinition(engineVersion, SearchParams.PepRegime(taskFiles),
                rules == IngestRules.Current ? SearchParams.PepIterative(taskFiles, engineVersion) : null).Row());
        writer.Add("definitions", definitionRows);

        writer.Notes["instance"] = manifest.Instance;
        writer.Notes["manifest"] = BundleWriter.PathText(manifest.Path);
        writer.Notes["run"] = entry.Run;
        writer.Notes["provenance_schema"] = S(Get(searchProvenance, "schema"));
        writer.Notes["definitions_namespace"] = searchNamespace;
        writer.Notes["metamorpheus"] = engineVersion;
        writer.Notes["licence"] = manifest.Licence;
        writer.Notes["credit"] = manifest.Credit;
        writer.Notes["readers"] = log.Entries;
        writer.Notes["modification_registry"] = new OrderedDictionary<string, object?>
        {
            ["source"] = settings is null ? null : BundleWriter.PathText(settings),
            ["entries"] = (long)registry.Count,
            ["files"] = registry.Sources,
            ["unresolved"] = proforma.Unresolved,
        };
        writer.Notes["protein_databases"] = new OrderedDictionary<string, object?>
        {
            ["read"] = sequences.Files,
            ["missing"] = sequences.Missing,
            ["unplaced_site_pairs"] = unplaced,
            ["site_residue_check"] = siteCheck,
        };
        writer.Notes["occupancy"] = occupancyNote;
        writer.Notes["reconciliation"] = checks.Select(c => c.AsDict()).ToList();
        writer.Notes["collapsed_duplicates"] = collapsed.Select(c => new OrderedDictionary<string, object?>
        {
            ["table"] = c.Table, ["key"] = c.Key, ["identifier"] = c.Identifier, ["written"] = (long)c.Written, ["dropped"] = (long)c.Dropped,
        }).ToList();

        // Re-ingesting unchanged inputs with unchanged code is a no-op, not an error: the pipeline runs it again
        // after every stage, and it should be safe to do so.
        var existing = Path.Combine(writer.BundlePath(), BundleWriter.ManifestName);
        var skipped = File.Exists(existing) && !overwrite;
        var output = skipped ? writer.BundlePath() : writer.Write(overwrite);
        using var manifestDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(output, BundleWriter.ManifestName)));
        var rowCounts = manifestDoc.RootElement.GetProperty("tables").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
        return new IngestResult(
            datasetId, output, writer.BundleId, rowCounts, checks.Select(c => c.AsDict()).ToList(), findings,
            new Dictionary<string, long>(proforma.Unresolved), new Dictionary<string, long>(runNames.Unmatched), skipped);
    }

    private static bool IsFalsy(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        long l => l == 0,
        double d => d == 0,
        System.Collections.ICollection c => c.Count == 0,
        _ => false,
    };

    private static Row Finding(string datasetId, string code, string severity, string message, string? runId = null, string source = "datarepo ingest") => new()
    {
        ["finding_id"] = $"{datasetId}:{code}",
        ["dataset_id"] = datasetId,
        ["run_id"] = runId,
        ["code"] = code,
        ["severity"] = severity,
        ["status"] = "open",
        ["message"] = message,
        ["source"] = source,
    };

    /// <summary>Metric rows for every total MetaMorpheus's results.txt reports.</summary>
    private static List<Row> ResultsMetrics(Dictionary<string, Dictionary<string, long>> results, string datasetId, RunNameMap runNames, string ns)
    {
        var definitionFor = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["psms"] = Definitions.Cite(Definitions.Psm1pct, ns),
            ["peptides"] = Definitions.Cite(Definitions.PeptideCount1pct, ns),
            ["protein_groups"] = Definitions.Cite(Definitions.ProteinGroupCount1pct, ns),
            ["ms2_scans"] = Definitions.Cite(Definitions.Ms2Count, ns),
            ["precursors"] = Definitions.Cite(Definitions.PrecursorCount, ns),
        };
        var rows = new List<Row>();
        foreach (var (scope, counts) in results)
        {
            string scopeKind, scopeId;
            if (scope.Length > 0)
            {
                var resolved = runNames.Resolve(scope);
                if (resolved is null) continue;
                (scopeKind, scopeId) = ("run", $"{datasetId}:{resolved}");
            }
            else (scopeKind, scopeId) = ("dataset", datasetId);
            foreach (var (name, value) in counts)
            {
                if (!definitionFor.TryGetValue(name, out var definition)) continue;
                rows.Add(new Row
                {
                    ["scope"] = scopeKind,
                    ["scope_id"] = scopeId,
                    ["name"] = name != "psms" ? name : "psms_1pct",
                    ["value"] = value,
                    ["definition_id"] = definition,
                    ["source"] = "results.txt",
                });
            }
        }
        return rows;
    }

    /// <summary>One Finding naming every row the producer wrote more than once. The collapse is lossless, so this
    /// is a fact about the producer's output that would otherwise vanish the moment it was repaired.</summary>
    private static List<Row> DuplicateFindings(List<Collapse> collapsed, string datasetId)
    {
        if (collapsed.Count == 0) return [];
        var parts = collapsed.GroupBy(c => c.Table).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var group = g.ToList();
            var example = group[0];
            return $"{g.Key}: {group.Count} identifier(s), {group.Sum(c => c.Dropped)} row(s) dropped, "
                + $"e.g. {example.Identifier} written {example.Written} times";
        });
        return
        [
            Finding(datasetId, "collapsed_duplicate_rows", "info",
                "The producer wrote some rows more than once, identical in every column, and the "
                + "copies were dropped so each identifier appears once. Nothing was lost: the kept "
                + "row is byte-for-byte what the duplicates said. Rows that share an identifier and "
                + "disagree anywhere are still refused rather than collapsed. "
                + string.Join("; ", parts)
                + ". The full list is in bundle.json under collapsed_duplicates.",
                source: "datarepo ingest integrity"),
        ];
    }

    private static List<Row> ModificationFindings(ProformaCache proforma, string datasetId)
    {
        if (proforma.Unresolved.Count == 0) return [];
        var names = string.Join(", ", proforma.Unresolved.Keys.Order(StringComparer.Ordinal));
        return
        [
            Finding(datasetId, "unresolved_modifications", "warning",
                $"{proforma.Unresolved.Count} modification(s) could not be mapped to a UNIMOD "
                + $"accession or a mass, and are carried in ProForma as [Info:...] tags: {names}. "
                + "Their ptm_sites rows exist and carry modification_name, with modification null, "
                + "so a query by name finds them and a query by UNIMOD accession will not."),
        ];
    }

    /// <summary>How many (PSM, protein) pairs got no site because their position could not be found. An unplaced
    /// site is a gap a reader can see; a misplaced one is a fact they will quote (aging 043).</summary>
    private static List<Row> UnplacedSiteFindings(OrderedDictionary<string, long> unplaced, ProteinSequences sequences, string datasetId)
    {
        if (unplaced.Count == 0 && sequences.Missing.Count == 0) return [];
        var parts = new List<string>();
        if (unplaced.GetValueOrDefault("no_sequence") > 0)
            parts.Add($"{unplaced["no_sequence"]} had no sequence in the searched databases");
        if (unplaced.GetValueOrDefault("peptide_not_in_sequence") > 0)
            parts.Add(
                $"{unplaced["peptide_not_in_sequence"]} named a protein that does not contain the "
                + "stored peptide -- typically a PSM ambiguous between peptide sequences (level 4 or 5), "
                + "where this protein carries a different candidate than the first, which is the one "
                + "stored");
        if (unplaced.GetValueOrDefault("c_term_no_sequence") > 0)
            parts.Add(
                $"{unplaced["c_term_no_sequence"]} carried a C-terminal modification on a protein with "
                + "no sequence, so protein and peptide C-terminus could not be told apart");
        if (sequences.Missing.Count > 0)
            parts.Add("database(s) named by the search provenance but not on disk: " + string.Join(", ", sequences.Missing));
        return
        [
            Finding(datasetId, "unplaced_ptm_sites", "warning",
                "Some modifications on peptides shared between proteins were not written to "
                + "ptm_sites for one or more of those proteins, because the peptide could not be "
                + "located in that protein's sequence and the producer's residue spans cannot be "
                + "paired with its accessions. Of the (PSM, protein) pairs affected: "
                + string.Join("; ", parts)
                + ". Sites on the other proteins of the same PSMs are written. Counts are in "
                + "bundle.json under protein_databases."),
        ];
    }

    private static List<Row> SiteResidueFindings(OrderedDictionary<string, long> check, string datasetId)
    {
        var bad = check.GetValueOrDefault("wrong_residue") + check.GetValueOrDefault("beyond_length");
        if (bad == 0) return [];
        return
        [
            Finding(datasetId, "ptm_site_residue_mismatch", "warning",
                $"{check.GetValueOrDefault("wrong_residue")} ptm_sites row(s) name a residue that is not at "
                + $"their position in the searched sequence, and {check.GetValueOrDefault("beyond_length")} have "
                + "a position beyond the protein's length. Treat those positions as wrong. Counts "
                + "are in bundle.json under protein_databases.site_residue_check.",
                source: "datarepo ingest (DATAREPO-32 self-check)"),
        ];
    }

    private static IEnumerable<Row> LineageFindings(List<Row> mismatches, string datasetId) =>
        mismatches.Select(m => new Row
        {
            ["finding_id"] = $"{datasetId}:upstream_provenance_changed:{S(m["stage"])}",
            ["dataset_id"] = datasetId,
            ["run_id"] = null,
            ["code"] = "upstream_provenance_changed",
            ["severity"] = "warning",
            ["status"] = "open",
            ["message"] =
                $"The search recorded its upstream '{S(m["stage"])}' provenance ({S(m["path"])}) with sha256 "
                + $"{S(m["recorded"])}, and the file there now has {S(m["actual"])}: it was overwritten after "
                + "the search. It is left out of this bundle rather than read as this search's record. "
                + $"The search's own record of the {S(m["stage"])} stage is gone unless the producer archived it.",
            ["source"] = "datarepo ingest",
        });

    private static List<Row> ContaminantLabelFindings(Dictionary<string, long> unresolved, string? tc, string datasetId)
    {
        if (unresolved.Count == 0) return [];
        var examples = string.Join(", ", unresolved.Keys.Order(StringComparer.Ordinal).Take(5));
        return
        [
            Finding(datasetId, "contaminant_label_unresolved", "warning",
                $"{unresolved.Count} protein(s) share a PSM row with proteins of another kind, and the "
                + "searched databases could not say which they are (not on disk, or in both a target "
                + $"and the contaminant database under TCAmbiguity {(string.IsNullOrEmpty(tc) ? "unknown" : tc)}). Each was given "
                + "its row's label, contaminant over target, so `is_contaminant` may over-state for "
                + $"them: {examples}."),
        ];
    }

    /// <summary>What the data-file gate did with an SDRF that does not match the searched runs (G62).</summary>
    private static Row SdrfGateFinding(string datasetId, string code, List<string> namedUnsearched, List<string> unnamed)
    {
        var parts = new List<string>();
        if (namedUnsearched.Count > 0)
            parts.Add(
                $"it names {namedUnsearched.Count} file(s) the search did not use, whose rows were not "
                + $"used: {string.Join(", ", namedUnsearched.Take(10))}{(namedUnsearched.Count > 10 ? " ..." : "")}");
        if (unnamed.Count > 0)
            parts.Add(
                $"it names no row for {unnamed.Count} searched run(s), each given a synthetic sample "
                + $"with no annotation: {string.Join(", ", unnamed.Take(10))}{(unnamed.Count > 10 ? " ..." : "")}");
        var what = code == "sdrf_unmatched"
            ? "The SDRF matches none of the searched files and was not used; every run has a synthetic "
              + "sample with no annotation, as if there were no SDRF"
            : "The SDRF was used only for the runs it names that were searched";
        var status = code.StartsWith("sdrf_", StringComparison.Ordinal) ? code["sdrf_".Length..] : code;
        return Finding(datasetId, code, "warning", $"{what}: {string.Join("; ", parts)}. sdrf_status is `{status}`.");
    }

    /// <summary>The SDRF characteristics behind the four curated sample columns an <c>sdrf_skeleton</c> finding names.</summary>
    private static readonly string[] AnnotationCharacteristics =
    [
        "characteristics[organism part]", "characteristics[cell type]", "characteristics[disease]", "characteristics[individual]",
    ];

    /// <summary>The distinct text values of the four annotation characteristics, e.g. <c>organism part: Urine</c>;
    /// empty when the SDRF holds none.</summary>
    private static string UncodedAnnotation(IEnumerable<Row> characteristics)
    {
        var seen = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var row in characteristics)
        {
            var name = (row.TryGetValue("name", out var n) && n is not null ? PyFormat.Str(n) : "").Trim().ToLowerInvariant();
            var value = (row.TryGetValue("value", out var v) && !IsFalsy(v) ? PyFormat.Str(v!) : "").Trim();
            if (AnnotationCharacteristics.Contains(name) && !Sdrf.NotAvailable.Contains(value.ToLowerInvariant()))
            {
                var key = name["characteristics[".Length..^1];
                if (!seen.TryGetValue(key, out var set)) seen[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(value);
            }
        }
        return string.Join("; ", seen.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value.Take(5))}"));
    }

    /// <summary>A finding when a dataset's runs differ in enrichment (G63), carried where it cannot be skipped (D19).</summary>
    private static List<Row> EnrichmentFindings(List<Row> runs, string datasetId, bool mixed)
    {
        if (!mixed) return [];
        var known = runs.Where(r => r["enrichment"] is not null)
            .GroupBy(r => string.Join(", ", ((IEnumerable<object?>)r["enrichment"]!).Select(x => S(x))))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var unknown = runs.Count(r => r["enrichment"] is null);
        string message;
        if (known.Count > 0)
        {
            var split = string.Join("; ", known.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Value} run(s) [{kv.Key}]"));
            message =
                $"The runs of this dataset differ in enrichment: {split}. The dataset's own `enrichment` "
                + "is the producer's declaration and is true of only some runs, so answer any "
                + "enrichment-dependent question per run from `runs.enrichment`, and never pool its runs "
                + "as one kind.";
        }
        else
        {
            message =
                "The producer flagged this dataset as mixing enrichments but did not say which run is "
                + $"which, so `runs.enrichment` is NULL on all {unknown} runs. Do not use it for any "
                + "question that depends on whether a run was enriched.";
        }
        return [Finding(datasetId, "mixed_enrichment", "warning", message)];
    }

    /// <summary>One finding per deposited file the search left out, so a query can find it (DATAREPO-51).</summary>
    private static List<Row> ExcludedFileFindings(IReadOnlySet<string> excluded, string? reason,
        IReadOnlyDictionary<string, object?>? fetch, IReadOnlyDictionary<string, object?>? qc, string datasetId)
    {
        var byName = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var f in List(Get(fetch, "files")))
        {
            var file = Map(f);
            byName[Path.GetFileName(S(Get(file, "name") ?? ""))] = file ?? new Dictionary<string, object?>();
        }
        var rows = new List<Row>();
        foreach (var name in excluded.Order(StringComparer.Ordinal))
        {
            byName.TryGetValue(name, out var entry);
            var known = byName.ContainsKey(name) || (qc is not null && qc.ContainsKey(name));
            var sha = Get(entry, "sha256");
            rows.Add(new Row
            {
                ["finding_id"] = $"{datasetId}:excluded_from_search:{name}",
                ["dataset_id"] = datasetId,
                ["run_id"] = null,
                ["code"] = "excluded_from_search",
                ["severity"] = "info",
                ["status"] = "open",
                ["message"] =
                    $"The deposited file {name} was left out of the search, so it has no run row, "
                    + $"no PSMs and no quantities here. Producer's reason: {(string.IsNullOrEmpty(reason) ? "not given" : reason)}. "
                    + (!IsFalsy(sha) ? $"Deposited sha256: {S(sha)}." : "Its checksum is not in the fetch manifest.")
                    + (known ? "" : " The name matches no file in the fetch manifest or the QC report."),
                ["source"] = "search provenance.json: excluded_files",
            });
        }
        return rows;
    }

    /// <summary>Every occupancy entry the ingest read and did not store, said once, by reason (D29).</summary>
    private static List<Row> OccupancyFindings(OccupancyResult occ, string datasetId)
    {
        var findings = new List<Row>();
        if (occ.TruncatedCells > 0)
            findings.Add(Finding(datasetId, "occupancy_cells_truncated", "error",
                $"{occ.TruncatedCells} occupancy cell(s) were replaced by MetaMorpheus's 'Output too "
                + "long for Excel' sentence (the machine's WriteExcelCompatibleTSVs setting). Every site "
                + "in such a cell is missing from ptm_stoichiometry, and it is the largest groups that "
                + "lose them. Re-run the search with the setting off (QuantProject DEF-OCC-TRUNCATE)."));
        if (occ.FailedFields.Count > 0)
            findings.Add(Finding(datasetId, "occupancy_cells_unparsed", "error",
                $"pyMzLib could not parse some occupancy cells ({string.Join(", ", occ.FailedFields)}), so the "
                + "sites in them are missing from ptm_stoichiometry. The cell does not follow "
                + "MetaMorpheus's grammar (QuantProject DEF-OCC-CELL); report it with the file."));
        var lost = occ.NotStored.Where(kv => kv.Key != "decoy group").OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (lost.Count > 0)
        {
            var detail = string.Join("; ", lost.Select(kv => $"{kv.Value} {kv.Key}"));
            findings.Add(Finding(datasetId, "occupancy_not_stored", "warning",
                $"Of {occ.Entries} occupancy entries MetaMorpheus wrote, some are not in "
                + $"ptm_stoichiometry: {detail}. 'site not in ptm_sites' means the entry's site has no "
                + "ptm_sites row to key on; 'not determinable' means a cell's segments could not be "
                + "assigned to its accessions from the sequences (QuantProject DEF-OCC-ACCESSION), "
                + "which is never guessed. Absence of a row for these sites is therefore not NA. "
                + "A 'duplicate' is a second entry for a (site, run) that already has a row: one with "
                + "identical values loses nothing; one with DIFFERENT values means MetaMorpheus wrote "
                + "two answers and the first in file order was stored (examples in bundle.json "
                + "occupancy.differing_duplicates; which one should be stored is QuantProject's to define)."));
        }
        if (occ.NotStored.GetValueOrDefault("decoy group") > 0)
            findings.Add(Finding(datasetId, "occupancy_decoy_groups", "info",
                $"{occ.NotStored["decoy group"]} occupancy entries on decoy protein groups were not "
                + "stored: MetaMorpheus computes them, and they are FDR machinery, not biology."));
        return findings;
    }

    private static List<Row> UsiFindings(RunNameMap runNames, string datasetId)
    {
        if (runNames.Unmatched.Count == 0) return [];
        var names = string.Join(", ", runNames.Unmatched.Keys.Order(StringComparer.Ordinal));
        return
        [
            Finding(datasetId, "unmatched_runs", "warning",
                "Run name(s) the search reported could not be matched to a deposited file, so "
                + "their PSMs carry no USI and their quantities are attached to an assay ID that "
                + $"has no run row: {names}."),
        ];
    }
}
