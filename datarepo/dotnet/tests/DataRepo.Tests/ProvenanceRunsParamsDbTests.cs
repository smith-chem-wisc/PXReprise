using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary><c>Sources/Provenance</c>, <c>Runs</c>, <c>SearchParams</c> and <c>ProteinDb</c> against what the Python wrote.</summary>
/// <remarks>
/// Every expected value comes from <c>Fixtures/provenance-runs-params-db</c>, which the Python
/// (datarepo 0.32.0, plus D37/G81 from <c>wip/d37-g81-python</c> 3e68781) produced from the same inputs; see its
/// PROVENANCE.md. Rows are compared as Python's <c>json.dumps</c> text, key order included, so an int that
/// became a float or a key that moved is a failure.
/// </remarks>
public class ProvenanceRunsParamsDbTests
{
    private static readonly string FixtureDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "provenance-runs-params-db");
    private static readonly Lazy<Dictionary<string, object?>> CasesLazy = new(() => Provenance.Load(Path.Combine(FixtureDir, "cases.json")));
    private static Dictionary<string, object?> Cases => CasesLazy.Value;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static string RepoPath(string relative) => Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Python's <c>json.dumps(value)</c>: what both sides are compared as.</summary>
    private static string Py(object? value) => PyFormat.Json(value, sortKeys: false);

    private static Dictionary<string, object?> D(object? value) => (Dictionary<string, object?>)value!;
    private static List<object?> L(object? value) => (List<object?>)value!;

    /// <summary>The repo root becomes <c>&lt;repo&gt;</c> and separators <c>/</c>, as the fixture wrote paths.</summary>
    private static string Norm(string text) => text.Replace(RepoRoot(), "<repo>").Replace('\\', '/');

    private static readonly Dictionary<string, Type> Errors = new()
    {
        ["UnsupportedProvenance"] = typeof(UnsupportedProvenanceException),
        ["IngestError"] = typeof(IngestException),
    };

    /// <summary>One of the fixture's <c>attempt()</c> results: <c>{"value": ...}</c> or <c>{"error", "message"}</c>.</summary>
    private static void Expect(object? expected, Func<object?> actual, string label, Func<string, string>? normalise = null)
    {
        var e = D(expected);
        if (e.TryGetValue("error", out var error))
        {
            var thrown = Assert.Catch(() => actual(), $"{label}: Python raised {error}");
            Assert.That(thrown, Is.InstanceOf(Errors[(string)error!]), label);
            Assert.That((normalise ?? (s => s))(thrown!.Message), Is.EqualTo(e["message"]), label);
            return;
        }
        Assert.That(Py(actual()), Is.EqualTo(Py(e["value"])), label);
    }

    private static IEnumerable<Dictionary<string, object?>> Section(string name) => L(Cases[name]).Cast<Dictionary<string, object?>>();

    // ----------------------------------------------------------------------------------------- provenance

    [Test]
    public void ProvenanceMatchesPython()
    {
        var checkedCount = 0;
        foreach (var c in Section("provenance"))
        {
            var name = (string)c["name"]!;
            var doc = D(c["doc"]);
            var runNames = new RunNameMap(L(c["run_names"]).Cast<string>().ToList());
            Expect(c["schema_version"], () => (long)Provenance.SchemaVersion(doc), $"{name}: schema_version");
            Expect(c["definitions_namespace"], () => Provenance.DefinitionsNamespace(doc), $"{name}: definitions_namespace");
            Expect(c["record_row"], () => Provenance.RecordRow(doc, "PXD000001", "04_search", "sources/provenance_04_search.json", string.Concat(Enumerable.Repeat("ab", 32))), $"{name}: record_row");
            Expect(c["finding_rows"], () => Provenance.FindingRows(doc, "PXD000001", "provenance.json flags (04_search)"), $"{name}: finding_rows");
            foreach (var ns in new[] { "aging", "pxreprise" })
            {
                foreach (var version in new[] { 2, 3 })
                    Expect(c[$"metric_rows/{version}/{ns}"], () => Provenance.MetricRows(doc, "PXD000001", version, ns), $"{name}: metric_rows {version} {ns}");
                Expect(c[$"contamination_metric_rows/raw/{ns}"], () => Provenance.ContaminationMetricRows(doc, "PXD000001", null, ns), $"{name}: contamination raw {ns}");
                Expect(c[$"contamination_metric_rows/mapped/{ns}"], () => Provenance.ContaminationMetricRows(doc, "PXD000001", runNames, ns), $"{name}: contamination mapped {ns}");
            }
            checkedCount++;
        }
        Assert.That(checkedCount, Is.GreaterThanOrEqualTo(15));
    }

    [Test]
    public void TheDefaultNamespaceIsAgingsAndChangesNothing()
    {
        // The fixture's aging rows are the ones master's 0.32.0 wrote (PROVENANCE.md: compared leaf for leaf).
        var c = Section("provenance").First(x => (string)x["name"]! == "layout 3, every block");
        var doc = D(c["doc"]);
        Assert.That(Py(Provenance.MetricRows(doc, "PXD000001", 3)), Is.EqualTo(Py(D(c["metric_rows/3/aging"])["value"])));
        Assert.That(Py(Provenance.ContaminationMetricRows(doc, "PXD000001")), Is.EqualTo(Py(D(c["contamination_metric_rows/raw/aging"])["value"])));
    }

    [Test]
    public void TheLoaderKeepsPythonsNumberTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), "datarepo-json-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"b\": 1, \"a\": 1.0, \"e\": 1e3, \"big\": 123456789012345678901234567890, \"b\": [true, null, -0]}")]);
            var doc = Provenance.Load(path);
            Assert.That(doc.Keys, Is.EqualTo(new[] { "b", "a", "e", "big" }), "first position, last value, as a Python dict");
            Assert.That(doc["big"], Is.EqualTo(System.Numerics.BigInteger.Parse("123456789012345678901234567890")), "an int past 64 bits stays an int");
            doc.Remove("big");  // PyFormat.Json writes a BigInteger as a string; reported, not this module's to fix
            Assert.That(Py(doc), Is.EqualTo("{\"b\": [true, null, 0], \"a\": 1.0, \"e\": 1000.0}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ----------------------------------------------------------------------------------------- runs

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Facts(object? value) =>
        D(value).ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)D(kv.Value));

    [Test]
    public void RunsMatchPython()
    {
        foreach (var c in Section("runs"))
        {
            var name = (string)c["name"]!;
            var search = D(c["search_provenance"]);
            var (names, reason) = Runs.ExcludedFiles(search);
            Expect(c["excluded_files"], () => new List<object?> { names.Order(StringComparer.Ordinal).Cast<object?>().ToList(), reason }, $"{name}: excluded_files");
            foreach (var ns in new[] { "aging", "pxreprise" })
            {
                var (runs, metrics) = Runs.Build((string)c["dataset_id"]!, c["fetch"] as Dictionary<string, object?>,
                    c["qc"] as Dictionary<string, object?>, Facts(c["run_facts"]), names, ns, IngestRules.Python0320);
                Assert.That(Py(new List<object?> { runs, metrics }), Is.EqualTo(Py(c[$"build/{ns}"])), $"{name}: build {ns}");
            }
        }
    }

    [Test]
    public void EnrichmentAssignmentMatchesPython()
    {
        foreach (var c in Section("assign_enrichment"))
        {
            var name = (string)c["name"]!;
            var runs = L(c["runs"]).Select(r => new Row(D(r))).ToList();
            var pairs = L(c["run_enrichment"]).Select(p => ((string)L(p)[0]!, (string)L(p)[1]!)).ToList();
            Expect(c["result"], () => Runs.AssignEnrichment(runs, "PXD000002", L(c["declared"]).Cast<string>().ToList(), (bool)c["mixed"]!, pairs), $"{name}: result");
            var after = runs.Select(r => new Dictionary<string, object?>
            {
                ["file_name"] = r["file_name"], ["enrichment"] = r["enrichment"], ["enrichment_source"] = r["enrichment_source"],
            }).ToList();
            Assert.That(Py(after), Is.EqualTo(Py(c["after"])), $"{name}: runs after");
        }
    }

    [Test]
    public void TheEnrichmentVocabularyIsTheSchemas() =>
        Assert.That(Runs.EnrichmentVocabulary, Is.EqualTo(L(Cases["enrichment_vocabulary"]).Cast<string>()));

    // ----------------------------------------------------------------------------------------- search params

    [Test]
    public void SearchParametersMatchPython()
    {
        foreach (var c in Section("search_params"))
        {
            var name = (string)c["name"]!;
            var files = L(c["files"]).Select(f => RepoPath((string)f!)).ToList();
            var unimod = D(c["unimod"]);
            var rows = SearchParams.ModificationRows(files, "PXD000001", n => unimod.TryGetValue(n, out var u) ? (string?)u : null);
            Assert.That(Py(rows), Is.EqualTo(Py(c["modification_rows"])), $"{name}: modification_rows");
            Assert.That(SearchParams.PepRegime(files), Is.EqualTo(c["pep_regime"]), $"{name}: pep_regime");
            Assert.That(SearchParams.TcAmbiguity(files), Is.EqualTo(c["tc_ambiguity"]), $"{name}: tc_ambiguity");
            foreach (var (release, expected) in D(c["pep_iterative"]))
                Assert.That(SearchParams.PepIterative(files, release == "None" ? null : release), Is.EqualTo(expected), $"{name}: pep_iterative {release}");
        }
    }

    [Test]
    public void TheSearchedDatabaseAndTasksComeFromTheProvenance()
    {
        foreach (var c in Section("provenance_inputs"))
        {
            var doc = D(c["provenance"]);
            var (dbName, sha) = SearchParams.SearchedDatabase(doc);
            Assert.That(Py(new List<object?> { dbName, sha }), Is.EqualTo(Py(c["searched_database"])));
            Assert.That(Py(SearchParams.TaskNames(doc)), Is.EqualTo(Py(c["task_names"])));
        }
    }

    // ----------------------------------------------------------------------------------------- protein db

    [Test]
    public void ProteinDatabasesReadAsPythonReadThem()
    {
        var db = D(Cases["protein_db"]);
        var sequences = new ProteinSequences();
        foreach (var read in L(db["reads"]).Cast<Dictionary<string, object?>>())
        {
            var rel = (string)read["path"]!;
            var path = RepoPath(rel);
            var pairs = (rel.EndsWith(".xml") ? ProteinDb.IterUniprotXml(path) : ProteinDb.IterFasta(path))
                .Select(p => (object?)new List<object?> { p.Accession, p.Sequence }).ToList();
            Assert.That(Py(pairs), Is.EqualTo(Py(read["pairs"])), rel);
            Assert.That((long)ProteinDb.ReadDatabase(path, sequences), Is.EqualTo(read["count"]), rel);
        }
        var state = D(db["state"]);
        Assert.That((long)sequences.Count, Is.EqualTo(state["len"]));
        Assert.That(Py(sequences.ByAccession), Is.EqualTo(Py(state["by_accession"])));
        Assert.That(Py(sequences.ByAccession.Keys.ToDictionary(a => a, a => (object?)sequences.DatabaseStatus(a))), Is.EqualTo(Py(state["status"])));
        Assert.That(sequences.DatabaseStatus("NOT_THERE"), Is.Null);

        foreach (var (path, expected) in D(db["errors"]))
            Expect(expected, () => ProteinDb.ReadDatabase(path, new ProteinSequences()), path, Norm);
        foreach (var (path, expected) in D(db["is_contaminant_database"]))
            Assert.That(ProteinDb.IsContaminantDatabase(path), Is.EqualTo(expected), path);
        foreach (var o in L(db["occurrences"]).Cast<List<object?>>())
            Assert.That(Py(ProteinDb.Occurrences((string)o[0]!, (string)o[1]!).Cast<object?>().ToList()), Is.EqualTo(Py(o[2])), $"{o[0]} in {o[1]}");
    }

    [Test]
    public void LoadingTheSearchedDatabasesMatchesPython()
    {
        var workRoot = RepoPath("tests/data/work_root");
        foreach (var c in L(D(Cases["protein_db"])["loads"]).Cast<Dictionary<string, object?>>())
        {
            var name = (string)c["name"]!;
            Expect(c["result"], () =>
            {
                var s = ProteinDb.Load(D(c["provenance"]), workRoot);
                return new Dictionary<string, object?>
                {
                    ["len"] = (long)s.Count,
                    ["files"] = s.Files.Select(f => new Dictionary<string, object?>(f) { ["path"] = Norm((string)f["path"]!) }).ToList(),
                    ["missing"] = s.Missing.Select(Norm).ToList(),
                    ["by_accession"] = s.ByAccession,
                    ["status"] = s.ByAccession.Keys.ToDictionary(a => a, a => (object?)s.DatabaseStatus(a)),
                };
            }, name, Norm);
        }
    }

    // ----------------------------------------------------------------------------------------- real data

    private const string AgingData = "F:/aging_data";

    private static Dictionary<string, object?> RealData()
    {
        if (!Directory.Exists(AgingData)) Assert.Ignore($"{AgingData} is not on this machine");
        return Provenance.Load(Path.Combine(FixtureDir, "real_data.json"));
    }

    private static string Real(string relative) => Path.Combine(AgingData, relative);

    private static string Sha256Hex(IEnumerable<(string Accession, string Sequence)> pairs, out long count, out long distinct, out string distinctSha, out long accessions)
    {
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var dist = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var seen = new HashSet<(string, string)>();
        var accs = new HashSet<string>(StringComparer.Ordinal);
        count = 0;
        foreach (var p in pairs)
        {
            var line = Encoding.UTF8.GetBytes($"{p.Accession}\t{p.Sequence}\n");
            all.AppendData(line);
            count++;
            accs.Add(p.Accession);
            if (seen.Add(p)) dist.AppendData(line);
        }
        distinct = seen.Count;
        distinctSha = Convert.ToHexStringLower(dist.GetHashAndReset());
        accessions = accs.Count;
        return Convert.ToHexStringLower(all.GetHashAndReset());
    }

    [Test, Category("RealData")]
    public void RealSearchDatabasesReadAsPythonReadThem()
    {
        foreach (var (path, value) in D(RealData()["databases"]))
        {
            var e = D(value);
            if (!File.Exists(path)) Assert.Fail($"{path} named by the fixture is gone");
            var pairs = path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? ProteinDb.IterUniprotXml(path) : ProteinDb.IterFasta(path);
            var sha = Sha256Hex(pairs, out var count, out var distinct, out var distinctSha, out var accessions);
            Assert.That(BundleWriter.Sha256File(path), Is.EqualTo(e["file_sha256"]), $"{path}: the file changed since the fixture was made");
            Assert.That(count, Is.EqualTo(e["entries"]), path);
            Assert.That(sha, Is.EqualTo(e["pairs_sha256"]), path);
            Assert.That(distinct, Is.EqualTo(e["distinct_pairs"]), path);
            Assert.That(distinctSha, Is.EqualTo(e["distinct_pairs_sha256"]), path);
            Assert.That(accessions, Is.EqualTo(e["distinct_accessions"]), path);
            Assert.That(ProteinDb.IsContaminantDatabase(path), Is.EqualTo(e["is_contaminant_database"]), path);
        }
    }

    [Test, Category("RealData")]
    public void RealRunFoldersMatchPython()
    {
        foreach (var c in L(RealData()["datasets"]).Cast<Dictionary<string, object?>>())
        {
            var dir = (string)c["search_dir"]!;
            var datasetId = (string)c["dataset_id"]!;
            var doc = Provenance.Load(Path.Combine(Real(dir), "provenance.json"));

            Expect(c["schema_version"], () => (long)Provenance.SchemaVersion(doc), $"{dir}: schema_version");
            Expect(c["definitions_namespace"], () => Provenance.DefinitionsNamespace(doc), $"{dir}: definitions_namespace");
            foreach (var r in L(c["record_rows"]).Cast<Dictionary<string, object?>>())
            {
                var stage = Path.GetFileName(Path.GetDirectoryName(Real((string)r["path"]!)))!;
                var row = Provenance.RecordRow(Provenance.Load(Real((string)r["path"]!)), datasetId, stage, $"sources/provenance_{stage}.json", "00");
                Assert.That(Py(row), Is.EqualTo(Py(r["row"])), $"{dir}: record_row {r["path"]}");
            }
            Assert.That(Py(Provenance.FindingRows(doc, datasetId, $"provenance.json flags ({Path.GetFileName(dir)})")), Is.EqualTo(Py(c["finding_rows"])), $"{dir}: finding_rows");

            var inputs = D(c["inputs"]);
            var fetch = inputs["fetch_manifest"] is string f ? Runs.LoadFetchManifest(Real(f)) : null;
            var qc = inputs["qc_report"] is string q ? Runs.LoadQcReport(Real(q)) : null;
            var (excluded, reason) = Runs.ExcludedFiles(doc);
            Assert.That(Py(new List<object?> { excluded.Order(StringComparer.Ordinal).Cast<object?>().ToList(), reason }), Is.EqualTo(Py(c["excluded_files"])), $"{dir}: excluded_files");
            var (runs, metrics) = Runs.Build(datasetId, fetch, qc, Facts(inputs["run_facts"]), excluded, rules: IngestRules.Python0320);
            Assert.That(Py(new List<object?> { runs, metrics }), Is.EqualTo(Py(c["build/aging"])), $"{dir}: build");
            var (_, pxrMetrics) = Runs.Build(datasetId, fetch, qc, Facts(inputs["run_facts"]), excluded, "pxreprise");
            Assert.That(Py(pxrMetrics), Is.EqualTo(Py(D(c["build/pxreprise"])["metrics"])), $"{dir}: build pxreprise");

            if (c.GetValueOrDefault("assign_enrichment") is Dictionary<string, object?> en)
            {
                var copy = runs.Select(r => new Row(r)).ToList();
                var pairs = L(en["run_enrichment"]).Select(p => ((string)L(p)[0]!, (string)L(p)[1]!)).ToList();
                Expect(en["result"], () => Runs.AssignEnrichment(copy, datasetId, L(en["declared"]).Cast<string>().ToList(), (bool)en["mixed"]!, pairs), $"{dir}: assign_enrichment");
                Assert.That(Py(copy.Select(r => new List<object?> { r["enrichment"], r["enrichment_source"] }).ToList()), Is.EqualTo(Py(en["after"])), $"{dir}: enrichment after");
            }

            var runNames = new RunNameMap(runs.Select(r => SourcesPy.PathStem((string)r["file_name"]!)).ToList());
            foreach (var ns in new[] { "aging", "pxreprise" })
            {
                if (c.TryGetValue($"metric_rows/{ns}", out var expectedMetrics))
                    Assert.That(Py(Provenance.MetricRows(doc, datasetId, Provenance.SchemaVersion(doc), ns)), Is.EqualTo(Py(expectedMetrics)), $"{dir}: metric_rows {ns}");
                Assert.That(Py(Provenance.ContaminationMetricRows(doc, datasetId, runNames, ns)), Is.EqualTo(Py(c[$"contamination_metric_rows/{ns}"])), $"{dir}: contamination {ns}");
            }

            var sp = D(c["search_params"]);
            var taskFiles = L(sp["task_files"]).Select(t => Real((string)t!)).ToList();
            var unimod = D(sp["unimod"]);
            Assert.That(Py(SearchParams.ModificationRows(taskFiles, datasetId, n => unimod.TryGetValue(n, out var u) ? (string?)u : null)), Is.EqualTo(Py(sp["modification_rows"])), $"{dir}: modification_rows");
            Assert.That(SearchParams.PepRegime(taskFiles), Is.EqualTo(sp["pep_regime"]), $"{dir}: pep_regime");
            Assert.That(SearchParams.PepIterative(taskFiles, sp["release"] as string), Is.EqualTo(sp["pep_iterative"]), $"{dir}: pep_iterative");
            Assert.That(SearchParams.TcAmbiguity(taskFiles), Is.EqualTo(sp["tc_ambiguity"]), $"{dir}: tc_ambiguity");
            var (dbName, dbSha) = SearchParams.SearchedDatabase(doc);
            Assert.That(Py(new List<object?> { dbName, dbSha }), Is.EqualTo(Py(sp["searched_database"])), $"{dir}: searched_database");
            Assert.That(Py(SearchParams.TaskNames(doc)), Is.EqualTo(Py(sp["task_names"])), $"{dir}: task_names");
            Assert.That(Py(ProteinDb.SearchedDatabases(doc, AgingData).Select(x => new List<object?> { x.Path, x.Sha256 }).ToList()),
                Is.EqualTo(Py(sp["searched_databases"])), $"{dir}: searched_databases");

            if (c.GetValueOrDefault("protein_db") is Dictionary<string, object?> expectedDb)
                Expect(expectedDb, () => SequencesDigest(ProteinDb.Load(doc, AgingData)), $"{dir}: protein_db.load");
        }
    }

    private static Dictionary<string, object?> SequencesDigest(ProteinSequences s)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (acc, seqs) in s.ByAccession)
            h.AppendData(Encoding.UTF8.GetBytes($"{acc}\t{s.DatabaseStatus(acc) ?? "None"}\t{string.Join('|', seqs)}\n"));
        return new Dictionary<string, object?>
        {
            ["len"] = (long)s.Count,
            ["files"] = s.Files,
            ["missing"] = s.Missing,
            ["sha256"] = Convert.ToHexStringLower(h.GetHashAndReset()),
        };
    }
}
