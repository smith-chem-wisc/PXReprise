using System.Text;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest.Sources;
using MzLibUtil;
using Proteomics;
using Readers;
using UsefulProteomicsDatabases.Annotation;
using UsefulProteomicsDatabases.GeneOntology;

namespace PXReprise.Runner;

/// <summary>go's three engines: GO stored once per database file and per release, never per bundle, and joined by the
/// catalog (catalog format 17; go 012 P1-P4, dataRepo 028; mzLib #1497, released in 1.0.597).</summary>
/// <remarks>
/// <para>They replace <c>go.annotate_groups</c>, which wrote every search's group-by-term rows, so a database searched by
/// 34 deposits had its terms restated 34 times. Each engine computes nothing about GO: it decides what to run on, checks
/// every input against what the bundles recorded, runs mzLib, reads mzLib's own file back with mzLib's own reader as the
/// acceptance check, and stores the rows that reader gives, keeping the file beside them so a reader can re-check it.</para>
/// <list type="bullet">
/// <item><see cref="AnnotateDatabases"/> (a): one database FILE's direct GO terms per entry (<c>GoAccessionAnnotation</c>),
/// one artefact per (file, go.obo, mzLib, definition), shared by every search that read the file. Each row also carries
/// <c>isoform_of</c> (mzLib's <see cref="ProteinAccession"/> parser: a UniProt isoform's entry), so the catalog can give an
/// isoform with no GO of its own its entry's terms when the two are in different files, as the annotator over all a
/// search's files does (D37).</item>
/// <item><see cref="CategorizeTerms"/> (b) and the ontology: one go.obo's terms and is_a/part_of closure
/// (<c>GoOntologyTsv</c>) and one consumer map's categories over every term (<c>GoCategoryTsv</c>), one artefact per
/// (go.obo, map). It takes no bundle.</item>
/// <item><see cref="ResolveMembers"/> (c): which database entry each member of one search's protein groups takes
/// (<c>MemberResolver</c>, over ALL the search's target databases together), one artefact per (protein-group file, target
/// databases). It holds no GO.</item>
/// </list>
/// <para>Inputs by role, each hashed into the artefact id; the databases and the protein-group file come from each bundle's
/// own <c>bundle.json</c> and are refused unless they still hash to what the bundle recorded. Contaminant panels are never
/// annotation input (GO-D5): <c>GoEngine</c>'s reasoning, unchanged.</para>
/// <para><b>Definitions are go's</b> (D24: dataRepo never defines another project's number), and each run refuses a call
/// that passes none. (a) is <see cref="AccessionDefinitionId"/>. go's DEFINITIONS.md gives the ontology tables and member
/// resolution no id of their own: (b)'s rule is stated in <c>go:DEF-GROUP-GO-ANNOTATION v1</c> ("A row of the category
/// table"), so (b) runs under it; (c) runs under <see cref="AccessionDefinitionId"/>, the per-database definition it was
/// published beside.</para>
/// <para><b>Unknown GO ids are skipped and recorded</b> (go D35), as <c>go.annotate_groups</c> skipped them.</para>
/// </remarks>
public static class GoReferenceEngines
{
    public const string AnnotateDatabases = PXReprise.Catalog.Runner.GoAnnotateDatabases;
    public const string CategorizeTerms = PXReprise.Catalog.Runner.GoCategorizeTerms;
    public const string ResolveMembers = PXReprise.Catalog.Runner.GoResolveMembers;

    /// <summary>go's definition of a database entry's direct GO terms (go's DEFINITIONS.md, mzLib #1497).</summary>
    public const string AccessionDefinitionId = "go:DEF-ACCESSION-GO-ANNOTATION v1";

    /// <summary>go's definition whose text states the category table's rule (D14, D28): <see cref="CategorizeTerms"/>'s.</summary>
    public const string CategoryDefinitionId = "go:DEF-GROUP-GO-ANNOTATION v1";

    /// <summary>The definition <see cref="ResolveMembers"/> runs under. go names none for member resolution; it was
    /// published beside, and serves, the per-database definition.</summary>
    public const string MemberDefinitionId = AccessionDefinitionId;

    /// <summary>go D35: drop a GO id the pinned go.obo lacks, and say so, rather than refuse the database.</summary>
    public const bool SkipUnknownGoIds = true;

    /// <summary>mzLib's files, kept in each artefact beside the Parquet the catalog reads.</summary>
    public const string AccessionTermsFile = "go_accession_terms.tsv";
    public const string TermsFile = "go_terms.tsv";
    public const string ClosureFile = "go_closure.tsv";
    public const string CategoryFile = "go_category.tsv";
    public const string MemberFile = "member_resolution.tsv";

    private static ColumnSpec S(string name, bool nullable = true) => new(name, ColumnType.String, false, nullable);
    private static ColumnSpec B(string name, bool nullable = true) => new(name, ColumnType.Boolean, false, nullable);

    /// <summary>The Parquet columns of each table the engines write: mzLib's columns, plus <c>isoform_of</c> on (a).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> Columns =
        new Dictionary<string, IReadOnlyList<ColumnSpec>>(StringComparer.Ordinal)
        {
            [CatalogBuilder.GoAccessionTermsTable] = [S("accession", false), S("go_id"), S("aspect"), S("evidence"), B("inherited"), S("isoform_of")],
            [CatalogBuilder.GoTermsTable] = [S("go_id", false), S("go_name"), S("aspect", false), B("obsolete", false)],
            [CatalogBuilder.GoClosureTable] = [S("go_id", false), S("ancestor_go_id", false)],
            [CatalogBuilder.GoTermCategoriesTable] = [S("go_id", false), S("category", false), S("subcategory")],
            [CatalogBuilder.GoGroupMembersTable] = [S("accession", false), S("annotated_as"), S("resolution", false), B("entrapment", false), S("sequence_accession")],
        };

    /// <summary>The engine release: the mzLib this build references.</summary>
    public static Dictionary<string, string> Release() => new(StringComparer.Ordinal)
    {
        ["mzlib"] = typeof(GoAccessionAnnotation).Assembly.GetName().Version!.ToString(3),
    };

    private static string Str(object? v) => v is null ? "None" : PyFormat.Str(v);

    /// <summary>GO's own namespace name, as mzLib's writers spell it (its <c>GoAnnotationTsv.AspectName</c>, internal).</summary>
    public static string? AspectName(GoAspect? aspect) => aspect switch
    {
        null => null,
        GoAspect.BiologicalProcess => "biological_process",
        GoAspect.CellularComponent => "cellular_component",
        GoAspect.MolecularFunction => "molecular_function",
        GoAspect.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(aspect), aspect, null),
    };

    private static Dictionary<string, object?> Obj(IReadOnlyDictionary<string, string> d) => d.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    // --- shared checks -------------------------------------------------------------------------------------

    private static void RequireDefinition(string engine, string? definitionId, string goId)
    {
        if (string.IsNullOrEmpty(definitionId))
            throw new RunnerException(
                $"{engine}: no definition id was given for these rows (go's is {goId}). dataRepo never defines another "
                + "project's number (D24), and an artefact without one could not be cited.");
    }

    private static void RequireInputs(string engine, IReadOnlyDictionary<string, string> inputs, IReadOnlyList<string> roles, string fromBundles)
    {
        var missing = roles.Where(r => !inputs.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new RunnerException($"{engine} needs --input {string.Join("=<path>, --input ", missing)}=<path>.");
        var unknown = inputs.Keys.Where(k => !roles.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new RunnerException(
                $"{engine} takes {(roles.Count == 0 ? "no --input" : $"inputs {string.Join(", ", roles)}")}; not {string.Join(", ", unknown)}. {fromBundles}");
        foreach (var (role, path) in inputs)
            if (!File.Exists(path)) throw new RunnerException($"--input {role}={path}: no such file");
    }

    /// <summary>The file is on disk and hashes to what the bundle recorded.</summary>
    private static void RequireRecorded(string path, string recorded, string what, string whose)
    {
        if (!File.Exists(path))
            throw new RunnerException($"{path} ({what} of {whose}) is not on disk. The runner fetches nothing: restore the file the bundle recorded.");
        var actual = BundleWriter.Sha256File(path);
        if (actual != recorded)
            throw new RunnerException(
                $"{path} now hashes to {actual}, and {whose} recorded {recorded}. It is not the file the search read, so "
                + "annotating it would describe some other search.");
    }

    private static GeneOntologyGraph LoadOntology(string path, string sha)
    {
        GeneOntologyGraph ontology;
        try
        {
            ontology = GeneOntologyGraph.Load(path);
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"mzLib refused the ontology {path}: {e.Message}");
        }
        if (ontology.SourceSha256 != sha) throw new RunnerException($"{path} changed on disk while the run was reading it");
        return ontology;
    }

    /// <summary>A scratch directory for mzLib's files, removed whatever happens.</summary>
    private static T InScratch<T>(Func<string, T> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "datarepo-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            return body(work);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    private static void WriteText(string path, Action<TextWriter> write)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        write(writer);
    }

    /// <summary>mzLib's reader on mzLib's file: the acceptance check.</summary>
    private static AnnotationTable<T> Accept<T>(Func<AnnotationTable<T>> read, string where)
    {
        try
        {
            return read();
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"{where}: mzLib's file failed acceptance, so nothing was written: {e.Message}");
        }
    }

    private static void RequireHeader<T>(AnnotationTable<T> table, string key, string? want, string where)
    {
        var got = table.Header.GetValueOrDefault(key);
        if (got != want) throw new RunnerException($"{where}: mzLib's {table.SourceFileName} says {key} {Str(got)}, and the input was {Str(want)}");
    }

    private static List<object?> Requested(IEnumerable<BundleRef> bundles) =>
        bundles.Select(b => (object?)new Dictionary<string, object?> { ["dataset_id"] = b.DatasetId, ["bundle_id"] = b.BundleId }).ToList();

    // --- (a) go.annotate_databases ----------------------------------------------------------------------------

    /// <summary>For a UniProt isoform accession, its entry, by mzLib's parser (the rule <c>GoAccessionAnnotation</c>
    /// applies D37 by); null for any other accession.</summary>
    public static string? IsoformOf(string accession) =>
        ProteinAccession.Parse(accession) is { Namespace: AccessionNamespace.UniProt, Isoform: not null } parsed ? parsed.EntryAccession : null;

    /// <summary>mzLib's (a) rows as <c>go_accession_terms</c> Parquet rows.</summary>
    public static List<IReadOnlyDictionary<string, object?>> AccessionTermRows(AnnotationTable<GoAccessionTermRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accession"] = r.Accession,
            ["go_id"] = r.GoId,
            ["aspect"] = AspectName(r.Aspect),
            ["evidence"] = r.Evidence.Count > 0 ? string.Join(';', r.Evidence) : null,
            ["inherited"] = r.Inherited,
            ["isoform_of"] = IsoformOf(r.Accession),
        }).ToList();

    /// <summary>One database file's (a): mzLib's annotation of its proteins, written by mzLib and read back.</summary>
    /// <param name="work">Where mzLib's file is written.</param>
    public static (AnnotationTable<GoAccessionTermRow> Table, GoAccessionAnnotation Annotation, string Path) AnnotateFile(
        GeneOntologyGraph ontology, IEnumerable<Protein> proteins, string databaseSha256, string work, string where)
    {
        GoAccessionAnnotation annotation;
        try
        {
            annotation = new GoAccessionAnnotation(ontology, proteins, databaseSha256, SkipUnknownGoIds);
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"{where}: mzLib refused the database: {e.Message}");
        }
        var path = Path.Combine(work, AccessionTermsFile);
        WriteText(path, w => GoAccessionTermsTsv.Write(w, annotation));
        var table = Accept(() => GoAccessionTermsTsv.Read(path), where);
        RequireHeader(table, "annotation_db_sha256", databaseSha256, where);
        RequireHeader(table, "go_obo_sha256", ontology.SourceSha256, where);
        return (table, annotation, path);
    }

    /// <summary>Annotates every target database file the bundles searched not already annotated under these inputs, one
    /// artefact per file.</summary>
    /// <param name="definitionId">go's definition id: <see cref="AccessionDefinitionId"/> in production; refused when null
    /// or empty.</param>
    /// <exception cref="RunnerException">No definition id, a missing or unknown input, a bundle with no target database, a
    /// database that no longer hashes to what its bundles recorded, a development datarepo, or mzLib's file failing
    /// acceptance. Files already annotated stay annotated: each artefact is written whole or not at all.</exception>
    public static RunResult AnnotateDatabasesRun(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        string? definitionId, IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireDefinition(AnnotateDatabases, definitionId, AccessionDefinitionId);
        RequireInputs(AnnotateDatabases, inputs, ["ontology"], "The databases come from each bundle's own record.");
        if (bundles.Count == 0) throw new RunnerException($"{AnnotateDatabases} needs at least one bundle whose databases to annotate");
        install ??= EngineRunner.InstallIdentity();
        release ??= Release();
        var oboSha = BundleWriter.Sha256File(inputs["ontology"]);

        var files = new SortedDictionary<string, (GoDatabaseFile File, List<BundleRef> Bundles)>(StringComparer.Ordinal);
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bundle in bundles)
        {
            var search = CatalogBuilder.GoInputsOf(bundle);
            if (search.Targets.Count == 0)
                throw new RunnerException(
                    $"{bundle.DatasetId} bundle {bundle.BundleId} records no searched target database (`protein_database:*` in "
                    + "bundle.json's sources), so there is nothing to annotate.");
            foreach (var c in search.Contaminants) skipped.Add(c.Name);
            foreach (var t in search.Targets)
            {
                if (!files.TryGetValue(t.Sha256, out var entry)) files[t.Sha256] = entry = (t, []);
                entry.Bundles.Add(bundle);
            }
        }

        GeneOntologyGraph? ontology = null;
        var result = new RunResult { SkippedContaminant = skipped.ToList() };
        foreach (var (sha, (file, users)) in files)
        {
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal) { [PXReprise.Catalog.Runner.GoDatabaseRole] = sha, ["ontology"] = oboSha };
            var aid = EngineRunner.ArtefactId(AnnotateDatabases, release, hashed, definitionId!);
            var existing = EngineRunner.ArtefactDir(store, AnnotateDatabases, aid);
            if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            var whose = string.Join(", ", users.Select(b => $"{b.DatasetId} bundle {b.BundleId}"));
            RequireRecorded(file.Path, sha, "a searched database", whose);
            ontology ??= LoadOntology(inputs["ontology"], oboSha);
            var where = $"{file.Name} ({sha[..12]})";
            // Loaded as the rest of the C# loads a searched database (Specificity, the bridge): no decoys, no sequence
            // variant applied, not a contaminant. One file alone: the artefact is the file's.
            var proteins = Specificity.LoadProteins([(file.Path, sha, false)]);
            result.Written.Add(InScratch(work =>
            {
                var (table, annotation, path) = AnnotateFile(ontology, proteins, sha, work, where);
                var rows = AccessionTermRows(table);
                var aspects = rows.Where(r => r["aspect"] is not null).GroupBy(r => (string)r["aspect"]!)
                    .ToDictionary(g => g.Key, g => (object?)(long)g.Count(), StringComparer.Ordinal);
                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["definition_id"] = definitionId,
                    ["release"] = Obj(release),
                    ["inputs"] = Obj(hashed),
                    ["input_files"] = new Dictionary<string, object?>
                    {
                        [PXReprise.Catalog.Runner.GoDatabaseRole] = BundleWriter.PathText(file.Path),
                        ["ontology"] = BundleWriter.PathText(inputs["ontology"]),
                    },
                    ["datarepo_install"] = install,
                    ["requested_for"] = Requested(users),
                    ["engine_summary"] = new Dictionary<string, object?>
                    {
                        ["database"] = file.Name,
                        ["entries"] = (long)annotation.Accessions.Count,
                        ["entries_without_go"] = (long)rows.Count(r => r["go_id"] is null),
                        ["rows_by_aspect"] = aspects,
                        ["inherited_rows"] = (long)rows.Count(r => r["inherited"] is true),
                        ["go_release"] = table.Header.GetValueOrDefault("go_release"),
                        ["mzlib_release"] = table.Header.GetValueOrDefault("mzlib_release"),
                        ["mzlib_version"] = table.Header.GetValueOrDefault("mzlib_version"),
                        ["skip_unknown_go_ids"] = SkipUnknownGoIds,
                        ["unresolved_go_ids"] = annotation.UnresolvedGoIds.Cast<object?>().ToList(),
                        ["file_sha256"] = BundleWriter.Sha256File(path),
                    },
                    ["acceptance"] = "passed",
                };
                return EngineRunner.WriteArtefact(store, AnnotateDatabases, aid, record,
                    new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { [CatalogBuilder.GoAccessionTermsTable] = rows },
                    new Dictionary<string, string> { [AccessionTermsFile] = path }, Columns);
            }));
        }
        return result;
    }

    // --- (b) go.categorize_terms --------------------------------------------------------------------------------

    /// <summary>mzLib's ontology and category tables as Parquet rows.</summary>
    public static List<IReadOnlyDictionary<string, object?>> TermRows(AnnotationTable<GoTermRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["go_id"] = r.GoId, ["go_name"] = r.GoName, ["aspect"] = AspectName(r.Aspect), ["obsolete"] = r.IsObsolete,
        }).ToList();

    public static List<IReadOnlyDictionary<string, object?>> ClosureRows(AnnotationTable<GoClosureRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["go_id"] = r.GoId, ["ancestor_go_id"] = r.AncestorGoId,
        }).ToList();

    public static List<IReadOnlyDictionary<string, object?>> CategoryRows(AnnotationTable<GoCategoryRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["go_id"] = r.GoId, ["category"] = r.Category, ["subcategory"] = r.Subcategory,
        }).ToList();

    /// <summary>The ontology (terms and closure) and one map's categories over every term, once per (go.obo, map).</summary>
    /// <param name="definitionId">go's definition id: <see cref="CategoryDefinitionId"/> in production; refused when
    /// null or empty.</param>
    /// <exception cref="RunnerException">No definition id, a missing or unknown input, mzLib refusing the ontology or map,
    /// a development datarepo, or mzLib's files failing acceptance.</exception>
    public static RunResult CategorizeTermsRun(string store, IReadOnlyDictionary<string, string> inputs, string? definitionId,
        IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireDefinition(CategorizeTerms, definitionId, CategoryDefinitionId);
        RequireInputs(CategorizeTerms, inputs, ["ontology", "category_map"], "It takes no bundle.");
        install ??= EngineRunner.InstallIdentity();
        release ??= Release();
        var hashed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ontology"] = BundleWriter.Sha256File(inputs["ontology"]),
            ["category_map"] = BundleWriter.Sha256File(inputs["category_map"]),
        };
        var aid = EngineRunner.ArtefactId(CategorizeTerms, release, hashed, definitionId!);
        var result = new RunResult();
        var existing = EngineRunner.ArtefactDir(store, CategorizeTerms, aid);
        if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
        {
            result.AlreadyDone.Add(ArtefactRef.Load(existing));
            return result;
        }
        var ontology = LoadOntology(inputs["ontology"], hashed["ontology"]);
        GoCategoryMap map;
        GoCategoryResolver resolver;
        try
        {
            map = GoCategoryMap.Load(inputs["category_map"]);
            resolver = new GoCategoryResolver(map, ontology);
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"mzLib refused the category map {inputs["category_map"]}: {e.Message}");
        }
        if (map.SourceSha256 != hashed["category_map"]) throw new RunnerException($"{inputs["category_map"]} changed on disk while the run was reading it");

        result.Written.Add(InScratch(work =>
        {
            var (termsPath, closurePath, categoryPath) = (Path.Combine(work, TermsFile), Path.Combine(work, ClosureFile), Path.Combine(work, CategoryFile));
            WriteText(termsPath, w => GoOntologyTsv.WriteTerms(w, ontology));
            WriteText(closurePath, w => GoOntologyTsv.WriteClosure(w, ontology));
            WriteText(categoryPath, w => GoCategoryTsv.Write(w, resolver));
            const string where = CategorizeTerms;
            var terms = Accept(() => GoOntologyTsv.ReadTerms(termsPath), where);
            var closure = Accept(() => GoOntologyTsv.ReadClosure(closurePath), where);
            var categories = Accept(() => GoCategoryTsv.Read(categoryPath), where);
            foreach (var table in new[] { terms.Header, closure.Header, categories.Header })
                if (table.GetValueOrDefault("go_obo_sha256") != hashed["ontology"])
                    throw new RunnerException($"{where}: a file mzLib wrote names another go.obo than the input");
            var mapLine = categories.Header.GetValueOrDefault("category_map");
            if (mapLine != $"{map.MapName} {map.MapVersion} {hashed["category_map"]}")
                throw new RunnerException($"{where}: mzLib's {CategoryFile} names the map {Str(mapLine)}, and the input was {map.MapName} {map.MapVersion} {hashed["category_map"]}");
            var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
            {
                [CatalogBuilder.GoTermsTable] = TermRows(terms),
                [CatalogBuilder.GoClosureTable] = ClosureRows(closure),
                [CatalogBuilder.GoTermCategoriesTable] = CategoryRows(categories),
            };
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = definitionId,
                ["release"] = Obj(release),
                ["inputs"] = Obj(hashed),
                ["input_files"] = new Dictionary<string, object?>
                {
                    ["ontology"] = BundleWriter.PathText(inputs["ontology"]),
                    ["category_map"] = BundleWriter.PathText(inputs["category_map"]),
                },
                ["datarepo_install"] = install,
                ["engine_summary"] = new Dictionary<string, object?>
                {
                    ["go_release"] = terms.Header.GetValueOrDefault("go_release"),
                    ["terms"] = (long)terms.Rows.Count,
                    ["obsolete_terms"] = (long)terms.Rows.Count(t => t.IsObsolete),
                    ["closure_pairs"] = (long)closure.Rows.Count,
                    ["category_rows"] = (long)categories.Rows.Count,
                    ["category_map"] = new Dictionary<string, object?> { ["name"] = map.MapName, ["version"] = map.MapVersion, ["sha256"] = map.SourceSha256 },
                    ["mzlib_release"] = terms.Header.GetValueOrDefault("mzlib_release"),
                    ["mzlib_version"] = terms.Header.GetValueOrDefault("mzlib_version"),
                },
                ["acceptance"] = "passed",
            };
            return EngineRunner.WriteArtefact(store, CategorizeTerms, aid, record, tables,
                new Dictionary<string, string> { [TermsFile] = termsPath, [ClosureFile] = closurePath, [CategoryFile] = categoryPath }, Columns);
        }));
        return result;
    }

    // --- (c) go.resolve_members ---------------------------------------------------------------------------------

    /// <summary>mzLib's (c) rows as <c>go_group_members</c> Parquet rows.</summary>
    public static List<IReadOnlyDictionary<string, object?>> MemberRows(AnnotationTable<MemberResolution> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accession"] = r.Accession,
            ["annotated_as"] = r.AnnotatedAs,
            ["resolution"] = MemberResolutionTsv.KindName(r.Kind),
            ["entrapment"] = r.IsEntrapment,
            ["sequence_accession"] = r.SequenceAccession,
        }).ToList();

    /// <summary>One search's (c): every distinct member of its non-decoy groups resolved against all its target proteins,
    /// written by mzLib and read back.</summary>
    public static (AnnotationTable<MemberResolution> Table, string Path) ResolveGroups(IEnumerable<GoAnnotationGroup> groups,
        IEnumerable<Protein> targetProteins, string annotationDbSha256, string? proteinGroupsSha256, string work, string where)
    {
        var rows = new MemberResolver(targetProteins).ResolveAll(groups.SelectMany(g => g.MemberAccessions ?? []));
        var path = Path.Combine(work, MemberFile);
        WriteText(path, w => MemberResolutionTsv.Write(w, rows, annotationDbSha256, proteinGroupsSha256));
        var table = Accept(() => MemberResolutionTsv.Read(path), where);
        RequireHeader(table, "annotation_db_sha256", annotationDbSha256, where);
        RequireHeader(table, "source_file_sha256", proteinGroupsSha256, where);
        return (table, path);
    }

    /// <summary>Resolves the members of every bundle not already resolved with these files, one artefact per bundle.</summary>
    /// <param name="definitionId"><see cref="MemberDefinitionId"/> in production; refused when null or empty.</param>
    /// <exception cref="RunnerException">No definition id, any --input, a bundle with no protein-group file (an isobaric
    /// search, GO-D14) or no target database, a file that no longer hashes to what the bundle recorded, a development
    /// datarepo, or mzLib's file failing acceptance.</exception>
    public static RunResult ResolveMembersRun(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        string? definitionId, IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireDefinition(ResolveMembers, definitionId, MemberDefinitionId);
        RequireInputs(ResolveMembers, inputs, [], "The protein groups and the searched databases come from each bundle's own record.");
        if (bundles.Count == 0) throw new RunnerException($"{ResolveMembers} needs at least one bundle to run on");
        install ??= EngineRunner.InstallIdentity();
        release ??= Release();
        var result = new RunResult();
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bundle in bundles)
        {
            var where = $"{bundle.DatasetId} bundle {bundle.BundleId}";
            var search = CatalogBuilder.GoInputsOf(bundle);
            if (search.ProteinGroupsPath is null || search.ProteinGroupsSha256 is null)
                throw new RunnerException(
                    $"{where} records no `protein_group_quant` source in bundle.json, so there are no groups to resolve (an isobaric "
                    + "search writes no AllQuantifiedProteinGroups.tsv and is not annotated, GO-D14).");
            if (search.Targets.Count == 0)
                throw new RunnerException($"{where} records no searched target database (`protein_database:*` in bundle.json's sources).");
            var repeated = search.Targets.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (repeated.Count > 0)
                throw new RunnerException($"{where} searched two target databases named {string.Join(", ", repeated)}; their roles would collide.");
            foreach (var c in search.Contaminants) skipped.Add(c.Name);
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal) { [PXReprise.Catalog.Runner.GoProteinGroupsRole] = search.ProteinGroupsSha256 };
            foreach (var t in search.Targets) hashed[PXReprise.Catalog.Runner.GoDatabaseRolePrefix + t.Name] = t.Sha256;
            var aid = EngineRunner.ArtefactId(ResolveMembers, release, hashed, definitionId!);
            var existing = EngineRunner.ArtefactDir(store, ResolveMembers, aid);
            if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            RequireRecorded(search.ProteinGroupsPath, search.ProteinGroupsSha256, "the protein-group file", where);
            foreach (var t in search.Targets) RequireRecorded(t.Path, t.Sha256, "a searched database", where);
            List<GoAnnotationGroup> groups;
            try
            {
                var file = new ProteinGroupFromTsvFile(search.ProteinGroupsPath);
                file.LoadResults();
                groups = file.Results.ToGoAnnotationGroups().ToList();
            }
            catch (Exception e) when (e is MzLibException or InvalidDataException)
            {
                throw new RunnerException($"{where}: mzLib could not read the protein groups {search.ProteinGroupsPath}: {e.Message}");
            }
            if (groups.Count == 0) throw new RunnerException($"{where}: the protein-group file holds no non-decoy group");
            var dbSha = search.AnnotationDbSha256!;
            // ALL the search's target databases together, as the annotator over the search read them: a member is resolved
            // against every entry the search could have reported.
            var proteins = Specificity.LoadProteins(search.Targets.Select(t => (t.Path, t.Sha256, false)));
            result.Written.Add(InScratch(work =>
            {
                var (table, path) = ResolveGroups(groups, proteins, dbSha, search.ProteinGroupsSha256, work, where);
                var rows = MemberRows(table);
                var inputFiles = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PXReprise.Catalog.Runner.GoProteinGroupsRole] = BundleWriter.PathText(search.ProteinGroupsPath),
                };
                foreach (var t in search.Targets) inputFiles[PXReprise.Catalog.Runner.GoDatabaseRolePrefix + t.Name] = BundleWriter.PathText(t.Path);
                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["definition_id"] = definitionId,
                    ["release"] = Obj(release),
                    ["inputs"] = Obj(hashed),
                    ["input_files"] = inputFiles,
                    ["datarepo_install"] = install,
                    ["requested_for"] = Requested([bundle]),
                    ["engine_summary"] = new Dictionary<string, object?>
                    {
                        ["groups"] = (long)groups.Count,
                        ["members"] = (long)rows.Count,
                        ["resolution_counts"] = new[] { "self", "inherited", "no_entry" }
                            .ToDictionary(k => k, k => (object?)(long)rows.Count(r => Equals(r["resolution"], k)), StringComparer.Ordinal),
                        ["entrapment_members"] = (long)rows.Count(r => r["entrapment"] is true),
                        ["annotation_db_sha256"] = dbSha,
                        ["annotation_databases"] = search.Targets.Select(t => (object?)new Dictionary<string, object?> { ["name"] = t.Name, ["sha256"] = t.Sha256 }).ToList(),
                        ["contaminant_databases_not_annotated"] = search.Contaminants.Select(c => (object?)c.Name).ToList(),
                        ["mzlib_release"] = table.Header.GetValueOrDefault("mzlib_release"),
                        ["mzlib_version"] = table.Header.GetValueOrDefault("mzlib_version"),
                        ["file_sha256"] = BundleWriter.Sha256File(path),
                    },
                    ["acceptance"] = "passed",
                };
                return EngineRunner.WriteArtefact(store, ResolveMembers, aid, record,
                    new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { [CatalogBuilder.GoGroupMembersTable] = rows },
                    new Dictionary<string, string> { [MemberFile] = path }, Columns);
            }));
        }
        result.SkippedContaminant.AddRange(skipped);
        return result;
    }

    /// <summary>The refusal <c>datarepo run go.annotate_groups</c> gives from datarepo 1.5.0.</summary>
    public static string Retired =>
        $"{PXReprise.Catalog.Runner.GoEngine} is retired from datarepo 1.5.0 (catalog format 17): GO is no longer stored per "
        + $"bundle. Run {CategorizeTerms} once (--input ontology=<go.obo> --input category_map=<map.tsv>), {AnnotateDatabases} "
        + $"over the datasets (--input ontology=<go.obo>; one artefact per searched database file), and {ResolveMembers} over "
        + "the datasets (no --input), then build: the catalog joins them into protein_localizations.";
}
