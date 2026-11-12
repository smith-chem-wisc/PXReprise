using System.Text.Json;
using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 7: PXReprise's QC payload gives each run's PSM, peptide and protein-group counts over
/// the whole search's 1% set. They are stored under PXReprise's definitions and names of their own, only from the payload
/// that names this bundle's search, and only beside a definition text the ingester carries.</summary>
public class QcPayloadTests
{
    private const string Dataset = "PXD000001";
    private const string SearchSha = "5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e";

    private static readonly IReadOnlySet<string> RunIds =
        new HashSet<string>(StringComparer.Ordinal) { $"{Dataset}:run1", $"{Dataset}:run2" };

    private static Dictionary<string, object?> Definition(string id, string version = "v1") => new() { ["id"] = id, ["version"] = version };

    private static Dictionary<string, object?> V2Definitions() => new()
    {
        ["psms"] = Definition("pxreprise:DEF-PSM-1PCT-RUN"),
        ["peptides"] = Definition("pxreprise:DEF-PEPTIDE-1PCT-RUN"),
        ["protein_groups"] = Definition("pxreprise:DEF-PROTEINGROUP-1PCT-RUN"),
        ["ms2_scans"] = Definition("aging:DEF-MS2"),
    };

    /// <summary>The shape of PXD021757's payload: psms and peptides on every file, protein_groups null (PXReprise's
    /// builder did not find the group table's peptide columns).</summary>
    private static Dictionary<string, object?> Payload(string schema = "qc-payload/2", Dictionary<string, object?>? definitions = null,
        params (string File, object? Psms, object? Peptides, object? Groups)[] files) => new()
    {
        ["schema"] = schema,
        ["files"] = (files.Length > 0 ? files : [("run1", 800L, 661L, null), ("run2", 4774L, 4065L, null)])
            .Select(f => new Dictionary<string, object?>
            {
                ["file"] = f.File,
                ["metrics"] = new Dictionary<string, object?> { ["ms2_scans"] = 17130L, ["psms"] = f.Psms, ["peptides"] = f.Peptides, ["protein_groups"] = f.Groups },
            }).ToList(),
        ["definitions"] = definitions ?? V2Definitions(),
    };

    private sealed class RunDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("datarepo-qcpayload-").FullName;

        /// <summary>A stage folder with a provenance record and, unless <paramref name="payload"/> is null, a payload.</summary>
        public void Stage(string dir, Dictionary<string, object?>? payload, string stage = "qc_payload", string upstreamSha = SearchSha,
            bool recordTheRealSha = true)
        {
            var folder = System.IO.Path.Combine(Path, dir);
            Directory.CreateDirectory(folder);
            var payloadPath = System.IO.Path.Combine(folder, QcPayload.FileName);
            if (payload is not null) File.WriteAllText(payloadPath, JsonSerializer.Serialize(payload));
            var sha = payload is not null && recordTheRealSha ? BundleWriter.Sha256File(payloadPath) : new string('0', 64);
            var provenance = new Dictionary<string, object?>
            {
                ["schema"] = "aging-provenance/3",
                ["stage"] = stage,
                ["upstream"] = new[] { new Dictionary<string, object?> { ["stage"] = "search_metamorpheus", ["path"] = "run/PXD000001/04_search/provenance.json", ["sha256"] = upstreamSha } },
                ["outputs"] = new[] { new Dictionary<string, object?> { ["path"] = $"run/PXD000001/{dir}/qc_payload.json", ["sha256"] = sha } },
            };
            File.WriteAllText(System.IO.Path.Combine(folder, "provenance.json"), JsonSerializer.Serialize(provenance));
        }

        public QcPayload.Reading Read() => QcPayload.Read(QcPayload.Find(Path, SearchSha), RunIds, Dataset);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Test]
    public void TheCountsAreStoredUnderPxreprisesDefinitionsAndNamesOfTheirOwn()
    {
        using var run = new RunDir();
        run.Stage("05_qc", Payload());
        var reading = run.Read();
        Assert.Multiple(() =>
        {
            Assert.That(reading.Read?.StageDir, Is.EqualTo("05_qc"));
            Assert.That(reading.Metrics.Select(m => $"{m["scope"]} {m["scope_id"]} {m["name"]} {m["value"]} {m["definition_id"]} {m["source"]}"), Is.EqualTo(new[]
            {
                "run PXD000001:run1 psms_1pct_whole_search 800 pxreprise:DEF-PSM-1PCT-RUN qc_payload.json",
                "run PXD000001:run1 peptides_1pct_whole_search 661 pxreprise:DEF-PEPTIDE-1PCT-RUN qc_payload.json",
                "run PXD000001:run2 psms_1pct_whole_search 4774 pxreprise:DEF-PSM-1PCT-RUN qc_payload.json",
                "run PXD000001:run2 peptides_1pct_whole_search 4065 pxreprise:DEF-PEPTIDE-1PCT-RUN qc_payload.json",
            }), "a null count is not written; ms2_scans is the QC report's, not read here");
            Assert.That(reading.Metrics.Select(m => (string)m["name"]!), Has.None.AnyOf(Ingester.RunCountNames.ToArray()),
                "never under results.txt's names: the FDR is the whole search's, not the file's");
            Assert.That(Ingester.QcPayloadFindings(reading, Dataset), Is.Empty);
        });
    }

    /// <summary>A run folder can hold more than one search and more than one payload stage (PXD014274 has
    /// <c>05_qc.bak-20261012-excluded-true</c>). Only a stage whose record names this search, by sha256, is read.</summary>
    [Test]
    public void OnlyAPayloadStageNamingThisSearchIsRead()
    {
        using var run = new RunDir();
        run.Stage("04_search", null, stage: "search_metamorpheus");
        run.Stage("05_qc.old", Payload(files: [("run1", 1L, 1L, 1L)]), upstreamSha: new string('a', 64));
        run.Stage("05_qc", Payload());
        Directory.CreateDirectory(Path.Combine(run.Path, "06_broken"));
        File.WriteAllText(Path.Combine(run.Path, "06_broken", "provenance.json"), "{ not json");
        Assert.That(QcPayload.Find(run.Path, SearchSha).Select(s => s.StageDir), Is.EqualTo(new[] { "05_qc" }));
        Assert.That(run.Read().Metrics, Has.Count.EqualTo(4));
    }

    [Test]
    public void APayloadThatIsNotTheFileItsRecordWroteIsNotRead()
    {
        using var run = new RunDir();
        run.Stage("05_qc", Payload(), recordTheRealSha: false);
        var reading = run.Read();
        Assert.That(reading.Metrics, Is.Empty);
        var finding = Ingester.QcPayloadFindings(reading, Dataset).Single();
        Assert.That(finding["code"], Is.EqualTo("qc_payload_not_read"));
        Assert.That((string)finding["message"]!, Does.Contain("`05_qc` (its qc_payload.json is missing, or is not the file its stage record wrote)"));
    }

    /// <summary>qc-payload/1 counts under aging's per-file definitions: results.txt's own lines, which ingest reads from
    /// results.txt. Nothing is read, nothing is said, and the payload is no source.</summary>
    [Test]
    public void APayloadCopyingResultsTxtsPerFileLinesGivesNothing()
    {
        using var run = new RunDir();
        run.Stage("05_qc", Payload("qc-payload/1", new Dictionary<string, object?>
        {
            ["psms"] = Definition("aging:DEF-PSM-1PCT-RUN"),
            ["peptides"] = Definition("aging:DEF-PEPTIDE-1PCT-RUN"),
            ["protein_groups"] = Definition("aging:DEF-PROTEINGROUP-1PCT-RUN"),
        }));
        var reading = run.Read();
        Assert.Multiple(() =>
        {
            Assert.That(reading.Read, Is.Null);
            Assert.That(reading.Metrics, Is.Empty);
            Assert.That(Ingester.QcPayloadFindings(reading, Dataset), Is.Empty);
        });
    }

    /// <summary>PXReprise's D36 filter will arrive as v2 of each definition. Until its text is carried, its counts are left
    /// out and said to be.</summary>
    [Test]
    public void ACountUnderADefinitionVersionNotCarriedIsLeftOut()
    {
        using var run = new RunDir();
        var definitions = V2Definitions();
        definitions["psms"] = Definition("pxreprise:DEF-PSM-1PCT-RUN", "v2");
        run.Stage("05_qc", Payload(definitions: definitions));
        var reading = run.Read();
        Assert.That(reading.Metrics.Select(m => m["name"]).Distinct(), Is.EqualTo(new[] { "peptides_1pct_whole_search" }));
        var finding = Ingester.QcPayloadFindings(reading, Dataset).Single();
        Assert.That(finding["code"], Is.EqualTo("qc_payload_definition_unknown"));
        Assert.That((string)finding["message"]!, Does.Contain("`psms` under `pxreprise:DEF-PSM-1PCT-RUN v2`"));
    }

    [Test]
    public void TwoPayloadsNamingThisSearchAreNeitherRead()
    {
        using var run = new RunDir();
        run.Stage("05_qc", Payload());
        run.Stage("05_qc.bak", Payload());
        var reading = run.Read();
        Assert.That(reading.Metrics, Is.Empty);
        Assert.That((string)Ingester.QcPayloadFindings(reading, Dataset).Single()["message"]!,
            Does.Contain("`05_qc` and `05_qc.bak` (each names this search and gives counts, and which one is the search's is not ours to choose)"));
    }

    /// <summary>PXD023647's payload lists a twelfth file with no counts against eleven runs: nothing to say. A file with a
    /// count and no run is said.</summary>
    [Test]
    public void AFileNamingNoRunIsLeftOutAndSaidOnlyWhenItHasACount()
    {
        using var run = new RunDir();
        run.Stage("05_qc", Payload(files: [("run1", 800L, 661L, 12L), ("run2", 4774L, 4065L, 30L), ("run3_B", null, null, null), ("run4", 5L, null, null)]));
        var reading = run.Read();
        Assert.That(reading.Metrics, Has.Count.EqualTo(6));
        var finding = Ingester.QcPayloadFindings(reading, Dataset).Single();
        Assert.That(finding["code"], Is.EqualTo("qc_payload_files_unplaced"));
        Assert.That((string)finding["message"]!, Does.StartWith("1 file(s)").And.Contain("`run4`"));
    }

    [Test]
    public void APayloadOfAnUnknownLayoutOrNotWholeCountsIsNotTakenAsWritten()
    {
        using (var run = new RunDir())
        {
            run.Stage("05_qc", Payload("qc-payload/3"));
            var reading = run.Read();
            Assert.That(reading.Metrics, Is.Empty);
            Assert.That((string)Ingester.QcPayloadFindings(reading, Dataset).Single()["message"]!,
                Does.Contain("`05_qc` (its schema `qc-payload/3` is not one this ingester reads)"));
        }
        using (var run = new RunDir())
        {
            run.Stage("05_qc", Payload(files: [("run1", 800.5, 661L, null)]));
            var reading = run.Read();
            Assert.That(reading.Metrics.Select(m => m["name"]), Is.EqualTo(new[] { "peptides_1pct_whole_search" }));
            Assert.That(Ingester.QcPayloadFindings(reading, Dataset).Single()["code"], Is.EqualTo("qc_payload_counts_not_whole"));
        }
    }

    // --- the bundle note, with the payload's rows beside results.txt's ------------------------------------------------

    private static Row Metric(int run, string name, string source) => new()
    {
        ["scope"] = "run", ["scope_id"] = $"{Dataset}:run{run}", ["name"] = name, ["value"] = 1L, ["source"] = source,
    };

    private static string Note(int runs, IEnumerable<Row> metrics) =>
        (string)Ingester.RunCountFindings(Enumerable.Range(1, runs).Select(i => new Row { ["run_id"] = $"{Dataset}:run{i}" }).ToList(),
            metrics.ToList(), true, false, Dataset).Single()["message"]!;

    [Test]
    public void TheNoteSaysWhereTheWholeSearchCountsAreAndThatTheyAreAnotherNumber()
    {
        var payload = Enumerable.Range(1, 3).SelectMany(r => new[] { Metric(r, "psms_1pct_whole_search", "qc_payload.json"), Metric(r, "peptides_1pct_whole_search", "qc_payload.json") });
        var message = Note(3, payload.Concat(Enumerable.Range(1, 3).SelectMany(r => new[] { Metric(r, "precursors", "results.txt"), Metric(r, "ms2_scans", "results.txt") })));
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("`psms_1pct` (3 of 3 runs), `peptides` (3 of 3 runs), `protein_groups` (3 of 3 runs)."));
            Assert.That(message, Does.Contain("a different number (the FDR is the whole search's, not recomputed on each file)"));
            Assert.That(message, Does.Contain("`psms_1pct_whole_search` for 3 of the 3 runs, `peptides_1pct_whole_search` for 3 of the 3 runs, "
                + "`protein_groups_1pct_whole_search` for none of them."));
        });
        Assert.That(Note(2, []), Does.EndWith("No QC payload of this search gives these runs a whole-search count either."));
        Assert.That(Note(2, Enumerable.Range(1, 2).SelectMany(r => Ingester.RunCountNames.Take(3).Select(n => Metric(r, n, "results.txt")))),
            Does.Not.Contain("payload"), "only scan counts missing: the payload has none of those");
    }

    // --- one whole ingest ------------------------------------------------------------------------------------------

    private static string RepoRoot() => DataPaths.Root;

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    /// <summary>The fixture deposit with results.txt's per-file PSM lines and a payload stage: results.txt's per-run rows
    /// cite aging's per-file definitions, the payload's cite PXReprise's, and the payload and its record are sources. A
    /// parity ingest keeps 0.32.0's rows.</summary>
    [Test]
    public void AnIngestStoresBothPerRunNumbersEachUnderItsOwnDefinition()
    {
        var copy = Path.Combine(Path.GetTempPath(), "datarepo-qcpayload-ingest-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(DataPaths.Instance, copy);
            var runDir = Path.Combine(copy, "work_root", "run_test", "PXD999999");
            var results = Path.Combine(runDir, "04_search", "mm", "Task3SearchTask", "results.txt");
            File.AppendAllText(results, "QE-002106_GM1_a-calib - Target PSMs with q-value <= 0.01: 30\nQE-002107_GM1_b-calib - Target PSMs with q-value <= 0.01: 27\n");
            var searchSha = BundleWriter.Sha256File(Path.Combine(runDir, "04_search", "provenance.json"));
            var stage = Path.Combine(runDir, "05_qc");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, QcPayload.FileName), JsonSerializer.Serialize(Payload(files:
                [("QE-002106_GM1_a", 31L, 21L, null), ("QE-002107_GM1_b", 27L, 20L, null)])));
            File.WriteAllText(Path.Combine(stage, "provenance.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schema"] = "aging-provenance/3",
                ["definitions"] = "pxreprise",
                ["stage"] = "qc_payload",
                ["upstream"] = new[] { new Dictionary<string, object?> { ["stage"] = "search_metamorpheus", ["path"] = "run_test/PXD999999/04_search/provenance.json", ["sha256"] = searchSha } },
                ["outputs"] = new[] { new Dictionary<string, object?> { ["path"] = "run_test/PXD999999/05_qc/qc_payload.json", ["sha256"] = BundleWriter.Sha256File(Path.Combine(stage, QcPayload.FileName)) } },
            }));
            var manifest = Path.Combine(copy, "manifest.yaml");
            var store = Path.Combine(copy, "out");

            var current = Ingester.Ingest(manifest, "PXD999999", store, rules: IngestRules.Current, overwrite: true);
            var (_, metrics) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "metrics.parquet"));
            string Cites(string scope, string name, string source) => string.Join(",", metrics
                .Where(m => (string)m["scope"]! == scope && (string)m["name"]! == name && (string)m["source"]! == source)
                .Select(m => $"{m["scope_id"]}={m["value"]}:{m["definition_id"]}").Order(StringComparer.Ordinal));
            var (_, definitions) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "definitions.parquet"));
            var (_, records) = ArrowTables.ReadParquet(Path.Combine(current.BundlePath, "provenance_records.parquet"));
            var bundle = JsonDocument.Parse(File.ReadAllText(Path.Combine(current.BundlePath, "bundle.json"))).RootElement;
            var roles = bundle.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("role").GetString()).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(Cites("run", "psms_1pct", "results.txt"),
                    Is.EqualTo("PXD999999:QE-002106_GM1_a=30:aging:DEF-PSM-1PCT-RUN,PXD999999:QE-002107_GM1_b=27:aging:DEF-PSM-1PCT-RUN"));
                Assert.That(Cites("dataset", "psms_1pct", "results.txt"), Is.EqualTo("PXD999999=58:aging:DEF-PSM-1PCT"));
                Assert.That(Cites("run", "ms2_scans", "results.txt"), Does.Contain(":aging:DEF-MS2"), "a scan count partitions; one definition serves both grains");
                Assert.That(Cites("run", "psms_1pct_whole_search", "qc_payload.json"),
                    Is.EqualTo("PXD999999:QE-002106_GM1_a=31:pxreprise:DEF-PSM-1PCT-RUN,PXD999999:QE-002107_GM1_b=27:pxreprise:DEF-PSM-1PCT-RUN"));
                Assert.That(definitions.Select(d => (string)d["definition_id"]!), Is.SupersetOf(new[]
                {
                    "aging:DEF-PSM-1PCT-RUN", "pxreprise:DEF-PSM-1PCT-RUN", "pxreprise:DEF-PEPTIDE-1PCT-RUN",
                }));
                Assert.That(definitions.Single(d => (string)d["definition_id"]! == "pxreprise:DEF-PSM-1PCT-RUN")["text"],
                    Does.Contain("It is NOT the number `aging:DEF-PSM-1PCT-RUN v1` named."));
                Assert.That(roles, Does.Contain("qc_payload").And.Contain("provenance:05_qc"));
                Assert.That(records.Select(r => r["stage"]), Does.Contain("qc_payload"));
            });

            var parity = Ingester.Ingest(manifest, "PXD999999", store, rules: IngestRules.Python0320, overwrite: true);
            (_, metrics) = ArrowTables.ReadParquet(Path.Combine(parity.BundlePath, "metrics.parquet"));
            Assert.Multiple(() =>
            {
                Assert.That(Cites("run", "psms_1pct", "results.txt"), Does.Contain("=30:aging:DEF-PSM-1PCT,"), "0.32.0's rows, kept");
                Assert.That(metrics.Where(m => (string)m["source"]! == QcPayload.FileName), Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
        }
    }
}
