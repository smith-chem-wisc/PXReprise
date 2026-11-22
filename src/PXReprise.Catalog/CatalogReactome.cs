using System.Security.Cryptography;
using System.Text;
using PXReprise.Bundle;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;

namespace PXReprise.Catalog;

/// <summary>Reactome pathways and KEGG gene links as repository-wide reference data: stored once per database file and
/// per release, never per bundle, and joined by the catalog (kegg's <c>kegg:DEF-REACTOME-MEMBERSHIP v1</c>; mzLib #1536,
/// #1537 and #1576).</summary>
/// <remarks>
/// <para><b>GO's pattern</b> (<c>CatalogGo.cs</c>), applied to kegg's two engines:</para>
/// <list type="number">
/// <item><b>One copy per reference release, never per bundle.</b> <c>kegg.reactome_membership</c> writes one database
/// FILE's membership (T1), KEGG gene links (T4) and species codes, once per (file, mzLib, definition), shared by every
/// search that read the file, as <c>go.annotate_databases</c> is. <c>kegg.reactome_release</c> writes one Reactome
/// release's pathways, hierarchy and ancestor closure, and a catalog serves exactly one. Which entry each protein-group
/// member takes is go's <c>go.resolve_members</c> artefact, read for Reactome as it is for GO; it holds neither.</item>
/// <item><b>Reference tables, always there.</b> <c>reactome_pathways</c>, <c>reactome_hierarchy</c>,
/// <c>reactome_ancestors</c>, <c>reactome_membership</c>, <c>reactome_species</c> and <c>kegg_gene_links</c> are copied from
/// the artefacts (kind <c>engine:&lt;engine&gt;</c>, <c>artefact_id</c> on every row) in both layouts, typed and empty when
/// none is served.</item>
/// <item><b>Derived per search, in which every refusal is a row.</b> <c>protein_reactome</c> gives every distinct member
/// of a served search's non-decoy groups at least one row, with its pathways or the reason it has none;
/// <c>reactome_group_status</c> gives every group exactly one status on kegg's ladder; <c>reactome_group_pathways</c> gives
/// each group's pathways with how many of its members carry each.</item>
/// <item><b>Checks</b> that fail the build: every member has a status, every entry a member takes is in its search's
/// files, each artefact's rows are all there, one release per catalog, one Reactome code per organism (dataRepo
/// KEGG-D6), and the two exactness conditions below.</item>
/// </list>
/// <para><b>Per file, with the definition's two set-wide rules applied here, over every file the search read</b> (kegg
/// 010; GO's inheritance is applied the same way). mzLib's <c>ReactomeAccessionAnnotation</c> reads the target proteins
/// one search loaded, so two of its rules read the whole set, and a file alone can give other rows:</para>
/// <list type="bullet">
/// <item><b>An organism's Reactome code</b> is the prefix a strict majority of at least 10 of its references carry, each
/// (entry, pathway) once; a reference with another prefix is dropped. Here: the code a file of the search read for the
/// organism, or, when no file could, the code read over the union of the files' references (all kept unfiltered, so the
/// union is exact). A file's rows are then kept only when their prefix is that code.</item>
/// <item><b>An isoform with no reference of its own</b> takes its entry's whole-entry rows, flagged inherited. Here: over
/// the union, through <c>reactome_membership.isoform_of</c> (mzLib's accession parser), so an isoform held in another file
/// than its entry inherits as it does over the whole set. The rows a file inherited within itself are left out, and every
/// entry's status is read again over the union: <c>species_not_covered</c> only when no file of the search gives its
/// organism any reference.</item>
/// </list>
/// <para><b>Exact, or refused.</b> The per-file artefacts give the same rows as mzLib over the whole set except in two
/// cases they cannot see into, and a check fails the build in each rather than guess: an organism whose code one file
/// read while another file's references of other prefixes could change the set's majority (the code file's dropped count
/// is an upper bound), and an isoform that inherits here but whose file's species check dropped references, its entry
/// being in another file (it may have had references of its own). Neither arises in a UniProt proteome.</para>
/// <para><b>The member join is the definition's "Per member" and "Group level" text, with equality joins only</b> (mzLib's
/// <c>TestReactomePerDatabaseTables.JoinMembers</c> and <c>JoinGroup</c>, ported to SQL): a member takes its entry's
/// whole-entry rows (on <c>annotated_as</c>) and every isoform row whose <c>sequence_accession</c> is its own, DISTINCT. It
/// reads <c>no_entry</c> when the member resolution says so, <c>annotated</c> when it takes a pathway, and otherwise its
/// entry's status, except that an entry reading <c>annotated</c> gives <c>no_reference</c> (kegg D20: its references are
/// scoped to other isoforms). A group reads the highest of <c>annotated &gt; contaminant &gt; no_entry &gt;
/// species_not_covered &gt; no_reference</c>, over one member set for everything; entrapment is a label, never a count
/// change. Ancestors are not expanded: join a pathway to <c>reactome_ancestors</c> to roll it up.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    public const string ReactomePathwaysTable = "reactome_pathways";
    public const string ReactomeHierarchyTable = "reactome_hierarchy";
    public const string ReactomeAncestorsTable = "reactome_ancestors";
    public const string ReactomeMembershipTable = "reactome_membership";
    public const string ReactomeSpeciesTable = "reactome_species";
    public const string KeggGeneLinksTable = "kegg_gene_links";
    public const string ProteinReactomeTable = "protein_reactome";
    public const string ReactomeGroupStatusTable = "reactome_group_status";
    public const string ReactomeGroupPathwaysTable = "reactome_group_pathways";

    /// <summary>Reactome's top-level "Disease" pathway. Disease is never inferred to another species (kegg's definition,
    /// "Cross-species"), so a pathway at or under it is flagged and kept out of cross-species comparison.</summary>
    public const string ReactomeDiseaseRoot = "R-HSA-1643685";

    /// <summary>KEGG's documented entry link (https://www.kegg.jp/kegg/docs/weblinks.html); nothing calls it.</summary>
    public const string KeggEntryUrl = "https://www.kegg.jp/entry/";

    /// <summary>The organism the definition's species rule gives the entries that name no NCBI taxon, which are one
    /// organism within a search (kegg D19).</summary>
    public const string ReactomeUnknownOrganism = "unknown";

    /// <summary>The Reactome tables every catalog of the current rules holds, in the order they are built: six copied
    /// from kegg's artefacts and three derived.</summary>
    public static readonly IReadOnlyList<string> ReactomeTables =
    [
        ReactomePathwaysTable, ReactomeHierarchyTable, ReactomeAncestorsTable, ReactomeMembershipTable, ReactomeSpeciesTable,
        KeggGeneLinksTable, ProteinReactomeTable, ReactomeGroupStatusTable, ReactomeGroupPathwaysTable,
    ];

    /// <summary>The kind <c>catalog_tables</c> gives a Reactome table: the engine's for an artefact's own rows,
    /// <c>derived</c> for what the build joins from them.</summary>
    public static string ReactomeTableKind(string table) => table switch
    {
        ReactomePathwaysTable or ReactomeHierarchyTable or ReactomeAncestorsTable => $"engine:{Runner.ReactomeRelease}",
        ReactomeMembershipTable or ReactomeSpeciesTable or KeggGeneLinksTable => $"engine:{Runner.ReactomeMembership}",
        _ => "derived",
    };

    /// <summary>kegg's group statuses, highest first (the definition's "Group level" ladder).</summary>
    public static readonly IReadOnlyList<string> ReactomeGroupStatuses = ["annotated", "contaminant", "no_entry", "species_not_covered", "no_reference"];

    /// <summary>Whether this build serves Reactome: whenever it serves GO, whose member resolution it reads.</summary>
    private static bool ReactomeActive => GoActive;

    /// <summary>Each Reactome table's columns and types, so an empty table has the shape a filled one has.</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string Type)[]> ReactomeColumns =
        new Dictionary<string, (string Name, string Type)[]>(StringComparer.Ordinal)
        {
            [ReactomePathwaysTable] =
            [
                ("pathway_id", "VARCHAR"), ("name", "VARCHAR"), ("species_name", "VARCHAR"), ("human_equivalent_id", "VARCHAR"),
                ("in_disease", "BOOLEAN"), ("reactome_release", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [ReactomeHierarchyTable] = [("parent_id", "VARCHAR"), ("child_id", "VARCHAR"), ("artefact_id", "VARCHAR")],
            [ReactomeAncestorsTable] = [("pathway_id", "VARCHAR"), ("ancestor_pathway_id", "VARCHAR"), ("artefact_id", "VARCHAR")],
            [ReactomeMembershipTable] =
            [
                ("database_sha256", "VARCHAR"), ("accession", "VARCHAR"), ("organism", "VARCHAR"), ("pathway_id", "VARCHAR"),
                ("pathway_species", "VARCHAR"), ("molecule", "VARCHAR"), ("sequence_accession", "VARCHAR"), ("inherited", "BOOLEAN"),
                ("status", "VARCHAR"), ("isoform_of", "VARCHAR"), ("definition_id", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [ReactomeSpeciesTable] =
            [
                ("database_sha256", "VARCHAR"), ("organism", "VARCHAR"), ("species_code", "VARCHAR"),
                ("unvalidated_references", "BIGINT"), ("artefact_id", "VARCHAR"),
            ],
            [KeggGeneLinksTable] =
            [
                ("database_sha256", "VARCHAR"), ("accession", "VARCHAR"), ("kegg_gene_id", "VARCHAR"), ("kegg_url", "VARCHAR"),
                ("artefact_id", "VARCHAR"),
            ],
            [ProteinReactomeTable] =
            [
                ("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR"), ("protein_accession", "VARCHAR"), ("annotated_as", "VARCHAR"),
                ("entrapment", "BOOLEAN"), ("reactome_status", "VARCHAR"), ("pathway_id", "VARCHAR"), ("inherited", "BOOLEAN"),
                ("release_status", "VARCHAR"), ("annotation_db_sha256", "VARCHAR"), ("reactome_release", "VARCHAR"), ("source_id", "VARCHAR"),
            ],
            [ReactomeGroupStatusTable] =
            [
                ("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR"), ("protein_group_id", "VARCHAR"), ("protein_group", "VARCHAR"),
                ("members", "BIGINT"), ("reactome_status", "VARCHAR"), ("n_pathways", "BIGINT"), ("entrapment_members", "VARCHAR[]"),
                ("is_contaminant", "BOOLEAN"), ("q_value", "DOUBLE"), ("reactome_release", "VARCHAR"), ("source_id", "VARCHAR"),
            ],
            [ReactomeGroupPathwaysTable] =
            [
                ("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR"), ("protein_group_id", "VARCHAR"), ("pathway_id", "VARCHAR"),
                ("members", "BIGINT"), ("members_in_pathway", "BIGINT"), ("carriers", "VARCHAR[]"), ("release_status", "VARCHAR"),
                ("reactome_release", "VARCHAR"), ("source_id", "VARCHAR"),
            ],
        };

    // --- selection -----------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, object?> ReactomeSummary(ArtefactRef a) =>
        a.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();

    /// <summary>A membership artefact's species codes, from its run record (its file's <c>species_codes</c> header).</summary>
    private static IReadOnlyDictionary<string, string> SpeciesCodesOf(ArtefactRef a) =>
        (SourcesPy.Get(ReactomeSummary(a), "species_codes") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>())
            .Where(kv => kv.Value is string).ToDictionary(kv => kv.Key, kv => (string)kv.Value!, StringComparer.Ordinal);

    /// <summary>How many references a membership artefact's file dropped for a prefix other than its organism's code.</summary>
    private static long DroppedOf(ArtefactRef a) => PyInt(SourcesPy.Get(ReactomeSummary(a), "dropped_foreign_references") ?? 0L);

    /// <summary>kegg's artefacts for these bundles, the member artefacts of the searches they serve, and three coverage
    /// checks that always pass and say what is not served.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>kegg.reactome_release</c>: at most one in the store. Two are refused, never one picked: which release a
    /// catalog serves is the operator's decision, and a pathway id means one thing in it (kegg KEGG-P2, PXReprise rule 5).
    /// With none, no Reactome is served at all.</item>
    /// <item><c>kegg.reactome_membership</c>: one per target database file a bundle searched. Two for one file are refused,
    /// and so is one organism given two different Reactome codes by two served files (dataRepo KEGG-D6), naming the
    /// organism, both codes and both files. The entries that name no taxon (<c>unknown</c>, kegg D19) are one organism
    /// within a search only, so they are not compared across files.</item>
    /// <item>A bundle is served Reactome when the release, the membership of every one of its target files and its own
    /// <c>go.resolve_members</c> artefact are all there; otherwise it is named, and the build goes on.</item>
    /// </list>
    /// Artefacts written against another schema are skipped and named.
    /// </remarks>
    private static void SelectReactome(string store, IReadOnlyList<BundleRef> bundles, List<ArtefactRef> chosen, List<CatalogCheck> checks)
    {
        string Dir(string engine) => SourcesPy.PathStr(System.IO.Path.Combine(store, Runner.EngineDir, engine));
        var stale = new List<string>();
        var searches = bundles.ToDictionary(b => b.BundleId, GoInputsOf, StringComparer.Ordinal);
        List<ArtefactRef> Current(string engine, Func<ArtefactRef, bool> relevant)
        {
            var all = Runner.DiscoverArtefacts(store, engine).Where(relevant).ToList();
            stale.AddRange(all.Where(r => r.SchemaVersion != SchemaContract.Version).Select(r => $"{engine}/{r.ArtefactId} (schema {r.SchemaVersion})"));
            return all.Where(r => r.SchemaVersion == SchemaContract.Version).ToList();
        }
        static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

        var releases = Current(Runner.ReactomeRelease, _ => true);
        if (releases.Count > 1)
            throw new CatalogException(
                $"{Runner.ReactomeRelease}: {releases.Count} artefacts ({string.Join(", ", releases.Select(r => r.ArtefactId))}) hold a "
                + "Reactome release. A catalog serves one, so a pathway id means one thing in every dataset (kegg KEGG-P2). Move the "
                + $"ones it should not serve out of {Dir(Runner.ReactomeRelease)}.");
        var release = releases.SingleOrDefault();
        if (release is not null) chosen.Add(release);

        var files = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var b in bundles)
            foreach (var t in searches[b.BundleId].Targets)
            {
                if (!files.TryGetValue(t.Sha256, out var ids)) files[t.Sha256] = ids = new SortedSet<string>(StringComparer.Ordinal);
                ids.Add(b.DatasetId);
            }

        var memberships = new Dictionary<string, ArtefactRef>(StringComparer.Ordinal);
        if (release is not null)
        {
            var byFile = Current(Runner.ReactomeMembership, r => r.Inputs.GetValueOrDefault(Runner.ReactomeDatabaseRole) is { } s && files.ContainsKey(s))
                .GroupBy(r => r.Inputs[Runner.ReactomeDatabaseRole], StringComparer.Ordinal);
            foreach (var group in byFile.OrderBy(g => g.Key, SourcesPy.CodePointOrder))
            {
                var refs = group.ToList();
                if (refs.Count > 1)
                    throw new CatalogException(
                        $"{Runner.ReactomeMembership}: {refs.Count} artefacts ({string.Join(", ", refs.Select(r => r.ArtefactId))}) annotate the "
                        + $"database {Short(group.Key)}... searched by {string.Join(", ", files[group.Key])}. A catalog serves one. Move the ones "
                        + $"it should not serve out of {Dir(Runner.ReactomeMembership)}.");
                memberships[group.Key] = refs[0];
            }
            // dataRepo KEGG-D6: one organism, one Reactome code, across every served file.
            var seen = new Dictionary<string, (string Code, string File)>(StringComparer.Ordinal);
            foreach (var (sha, a) in memberships.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
                foreach (var (organism, code) in SpeciesCodesOf(a).OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
                {
                    if (organism == ReactomeUnknownOrganism) continue;
                    if (seen.TryGetValue(organism, out var first) && first.Code != code)
                        throw new CatalogException(
                            $"{Runner.ReactomeMembership}: {organism} has Reactome code {first.Code} in database {Short(first.File)}... "
                            + $"({string.Join(", ", files[first.File])}) and {code} in database {Short(sha)}... ({string.Join(", ", files[sha])}). "
                            + "One organism must carry one code in a catalog (dataRepo KEGG-D6): the two files' references disagree about "
                            + "the species, so check what was searched.");
                    seen.TryAdd(organism, (code, sha));
                }
            foreach (var a in memberships.Values.OrderBy(a => a.ArtefactId, SourcesPy.CodePointOrder)) chosen.Add(a);
        }

        var members = Current(Runner.GoResolveMembers, r => searches.Values.Any(s => GoMembersMatch(r, s)));
        var served = 0;
        var notServed = new List<string>();
        foreach (var b in bundles.OrderBy(b => b.DatasetId, SourcesPy.CodePointOrder))
        {
            var search = searches[b.BundleId];
            var matched = members.Where(r => GoMembersMatch(r, search)).ToList();
            if (matched.Count > 1)
                throw new CatalogException(
                    $"{Runner.GoResolveMembers}: {matched.Count} artefacts ({string.Join(", ", matched.Select(r => r.ArtefactId))}) resolve the "
                    + $"members of {b.DatasetId} bundle {b.BundleId}. A catalog serves one. Move the ones it should not serve out of "
                    + $"{Dir(Runner.GoResolveMembers)}.");
            var lacking = search.Targets.Where(t => !memberships.ContainsKey(t.Sha256)).Select(t => t.Name).ToList();
            string? why =
                search.ProteinGroupsSha256 is null ? "no protein-group file (an isobaric search is not annotated, GO-D14)"
                : search.Targets.Count == 0 ? "no target database"
                : release is null ? $"no {Runner.ReactomeRelease} artefact"
                : lacking.Count > 0 ? $"no {Runner.ReactomeMembership} artefact for {string.Join(", ", lacking)}"
                : matched.Count == 0 ? $"no {Runner.GoResolveMembers} artefact"
                : null;
            if (why is not null)
            {
                notServed.Add($"{b.DatasetId} ({b.BundleId}): {why}");
                continue;
            }
            served++;
            if (!chosen.Any(c => c.Engine == matched[0].Engine && c.ArtefactId == matched[0].ArtefactId)) chosen.Add(matched[0]);
        }

        string? StaleOf(string engine)
        {
            var mine = stale.Where(s => s.StartsWith(engine + "/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            return mine.Count > 0 ? "skipped, other schema: " + string.Join(", ", mine) : null;
        }
        static string? Join(params string?[] parts)
        {
            var kept = parts.Where(p => p is { Length: > 0 }).ToList();
            return kept.Count > 0 ? string.Join("; ", kept) : null;
        }
        checks.Add(new CatalogCheck($"{Runner.ReactomeRelease} (Reactome release served)", "engine-coverage", true,
            release is null ? 0 : 1, 1,
            Join(release is null ? "no artefact, so no Reactome is served" : null, StaleOf(Runner.ReactomeRelease))));
        var uncovered = files.Keys.Where(sha => !memberships.ContainsKey(sha)).ToList();
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} coverage (target databases with an artefact)", "engine-coverage", true,
            files.Count - uncovered.Count, files.Count,
            Join(uncovered.Count == 0 ? null
                    : (release is null ? "none served without a release; " : "") + "no artefact for "
                      + string.Join("; ", uncovered.Select(sha => $"{Short(sha)} ({string.Join(", ", files[sha])})")),
                StaleOf(Runner.ReactomeMembership))));
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} served (bundles served Reactome)", "engine-coverage", true,
            served, bundles.Count,
            Join(notServed.Count > 0 ? "not served: " + string.Join("; ", notServed) : null, StaleOf(Runner.GoResolveMembers))));
    }

    // --- the build -----------------------------------------------------------------------------------------

    /// <summary>One served search, as the build derives its rows.</summary>
    /// <param name="DatabaseSha256s">Every target database file the search read: the files its rows are read from.</param>
    /// <param name="MembersPath">The search's <c>go.resolve_members</c> Parquet: the entry each member takes.</param>
    internal sealed record ReactomeSearch(string DatasetId, string BundleId, string AnnotationDbSha256, IReadOnlyList<string> DatabaseSha256s,
        string MembersPath, string SourceId, string SourceVersion, string ReactomeRelease);

    /// <summary>What a build serves: the release artefact, the membership artefacts by file, and the served searches.</summary>
    private sealed record ReactomePlan(ArtefactRef? Release, IReadOnlyDictionary<string, ArtefactRef> Memberships,
        IReadOnlyList<(BundleRef Bundle, ReactomeSearch Search, ArtefactRef Members)> Served);

    private static string ReactomeReleaseOf(ArtefactRef release) => SourcesPy.Get(ReactomeSummary(release), "reactome_release") as string ?? "";

    private static string ArtefactTable(ArtefactRef a, string table) => a.TablePath(table)
        ?? throw new CatalogException($"{a.Engine}/{a.ArtefactId}: {a.Path} holds no {table}.parquet. Run the engine again.");

    /// <summary>The served searches among these bundles: those with a membership artefact for each target file and
    /// exactly one member artefact, under one release artefact.</summary>
    /// <exception cref="CatalogException">Two release artefacts, or two artefacts for one file or one search:
    /// <see cref="SelectReactome"/> refuses the same, and this refuses artefacts handed to the build some other way.</exception>
    private static ReactomePlan ReactomePlanOf(IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var releases = artefacts.Where(a => a.Engine == Runner.ReactomeRelease).ToList();
        if (releases.Count > 1)
            throw new CatalogException($"{Runner.ReactomeRelease}: {releases.Count} artefacts given. A catalog serves one Reactome release.");
        var release = releases.SingleOrDefault();
        if (release is null) return new ReactomePlan(null, new Dictionary<string, ArtefactRef>(), []);
        var byFile = new Dictionary<string, ArtefactRef>(StringComparer.Ordinal);
        foreach (var a in artefacts.Where(a => a.Engine == Runner.ReactomeMembership))
            if (!byFile.TryAdd(a.Inputs.GetValueOrDefault(Runner.ReactomeDatabaseRole) ?? "", a))
                throw new CatalogException($"{Runner.ReactomeMembership}: two artefacts given for one database ({byFile[a.Inputs[Runner.ReactomeDatabaseRole]].ArtefactId}, {a.ArtefactId}). A catalog serves one.");
        var number = ReactomeReleaseOf(release);
        var served = new List<(BundleRef, ReactomeSearch, ArtefactRef)>();
        foreach (var b in bundles.OrderBy(b => b.DatasetId, SourcesPy.CodePointOrder))
        {
            var search = GoInputsOf(b);
            if (search.Targets.Count == 0 || search.Targets.Any(t => !byFile.ContainsKey(t.Sha256))) continue;
            var matched = artefacts.Where(a => GoMembersMatch(a, search)).ToList();
            if (matched.Count == 0) continue;
            if (matched.Count > 1)
                throw new CatalogException($"{Runner.GoResolveMembers}: {matched.Count} artefacts given for {b.DatasetId} bundle {b.BundleId}. A catalog serves one.");
            var used = new[] { release }.Concat(search.Targets.Select(t => byFile[t.Sha256]).DistinctBy(a => a.ArtefactId)).Append(matched[0]).ToList();
            var key = string.Concat(used.Select(a => $"{a.Engine}\t{a.ArtefactId}\n").Order(StringComparer.Ordinal));
            var sourceId = $"reactome:{number}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("reactome-source/1\n" + key)))[..12]}";
            var mzlib = used.Select(a => SourcesPy.Get(SourcesPy.Get(a.Record, "release") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>(), "mzlib") as string)
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var set = search.AnnotationDbSha256!;
            var version = $"Reactome release {number}; mzLib {string.Join(", ", mzlib)}; {release.Record.GetValueOrDefault("definition_id")}; "
                + $"annotation db {set}; artefacts {string.Join(", ", used.Select(a => $"{a.Engine}/{a.ArtefactId}"))}";
            served.Add((b, new ReactomeSearch(b.DatasetId, b.BundleId, set, search.Targets.Select(t => t.Sha256).Distinct(StringComparer.Ordinal).ToList(),
                ArtefactTable(matched[0], GoGroupMembersTable), sourceId, version, number), matched[0]));
        }
        // Every membership artefact given is served, a search's or not: a database's membership is reference data.
        return new ReactomePlan(release, byFile, served);
    }

    /// <summary>Creates every Reactome table typed and empty.</summary>
    internal static void CreateReactomeTables(DuckDBConnection con)
    {
        foreach (var table in ReactomeTables)
            Exec(con, $"CREATE TABLE \"{table}\" ({string.Join(", ", ReactomeColumns[table].Select(c => $"\"{c.Name}\" {c.Type}"))})");
    }

    /// <summary>The release artefact's three tables into the catalog, the release named on every pathway, and each
    /// pathway's <c>in_disease</c> from the release's own closure.</summary>
    internal static void LoadReactomeRelease(DuckDBConnection con, string pathwaysPath, string hierarchyPath, string ancestorsPath,
        string reactomeRelease, string artefactId)
    {
        var aid = Quote(artefactId);
        Exec(con, $"INSERT INTO {ReactomeHierarchyTable} BY NAME SELECT parent_id, child_id, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(hierarchyPath))})");
        Exec(con, $"INSERT INTO {ReactomeAncestorsTable} BY NAME SELECT pathway_id, ancestor_pathway_id, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(ancestorsPath))})");
        Exec(con, $@"INSERT INTO {ReactomePathwaysTable} BY NAME
            SELECT p.pathway_id, p.name, p.species_name, p.human_equivalent_id,
                   p.pathway_id IN (SELECT pathway_id FROM {ReactomeAncestorsTable} WHERE artefact_id = {aid} AND ancestor_pathway_id = {Quote(ReactomeDiseaseRoot)}) AS in_disease,
                   {Quote(reactomeRelease)} AS reactome_release, {aid} AS artefact_id
            FROM read_parquet({Quote(AsPosix(pathwaysPath))}) p");
    }

    /// <summary>One membership artefact's three tables into the catalog, its file's sha256 on every row.</summary>
    internal static void LoadReactomeMembership(DuckDBConnection con, string membershipPath, string linksPath, string speciesPath,
        string databaseSha256, string artefactId)
    {
        var (db, aid) = (Quote(databaseSha256), Quote(artefactId));
        Exec(con, $"INSERT INTO {ReactomeMembershipTable} BY NAME SELECT {db} AS database_sha256, accession, organism, pathway_id, pathway_species, "
            + $"molecule, sequence_accession, inherited, status, isoform_of, definition_id, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(membershipPath))})");
        Exec(con, $"INSERT INTO {KeggGeneLinksTable} BY NAME SELECT {db} AS database_sha256, accession, kegg_gene_id, "
            + $"{Quote(KeggEntryUrl)} || kegg_gene_id AS kegg_url, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(linksPath))})");
        Exec(con, $"INSERT INTO {ReactomeSpeciesTable} BY NAME SELECT {db} AS database_sha256, organism, species_code, "
            + $"unvalidated_references, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(speciesPath))})");
    }

    /// <summary>The join every derived Reactome row of one search comes from, in SQL with equality joins only.
    /// <c>{groups}</c> is the search's groups (<see cref="GoGroupsOf"/>), <c>{members}</c> its member artefact's Parquet
    /// and <c>{files}</c> its target files' sha256s, as literals.</summary>
    /// <remarks>
    /// <para>The set the search loaded, from its files (mzLib's <c>ReactomeAccessionAnnotation</c> over all of them):</para>
    /// <list type="number">
    /// <item><c>own_raw</c>: each entry's own references in every file (the rows a file inherited within itself left out),
    /// an entry in two files unioned. <c>setcode</c>: each organism's code, a file's where a file read one, else the
    /// definition's rule over the union (a strict majority of at least 10, each (entry, pathway) once). <c>own</c>: the
    /// references whose prefix is that code, or all of them when the organism has none.</item>
    /// <item><c>eff</c>: the rows each entry takes: its own, and for a UniProt isoform with no reference of its own in any
    /// file (<c>has_own</c>, which also counts one a file's species check removed beside its entry), its entry's
    /// whole-entry rows, inherited. <c>entry</c>: its status over the set: <c>annotated</c> with a row, else
    /// <c>no_reference</c> when any file gives its organism a reference, else <c>species_not_covered</c>.</item>
    /// </list>
    /// <para>Then the member join (mzLib's <c>JoinMembers</c>, kegg's "Per member"):</para>
    /// <list type="number">
    /// <item><c>mem</c>: each distinct member of the search's groups, with its member-resolution row (none when the member
    /// artefact lacks it, which a check refuses).</item>
    /// <item><c>carried</c>: each (member, pathway) it takes: its entry's whole-entry rows (<c>annotated_as</c>,
    /// <c>sequence_accession</c> NULL) and every isoform row of the set whose <c>sequence_accession</c> is its own, DISTINCT,
    /// <c>inherited</c> when every way it takes the pathway is an inherited row. A <c>no_entry</c> member takes none.</item>
    /// <item><c>status</c>: <c>no_entry</c> as the member resolution says; <c>annotated</c> when it takes a pathway;
    /// otherwise its entry's status, <c>annotated</c> giving <c>no_reference</c> (D20).</item>
    /// </list>
    /// </remarks>
    private const string ReactomeCarried = @"
        grp AS ({groups}),
        gm AS (SELECT group_key, unnest(members) AS member FROM grp),
        res AS (SELECT accession, annotated_as, resolution, entrapment, sequence_accession FROM read_parquet({members})),
        db AS (
            SELECT database_sha256, accession, organism, coalesce(organism, 'unknown') AS org, pathway_id, pathway_species,
                   sequence_accession, inherited, isoform_of
            FROM reactome_membership WHERE database_sha256 IN ({files})
        ),
        own_raw AS (
            SELECT DISTINCT accession, org, pathway_id, pathway_species, sequence_accession FROM db
            WHERE pathway_id IS NOT NULL AND NOT inherited
        ),
        filecodes AS (
            SELECT DISTINCT organism AS org, species_code FROM reactome_species
            WHERE database_sha256 IN ({files}) AND species_code IS NOT NULL
        ),
        refs AS (SELECT DISTINCT org, accession, pathway_id, pathway_species FROM own_raw),
        counted AS (
            SELECT org, pathway_species, count(*) AS n, sum(count(*)) OVER (PARTITION BY org) AS total FROM refs GROUP BY org, pathway_species
        ),
        setcode AS (
            SELECT org, species_code FROM filecodes
            UNION ALL
            SELECT org, pathway_species FROM counted
            WHERE total >= 10 AND 2 * n > total AND pathway_species IS NOT NULL AND org NOT IN (SELECT org FROM filecodes)
        ),
        own AS (
            SELECT DISTINCT r.accession, r.pathway_id, r.sequence_accession FROM own_raw r LEFT JOIN setcode s ON s.org = r.org
            WHERE s.species_code IS NULL OR r.pathway_species = s.species_code
        ),
        entries AS (SELECT accession, coalesce(min(organism), 'unknown') AS org, min(isoform_of) AS isoform_of FROM db GROUP BY accession),
        own_whole AS (SELECT DISTINCT accession, pathway_id FROM own WHERE sequence_accession IS NULL),
        dropped_own AS (
            SELECT DISTINCT i.accession FROM db i JOIN db e ON e.database_sha256 = i.database_sha256 AND e.accession = i.isoform_of
            WHERE i.pathway_id IS NULL AND e.pathway_id IS NOT NULL AND e.sequence_accession IS NULL AND NOT e.inherited
        ),
        has_own AS (SELECT accession FROM own_raw UNION SELECT accession FROM dropped_own),
        eff AS (
            SELECT accession, pathway_id, sequence_accession, false AS inherited FROM own
            UNION ALL
            SELECT e.accession, w.pathway_id, NULL AS sequence_accession, true AS inherited
            FROM entries e JOIN own_whole w ON w.accession = e.isoform_of
            WHERE e.isoform_of IS NOT NULL AND e.accession NOT IN (SELECT accession FROM has_own)
        ),
        entry AS (
            SELECT e.accession,
                   CASE WHEN e.accession IN (SELECT accession FROM eff) THEN 'annotated'
                        WHEN e.org IN (SELECT org FROM own_raw) THEN 'no_reference' ELSE 'species_not_covered' END AS status
            FROM entries e
        ),
        mem AS (
            SELECT d.member, r.annotated_as, r.resolution, r.entrapment, r.sequence_accession
            FROM (SELECT DISTINCT member FROM gm) d LEFT JOIN res r ON r.accession = d.member
        ),
        carried AS (
            SELECT member, pathway_id, bool_and(inherited) AS inherited FROM (
                SELECT m.member, f.pathway_id, f.inherited FROM mem m JOIN eff f ON f.accession = m.annotated_as
                WHERE m.resolution IN ('self', 'inherited') AND f.sequence_accession IS NULL
                UNION ALL
                SELECT m.member, f.pathway_id, f.inherited FROM mem m JOIN eff f ON f.sequence_accession = m.sequence_accession
                WHERE m.resolution IN ('self', 'inherited')
            ) GROUP BY member, pathway_id
        ),
        status AS (
            SELECT m.member, m.annotated_as, m.entrapment,
                   CASE WHEN m.resolution IS NULL THEN NULL
                        WHEN m.resolution = 'no_entry' THEN 'no_entry'
                        WHEN coalesce(n.carried, 0) > 0 THEN 'annotated'
                        WHEN e.status = 'annotated' THEN 'no_reference'
                        ELSE e.status END AS reactome_status
            FROM mem m
            LEFT JOIN (SELECT member, count(*) AS carried FROM carried GROUP BY member) n ON n.member = m.member
            LEFT JOIN entry e ON e.accession = m.annotated_as
        )";

    /// <summary><c>protein_reactome</c>: per member, a row per pathway it takes, or one pathway-less row giving its
    /// status; <c>release_status</c> says whether the served release has the pathway (kegg's drift rule).</summary>
    private const string ProteinReactomeSelect = @"
        SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, s.member AS protein_accession, s.annotated_as, s.entrapment,
               s.reactome_status, k.pathway_id, k.inherited,
               CASE WHEN k.pathway_id IS NULL THEN NULL WHEN p.pathway_id IS NULL THEN 'not_in_release' ELSE 'in_release' END AS release_status,
               {set} AS annotation_db_sha256, {release} AS reactome_release, {source} AS source_id
        FROM status s
        LEFT JOIN carried k ON k.member = s.member
        LEFT JOIN reactome_pathways p ON p.pathway_id = k.pathway_id
        ORDER BY protein_accession, pathway_id NULLS FIRST";

    /// <summary><c>reactome_group_status</c>: every group of the search exactly once, with the highest status on kegg's
    /// ladder over one member set: <c>annotated</c> when any member takes a pathway, else <c>contaminant</c> when the
    /// group is marked, else the highest of its members' <c>no_entry</c>, <c>species_not_covered</c>,
    /// <c>no_reference</c>.</summary>
    private const string ReactomeStatusSelect = @"
        SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, g.group_key AS protein_group_id, g.protein_group,
               len(g.members) AS members,
               CASE WHEN coalesce(c.n_pathways, 0) > 0 THEN 'annotated' WHEN g.is_contaminant THEN 'contaminant'
                    WHEN coalesce(m.any_no_entry, false) THEN 'no_entry'
                    WHEN coalesce(m.any_not_covered, false) THEN 'species_not_covered' ELSE 'no_reference' END AS reactome_status,
               coalesce(c.n_pathways, 0) AS n_pathways, coalesce(m.entrapment_members, []::VARCHAR[]) AS entrapment_members,
               g.is_contaminant, g.q_value, {release} AS reactome_release, {source} AS source_id
        FROM grp g
        LEFT JOIN (
            SELECT gm.group_key, count(DISTINCT k.pathway_id) AS n_pathways FROM gm JOIN carried k ON k.member = gm.member GROUP BY gm.group_key
        ) c ON c.group_key = g.group_key
        LEFT JOIN (
            SELECT gm.group_key, bool_or(s.reactome_status = 'no_entry') AS any_no_entry,
                   bool_or(s.reactome_status = 'species_not_covered') AS any_not_covered,
                   list_sort(list(gm.member) FILTER (WHERE s.entrapment)) AS entrapment_members
            FROM gm JOIN status s ON s.member = gm.member GROUP BY gm.group_key
        ) m ON m.group_key = g.group_key
        ORDER BY g.protein_group, g.group_key";

    /// <summary><c>reactome_group_pathways</c>: per (group, pathway a member takes), the group's member count, how many of
    /// them take the pathway and which (kegg's <c>members</c> and <c>members_in_pathway</c>).</summary>
    private const string ReactomeGroupPathwaysSelect = @"
        SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, gm.group_key AS protein_group_id, k.pathway_id,
               any_value(len(g.members)) AS members, count(DISTINCT gm.member) AS members_in_pathway,
               list_sort(list_distinct(list(gm.member))) AS carriers,
               CASE WHEN any_value(p.pathway_id) IS NULL THEN 'not_in_release' ELSE 'in_release' END AS release_status,
               {release} AS reactome_release, {source} AS source_id
        FROM gm JOIN carried k ON k.member = gm.member JOIN grp g ON g.group_key = gm.group_key
        LEFT JOIN reactome_pathways p ON p.pathway_id = k.pathway_id
        GROUP BY gm.group_key, k.pathway_id
        ORDER BY protein_group_id, k.pathway_id";

    /// <summary>A query over one search's <see cref="ReactomeCarried"/> join, the placeholders filled as literals.</summary>
    internal static string ReactomeSql(ReactomeSearch search, string groups, string select) =>
        "SELECT * FROM (WITH " + ReactomeCarried
            .Replace("{groups}", groups)
            .Replace("{members}", Quote(AsPosix(search.MembersPath)))
            .Replace("{files}", string.Join(", ", search.DatabaseSha256s.Select(Quote)))
        + "\n" + select
            .Replace("{dataset}", Quote(search.DatasetId))
            .Replace("{bundle}", Quote(search.BundleId))
            .Replace("{set}", Quote(search.AnnotationDbSha256))
            .Replace("{source}", Quote(search.SourceId))
            .Replace("{release}", Quote(search.ReactomeRelease))
        + ")";

    /// <summary>One search's derived rows: <c>protein_reactome</c>, <c>reactome_group_status</c>,
    /// <c>reactome_group_pathways</c> and its <c>annotation_sources</c> row.</summary>
    /// <param name="groups">The search's groups: <c>group_key</c>, <c>protein_group</c>, <c>is_contaminant</c>,
    /// <c>q_value</c>, <c>members</c> (<see cref="GoGroupsOf"/>; a test passes its own fixture groups).</param>
    internal static void DeriveReactome(DuckDBConnection con, ReactomeSearch search, string groups)
    {
        string Fill(string select) => ReactomeSql(search, groups, select);
        Exec(con, $"INSERT INTO {ProteinReactomeTable} BY NAME " + Fill(ProteinReactomeSelect));
        Exec(con, $"INSERT INTO {ReactomeGroupStatusTable} BY NAME " + Fill(ReactomeStatusSelect));
        Exec(con, $"INSERT INTO {ReactomeGroupPathwaysTable} BY NAME " + Fill(ReactomeGroupPathwaysSelect));
        Exec(con, "INSERT INTO annotation_sources BY NAME SELECT ? AS dataset_id, ? AS bundle_id, ? AS source_id, 'kegg' AS owner_project, "
            + "? AS version, NULL AS doi", search.DatasetId, search.BundleId, search.SourceId, search.SourceVersion);
    }

    /// <summary>Every Reactome table: the reference tables copied from the served artefacts, and the derived ones joined
    /// from them, each served search's member artefact and its <c>protein_groups</c>; all typed and empty when nothing is
    /// served.</summary>
    /// <returns><c>{table: rows}</c> when a release artefact was served, else nothing, as <see cref="BuildGo"/>.</returns>
    /// <remarks>Runs after <see cref="BuildGo"/>, which creates <c>annotation_sources</c>.</remarks>
    private static Dictionary<string, long> BuildReactome(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!ReactomeActive) return counts;
        CreateReactomeTables(con);
        var plan = ReactomePlanOf(bundles, artefacts);
        if (plan.Release is not { } release) return counts;
        try
        {
            LoadReactomeRelease(con, ArtefactTable(release, ReactomePathwaysTable), ArtefactTable(release, ReactomeHierarchyTable),
                ArtefactTable(release, ReactomeAncestorsTable), ReactomeReleaseOf(release), release.ArtefactId);
            foreach (var (file, a) in plan.Memberships.OrderBy(kv => kv.Value.ArtefactId, SourcesPy.CodePointOrder))
                LoadReactomeMembership(con, ArtefactTable(a, ReactomeMembershipTable), ArtefactTable(a, KeggGeneLinksTable),
                    ArtefactTable(a, ReactomeSpeciesTable), file, a.ArtefactId);
            foreach (var (_, search, _) in plan.Served) DeriveReactome(con, search, GoGroupsOf(search.BundleId));
        }
        catch (DuckDBException e)
        {
            throw new CatalogException($"kegg's artefacts could not be read: {e.Message}");
        }
        foreach (var table in ReactomeTables) counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        counts["annotation_sources"] = Count(con, "SELECT count(*) FROM annotation_sources");
        return counts;
    }

    // --- checks --------------------------------------------------------------------------------------------

    /// <summary>The two cases in which per-file artefacts cannot give mzLib's rows over the whole set, for one served
    /// search: each organism whose code a file read, with how many of the set's references carry it and how many carry
    /// another prefix in the files that read none; and each isoform that inherits here although a file holding it, without
    /// its entry, read its organism's code (that file's species check may have dropped references of its own).</summary>
    /// <param name="groups">The search's groups (<see cref="GoGroupsOf"/>; a test passes its own).</param>
    /// <param name="droppedByFile">Each file's count of references its species check dropped (its artefact's
    /// <c>dropped_foreign_references</c>).</param>
    /// <returns>One sentence per case the build cannot make exact; empty when the search's rows are mzLib's.</returns>
    internal static List<string> ReactomeInexact(DuckDBConnection con, ReactomeSearch search, string groups, IReadOnlyDictionary<string, long> droppedByFile)
    {
        var (codes, isoforms) = ReactomeExactness(con, search, groups);
        var inexact = new List<string>();
        foreach (var row in codes)
        {
            var codedIn = (row[4] as System.Collections.IEnumerable)?.Cast<object?>().Select(PyStr).ToList() ?? [];
            var dropped = codedIn.Sum(f => droppedByFile.GetValueOrDefault(f));
            var (nCode, nOther) = (PyInt(row[2]), PyInt(row[3]));
            if (nCode <= nOther + dropped)
                inexact.Add($"{search.DatasetId}: {PyStr(row[0])}'s code {PyStr(row[1])} has {nCode} references against {nOther} of other "
                    + $"prefixes and up to {dropped} the code's files dropped, so the whole set's majority cannot be read from the files");
        }
        foreach (var row in isoforms)
        {
            var file = PyStr(row[1]);
            if (droppedByFile.GetValueOrDefault(file) > 0)
                inexact.Add($"{search.DatasetId}: {PyStr(row[0])} inherits its entry's rows, but {file[..Math.Min(12, file.Length)]}, which holds it "
                    + "without its entry, dropped references whose prefix was not its organism's code, so it may have had references of its own");
        }
        return inexact;
    }

    /// <summary>Each organism's Reactome code over one served search's files: a file's where one was read, else the
    /// definition's rule over their union (the join's <c>setcode</c>).</summary>
    internal static List<(string Organism, string Code)> ReactomeSetCodes(DuckDBConnection con, ReactomeSearch search, string groups) =>
        Query(con, ReactomeSql(search, groups, "SELECT org, species_code FROM setcode ORDER BY org"))
            .Select(r => (PyStr(r[0]), PyStr(r[1]))).ToList();

    private static (List<object?[]> Codes, List<object?[]> Isoforms) ReactomeExactness(DuckDBConnection con, ReactomeSearch search, string groups)
    {
        var codes = Query(con, ReactomeSql(search, groups, @"
            SELECT f.org, f.species_code, count(*) FILTER (WHERE r.pathway_species = f.species_code) AS n_code,
                   count(*) FILTER (WHERE r.pathway_species IS DISTINCT FROM f.species_code) AS n_other,
                   (SELECT list(DISTINCT s.database_sha256) FROM reactome_species s
                    WHERE s.database_sha256 IN ({files}) AND s.organism = f.org AND s.species_code IS NOT NULL) AS coded_in
            FROM filecodes f JOIN refs r ON r.org = f.org GROUP BY f.org, f.species_code ORDER BY f.org"
            .Replace("{files}", string.Join(", ", search.DatabaseSha256s.Select(Quote)))));
        var isoforms = Query(con, ReactomeSql(search, groups, @"
            SELECT DISTINCT e.accession, x.database_sha256 FROM entries e
            JOIN db x ON x.accession = e.accession
            JOIN reactome_species s ON s.database_sha256 = x.database_sha256 AND s.organism = e.org AND s.species_code IS NOT NULL
            WHERE e.isoform_of IS NOT NULL AND e.accession NOT IN (SELECT accession FROM has_own)
              AND e.isoform_of IN (SELECT accession FROM own_whole)
              AND NOT EXISTS (SELECT 1 FROM db y WHERE y.database_sha256 = x.database_sha256 AND y.accession = e.isoform_of)
            ORDER BY 1, 2"));
        return (codes, isoforms);
    }

    /// <summary>The Reactome tables hold every row of every served artefact and agree with each other.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Each release and membership artefact's rows are all copied (kind <c>row_count</c>).</item>
    /// <item>Every member of every non-decoy group of a served search has a status: a member with no member-resolution
    /// row would read as nothing (dataRepo 028, as for GO).</item>
    /// <item>Every entry a member takes (<c>annotated_as</c>) is in its search's files' <c>reactome_membership</c>:
    /// otherwise the member resolution and the membership were built from different databases.</item>
    /// <item>One Reactome code per organism over every served search, the codes the catalog read over a search's files
    /// included (dataRepo KEGG-D6; <c>unknown</c> excepted).</item>
    /// <item>The per-file artefacts are exact for every served search (<see cref="ReactomeExactness"/>): an organism's code
    /// a file read is the set's whenever its references outnumber every other prefix's, those the code files dropped
    /// included; and no inheriting isoform is one a file's species check may have stripped.</item>
    /// <item>No search's group is in <c>reactome_group_status</c> twice.</item>
    /// <item>Release drift (kegg's definition): how many of the served files' pathway ids the release has. It always
    /// passes: an id UniProt cites and the release lacks reads <c>not_in_release</c>, with no name or ancestors.</item>
    /// </list>
    /// </remarks>
    private static List<CatalogCheck> CheckReactomeTables(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        if (!ReactomeActive) return checks;
        var plan = ReactomePlanOf(bundles, artefacts);
        if (plan.Release is not { } release) return checks;

        foreach (var a in new[] { release }.Concat(plan.Memberships.Values.OrderBy(a => a.ArtefactId, SourcesPy.CodePointOrder)))
            foreach (var (table, expected) in a.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var observed = Count(con, $"SELECT count(*) FROM \"{table}\" WHERE artefact_id = ?", a.ArtefactId);
                checks.Add(new CatalogCheck($"{a.Engine}/{a.ArtefactId}/{table}", "row_count", observed == expected, observed, expected,
                    $"engine artefact {a.ArtefactId}"));
            }

        var number = ReactomeReleaseOf(release);
        var cited = $"SELECT DISTINCT database_sha256, pathway_id FROM {ReactomeMembershipTable} WHERE pathway_id IS NOT NULL";
        var inRelease = Count(con, $"SELECT count(*) FROM ({cited}) m WHERE m.pathway_id IN (SELECT pathway_id FROM {ReactomePathwaysTable})");
        var total = Count(con, $"SELECT count(*) FROM ({cited})");
        var drift = Query(con, $@"
            SELECT database_sha256, count(*), min(pathway_id) FROM ({cited}) m
            WHERE m.pathway_id NOT IN (SELECT pathway_id FROM {ReactomePathwaysTable})
            GROUP BY database_sha256 ORDER BY database_sha256");
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} drift (pathway ids of each database in Reactome release {number})", "engine-coverage", true,
            inRelease, total, drift.Count == 0 ? null
                : "not_in_release: " + string.Join("; ", drift.Select(r => $"{PyStr(r[0])[..12]}: {PyInt(r[1])}, e.g. {PyStr(r[2])}"))));

        if (plan.Served.Count == 0) return checks;
        var served = string.Join(", ", plan.Served.Select(s => Quote(s.Bundle.BundleId)));
        var coverage = Query(con, $@"
            WITH members AS (
                SELECT DISTINCT dataset_id, bundle_id, accession FROM (
                    SELECT dataset_id, bundle_id, unnest(protein_accessions) AS accession FROM protein_groups
                    WHERE bundle_id IN ({served}) AND target_decoy IS DISTINCT FROM 'decoy')
                WHERE coalesce(accession, '') <> ''),
            answered AS (SELECT DISTINCT bundle_id, protein_accession FROM {ProteinReactomeTable} WHERE reactome_status IS NOT NULL)
            SELECT count(*), count(a.protein_accession), min(m.dataset_id || ': ' || m.accession) FILTER (WHERE a.protein_accession IS NULL)
            FROM members m LEFT JOIN answered a ON a.bundle_id = m.bundle_id AND a.protein_accession = m.accession")[0];
        var (wanted, answeredCount) = (PyInt(coverage[0]), PyInt(coverage[1]));
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} members (a {ProteinReactomeTable} status for every member of a served search's groups)",
            "engine-consistency", answeredCount == wanted, answeredCount, wanted,
            answeredCount == wanted
                ? $"{wanted} member accession(s) of {plan.Served.Count} search(es)"
                : $"no status for {wanted - answeredCount} member(s), e.g. {PyStr(coverage[2])}: the {Runner.GoResolveMembers} artefact was not made from these protein groups"));

        var files = string.Join(", ", plan.Served.SelectMany(s => s.Search.DatabaseSha256s.Select(f => $"({Quote(s.Bundle.BundleId)}, {Quote(f)})")));
        var (missing, missingExample) = CountAndExample(con, $@"
            WITH files(bundle_id, database_sha256) AS (VALUES {files})
            SELECT count(*), min(r.dataset_id || ': ' || r.protein_accession || ' -> ' || r.annotated_as) FROM (
                SELECT DISTINCT dataset_id, bundle_id, protein_accession, annotated_as FROM {ProteinReactomeTable} WHERE annotated_as IS NOT NULL) r
            WHERE NOT EXISTS (
                SELECT 1 FROM files f JOIN {ReactomeMembershipTable} m ON m.database_sha256 = f.database_sha256
                WHERE f.bundle_id = r.bundle_id AND m.accession = r.annotated_as)");
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} entries (every annotated_as in its search's {ReactomeMembershipTable})", "engine-consistency",
            missing == 0, missing, 0, missing == 0 ? null
                : $"e.g. {PyStr(missingExample)}: the member resolution and the membership were built from different databases"));

        // KEGG-D6 over the codes the build read too, and the two exactness conditions, one served search at a time.
        var codesByOrganism = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var inexact = new List<string>();
        var droppedByFile = plan.Memberships.ToDictionary(kv => kv.Key, kv => DroppedOf(kv.Value), StringComparer.Ordinal);
        foreach (var (_, search, _) in plan.Served)
        {
            var groups = GoGroupsOf(search.BundleId);
            foreach (var (organism, code) in ReactomeSetCodes(con, search, groups))
            {
                if (organism == ReactomeUnknownOrganism) continue;
                if (!codesByOrganism.TryGetValue(organism, out var set)) codesByOrganism[organism] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(code);
            }
            inexact.AddRange(ReactomeInexact(con, search, groups, droppedByFile));
        }
        var conflicts = codesByOrganism.Where(kv => kv.Value.Count > 1).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}").ToList();
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} species (one Reactome code per organism, dataRepo KEGG-D6)", "engine-consistency",
            conflicts.Count == 0, conflicts.Count, 0, conflicts.Count == 0 ? null : string.Join("; ", conflicts)));
        checks.Add(new CatalogCheck($"{Runner.ReactomeMembership} exactness (per-file artefacts give the whole set's rows)", "engine-consistency",
            inexact.Count == 0, inexact.Count, 0, inexact.Count == 0 ? null : string.Join("; ", inexact.Take(5))
                + (inexact.Count > 5 ? $"; and {inexact.Count - 5} more" : "")
                + ". The definition reads a search's whole set of files, and these per-file artefacts cannot give its rows here: the build refuses rather than guess."));

        var (duplicates, example) = CountAndExample(con,
            $"SELECT count(*), min(k) FROM (SELECT min(protein_group_id) AS k FROM {ReactomeGroupStatusTable} GROUP BY bundle_id, protein_group_id HAVING count(*) > 1)");
        checks.Add(new CatalogCheck($"{ReactomeGroupStatusTable} (bundle_id, protein_group_id)", "engine-unique", duplicates == 0, duplicates, 0,
            duplicates == 0 ? null : $"e.g. {PyStr(example)}"));
        return checks;
    }
}
