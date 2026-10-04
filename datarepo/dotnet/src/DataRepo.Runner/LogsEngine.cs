using System.IO.Compression;
using System.Security.Cryptography;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest.Sources;
using Omics.Modifications;
using Proteomics;
using UsefulProteomicsDatabases;
using UsefulProteomicsDatabases.Ensembl;

namespace DataRepo.Runner;

/// <summary>What the resolver returned for one database, in pyMzLib's <c>proteins.resolve_genes</c> shape.</summary>
public sealed class ResolveResult
{
    public required List<string> ColumnNames { get; init; }
    public required Dictionary<string, List<object?>> Columns { get; init; }
    public long ProteinCount { get; init; }
    public long RecordCount { get; init; }
    public long FailedCount { get; init; }
    public required IReadOnlyDictionary<string, long> OutcomeCounts { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }
}

/// <summary>One searched target database, and the bundles that searched it.</summary>
public sealed record TargetDatabase(string Path, string Sha256, List<(string DatasetId, string BundleId)> Bundles);

/// <summary>What one invocation did, per database.</summary>
public sealed class RunResult
{
    public List<ArtefactRef> Written { get; } = [];
    public List<ArtefactRef> AlreadyDone { get; } = [];
    public List<string> SkippedContaminant { get; init; } = [];
}

/// <summary><c>logs.resolve_genes</c>: protein to Ensembl gene, per searched database (G64, D28 U15).</summary>
/// <remarks>
/// <para>The method is logs' (<c>logs:DEF-GENE-RESOLUTION v1</c>, logs 017); the code is mzLib's
/// <see cref="EnsemblGeneResolver"/>, called directly (the Python called it through pyMzLib's
/// <c>proteins.resolve_genes</c>, whose projection is reproduced here column for column). This computes nothing
/// about genes: it decides WHAT to run on, checks every input, calls the resolver, and refuses rows that do not
/// keep logs' contract.</para>
/// <para><b>The unit is one searched target database</b>, not a bundle: fifteen human datasets search one
/// proteome, so one artefact serves all of them. <b>The contaminant database is never resolved</b> (our logs
/// 018): a contaminant must not be mapped through anything (logs 002 section 0).</para>
/// <para>Inputs by role (U14), each checked before anything runs: <c>gene_set</c> (logs' compact gene table,
/// hashed), <c>xref</c> (Ensembl's UniProt xref, required: without it the run is not v1), <c>logs_manifest</c>
/// (used only to CHECK, so recorded and not hashed), and the searched databases from each bundle's own
/// <c>bundle.json</c>, refused unless the file still hashes to what the bundle recorded. Ported from
/// <c>engines/logs.py</c>.</para>
/// <para>The engine release is <c>{"mzlib": "&lt;NuGet version&gt;"}</c>, where the Python's was pyMzLib's and the
/// bridge's mzLib commit, so a C# artefact's id differs from a Python one's for the same inputs, by design.</para>
/// </remarks>
public static class LogsEngine
{
    public const string Engine = "logs.resolve_genes";
    public const string DefinitionId = "logs:DEF-GENE-RESOLUTION v1";
    public const string Table = "gene_resolutions";
    public static readonly IReadOnlyList<string> Roles = ["gene_set", "xref", "logs_manifest"];

    /// <summary>pyMzLib's bookkeeping columns: which file a row came from. Not stored; search_database_sha256 says it.</summary>
    private static readonly HashSet<string> NotStored = new(StringComparer.Ordinal) { "source_index", "source_path" };

    /// <summary>The resolution table's columns, as the bridge projects them (mzLib's GeneResolutionTsv headers
    /// after the two source columns).</summary>
    public static readonly IReadOnlyList<string> GeneResolutionColumns =
    [
        "source_index", "source_path", "accession", "entry_accession", "isoform", "namespace", "outcome",
        "n_genes", "gene_id", "versioned_gene_id", "gene_symbol", "gene_biotype", "off_primary_genes",
        "uniprot_gene_name", "source", "search_database_sha256", "gene_set_release", "gene_set_sha256",
        "ensembl_xref_agrees", "ensembl_xref_info_type", "ensembl_xref_sha256",
    ];

    private static string S(object? v) => v is null ? "None" : PyFormat.Str(v);

    private static List<string> Organisms(BundleRef reference)
    {
        var path = reference.TablePath("datasets");
        if (path is null) return [];
        var (_, rows) = ArrowTables.ReadParquet(path);
        return rows.SelectMany(r => r.GetValueOrDefault("organisms") as IEnumerable<object?> ?? [])
            .Where(o => o is not null).Select(o => (string)o!).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>logs' manifest entry whose gene set AND xref are the files given, or a refusal.</summary>
    public static IReadOnlyDictionary<string, object?> SpeciesEntry(IReadOnlyDictionary<string, object?> manifest, string geneSetSha, string xrefSha)
    {
        static object? G(object? d, string k) => d is IReadOnlyDictionary<string, object?> m && m.TryGetValue(k, out var v) ? v : null;
        foreach (var e in manifest.GetValueOrDefault("species") as IEnumerable<object?> ?? [])
        {
            var entry = e as IReadOnlyDictionary<string, object?>;
            var inputs = G(entry, "inputs");
            if (!Equals(G(G(inputs, "gene_set"), "sha256"), geneSetSha)) continue;
            var paired = G(G(inputs, "ensembl_uniprot_xref"), "sha256");
            if (!Equals(paired, xrefSha))
                throw new RunnerException(
                    $"the gene set is logs' {S(G(entry, "species"))} file, but the xref (sha256 {xrefSha}) "
                    + $"is not the one logs' manifest pairs with it ({S(paired)}).");
            if (G(entry, "row_values") is not IReadOnlyDictionary<string, object?> rv || rv.Count == 0)
                throw new RunnerException($"logs' manifest entry for {S(G(entry, "species"))} has no row_values");
            return entry!;
        }
        throw new RunnerException(
            $"no species in logs' manifest has a gene set with sha256 {geneSetSha}. Pass the gene set "
            + "file the manifest names, unchanged.");
    }

    /// <summary>The distinct target databases the bundles searched, and the contaminant files left out.</summary>
    /// <exception cref="RunnerException">A bundle of another organism than the gene set's, or one that records no
    /// searched database.</exception>
    public static (List<TargetDatabase> Databases, List<string> Contaminants) TargetDatabases(IReadOnlyList<BundleRef> bundles, string taxon)
    {
        var bySha = new Dictionary<string, TargetDatabase>(StringComparer.Ordinal);
        var contaminants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in bundles)
        {
            var organisms = Organisms(reference);
            if (organisms.Count != 1 || organisms[0] != taxon)
                // Two organisms means two proteomes, and nothing in bundle.json says which database is which species.
                throw new RunnerException(
                    $"{reference.DatasetId} bundle {reference.BundleId} is {(organisms.Count > 0 ? string.Join(", ", organisms) : "of no recorded organism")}, "
                    + $"and the gene set is {taxon}. A gene set only resolves its own species, and a "
                    + "dataset of several organisms is not run until its databases can be told apart.");
            var read = (reference.Manifest.GetValueOrDefault("protein_databases") as IReadOnlyDictionary<string, object?>)?
                .GetValueOrDefault("read") as IEnumerable<object?>;
            var databases = read?.Cast<IReadOnlyDictionary<string, object?>>().ToList() ?? [];
            if (databases.Count == 0)
                throw new RunnerException(
                    $"{reference.DatasetId} bundle {reference.BundleId} records no searched database "
                    + "(`protein_databases.read` in bundle.json), so there is nothing to resolve.");
            foreach (var db in databases)
            {
                var path = S(db["path"]);
                if (ProteinDb.IsContaminantDatabase(path))
                {
                    contaminants.Add(Path.GetFileName(path));
                    continue;
                }
                var sha = S(db["sha256"]);
                if (!bySha.TryGetValue(sha, out var target)) bySha[sha] = target = new TargetDatabase(path, sha, []);
                target.Bundles.Add((reference.DatasetId, reference.BundleId));
            }
        }
        return (bySha.Keys.Order(StringComparer.Ordinal).Select(k => bySha[k]).ToList(), contaminants.ToList());
    }

    /// <summary>The engine release: the mzLib this build references.</summary>
    public static Dictionary<string, string> Release() => new(StringComparer.Ordinal)
    {
        ["mzlib"] = typeof(EnsemblGeneResolver).Assembly.GetName().Version!.ToString(3),
    };

    /// <summary>Resolves one target database with mzLib, projected as pyMzLib's bridge projected it.</summary>
    public static ResolveResult Resolve(string database, string geneSetPath, string xrefPath)
    {
        var geneSet = EnsemblGeneSetReader.Load(geneSetPath);
        var xrefs = EnsemblXrefTable.Load(xrefPath);
        var resolver = new EnsemblGeneResolver(geneSet, xrefs);
        var absolute = Path.GetFullPath(database);
        // Loaded exactly as the bridge loads a database: no decoys, no applied variants, not a contaminant.
        var lower = absolute.ToLowerInvariant();
        List<Protein> proteins = lower.EndsWith(".xml") || lower.EndsWith(".xml.gz")
            ? ProteinDbLoader.LoadProteinXML(absolute, generateTargets: true, DecoyType.None, Array.Empty<Modification>(), false,
                modTypesToExclude: null, out _, maxThreads: 1, maxHeterozygousVariants: 0)
            : ProteinDbLoader.LoadProteinFasta(absolute, generateTargets: true, DecoyType.None, false, out _, maxThreads: 1);
        var sha = DecompressedSha256(absolute);

        var columns = GeneResolutionColumns.ToDictionary(c => c, _ => new List<object?>(), StringComparer.Ordinal);
        var outcomeCounts = Enum.GetValues<GeneResolutionOutcome>().ToDictionary(o => GeneResolutionTsv.OutcomeName(o), _ => 0L, StringComparer.Ordinal);
        long proteinCount = 0, rowCount = 0;
        foreach (var protein in proteins)
        {
            var rows = resolver.Resolve(protein, sha);
            proteinCount++;
            outcomeCounts[GeneResolutionTsv.OutcomeName(rows[0].Outcome)]++;
            foreach (var row in rows)
            {
                rowCount++;
                object?[] cells =
                [
                    0L, absolute, row.Accession, row.EntryAccession, row.Isoform, row.Namespace.ToString().ToLowerInvariant(),
                    GeneResolutionTsv.OutcomeName(row.Outcome), (long)row.GeneCount, row.GeneId, row.VersionedGeneId,
                    row.GeneSymbol, row.GeneBiotype, row.OffPrimaryGenes, row.UniProtGeneName, row.Source,
                    row.SearchDatabaseSha256, row.GeneSetRelease, row.GeneSetSha256, row.EnsemblXrefAgrees,
                    row.EnsemblXrefInfoType, row.EnsemblXrefSha256,
                ];
                for (var c = 0; c < cells.Length; c++) columns[GeneResolutionColumns[c]].Add(Wire(cells[c]));
            }
        }
        var caveats = new List<string>();
        if (geneSet.Release is null)
            caveats.Add(
                $"The gene set's file name '{geneSet.SourceFileName}' carries no Ensembl release number, so "
                + "gene_set_release is null. Keep Ensembl's own file name (Species.Assembly.Release.gtf.gz) so the "
                + "release travels with every row; the sha256 still pins the exact file.");
        if (geneSet.GenomeBuild is null)
            caveats.Add("The gene set has no '#!genome-build' header, so the assembly it covers is not recorded.");
        return new ResolveResult
        {
            ColumnNames = GeneResolutionColumns.ToList(),
            Columns = columns,
            ProteinCount = proteinCount,
            RecordCount = rowCount,
            FailedCount = 0,
            OutcomeCounts = outcomeCounts,
            Caveats = caveats,
        };
    }

    /// <summary>A cell as the bridge's JSON carried it: a sequence <c>;</c>-joined, an enum by name, a whole double as an integer.</summary>
    private static object? Wire(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        double d when double.IsNaN(d) || double.IsInfinity(d) => null,
        double d when d == Math.Floor(d) && Math.Abs(d) < 9e15 => (long)d,
        Enum e => e.ToString(),
        System.Collections.IEnumerable e => string.Join(";", e.Cast<object?>().Select(x => x?.ToString() ?? "")),
        int i => (long)i,
        _ => value,
    };

    /// <summary>Lower-case hex sha256 of the database's DECOMPRESSED bytes, which the resolver keys rows on.</summary>
    public static string DecompressedSha256(string path)
    {
        using var file = File.OpenRead(path);
        using var content = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>The resolver's table as <c>gene_resolutions</c> rows, after logs' acceptance checks (RUNNER.md).</summary>
    /// <exception cref="RunnerException">A failed read, a row for another database, a row whose gene set or xref
    /// differ from logs' manifest (logs 016 section 1), an outcome outside mzLib's six, or no rows at all.</exception>
    public static List<IReadOnlyDictionary<string, object?>> RowsFrom(ResolveResult result, string databaseSha, IReadOnlyDictionary<string, object?> rowValues)
    {
        if (result.FailedCount > 0) throw new RunnerException($"the resolver failed to read {result.FailedCount} database file(s)");
        var names = result.ColumnNames;
        var n = names.Count > 0 ? result.Columns[names[0]].Count : 0;
        if (n == 0) throw new RunnerException("the resolver returned no rows for the database");
        var outcomes = SchemaDocs.Enums["GeneResolutionOutcome"].Values.ToHashSet(StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gene_set_sha256"] = S(rowValues["gene_set_sha256"]),
            ["ensembl_xref_sha256"] = S(rowValues["ensembl_xref_sha256"]),
            ["gene_set_release"] = S(rowValues["gene_set_release"]),
        };
        var stored = names.Where(c => !NotStored.Contains(c)).ToList();
        var known = Tables.ByName[Table].Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = stored.Where(c => !known.Contains(c)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new RunnerException(
                $"the resolver writes column(s) [{string.Join(", ", unknown.Select(u => $"'{u}'"))}] that `{Table}` has no place for. A newer "
                + "mzLib changed the contract; the schema has to take it before the runner does.");
        var rows = new List<IReadOnlyDictionary<string, object?>>(n);
        for (var i = 0; i < n; i++)
        {
            var row = new Row();
            foreach (var c in stored) row[c] = result.Columns[c][i];
            var where = $"row {i} ({S(row.GetValueOrDefault("accession"))}, {S(row.GetValueOrDefault("gene_id"))})";
            if (!Equals(row.GetValueOrDefault("search_database_sha256"), databaseSha))
                throw new RunnerException($"{where}: search_database_sha256 {S(row.GetValueOrDefault("search_database_sha256"))} is not the database resolved");
            foreach (var (key, value) in expected)
            {
                var got = row.GetValueOrDefault(key);
                if ((got is null or "" ? "" : PyFormat.Str(got)) != value)
                    throw new RunnerException($"{where}: {key} {PyFormat.Repr(got)} differs from logs' manifest {PyFormat.Repr(value)}");
            }
            if (row.GetValueOrDefault("outcome") is not string outcome || !outcomes.Contains(outcome))
                throw new RunnerException($"{where}: outcome {PyFormat.Repr(row.GetValueOrDefault("outcome"))} is not one of mzLib's [{string.Join(", ", outcomes.Order(StringComparer.Ordinal).Select(o => $"'{o}'"))}]");
            row["definition_id"] = DefinitionId;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Resolves every target database the bundles searched, one artefact per database.</summary>
    /// <param name="install">The datarepo install record; <see cref="EngineRunner.InstallIdentity"/> when null. Tests pass one.</param>
    /// <param name="release">The engine release; <see cref="Release"/> when null. Tests pass one.</param>
    /// <param name="resolve">The resolver; <see cref="Resolve"/> when null. Tests pass a stand-in.</param>
    /// <exception cref="RunnerException">A missing or mismatched input, a development datarepo, or rows that fail
    /// acceptance. Databases already resolved stay resolved: each artefact is written whole or not at all.</exception>
    public static RunResult Run(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null,
        Func<string, string, string, ResolveResult>? resolve = null)
    {
        var missing = Roles.Where(r => !inputs.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            var extra = missing.Contains("xref") ? " Without `xref`, the rows are not logs:DEF-GENE-RESOLUTION v1 (logs 017)." : "";
            throw new RunnerException($"{Engine} needs --input {string.Join("=<path>, --input ", missing)}=<path>.{extra}");
        }
        var unknown = inputs.Keys.Where(k => !Roles.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new RunnerException($"{Engine} takes inputs {string.Join(", ", Roles)}; not {string.Join(", ", unknown)}");
        foreach (var (role, path) in inputs)
            if (!File.Exists(path)) throw new RunnerException($"--input {role}={path}: no such file");
        if (bundles.Count == 0) throw new RunnerException($"{Engine} needs at least one bundle to run on behalf of");

        install ??= EngineRunner.InstallIdentity();
        resolve ??= Resolve;
        var geneSetSha = BundleWriter.Sha256File(inputs["gene_set"]);
        var xrefSha = BundleWriter.Sha256File(inputs["xref"]);
        var manifestPath = inputs["logs_manifest"];
        var manifest = CatalogBuilder.ReadJsonObject(manifestPath, msg => new RunnerException(msg));
        var entry = SpeciesEntry(manifest, geneSetSha, xrefSha);
        var taxon = $"NCBITaxon:{S(entry["ncbi_taxonomy_id"])}";
        var (databases, contaminants) = TargetDatabases(bundles, taxon);
        release ??= Release();

        var result = new RunResult { SkippedContaminant = contaminants };
        foreach (var db in databases)
        {
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["search_database"] = db.Sha256, ["gene_set"] = geneSetSha, ["xref"] = xrefSha,
            };
            var aid = EngineRunner.ArtefactId(Engine, release, hashed, DefinitionId);
            var existing = EngineRunner.ArtefactDir(store, Engine, aid);
            if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            if (!File.Exists(db.Path))
                throw new RunnerException(
                    $"{db.Path} (searched by {string.Join(", ", db.Bundles.Select(b => b.DatasetId))}) is not on disk. The "
                    + "runner fetches nothing: restore the file the bundle recorded.");
            var actual = BundleWriter.Sha256File(db.Path);
            if (actual != db.Sha256)
                throw new RunnerException(
                    $"{db.Path} now hashes to {actual}, and the bundles recorded {db.Sha256}. It is not "
                    + "the database they searched, so resolving it would describe some other search.");
            var verb = resolve(db.Path, inputs["gene_set"], inputs["xref"]);
            var rows = RowsFrom(verb, db.Sha256, (IReadOnlyDictionary<string, object?>)entry["row_values"]!);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = DefinitionId,
                ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["input_files"] = new Dictionary<string, object?>
                {
                    ["search_database"] = BundleWriter.PathText(db.Path),
                    ["gene_set"] = BundleWriter.PathText(inputs["gene_set"]),
                    ["xref"] = BundleWriter.PathText(inputs["xref"]),
                },
                ["checked_against"] = new Dictionary<string, object?>
                {
                    ["logs_manifest"] = BundleWriter.PathText(manifestPath),
                    ["logs_manifest_sha256"] = BundleWriter.Sha256File(manifestPath),
                    ["species"] = entry.GetValueOrDefault("species"),
                    ["row_values"] = entry["row_values"],
                },
                ["datarepo_install"] = install,
                ["requested_for"] = db.Bundles.Select(b => (object?)new Dictionary<string, object?> { ["dataset_id"] = b.DatasetId, ["bundle_id"] = b.BundleId }).ToList(),
                ["engine_summary"] = new Dictionary<string, object?>
                {
                    ["protein_count"] = verb.ProteinCount,
                    ["record_count"] = verb.RecordCount,
                    ["outcome_counts"] = verb.OutcomeCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["caveats"] = verb.Caveats.Cast<object?>().ToList(),
                },
                ["acceptance"] = "passed",
            };
            result.Written.Add(EngineRunner.WriteArtefact(store, Engine, aid, record,
                new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { [Table] = rows }));
        }
        return result;
    }
}
