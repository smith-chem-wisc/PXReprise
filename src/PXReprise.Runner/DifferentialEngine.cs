using System.Globalization;
using System.Text;
using System.Text.Json;
using PXReprise.Bundle;
using PXReprise.Catalog;

namespace PXReprise.Runner;

/// <summary><c>differential.register</c>: register one of PXReprise's <c>differential</c> stage folders beside the bundle of
/// the search it read (our 145, PXR-R78; PXR-R85), so a catalog serves it (<c>CatalogDifferential.cs</c>).</summary>
/// <remarks>
/// <para><b>It computes nothing.</b> The analysis is mzLib's, under QuantProject's <c>DEF-DIFF-ABUNDANCE</c>, run by
/// PXReprise's batch into <c>&lt;run&gt;/&lt;PXD&gt;/06_differential/</c>. This checks that a stage folder keeps its own
/// record and is bound to the bundle, turns its tables into rows, and keeps every file of it verbatim under
/// <see cref="FilesDir"/>, so the artefact still holds the stage when the run folder is superseded.</para>
/// <para><b>Input:</b> <c>stage</c>, the stage folder (it holds <c>provenance.json</c> and one folder per analysis), and
/// one accession, whose bundle is picked as for every engine (<c>--bundle</c>, <c>--latest</c>).</para>
/// <para><b>Acceptance, each refusing the run:</b></para>
/// <list type="bullet">
/// <item><c>provenance.json</c> says <c>stage: differential</c> and <c>schema: pxreprise-differential/1</c>, no other version
/// read as 1;</item>
/// <item>every input the stage recorded is a source of the bundle, by sha256, the search's own <c>provenance.json</c>
/// (<c>provenance:04_search</c>) among them;</item>
/// <item>every input an analysis's <c>DifferentialResults.metadata.json</c> names by file is the bundle's source of the
/// same role, by file name and sha256. An input that names no file is recorded and not bound, and only
/// <c>observation_table</c> may (PXR-R85, option A: the table mzLib builds in memory from the peptide file);</item>
/// <item>each analysis's folder holds the results, decisions and metadata files; the metadata's <c>analysis_id</c> and
/// <c>row_count</c> and the stage's counts agree with the files, every row names its analysis, and every results row its
/// definition;</item>
/// <item>both tables have exactly mzLib's <c>diff-1.0</c> header, and every cell reads as its column's type
/// (<see cref="CatalogBuilder.DifferentialResultColumns"/>): a blank cell is NULL, anything else unreadable is refused.</item>
/// </list>
/// <para><b>The artefact id</b> hashes the bundle id and every file of the stage by its path and sha256, so registering the
/// same folder again is "already done", and a re-run of the stage is another artefact (a catalog then refuses two for one
/// bundle until the operator moves one out).</para>
/// </remarks>
public static class DifferentialEngine
{
    public const string Engine = PXReprise.Catalog.Runner.DifferentialEngine;

    /// <summary>The one stage record version this reads.</summary>
    public const string StageSchema = "pxreprise-differential/1";

    public const string StageRole = "stage";
    public const string ProvenanceFile = "provenance.json";
    public const string ResultsFile = "DifferentialResults.tsv";
    public const string DecisionsFile = "DifferentialDecisions.tsv";
    public const string MetadataFile = "DifferentialResults.metadata.json";
    public const string ReportFile = "StatisticalReport.md";
    public const string AssumptionsFile = "DifferentialAssumptions.md";

    /// <summary>Where the artefact keeps the stage's files, at their paths in the stage folder.</summary>
    public const string FilesDir = "files";

    /// <summary>The one metadata input that may name no file (PXR-R85, option A).</summary>
    public const string ObservationTableRole = "observation_table";

    /// <summary>The bundle source the stage's search record must be.</summary>
    public const string SearchProvenanceRole = "provenance:04_search";

    /// <summary>One file of the stage: its path in the folder (with <c>/</c>), where it is on disk, its sha256 and size.</summary>
    public sealed record StageFile(string Path, string Source, string Sha256, long Size);

    /// <summary>Register one stage folder for one bundle.</summary>
    /// <exception cref="RunnerException">A missing or bad input, a stage that is not bound to the bundle, or a development
    /// datarepo.</exception>
    public static RunResult Run(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, object?>? install = null)
    {
        var unknown = inputs.Keys.Where(k => k != StageRole).Order(StringComparer.Ordinal).ToList();
        if (!inputs.TryGetValue(StageRole, out var stage) || unknown.Count > 0)
            throw new RunnerException($"{Engine} takes one input, --input {StageRole}=<the stage folder, e.g. <run>/<PXD>/06_differential>"
                + (unknown.Count > 0 ? $"; not {string.Join(", ", unknown)}." : "."));
        if (bundles.Count != 1)
            throw new RunnerException($"{Engine} registers one deposit's stage at a time: name its accession once (given {bundles.Count}).");
        if (!Directory.Exists(stage)) throw new RunnerException($"--input {StageRole}={stage}: no such folder");
        var bundle = bundles[0];
        var provenancePath = Path.Combine(stage, ProvenanceFile);
        if (!File.Exists(provenancePath))
            throw new RunnerException($"{BundleWriter.PathText(stage)} holds no {ProvenanceFile}: the stage has not finished, or this is not a "
                + "differential stage folder. A stage writes its record last.");
        var provenance = CatalogBuilder.ReadJsonObject(provenancePath, m => new RunnerException(m));
        CheckStage(provenance, stage);

        var files = StageFiles(stage);
        var hashed = new Dictionary<string, string>(StringComparer.Ordinal) { ["bundle"] = bundle.BundleId };
        foreach (var f in files) hashed[$"file:{f.Path}"] = f.Sha256;
        var tools = Map(provenance.GetValueOrDefault("tools"));
        var release = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in new[] { "pxreprise", "mzlib" })
            if (tools.GetValueOrDefault(tool) is string v && v.Length > 0) release[tool] = v;
        if (!release.ContainsKey("mzlib"))
            throw new RunnerException($"{BundleWriter.PathText(provenancePath)}: tools.mzlib is not recorded, so the analysis cannot be tied to the mzLib that computed it.");
        var definition = Str(provenance, "definition")
            ?? throw new RunnerException($"{BundleWriter.PathText(provenancePath)}: no definition is recorded.");
        var aid = EngineRunner.ArtefactId(Engine, release, hashed, definition);
        var result = new RunResult();
        var existing = EngineRunner.ArtefactDir(store, Engine, aid);
        if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
        {
            result.AlreadyDone.Add(ArtefactRef.Load(existing));
            return result;
        }
        install ??= EngineRunner.InstallIdentity();

        var sources = SourcesOf(bundle);
        var boundStage = BindStage(provenance, bundle, sources, store);
        var read = ReadAnalyses(stage, provenance, sources, SearchOutputs(bundle, sources), definition);
        var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
        {
            [CatalogBuilder.DifferentialStagesTable] = [StageRow(provenance, read.Analyses.Count)],
            [CatalogBuilder.DifferentialAnalysesTable] = read.Analyses,
            [CatalogBuilder.DifferentialResultsTable] = read.Results,
            [CatalogBuilder.DifferentialDecisionsTable] = read.Decisions,
            [CatalogBuilder.DifferentialExclusionsTable] = ExclusionRows(provenance),
            [CatalogBuilder.DifferentialFindingsTable] = List(provenance.GetValueOrDefault("findings")).Select(Map)
                .Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["kind"] = f.GetValueOrDefault("kind"), ["detail"] = f.GetValueOrDefault("detail") })
                .ToList(),
            [CatalogBuilder.DifferentialFilesTable] = files.Select(f => FileRow(f, read.AnalysisNames)).ToList(),
        };
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["definition_id"] = definition,
            ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["input_files"] = new Dictionary<string, object?> { [StageRole] = BundleWriter.PathText(Path.GetFullPath(stage)) },
            ["datarepo_install"] = install,
            ["requested_for"] = new List<object?> { new Dictionary<string, object?> { ["dataset_id"] = bundle.DatasetId, ["bundle_id"] = bundle.BundleId } },
            ["engine_summary"] = new Dictionary<string, object?>
            {
                ["dataset_id"] = bundle.DatasetId,
                ["bundle_id"] = bundle.BundleId,
                ["analysed"] = read.Analyses.Count > 0,
                ["analyses"] = read.Analyses.Select(a => (object?)new Dictionary<string, object?>
                {
                    ["name"] = a["name"], ["analysis_id"] = a["analysis_id"], ["rows"] = a["rows"], ["decisions"] = a["decisions"],
                }).ToList(),
                ["finding_kinds"] = List(provenance.GetValueOrDefault("findings")).Select(f => Map(f).GetValueOrDefault("kind")).ToList(),
                ["stage_inputs_bound"] = (long)boundStage,
                ["files"] = (long)files.Count,
            },
            ["acceptance"] = "passed",
        };
        result.Written.Add(EngineRunner.WriteArtefact(store, Engine, aid, record, tables,
            files.ToDictionary(f => $"{FilesDir}/{f.Path}", f => f.Source),
            CatalogBuilder.DifferentialColumns.ToDictionary(kv => kv.Key, kv => kv.Value)));
        return result;
    }

    /// <summary>The stage's record is one this reads.</summary>
    private static void CheckStage(IReadOnlyDictionary<string, object?> provenance, string stage)
    {
        var where = BundleWriter.PathText(Path.Combine(stage, ProvenanceFile));
        if (Str(provenance, "stage") != "differential")
            throw new RunnerException($"{where} says stage {PyFormat.Repr(provenance.GetValueOrDefault("stage"))}, not \"differential\".");
        if (Str(provenance, "schema") != StageSchema)
            throw new RunnerException($"{where} is schema {PyFormat.Repr(provenance.GetValueOrDefault("schema"))}; this datarepo reads {StageSchema} only.");
        var analyses = List(provenance.GetValueOrDefault("analyses"));
        if (provenance.GetValueOrDefault("analysed") is not bool analysed || analysed != analyses.Count > 0)
            throw new RunnerException($"{where}: analysed is {PyFormat.Repr(provenance.GetValueOrDefault("analysed"))} with {analyses.Count} analyses.");
    }

    /// <summary>Every file under the stage folder, by its path there, in code-point order.</summary>
    public static List<StageFile> StageFiles(string stage) =>
        Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(p => new StageFile(Path.GetRelativePath(stage, p).Replace('\\', '/'), p, BundleWriter.Sha256File(p), new FileInfo(p).Length))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>The bundle's sources: role, file name and sha256.</summary>
    private static List<(string Role, string Name, string Sha256)> SourcesOf(BundleRef bundle) =>
        List(bundle.Manifest.GetValueOrDefault("sources")).Select(Map)
            .Select(s => (Str(s, "role") ?? "", PathName(Str(s, "path") ?? ""), Str(s, "sha256") ?? ""))
            .ToList();

    /// <summary>The bundle's own copy of its search record, which lists every file the search wrote, by sha256.</summary>
    public const string SearchRecordCopy = "sources/provenance_04_search.json";

    /// <summary>What a stage input may be: a source of the bundle, or a file the bundle's search record lists among its
    /// outputs (MetaMorpheus's written <c>experiment.sdrf.tsv</c>, which the stage reads for each sample's individual). The
    /// record is itself a source, by sha256, so a file it names by sha256 is bound to the bundle through it.</summary>
    private static HashSet<string> BoundShas(BundleRef bundle, List<(string Role, string Name, string Sha256)> sources)
    {
        var shas = sources.Select(s => s.Sha256).ToHashSet(StringComparer.Ordinal);
        shas.UnionWith(SearchOutputs(bundle, sources).Select(o => o.Sha256));
        return shas;
    }

    /// <summary>The files the bundle's search record lists among its outputs, by file name and sha256; none when the bundle
    /// keeps no copy of the record, or its copy is not the record it was ingested from.</summary>
    private static List<(string Name, string Sha256)> SearchOutputs(BundleRef bundle, List<(string Role, string Name, string Sha256)> sources)
    {
        var copy = Path.Combine(bundle.Path, SearchRecordCopy);
        if (!File.Exists(copy) || !sources.Any(s => s.Role == SearchProvenanceRole && s.Sha256 == BundleWriter.Sha256File(copy))) return [];
        var record = CatalogBuilder.ReadJsonObject(copy, m => new RunnerException(m));
        return List(record.GetValueOrDefault("outputs")).Select(Map)
            .Select(o => (PathName(Str(o, "path") ?? ""), Str(o, "sha256") ?? "")).Where(o => o.Item2.Length > 0).ToList();
    }

    /// <summary>Every input the stage recorded is bound to the bundle (<see cref="BoundShas"/>), the search's own record
    /// among them.</summary>
    /// <returns>How many were bound.</returns>
    private static int BindStage(IReadOnlyDictionary<string, object?> provenance, BundleRef bundle, List<(string Role, string Name, string Sha256)> sources, string store)
    {
        var shas = BoundShas(bundle, sources);
        var recorded = List(provenance.GetValueOrDefault("inputs")).Select(Map)
            .Select(i => (Name: PathName(Str(i, "path") ?? ""), Sha256: Str(i, "sha256") ?? "")).ToList();
        var unbound = recorded.Where(i => !shas.Contains(i.Sha256)).ToList();
        var search = sources.Where(s => s.Role == SearchProvenanceRole).Select(s => s.Sha256).ToList();
        var readSearch = search.Count == 0 || recorded.Any(i => search.Contains(i.Sha256));
        if (unbound.Count == 0 && readSearch && recorded.Count > 0) return recorded.Count;

        // Name the bundle the stage does belong to, when the store holds one: a stage of a superseded search is the usual case.
        var others = CatalogBuilder.DiscoverBundles(store, bundle.DatasetId)
            .Where(b => b.BundleId != bundle.BundleId)
            .Where(b => recorded.Count > 0 && recorded.All(i => BoundShas(b, SourcesOf(b)).Contains(i.Sha256)))
            .Select(b => b.BundleId).ToList();
        var what = recorded.Count == 0 ? "records no input"
            : unbound.Count > 0 ? $"read {string.Join(", ", unbound.Select(i => $"{i.Name} ({i.Sha256[..Math.Min(16, i.Sha256.Length)]})"))}, which bundle {bundle.BundleId} does not hold"
            : $"does not name the search record bundle {bundle.BundleId} was ingested from ({SearchProvenanceRole})";
        throw new RunnerException($"{bundle.DatasetId}: the stage {what}, so it was not computed from this bundle's search. "
            + (others.Count > 0 ? $"It is bound to {string.Join(", ", others)}: pin that one with --bundle {bundle.DatasetId}=<id>." : "No bundle in the store holds its inputs."));
    }

    private sealed record AnalysesRead(List<IReadOnlyDictionary<string, object?>> Analyses, List<IReadOnlyDictionary<string, object?>> Results,
        List<IReadOnlyDictionary<string, object?>> Decisions, HashSet<string> AnalysisNames);

    /// <summary>Each analysis's row, results and decisions, with every check on them.</summary>
    private static AnalysesRead ReadAnalyses(string stage, IReadOnlyDictionary<string, object?> provenance, List<(string Role, string Name, string Sha256)> sources,
        List<(string Name, string Sha256)> searchOutputs, string definition)
    {
        var analyses = new List<IReadOnlyDictionary<string, object?>>();
        var results = new List<IReadOnlyDictionary<string, object?>>();
        var decisions = new List<IReadOnlyDictionary<string, object?>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in List(provenance.GetValueOrDefault("analyses")).Select(Map))
        {
            var name = Str(a, "name") ?? "";
            if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".." || !names.Add(name))
                throw new RunnerException($"{ProvenanceFile}: analysis name {PyFormat.Repr(name)} is not one folder of the stage, or is given twice.");
            var dir = Path.Combine(stage, name);
            foreach (var f in new[] { ResultsFile, DecisionsFile, MetadataFile })
                if (!File.Exists(Path.Combine(dir, f))) throw new RunnerException($"{BundleWriter.PathText(dir)} holds no {f}.");
            var analysisId = Str(a, "analysis_id") ?? "";
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, MetadataFile)));
            var m = metadata.RootElement;
            var where = $"{name}/{MetadataFile}";
            if (Prop(m, "analysis_id") != analysisId)
                throw new RunnerException($"{where}: analysis_id {PyFormat.Repr(Prop(m, "analysis_id"))}, and the stage's record says {PyFormat.Repr(analysisId)}.");

            var bound = new List<object?>();
            string? observation = null;
            foreach (var input in m.TryGetProperty("inputs", out var list) ? list.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var (role, file, sha) = (Prop(input, "role") ?? "", Prop(input, "file") ?? "", Prop(input, "sha256") ?? "");
                if (file.Length == 0)
                {
                    if (role != ObservationTableRole)
                        throw new RunnerException($"{where}: input {PyFormat.Repr(role)} names no file. Only {ObservationTableRole} may (it is built in memory from the peptide file); another is a new case to decide, not one to pass.");
                    observation = sha;
                    continue;
                }
                var source = sources.Where(s => s.Role == role).ToList();
                // A role the bundle has no source of (written_sdrf: MetaMorpheus's experiment.sdrf.tsv) binds through the
                // bundle's search record, which lists the file by name and sha256.
                if (source.Count == 0 && searchOutputs.Any(o => o.Name == file && o.Sha256 == sha))
                {
                    bound.Add(role);
                    continue;
                }
                if (source.Count != 1 || source[0].Sha256 != sha || source[0].Name != file)
                    throw new RunnerException($"{where}: input {role} is {file} ({sha[..Math.Min(16, sha.Length)]}), and the bundle's "
                        + (source.Count == 0 ? $"has no {role} source and its search record lists no such output" : $"{role} source is {string.Join(", ", source.Select(s => $"{s.Name} ({s.Sha256[..Math.Min(16, s.Sha256.Length)]})"))}")
                        + ", so the analysis was not computed from this bundle's files.");
                bound.Add(role);
            }

            var rows = ReadTsv(Path.Combine(dir, ResultsFile), CatalogBuilder.DifferentialResultColumns, $"{name}/{ResultsFile}");
            var expectedRows = Long(a, "rows");
            // diff-1.0 writes row_count as {"total": n, "by_status": {status: n}}.
            var count = m.TryGetProperty("row_count", out var rc) && rc.ValueKind == JsonValueKind.Object ? rc : default;
            var metadataRows = count.ValueKind == JsonValueKind.Object && count.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number
                ? total.GetInt64() : (long?)null;
            if (rows.Count != expectedRows || rows.Count != metadataRows)
                throw new RunnerException($"{name}/{ResultsFile} holds {rows.Count} rows; the stage's record says {PyFormat.Repr(expectedRows)} and the metadata's row_count.total {PyFormat.Repr(metadataRows)}.");
            if (count.ValueKind == JsonValueKind.Object && count.TryGetProperty("by_status", out var byStatus) && byStatus.ValueKind == JsonValueKind.Object)
            {
                var stated = byStatus.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);
                var observed = rows.GroupBy(r => r["status"] as string ?? "").ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
                if (stated.Count != observed.Count || stated.Any(kv => observed.GetValueOrDefault(kv.Key) != kv.Value))
                    throw new RunnerException($"{name}/{ResultsFile}: its rows by status ({string.Join(", ", observed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"))}) "
                        + $"are not the metadata's row_count.by_status ({string.Join(", ", stated.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"))}).");
            }
            foreach (var (row, index) in rows.Select((r, i) => (r, i)))
            {
                if (!Equals(row["analysis_id"], analysisId))
                    throw new RunnerException($"{name}/{ResultsFile} row {index + 1} names analysis {PyFormat.Repr(row["analysis_id"])}, not {analysisId}.");
                if (!Equals(row["definition_id"], definition))
                    throw new RunnerException($"{name}/{ResultsFile} row {index + 1} is under {PyFormat.Repr(row["definition_id"])}, and the stage's record says {definition}.");
            }
            var decisionRows = ReadTsv(Path.Combine(dir, DecisionsFile), CatalogBuilder.DifferentialDecisionColumns, $"{name}/{DecisionsFile}");
            if (decisionRows.Count != Long(a, "decisions"))
                throw new RunnerException($"{name}/{DecisionsFile} holds {decisionRows.Count} rows; the stage's record says {PyFormat.Repr(Long(a, "decisions"))}.");
            if (decisionRows.FirstOrDefault(r => !Equals(r["analysis_id"], analysisId)) is { } stray)
                throw new RunnerException($"{name}/{DecisionsFile} has a row of analysis {PyFormat.Repr(stray["analysis_id"])}, not {analysisId}.");
            results.AddRange(rows);
            decisions.AddRange(decisionRows);

            analyses.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["analysis_id"] = analysisId,
                ["name"] = name,
                ["quant_basis"] = Str(a, "quant_basis"),
                ["definition_version"] = Prop(m, "definition_version"),
                ["method_version"] = m.TryGetProperty("software", out var sw) && sw.ValueKind == JsonValueKind.Array
                    ? sw.EnumerateArray().Where(s => Prop(s, "name") == "mzLib").Select(s => Prop(s, "version")).FirstOrDefault() : null,
                ["samples"] = Long(a, "samples"),
                ["rows"] = (long)rows.Count,
                ["contrasts"] = Long(a, "contrasts"),
                ["decisions"] = (long)decisionRows.Count,
                ["design_warnings"] = List(a.GetValueOrDefault("design_warnings")),
                ["settings_json"] = m.TryGetProperty("settings", out var settings) ? settings.GetRawText() : null,
                ["bound_roles"] = bound,
                ["observation_table_sha256"] = observation,
            });
        }
        return new AnalysesRead(analyses, results, decisions, names);
    }

    /// <summary>One tab-separated table, its header exactly <paramref name="columns"/>, each cell read as its column's type.</summary>
    /// <exception cref="RunnerException">Another header, a row of another width, or a cell that does not read.</exception>
    public static List<IReadOnlyDictionary<string, object?>> ReadTsv(string path, IReadOnlyList<ColumnSpec> columns, string label)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var reader = new StreamReader(path, new UTF8Encoding(false));
        var header = reader.ReadLine()?.TrimEnd('\r').Split('\t') ?? [];
        var names = columns.Select(c => c.Name).ToList();
        if (!header.SequenceEqual(names, StringComparer.Ordinal))
        {
            var missing = names.Except(header, StringComparer.Ordinal).ToList();
            var extra = header.Except(names, StringComparer.Ordinal).ToList();
            throw new RunnerException($"{label}: the header is not mzLib's diff-1.0 header"
                + (missing.Count > 0 ? $"; missing {string.Join(", ", missing)}" : "")
                + (extra.Count > 0 ? $"; not known: {string.Join(", ", extra)}" : "")
                + (missing.Count == 0 && extra.Count == 0 ? "; the same columns in another order" : "")
                + ". A new column is a decision for this datarepo, not a guess.");
        }
        string? line;
        var number = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            number++;
            line = line.TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            if (cells.Length != columns.Count)
                throw new RunnerException($"{label} line {number}: {cells.Length} cells, and the header has {columns.Count}.");
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            for (var c = 0; c < columns.Count; c++)
                row[columns[c].Name] = Cell(cells[c], columns[c], label, number);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A cell as its column's type: blank is NULL, and a cell that does not read is refused, never read as NULL.</summary>
    private static object? Cell(string cell, ColumnSpec column, string label, int line)
    {
        if (cell.Length == 0) return null;
        object? value = column.Type switch
        {
            ColumnType.String => cell,
            ColumnType.Float64 => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            ColumnType.Int64 => long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
            ColumnType.Boolean => cell switch { "true" => true, "false" => false, _ => null },
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, null),
        };
        return value ?? throw new RunnerException($"{label} line {line}: {column.Name} is {PyFormat.Repr(cell)}, which is not a {column.Type}.");
    }

    /// <summary>The stage's own row.</summary>
    private static IReadOnlyDictionary<string, object?> StageRow(IReadOnlyDictionary<string, object?> p, int analyses)
    {
        var design = Map(p.GetValueOrDefault("design"));
        var individuals = Map(p.GetValueOrDefault("individuals"));
        var search = Map(p.GetValueOrDefault("search"));
        var filter = Map(search.GetValueOrDefault("identification_filter"));
        var tools = Map(p.GetValueOrDefault("tools"));
        var corrected = p.GetValueOrDefault("corrected_sdrf") is IReadOnlyDictionary<string, object?> c ? Str(c, "sdrf_sha256") : null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stage_schema"] = Str(p, "schema"),
            ["definition_id"] = Str(p, "definition"),
            ["method_version"] = Str(p, "method_version"),
            ["quant_style"] = Str(p, "quant_style"),
            ["match_between_runs"] = p.GetValueOrDefault("match_between_runs") as bool?,
            ["analysed"] = analyses > 0,
            ["n_analyses"] = (long)analyses,
            ["design_file"] = Str(design, "file") is { } file ? PathName(file) : null,
            ["design_sha256"] = Str(design, "sha256"),
            ["design_samples"] = Long(design, "samples"),
            ["conditions"] = List(design.GetValueOrDefault("conditions")),
            ["corrected_sdrf_sha256"] = corrected,
            ["individuals_source"] = Str(individuals, "source"),
            ["individuals_reason"] = Str(individuals, "reason"),
            ["n_individuals"] = Long(individuals, "individuals"),
            ["metamorpheus"] = Str(search, "metamorpheus"),
            ["identification_filter"] = List(filter.GetValueOrDefault("counts")).Select(Map).Select(x => Str(x, "filter")).FirstOrDefault(),
            ["pep_trained"] = filter.GetValueOrDefault("pep_trained") as bool?,
            ["pxreprise"] = Str(tools, "pxreprise"),
            ["mzlib"] = Str(tools, "mzlib"),
            ["n_samples_left_out"] = (long)List(p.GetValueOrDefault("samples_left_out")).Count,
            ["n_lost_transfer_files"] = (long)List(p.GetValueOrDefault("lost_transfer_files")).Count,
            ["finding_kinds"] = List(p.GetValueOrDefault("findings")).Select(f => Map(f).GetValueOrDefault("kind")).ToList(),
            ["started_utc"] = Str(p, "started_utc"),
            ["finished_utc"] = Str(p, "finished_utc"),
        };
    }

    /// <summary>What an entry of <c>samples_left_out</c> may hold: a label-free sample names its files, an isobaric one its
    /// channels (<c>DifferentialStage</c>).</summary>
    private static readonly string[] LeftOutKeys = ["sample", "condition", "files", "channels"];

    /// <summary>What an entry of <c>lost_transfer_files</c> may hold.</summary>
    private static readonly string[] LostTransferKeys = ["file", "mbr_peaks", "min_pip_q_value"];

    /// <summary>One row per sample the stage left out and per file that lost every match-between-runs transfer.</summary>
    /// <remarks>An entry holding a key not read here is refused, never stored without it.</remarks>
    private static List<IReadOnlyDictionary<string, object?>> ExclusionRows(IReadOnlyDictionary<string, object?> p)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var s in List(p.GetValueOrDefault("samples_left_out")).Select(Map))
        {
            OnlyKnownKeys(s, LeftOutKeys, "samples_left_out");
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "sample_left_out", ["sample"] = Str(s, "sample"), ["condition"] = Str(s, "condition"),
                ["files"] = List(s.GetValueOrDefault("files")), ["channels"] = List(s.GetValueOrDefault("channels")),
            });
        }
        foreach (var f in List(p.GetValueOrDefault("lost_transfer_files")).Select(Map))
        {
            OnlyKnownKeys(f, LostTransferKeys, "lost_transfer_files");
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = "lost_transfer_file", ["file"] = Str(f, "file"), ["mbr_peaks"] = Long(f, "mbr_peaks"),
                ["min_pip_q_value"] = f.GetValueOrDefault("min_pip_q_value") switch { double d => d, long l => (double)l, _ => null },
            });
        }
        return rows;
    }

    private static void OnlyKnownKeys(IReadOnlyDictionary<string, object?> entry, string[] known, string field)
    {
        var other = entry.Keys.Where(k => !known.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (other.Count > 0)
            throw new RunnerException($"{ProvenanceFile}: an entry of {field} holds {string.Join(", ", other)}, which this register does not "
                                      + "read. That is a new case to decide, not one to store without it.");
    }

    /// <summary>A stored file's row, its role read from its name and place.</summary>
    private static IReadOnlyDictionary<string, object?> FileRow(StageFile f, HashSet<string> analyses)
    {
        var parts = f.Path.Split('/');
        var analysis = parts.Length > 1 && analyses.Contains(parts[0]) ? parts[0] : null;
        var name = parts[^1];
        var role = (analysis, parts.Length, name) switch
        {
            (null, 1, ProvenanceFile) => "provenance",
            (not null, 2, ResultsFile) => "results",
            (not null, 2, DecisionsFile) => "decisions",
            (not null, 2, MetadataFile) => "metadata",
            (not null, 2, ReportFile) => "report",
            (not null, 2, AssumptionsFile) => "assumptions",
            (not null, 3, _) when parts[1] == "figures" && name.EndsWith(".svg", StringComparison.Ordinal) => "figure",
            (not null, 3, _) when parts[1] == "figures" && name.EndsWith(".tsv", StringComparison.Ordinal) => "figure_table",
            _ => "other",
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = f.Path, ["analysis"] = analysis, ["role"] = role, ["sha256"] = f.Sha256, ["size_bytes"] = f.Size,
        };
    }

    // --- reading the stage's JSON -------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, object?> Map(object? value) =>
        value as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private static List<object?> List(object? value) => value is IEnumerable<object?> e ? e.ToList() : [];

    private static string? Str(IReadOnlyDictionary<string, object?> map, string key) => map.GetValueOrDefault(key) as string;

    private static long? Long(IReadOnlyDictionary<string, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        long l => l,
        int i => i,
        _ => null,
    };

    private static string? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>A Windows or POSIX path's file name, whatever this machine's separator.</summary>
    private static string PathName(string path) => path.Split('/', '\\')[^1];
}
