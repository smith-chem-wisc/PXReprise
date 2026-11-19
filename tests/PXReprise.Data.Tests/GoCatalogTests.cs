using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest.Sources;
using PXReprise.Runner;
using Proteomics;
using UsefulProteomicsDatabases.Annotation;
using UsefulProteomicsDatabases.GeneOntology;

namespace PXReprise.Data.Tests;

/// <summary>The catalog's GO join (<c>CatalogGo.cs</c>) gives the rows of <c>go:DEF-GROUP-GO-ANNOTATION v1</c>: go's own
/// fixture row for row, and <c>GoGroupAnnotator</c> over a search whose isoform is in another database file than its
/// entry.</summary>
/// <remarks>
/// <para>Each test loads the stored tables into the catalog's GO tables with the build's own loaders, from Parquet the
/// engines' own row converters wrote, and runs the build's own SQL (<see cref="CatalogBuilder.DeriveGo"/>), so nothing
/// here restates the join. Two things are compared with the annotator's file (<c>go_annotation_format 1</c>):</para>
/// <list type="bullet">
/// <item><b>Every group row</b>, all aspects: the 16 columns from <c>protein_group</c> to <c>q_value</c>, rebuilt from the
/// join's per-member rows and <c>go_group_status</c>.</item>
/// <item><b><c>protein_localizations</c></b>: what v1 stored from that file (<c>Go.LocalizationRows</c>): each CC row
/// expanded over <c>accession_used</c>, with that accession's own <c>inherited</c>, <c>propagated</c> and evidence (go D33,
/// GO-D7) and the group's <c>q_value</c>, <c>n_members</c> and <c>n_with</c>. v1 keyed its rows by (accession, term) and
/// refused a file in which an accession sits in two groups; go's fixture has such accessions (<c>P04406-2</c>,
/// <c>P33333</c>, <c>Q9ZZZ9</c>), so the expected rows here are one per (group, accession, term), which is what v1's
/// expansion gives row by row.</item>
/// </list>
/// </remarks>
public class GoCatalogTests
{
    private static string Fixture(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "go-view-v1-a83d1c63", name);
    private static string Ontology => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "go-engine", "go-trimmed.obo");
    private static string Map => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "go-engine", "fixture_map.tsv");

    private string _root = "";

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-go-catalog-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Parquet(string table, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var path = Path.Combine(_root, $"{table}-{Guid.NewGuid():N}.parquet");
        ArrowTables.WriteParquet(ArrowTables.FromRows(table, GoReferenceEngines.Columns[table], rows), path);
        return path;
    }

    /// <summary>A connection holding the catalog's GO tables, the ontology loaded from mzLib's term and closure files.</summary>
    private DuckDBConnection Catalog(AnnotationTable<GoTermRow> terms, AnnotationTable<GoClosureRow> closure,
        AnnotationTable<GoCategoryRow>? categories = null)
    {
        var con = CatalogBuilder.Open(":memory:", readOnly: false);
        CatalogBuilder.CreateGoTables(con);
        CatalogBuilder.LoadGoOntology(con, Parquet(CatalogBuilder.GoTermsTable, GoReferenceEngines.TermRows(terms)),
            Parquet(CatalogBuilder.GoClosureTable, GoReferenceEngines.ClosureRows(closure)),
            Parquet(CatalogBuilder.GoTermCategoriesTable, categories is null ? [] : GoReferenceEngines.CategoryRows(categories)),
            terms.Header["go_release"], "fixture", "1", "ontology-artefact");
        return con;
    }

    /// <summary>Groups as the derivation reads them, from a table of (name, <c>;</c>-joined members, contaminant mark,
    /// q-value).</summary>
    private static string Groups(DuckDBConnection con, IEnumerable<(string Name, string Members, bool Contaminant, double Q)> groups)
    {
        CatalogBuilder.Exec(con, "CREATE TABLE test_groups (protein_group VARCHAR, protein_accessions VARCHAR, is_contaminant BOOLEAN, q_value DOUBLE)");
        foreach (var g in groups)
            CatalogBuilder.Exec(con, "INSERT INTO test_groups VALUES (?, ?, ?, ?)", g.Name, g.Members, g.Contaminant, g.Q);
        return "SELECT protein_group AS group_key, protein_group, is_contaminant, q_value, "
            + "list_distinct(list_filter(string_split(protein_accessions, ';'), a -> a <> '')) AS members FROM test_groups";
    }

    // --- the annotator's file, read as go's columns ---------------------------------------------------------

    private static readonly string[] GroupColumns =
    [
        "protein_group", "accession_used", "accession_direct", "accession_inherited", "go_id", "go_name", "aspect", "evidence",
        "evidence_by_member", "inherited", "propagated", "n_members", "n_with", "entrapment_members", "annotation_status", "q_value",
    ];

    private static (Dictionary<string, string> Header, List<Dictionary<string, string>> Rows) ReadGoFile(string path)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<Dictionary<string, string>>();
        string[]? names = null;
        foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0))
        {
            if (line.StartsWith("#!", StringComparison.Ordinal))
            {
                var at = line.IndexOf(' ');
                header[line[2..at]] = line[(at + 1)..];
                continue;
            }
            var cells = line.Split('\t');
            if (names is null)
            {
                names = cells;
                continue;
            }
            rows.Add(names.Select((n, i) => (n, cells[i])).ToDictionary(p => p.n, p => p.Item2, StringComparer.Ordinal));
        }
        return (header, rows);
    }

    private static List<string> Split(string cell) => cell.Split(';').Where(c => c.Length > 0).ToList();

    private static string Q(double q) => q.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The file's group rows, as the 16 columns joined.</summary>
    private static List<string> ExpectedGroupRows(List<Dictionary<string, string>> rows) =>
        rows.Select(r => string.Join("\t", GroupColumns.Select(c => c == "q_value" ? Q(double.Parse(r[c], CultureInfo.InvariantCulture)) : r[c])))
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>What v1 stored in <c>protein_localizations</c> from the file (<c>Go.LocalizationRows</c>'s expansion).</summary>
    private static List<string> ExpectedLocalizations(List<Dictionary<string, string>> rows, string release) =>
        rows.Where(r => r["go_id"].Length > 0 && r["aspect"] == Go.CellularComponent)
            .SelectMany(r =>
            {
                var direct = Split(r["accession_direct"]).ToHashSet(StringComparer.Ordinal);
                var inherited = Split(r["accession_inherited"]).ToHashSet(StringComparer.Ordinal);
                var evidence = Go.EvidenceByMember(r["evidence_by_member"]);
                return Split(r["accession_used"]).Select(a => Localization(a, r["go_id"], release,
                    evidence[a].Count > 0 ? string.Join(';', evidence[a]) : null, r["protein_group"], double.Parse(r["q_value"], CultureInfo.InvariantCulture),
                    long.Parse(r["n_members"], CultureInfo.InvariantCulture), long.Parse(r["n_with"], CultureInfo.InvariantCulture),
                    inherited.Contains(a), !direct.Contains(a)));
            })
            .Order(StringComparer.Ordinal).ToList();

    private static string Localization(object? accession, object? term, object? release, object? evidence, object? group, object? q,
        object? members, object? with, object? inherited, object? propagated) =>
        string.Join("|", accession, term, release, evidence ?? "NULL", group, q is double d ? Q(d) : q, members, with,
            inherited is bool i ? (i ? "true" : "false") : "NULL", propagated is bool p ? (p ? "true" : "false") : "NULL");

    // --- what the catalog built ------------------------------------------------------------------------------

    /// <summary>The group rows the catalog's join gives, in the file's 16 columns.</summary>
    private static List<string> CatalogGroupRows(DuckDBConnection con, CatalogBuilder.GoSearch search, string groups)
    {
        static string B(bool b) => b ? "true" : "false";
        var carried = CatalogBuilder.Query(con, CatalogBuilder.GoSql(search, groups,
                "SELECT g.protein_group, c.member, c.go_id, t.go_name, t.aspect, c.direct, c.inherited, array_to_string(c.evidence, ',') "
                + "FROM carried c JOIN grp g ON g.group_key = c.group_key LEFT JOIN go_terms t ON t.go_id = c.go_id"))
            .Select(r => (Group: (string)r[0]!, Member: (string)r[1]!, Term: (string)r[2]!, Name: (string)r[3]!, Aspect: (string)r[4]!,
                Direct: (bool)r[5]!, Inherited: (bool)r[6]!, Evidence: (string)r[7]!))
            .ToList();
        var lines = new List<string>();
        foreach (var s in CatalogBuilder.Query(con,
                     "SELECT protein_group, n_members, annotation_status, array_to_string(entrapment_members, ';'), q_value, n_terms "
                     + "FROM go_group_status WHERE bundle_id = ?", search.BundleId))
        {
            var (group, members, status, entrapment, q) = ((string)s[0]!, Convert.ToInt64(s[1], CultureInfo.InvariantCulture), (string)s[2]!, (string)s[3]!, (double)s[4]!);
            var terms = carried.Where(c => c.Group == group).GroupBy(c => c.Term).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
            Assert.That(Convert.ToInt64(s[5], CultureInfo.InvariantCulture), Is.EqualTo(terms.Count), $"{group}: n_terms");
            if (terms.Count == 0)
            {
                lines.Add(string.Join("\t", group, "", "", "", "", "", "", "", "", "", "", members, 0, entrapment, status, Q(q)));
                continue;
            }
            foreach (var t in terms)
            {
                var used = t.OrderBy(c => c.Member, StringComparer.Ordinal).ToList();
                var direct = used.Where(c => c.Direct).Select(c => c.Member).ToList();
                var inherited = used.Where(c => c.Inherited).Select(c => c.Member).ToList();
                var pooled = used.SelectMany(c => c.Evidence.Split(',').Where(e => e.Length > 0)).Distinct().Order(StringComparer.Ordinal);
                lines.Add(string.Join("\t", group, string.Join(';', used.Select(c => c.Member)), string.Join(';', direct), string.Join(';', inherited),
                    t.Key, used[0].Name, used[0].Aspect, string.Join(';', pooled), string.Join(';', used.Select(c => $"{c.Member}={c.Evidence}")),
                    B(inherited.Count == used.Count), B(direct.Count == 0), members, used.Count, entrapment, status, Q(q)));
            }
        }
        return lines.Order(StringComparer.Ordinal).ToList();
    }

    private static List<string> CatalogLocalizations(DuckDBConnection con) =>
        CatalogBuilder.Query(con,
                "SELECT protein_accession, compartment, go_release, evidence, protein_group, q_value, n_members, n_with, inherited, propagated "
                + "FROM protein_localizations")
            .Select(r => Localization(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9]))
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>go D26's five counters, counted in <c>go_group_status</c> at q_value &lt;= 0.01.</summary>
    private static Dictionary<string, string> Counters(DuckDBConnection con) =>
        CatalogBuilder.Query(con,
                "SELECT 'n_multi_member_groups', count(*) FILTER (WHERE n_members > 1) FROM go_group_status WHERE q_value <= 0.01 "
                + "UNION ALL SELECT 'status_' || s, (SELECT count(*) FROM go_group_status WHERE q_value <= 0.01 AND annotation_status = s) "
                + "FROM (SELECT unnest(['annotated', 'no_go_terms', 'no_entry', 'contaminant']) AS s)")
            .ToDictionary(r => (string)r[0]!, r => Convert.ToString(r[1], CultureInfo.InvariantCulture)!, StringComparer.Ordinal);

    // --- go's fixture ----------------------------------------------------------------------------------------

    [Test]
    public void GosFixtureIsReproducedRowForRow()
    {
        var terms = GoOntologyTsv.ReadTerms(Fixture("go_terms.tsv"));
        var accessionTerms = GoAccessionTermsTsv.Read(Fixture("go_accession_terms.tsv"));
        var members = MemberResolutionTsv.Read(Fixture("member_resolution.tsv"));
        var dbSha = accessionTerms.Header["annotation_db_sha256"];
        using var con = Catalog(terms, GoOntologyTsv.ReadClosure(Fixture("go_closure.tsv")));
        CatalogBuilder.LoadGoAccessionTerms(con, Parquet(CatalogBuilder.GoAccessionTermsTable, GoReferenceEngines.AccessionTermRows(accessionTerms)), dbSha, "a");
        CatalogBuilder.LoadGoMembers(con, Parquet(CatalogBuilder.GoGroupMembersTable, GoReferenceEngines.MemberRows(members)), "FIXTURE", "b", dbSha, "c");
        var groupRows = File.ReadAllLines(Fixture("protein_groups.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t'))
            .Select(c => (c[0], c[1], c[2] == "true", double.Parse(c[3], CultureInfo.InvariantCulture))).ToList();
        Assert.That(groupRows, Has.Count.EqualTo(18));
        var groups = Groups(con, groupRows);
        var search = new CatalogBuilder.GoSearch("FIXTURE", "b", dbSha, [dbSha], "go:test", "test", terms.Header["go_release"]);
        CatalogBuilder.DeriveGo(con, search, groups);

        var (header, expected) = ReadGoFile(Fixture("expected_go_annotation.tsv"));
        Assert.That(expected, Has.Count.EqualTo(165));
        Assert.Multiple(() =>
        {
            Assert.That(CatalogGroupRows(con, search, groups), Is.EqualTo(ExpectedGroupRows(expected)), "every group row, all aspects");
            var localizations = ExpectedLocalizations(expected, header["go_release"]);
            Assert.That(localizations, Has.Count.GreaterThan(50));
            Assert.That(CatalogLocalizations(con), Is.EqualTo(localizations), "protein_localizations, as v1 expanded the file");
            var counters = Counters(con);
            foreach (var key in counters.Keys) Assert.That(counters[key], Is.EqualTo(header[key]), $"go D26 counter {key}");
            Assert.That(CatalogBuilder.Query(con, "SELECT DISTINCT annotation_status FROM go_group_status").Select(r => r[0]),
                Is.EquivalentTo(CatalogBuilder.GoStatuses), "the fixture reaches every status");
            Assert.That(CatalogBuilder.Query(con, "SELECT count(*) FROM annotation_sources").Single()[0], Is.EqualTo(1L));
        });
    }

    // --- an isoform in another file than its entry -------------------------------------------------------------

    private const string Nucleus = "GO:0005634";
    private const string Mitochondrion = "GO:0005739";
    private const string InnerMembrane = "GO:0005743";
    private const string Gapdh = "GO:0004365";

    private static string Entry(string accession, params (string Id, string Evidence)[] go) =>
        $"<entry>\n  <accession>{accession}</accession>\n"
        + string.Concat(go.Select(g => $"  <dbReference type=\"GO\" id=\"{g.Id}\">\n    <property type=\"term\" value=\"C:a term\"/>\n"
            + $"    <property type=\"evidence\" value=\"{g.Evidence}\"/>\n  </dbReference>\n"))
        + "  <sequence>MKWVTFISLLFLFSSAYSR</sequence>\n</entry>\n";

    private string Database(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<uniprot xmlns=\"http://uniprot.org/uniprot\">\n"
            + string.Concat(entries) + "</uniprot>\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>A search that read two target files: the canonical proteome, and a file of isoforms that also holds one of
    /// the canonical entries with other evidence.</summary>
    /// <remarks>
    /// <c>P04406-2</c> has no GO of its own and its entry <c>P04406</c> is in both files, so the annotator over the two
    /// files gives it P04406's terms from BOTH (nucleus with two codes, GAPDH activity), where the isoform file alone gives
    /// it only that file's copy (nucleus, one code). <c>Q13148-3</c> has GO of its own and keeps it. The groups reach a
    /// member the files lack (<c>P04406-9</c>, which takes its entry), a variant of the isoform (<c>P04406-2_A20T</c>,
    /// which takes the isoform and so its entry's terms: mzLib #1497's corner), a member with no entry, and a contaminant.
    /// </remarks>
    [Test]
    public void AnIsoformInAnotherFileThanItsEntryTakesTheEntrysTermsAsTheAnnotatorOverAllFilesGivesThem()
    {
        var canonical = Database("canonical.xml",
            Entry("P04406", (Nucleus, "ECO:0000314"), (Gapdh, "ECO:0000269")),
            Entry("Q13148", (InnerMembrane, "ECO:0000250")),
            Entry("P11111", (Mitochondrion, "ECO:0000314")));
        var isoforms = Database("isoforms.xml",
            Entry("P04406", (Nucleus, "ECO:0000501")),
            Entry("P04406-2"),
            Entry("Q13148-3", (Gapdh, "ECO:0000314")));
        var files = new[] { canonical, isoforms }.Select(p => (Path: p, Sha: BundleWriter.Sha256File(p))).ToList();
        var setSha = CatalogBuilder.AnnotationDbSha256(files.Select(f => f.Sha).ToList());
        var all = Specificity.LoadProteins(files.Select(f => (f.Path, f.Sha, false)));
        Assert.That(all.Select(p => p.Accession), Is.EqualTo(new[] { "P04406", "Q13148", "P11111", "P04406", "P04406-2", "Q13148-3" }));

        var ontology = GeneOntologyGraph.Load(Ontology);
        var groups = new List<GoAnnotationGroup>
        {
            new("P04406-2", ["P04406-2"], false, false, 0.001),
            new("P04406|P04406-2", ["P04406", "P04406-2"], false, false, 0.001),
            new("P04406-2_A20T", ["P04406-2_A20T"], false, false, 0.001),
            new("Q13148-3", ["Q13148-3"], false, false, 0.002),
            new("P04406-9", ["P04406-9"], false, false, 0.03),
            new("P11111|Q13148-3", ["P11111", "Q13148-3"], false, false, 0.001),
            new("Q9ZZZ9", ["Q9ZZZ9"], false, false, 0.001),
            new("P99998", ["P99998"], false, true, 0.001),
        };

        // The annotator over everything the search read: what go.annotate_groups stored.
        var annotator = new GoGroupAnnotator(ontology, all, setSha, skipUnknownGoIds: true);
        var expectedPath = Path.Combine(_root, "expected.tsv");
        using (var w = new StreamWriter(expectedPath, false, new UTF8Encoding(false)))
            GoAnnotationTsv.Write(w, annotator.AnnotateAll(groups), ontology, setSha, null, annotator.UnresolvedGoIds);

        // The stored tables: (a) per FILE, (c) over the search's files together.
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var perFile = files.Select(f =>
        {
            var dir = Directory.CreateDirectory(Path.Combine(work, Path.GetFileNameWithoutExtension(f.Path))).FullName;
            return (f.Sha, GoReferenceEngines.AnnotateFile(ontology, Specificity.LoadProteins([(f.Path, f.Sha, false)]), f.Sha, dir, f.Path).Table);
        }).ToList();
        var isoformRows = perFile[1].Table.Rows.Where(r => r.Accession == "P04406-2").ToList();
        Assert.That(isoformRows.Select(r => (r.GoId, string.Join(";", r.Evidence), r.Inherited)), Is.EqualTo(new[] { (Nucleus, "ECO:0000501", (bool?)true) }),
            "the isoform file alone gives the isoform only that file's copy of its entry: the join must not read it");
        var (members, _) = GoReferenceEngines.ResolveGroups(groups, all, setSha, null, work, "test");
        Assert.That(members.Rows.Single(r => r.Accession == "P04406-2").Kind, Is.EqualTo(MemberResolutionKind.Self),
            "the member resolution does not carry D37: an isoform the databases hold is itself");

        var resolver = new GoCategoryResolver(GoCategoryMap.Load(Map), ontology);
        var categoryPath = Path.Combine(work, "categories.tsv");
        using (var w = new StreamWriter(categoryPath, false, new UTF8Encoding(false))) GoCategoryTsv.Write(w, resolver);
        var termsPath = Path.Combine(work, "terms.tsv");
        var closurePath = Path.Combine(work, "closure.tsv");
        using (var w = new StreamWriter(termsPath, false, new UTF8Encoding(false))) GoOntologyTsv.WriteTerms(w, ontology);
        using (var w = new StreamWriter(closurePath, false, new UTF8Encoding(false))) GoOntologyTsv.WriteClosure(w, ontology);
        using var con = Catalog(GoOntologyTsv.ReadTerms(termsPath), GoOntologyTsv.ReadClosure(closurePath), GoCategoryTsv.Read(categoryPath));
        foreach (var (sha, table) in perFile)
            CatalogBuilder.LoadGoAccessionTerms(con, Parquet(CatalogBuilder.GoAccessionTermsTable, GoReferenceEngines.AccessionTermRows(table)), sha, sha[..8]);
        CatalogBuilder.LoadGoMembers(con, Parquet(CatalogBuilder.GoGroupMembersTable, GoReferenceEngines.MemberRows(members)), "PXDTEST", "b", setSha, "c");
        var groupsSql = Groups(con, groups.Select(g => (g.Name, string.Join(";", g.MemberAccessions), g.IsContaminant, g.QValue)));
        var search = new CatalogBuilder.GoSearch("PXDTEST", "b", setSha, files.Select(f => f.Sha).ToList(), "go:test", "test", ontology.Release);
        CatalogBuilder.DeriveGo(con, search, groupsSql);

        var (header, expected) = ReadGoFile(expectedPath);
        Assert.Multiple(() =>
        {
            Assert.That(CatalogGroupRows(con, search, groupsSql), Is.EqualTo(ExpectedGroupRows(expected)), "every group row, all aspects");
            Assert.That(CatalogLocalizations(con), Is.EqualTo(ExpectedLocalizations(expected, header["go_release"])), "protein_localizations");
            var isoform = CatalogBuilder.Query(con,
                "SELECT evidence, inherited, propagated FROM protein_localizations WHERE protein_group = 'P04406-2' AND compartment = ?", Nucleus).Single();
            Assert.That(isoform, Is.EqualTo(new object?[] { "ECO:0000314;ECO:0000501", true, false }),
                "the entry's nucleus from both files, inherited");
            var counters = Counters(con);
            foreach (var key in counters.Keys) Assert.That(counters[key], Is.EqualTo(header[key]), $"go D26 counter {key}");
            // v1's category file: the map over the terms of the annotator's rows.
            var v1Categories = Path.Combine(_root, "expected_categories.tsv");
            using (var w = new StreamWriter(v1Categories, false, new UTF8Encoding(false))) GoCategoryTsv.Write(w, resolver, annotator.AnnotateAll(groups));
            var expectedCategories = GoCategoryTsv.Read(v1Categories).Rows.Select(r => $"{r.GoId}|{r.Category}|{r.Subcategory}").Order(StringComparer.Ordinal).ToList();
            Assert.That(expectedCategories, Is.EqualTo(new[] { $"{Nucleus}|nucleus|", $"{Mitochondrion}|mitochondrion|" }),
                "P11111's mitochondrion and P04406's nucleus; Q13148's inner membrane is no member's (Q13148-3 keeps its own GO)");
            Assert.That(CatalogBuilder.Query(con, "SELECT compartment, organelle_category, organelle_subcategory, category_map_name, organelle_map_version FROM organelle_term_categories")
                    .Select(r => $"{r[0]}|{r[1]}|{r[2]}").Order(StringComparer.Ordinal),
                Is.EqualTo(expectedCategories), "organelle_term_categories: the map's rows for the terms the groups carry, as v1's category file");
        });
    }
}
