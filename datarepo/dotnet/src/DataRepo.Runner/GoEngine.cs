using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest.Sources;
using MzLibUtil;
using Readers;
using UsefulProteomicsDatabases.GeneOntology;

namespace DataRepo.Runner;

/// <summary>One searched database a go run reads, from the bundle's own <c>sources</c>.</summary>
public sealed record GoDatabase(string Name, string Path, string Sha256);

/// <summary>What one bundle gives a go run: its protein-group file and its searched databases, by role.</summary>
/// <param name="Targets">The target databases, annotated with (the annotation database).</param>
/// <param name="Contaminants">The contaminant panels the search also read, which are NOT annotation input.</param>
public sealed record GoBundleInputs(
    BundleRef Bundle, string ProteinGroupsPath, string ProteinGroupsSha256, List<GoDatabase> Targets, List<GoDatabase> Contaminants);

/// <summary><c>go.annotate_groups</c>: GO annotation of one search's protein groups (charter section 2, G86).</summary>
/// <remarks>
/// <para>The method is go's (<c>go/design/PLAN.md</c> S5-S6, rulings D1-D39); the code is what mzLib 1.0.593
/// ships, called directly: <see cref="ProteinGroupFromTsvExtensions.ToGoAnnotationGroups"/>,
/// <see cref="GoGroupAnnotator"/>, <see cref="GoCategoryResolver"/>, and go's own writers
/// <see cref="GoAnnotationTsv"/> and <see cref="GoCategoryTsv"/>. This computes nothing about GO: it decides what
/// to run on, checks every input, runs mzLib, reads go's two files back with <see cref="Go"/>'s reader as the
/// acceptance check (released writer, header recount, coverage, the entrapment refusal of go 018/020), and stores
/// the rows that reader builds.</para>
/// <para><b>The unit is one bundle</b>, unlike logs: go annotates one search's protein groups, so one artefact per
/// search. Its id is keyed on inputs, never on the bundle id, so a re-ingest that reads the same files is
/// "already done".</para>
/// <para>Inputs by role, never by guess, each hashed into the artefact id: <c>protein_groups</c> (the bundle's
/// <c>protein_group_quant</c> source), <c>annotation_database:&lt;file&gt;</c> (each TARGET database the search
/// read), from the bundle's own <c>bundle.json</c> and refused unless the file still hashes to what the bundle
/// recorded; and <c>ontology</c> (go.obo) and <c>category_map</c> (the consumer's map, aging's for aging) from
/// <c>--input</c>.</para>
/// <para><b>Contaminant panels are not annotation input.</b> go's own pre-release run (go 010 section 1) annotated
/// from the searched target XML alone, and <c>annotation_db_sha256</c> names one database. go's status precedence
/// (go 012 section 2, aging 011 section 2) was ruled on the premise that a contaminant has no annotation entry, so
/// that a trypsin or bovine albumin group reads <c>contaminant</c>; MetaMorpheus's contaminant XML carries its own
/// GO (3,557 GO references on 264 entries in aging's copy), so passing it would turn those groups into
/// <c>annotated</c> rows of porcine and bovine terms, and <c>protein_localizations</c> carries no contaminant mark
/// to tell them apart (logs 002 section 0: the contaminant flag must travel with the accession). A human keratin
/// that is also a target entry is still annotated, from its target entry.</para>
/// <para><b>Unknown GO ids are skipped and recorded</b> (<see cref="SkipUnknownGoIds"/>, go D35): a UniProt
/// release newer than the pinned go.obo cites ids the release lacks, and refusing would cost a whole dataset for
/// one term. mzLib's writer then writes <c>#!unresolved_go_ids N</c>, and <c>run.json</c> lists the ids.</para>
/// <para><b>No definition id yet.</b> go has published none (charter S4; asked as GO-D4, our go 021), and dataRepo
/// never defines another project's number (D24), so <see cref="DefinitionId"/> is null and <see cref="Run"/>
/// refuses without one. When go publishes, set <see cref="DefinitionId"/>; that one line enables the CLI.</para>
/// </remarks>
public static class GoEngine
{
    public const string Engine = DataRepo.Catalog.Runner.GoEngine;

    /// <summary>go's definition id for these rows: NONE PUBLISHED (charter S4, our go 021 GO-D4).</summary>
    /// <remarks>Enabling the engine is setting this to the id go publishes, e.g. <c>"go:DEF-... v1"</c>.</remarks>
    public const string? DefinitionId = null;

    /// <summary>The roles the operator supplies; the protein groups and databases come from the bundle.</summary>
    public static readonly IReadOnlyList<string> Roles = ["ontology", "category_map"];

    /// <summary>go D35: drop a GO id the pinned go.obo lacks, and say so, rather than refuse the dataset.</summary>
    public const bool SkipUnknownGoIds = true;

    /// <summary>go's two files, kept in the artefact in go's own format.</summary>
    public const string AnnotationFile = "go_annotation.tsv";

    public const string CategoryFile = "go_category.tsv";

    private static string S(object? v) => v is null ? "None" : PyFormat.Str(v);

    /// <summary>The engine release: the mzLib this build references.</summary>
    public static Dictionary<string, string> Release() => new(StringComparer.Ordinal)
    {
        ["mzlib"] = typeof(GoGroupAnnotator).Assembly.GetName().Version!.ToString(3),
    };

    /// <summary>The bundle's protein-group file and searched databases, from its <c>sources</c>.</summary>
    /// <exception cref="RunnerException">No protein-group source, no target database, or two target databases
    /// with one file name.</exception>
    public static GoBundleInputs InputsOf(BundleRef bundle)
    {
        string? groupsPath = null, groupsSha = null;
        var targets = new List<GoDatabase>();
        var contaminants = new List<GoDatabase>();
        foreach (var item in bundle.Manifest.GetValueOrDefault("sources") as IEnumerable<object?> ?? [])
        {
            if (item is not IReadOnlyDictionary<string, object?> source) continue;
            var role = S(source.GetValueOrDefault("role"));
            if (role == "protein_group_quant")
            {
                groupsPath = S(source.GetValueOrDefault("path"));
                groupsSha = S(source.GetValueOrDefault("sha256"));
            }
            else if (role.StartsWith("protein_database:", StringComparison.Ordinal))
            {
                var path = S(source.GetValueOrDefault("path"));
                var db = new GoDatabase(role["protein_database:".Length..], path, S(source.GetValueOrDefault("sha256")));
                (ProteinDb.IsContaminantDatabase(path) ? contaminants : targets).Add(db);
            }
        }
        var where = $"{bundle.DatasetId} bundle {bundle.BundleId}";
        if (groupsPath is null || groupsSha is null)
            throw new RunnerException($"{where} records no `protein_group_quant` source in bundle.json, so there are no groups to annotate.");
        if (targets.Count == 0)
            throw new RunnerException($"{where} records no searched target database (`protein_database:*` in bundle.json's sources), so there is nothing to annotate from.");
        var repeated = targets.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (repeated.Count > 0)
            throw new RunnerException($"{where} searched two target databases named {string.Join(", ", repeated)}; their roles would collide.");
        return new GoBundleInputs(bundle, groupsPath, groupsSha,
            targets.OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
            contaminants.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>The <c>annotation_db_sha256</c> go's rows carry: the database's own sha256 when there is one, and
    /// otherwise a sha256 over the sorted sha256s of all of them (one per line, after a version line).</summary>
    /// <remarks>mzLib's annotator stamps ONE database hash on every row. A search that read a proteome and an
    /// isoform database was annotated from both, so the value names both; <c>run.json</c> lists each file.</remarks>
    public static string AnnotationDbSha256(IReadOnlyList<GoDatabase> targets)
    {
        if (targets.Count == 1) return targets[0].Sha256;
        var text = "annotation-databases/1\n" + string.Concat(targets.Select(t => t.Sha256).Order(StringComparer.Ordinal).Select(s => s + "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary><c>{role: sha256}</c> for everything that reaches a row.</summary>
    public static Dictionary<string, string> HashedInputs(GoBundleInputs bundle, string ontologySha, string mapSha)
    {
        var hashed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DataRepo.Catalog.Runner.GoProteinGroupsRole] = bundle.ProteinGroupsSha256,
            ["ontology"] = ontologySha,
            ["category_map"] = mapSha,
        };
        foreach (var t in bundle.Targets) hashed[DataRepo.Catalog.Runner.GoDatabaseRolePrefix + t.Name] = t.Sha256;
        return hashed;
    }

    private static void RequireRecorded(string path, string recorded, string what, BundleRef bundle)
    {
        if (!File.Exists(path))
            throw new RunnerException(
                $"{path} ({what} of {bundle.DatasetId} bundle {bundle.BundleId}) is not on disk. The runner fetches "
                + "nothing: restore the file the bundle recorded.");
        var actual = BundleWriter.Sha256File(path);
        if (actual != recorded)
            throw new RunnerException(
                $"{path} now hashes to {actual}, and {bundle.DatasetId} bundle {bundle.BundleId} recorded {recorded}. It is "
                + "not the file the search read, so annotating it would describe some other search.");
    }

    /// <summary>The ontology, map and resolver one run shares across bundles.</summary>
    private sealed class Loaded(GeneOntologyGraph ontology, GoCategoryMap map, GoCategoryResolver resolver)
    {
        public GeneOntologyGraph Ontology { get; } = ontology;
        public GoCategoryMap Map { get; } = map;
        public GoCategoryResolver Resolver { get; } = resolver;
    }

    private static Loaded Load(string ontologyPath, string mapPath)
    {
        try
        {
            var ontology = GeneOntologyGraph.Load(ontologyPath);
            var map = GoCategoryMap.Load(mapPath);
            return new Loaded(ontology, map, new GoCategoryResolver(map, ontology));
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"mzLib refused an input: {e.Message}");
        }
    }

    /// <summary>Annotates every bundle not already annotated with these inputs, one artefact per bundle.</summary>
    /// <param name="definitionId">go's definition id for the rows. <see cref="DefinitionId"/> in production, which
    /// is null until go publishes one, so this refuses; tests pass an obviously fake id.</param>
    /// <param name="install">The datarepo install record; <see cref="EngineRunner.InstallIdentity"/> when null. Tests pass one.</param>
    /// <param name="release">The engine release; <see cref="Release"/> when null.</param>
    /// <exception cref="RunnerException">No definition id, a missing or mismatched input, a development datarepo,
    /// or files that fail acceptance. Bundles already annotated stay annotated: each artefact is written whole or
    /// not at all.</exception>
    public static RunResult Run(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        string? definitionId, IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        if (string.IsNullOrEmpty(definitionId))
            throw new RunnerException(
                $"{Engine}: go has published no definition id for these rows (charter S4; asked as GO-D4 in "
                + "dataRepo's go 021). dataRepo never defines another project's number (D24), and an artefact "
                + "without one could not be cited, so the engine does not run until go publishes one.");
        var missing = Roles.Where(r => !inputs.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new RunnerException($"{Engine} needs --input {string.Join("=<path>, --input ", missing)}=<path>.");
        var unknown = inputs.Keys.Where(k => !Roles.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new RunnerException(
                $"{Engine} takes inputs {string.Join(", ", Roles)}; not {string.Join(", ", unknown)}. The protein groups "
                + "and the searched databases come from each bundle's own record.");
        foreach (var (role, path) in inputs)
            if (!File.Exists(path)) throw new RunnerException($"--input {role}={path}: no such file");
        if (bundles.Count == 0) throw new RunnerException($"{Engine} needs at least one bundle to run on");

        install ??= EngineRunner.InstallIdentity();
        release ??= Release();
        var ontologySha = BundleWriter.Sha256File(inputs["ontology"]);
        var mapSha = BundleWriter.Sha256File(inputs["category_map"]);
        Loaded? loaded = null;

        var result = new RunResult();
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in bundles)
        {
            var bundle = InputsOf(reference);
            foreach (var c in bundle.Contaminants) skipped.Add(c.Name);
            var hashed = HashedInputs(bundle, ontologySha, mapSha);
            var aid = EngineRunner.ArtefactId(Engine, release, hashed, definitionId);
            var existing = EngineRunner.ArtefactDir(store, Engine, aid);
            if (File.Exists(Path.Combine(existing, DataRepo.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            RequireRecorded(bundle.ProteinGroupsPath, bundle.ProteinGroupsSha256, "the protein-group file", reference);
            foreach (var t in bundle.Targets) RequireRecorded(t.Path, t.Sha256, "a searched database", reference);
            loaded ??= Load(inputs["ontology"], inputs["category_map"]);
            if (loaded.Ontology.SourceSha256 != ontologySha || loaded.Map.SourceSha256 != mapSha)
                throw new RunnerException("an input changed on disk while the run was reading it");
            result.Written.Add(Annotate(store, bundle, loaded, inputs, hashed, aid, definitionId, release, install));
        }
        result.SkippedContaminant.AddRange(skipped);
        return result;
    }

    /// <summary>One bundle: groups, annotator, go's two files, acceptance, rows, artefact.</summary>
    private static ArtefactRef Annotate(string store, GoBundleInputs bundle, Loaded loaded, IReadOnlyDictionary<string, string> inputs,
        Dictionary<string, string> hashed, string aid, string definitionId, IReadOnlyDictionary<string, string> release,
        IReadOnlyDictionary<string, object?> install)
    {
        var where = $"{bundle.Bundle.DatasetId} bundle {bundle.Bundle.BundleId}";
        List<GoAnnotationGroup> groups;
        try
        {
            var file = new ProteinGroupFromTsvFile(bundle.ProteinGroupsPath);
            file.LoadResults();
            groups = file.Results.ToGoAnnotationGroups().ToList();
        }
        catch (Exception e) when (e is MzLibException or InvalidDataException)
        {
            throw new RunnerException($"{where}: mzLib could not read the protein groups {bundle.ProteinGroupsPath}: {e.Message}");
        }
        if (groups.Count == 0) throw new RunnerException($"{where}: the protein-group file holds no non-decoy group");

        var dbSha = AnnotationDbSha256(bundle.Targets);
        // Loaded as the rest of the C# loads a searched database (Specificity, the bridge's Proteins.Load): no
        // decoys, no sequence variants applied (the annotator inherits a variant's terms itself, go D34), and not
        // a contaminant, since only target databases are passed.
        var proteins = Specificity.LoadProteins(bundle.Targets.Select(t => (t.Path, t.Sha256, false)));
        var annotator = new GoGroupAnnotator(loaded.Ontology, proteins, dbSha, skipUnknownGoIds: SkipUnknownGoIds);
        IReadOnlyList<GoAnnotationRow> rows;
        try
        {
            rows = annotator.AnnotateAll(groups);
        }
        catch (ArgumentException e)
        {
            throw new RunnerException($"{where}: mzLib refused a group: {e.Message}");
        }

        var work = Path.Combine(Path.GetTempPath(), "datarepo-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var annotationPath = Path.Combine(work, AnnotationFile);
            var categoryPath = Path.Combine(work, CategoryFile);
            var utf8 = new UTF8Encoding(false);
            using (var writer = new StreamWriter(annotationPath, false, utf8))
                GoAnnotationTsv.Write(writer, rows, loaded.Ontology, dbSha, bundle.ProteinGroupsSha256, annotator.UnresolvedGoIds);
            using (var writer = new StreamWriter(categoryPath, false, utf8))
                GoCategoryTsv.Write(writer, loaded.Resolver, rows);

            // Acceptance (RUNNER.md): go's files read back through the reader built for go's contract.
            GoAnnotation annotation;
            GoCategories categories;
            try
            {
                annotation = Go.ReadAnnotation(annotationPath);
                categories = Go.ReadCategories(categoryPath);
                Go.CheckCoverage(annotation, categories);
            }
            catch (IngestException e)
            {
                throw new RunnerException($"{where}: go's files failed acceptance, so nothing was written: {e.Message}");
            }
            var pins = new (string Key, string? Got, string Want)[]
            {
                ("annotation_db_sha256", annotation.Header.GetValueOrDefault("annotation_db_sha256"), dbSha),
                ("go_obo_sha256", annotation.Header.GetValueOrDefault("go_obo_sha256"), hashed["ontology"]),
                ("source_file_sha256", annotation.Header.GetValueOrDefault("source_file_sha256"), bundle.ProteinGroupsSha256),
                ("category_map sha256", categories.MapSha256, hashed["category_map"]),
            };
            foreach (var (key, got, want) in pins)
                if (got != want)
                    throw new RunnerException($"{where}: go's file says {key} {S(got)}, and the input was {want}");

            var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["protein_localizations"] = Go.LocalizationRows(annotation).Cast<IReadOnlyDictionary<string, object?>>().ToList(),
                ["organelle_term_categories"] = Go.CategoryRows(categories, annotation).Cast<IReadOnlyDictionary<string, object?>>().ToList(),
                ["annotation_sources"] = [Go.SourceRow(annotation)],
            };
            var statuses = Go.Statuses.ToDictionary(s => s, _ => (object?)0L, StringComparer.Ordinal);
            foreach (var first in rows.GroupBy(r => r.ProteinGroup, StringComparer.Ordinal).Select(g => g.First()))
            {
                var name = GoAnnotationTsv.StatusName(first.Status);
                statuses[name] = (long)statuses[name]! + 1;
            }
            var inputFiles = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [DataRepo.Catalog.Runner.GoProteinGroupsRole] = BundleWriter.PathText(bundle.ProteinGroupsPath),
                ["ontology"] = BundleWriter.PathText(inputs["ontology"]),
                ["category_map"] = BundleWriter.PathText(inputs["category_map"]),
            };
            foreach (var t in bundle.Targets) inputFiles[DataRepo.Catalog.Runner.GoDatabaseRolePrefix + t.Name] = BundleWriter.PathText(t.Path);
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = definitionId,
                ["release"] = release.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["inputs"] = hashed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["input_files"] = inputFiles,
                ["datarepo_install"] = install,
                ["requested_for"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["dataset_id"] = bundle.Bundle.DatasetId, ["bundle_id"] = bundle.Bundle.BundleId },
                },
                ["engine_summary"] = new Dictionary<string, object?>
                {
                    ["groups"] = (long)groups.Count,
                    ["annotation_rows"] = (long)rows.Count,
                    ["group_status_counts"] = statuses,
                    ["header_counters"] = new[] { "counter_q_value_max", "n_multi_member_groups" }
                        .Concat(Go.Statuses.Select(s => $"status_{s}"))
                        .ToDictionary(k => k, k => (object?)annotation.Header[k]),
                    ["go_release"] = annotation.GoRelease,
                    ["mzlib_release"] = annotation.MzlibRelease,
                    ["annotation_db_sha256"] = dbSha,
                    ["annotation_databases"] = bundle.Targets.Select(t => (object?)new Dictionary<string, object?> { ["name"] = t.Name, ["sha256"] = t.Sha256 }).ToList(),
                    ["contaminant_databases_not_annotated"] = bundle.Contaminants.Select(c => (object?)c.Name).ToList(),
                    ["category_map"] = new Dictionary<string, object?>
                    {
                        ["name"] = categories.MapName, ["version"] = categories.MapVersion, ["sha256"] = categories.MapSha256,
                    },
                    ["skip_unknown_go_ids"] = SkipUnknownGoIds,
                    ["unresolved_go_ids"] = annotator.UnresolvedGoIds.Cast<object?>().ToList(),
                    ["not_stored"] = annotation.NotStored.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["annotation_file_sha256"] = annotation.Sha256,
                    ["category_file_sha256"] = categories.Sha256,
                },
                ["acceptance"] = "passed",
            };
            return EngineRunner.WriteArtefact(store, Engine, aid, record, tables,
                new Dictionary<string, string> { [AnnotationFile] = annotationPath, [CategoryFile] = categoryPath });
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
