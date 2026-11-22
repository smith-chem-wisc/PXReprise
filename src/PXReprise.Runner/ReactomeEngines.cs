using System.Text;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest.Sources;
using Proteomics;
using UsefulProteomicsDatabases.Annotation;
using UsefulProteomicsDatabases.Pathways;

namespace PXReprise.Runner;

/// <summary>One Reactome release this runner reads, by the sha256 of its two files. Reactome's files carry no release
/// number, so the number comes from here, never from the operator or a file name.</summary>
/// <param name="Release">Reactome's release number, e.g. <c>96</c>.</param>
/// <param name="PathwaysSha256">The sha256 of the release's <c>ReactomePathways.txt</c>.</param>
/// <param name="RelationSha256">The sha256 of the release's <c>ReactomePathwaysRelation.txt</c>.</param>
/// <param name="Attribution">The citation Reactome asks for (its data are CC0, so it is requested, not required).</param>
public sealed record ReactomeReleaseFiles(string Release, string PathwaysSha256, string RelationSha256, string Attribution);

/// <summary>kegg's two engines: Reactome stored once per target set and once per release, never per bundle, and joined
/// by the catalog (<c>kegg:DEF-REACTOME-MEMBERSHIP v1</c>; mzLib #1536, #1537 and #1576, released in 1.0.598).</summary>
/// <remarks>
/// <para>go's pattern (<see cref="GoReferenceEngines"/>): each engine computes nothing about Reactome. It decides what to
/// run on, checks every input against what the bundles recorded, runs mzLib, reads mzLib's own files back with mzLib's
/// own readers as the acceptance check, and stores the rows those readers give. The files are left out of the artefact:
/// each one's sha256 and row count stay in <c>run.json</c> (<c>files</c>), and the rows are the artefact's Parquet.</para>
/// <list type="bullet">
/// <item><see cref="Membership"/> (T1 and T4): one database FILE's Reactome membership (<c>ReactomeAccessionAnnotation</c>)
/// and KEGG gene links (<c>KeggGeneLinksTsv</c>), one artefact per (file, mzLib, definition), shared by every search that
/// read the file: GO's keying (<c>go.annotate_databases</c>), so GO and Reactome keep one (kegg 010). The two rules of the
/// definition that read a search's whole target set, an organism's Reactome code and an isoform's inheritance of its
/// entry's rows, are applied again by the catalog over every file the search read (<c>CatalogReactome.cs</c>), as GO's
/// inheritance is. So each row also carries <c>isoform_of</c> (mzLib's accession parser, as GO's) and
/// <c>pathway_species</c> (the id's species code, read as mzLib's species rule reads it), and the artefact records the
/// file's species codes and how many references the file's own species check dropped.</item>
/// <item><see cref="ReleaseEngine"/> (T2, T3 and the closure): one Reactome release's pathways, hierarchy and ancestors
/// (<c>ReactomeRelease</c>, <c>ReactomeReleaseTsv</c>), one artefact per (the two files, mzLib, definition). It takes no
/// bundle, and only a release listed in <see cref="Releases"/>.</item>
/// </list>
/// <para>Which database entry each protein-group member takes is go's <c>go.resolve_members</c> artefact (mzLib's
/// <c>MemberResolver</c>), which holds no GO: the catalog reads the same one for GO and for Reactome.</para>
/// <para><b>The definition is kegg's</b> (D24: dataRepo never defines another project's number), and each run refuses a
/// call that passes none. The release tables have no definition of their own: their rules ("Ancestors and release
/// drift", "Cross-species") are stated in <see cref="DefinitionId"/>, so both engines run under it.</para>
/// <para>Contaminant panels are never annotation input (GO-D5), as for GO.</para>
/// </remarks>
public static class ReactomeEngines
{
    public const string Membership = PXReprise.Catalog.Runner.ReactomeMembership;
    public const string ReleaseEngine = PXReprise.Catalog.Runner.ReactomeRelease;

    /// <summary>kegg's definition: what membership means, and the release tables' rules. mzLib writes the same id in T1's
    /// header (<c>ReactomeAccessionAnnotation.DefinitionId</c> and <c>DefinitionVersion</c>).</summary>
    public const string DefinitionId = "kegg:DEF-REACTOME-MEMBERSHIP v1";

    /// <summary>The input roles of <see cref="ReleaseEngine"/>: Reactome's two flat files.</summary>
    public static readonly IReadOnlyList<string> ReleaseRoles = ["pathways", "relation"];

    /// <summary>The Reactome releases this runner reads, copied from Reactome's download server. A new release is a new
    /// line, never an edit to one listed here, so adding one is one line and a datarepo release. Release 96 is the one
    /// UniProt 2026_03's Reactome cross-references carry (kegg KEGG-P2), downloaded 2026-10-25 from
    /// <c>https://download.reactome.org/96/</c>.</summary>
    public static readonly IReadOnlyList<ReactomeReleaseFiles> Releases =
    [
        new("96",
            "eac3ac074b3849a6d928260f4f71920f346584b0114afa7b60fcc8e40eefd9ec",
            "72e2da17ef5806d287a0d932cd6d161fea62ef7a567b6e95601b67463c07170e",
            "\"ReactomePathways.txt\" and \"ReactomePathwaysRelation.txt\", Reactome, release 96, https://reactome.org/download-data/ "
            + "(downloaded 2026-10-25). Reactome's data are CC0 (https://reactome.org/license); Ragueneau E, et al. The Reactome "
            + "Knowledgebase 2026. Nucleic Acids Res. 2026;54(D1):D673-D681. doi:10.1093/nar/gkaf1223."),
    ];

    /// <summary>UniProt's licence, verbatim: T1 and T4 are UniProt's cross-references, read from the searched XML.</summary>
    public const string UniProtAttribution =
        "The Reactome and KEGG cross-references are UniProt's, read from the searched UniProt XML. UniProt "
        + "(https://www.uniprot.org/help/license): \"We have chosen to apply the Creative Commons Attribution 4.0 International "
        + "(CC BY 4.0) License to all copyrightable parts of our databases.\"";

    /// <summary>mzLib's files, written and read back as acceptance; each is recorded in <c>run.json</c> by sha256 and row
    /// count, and none is kept: the Parquet holds the rows, and the run record the header values.</summary>
    public const string MembershipFile = "reactome_membership.tsv";
    public const string KeggLinksFile = "kegg_gene_links.tsv";
    public const string PathwaysFile = "reactome_pathways.tsv";
    public const string HierarchyFile = "reactome_hierarchy.tsv";
    public const string AncestorsFile = "reactome_ancestors.tsv";

    private static ColumnSpec S(string name, bool nullable = true) => new(name, ColumnType.String, false, nullable);
    private static ColumnSpec B(string name, bool nullable = true) => new(name, ColumnType.Boolean, false, nullable);
    private static ColumnSpec I(string name, bool nullable = true) => new(name, ColumnType.Int64, false, nullable);

    /// <summary>The Parquet columns of each table the engines write: mzLib's columns, plus on T1 its header's
    /// <c>definition_id</c> (every row carries its definition, dataRepo R10), <c>isoform_of</c> and <c>pathway_species</c>;
    /// and <c>reactome_species</c>, T1's header maps as rows.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> Columns =
        new Dictionary<string, IReadOnlyList<ColumnSpec>>(StringComparer.Ordinal)
        {
            [CatalogBuilder.ReactomeMembershipTable] =
            [
                S("accession", false), S("organism"), S("pathway_id"), S("molecule"), S("sequence_accession"), B("inherited"), S("status", false),
                S("definition_id", false), S("isoform_of"), S("pathway_species"),
            ],
            [CatalogBuilder.KeggGeneLinksTable] = [S("accession", false), S("kegg_gene_id", false)],
            [CatalogBuilder.ReactomeSpeciesTable] = [S("organism", false), S("species_code"), I("unvalidated_references")],
            [CatalogBuilder.ReactomePathwaysTable] = [S("pathway_id", false), S("name"), S("species_name"), S("human_equivalent_id")],
            [CatalogBuilder.ReactomeHierarchyTable] = [S("parent_id", false), S("child_id", false)],
            [CatalogBuilder.ReactomeAncestorsTable] = [S("pathway_id", false), S("ancestor_pathway_id", false)],
        };

    /// <summary>The engine release: the mzLib this build references.</summary>
    public static Dictionary<string, string> Release() => new(StringComparer.Ordinal)
    {
        ["mzlib"] = typeof(ReactomeAccessionAnnotation).Assembly.GetName().Version!.ToString(3),
    };

    private static string Str(object? v) => v is null ? "None" : PyFormat.Str(v);

    private static Dictionary<string, object?> Obj(IReadOnlyDictionary<string, string> d) => d.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    // --- shared checks -------------------------------------------------------------------------------------

    private static void RequireDefinition(string engine, string? definitionId)
    {
        if (string.IsNullOrEmpty(definitionId))
            throw new RunnerException(
                $"{engine}: no definition id was given for these rows (kegg's is {DefinitionId}). dataRepo never defines another "
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

    /// <summary>A scratch directory for mzLib's files, removed whatever happens.</summary>
    private static T InScratch<T>(Func<string, T> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "datarepo-reactome-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>What <c>run.json</c> records of mzLib's files: each one's sha256 and row count; none is kept.</summary>
    private static Dictionary<string, object?> FileRecord(params (string Name, string Path, int Rows)[] files) =>
        files.ToDictionary(f => f.Name, f => (object?)new Dictionary<string, object?>
        {
            ["sha256"] = BundleWriter.Sha256File(f.Path), ["rows"] = (long)f.Rows, ["kept"] = false,
        }, StringComparer.Ordinal);

    private static List<object?> Requested(IEnumerable<BundleRef> bundles) =>
        bundles.Select(b => (object?)new Dictionary<string, object?> { ["dataset_id"] = b.DatasetId, ["bundle_id"] = b.BundleId }).ToList();

    // --- kegg.reactome_membership ------------------------------------------------------------------------------

    /// <summary>"annotated", "no_reference" or "species_not_covered", as mzLib's file spells them.</summary>
    public static string StatusName(ReactomeMembershipStatus status) => ReactomeMembershipTsv.StatusName(status);

    /// <summary>A Reactome stable id and its species code, as mzLib's species rule reads it
    /// (<c>ReactomeAccessionAnnotation</c>'s <c>^R-(?&lt;species&gt;[A-Z]{3})-\d+$</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex StableId = new(@"^R-(?<species>[A-Z]{3})-\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The species code of a Reactome stable id (<c>HSA</c> of <c>R-HSA-70171</c>), as mzLib's species rule counts
    /// it; null for an id that is not a stable id, which counts toward an organism's references but can never be its
    /// code.</summary>
    public static string? PathwaySpecies(string? pathwayId) =>
        pathwayId is not null && StableId.Match(pathwayId) is { Success: true } m ? m.Groups["species"].Value : null;

    /// <summary>mzLib's T1 rows as <c>reactome_membership</c> Parquet rows, each with the header's definition,
    /// <c>isoform_of</c> (mzLib's accession parser, <see cref="GoReferenceEngines.IsoformOf"/>) and <c>pathway_species</c>.</summary>
    public static List<IReadOnlyDictionary<string, object?>> MembershipRows(AnnotationTable<ReactomeMembershipRow> table)
    {
        var definition = table.Header["definition"];
        return table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accession"] = r.Accession,
            ["organism"] = r.Organism,
            ["pathway_id"] = r.PathwayId,
            ["molecule"] = r.Molecule,
            ["sequence_accession"] = r.SequenceAccession,
            ["inherited"] = r.Inherited,
            ["status"] = StatusName(r.Status),
            ["definition_id"] = definition,
            ["isoform_of"] = GoReferenceEngines.IsoformOf(r.Accession),
            ["pathway_species"] = PathwaySpecies(r.PathwayId),
        }).ToList();
    }

    /// <summary>mzLib's T4 rows as <c>kegg_gene_links</c> Parquet rows.</summary>
    public static List<IReadOnlyDictionary<string, object?>> KeggRows(AnnotationTable<KeggGeneLinkRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accession"] = r.Accession, ["kegg_gene_id"] = r.KeggGeneId,
        }).ToList();

    /// <summary>One header map of T1 (<c>organism=value;...</c>), or nothing when mzLib wrote no line for it.</summary>
    public static SortedDictionary<string, string> HeaderMap(AnnotationTable<ReactomeMembershipRow> table, string key)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var line = table.Header.GetValueOrDefault(key);
        if (string.IsNullOrEmpty(line)) return map;
        foreach (var pair in line.Split(';'))
        {
            var at = pair.LastIndexOf('=');
            if (at <= 0 || !map.TryAdd(pair[..at], pair[(at + 1)..]))
                throw new RunnerException($"mzLib's {table.SourceFileName}: {key} holds {Str(pair)}, which is not one organism=value pair");
        }
        return map;
    }

    /// <summary>T1's species header as <c>reactome_species</c> rows: one per organism with a Reactome reference, its code
    /// when it was read, else the count of its references (kept unfiltered). Organisms in ordinal order.</summary>
    public static List<IReadOnlyDictionary<string, object?>> SpeciesRows(AnnotationTable<ReactomeMembershipRow> table)
    {
        var codes = HeaderMap(table, "species_codes");
        var unvalidated = HeaderMap(table, "unvalidated_organisms");
        foreach (var organism in codes.Keys.Intersect(unvalidated.Keys, StringComparer.Ordinal))
            throw new RunnerException($"mzLib's {table.SourceFileName}: {organism} has a species code and is listed unvalidated");
        return codes.Keys.Concat(unvalidated.Keys).Order(StringComparer.Ordinal).Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["organism"] = o,
            ["species_code"] = codes.GetValueOrDefault(o),
            ["unvalidated_references"] = unvalidated.TryGetValue(o, out var n) ? long.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : null,
        }).ToList();
    }

    /// <summary>One database file's T1 and T4: mzLib's annotation of its proteins, written by mzLib and read back.</summary>
    /// <param name="annotationDbSha256">The file's sha256: the key mzLib writes in both headers.</param>
    /// <param name="work">Where mzLib's files are written.</param>
    public static (AnnotationTable<ReactomeMembershipRow> Membership, string MembershipPath, AnnotationTable<KeggGeneLinkRow> Links, string LinksPath)
        AnnotateFile(IReadOnlyList<Protein> proteins, string annotationDbSha256, string work, string where)
    {
        ReactomeAccessionAnnotation annotation;
        try
        {
            annotation = new ReactomeAccessionAnnotation(proteins, annotationDbSha256);
        }
        catch (ArgumentException e)
        {
            throw new RunnerException($"{where}: mzLib refused the database: {e.Message}");
        }
        var membershipPath = Path.Combine(work, MembershipFile);
        var linksPath = Path.Combine(work, KeggLinksFile);
        try
        {
            WriteText(membershipPath, w => ReactomeMembershipTsv.Write(w, annotation));
            WriteText(linksPath, w => KeggGeneLinksTsv.Write(w, KeggGeneLinksTsv.Links(proteins), annotationDbSha256));
        }
        catch (ArgumentException e)
        {
            throw new RunnerException($"{where}: mzLib could not write the database's tables: {e.Message}");
        }
        var membership = Accept(() => ReactomeMembershipTsv.Read(membershipPath), where);
        RequireHeader(membership, "annotation_db_sha256", annotationDbSha256, where);
        RequireHeader(membership, "definition", DefinitionId, where);
        var links = Accept(() => KeggGeneLinksTsv.Read(linksPath), where);
        RequireHeader(links, "annotation_db_sha256", annotationDbSha256, where);
        return (membership, membershipPath, links, linksPath);
    }

    /// <summary>Annotates every target database FILE the bundles searched not already annotated under this definition, one
    /// artefact per file, as <c>go.annotate_databases</c> does.</summary>
    /// <param name="definitionId">kegg's definition id: <see cref="DefinitionId"/> in production; refused when null or empty.</param>
    /// <exception cref="RunnerException">No definition id, any --input, a bundle with no target database, a database that
    /// no longer hashes to what its bundles recorded, a development datarepo, or mzLib's files failing acceptance. Files
    /// already annotated stay annotated: each artefact is written whole or not at all.</exception>
    public static RunResult MembershipRun(string store, IReadOnlyList<BundleRef> bundles, IReadOnlyDictionary<string, string> inputs,
        string? definitionId, IReadOnlyDictionary<string, object?>? install = null, IReadOnlyDictionary<string, string>? release = null)
    {
        RequireDefinition(Membership, definitionId);
        RequireInputs(Membership, inputs, [], "The target databases come from each bundle's own record.");
        if (bundles.Count == 0) throw new RunnerException($"{Membership} needs at least one bundle whose target databases to annotate");
        install ??= EngineRunner.InstallIdentity();
        release ??= Release();

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

        var result = new RunResult { SkippedContaminant = skipped.ToList() };
        foreach (var (sha, (file, users)) in files)
        {
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal) { [PXReprise.Catalog.Runner.ReactomeDatabaseRole] = sha };
            var aid = EngineRunner.ArtefactId(Membership, release, hashed, definitionId!);
            var existing = EngineRunner.ArtefactDir(store, Membership, aid);
            if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
            {
                result.AlreadyDone.Add(ArtefactRef.Load(existing));
                continue;
            }
            var whose = string.Join(", ", users.Select(b => $"{b.DatasetId} bundle {b.BundleId}"));
            RequireRecorded(file.Path, sha, "a searched database", whose);
            var where = $"{file.Name} ({sha[..12]})";
            // Loaded as the rest of the C# loads a searched database (Specificity, the bridge): no decoys, no sequence
            // variant applied, not a contaminant. One file alone: the artefact is the file's.
            var proteins = Specificity.LoadProteins([(file.Path, sha, false)]);
            result.Written.Add(InScratch(work =>
            {
                var (membership, membershipPath, links, linksPath) = AnnotateFile(proteins, sha, work, where);
                var rows = MembershipRows(membership);
                var keggRows = KeggRows(links);
                var species = SpeciesRows(membership);
                var entries = membership.Rows.GroupBy(r => r.Accession, StringComparer.Ordinal).ToList();
                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["definition_id"] = definitionId,
                    ["release"] = Obj(release),
                    ["inputs"] = Obj(hashed),
                    ["input_files"] = new Dictionary<string, object?> { [PXReprise.Catalog.Runner.ReactomeDatabaseRole] = BundleWriter.PathText(file.Path) },
                    ["datarepo_install"] = install,
                    ["requested_for"] = Requested(users),
                    ["engine_summary"] = new Dictionary<string, object?>
                    {
                        ["database"] = file.Name,
                        ["entries"] = (long)entries.Count,
                        ["entries_by_status"] = new[] { "annotated", "no_reference", "species_not_covered" }.ToDictionary(
                            k => k, k => (object?)(long)entries.Count(g => StatusName(g.First().Status) == k), StringComparer.Ordinal),
                        ["pathway_rows"] = (long)membership.Rows.Count(r => r.PathwayId is not null),
                        ["isoform_rows"] = (long)membership.Rows.Count(r => r.Molecule is not null),
                        ["inherited_rows"] = (long)membership.Rows.Count(r => r.Inherited == true),
                        ["species_codes"] = HeaderMap(membership, "species_codes").ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal),
                        ["unvalidated_organisms"] = HeaderMap(membership, "unvalidated_organisms")
                            .ToDictionary(kv => kv.Key, kv => (object?)long.Parse(kv.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal),
                        ["dropped_foreign_references"] = long.Parse(membership.Header.GetValueOrDefault("dropped_foreign_references") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                        ["kegg_links"] = (long)links.Rows.Count,
                        ["definition"] = membership.Header["definition"],
                        ["mzlib_release"] = membership.Header.GetValueOrDefault("mzlib_release"),
                        ["mzlib_version"] = membership.Header.GetValueOrDefault("mzlib_version"),
                        ["attribution"] = UniProtAttribution,
                    },
                    ["files"] = FileRecord((MembershipFile, membershipPath, membership.Rows.Count), (KeggLinksFile, linksPath, links.Rows.Count)),
                    ["acceptance"] = "passed",
                };
                return EngineRunner.WriteArtefact(store, Membership, aid, record,
                    new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
                    {
                        [CatalogBuilder.ReactomeMembershipTable] = rows,
                        [CatalogBuilder.KeggGeneLinksTable] = keggRows,
                        [CatalogBuilder.ReactomeSpeciesTable] = species,
                    }, null, Columns);
            }));
        }
        return result;
    }

    // --- kegg.reactome_release ---------------------------------------------------------------------------------

    public static List<IReadOnlyDictionary<string, object?>> PathwayRows(AnnotationTable<ReactomePathwayRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pathway_id"] = r.PathwayId, ["name"] = r.Name, ["species_name"] = r.SpeciesName, ["human_equivalent_id"] = r.HumanEquivalentId,
        }).ToList();

    public static List<IReadOnlyDictionary<string, object?>> HierarchyRows(AnnotationTable<ReactomeHierarchyRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["parent_id"] = r.ParentId, ["child_id"] = r.ChildId,
        }).ToList();

    public static List<IReadOnlyDictionary<string, object?>> AncestorRows(AnnotationTable<ReactomeAncestorRow> table) =>
        table.Rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pathway_id"] = r.PathwayId, ["ancestor_pathway_id"] = r.AncestorPathwayId,
        }).ToList();

    /// <summary>One Reactome release's three tables, once per release.</summary>
    /// <param name="definitionId">kegg's definition id: <see cref="DefinitionId"/> in production; refused when null or empty.</param>
    /// <param name="releases">The releases this reads; <see cref="Releases"/> when null. Tests pass their own.</param>
    /// <exception cref="RunnerException">No definition id, a missing or unknown input, files that are not a release listed
    /// in <paramref name="releases"/>, mzLib refusing them (a cycle, an edge naming no pathway), a development datarepo, or
    /// mzLib's files failing acceptance.</exception>
    public static RunResult ReleaseRun(string store, IReadOnlyDictionary<string, string> inputs, string? definitionId,
        IReadOnlyList<ReactomeReleaseFiles>? releases = null, IReadOnlyDictionary<string, object?>? install = null,
        IReadOnlyDictionary<string, string>? release = null)
    {
        RequireDefinition(ReleaseEngine, definitionId);
        RequireInputs(ReleaseEngine, inputs, ReleaseRoles, "It takes no bundle.");
        releases ??= Releases;
        var hashed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pathways"] = BundleWriter.Sha256File(inputs["pathways"]),
            ["relation"] = BundleWriter.Sha256File(inputs["relation"]),
        };
        var known = releases.FirstOrDefault(r => r.PathwaysSha256 == hashed["pathways"] && r.RelationSha256 == hashed["relation"])
            ?? throw new RunnerException(
                $"{Path.GetFileName(inputs["pathways"])} (sha256 {hashed["pathways"]}) and {Path.GetFileName(inputs["relation"])} "
                + $"(sha256 {hashed["relation"]}) are not a Reactome release this runner reads. Known: "
                + $"{string.Join(", ", releases.Select(r => $"release {r.Release}"))}. Pass the release's two files unchanged, as "
                + "Reactome's download server gives them; a new release is a new datarepo release.");
        install ??= EngineRunner.InstallIdentity();
        release ??= Release();
        var aid = EngineRunner.ArtefactId(ReleaseEngine, release, hashed, definitionId!);
        var result = new RunResult();
        var existing = EngineRunner.ArtefactDir(store, ReleaseEngine, aid);
        if (File.Exists(Path.Combine(existing, PXReprise.Catalog.Runner.RunRecord)))
        {
            result.AlreadyDone.Add(ArtefactRef.Load(existing));
            return result;
        }
        ReactomeRelease loaded;
        try
        {
            loaded = ReactomeRelease.Load(inputs["pathways"], inputs["relation"], known.Release);
        }
        catch (InvalidDataException e)
        {
            throw new RunnerException($"mzLib refused Reactome release {known.Release}: {e.Message}");
        }
        if (loaded.PathwaysSha256 != hashed["pathways"] || loaded.RelationSha256 != hashed["relation"])
            throw new RunnerException($"Reactome release {known.Release}'s files changed on disk while the run was reading them");

        result.Written.Add(InScratch(work =>
        {
            var (pathwaysPath, hierarchyPath, ancestorsPath) = (Path.Combine(work, PathwaysFile), Path.Combine(work, HierarchyFile), Path.Combine(work, AncestorsFile));
            WriteText(pathwaysPath, w => ReactomeReleaseTsv.WritePathways(w, loaded));
            WriteText(hierarchyPath, w => ReactomeReleaseTsv.WriteHierarchy(w, loaded));
            WriteText(ancestorsPath, w => ReactomeReleaseTsv.WriteAncestors(w, loaded));
            var where = $"{ReleaseEngine} (release {known.Release})";
            var pathways = Accept(() => ReactomeReleaseTsv.ReadPathways(pathwaysPath), where);
            var hierarchy = Accept(() => ReactomeReleaseTsv.ReadHierarchy(hierarchyPath), where);
            var ancestors = Accept(() => ReactomeReleaseTsv.ReadAncestors(ancestorsPath), where);
            foreach (var header in new[] { pathways.Header, hierarchy.Header, ancestors.Header })
                if (header.GetValueOrDefault("reactome_release") != known.Release
                    || header.GetValueOrDefault("reactome_pathways_sha256") != hashed["pathways"]
                    || header.GetValueOrDefault("reactome_relation_sha256") != hashed["relation"])
                    throw new RunnerException($"{where}: a file mzLib wrote names another release or other files than the input");
            var record = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definition_id"] = definitionId,
                ["release"] = Obj(release),
                ["inputs"] = Obj(hashed),
                ["input_files"] = new Dictionary<string, object?>
                {
                    ["pathways"] = BundleWriter.PathText(inputs["pathways"]),
                    ["relation"] = BundleWriter.PathText(inputs["relation"]),
                },
                ["datarepo_install"] = install,
                ["engine_summary"] = new Dictionary<string, object?>
                {
                    ["reactome_release"] = known.Release,
                    ["pathways"] = (long)pathways.Rows.Count,
                    ["species"] = (long)pathways.Rows.Select(p => p.SpeciesName).Distinct(StringComparer.Ordinal).Count(),
                    ["human_equivalents"] = (long)pathways.Rows.Count(p => p.HumanEquivalentId is not null),
                    ["hierarchy_pairs"] = (long)hierarchy.Rows.Count,
                    ["ancestor_pairs"] = (long)ancestors.Rows.Count,
                    ["roots"] = (long)loaded.Roots.Count,
                    ["mzlib_release"] = pathways.Header.GetValueOrDefault("mzlib_release"),
                    ["mzlib_version"] = pathways.Header.GetValueOrDefault("mzlib_version"),
                    ["attribution"] = known.Attribution,
                },
                ["files"] = FileRecord((PathwaysFile, pathwaysPath, pathways.Rows.Count), (HierarchyFile, hierarchyPath, hierarchy.Rows.Count),
                    (AncestorsFile, ancestorsPath, ancestors.Rows.Count)),
                ["acceptance"] = "passed",
            };
            return EngineRunner.WriteArtefact(store, ReleaseEngine, aid, record,
                new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal)
                {
                    [CatalogBuilder.ReactomePathwaysTable] = PathwayRows(pathways),
                    [CatalogBuilder.ReactomeHierarchyTable] = HierarchyRows(hierarchy),
                    [CatalogBuilder.ReactomeAncestorsTable] = AncestorRows(ancestors),
                }, null, Columns);
        }));
        return result;
    }
}
