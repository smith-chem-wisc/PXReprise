using System.Globalization;
using DuckDB.NET.Data;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Runner;
using Proteomics;
using UsefulProteomicsDatabases.Annotation;
using UsefulProteomicsDatabases.Pathways;

namespace PXReprise.Data.Tests;

/// <summary>The catalog's Reactome join (<c>CatalogReactome.cs</c>) gives the rows of <c>kegg:DEF-REACTOME-MEMBERSHIP v1</c>
/// over a search's whole target set from per-file artefacts: mzLib's own hand-written expectation, row for row, whether the
/// set is one file or split over several.</summary>
/// <remarks>
/// <para>The fixture, the groups and the expected answers are ported from mzLib 1.0.598's
/// <c>TestReactomeMembership.Fixture</c> and <c>TestReactomePerDatabaseTables</c>, whose expectation is written by hand
/// from the definition and checked there against <c>ReactomeAccessionAnnotation</c> over the whole set. Here each split
/// file is annotated alone by the engine's own code (<see cref="ReactomeEngines.AnnotateFile"/>: mzLib writes its file
/// and reads it back), loaded with the build's own loader, and joined by the build's own SQL
/// (<see cref="CatalogBuilder.DeriveReactome"/>), so nothing here restates the join. The member resolution is mzLib's
/// <c>MemberResolver</c> over all the files' proteins, as <c>go.resolve_members</c> resolves a search.</para>
/// <para>The splits reach both rules the catalog applies across files: an isoform in another file than its entry (it
/// inherits), an organism no file alone has 10 references for (its code is read over the union, and its foreign id
/// dropped), and one accession whose two copies are in two files (unioned).</para>
/// </remarks>
public class ReactomeCatalogTests
{
    private const string Human = "9606";
    private const string Mouse = "10090";
    private const string Rat = "10116";
    private const string Chicken = "9031";
    private const string Yeast = "559292";

    private static DatabaseReference Taxon(string id) => new("NCBI Taxonomy", id, new List<Tuple<string, string>>());

    private static DatabaseReference Reactome(string id, string? molecule = null) =>
        new("Reactome", id, new[] { Tuple.Create(ReactomePathway.PathwayNamePropertyType, "name of " + id) }, molecule);

    private static DatabaseReference Kegg(string id) => new("KEGG", id, new List<Tuple<string, string>>());

    private static Protein P(string accession, string taxon, params DatabaseReference[] references) =>
        new("PEPTIDEK", accession, databaseReferences: references.Prepend(Taxon(taxon)).ToList());

    private static Protein Displaying(string accession, string displayedIsoform, string taxon, params DatabaseReference[] references) =>
        new("PEPTIDEK", accession, databaseReferences: references.Prepend(Taxon(taxon)).ToList(),
            uniProtSequenceAttributes: new UniProtSequenceAttributes(8, 0, "", DateTime.MinValue, 1, null,
                UniProtSequenceAttributes.FragmentType.unspecified, displayedIsoform));

    /// <summary>mzLib's <c>TestReactomeMembership.Fixture</c>, entry for entry, by name so a split can place each.</summary>
    private static readonly (string Key, Protein Protein)[] Fixture =
    [
        ("P11111", P("P11111", Human, Reactome("R-HSA-1001"), Reactome("R-HSA-1002"), Kegg("hsa:1111"))),
        ("P11111_A20T", P("P11111_A20T", Human, Reactome("R-HSA-1001"), Reactome("R-HSA-1002"), Kegg("hsa:1111"))),
        ("P22222", Displaying("P22222", "P22222-2", Human,
            Reactome("R-HSA-2001", "P22222-2"), Reactome("R-HSA-2002", "P22222-3"), Reactome("R-HSA-2003"), Kegg("hsa:2223"), Kegg("hsa:2222"))),
        ("P22222-3", P("P22222-3", Human)),
        ("P22222-4", P("P22222-4", Human, Reactome("R-HSA-2004"))),
        ("P33333", P("P33333", Human, Reactome("R-HSA-3001"), Reactome("R-MMU-3002"))),
        ("P55555", P("P55555", Human)),
        ("P66666a", P("P66666", Human, Reactome("R-HSA-6001"), Kegg("hsa:6666"))),
        ("P66666b", P("P66666", Human, Reactome("R-HSA-6002"), Kegg("hsa:6666"))),
        ("P77777", P("P77777", Human, Reactome("R-HSA-7701", "P77777-2"))),
        ("Q11111", P("Q11111", Mouse, Kegg("mmu:1111"))),
        ("Q22222", P("Q22222", Mouse)),
        ("Q33331", P("Q33331", Rat, Reactome("R-RNO-4001"))),
        ("Q33332", P("Q33332", Rat, Reactome("R-RNO-4002"))),
        ("Q33333", P("Q33333", Rat, Reactome("R-HSA-4003"))),
        ("Q44441", P("Q44441", Chicken, Reactome("R-GGA-5001"), Reactome("R-GGA-5002"), Reactome("R-GGA-5003"))),
        ("Q44442", P("Q44442", Chicken, Reactome("R-GGA-5004"), Reactome("R-GGA-5005"), Reactome("R-GGA-5006"))),
        ("Q44443", P("Q44443", Chicken, Reactome("R-HSA-5007"), Reactome("R-HSA-5008"), Reactome("R-HSA-5009"))),
        ("Q44444", P("Q44444", Chicken, Reactome("R-HSA-5010"), Reactome("R-HSA-5011"), Reactome("R-HSA-5012"))),
        ("Random_foreign_Q99999", P("Random_foreign_Q99999", Yeast, Reactome("R-SCE-7001"))),
        ("DECOY_P11111", new Protein("PEPTIDEK", "DECOY_P11111", isDecoy: true,
            databaseReferences: new List<DatabaseReference> { Taxon(Human), Reactome("R-HSA-1001"), Kegg("hsa:1111") })),
    ];

    /// <summary>mzLib's <c>TestReactomePerDatabaseTables.Groups</c>: (name, members, contaminant mark).</summary>
    private static readonly (string Name, string Members, bool Contaminant)[] Groups =
    [
        ("G1", "P11111|P11111_A20T", false),
        ("G2", "P22222|P22222-3|P22222-2", false),
        ("G3", "P11111", true),
        ("G4", "CON_P02768", true),
        ("G5", "Random_foreign_Q99999|P55555", false),
        ("G6", "P11111_2|P55555", false),
        ("G7", "P11111_2|Q11111", false),
        ("G8", "Q11111|P55555", false),
        ("G9", "P55555", false),
        ("G10", "P11111|Random_P11111_f0|CON_P02768", false),
        ("G11", "P22222-3_A20T|P22222-5|P22222-4", false),
        ("G12", "Q33333|Q44441", false),
        ("G13", "P33333|P66666", false),
        ("G14", "CON_P02768", false),
        ("G15", "P77777|P77777-2|P77777-3", false),
        ("G16", "P77777-3", false),
    ];

    /// <summary>mzLib's hand-written member answers: status and pathways.</summary>
    private static readonly Dictionary<string, string> ExpectedMembers = new(StringComparer.Ordinal)
    {
        ["P11111"] = "annotated[R-HSA-1001,R-HSA-1002]",
        ["P11111_A20T"] = "annotated[R-HSA-1001,R-HSA-1002]",
        ["P11111_2"] = "no_entry[]",
        ["P22222"] = "annotated[R-HSA-2001,R-HSA-2003]",
        ["P22222-2"] = "annotated[R-HSA-2003]",
        ["P22222-3"] = "annotated[R-HSA-2002,R-HSA-2003]",
        ["P22222-3_A20T"] = "annotated[R-HSA-2002,R-HSA-2003]",
        ["P22222-4"] = "annotated[R-HSA-2004]",
        ["P22222-5"] = "annotated[R-HSA-2003]",
        ["P33333"] = "annotated[R-HSA-3001]",
        ["P55555"] = "no_reference[]",
        ["P66666"] = "annotated[R-HSA-6001,R-HSA-6002]",
        ["P77777"] = "no_reference[]",
        ["P77777-2"] = "annotated[R-HSA-7701]",
        ["P77777-3"] = "no_reference[]",
        ["Q11111"] = "species_not_covered[]",
        ["Q33333"] = "annotated[R-HSA-4003]",
        ["Q44441"] = "annotated[R-GGA-5001,R-GGA-5002,R-GGA-5003]",
        ["Random_foreign_Q99999"] = "annotated[R-SCE-7001]",
        ["Random_P11111_f0"] = "no_entry[]",
        ["CON_P02768"] = "no_entry[]",
    };

    /// <summary>mzLib's hand-written group answers: status, member count, each pathway's carriers, entrapment.</summary>
    private static readonly Dictionary<string, string> ExpectedGroups = new(StringComparer.Ordinal)
    {
        ["G1"] = "annotated n=2 {R-HSA-1001:P11111,P11111_A20T; R-HSA-1002:P11111,P11111_A20T} entrapment=[]",
        ["G2"] = "annotated n=3 {R-HSA-2001:P22222; R-HSA-2002:P22222-3; R-HSA-2003:P22222,P22222-2,P22222-3} entrapment=[]",
        ["G3"] = "annotated n=1 {R-HSA-1001:P11111; R-HSA-1002:P11111} entrapment=[]",
        ["G4"] = "contaminant n=1 {} entrapment=[]",
        ["G5"] = "annotated n=2 {R-SCE-7001:Random_foreign_Q99999} entrapment=[Random_foreign_Q99999]",
        ["G6"] = "no_entry n=2 {} entrapment=[]",
        ["G7"] = "no_entry n=2 {} entrapment=[]",
        ["G8"] = "species_not_covered n=2 {} entrapment=[]",
        ["G9"] = "no_reference n=1 {} entrapment=[]",
        ["G10"] = "annotated n=3 {R-HSA-1001:P11111; R-HSA-1002:P11111} entrapment=[Random_P11111_f0]",
        ["G11"] = "annotated n=3 {R-HSA-2002:P22222-3_A20T; R-HSA-2003:P22222-3_A20T,P22222-5; R-HSA-2004:P22222-4} entrapment=[]",
        ["G12"] = "annotated n=2 {R-GGA-5001:Q44441; R-GGA-5002:Q44441; R-GGA-5003:Q44441; R-HSA-4003:Q33333} entrapment=[]",
        ["G13"] = "annotated n=2 {R-HSA-3001:P33333; R-HSA-6001:P66666; R-HSA-6002:P66666} entrapment=[]",
        ["G14"] = "no_entry n=1 {} entrapment=[]",
        ["G15"] = "annotated n=3 {R-HSA-7701:P77777-2} entrapment=[]",
        ["G16"] = "no_reference n=1 {} entrapment=[]",
    };

    private string _root = "";

    [SetUp]
    public void MakeScratch() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "datarepo-reactome-catalog-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Parquet(string table, IReadOnlyDictionary<string, IReadOnlyList<ColumnSpec>> columns, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var path = Path.Combine(_root, $"{table}-{Guid.NewGuid():N}.parquet");
        ArrowTables.WriteParquet(ArrowTables.FromRows(table, columns[table], rows), path);
        return path;
    }

    /// <summary>A catalog holding the given files' membership artefacts, each annotated alone by the engine's code, and the
    /// search over all of them; the search, ready to derive.</summary>
    private (DuckDBConnection Con, CatalogBuilder.ReactomeSearch Search, Dictionary<string, long> Dropped) Catalog(IReadOnlyList<Protein[]> files)
    {
        var con = CatalogBuilder.Open(":memory:", readOnly: false);
        CatalogBuilder.CreateGoTables(con);
        CatalogBuilder.CreateReactomeTables(con);
        var shas = new List<string>();
        var dropped = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            var sha = $"{i:x2}{new string('f', 62)}";
            shas.Add(sha);
            var work = Path.Combine(_root, $"file{i}");
            Directory.CreateDirectory(work);
            var (membership, _, links, _) = ReactomeEngines.AnnotateFile(files[i], sha, work, $"file {i}");
            dropped[sha] = long.Parse(membership.Header.GetValueOrDefault("dropped_foreign_references") ?? "0", CultureInfo.InvariantCulture);
            CatalogBuilder.LoadReactomeMembership(con,
                Parquet(CatalogBuilder.ReactomeMembershipTable, ReactomeEngines.Columns, ReactomeEngines.MembershipRows(membership)),
                Parquet(CatalogBuilder.KeggGeneLinksTable, ReactomeEngines.Columns, ReactomeEngines.KeggRows(links)),
                Parquet(CatalogBuilder.ReactomeSpeciesTable, ReactomeEngines.Columns, ReactomeEngines.SpeciesRows(membership)),
                sha, $"artefact-{i}");
        }
        var all = files.SelectMany(f => f).ToList();
        var set = CatalogBuilder.AnnotationDbSha256(shas);
        var resolved = new MemberResolver(all).ResolveAll(Groups.SelectMany(g => g.Members.Split('|')));
        var resolutionPath = Path.Combine(_root, "c.tsv");
        using (var w = new StreamWriter(resolutionPath)) MemberResolutionTsv.Write(w, resolved, set);
        var members = Parquet(CatalogBuilder.GoGroupMembersTable, GoReferenceEngines.Columns, GoReferenceEngines.MemberRows(MemberResolutionTsv.Read(resolutionPath)));
        return (con, new CatalogBuilder.ReactomeSearch("PXD000000", "bundle", set, shas, members, "source", "version", "96"), dropped);
    }

    /// <summary>The fixture groups as the derivation reads them.</summary>
    private static string GroupsSql(DuckDBConnection con)
    {
        CatalogBuilder.Exec(con, "CREATE TABLE test_groups (protein_group VARCHAR, protein_accessions VARCHAR, is_contaminant BOOLEAN, q_value DOUBLE)");
        foreach (var g in Groups)
            CatalogBuilder.Exec(con, "INSERT INTO test_groups VALUES (?, ?, ?, ?)", g.Name, g.Members, g.Contaminant, 0.0);
        return "SELECT protein_group AS group_key, protein_group, is_contaminant, q_value, "
            + "list_distinct(list_filter(string_split(protein_accessions, '|'), a -> a <> '')) AS members FROM test_groups";
    }

    private static Protein[] Pick(params string[] keys) => keys.Select(k => Fixture.Single(f => f.Key == k).Protein).ToArray();

    private static string[] Keys(Func<string, bool> where) => Fixture.Select(f => f.Key).Where(where).ToArray();

    /// <summary>The fixture split over files, each a named case.</summary>
    private static IEnumerable<TestCaseData> Splits()
    {
        yield return new TestCaseData((object)new[] { Keys(_ => true) }).SetName("OneFile");
        // P22222-3 (no reference of its own) and P22222-4 in a file without their entry P22222.
        yield return new TestCaseData((object)new[]
        {
            Keys(k => k is not ("P22222-3" or "P22222-4")), new[] { "P22222-3", "P22222-4" },
        }).SetName("IsoformsApartFromTheirEntry");
        // No file holds 10 human references, so the human code is read over the union and R-MMU-3002 dropped there; the
        // two copies of P66666 are in two files; P22222-3 is apart from its entry.
        yield return new TestCaseData((object)new[]
        {
            new[] { "P11111", "P11111_A20T", "P22222", "P33333" },
            new[] { "P22222-3", "P22222-4", "P55555", "P66666a", "P77777" },
            Keys(k => k is "P66666b" || k.StartsWith('Q') || k.StartsWith("Random", StringComparison.Ordinal) || k.StartsWith("DECOY", StringComparison.Ordinal)),
        }).SetName("NoFileReadsTheHumanCode");
    }

    private static (Dictionary<string, string> Members, Dictionary<string, string> Groups) Answers(DuckDBConnection con)
    {
        var members = CatalogBuilder.Query(con, $@"
            SELECT protein_accession, any_value(reactome_status), list_sort(list(pathway_id) FILTER (WHERE pathway_id IS NOT NULL))
            FROM {CatalogBuilder.ProteinReactomeTable} GROUP BY protein_accession")
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]}[{string.Join(",", ((System.Collections.IEnumerable?)r[2])?.Cast<object?>() ?? [])}]", StringComparer.Ordinal);
        var carriers = CatalogBuilder.Query(con, $"SELECT protein_group_id, pathway_id, carriers FROM {CatalogBuilder.ReactomeGroupPathwaysTable} ORDER BY 1, 2")
            .GroupBy(r => (string)r[0]!)
            .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(r => $"{r[1]}:{string.Join(",", ((System.Collections.IEnumerable)r[2]!).Cast<object?>())}")));
        var groups = CatalogBuilder.Query(con, $"SELECT protein_group_id, reactome_status, members, entrapment_members FROM {CatalogBuilder.ReactomeGroupStatusTable}")
            .ToDictionary(r => (string)r[0]!, r => $"{r[1]} n={r[2]} {{{carriers.GetValueOrDefault((string)r[0]!, "")}}} "
                + $"entrapment=[{string.Join(",", ((System.Collections.IEnumerable)r[3]!).Cast<object?>())}]", StringComparer.Ordinal);
        return (members, groups);
    }

    [TestCaseSource(nameof(Splits))]
    public void PerFileArtefactsJoinedOverTheSearchsFilesGiveTheDefinitionsRows(string[][] split)
    {
        var (con, search, dropped) = Catalog(split.Select(f => Pick(f)).ToList());
        using (con)
        {
            var groups = GroupsSql(con);
            CatalogBuilder.DeriveReactome(con, search, groups);
            var (members, answers) = Answers(con);
            Assert.Multiple(() =>
            {
                Assert.That(members.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"),
                    Is.EqualTo(ExpectedMembers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")), "every member, as mzLib's expectation");
                Assert.That(answers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"),
                    Is.EqualTo(ExpectedGroups.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")), "every group, as mzLib's expectation");
                Assert.That(CatalogBuilder.ReactomeSetCodes(con, search, groups), Is.EqualTo(new[] { ("NCBITaxon:9606", "HSA") }),
                    "human's code, read in a file or over the union; rat and chicken have none (too few, a tie)");
                Assert.That(CatalogBuilder.ReactomeInexact(con, search, groups, dropped), Is.Empty, "the per-file artefacts are exact here");
                Assert.That(CatalogBuilder.Query(con, $"SELECT DISTINCT inherited FROM {CatalogBuilder.ProteinReactomeTable} "
                    + "WHERE protein_accession = 'P22222-3' AND pathway_id = 'R-HSA-2003'").Single()[0], Is.EqualTo(true),
                    "P22222-3 takes R-HSA-2003 as its entry's");
                Assert.That(CatalogBuilder.Query(con, "SELECT owner_project, source_id FROM annotation_sources").Single(), Is.EqualTo(new object?[] { "kegg", "source" }));
            });
        }
    }

    /// <summary>On aging's human target set as kegg checked it (the UniProt 2026_03 proteome and aging's isoform overlay,
    /// 20,652 and 104 entries), the catalog's rows from the two per-file artefacts equal mzLib's over both files loaded
    /// together: every (entry, pathway, isoform scope, inherited) row and every entry's status. The overlay's isoforms
    /// with no reference of their own take their entries' pathways from the other file (kegg counted 81).</summary>
    [Test, Category("RealData")]
    public void OnAgingsHumanTargetSetThePerFileRowsAreMzLibsOverTheSet()
    {
        string[] paths = [@"F:\aging_data\db\UP000005640_all_genecentric_2026_03.xml", @"F:\aging_data\db\aging_isoforms_human.xml"];
        if (!paths.All(File.Exists)) Assert.Ignore("aging's human databases are not on this machine");
        var files = paths.Select(p => PXReprise.Ingest.Sources.Specificity.LoadProteins([(p, BundleWriter.Sha256File(p), false)]).ToArray()).ToList();

        var whole = new ReactomeAccessionAnnotation(files.SelectMany(f => f), "set");
        var expectedRows = whole.Rows().Where(r => r.PathwayId is not null)
            .Select(r => $"{r.Accession}|{r.PathwayId}|{r.SequenceAccession ?? "NULL"}|{(r.Inherited == true ? "true" : "false")}")
            .Order(StringComparer.Ordinal).ToList();
        var expectedStatus = whole.Rows().GroupBy(r => r.Accession).Select(g => $"{g.Key}|{ReactomeEngines.StatusName(g.First().Status)}")
            .Order(StringComparer.Ordinal).ToList();

        var (con, search, dropped) = Catalog(files);
        using (con)
        {
            var groups = GroupsSql(con);
            var rows = CatalogBuilder.Query(con, CatalogBuilder.ReactomeSql(search, groups,
                    "SELECT accession, pathway_id, coalesce(sequence_accession, 'NULL'), CASE WHEN inherited THEN 'true' ELSE 'false' END FROM eff"))
                .Select(r => string.Join("|", r)).Order(StringComparer.Ordinal).ToList();
            var status = CatalogBuilder.Query(con, CatalogBuilder.ReactomeSql(search, groups, "SELECT accession, status FROM entry"))
                .Select(r => string.Join("|", r)).Order(StringComparer.Ordinal).ToList();
            TestContext.Out.WriteLine($"{files[0].Length} + {files[1].Length} entries; {rows.Count} pathway rows; {status.Count} statuses; "
                + $"{rows.Count(r => r.EndsWith("|true", StringComparison.Ordinal))} inherited rows on "
                + $"{rows.Where(r => r.EndsWith("|true", StringComparison.Ordinal)).Select(r => r.Split('|')[0]).Distinct().Count()} isoforms; dropped {string.Join(", ", dropped.Values)}");
            Assert.Multiple(() =>
            {
                Assert.That(rows, Has.Count.GreaterThan(40_000));
                Assert.That(rows.Except(expectedRows), Is.Empty, "no row mzLib does not give");
                Assert.That(expectedRows.Except(rows), Is.Empty, "every row mzLib gives");
                Assert.That(status, Is.EqualTo(expectedStatus), "every entry's status");
                Assert.That(rows.Where(r => r.EndsWith("|true", StringComparison.Ordinal)).Select(r => r.Split('|')[0]).Distinct().Count(),
                    Is.GreaterThanOrEqualTo(81), "the overlay's isoforms inherit across the two files");
                Assert.That(CatalogBuilder.ReactomeInexact(con, search, groups, dropped), Is.Empty);
                Assert.That(CatalogBuilder.ReactomeSetCodes(con, search, groups), Is.EqualTo(new[] { ("NCBITaxon:9606", "HSA") }));
            });
        }
    }

    /// <summary>The two cases per-file artefacts cannot settle are named, never guessed: an organism whose code one file
    /// read while another file's references of other prefixes, with the ones the code's file dropped, could outvote it;
    /// and an isoform a file's species check may have stripped, held apart from its entry.</summary>
    [Test]
    public void WhatPerFileArtefactsCannotSettleIsNamed()
    {
        Protein Many(string accession, string prefix, int count) =>
            P(accession, Human, Enumerable.Range(1, count).Select(i => Reactome($"R-{prefix}-{1000 + i}")).ToArray());

        // File 0 reads HSA (10 against 9) and drops 9; file 1 has 2 MMU references and reads none. Over the set it is 10
        // against 11: no majority, and nothing dropped. The catalog cannot tell from the files.
        var (con, search, dropped) = Catalog([[Many("P00001", "HSA", 10), Many("P00002", "MMU", 9)], [Many("P00003", "MMU", 2)]]);
        using (con)
        {
            Assert.That(dropped.Values, Is.EqualTo(new[] { 9L, 0L }));
            var inexact = CatalogBuilder.ReactomeInexact(con, search, GroupsSql(con), dropped);
            Assert.That(inexact.Single(), Does.Contain("NCBITaxon:9606's code HSA has 10 references against 2 of other prefixes and up to 9"));
        }

        // A clear majority is exact: 12 against 1 dropped and 1 kept elsewhere.
        (con, search, dropped) = Catalog([[Many("P00001", "HSA", 12), Many("P00002", "MMU", 1)], [Many("P00003", "MMU", 1)]]);
        using (con) Assert.That(CatalogBuilder.ReactomeInexact(con, search, GroupsSql(con), dropped), Is.Empty);

        // P00009-2's only reference is foreign, so its file drops it; over the set it would not inherit, but here its entry
        // is in the other file and the file cannot say it had a reference.
        (con, search, dropped) = Catalog([[Many("P00001", "HSA", 12), P("P00009-2", Human, Reactome("R-MMU-9001"))], [P("P00009", Human, Reactome("R-HSA-9001"))]]);
        using (con)
        {
            var inexact = CatalogBuilder.ReactomeInexact(con, search, GroupsSql(con), dropped);
            Assert.That(inexact.Single(), Does.Contain("P00009-2 inherits its entry's rows"));
        }

        // Beside its entry, the same isoform is settled from the file itself: it had a reference, so it does not inherit.
        (con, search, dropped) = Catalog([[Many("P00001", "HSA", 12), P("P00009-2", Human, Reactome("R-MMU-9001")), P("P00009", Human, Reactome("R-HSA-9001"))]]);
        using (con)
        {
            var groups = GroupsSql(con);
            Assert.That(CatalogBuilder.ReactomeInexact(con, search, groups, dropped), Is.Empty);
            Assert.That(CatalogBuilder.Query(con, CatalogBuilder.ReactomeSql(search, groups, "SELECT status FROM entry WHERE accession = 'P00009-2'")).Single()[0],
                Is.EqualTo("no_reference"), "as mzLib over the set: its reference was foreign, so it inherits nothing");
        }
    }
}
