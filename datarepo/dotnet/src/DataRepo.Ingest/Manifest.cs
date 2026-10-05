using DataRepo.Bundle;

namespace DataRepo.Ingest;

/// <summary>One dataset in the producing instance's manifest, with the axes D5 makes real columns.</summary>
/// <remarks>Ported from <c>manifest.py</c>. Values keep their PyYAML types (<see cref="PyYaml"/>), because
/// <see cref="ContentDeclaration"/> is hashed into the bundle id.</remarks>
public sealed record DatasetEntry
{
    public required string Accession { get; init; }
    public required string Status { get; init; }
    public required string Run { get; init; }
    public object? Title { get; init; }
    public IReadOnlyDictionary<string, string> Stages { get; init; } = new Dictionary<string, string>();
    public object? SearchResults { get; init; }
    public object? Files { get; init; }
    public object? Organism { get; init; }
    public object? Acquisition { get; init; }
    public object? QuantMethod { get; init; }
    public string Labelling { get; init; } = "none";
    public object? LabellingPlex { get; init; }
    public IReadOnlyList<string> Enrichment { get; init; } = ["none"];
    /// <summary><c>(run base name, enrichment value)</c> pairs, sorted, from <c>run_enrichment: {value: [run, ...]}</c>.</summary>
    public IReadOnlyList<(string Run, string Value)> RunEnrichment { get; init; } = [];
    public bool MixedEnrichment { get; init; }
    public object? Metamorpheus { get; init; }
    public IReadOnlyList<string> PermittedResponses { get; init; } = [];
    public object? ProvenanceSchema { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = [];
    public object? Sdrf { get; init; }
    public object? Reason { get; init; }
    public object? Notes { get; init; }
    /// <summary><c>(run base name, reason)</c> pairs, sorted by run, from <c>excluded_runs: {run: reason}</c> (G85):
    /// runs the producer leaves out of ANALYSIS. They were searched and stay in the bundle.</summary>
    public IReadOnlyList<(string Run, string Reason)> ExcludedRuns { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Raw { get; init; } = new Dictionary<string, object?>();

    /// <summary>Manifest fields that shape what a bundle CONTAINS, so they go into its content hash.</summary>
    /// <remarks>Anything that reaches a written row is an input to the content hash, and nothing else
    /// is: rewording a <c>reason</c> must not move a bundle id (aging 019 section 1). The order and the
    /// reasons are <c>manifest.CONTENT_FIELDS</c>'s.</remarks>
    public static readonly IReadOnlyList<string> ContentFields =
    [
        "accession", "run", "stages", "search_results", "title", "files", "organism", "acquisition",
        "quant_method", "labelling", "labelling_plex", "enrichment", "run_enrichment", "mixed_enrichment",
        "metamorpheus", "permitted_responses",
    ];

    /// <summary>Fields that do NOT go into the content hash, each with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> NonContentFields = new Dictionary<string, string>
    {
        ["status"] = "gates whether a bundle is written at all; every bundle that exists was 'include'",
        ["reason"] = "the producer's prose for a status; never read into a row",
        ["notes"] = "the producer's prose; never read into a row",
        ["flags"] = "shown by `datarepo manifest`; the ingest reads none of them except `mixed_enrichment`, which is lifted into its own content field so the rest stay prose",
        ["provenance_schema"] = "the producer's declared expectation; the ingest reads the schema from provenance.json itself and refuses a version it cannot map",
        ["sdrf"] = "declared but not read -- the SDRF is found under the run folder and hashed as a file",
        ["excluded_runs"] = "the producer's judgement of which runs an ANALYSIS should leave out (G85); every run is still searched, ingested and counted the same, so no row changes, and the catalog takes the field at build (run_exclusions)",
        ["raw"] = "the source row itself, which is the container for every field above",
    };

    public bool Ingestable => Status == "include";

    /// <summary>The manifest's contribution to the bundle's content hash: <see cref="ContentFields"/> only.</summary>
    public Dictionary<string, object?> ContentDeclaration() => new()
    {
        ["accession"] = Accession,
        ["run"] = Run,
        ["stages"] = Stages.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["search_results"] = SearchResults,
        ["title"] = Title,
        ["files"] = Files,
        ["organism"] = Organism,
        ["acquisition"] = Acquisition,
        ["quant_method"] = QuantMethod,
        ["labelling"] = Labelling,
        ["labelling_plex"] = LabellingPlex,
        ["enrichment"] = Enrichment.ToList(),
        ["run_enrichment"] = RunEnrichment.Select(p => new List<object?> { p.Run, p.Value }).ToList(),
        ["mixed_enrichment"] = MixedEnrichment,
        ["metamorpheus"] = Metamorpheus,
        ["permitted_responses"] = PermittedResponses.ToList(),
    };

    /// <summary>A string field's value as Python's <c>str()</c>, or null.</summary>
    public static string? Text(object? value) => value is null ? null : PyFormat.Str(value);
}

/// <summary>A producing instance's whole manifest: the ingest contract (aging 006).</summary>
public sealed record Manifest(
    string Path,
    int ManifestVersion,
    string Instance,
    string WorkRoot,
    string Store,
    object? Licence,
    object? Credit,
    IReadOnlyDictionary<string, DatasetEntry> Datasets)
{
    /// <summary>One dataset, refusing the ones the producer marked unfit.</summary>
    /// <exception cref="ManifestException">The accession is not in the manifest.</exception>
    /// <exception cref="DatasetExcludedException">Its status is not <c>include</c>.</exception>
    public DatasetEntry Dataset(string accession)
    {
        if (!Datasets.TryGetValue(accession, out var entry))
        {
            var known = Datasets.Count == 0 ? "(none)" : string.Join(", ", Datasets.Keys.Order(StringComparer.Ordinal));
            throw new ManifestException($"{accession} is not in {Path}. Datasets listed: {known}");
        }
        if (!entry.Ingestable)
        {
            var reason = (DatasetEntry.Text(entry.Reason) ?? "no reason given").Trim();
            throw new DatasetExcludedException(
                $"{accession} has status '{entry.Status}' in {Path} and will not be loaded. The producer's reason: {reason}");
        }
        return entry;
    }

    public IEnumerable<DatasetEntry> IngestableEntries() => Datasets.Values.Where(e => e.Ingestable);

    public string RunDir(DatasetEntry entry) => System.IO.Path.Combine(WorkRoot, entry.Run);

    /// <summary>Absolute path of one pipeline stage folder, or null if the manifest omits it.</summary>
    public string? StageDir(DatasetEntry entry, string stage) =>
        entry.Stages.TryGetValue(stage, out var rel) ? System.IO.Path.Combine(RunDir(entry), rel) : null;

    public string SearchResultsDir(DatasetEntry entry)
    {
        var rel = DatasetEntry.Text(entry.SearchResults)
            ?? $"{(entry.Stages.TryGetValue("search", out var s) ? s : "")}/mm/Task3SearchTask";
        return System.IO.Path.Combine(RunDir(entry), rel);
    }

    public static readonly IReadOnlySet<int> SupportedVersions = new HashSet<int> { 1 };

    /// <summary>Reads and checks an instance manifest.</summary>
    /// <exception cref="ManifestException">The file is missing, is not a mapping, uses an unknown
    /// <c>manifest_version</c>, or has a dataset entry without the fields an ingest needs.</exception>
    public static Manifest Load(string path)
    {
        if (!File.Exists(path)) throw new ManifestException($"no ingest manifest at {path}");
        object? doc;
        YamlDotNet.RepresentationModel.YamlNode? root;
        try
        {
            root = PyYaml.LoadRoot(path);
            doc = root is null ? null : PyYaml.Plain(root);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ManifestException($"{path} is not valid YAML: {e.Message}");
        }
        if (doc is not Dictionary<string, object?> map)
            throw new ManifestException($"{path} must contain a mapping, found {PyTypeName(doc)}");

        var version = map.GetValueOrDefault("manifest_version");
        if (version is not long v || !SupportedVersions.Contains((int)v))
            throw new ManifestException(
                $"{path} declares manifest_version {PyRepr(version)}; this ingester reads {string.Join(", ", SupportedVersions.Order())}");

        foreach (var required in new[] { "work_root", "store", "datasets" })
            if (map.GetValueOrDefault(required) is null)
                throw new ManifestException($"{path} is missing '{required}'");

        var entries = new Dictionary<string, DatasetEntry>(StringComparer.Ordinal);
        var datasets = map["datasets"] as List<object?> ?? throw new ManifestException($"{path}: datasets must be a list");
        for (var i = 0; i < datasets.Count; i++)
        {
            if (datasets[i] is not Dictionary<string, object?> row)
                throw new ManifestException($"{path}: datasets[{i}] must be a mapping");
            var accessionValue = row.GetValueOrDefault("accession");
            if (accessionValue is null || (accessionValue is string a && a.Length == 0))
                throw new ManifestException($"{path}: datasets[{i}] has no accession");
            var accession = PyFormat.Str(accessionValue);
            var status = row.TryGetValue("status", out var st) ? PyFormat.Str(st ?? "None") : "include";
            var run = row.GetValueOrDefault("run");
            if (status == "include" && (run is null || (run is string r && r.Length == 0)))
                throw new ManifestException($"{path}: {accession} has status '{status}' but no 'run'");
            if (entries.ContainsKey(accession))
                throw new ManifestException($"{path}: {accession} appears twice");
            var flags = AsTuple(row.GetValueOrDefault("flags"), []);
            entries[accession] = new DatasetEntry
            {
                Accession = accession,
                Status = status,
                Run = row.TryGetValue("run", out var runValue) ? PyFormat.Str(runValue ?? "None") : "",
                Title = row.GetValueOrDefault("title"),
                Stages = (row.GetValueOrDefault("stages") as Dictionary<string, object?> ?? [])
                    .ToDictionary(kv => kv.Key, kv => PyFormat.Str(kv.Value ?? "None")),
                SearchResults = row.GetValueOrDefault("search_results"),
                Files = row.GetValueOrDefault("files"),
                Organism = row.GetValueOrDefault("organism"),
                Acquisition = row.GetValueOrDefault("acquisition"),
                QuantMethod = row.GetValueOrDefault("quant_method"),
                Labelling = row.TryGetValue("labelling", out var lab) ? PyFormat.Str(lab ?? "None") : "none",
                LabellingPlex = row.GetValueOrDefault("labelling_plex"),
                Enrichment = AsTuple(row.GetValueOrDefault("enrichment"), ["none"]),
                RunEnrichment = RunEnrichmentPairs(row.GetValueOrDefault("run_enrichment"), $"{path}: {accession}"),
                MixedEnrichment = flags.Contains("mixed_enrichment"),
                Metamorpheus = row.GetValueOrDefault("metamorpheus"),
                PermittedResponses = AsTuple(row.GetValueOrDefault("permitted_responses"), []),
                ProvenanceSchema = row.GetValueOrDefault("provenance_schema"),
                Flags = flags,
                Sdrf = row.GetValueOrDefault("sdrf"),
                Reason = row.GetValueOrDefault("reason"),
                Notes = row.GetValueOrDefault("notes"),
                ExcludedRuns = ExcludedRunPairs(DatasetNode(root, i, "excluded_runs"), $"{path}: {accession}"),
                Raw = row,
            };
        }

        return new Manifest(
            Path: path,
            ManifestVersion: (int)v,
            Instance: map.TryGetValue("instance", out var inst) ? PyFormat.Str(inst ?? "None") : "unknown",
            WorkRoot: Resolve(map["work_root"]!, path),
            Store: Resolve(map["store"]!, path),
            Licence: map.GetValueOrDefault("licence"),
            Credit: map.GetValueOrDefault("credit"),
            Datasets: entries);
    }

    private static IReadOnlyList<string> AsTuple(object? value, IReadOnlyList<string> fallback) => value switch
    {
        null => fallback,
        string s => [s],
        System.Collections.IEnumerable e => e.Cast<object?>().Select(x => PyFormat.Str(x ?? "None")).ToList(),
        _ => [PyFormat.Str(value)],
    };

    /// <summary><c>{value: [run, ...]}</c> to sorted <c>(run, value)</c> pairs, refusing a run named twice.</summary>
    private static IReadOnlyList<(string Run, string Value)> RunEnrichmentPairs(object? value, string where)
    {
        if (value is null) return [];
        if (value is not Dictionary<string, object?> map)
            throw new ManifestException($"{where}: run_enrichment must map an enrichment value to a list of runs");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (enrichment, runs) in map)
        {
            if (runs is not List<object?> list)
                throw new ManifestException(
                    $"{where}: run_enrichment[{PyRepr(enrichment)}] must be a list of run names, found {PyTypeName(runs)}");
            foreach (var runValue in list)
            {
                var name = PyFormat.Str(runValue ?? "None");
                if (seen.TryGetValue(name, out var earlier))
                    throw new ManifestException(
                        $"{where}: run {PyRepr(name)} appears twice in run_enrichment (under {PyRepr(earlier)} and {PyRepr(enrichment)}). Each run has one entry.");
                seen[name] = enrichment;
            }
        }
        return seen.OrderBy(kv => kv.Key, StringComparer.Ordinal).ThenBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The node of one field of <c>datasets[index]</c>, or null when the entry does not write it.</summary>
    private static YamlDotNet.RepresentationModel.YamlNode? DatasetNode(YamlDotNet.RepresentationModel.YamlNode? root, int index, string field)
    {
        if (root is not YamlDotNet.RepresentationModel.YamlMappingNode map) return null;
        var datasets = map.Children.LastOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode { Value: "datasets" }).Value
            as YamlDotNet.RepresentationModel.YamlSequenceNode;
        if (datasets is null || index >= datasets.Children.Count) return null;
        if (datasets.Children[index] is not YamlDotNet.RepresentationModel.YamlMappingNode entry) return null;
        return entry.Children.LastOrDefault(kv => kv.Key is YamlDotNet.RepresentationModel.YamlScalarNode s && s.Value == field).Value;
    }

    /// <summary><c>{run: reason}</c> to <c>(run, reason)</c> pairs sorted by run (G85).</summary>
    /// <remarks>Read from the YAML node, not from <see cref="PyYaml.Plain"/>'s dictionary, because a run name is the
    /// key's TEXT: PyYAML's typing would read <c>2017_03</c> as the integer 201703, which names no run. A run listed
    /// twice never gets here: YamlDotNet refuses a duplicate key in any mapping ("Duplicate key"), so the manifest
    /// is refused as invalid YAML, and two reasons for one run are never silently reduced to one.</remarks>
    private static IReadOnlyList<(string Run, string Reason)> ExcludedRunPairs(YamlDotNet.RepresentationModel.YamlNode? node, string where)
    {
        if (node is null || PyYaml.Plain(node) is null) return [];
        if (node is not YamlDotNet.RepresentationModel.YamlMappingNode map)
            throw new ManifestException(
                $"{where}: excluded_runs must map a run name to the reason it is excluded from analysis, found {PyTypeName(PyYaml.Plain(node))}");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map.Children)
        {
            if (key is not YamlDotNet.RepresentationModel.YamlScalarNode { Value: { Length: > 0 } name } || name.Trim().Length == 0)
                throw new ManifestException($"{where}: excluded_runs has a key that is not a run name ({PyRepr(PyYaml.Plain(key))})");
            var reason = PyYaml.Plain(value);
            if (reason is not string text || text.Trim().Length == 0)
                throw new ManifestException(
                    $"{where}: excluded_runs[{PyRepr(name)}] must be a non-empty reason, found {(reason is string ? "an empty str" : PyTypeName(reason))}. "
                    + "A reader is owed why a searched run is left out of analysis.");
            seen[name] = text;
        }
        return seen.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>A manifest path, with a relative one taken against the manifest's own directory.</summary>
    private static string Resolve(object value, string manifestPath)
    {
        var candidate = PyFormat.Str(value);
        return System.IO.Path.IsPathRooted(candidate)
            ? candidate
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifestPath))!, candidate));
    }

    private static string PyRepr(object? value) => value switch
    {
        null => "None",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        _ => PyFormat.Str(value),
    };

    private static string PyTypeName(object? value) => value switch
    {
        null => "NoneType",
        string => "str",
        long => "int",
        double => "float",
        bool => "bool",
        List<object?> => "list",
        Dictionary<string, object?> => "dict",
        DateOnly => "date",
        _ => value.GetType().Name,
    };
}
