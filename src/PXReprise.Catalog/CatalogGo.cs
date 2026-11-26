using System.Security.Cryptography;
using System.Text;
using PXReprise.Bundle;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;

namespace PXReprise.Catalog;

/// <summary>One database file a search read, from its bundle's <c>sources</c>: the file name its role carries, its path
/// and its sha256.</summary>
public sealed record GoDatabaseFile(string Name, string Path, string Sha256);

/// <summary>What go's engines read of one bundle: its protein-group file (null when the search wrote none, as an isobaric
/// one does, GO-D14) and its searched databases by role.</summary>
/// <param name="Targets">The target databases, in file-name order: the annotation databases (GO-D5).</param>
/// <param name="Contaminants">The contaminant panels the search also read, which are never annotation input.</param>
public sealed record GoSearchInputs(string? ProteinGroupsPath, string? ProteinGroupsSha256,
    IReadOnlyList<GoDatabaseFile> Targets, IReadOnlyList<GoDatabaseFile> Contaminants)
{
    /// <summary>go's <c>annotation_db_sha256</c> for this search (GO-D6), or null with no target database.</summary>
    public string? AnnotationDbSha256 => Targets.Count == 0 ? null : CatalogBuilder.AnnotationDbSha256(Targets.Select(t => t.Sha256).ToList());
}

/// <summary>GO as repository-wide reference data: stored once per database file and per release, never per bundle, and
/// joined by the catalog (catalog format 17; go's agreed shape, go 012 P1-P4, dataRepo 028, mzLib #1497).</summary>
/// <remarks>
/// <para><b>Orthology's pattern</b> (<c>CatalogOrthology.cs</c>), applied to go's three engines:</para>
/// <list type="number">
/// <item><b>One copy per reference release, never per bundle.</b> <c>go.annotate_databases</c> writes one database
/// file's direct GO terms per entry, once per (file, go.obo, mzLib, definition), shared by every search that read the
/// file. <c>go.categorize_terms</c> writes one go.obo's terms and closure and one consumer map's categories, and a catalog
/// serves exactly one (GO-D9). Only <c>go.resolve_members</c> is per search, and it holds no GO: which database entry each
/// protein-group member takes.</item>
/// <item><b>Reference tables, always there.</b> <c>go_accession_terms</c>, <c>go_terms</c>, <c>go_closure</c>,
/// <c>go_term_categories</c> and <c>go_group_members</c> are copied from the artefacts (kind <c>engine:&lt;engine&gt;</c>,
/// <c>artefact_id</c> on every row) in both layouts, typed and empty when none is served.</item>
/// <item><b>One derived bridge, in which every refusal is a row.</b> <c>go_group_status</c> gives every non-decoy group of
/// a served search exactly one of go's four statuses (D19), so a group with no <c>protein_localizations</c> row says why;
/// <c>go_group_members.resolution</c> says per member whether it is in the search's databases (<c>no_entry</c>).</item>
/// <item><b>Checks</b> that fail the build: every member of a served search's groups has a <c>go_group_members</c> row
/// (dataRepo 028), every entry a member takes is in the search's databases, every term of the database tables is in the
/// release's term table, one go.obo for every artefact (GO-D9), and each artefact's rows are all there.</item>
/// </list>
/// <para><b>The group rows are a join, so <c>go:DEF-GROUP-GO-ANNOTATION v1</c> keeps its id</b> (go's DEFINITIONS.md,
/// "Computing v1 rows from the stored tables"; mzLib's <c>TestGoPerDatabaseTables.JoinView</c>, ported to SQL): each
/// member to its resolution, the entry it takes to its direct terms, each direct term through the closure. The three core
/// tables go's per-bundle artefacts used to fill, <c>protein_localizations</c>, <c>organelle_term_categories</c> and
/// <c>annotation_sources</c>, keep their columns and their rows and are derived here (kind <c>derived</c>). go's fixture
/// <c>go-view-v1-a83d1c63</c> is reproduced row for row (<c>GoCatalogTests</c>).</para>
/// <para><b>Per file, not per search's set of files, and still exact.</b> <c>GoAccessionAnnotation</c> gives a UniProt
/// isoform with no GO of its own its entry's terms (D37) only from the proteins it is given, so a file holding isoforms
/// alone gives them term-less rows when the entry is in another file. <c>MemberResolver</c> does not carry that case: a
/// member the databases hold is <c>self</c>. So the join reads each entry's OWN terms (<c>inherited = false</c>) over
/// every file the search read, unioned as the annotator unions an accession present twice, and applies D37 over that
/// union through <c>go_accession_terms.isoform_of</c>, which the engine filled with mzLib's own accession parser. Equality
/// joins only: no accession grammar sits in SQL. The result equals <c>GoGroupAnnotator</c> over all the search's target
/// proteins (<c>GoCatalogTests</c> checks an isoform in another file than its entry).</para>
/// <para><b>Derived per search, materialised in both layouts.</b> The derived rows are computed one served search at a
/// time, so nothing larger than one search's join is held, and nothing TEMP is created.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    public const string GoAccessionTermsTable = "go_accession_terms";
    public const string GoTermsTable = "go_terms";
    public const string GoClosureTable = "go_closure";
    public const string GoTermCategoriesTable = "go_term_categories";
    public const string GoGroupMembersTable = "go_group_members";
    public const string GoGroupStatusTable = "go_group_status";

    /// <summary>The GO tables every catalog of the current rules holds beside the three core ones, in the order they are
    /// built: five copied from go's artefacts and the derived <c>go_group_status</c>.</summary>
    public static readonly IReadOnlyList<string> GoReferenceTables =
        [GoTermsTable, GoClosureTable, GoTermCategoriesTable, GoAccessionTermsTable, GoGroupMembersTable, GoGroupStatusTable];

    /// <summary>The kind <c>catalog_tables</c> gives a GO table: the engine's for an artefact's own rows, <c>derived</c> for
    /// what the build joins from them (the three core tables among them).</summary>
    public static string GoTableKind(string table) => table switch
    {
        GoTermsTable or GoClosureTable or GoTermCategoriesTable => $"engine:{Runner.GoCategorizeTerms}",
        GoAccessionTermsTable => $"engine:{Runner.GoAnnotateDatabases}",
        GoGroupMembersTable => $"engine:{Runner.GoResolveMembers}",
        _ => "derived",
    };

    /// <summary>The aspect <c>protein_localizations</c> holds (GO-CC only).</summary>
    private const string CellularComponent = "cellular_component";

    /// <summary>go's four group statuses, in D19's precedence.</summary>
    public static readonly IReadOnlyList<string> GoStatuses = ["annotated", "contaminant", "no_entry", "no_go_terms"];

    /// <summary>Each GO table's columns and types, so an empty table has the shape a filled one has.</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string Type)[]> GoColumns =
        new Dictionary<string, (string Name, string Type)[]>(StringComparer.Ordinal)
        {
            [GoTermsTable] = [("go_id", "VARCHAR"), ("go_name", "VARCHAR"), ("aspect", "VARCHAR"), ("obsolete", "BOOLEAN"), ("go_release", "VARCHAR"), ("artefact_id", "VARCHAR")],
            [GoClosureTable] = [("go_id", "VARCHAR"), ("ancestor_go_id", "VARCHAR"), ("artefact_id", "VARCHAR")],
            [GoTermCategoriesTable] =
            [
                ("go_id", "VARCHAR"), ("category", "VARCHAR"), ("subcategory", "VARCHAR"), ("category_map_name", "VARCHAR"),
                ("category_map_version", "VARCHAR"), ("go_release", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [GoAccessionTermsTable] =
            [
                ("database_sha256", "VARCHAR"), ("accession", "VARCHAR"), ("go_id", "VARCHAR"), ("aspect", "VARCHAR"),
                ("evidence", "VARCHAR"), ("inherited", "BOOLEAN"), ("isoform_of", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [GoGroupMembersTable] =
            [
                ("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR"), ("accession", "VARCHAR"), ("annotated_as", "VARCHAR"),
                ("resolution", "VARCHAR"), ("entrapment", "BOOLEAN"), ("sequence_accession", "VARCHAR"),
                ("annotation_db_sha256", "VARCHAR"), ("artefact_id", "VARCHAR"),
            ],
            [GoGroupStatusTable] =
            [
                ("dataset_id", "VARCHAR"), ("bundle_id", "VARCHAR"), ("protein_group_id", "VARCHAR"), ("protein_group", "VARCHAR"),
                ("n_members", "BIGINT"), ("annotation_status", "VARCHAR"), ("n_terms", "BIGINT"), ("n_cc_terms", "BIGINT"),
                ("n_members_no_entry", "BIGINT"), ("entrapment_members", "VARCHAR[]"), ("is_contaminant", "BOOLEAN"),
                ("q_value", "DOUBLE"), ("go_release", "VARCHAR"), ("source_id", "VARCHAR"),
            ],
        };

    // --- what a bundle gives go ------------------------------------------------------------------------------

    /// <summary>go's <c>annotation_db_sha256</c> for a set of target databases (GO-D6): the file's own sha256 when there is
    /// one, and otherwise a sha256 over the sorted sha256s of all of them (one per line, after a version line).</summary>
    public static string AnnotationDbSha256(IReadOnlyList<string> sha256s)
    {
        if (sha256s.Count == 1) return sha256s[0];
        var text = "annotation-databases/1\n" + string.Concat(sha256s.Order(StringComparer.Ordinal).Select(s => s + "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The bundle's protein-group file and searched databases, from its own <c>sources</c>: role
    /// <c>protein_group_quant</c>, and each <c>protein_database:*</c> split by MetaMorpheus's contaminant rule.</summary>
    public static GoSearchInputs GoInputsOf(BundleRef bundle)
    {
        string? groupsPath = null, groupsSha = null;
        var targets = new List<GoDatabaseFile>();
        var contaminants = new List<GoDatabaseFile>();
        foreach (var item in SourcesPy.Get(bundle.Manifest, "sources") as IEnumerable<object?> ?? [])
        {
            if (item is not IReadOnlyDictionary<string, object?> source) continue;
            var role = SourcesPy.Str(SourcesPy.Get(source, "role", ""));
            var path = SourcesPy.Get(source, "path") as string;
            var sha = SourcesPy.Get(source, "sha256") as string;
            if (role == "protein_group_quant")
                (groupsPath, groupsSha) = (path, sha);
            else if (role.StartsWith("protein_database:", StringComparison.Ordinal) && path is not null && sha is not null)
                (ProteinDb.IsContaminantDatabase(path) ? contaminants : targets).Add(new GoDatabaseFile(role["protein_database:".Length..], path, sha));
        }
        return new GoSearchInputs(groupsPath, groupsSha,
            targets.OrderBy(t => t.Name, StringComparer.Ordinal).ToList(), contaminants.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>A <c>go.resolve_members</c> artefact belongs to a search when it resolved that search's protein-group file
    /// against exactly that search's target databases, by sha256. Keyed on inputs, so a re-ingest reading the same files is
    /// served by the same artefact.</summary>
    public static bool GoMembersMatch(ArtefactRef artefact, GoSearchInputs search)
    {
        if (artefact.Engine != Runner.GoResolveMembers || search.ProteinGroupsSha256 is null || search.Targets.Count == 0) return false;
        var inputs = artefact.Inputs;
        if (inputs.GetValueOrDefault(Runner.GoProteinGroupsRole) != search.ProteinGroupsSha256) return false;
        var resolved = inputs.Where(kv => kv.Key.StartsWith(Runner.GoDatabaseRolePrefix, StringComparison.Ordinal)).Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal);
        return resolved.SetEquals(search.Targets.Select(t => t.Sha256));
    }

    // --- selection -----------------------------------------------------------------------------------------

    /// <summary>go's artefacts for these bundles, and three coverage checks that always pass and say what is not served.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>go.categorize_terms</c>: at most one in the store. Two are refused, never one picked: which go.obo and map a
    /// catalog serves is the operator's decision (GO-D9). With none, no GO is served at all.</item>
    /// <item><c>go.annotate_databases</c>: one per target database file a bundle searched, under that go.obo. Another go.obo
    /// for a served file, or two artefacts for one file, are refused.</item>
    /// <item><c>go.resolve_members</c>: one per bundle, matched by its protein-group file and target databases. A bundle is
    /// served GO only when it has one and every one of its target files has its database artefact; otherwise it is named
    /// in the check, as a bundle without a <c>go.annotate_groups</c> artefact was up to 1.4.5, and the build goes on.</item>
    /// </list>
    /// Artefacts written against another schema are skipped and named; <c>go.annotate_groups</c> artefacts are retired,
    /// never served, and counted.
    /// </remarks>
    private static void SelectGo(string store, IReadOnlyList<BundleRef> bundles, List<ArtefactRef> chosen, List<CatalogCheck> checks)
    {
        string Dir(string engine) => SourcesPy.PathStr(System.IO.Path.Combine(store, Runner.EngineDir, engine));
        var stale = new List<string>();
        var searches = bundles.ToDictionary(b => b.BundleId, GoInputsOf, StringComparer.Ordinal);
        // An artefact of another schema is named only when it would have served these bundles, as SelectArtefacts does.
        List<ArtefactRef> Current(string engine, Func<ArtefactRef, bool> relevant)
        {
            var all = Runner.DiscoverArtefacts(store, engine).Where(relevant).ToList();
            stale.AddRange(all.Where(r => r.SchemaVersion != SchemaContract.Version).Select(r => $"{engine}/{r.ArtefactId} (schema {r.SchemaVersion})"));
            return all.Where(r => r.SchemaVersion == SchemaContract.Version).ToList();
        }
        static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

        var ontologies = Current(Runner.GoCategorizeTerms, _ => true);
        if (ontologies.Count > 1)
            throw new CatalogException(
                $"{Runner.GoCategorizeTerms}: {ontologies.Count} artefacts ({string.Join(", ", ontologies.Select(r => r.ArtefactId))}) "
                + "categorize the GO terms (another go.obo release or category map). A catalog serves one go.obo and one map, so a "
                + $"category means the same thing in every dataset (go GO-D9). Move the ones it should not serve out of {Dir(Runner.GoCategorizeTerms)}.");
        var ontology = ontologies.SingleOrDefault();
        var oboSha = ontology?.Inputs.GetValueOrDefault("ontology");
        if (ontology is not null) chosen.Add(ontology);

        var files = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var b in bundles)
            foreach (var t in searches[b.BundleId].Targets)
            {
                if (!files.TryGetValue(t.Sha256, out var ids)) files[t.Sha256] = ids = new SortedSet<string>(StringComparer.Ordinal);
                ids.Add(b.DatasetId);
            }

        var annotated = new Dictionary<string, ArtefactRef>(StringComparer.Ordinal);
        if (ontology is not null)
        {
            var bySha = new Dictionary<string, List<ArtefactRef>>(StringComparer.Ordinal);
            foreach (var r in Current(Runner.GoAnnotateDatabases, r => r.Inputs.GetValueOrDefault(Runner.GoDatabaseRole) is { } s && files.ContainsKey(s)))
            {
                var sha = r.Inputs[Runner.GoDatabaseRole];
                if (!bySha.TryGetValue(sha, out var list)) bySha[sha] = list = [];
                list.Add(r);
            }
            foreach (var (sha, refs) in bySha.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var other = refs.Where(r => r.Inputs.GetValueOrDefault("ontology") != oboSha).ToList();
                if (other.Count > 0)
                    throw new CatalogException(
                        $"{Runner.GoAnnotateDatabases}: {string.Join(", ", other.Select(r => r.ArtefactId))} annotated the database {Short(sha)}... "
                        + $"(searched by {string.Join(", ", files[sha])}) under another go.obo than {Runner.GoCategorizeTerms}/{ontology.ArtefactId}'s "
                        + $"({Short(oboSha ?? "(none)")}). A catalog serves one go.obo (go GO-D9): annotate the database again with the same "
                        + $"--input ontology, or move the others out of {Dir(Runner.GoAnnotateDatabases)}.");
                if (refs.Count > 1)
                    throw new CatalogException(
                        $"{Runner.GoAnnotateDatabases}: {refs.Count} artefacts ({string.Join(", ", refs.Select(r => r.ArtefactId))}) annotate the "
                        + $"database {Short(sha)}... searched by {string.Join(", ", files[sha])}. A catalog serves one. Move the ones it should "
                        + $"not serve out of {Dir(Runner.GoAnnotateDatabases)}.");
                annotated[sha] = refs[0];
                chosen.Add(refs[0]);
            }
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
            var lacking = search.Targets.Where(t => !annotated.ContainsKey(t.Sha256)).Select(t => t.Name).ToList();
            string? why =
                search.ProteinGroupsSha256 is null ? "no protein-group file (an isobaric search is not annotated, GO-D14)"
                : search.Targets.Count == 0 ? "no target database"
                : matched.Count == 0 ? $"no {Runner.GoResolveMembers} artefact"
                : ontology is null ? $"no {Runner.GoCategorizeTerms} artefact"
                : lacking.Count > 0 ? $"no {Runner.GoAnnotateDatabases} artefact for {string.Join(", ", lacking)}"
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
            var mine = stale.Where(s => s.StartsWith(engine + "/", StringComparison.Ordinal)).ToList();
            return mine.Count > 0 ? "skipped, other schema: " + string.Join(", ", mine) : null;
        }
        static string? Join(params string?[] parts)
        {
            var kept = parts.Where(p => p is { Length: > 0 }).ToList();
            return kept.Count > 0 ? string.Join("; ", kept) : null;
        }
        checks.Add(new CatalogCheck($"{Runner.GoCategorizeTerms} (ontology and category map served)", "engine-coverage", true,
            ontology is null ? 0 : 1, 1,
            Join(ontology is null ? "no artefact, so no GO is served" : null, StaleOf(Runner.GoCategorizeTerms))));
        var uncovered = files.Keys.Where(sha => !annotated.ContainsKey(sha)).ToList();
        checks.Add(new CatalogCheck($"{Runner.GoAnnotateDatabases} coverage (target databases with an artefact)", "engine-coverage", true,
            files.Count - uncovered.Count, files.Count,
            Join(uncovered.Count == 0 ? null
                    : (ontology is null ? "none served without an ontology; " : "") + "no artefact for "
                      + string.Join("; ", uncovered.Select(sha => $"{Short(sha)} ({string.Join(", ", files[sha])})")),
                StaleOf(Runner.GoAnnotateDatabases))));
        var retired = Runner.DiscoverArtefacts(store, Runner.GoEngine).Count;
        checks.Add(new CatalogCheck($"{Runner.GoResolveMembers} coverage (bundles served GO)", "engine-coverage", true,
            served, bundles.Count,
            Join(notServed.Count > 0 ? "not served: " + string.Join("; ", notServed) : null, StaleOf(Runner.GoResolveMembers),
                retired > 0 ? $"{retired} {Runner.GoEngine} artefact(s) in the store are retired and never served" : null)));
    }

    // --- the build -----------------------------------------------------------------------------------------

    /// <summary>One served search, as the build derives its rows.</summary>
    /// <param name="DatabaseSha256s">Every target database file the search read: the files its members' entries are read
    /// from.</param>
    internal sealed record GoSearch(string DatasetId, string BundleId, string AnnotationDbSha256, IReadOnlyList<string> DatabaseSha256s,
        string SourceId, string SourceVersion, string GoRelease);

    /// <summary>What a build serves: the ontology artefact, the database artefacts and the served searches with their
    /// member artefacts.</summary>
    private sealed record GoPlan(ArtefactRef? Ontology, IReadOnlyList<ArtefactRef> Databases,
        IReadOnlyList<(BundleRef Bundle, GoSearch Search, ArtefactRef Members)> Served);

    private static IReadOnlyDictionary<string, object?>? GoSummary(ArtefactRef a) =>
        a.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;

    private static string GoReleaseOf(ArtefactRef ontology) => SourcesPy.Get(GoSummary(ontology), "go_release") as string ?? "";

    /// <summary>The served searches among these bundles: those with exactly one member artefact and a database artefact
    /// for each target file, under one ontology artefact.</summary>
    /// <exception cref="CatalogException">Two ontology artefacts, or two artefacts for one database file or one search:
    /// <see cref="SelectGo"/> refuses the same, and this refuses artefacts handed to the build some other way.</exception>
    private static GoPlan GoPlanOf(IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var ontologies = artefacts.Where(a => a.Engine == Runner.GoCategorizeTerms).ToList();
        if (ontologies.Count > 1)
            throw new CatalogException($"{Runner.GoCategorizeTerms}: {ontologies.Count} artefacts given. A catalog serves one go.obo and one map (go GO-D9).");
        var ontology = ontologies.SingleOrDefault();
        if (ontology is null) return new GoPlan(null, [], []);
        var databases = artefacts.Where(a => a.Engine == Runner.GoAnnotateDatabases).ToList();
        var bySha = new Dictionary<string, ArtefactRef>(StringComparer.Ordinal);
        foreach (var a in databases)
            if (!bySha.TryAdd(a.Inputs.GetValueOrDefault(Runner.GoDatabaseRole) ?? "", a))
                throw new CatalogException($"{Runner.GoAnnotateDatabases}: two artefacts given for one database ({bySha[a.Inputs[Runner.GoDatabaseRole]].ArtefactId}, {a.ArtefactId}). A catalog serves one.");
        var release = GoReleaseOf(ontology);
        var served = new List<(BundleRef, GoSearch, ArtefactRef)>();
        foreach (var b in bundles.OrderBy(b => b.DatasetId, SourcesPy.CodePointOrder))
        {
            var search = GoInputsOf(b);
            var matched = artefacts.Where(a => GoMembersMatch(a, search)).ToList();
            if (matched.Count == 0 || search.Targets.Any(t => !bySha.ContainsKey(t.Sha256))) continue;
            if (matched.Count > 1)
                throw new CatalogException($"{Runner.GoResolveMembers}: {matched.Count} artefacts given for {b.DatasetId} bundle {b.BundleId}. A catalog serves one.");
            var used = new[] { ontology }.Concat(search.Targets.Select(t => bySha[t.Sha256]).DistinctBy(a => a.ArtefactId)).Append(matched[0]).ToList();
            var key = string.Concat(used.Select(a => $"{a.Engine}\t{a.ArtefactId}\n").Order(StringComparer.Ordinal));
            var sourceId = $"go:{release}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("go-source/1\n" + key)))[..12]}";
            var mzlib = used.Select(a => SourcesPy.Get(SourcesPy.Get(a.Record, "release") as IReadOnlyDictionary<string, object?>, "mzlib") as string)
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var dbSha = search.AnnotationDbSha256!;
            var version = $"{release}; mzLib {string.Join(", ", mzlib)}; go.obo {ontology.Inputs.GetValueOrDefault("ontology")}; "
                + $"annotation db {dbSha}; artefacts {string.Join(", ", used.Select(a => $"{a.Engine}/{a.ArtefactId}"))}";
            served.Add((b, new GoSearch(b.DatasetId, b.BundleId, dbSha, search.Targets.Select(t => t.Sha256).Distinct(StringComparer.Ordinal).ToList(),
                sourceId, version, release), matched[0]));
        }
        // Every database artefact given is served, a search's or not: the database's terms are reference data.
        return new GoPlan(ontology, databases, served);
    }

    /// <summary>Creates every GO table typed and empty: the five reference tables, <c>go_group_status</c>, and the three
    /// core tables go's rows fill (<see cref="Runner.GoTables"/>), with the bundle provenance columns.</summary>
    internal static void CreateGoTables(DuckDBConnection con)
    {
        foreach (var table in GoReferenceTables)
            Exec(con, $"CREATE TABLE \"{table}\" ({string.Join(", ", GoColumns[table].Select(c => $"\"{c.Name}\" {c.Type}"))})");
        foreach (var table in Runner.GoTables)
            CreateEmpty(con, table, DatasetProvenance, SchemaContract.Columns(Tables.ByName[table]).Where(c => c.Name != "dataset_id"));
    }

    /// <summary>The ontology artefact's three tables into the catalog, the release and map named on every row.</summary>
    internal static void LoadGoOntology(DuckDBConnection con, string termsPath, string closurePath, string categoriesPath,
        string goRelease, string mapName, string mapVersion, string artefactId)
    {
        var (aid, release) = (Quote(artefactId), Quote(goRelease));
        Exec(con, $"INSERT INTO {GoTermsTable} BY NAME SELECT go_id, go_name, aspect, obsolete, {release} AS go_release, {aid} AS artefact_id "
            + $"FROM read_parquet({Quote(AsPosix(termsPath))})");
        Exec(con, $"INSERT INTO {GoClosureTable} BY NAME SELECT go_id, ancestor_go_id, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(closurePath))})");
        Exec(con, $"INSERT INTO {GoTermCategoriesTable} BY NAME SELECT go_id, category, subcategory, {Quote(mapName)} AS category_map_name, "
            + $"{Quote(mapVersion)} AS category_map_version, {release} AS go_release, {aid} AS artefact_id FROM read_parquet({Quote(AsPosix(categoriesPath))})");
    }

    /// <summary>One database artefact's rows into <c>go_accession_terms</c>, its file's sha256 on every row.</summary>
    internal static void LoadGoAccessionTerms(DuckDBConnection con, string path, string databaseSha256, string artefactId) =>
        Exec(con, $"INSERT INTO {GoAccessionTermsTable} BY NAME SELECT {Quote(databaseSha256)} AS database_sha256, accession, go_id, aspect, "
            + $"evidence, inherited, isoform_of, {Quote(artefactId)} AS artefact_id FROM read_parquet({Quote(AsPosix(path))})");

    /// <summary>One search's member artefact rows into <c>go_group_members</c>, under the search's bundle.</summary>
    internal static void LoadGoMembers(DuckDBConnection con, string path, string datasetId, string bundleId, string annotationDbSha256, string artefactId) =>
        Exec(con, $"INSERT INTO {GoGroupMembersTable} BY NAME SELECT {Quote(datasetId)} AS dataset_id, {Quote(bundleId)} AS bundle_id, accession, "
            + $"annotated_as, resolution, entrapment, sequence_accession, {Quote(annotationDbSha256)} AS annotation_db_sha256, "
            + $"{Quote(artefactId)} AS artefact_id FROM read_parquet({Quote(AsPosix(path))})");

    /// <summary>One search's non-decoy protein groups, as the derivation reads them: the bundle's <c>protein_groups</c>
    /// rows that are not decoys, whatever their q-value (go's population, D18, D29), with the group's contaminant mark and
    /// q-value. <c>protein_group</c> is the group's accessions <c>|</c>-joined in the bundle's (ordinal) order.</summary>
    private static string GoGroupsOf(string bundleId) => $@"
        SELECT protein_group_id AS group_key, array_to_string(protein_accessions, '|') AS protein_group,
               coalesce(target_decoy = 'contaminant', false) AS is_contaminant, q_value,
               list_distinct(list_filter(protein_accessions, a -> coalesce(a, '') <> '')) AS members
        FROM protein_groups
        WHERE bundle_id = {Quote(bundleId)} AND target_decoy IS DISTINCT FROM 'decoy'";

    /// <summary>The join every derived GO row of one search comes from: mzLib's <c>JoinView</c>
    /// (<c>TestGoPerDatabaseTables</c>), go's "Computing v1 rows from the stored tables", in SQL with equality joins only.
    /// <c>{groups}</c> is the search's groups (<see cref="GoGroupsOf"/>), <c>{bundle}</c> its bundle id and <c>{files}</c>
    /// its target database files' sha256s, as literals.</summary>
    /// <remarks>
    /// <list type="number">
    /// <item><c>mem</c>: each distinct member of each group, with its <c>go_group_members</c> row.</item>
    /// <item><c>own</c>: each entry's own direct terms (<c>inherited = false</c>) over every file the search read, the
    /// evidence of an entry present in two files unioned, as <c>GoAccessionAnnotation</c> unions an accession given
    /// twice.</item>
    /// <item><c>effective</c>: the terms an entry takes. Its own; or, for a UniProt isoform with none of its own whose
    /// entry has some (<c>isoform_of</c>, D37), the entry's, inherited. Over the union, so an isoform held in another
    /// file than its entry takes the entry's terms as the annotator over all the files gives them.</item>
    /// <item><c>carried</c>: per (group, member, term), each direct term through its closure pairs (the term itself
    /// included): <c>direct</c> when the term is a direct one, <c>inherited</c> when the member's resolution is
    /// <c>inherited</c> or the entry's terms are, and the union of the evidence of the direct terms at or below it.</item>
    /// </list>
    /// </remarks>
    private const string GoCarried = @"
        grp AS ({groups}),
        gm AS (SELECT group_key, unnest(members) AS member FROM grp),
        mem AS (
            SELECT gm.group_key, gm.member, r.annotated_as, r.resolution, r.entrapment
            FROM gm LEFT JOIN go_group_members r ON r.bundle_id = {bundle} AND r.accession = gm.member
        ),
        db AS (
            SELECT accession, go_id, evidence, inherited, isoform_of FROM go_accession_terms WHERE database_sha256 IN ({files})
        ),
        entries AS (
            SELECT DISTINCT accession, isoform_of FROM db
            WHERE accession IN (SELECT annotated_as FROM mem WHERE annotated_as IS NOT NULL)
        ),
        own AS (
            SELECT accession, go_id,
                   list_sort(list_distinct(flatten(list(coalesce(string_split(evidence, ';'), []::VARCHAR[]))))) AS evidence
            FROM db
            WHERE go_id IS NOT NULL AND NOT inherited
              AND (accession IN (SELECT accession FROM entries)
                   OR accession IN (SELECT isoform_of FROM entries WHERE isoform_of IS NOT NULL))
            GROUP BY accession, go_id
        ),
        effective AS (
            SELECT o.accession, o.go_id, o.evidence, false AS inherited
            FROM own o WHERE o.accession IN (SELECT accession FROM entries)
            UNION ALL
            SELECT e.accession, o.go_id, o.evidence, true AS inherited
            FROM entries e JOIN own o ON o.accession = e.isoform_of
            WHERE NOT EXISTS (SELECT 1 FROM own x WHERE x.accession = e.accession)
        ),
        carried AS (
            SELECT m.group_key, m.member, c.ancestor_go_id AS go_id,
                   bool_or(c.ancestor_go_id = e.go_id) AS direct,
                   bool_or(m.resolution = 'inherited' OR e.inherited) AS inherited,
                   list_sort(list_distinct(flatten(list(e.evidence)))) AS evidence
            FROM mem m
            JOIN effective e ON e.accession = m.annotated_as
            JOIN go_closure c ON c.go_id = e.go_id
            WHERE m.resolution IN ('self', 'inherited')
            GROUP BY m.group_key, m.member, c.ancestor_go_id
        )";

    /// <summary><c>protein_localizations</c>: one row per (group, member, CC term the member carries), expanded over
    /// <c>accession_used</c> only, with the member's own qualifiers (go D33, GO-D7, as <c>Go.LocalizationRows</c> wrote them
    /// from go's file): <c>inherited</c>, <c>propagated</c> = not direct, <c>evidence</c> = its own codes; and the group's
    /// <c>q_value</c>, <c>n_members</c> and <c>n_with</c>.</summary>
    private const string GoLocalizationsSelect = @"
        SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, c.member AS protein_accession, c.go_id AS compartment,
               t.go_release, CASE WHEN len(c.evidence) > 0 THEN array_to_string(c.evidence, ';') END AS evidence,
               {source} AS source_id, g.protein_group, g.q_value, len(g.members) AS n_members,
               count(*) OVER (PARTITION BY c.group_key, c.go_id) AS n_with, c.inherited, NOT c.direct AS propagated
        FROM carried c JOIN grp g ON g.group_key = c.group_key JOIN go_terms t ON t.go_id = c.go_id
        WHERE t.aspect = 'cellular_component'
        ORDER BY protein_accession, compartment, g.protein_group";

    /// <summary><c>go_group_status</c>: every group of the search exactly once, with go's status in D19's precedence
    /// (<c>annotated</c> when any member carries any term, of any aspect; else <c>contaminant</c> when the group is marked;
    /// else <c>no_entry</c> when a member is not in the search's databases; else <c>no_go_terms</c>).</summary>
    private const string GoStatusSelect = @"
        SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, g.group_key AS protein_group_id, g.protein_group,
               len(g.members) AS n_members,
               CASE WHEN coalesce(s.n_terms, 0) > 0 THEN 'annotated' WHEN g.is_contaminant THEN 'contaminant'
                    WHEN coalesce(m.n_no_entry, 0) > 0 THEN 'no_entry' ELSE 'no_go_terms' END AS annotation_status,
               coalesce(s.n_terms, 0) AS n_terms, coalesce(s.n_cc_terms, 0) AS n_cc_terms,
               coalesce(m.n_no_entry, 0) AS n_members_no_entry, coalesce(m.entrapment_members, []::VARCHAR[]) AS entrapment_members,
               g.is_contaminant, g.q_value, {release} AS go_release, {source} AS source_id
        FROM grp g
        LEFT JOIN (
            SELECT c.group_key, count(DISTINCT c.go_id) AS n_terms,
                   count(DISTINCT c.go_id) FILTER (WHERE t.aspect = 'cellular_component') AS n_cc_terms
            FROM carried c LEFT JOIN go_terms t ON t.go_id = c.go_id GROUP BY c.group_key
        ) s ON s.group_key = g.group_key
        LEFT JOIN (
            SELECT group_key, count(*) FILTER (WHERE resolution = 'no_entry') AS n_no_entry,
                   list_sort(list(member) FILTER (WHERE entrapment)) AS entrapment_members
            FROM mem GROUP BY group_key
        ) m ON m.group_key = g.group_key
        ORDER BY g.protein_group, g.group_key";

    /// <summary><c>organelle_term_categories</c>: the served map's rows for every term the search's groups carry, of any
    /// aspect, as go's per-search category file held them (<c>GoCategoryTsv</c> over the annotation rows' terms).</summary>
    private const string GoCategoriesSelect = @"
        SELECT DISTINCT {dataset} AS dataset_id, {bundle} AS bundle_id, k.go_id AS compartment, k.category_map_name,
               k.category_map_version AS organelle_map_version, k.go_release, k.category AS organelle_category,
               k.subcategory AS organelle_subcategory, {source} AS source_id
        FROM (SELECT DISTINCT go_id FROM carried) c JOIN go_term_categories k ON k.go_id = c.go_id
        ORDER BY compartment, organelle_category, organelle_subcategory NULLS FIRST";

    /// <summary>A query over one search's <see cref="GoCarried"/> join (<c>grp</c>, <c>mem</c>, <c>effective</c>,
    /// <c>carried</c>, ...), with <c>{dataset}</c>, <c>{bundle}</c>, <c>{source}</c> and <c>{release}</c> in
    /// <paramref name="select"/> filled as literals.</summary>
    internal static string GoSql(GoSearch search, string groups, string select) =>
        "SELECT * FROM (WITH " + GoCarried
            .Replace("{groups}", groups)
            .Replace("{bundle}", Quote(search.BundleId))
            .Replace("{files}", string.Join(", ", search.DatabaseSha256s.Select(Quote)))
        + "\n" + select
            .Replace("{dataset}", Quote(search.DatasetId))
            .Replace("{bundle}", Quote(search.BundleId))
            .Replace("{source}", Quote(search.SourceId))
            .Replace("{release}", Quote(search.GoRelease))
        + ")";

    /// <summary>One search's derived rows: <c>protein_localizations</c>, <c>go_group_status</c>,
    /// <c>organelle_term_categories</c> and its <c>annotation_sources</c> row.</summary>
    /// <param name="groups">The search's groups: <c>group_key</c>, <c>protein_group</c>, <c>is_contaminant</c>,
    /// <c>q_value</c>, <c>members</c> (<see cref="GoGroupsOf"/>; a test passes go's fixture groups).</param>
    internal static void DeriveGo(DuckDBConnection con, GoSearch search, string groups)
    {
        string Fill(string select) => GoSql(search, groups, select);
        Exec(con, "INSERT INTO protein_localizations BY NAME " + Fill(GoLocalizationsSelect));
        Exec(con, $"INSERT INTO {GoGroupStatusTable} BY NAME " + Fill(GoStatusSelect));
        Exec(con, "INSERT INTO organelle_term_categories BY NAME " + Fill(GoCategoriesSelect));
        Exec(con, "INSERT INTO annotation_sources BY NAME SELECT ? AS dataset_id, ? AS bundle_id, ? AS source_id, 'go' AS owner_project, "
            + "? AS version, NULL AS doi", search.DatasetId, search.BundleId, search.SourceId, search.SourceVersion);
    }

    /// <summary>Every GO table: the reference tables copied from the served artefacts, and the derived ones joined from
    /// them and each served search's <c>protein_groups</c>; all typed and empty when nothing is served.</summary>
    /// <returns><c>{table: rows}</c> when an ontology artefact was served, else nothing, as <see cref="BuildOrthology"/>.</returns>
    /// <remarks>Runs after <see cref="LoadTables"/>, since it reads <c>protein_groups</c>.</remarks>
    private static Dictionary<string, long> BuildGo(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!GoActive) return counts;
        CreateGoTables(con);
        var plan = GoPlanOf(bundles, artefacts);
        if (plan.Ontology is not { } ontology) return counts;
        string Table(ArtefactRef a, string table) => a.TablePath(table)
            ?? throw new CatalogException($"{a.Engine}/{a.ArtefactId}: {a.Path} holds no {table}.parquet. Run the engine again.");
        var map = SourcesPy.Get(GoSummary(ontology), "category_map") as IReadOnlyDictionary<string, object?>;
        try
        {
            LoadGoOntology(con, Table(ontology, GoTermsTable), Table(ontology, GoClosureTable), Table(ontology, GoTermCategoriesTable),
                GoReleaseOf(ontology), SourcesPy.Get(map, "name") as string ?? "", SourcesPy.Get(map, "version") as string ?? "", ontology.ArtefactId);
            foreach (var a in plan.Databases.OrderBy(a => a.ArtefactId, SourcesPy.CodePointOrder))
                LoadGoAccessionTerms(con, Table(a, GoAccessionTermsTable), a.Inputs[Runner.GoDatabaseRole], a.ArtefactId);
            foreach (var (_, search, members) in plan.Served)
                LoadGoMembers(con, Table(members, GoGroupMembersTable), search.DatasetId, search.BundleId, search.AnnotationDbSha256, members.ArtefactId);
        }
        catch (DuckDBException e)
        {
            throw new CatalogException($"go's artefacts could not be read: {e.Message}");
        }
        foreach (var (_, search, _) in plan.Served) DeriveGo(con, search, GoGroupsOf(search.BundleId));
        foreach (var table in GoReferenceTables.Concat(Runner.GoTables)) counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        return counts;
    }

    // --- checks --------------------------------------------------------------------------------------------

    /// <summary>The GO tables hold every row of every served artefact and agree with each other.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Each ontology and database artefact's rows are all copied (kind <c>row_count</c>), and each member artefact's
    /// under each search it serves.</item>
    /// <item>Every artefact used one go.obo, the ontology artefact's (go GO-D9).</item>
    /// <item>Every member of every non-decoy group of a served search has a <c>go_group_members</c> row (dataRepo 028): a
    /// member with none would silently drop from its group's terms.</item>
    /// <item>Every entry a member takes (<c>annotated_as</c>) is in the search's database tables: otherwise the member
    /// resolution and the annotation were built from different databases (mzLib's annotator refuses the same).</item>
    /// <item>Every term of a database table is a term of the release (<c>go_terms</c>), so no carried term loses its
    /// name, aspect or closure.</item>
    /// <item>No search's group is in <c>go_group_status</c> twice, and no member in <c>go_group_members</c> twice.</item>
    /// </list>
    /// </remarks>
    private static List<CatalogCheck> CheckGoTables(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        if (!GoActive) return checks;
        var plan = GoPlanOf(bundles, artefacts);
        if (plan.Ontology is not { } ontology) return checks;

        foreach (var a in new[] { ontology }.Concat(plan.Databases.OrderBy(a => a.ArtefactId, SourcesPy.CodePointOrder)))
            foreach (var (table, expected) in a.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var observed = Count(con, $"SELECT count(*) FROM \"{table}\" WHERE artefact_id = ?", a.ArtefactId);
                checks.Add(new CatalogCheck($"{a.Engine}/{a.ArtefactId}/{table}", "row_count", observed == expected, observed, expected,
                    $"engine artefact {a.ArtefactId}"));
            }
        foreach (var (bundle, _, members) in plan.Served)
            foreach (var (table, expected) in members.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var observed = Count(con, $"SELECT count(*) FROM \"{table}\" WHERE artefact_id = ? AND bundle_id = ?", members.ArtefactId, bundle.BundleId);
                checks.Add(new CatalogCheck($"{members.Engine}/{members.ArtefactId}/{table}", "row_count", observed == expected, observed, expected,
                    $"engine artefact {members.ArtefactId} for {bundle.DatasetId} bundle {bundle.BundleId}"));
            }

        var obo = ontology.Inputs.GetValueOrDefault("ontology");
        var foreign = plan.Databases.Where(a => a.Inputs.GetValueOrDefault("ontology") != obo).Select(a => a.ArtefactId).ToList();
        checks.Add(new CatalogCheck($"{Runner.GoAnnotateDatabases} go.obo (one per catalog, GO-D9)", "engine-consistency", foreign.Count == 0,
            foreign.Count, 0, foreign.Count == 0
                ? $"every database artefact used {Runner.GoCategorizeTerms}/{ontology.ArtefactId}'s go.obo"
                : $"{string.Join(", ", foreign)} used another go.obo"));

        if (plan.Served.Count == 0) return checks;
        var served = string.Join(", ", plan.Served.Select(s => Quote(s.Bundle.BundleId)));
        var coverage = Query(con, $@"
            WITH members AS (
                SELECT DISTINCT dataset_id, bundle_id, accession FROM (
                    SELECT dataset_id, bundle_id, unnest(protein_accessions) AS accession FROM protein_groups
                    WHERE bundle_id IN ({served}) AND target_decoy IS DISTINCT FROM 'decoy')
                WHERE coalesce(accession, '') <> '')
            SELECT count(*), count(r.accession), min(m.dataset_id || ': ' || m.accession) FILTER (WHERE r.accession IS NULL)
            FROM members m LEFT JOIN {GoGroupMembersTable} r ON r.bundle_id = m.bundle_id AND r.accession = m.accession")[0];
        var (wanted, answered) = (PyInt(coverage[0]), PyInt(coverage[1]));
        checks.Add(new CatalogCheck($"{Runner.GoResolveMembers} members (a {GoGroupMembersTable} row for every member of a served search's groups)",
            "engine-consistency", answered == wanted, answered, wanted,
            answered == wanted
                ? $"{wanted} member accession(s) of {plan.Served.Count} search(es)"
                : $"no row for {wanted - answered} member(s), e.g. {PyStr(coverage[2])}: the member artefact was not made from these protein groups (dataRepo 028)"));

        var files = string.Join(", ", plan.Served.SelectMany(s => s.Search.DatabaseSha256s.Select(f => $"({Quote(s.Bundle.BundleId)}, {Quote(f)})")));
        var (missing, missingExample) = CountAndExample(con, $@"
            WITH files(bundle_id, database_sha256) AS (VALUES {files})
            SELECT count(*), min(r.dataset_id || ': ' || r.accession || ' -> ' || r.annotated_as) FROM {GoGroupMembersTable} r
            WHERE r.annotated_as IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM files f JOIN {GoAccessionTermsTable} a ON a.database_sha256 = f.database_sha256
                WHERE f.bundle_id = r.bundle_id AND a.accession = r.annotated_as)");
        checks.Add(new CatalogCheck($"{Runner.GoResolveMembers} entries (every annotated_as in the search's {GoAccessionTermsTable})", "engine-consistency",
            missing == 0, missing, 0, missing == 0 ? null
                : $"e.g. {PyStr(missingExample)}: the member resolution and the database annotation were built from different databases"));

        var (unknown, unknownExample) = CountAndExample(con, $@"
            SELECT count(*), min(go_id) FROM (SELECT DISTINCT go_id FROM {GoAccessionTermsTable} WHERE go_id IS NOT NULL) a
            WHERE NOT EXISTS (SELECT 1 FROM {GoTermsTable} t WHERE t.go_id = a.go_id)");
        checks.Add(new CatalogCheck($"{GoAccessionTermsTable} terms (every go_id a term of the release, {GoTermsTable})", "engine-consistency",
            unknown == 0, unknown, 0, unknown == 0 ? null : $"e.g. {PyStr(unknownExample)}"));

        foreach (var (table, key) in new[] { (GoGroupStatusTable, "bundle_id, protein_group_id"), (GoGroupMembersTable, "bundle_id, accession") })
        {
            var (duplicates, example) = CountAndExample(con, $"SELECT count(*), min(k) FROM (SELECT min({key.Split(", ")[1]}) AS k FROM {table} GROUP BY {key} HAVING count(*) > 1)");
            checks.Add(new CatalogCheck($"{table} ({key})", "engine-unique", duplicates == 0, duplicates, 0, duplicates == 0 ? null : $"e.g. {PyStr(example)}"));
        }
        return checks;
    }
}
