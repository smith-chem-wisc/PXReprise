using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary><c>Sources/Go</c> (go's two output files, read and checked, G53/G86) against what the Python wrote.</summary>
/// <remarks>
/// Inputs are go's own pre-release files in <c>tests/data/go</c>. Every expected value comes from
/// <c>Fixtures/go/cases.json</c>, which <c>sources/go.py</c> (dataRepo <c>fcdedcb</c>) produced from the same files
/// and from edited copies of them; see its PROVENANCE.md. Each edited copy is rebuilt here by the same edit and its
/// sha256 checked against the Python's before anything is compared, so both sides read the same bytes. The first
/// fifteen tests are <c>tests/test_go.py</c>, one for one.
/// </remarks>
public class GoTests
{
    private static readonly string FixtureDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "go");
    private static readonly Lazy<Dictionary<string, object?>> CasesLazy = new(() => Provenance.Load(Path.Combine(FixtureDir, "cases.json")));
    private static Dictionary<string, object?> Cases => CasesLazy.Value;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static string GoDir => Path.Combine(RepoRoot(), "tests", "data", "go");
    private static string Annotation => Path.Combine(GoDir, "fixture1347_go_annotation.tsv");
    private static string Categories => Path.Combine(GoDir, "fixture1347_go_category_smoke.tsv");
    private static readonly List<string> Columns = [.. Go.AnnotationColumns];

    private string _tmp = "";

    [SetUp]
    public void MakeTemp() => Directory.CreateDirectory(_tmp = Path.Combine(Path.GetTempPath(), "datarepo-go-" + Guid.NewGuid().ToString("N")));

    [TearDown]
    public void RemoveTemp()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    private static string Py(object? value) => PyFormat.Json(value, sortKeys: false);
    private static Dictionary<string, object?> D(object? value) => (Dictionary<string, object?>)value!;
    private static List<object?> L(object? value) => (List<object?>)value!;
    private static string ReadText(string path) => new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));

    private string Write(string name, string text, string? subdir = null)
    {
        var dir = subdir is null ? _tmp : Path.Combine(_tmp, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    /// <summary><c>_copy</c>: an edited copy of a fixture file, under its own name.</summary>
    private string Copy(string source, Func<string, string> edit)
    {
        var text = ReadText(source);
        var edited = edit(text);
        Assert.That(edited, Is.Not.EqualTo(text), "the edit did not apply");
        return Write(Path.GetFileName(source), edited);
    }

    // ---------------------------------------------------------------- the test file's edits, as go.py's tests wrote them

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replacement + text[(at + old.Length)..];
    }

    private static string RewriteTable(string text, Func<List<string>, List<List<string>>, (List<string>, List<List<string>>)> transform)
    {
        var lines = text.Split('\n');
        var meta = lines.Where(l => l.StartsWith("#!", StringComparison.Ordinal)).ToList();
        var table = lines.Where(l => l.Length > 0 && !l.StartsWith("#!", StringComparison.Ordinal)).Select(l => l.Split('\t').ToList()).ToList();
        var (names, rows) = transform(table[0], table.Skip(1).ToList());
        return string.Join("\n", meta.Append(string.Join("\t", names)).Concat(rows.Select(r => string.Join("\t", r)))) + "\n";
    }

    private static readonly Dictionary<string, Func<string, string>> Named = new()
    {
        ["two_groups"] = text =>
        {
            var lines = text.Split('\n');
            var header = lines.Where(l => l.StartsWith("#!", StringComparison.Ordinal)).ToList();
            var table = lines.Where(l => l.Length > 0 && !l.StartsWith("#!", StringComparison.Ordinal)).ToList();
            var groups = table.Skip(1).Select(r => r.Split('\t')[0]).ToList();
            var single = groups.First(g => !g.Contains('|'));
            var other = groups.First(g => g != single && !g.Contains('|'));
            var firstOther = table.FindIndex(r => r.Split('\t')[0] == other);
            var cells = table[firstOther].Split('\t');
            cells[0] = $"{other}|{single}";
            table[firstOther] = string.Join("\t", cells);
            return string.Join("\n", header.Concat(table)) + "\n";
        },
        ["term_not_annotated"] = text =>
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var cells = lines[i].Split('\t');
                if (cells.Length == Columns.Count && cells[Columns.IndexOf("go_id")].StartsWith("GO:", StringComparison.Ordinal))
                {
                    cells[Columns.IndexOf("annotation_status")] = "no_entry";
                    lines[i] = string.Join("\t", cells);
                    break;
                }
            }
            return string.Join("\n", lines);
        },
        ["reversed"] = text => RewriteTable(text, (names, rows) =>
            (Enumerable.Reverse(names).ToList(), rows.Select(r => Enumerable.Reverse(r).ToList()).ToList())),
        ["drop_evidence_by_member"] = text =>
        {
            var drop = Columns.IndexOf("evidence_by_member");
            return RewriteTable(text, (names, rows) =>
                (names.Where((_, i) => i != drop).ToList(), rows.Select(r => r.Where((_, i) => i != drop).ToList()).ToList()));
        },
        ["add_new_column"] = text => RewriteTable(text, (names, rows) =>
            ([.. names, "new_column"], rows.Select(r => (List<string>)[.. r, ""]).ToList())),
        ["entrapment"] = text =>
        {
            var col = Columns.IndexOf("entrapment_members");
            return RewriteTable(text, (names, rows) =>
            {
                var group = rows[0][0];
                foreach (var r in rows.Where(r => r[0] == group)) r[col] = "Random_P12345";
                return (names, rows);
            });
        },
        ["headers_only"] = text => string.Join("\n", text.Split('\n').Where(l => l.StartsWith("#!", StringComparison.Ordinal))) + "\n",
    };

    /// <summary>The fixture's op list: Python's <c>str.replace(old, new, count)</c>, <c>+ text</c>, and an insertion.</summary>
    private static string ApplyOps(string text, List<object?> ops)
    {
        foreach (var o in ops.Select(D))
        {
            switch ((string)o["op"]!)
            {
                case "replace":
                    var old = (string)o["old"]!;
                    Assert.That(text, Does.Contain(old));
                    text = (long)o["count"]! == 1 ? ReplaceFirst(text, old, (string)o["new"]!) : text.Replace(old, (string)o["new"]!, StringComparison.Ordinal);
                    break;
                case "append":
                    text += (string)o["text"]!;
                    break;
                case "insert_before":
                    var at = text.IndexOf((string)o["anchor"]!, StringComparison.Ordinal);
                    text = text[..at] + (string)o["text"]! + text[at..];
                    break;
                default:
                    throw new InvalidDataException((string)o["op"]!);
            }
        }
        return text;
    }

    // ------------------------------------------------------------------------------------------- tests/test_go.py

    [Test]
    public void AFileFromAnUnreleasedMzlibIsRefusedAndTheRefusalNamesTheCommit()
    {
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(Annotation))!.Message,
            Does.Contain("c5b16451f71d223c5165dfe5234f06146b80cd0f"));
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadCategories(Categories))!.Message, Does.Contain("unreleased"));
    }

    [Test]
    public void TheFixtureReadsAndEveryHeaderCounterRecountsFromTheRows()
    {
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        Assert.That(annotation.Rows.Select(r => r["protein_group"]).Distinct().Count(), Is.EqualTo(5));
        Assert.That(annotation.Header["status_contaminant"], Is.EqualTo("1"));
        // Nothing is dropped without a count: the contaminant group has no term, and the other two aspects have no table here.
        Assert.That(annotation.NotStored["no term (contaminant)"], Is.EqualTo(1));
        Assert.That(annotation.NotStored.Keys, Is.EquivalentTo(new[] { "no term (contaminant)", "aspect molecular_function", "aspect biological_process" }));
    }

    [Test]
    public void LocalizationsAreCellularComponentOnlyAndExpandOverAccessionUsed()
    {
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        var rows = Go.LocalizationRows(annotation);
        var cc = annotation.Rows.Where(r => r["aspect"] == "cellular_component").ToList();
        Assert.That(rows.Select(r => (string)r["compartment"]!).ToHashSet(), Is.EquivalentTo(cc.Select(r => r["go_id"]).ToHashSet()));
        var carried = cc.SelectMany(r => r["accession_used"].Split(';').Where(a => a.Length > 0).Select(a => (a, r["go_id"]))).ToHashSet();
        Assert.That(rows.Select(r => ((string)r["protein_accession"]!, (string)r["compartment"]!)).ToHashSet(), Is.EquivalentTo(carried));
        Assert.That(rows.All(r => (string)r["go_release"]! == "releases/2026-07-26"));
        Assert.That(rows.Select(r => r["source_id"]).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public void GosGroupColumnsAreStoredAsGoWroteThemAndItsQualifiersPerAccession()
    {
        // Schema 0.0.10 (D28): the columns go's D22/D29 rows carry, taken with gene_resolutions. GO-D7 (go 023):
        // inherited, propagated and evidence are the accession's own, from go D33's per-member columns.
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        var rows = Go.LocalizationRows(annotation);
        var source = new Dictionary<(string, string), Dictionary<string, string>>();
        foreach (var r in annotation.Rows.Where(r => r["aspect"] == "cellular_component"))
            foreach (var a in r["accession_used"].Split(';').Where(a => a.Length > 0))
                source[(a, r["go_id"])] = r;
        foreach (var row in rows)
        {
            var accession = (string)row["protein_accession"]!;
            var want = source[(accession, (string)row["compartment"]!)];
            Assert.That(row["protein_group"], Is.EqualTo(want["protein_group"]));
            Assert.That(row["q_value"], Is.EqualTo(double.Parse(want["q_value"], System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That((row["n_members"], row["n_with"]), Is.EqualTo(((object?)long.Parse(want["n_members"]), (object?)long.Parse(want["n_with"]))));
            Assert.That(row["propagated"], Is.EqualTo(!want["accession_direct"].Split(';').Contains(accession)));
            Assert.That(row["inherited"], Is.EqualTo(want["accession_inherited"].Split(';').Contains(accession)));
            var own = want["evidence_by_member"].Split(';').Single(e => e.StartsWith(accession + "=", StringComparison.Ordinal));
            Assert.That(row["evidence"], Is.EqualTo(own[(accession.Length + 1)..].Replace(',', ';')));
        }
        Assert.That(rows.Any(r => (bool)r["propagated"]!) && rows.Any(r => !(bool)r["propagated"]!));
        Assert.That(ArrowTables.FromRows("protein_localizations", rows).Length, Is.EqualTo(rows.Count));
    }

    [Test]
    public void AMultiMemberRowGivesEachAccessionItsOwnQualifiers()
    {
        // go 023's example on the fixture's first two-member CC row, whose members' evidence already differs
        // (P0C0S5 IDA and IPI, Q71UI9 IPI only), edited so P0C0S5 alone cites the term directly. The pooled flags
        // would give Q71UI9 P0C0S5's directness and its IDA.
        var annotation = Go.ReadAnnotation(Copy(Annotation, t => EditFirstTwoMemberRow(t, (names, r) =>
        {
            r[names.IndexOf("accession_direct")] = "P0C0S5";
            r[names.IndexOf("propagated")] = "false";
        })), allowPrerelease: true);
        var mixed = annotation.Rows.Single(r => r["accession_direct"] == "P0C0S5" && r["accession_used"] == "P0C0S5;Q71UI9");
        Assert.That(mixed["evidence_by_member"], Is.EqualTo("P0C0S5=ECO:0000314,ECO:0000353;Q71UI9=ECO:0000353"), "premise");
        var rows = Go.LocalizationRows(annotation).Where(r => (string)r["compartment"]! == mixed["go_id"])
            .ToDictionary(r => (string)r["protein_accession"]!);
        Assert.That(mixed["propagated"], Is.EqualTo("false"), "premise: go's pooled row reads direct");
        Assert.That((rows["P0C0S5"]["propagated"], rows["P0C0S5"]["evidence"]), Is.EqualTo(((object?)false, (object?)"ECO:0000314;ECO:0000353")));
        Assert.That((rows["Q71UI9"]["propagated"], rows["Q71UI9"]["evidence"]), Is.EqualTo(((object?)true, (object?)"ECO:0000353")));
        // The pooled reading, kept for go.py's parity cases only, is the defect GO-D7 names.
        var pooled = Go.LocalizationRows(annotation, IngestRules.Python0320).Single(r =>
            (string)r["protein_accession"]! == "Q71UI9" && (string)r["compartment"]! == mixed["go_id"]);
        Assert.That((pooled["propagated"], pooled["evidence"]), Is.EqualTo(((object?)false, (object?)"ECO:0000314;ECO:0000353")));
    }

    [Test]
    public void AnInheritedMemberIsInheritedOnItsOwnRowWhateverTheOthersAre()
    {
        // Mark Q71UI9 as inherited on a row both members carry: the pooled flag stays false (not EVERY member
        // inherited), but Q71UI9's own row must read true and P0C0S5's false.
        var annotation = Go.ReadAnnotation(Copy(Annotation, MarkInherited), allowPrerelease: true);
        var row = annotation.Rows.First(r => r["accession_inherited"] == "Q71UI9");
        var rows = Go.LocalizationRows(annotation).Where(r => (string)r["compartment"]! == row["go_id"])
            .ToDictionary(r => (string)r["protein_accession"]!);
        Assert.That(row["inherited"], Is.EqualTo("false"));
        Assert.That((rows["Q71UI9"]["inherited"], rows["P0C0S5"]["inherited"]), Is.EqualTo(((object?)true, (object?)false)));
    }

    /// <summary>The first two-member CC row: <c>accession_inherited</c> becomes <c>Q71UI9</c>, with <c>inherited</c> false.</summary>
    private static string MarkInherited(string text) => EditFirstTwoMemberRow(text, (names, r) => r[names.IndexOf("accession_inherited")] = "Q71UI9");

    private static string EditFirstTwoMemberRow(string text, Action<List<string>, List<string>> edit) =>
        RewriteTable(text, (names, rows) =>
        {
            var target = rows.First(r => r[names.IndexOf("aspect")] == "cellular_component" && r[names.IndexOf("accession_used")] == "P0C0S5;Q71UI9");
            edit(names, target);
            return (names, rows);
        });

    [TestCase("evidence_by_member", "P0C0S5=ECO:0000314", "evidence_by_member covers ['P0C0S5'], not accession_used ['P0C0S5', 'Q71UI9']")]
    [TestCase("evidence_by_member", "P0C0S5=ECO:0000314;Q71UI9=ECO:1;X1=ECO:2", "evidence_by_member covers")]
    [TestCase("evidence_by_member", "P0C0S5;Q71UI9=ECO:1", "is not `member=code,code`")]
    [TestCase("evidence_by_member", "P0C0S5=ECO:1;P0C0S5=ECO:2;Q71UI9=ECO:1", "names 'P0C0S5' twice")]
    [TestCase("accession_direct", "X1", "accession_direct names ['X1'], which accession_used does not")]
    [TestCase("accession_inherited", "X1", "accession_inherited names ['X1'], which accession_used does not")]
    [TestCase("accession_inherited", "P0C0S5;Q71UI9", "inherited 'false' but accession_inherited 'P0C0S5;Q71UI9'")]
    [TestCase("propagated", "false", "propagated 'false' but accession_direct ''")]
    public void PerMemberColumnsThatContradictTheRowAreRefused(string column, string value, string message)
    {
        // The fixture's first two-member CC row has accession_direct empty and propagated true.
        var annotation = Go.ReadAnnotation(Copy(Annotation, t => EditFirstTwoMemberRow(t, (names, r) =>
        {
            Assert.That((r[names.IndexOf("accession_direct")], r[names.IndexOf("propagated")]), Is.EqualTo(("", "true")), "premise");
            r[names.IndexOf(column)] = value;
        })), allowPrerelease: true);
        Assert.That(Assert.Throws<IngestException>(() => Go.LocalizationRows(annotation))!.Message,
            Does.Contain(message).And.Contains("group P0C0S5|Q71UI9"));
    }

    [Test]
    public void AFlagThatIsNotTrueOrFalseIsRefused()
    {
        Assert.That(Go.Flag(""), Is.Null);
        Assert.That(Assert.Throws<IngestException>(() => Go.Flag("yes"))!.Message, Does.Contain("expected `true` or `false`"));
    }

    [Test]
    public void CategoriesPairWithTheirAnnotationFileAndCarryTheMap()
    {
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        var categories = Go.ReadCategories(Categories, allowPrerelease: true);
        Go.CheckCoverage(annotation, categories);
        var rows = Go.CategoryRows(categories, annotation);
        Assert.That(rows.Select(r => ((string)r["category_map_name"]!, (string)r["organelle_map_version"]!)).Distinct(),
            Is.EqualTo(new[] { ("smoke", "1") }));
    }

    [Test]
    public void AHeaderCounterTheRowsDoNotSupportIsRefused()
    {
        var path = Copy(Annotation, t => t.Replace("#!status_annotated 4", "#!status_annotated 5"));
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("status_annotated header 5 rows 4"));
    }

    [Test]
    public void ACategoryTermTheAnnotationFileLacksIsRefused()
    {
        var path = Copy(Categories, t => t + "GO:9999999\tnucleus\t\n");
        var categories = Go.ReadCategories(path, allowPrerelease: true);
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        Assert.That(Assert.Throws<IngestException>(() => Go.CheckCoverage(annotation, categories))!.Message, Does.Contain("GO:9999999"));
    }

    [Test]
    public void ACategoryFileFromAnotherOntologyReleaseIsRefused()
    {
        var path = Copy(Categories, t => t.Replace("#!go_release releases/2026-07-26", "#!go_release releases/2026-01-01"));
        var categories = Go.ReadCategories(path, allowPrerelease: true);
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        Assert.That(Assert.Throws<IngestException>(() => Go.CheckCoverage(annotation, categories))!.Message, Does.Contain("not a pair"));
    }

    [Test]
    public void AnAccessionInTwoGroupsIsRefused()
    {
        var path = Copy(Annotation, Named["two_groups"]);
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("sit in more than one group"));
    }

    [Test]
    public void ATermOnAGroupNotMarkedAnnotatedIsRefused()
    {
        var path = Copy(Annotation, Named["term_not_annotated"]);
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("a term means annotated"));
    }

    [Test]
    public void ColumnsAreFoundByNameNotByPosition()
    {
        // GO-D1: go asked whether the reader looks columns up by name. Reversed order reads the same.
        var path = Copy(Annotation, Named["reversed"]);
        static IEnumerable<string> WithoutSource(List<Row> rows) =>
            rows.Select(r => Py(r.Where(kv => kv.Key != "source_id").ToDictionary(kv => kv.Key, kv => kv.Value)));
        Assert.That(WithoutSource(Go.LocalizationRows(Go.ReadAnnotation(path, allowPrerelease: true))),
            Is.EqualTo(WithoutSource(Go.LocalizationRows(Go.ReadAnnotation(Annotation, allowPrerelease: true)))));
    }

    [Test]
    public void AMissingOrUnknownColumnIsRefusedByName()
    {
        var path = Copy(Annotation, Named["drop_evidence_by_member"]);
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("missing ['evidence_by_member'], unknown []"));
        path = Copy(Annotation, Named["add_new_column"]);
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("missing [], unknown ['new_column']"));
    }

    [Test]
    public void AnUnknownHeaderKeySuchAsUnresolvedGoIdsIsIgnored()
    {
        // go D35: written only when ids were skipped, after source_file_sha256.
        var path = Copy(Annotation, t =>
        {
            var at = t.IndexOf("#!counter_q_value_max", StringComparison.Ordinal);
            return t[..at] + "#!unresolved_go_ids 3\n" + t[at..];
        });
        var annotation = Go.ReadAnnotation(path, allowPrerelease: true);
        Assert.That(annotation.Header["unresolved_go_ids"], Is.EqualTo("3"));
        Assert.That(Go.LocalizationRows(annotation), Has.Count.EqualTo(Go.LocalizationRows(Go.ReadAnnotation(Annotation, allowPrerelease: true)).Count));
    }

    [Test]
    public void AGroupWithEntrapmentMembersIsRefused()
    {
        // Our go 013: no file from an entrapment search until GO-E1. D36 made it detectable; go 020 keeps it until D39 ships.
        var path = Copy(Annotation, Named["entrapment"]);
        Assert.That(Assert.Throws<IngestException>(() => Go.ReadAnnotation(path, allowPrerelease: true))!.Message,
            Does.Contain("entrapment members 'Random_P12345'"));
    }

    // ----------------------------------------------------------------------------------- parity with the Python

    private static Dictionary<string, object?> AnnotationSummary(GoAnnotation a) => new()
    {
        ["header"] = a.Header.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["n_rows"] = (long)a.Rows.Count,
        ["sha256"] = a.Sha256,
        ["go_release"] = a.GoRelease,
        ["mzlib_release"] = a.MzlibRelease,
        ["not_stored"] = a.NotStored.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
    };

    private static Dictionary<string, object?> CategoriesSummary(GoCategories c) => new()
    {
        ["header"] = c.Header.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        ["n_rows"] = (long)c.Rows.Count,
        ["sha256"] = c.Sha256,
        ["map_name"] = c.MapName,
        ["map_version"] = c.MapVersion,
        ["map_sha256"] = c.MapSha256,
    };

    private static Dictionary<string, object?> Digest(List<Row> rows) => new()
    {
        ["n"] = (long)rows.Count,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyFormat.Json(rows, sortKeys: false)))),
    };

    /// <summary>One of the fixture's <c>attempt()</c> results: <c>{"value": ...}</c> or <c>{"error", "message"}</c>.</summary>
    /// <returns>The C# value when both sides produced one.</returns>
    private static T? Expect<T>(object? expected, Func<T> actual, Func<T, object?> view, string label) where T : class
    {
        var e = D(expected);
        if (e.TryGetValue("error", out var error))
        {
            var thrown = Assert.Catch(() => actual(), $"{label}: Python raised {error}");
            Assert.That(thrown, Is.InstanceOf(error as string == "IngestError" ? typeof(IngestException) : typeof(FormatException)), label);
            Assert.That(thrown!.Message, Is.EqualTo(e["message"]), label);
            return null;
        }
        var value = actual();
        Assert.That(Py(view(value)), Is.EqualTo(Py(e["value"])), label);
        return value;
    }

    [Test]
    public void TheFixtureFilesGiveThePythonsRowsAndRefusals()
    {
        Expect(Cases["annotation_refused"], () => Go.ReadAnnotation(Annotation), _ => null, "annotation without allowPrerelease");
        Expect(Cases["categories_refused"], () => Go.ReadCategories(Categories), _ => null, "categories without allowPrerelease");
        var annotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        var categories = Go.ReadCategories(Categories, allowPrerelease: true);
        Go.CheckCoverage(annotation, categories);
        Assert.That(Py(AnnotationSummary(annotation)), Is.EqualTo(Py(Cases["annotation"])));
        Assert.That(Py(CategoriesSummary(categories)), Is.EqualTo(Py(Cases["categories"])));
        Assert.That(Go.SourceId(annotation), Is.EqualTo(Cases["source_id"]));
        Assert.That(Py(Go.SourceRow(annotation)), Is.EqualTo(Py(Cases["source_row"])));
        var localizations = Go.LocalizationRows(annotation, IngestRules.Python0320); // go.py's pooled qualifiers (GO-D7 changed them)
        Assert.That(localizations, Has.Count.EqualTo(L(Cases["localization_rows"]).Count).And.Count.EqualTo(139));
        Assert.That(Py(localizations), Is.EqualTo(Py(Cases["localization_rows"])));
        var categoryRows = Go.CategoryRows(categories, annotation);
        Assert.That(categoryRows, Has.Count.EqualTo(17));
        Assert.That(Py(categoryRows), Is.EqualTo(Py(Cases["category_rows"])));
        // Every row fits its table's Arrow schema.
        Assert.That(ArrowTables.FromRows("protein_localizations", localizations).Length, Is.EqualTo(139));
        Assert.That(ArrowTables.FromRows("organelle_term_categories", categoryRows).Length, Is.EqualTo(17));
        Assert.That(ArrowTables.FromRows("annotation_sources", [Go.SourceRow(annotation)]).Length, Is.EqualTo(1));
    }

    [Test]
    public void FlagsMatchThePython()
    {
        foreach (var (cell, expected) in D(Cases["flag"]))
        {
            var e = D(expected);
            if (e.ContainsKey("error"))
                Assert.That(Assert.Throws<IngestException>(() => Go.Flag(cell))!.Message, Is.EqualTo(e["message"]), Py(cell));
            else
                Assert.That(Go.Flag(cell), Is.EqualTo(e["value"]), Py(cell));
        }
    }

    [Test]
    public void EveryEditedCopyGivesThePythonsResult()
    {
        var baseAnnotation = Go.ReadAnnotation(Annotation, allowPrerelease: true);
        var cases = L(Cases["cases"]).Select(D).ToList();
        Assert.That(cases, Has.Count.EqualTo(37));
        foreach (var c in cases)
        {
            var name = (string)c["name"]!;
            var isAnnotation = (string)c["file"]! == "annotation";
            var source = isAnnotation ? Annotation : Categories;
            var text = ReadText(source);
            var edited = c["edit"] is string named ? Named[named](text) : ApplyOps(text, L(c["edit"]));
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(edited))), Is.EqualTo(c["input_sha256"]),
                $"{name}: the C# edit wrote different bytes from the Python's");
            var path = Write(Path.GetFileName(source), edited, name);
            var allow = (bool)c["allow_prerelease"]!;
            if (isAnnotation)
            {
                var annotation = Expect(c["read"], () => Go.ReadAnnotation(path, allow), AnnotationSummary, $"{name}: read");
                if (annotation is null) continue;
                // go.py copied go's pooled qualifiers onto every member; GO-D7 changed that (see the tests below).
                Expect(c["localization_rows"], () => Go.LocalizationRows(annotation, IngestRules.Python0320), Digest, $"{name}: localization rows");
                Assert.That(Py(Go.SourceRow(annotation)), Is.EqualTo(Py(c["source_row"])), $"{name}: source row");
            }
            else
            {
                var categories = Expect(c["read"], () => Go.ReadCategories(path, allow), CategoriesSummary, $"{name}: read");
                if (categories is null) continue;
                Expect(c["coverage"], () => { Go.CheckCoverage(baseAnnotation, categories); return new object(); }, _ => null, $"{name}: coverage");
                Expect(c["category_rows"], () => Go.CategoryRows(categories, baseAnnotation), Digest, $"{name}: category rows");
            }
        }
    }
}
