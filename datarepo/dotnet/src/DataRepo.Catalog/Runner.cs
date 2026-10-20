using DataRepo.Bundle;
using DataRepo.Ingest.Sources;

namespace DataRepo.Catalog;

/// <summary>One written engine artefact, as <c>build</c> sees it.</summary>
/// <remarks>Engines that work on stored results (logs' gene resolution first) never run themselves; the
/// instance operator runs them, and each run writes an <b>engine artefact</b> beside the bundles, never inside
/// one (U12):
/// <code>
/// &lt;store&gt;/_engine/&lt;engine&gt;/&lt;artefact id&gt;/
///     run.json              the record: engine, releases, every input with its sha256
///     &lt;table&gt;.parquet       one file per schema table the engine fills
/// </code>
/// Only the part of <c>runner.py</c> the catalog reads is ported here; the runner itself is phase 5.</remarks>
/// <param name="Path">The artefact directory, as <c>str(Path)</c>.</param>
/// <param name="Record">The parsed <c>run.json</c>, as plain values.</param>
public sealed record ArtefactRef(string Path, IReadOnlyDictionary<string, object?> Record)
{
    /// <exception cref="RunnerException">The directory holds no <c>run.json</c>.</exception>
    public static ArtefactRef Load(string path)
    {
        var recordPath = System.IO.Path.Combine(path, Runner.RunRecord);
        if (!File.Exists(recordPath))
            throw new RunnerException($"{SourcesPy.PathStr(path)} holds no {Runner.RunRecord}, so it is not an engine artefact");
        return new ArtefactRef(SourcesPy.PathStr(path), CatalogBuilder.ReadJsonObject(recordPath, msg => new RunnerException(msg)));
    }

    public string Engine => SourcesPy.Str(Record["engine"]);

    public string ArtefactId => SourcesPy.Str(Record["artefact_id"]);

    public string SchemaVersion => SourcesPy.Str(SourcesPy.Get(Record, "schema_version", "?"));

    /// <summary><c>{role: sha256}</c> for every input that reaches a row.</summary>
    public IReadOnlyDictionary<string, string> Inputs => CatalogBuilder.StrMap(SourcesPy.Get(Record, "inputs"), v => SourcesPy.Str(v));

    public IReadOnlyDictionary<string, long> RowCounts => CatalogBuilder.StrMap(SourcesPy.Get(Record, "tables"), CatalogBuilder.PyInt);

    /// <summary>The table's Parquet file, or null when the artefact did not write one.</summary>
    public string? TablePath(string table)
    {
        var path = System.IO.Path.Combine(Path, $"{table}.parquet");
        return File.Exists(path) ? SourcesPy.PathStr(path) : null;
    }
}

/// <summary>The catalog's view of <c>runner.py</c>: where artefacts live and which tables they fill.</summary>
public static class Runner
{
    /// <summary>Where engine artefacts live in the store. The underscore keeps it out of the dataset namespace,
    /// as <c>_study/</c> does: no ProteomeXchange or MassIVE accession starts with one.</summary>
    public const string EngineDir = "_engine";

    public const string RunRecord = "run.json";

    /// <summary>Core tables that only an engine artefact fills, and the engine that fills each. A bundle never
    /// holds one; <c>build</c> loads them from <c>&lt;store&gt;/_engine/</c> instead.</summary>
    public static readonly IReadOnlyDictionary<string, string> EngineTables =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["gene_resolutions"] = "logs.resolve_genes" };

    /// <summary>go's engine: GO annotation of one search's protein groups (charter section 2, G86).</summary>
    public const string GoEngine = "go.annotate_groups";

    /// <summary>Core tables go's artefacts fill. Unlike <see cref="EngineTables"/>, a go artefact annotates ONE
    /// search's protein groups, so its rows belong to that dataset and keep the bundle provenance columns
    /// (<c>dataset_id</c>, <c>bundle_id</c>): stating the dataset is true here, where for logs it would be false.</summary>
    public static readonly IReadOnlyList<string> GoTables = ["protein_localizations", "organelle_term_categories", "annotation_sources"];

    /// <summary>ptmQtl's engines (ptmQtl 025; our 026): site pairs per search, pooled per species, and site-trait fits.</summary>
    public const string PtmQtlSitePairs = "ptmqtl.site_pairs";

    public const string PtmQtlPoolPairs = "ptmqtl.pool_pairs";

    public const string PtmQtlSiteTraits = "ptmqtl.site_traits";

    /// <summary>logs' orthology registration (DATAREPO-75): one verified snapshot, read in place, no rows.</summary>
    public const string OrthologyEngine = "logs.register_orthology";

    /// <summary>The input role of go's protein-group file, and the prefix of each annotation database's role.</summary>
    public const string GoProteinGroupsRole = "protein_groups";

    public const string GoDatabaseRolePrefix = "annotation_database:";

    /// <summary>Every engine artefact in the store (or one engine's), ordered by engine and id.</summary>
    public static List<ArtefactRef> DiscoverArtefacts(string store, string? engine = null)
    {
        var root = System.IO.Path.Combine(store, EngineDir);
        if (!Directory.Exists(root)) return [];
        var engines = engine is not null
            ? [System.IO.Path.Combine(root, engine)]
            : CatalogBuilder.SortedChildren(root).Where(Directory.Exists).ToList();
        var found = new List<ArtefactRef>();
        foreach (var directory in engines)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var child in CatalogBuilder.SortedChildren(directory))
                if (Directory.Exists(child) && File.Exists(System.IO.Path.Combine(child, RunRecord)))
                    found.Add(ArtefactRef.Load(child));
        }
        return found;
    }
}
