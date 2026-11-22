using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Seam.Tests;

/// <summary>
/// The engine and datarepo together (decision record 0073: one release for both). They meet in four places: the
/// manifest, the search output folder, datarepo's command line and its JSON reply, and the shared PXReprise.Storage code.
/// Here the engine's search stage runs on a real MetaMorpheus search (the fake CMD replays one, <c>FAKE_MM_REPLAY</c>),
/// the engine writes the deposit's manifest entry as a batch does, the real datarepo ingests it with the batch's own
/// command (<see cref="BatchRunner.IngestCommand"/>), the engine reads the reply as a batch does, and datarepo builds a
/// catalog from it. What datarepo made of the engine's output is compared with <c>Expected/PXD999999.txt</c>, so a change
/// on either side that moves it fails here until the same pull request updates that file and says why.
/// </summary>
/// <remarks>
/// <para>Not compared: the bundle id. It hashes each provenance file's bytes, and the engine's hold the clock, the
/// machine's name and this run's folder, so it is new on every run. It is checked instead to survive the batch's
/// delivery record and a second ingest, which is what a batch's reconcile pass relies on.</para>
/// <para>Not yet crossed: the fetch and QC stages. Their records are the fixture instance's
/// (<c>tests/datarepo-instance</c>), because the QC stage reads only Thermo .raw files and the repository holds no small
/// one with a recorded search.</para>
/// <para>To accept a change on purpose, run with <c>PXREPRISE_SEAM_WRITE_EXPECTED=1</c> and commit the rewritten file
/// with the change that moved it.</para>
/// </remarks>
[NonParallelizable]   // FAKE_MM_REPLAY is a process environment variable
public class EngineToDatarepoTests
{
    private const string Accession = "PXD999999";
    private const string Release = "1.1.11";   // the release that wrote the recorded search

    private string _root = "";
    private string _runRoot = "";
    private string _manifest = "";
    private string _catalog = "";
    private Profile _profile = null!;
    private SearchOutcome _search = null!;
    private string _entry = "";
    private (int Exit, string Stdout, string Stderr) _ingest;

    private static string RepoRoot { get; } = FindRoot();
    private static string Fixture => Path.Combine(RepoRoot, "tests", "datarepo-instance");
    private static string Recorded => Path.Combine(Fixture, "work_root", "run_test", Accession);
    private static string ExpectedFile => Path.Combine(RepoRoot, "tests", "PXReprise.Seam.Tests", "Expected", Accession + ".txt");

    /// <summary>A project's build output beside this one's: <c>&lt;project&gt;/bin/&lt;configuration&gt;/&lt;framework&gt;</c>.</summary>
    private static string Built(params string[] project)
    {
        var here = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        return Path.Combine([RepoRoot, .. project, "bin", here.Parent!.Name, here.Name]);
    }

    /// <summary>datarepo as a machine file names it: the program itself.</summary>
    private static string Datarepo => Path.Combine(Built("src", "PXReprise.DataCli"), OperatingSystem.IsWindows() ? "datarepo.exe" : "datarepo");

    [OneTimeSetUp]
    public async Task SearchDeliverAndBuild()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pxreprise-seam-" + Guid.NewGuid().ToString("N"))).FullName;
        _runRoot = Path.Combine(_root, "runs");
        string run = Path.Combine(_runRoot, Accession);

        // MetaMorpheus as installed: the fake CMD, with the modification files and contaminant panel the engine reads
        // beside it. The machine's settings folder is where a machine file puts it by default, <work_root>/mm_settings.
        string install = Path.Combine(_root, "MetaMorpheus-" + Release);
        CopyTree(Built("tests", "FakeMetaMorpheus"), install);
        string settings = Path.Combine(Fixture, "work_root", "mm_settings", Release);
        CopyTree(settings, install);
        CopyTree(settings, Path.Combine(_root, "mm_settings", Release));
        string db = Directory.CreateDirectory(Path.Combine(_root, "db")).FullName;
        File.Copy(Path.Combine(Fixture, "work_root", "db", "test_human.xml"), Path.Combine(db, "test_human.xml"));
        Directory.CreateDirectory(Path.Combine(install, "Contaminants"));
        File.Copy(Path.Combine(Fixture, "work_root", "db", "test_contaminants.xml"), Path.Combine(install, "Contaminants", "test_contaminants.xml"));

        var machine = Machine.Load(Write(_root, "machine.toml",
            // CMD.dll, run as `dotnet CMD.dll`: the one launch that works on every OS (the apphost is CMD.exe only on Windows).
            $"work_root = '{_root}'\ndatabase_dir = '{db}'\nmax_threads = 4\n[metamorpheus]\n\"{Release}\" = '{Path.Combine(install, "CMD.dll")}'\n"));
        _profile = ProfileLoader.Load(Write(_root, "profile.toml", $"""
            id = "seam-dda"
            version = 1
            description = "a label-free DDA search for the seam test"
            [accepts]
            acquisition = ["dda"]
            [engine]
            metamorpheus = "{Release}"
            tasks = ["Calibration", "Gptmd", "Search"]
            spectral_library = false
            [databases.human]
            proteome = "test_human.xml"
            taxon = 9606
            [contaminants]
            panel = "test_contaminants.xml"
            [quant]
            method = "flashlfq"
            mbr = true
            [deposit]
            max_files = 60
            [qc]
            ms2_analyzer = "orbitrap"
            """));

        // The fetch and QC records are the fixture's. The raw files are placeholders: the fake CMD never reads them, and
        // the batch's cleanup deletes them before a delivery in any case.
        CopyTree(Path.Combine(Recorded, "02_fetch"), Path.Combine(run, "02_fetch"));
        CopyTree(Path.Combine(Recorded, "02b_qc"), Path.Combine(run, "02b_qc"));
        string spectra = Directory.CreateDirectory(Path.Combine(run, "02_fetch", "spectra")).FullName;
        foreach (string name in new[] { "QE-002106_GM1_a.raw", "QE-002107_GM1_b.raw" }) Write(spectra, name, name);

        Environment.SetEnvironmentVariable("FAKE_MM_REPLAY", Path.Combine(Recorded, "04_search", "mm", "Task3SearchTask"));
        try
        {
            _search = await SearchStage.RunAsync(new SearchRequest(_profile, "human", machine, spectra, Path.Combine(run, "04_search"),
                Path.Combine(run, "02b_qc"), [], [], "2026-11-22", Accession, UseLibrary: false), CancellationToken.None);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_REPLAY", null); }
        Assert.That(_search.Success, Is.True, "the engine's search stage accepts the replayed search");

        // The delivery, as BatchRunner.DeliverOneAsync makes it: a new question's manifest against the machine's work root
        // (PXReprise's BatchRunnerTests pin that the batch passes that root), the deposit's entry, then datarepo's ingest.
        _manifest = Path.Combine(_root, "question", "manifest.yaml");
        ManifestEntry.EnsureExists(_manifest, "seam", machine.WorkRoot);
        ManifestEntry.Append(_manifest, new QueueEntry(Accession, "A deposit searched for the seam test", "human"), _profile, run, null, machine.WorkRoot);
        string lf = ManifestEntry.ReadLf(_manifest);
        _entry = lf[lf.IndexOf($"  - accession: {Accession}\n", StringComparison.Ordinal)..];
        _ingest = Run(BatchRunner.IngestCommand(Datarepo, _manifest, Accession, json: true));
        Assert.That(_ingest.Exit, Is.Zero, "datarepo ingests what the engine wrote:\n" + _ingest.Stderr);

        _catalog = Path.Combine(_root, "catalog.duckdb");
        var build = Run(Datarepo, "build", _manifest, "--out", _catalog);
        Assert.That(build.Exit, Is.Zero, "datarepo builds a catalog from it:\n" + build.Stdout + build.Stderr);
    }

    /// <summary>The run's folder is removed, unless <c>PXREPRISE_SEAM_KEEP=1</c> keeps it to be read.</summary>
    [OneTimeTearDown]
    public void Remove()
    {
        if (Environment.GetEnvironmentVariable("PXREPRISE_SEAM_KEEP") == "1")
        {
            TestContext.Progress.WriteLine($"kept {_root}");
            return;
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }   // a process that still holds a file on Windows; the folder is in the temp directory
    }

    /// <summary>The batch reads the reply as an ingest, with the bundle it wrote.</summary>
    [Test]
    public void TheEngineReadsDatarepoReplyAsAnIngestOfTheBundleItWrote()
    {
        var o = IngestEnvelope.Read(_ingest.Stdout, Accession, _ingest.Exit);
        Assert.Multiple(() =>
        {
            Assert.That(o.Rc, Is.Zero);
            Assert.That(o.Status, Is.EqualTo("ingested"));
            Assert.That(o.Reasons, Is.Empty);
            Assert.That(o.BundleId, Does.Match("^[0-9a-f]{16}$"));
            Assert.That(File.Exists(Path.Combine(_root, "question", "store", Accession, o.BundleId!, "bundle.json")), Is.True,
                "the bundle is in the store the engine's manifest names");
        });
    }

    /// <summary>A batch marks the entry delivered after the ingest, and its reconcile pass ingests a deposit again
    /// whenever its state is lost. Neither may move the bundle.</summary>
    [Test]
    public void TheDeliveryRecordAndASecondIngestKeepTheBundle()
    {
        string first = IngestEnvelope.Read(_ingest.Stdout, Accession, _ingest.Exit).BundleId!;
        ManifestEntry.MarkDelivered(_manifest, Accession, new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
        var again = Run(BatchRunner.IngestCommand(Datarepo, _manifest, Accession, json: true));
        var o = IngestEnvelope.Read(again.Stdout, Accession, again.Exit);
        Assert.That((o.Rc, o.Status, o.BundleId), Is.EqualTo((0, "unchanged", first)), again.Stderr);
    }

    /// <summary>
    /// A new question's bundle holds what its search read: both databases, the modification definitions, and the fetch and
    /// QC records upstream of the search. The engine's provenance names each input under the machine's work root, where
    /// the machine also keeps its MetaMorpheus settings, and datarepo reads them under the manifest's <c>work_root</c>; so
    /// the manifest the engine writes must name that root. The same deposit ingested from the fixture's own records gives
    /// exactly these.
    /// </summary>
    /// <remarks>Before smith-chem-wisc/PXReprise#46 the engine wrote the run root there, and this bundle had no database,
    /// eight unresolved modifications (Carbamidomethyl on C among them) and only the search's own record, while the
    /// ingest exited 0.</remarks>
    [Test]
    public void ANewQuestionsBundleHoldsTheSearchsDatabasesModificationsAndUpstreamRecords()
    {
        var databases = Query("SELECT database, role FROM dataset_databases ORDER BY database").Select(r => $"{r["database"]} {r["role"]}").ToList();
        var stages = Query("SELECT stage FROM provenance_records ORDER BY stage").Select(r => (string)r["stage"]!).ToList();
        var unresolved = JsonNode.Parse(_ingest.Stdout)!["datasets"]![0]!["unresolved_modifications"]!.AsObject()
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(databases, Is.EqualTo(new[] { "test_contaminants.xml contaminant", "test_human.xml target" }));
            Assert.That(stages, Is.EqualTo(new[] { "fetch", "qc_spectra", "search_metamorpheus" }));
            Assert.That(unresolved, Is.EqualTo(new[] { "UniProt:N-acetylalanine on A", "UniProt:N-acetylglutamate on E", "UniProt:N6,N6-dimethyllysine on K" }),
                "only the three the fixture's settings do not define; never Carbamidomethyl on C");
        });
    }

    /// <summary>What datarepo made of the engine's search, against the committed file.</summary>
    [Test]
    public void WhatDatarepoMakesOfTheEnginesSearchIsAsCommitted()
    {
        string actual = Summary();
        if (Environment.GetEnvironmentVariable("PXREPRISE_SEAM_WRITE_EXPECTED") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExpectedFile)!);
            File.WriteAllText(ExpectedFile, actual, new UTF8Encoding(false));
            Assert.Inconclusive($"rewrote {ExpectedFile}: commit it with the change that moved it, and say why");
        }
        string expected = File.Exists(ExpectedFile) ? File.ReadAllText(ExpectedFile).ReplaceLineEndings("\n") : "";
        if (actual == expected) return;
        string written = Path.Combine(TestContext.CurrentContext.WorkDirectory, Accession + ".actual.txt");
        File.WriteAllText(written, actual, new UTF8Encoding(false));
        TestContext.AddTestAttachment(written);
        var (a, e) = (actual.Split('\n'), expected.Split('\n'));
        int line = Enumerable.Range(0, Math.Max(a.Length, e.Length)).First(i => i >= a.Length || i >= e.Length || a[i] != e[i]);
        Assert.Fail($"""
            What datarepo makes of the engine's search moved, first at line {line + 1} of {ExpectedFile}:
              expected: {(line < e.Length ? e[line] : "(end of file)")}
              actual:   {(line < a.Length ? a[line] : "(end of file)")}
            The whole of it is in {written}. If the change is meant, rewrite the file (PXREPRISE_SEAM_WRITE_EXPECTED=1) and
            say in the pull request why it moved.
            """);
    }

    /// <summary>
    /// Findings that measure this run rather than its data, so they are left out of <see cref="Summary"/>.
    /// <c>low_core_use</c> is the engine's CPU seconds over wall seconds for the fake CMD's run, and the CPU time of a
    /// process that has exited is read on Windows but not on Linux.
    /// </summary>
    private static readonly string[] Measured = ["low_core_use"];

    /// <summary>
    /// What crosses the seam, in a stable order and with nothing of this run's own (its folder, its clock, the bundle id):
    /// the manifest entry the engine wrote, datarepo's reply to the ingest, and the catalog it built: every table's rows,
    /// every metric and every finding. The findings are listed rather than counted, without <see cref="Measured"/>.
    /// </summary>
    private string Summary()
    {
        var sb = new StringBuilder();
        sb.Append("# What datarepo makes of the engine's search of ").Append(Accession).Append(" (tests/PXReprise.Seam.Tests).\n");
        sb.Append("# Rewrite with PXREPRISE_SEAM_WRITE_EXPECTED=1, and say in the pull request why it moved.\n");

        sb.Append("\n## The manifest entry the engine wrote\n\n").Append(_entry.TrimEnd('\n')).Append('\n');

        var dataset = JsonNode.Parse(_ingest.Stdout)!["datasets"]!.AsArray().Single()!.AsObject();
        sb.Append("\n## datarepo ingest --json\n\n");
        sb.Append("status: ").Append(dataset["status"]).Append('\n');
        sb.Append("mismatches: ").Append(dataset["mismatches"]?.ToJsonString()).Append('\n');
        sb.Append("unresolved_modifications: ").Append(dataset["unresolved_modifications"]?.ToJsonString()).Append('\n');
        sb.Append("unmatched_runs: ").Append(dataset["unmatched_runs"]?.ToJsonString()).Append('\n');
        sb.Append("findings (warnings and errors): ").Append(string.Join(", ", dataset["findings"]!.AsArray()
            .Select(f => (string)f!).Where(f => !Measured.Contains(f)))).Append('\n');
        sb.Append("tables (findings listed below):\n");
        foreach (var (table, rows) in dataset["tables"]!.AsObject().Where(kv => kv.Key != "findings").OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append("  ").Append(table).Append(": ").Append(rows?.ToJsonString()).Append('\n');
        sb.Append("checks:\n");
        foreach (var check in dataset["checks"]!.AsArray().Select(c => c!.AsObject()).OrderBy(c => (string)c["name"]!, StringComparer.Ordinal))
            sb.Append("  ").Append(check["name"]).Append(": ok ").Append(check["ok"]?.ToJsonString())
              .Append(", observed ").Append(check["observed"]?.ToJsonString()).Append(", expected ").Append(check["expected"]?.ToJsonString())
              .Append(", source ").Append(check["source"]).Append('\n');

        sb.Append("\n## datarepo build: every catalog table's rows (findings listed below)\n\n");
        var tables = Query("SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_name <> 'findings' ORDER BY table_name")
            .Select(r => (string)r["table_name"]!).ToList();
        var counts = Query(string.Join(" UNION ALL ", tables.Select(t => $"SELECT '{t}' AS t, count(*) AS n FROM \"{t}\"")))
            .ToDictionary(r => (string)r["t"]!, r => r["n"]!.ToJsonString());
        foreach (string t in tables) sb.Append(t).Append(": ").Append(counts[t]).Append('\n');

        sb.Append("\n## Every metric\n\n");
        foreach (var m in Query("SELECT name, scope_id, value, source FROM metrics ORDER BY name, scope_id, source"))
            sb.Append(m["name"]).Append(' ').Append(m["scope_id"]).Append(' ').Append(m["value"]?.ToJsonString()).Append(" (").Append(m["source"]).Append(")\n");

        sb.Append("\n## Every finding but ").Append(string.Join(", ", Measured)).Append("\n\n");
        string measured = string.Join(", ", Measured.Select(c => $"'{c}'"));
        foreach (var f in Query($"SELECT code, severity, count(*) AS n FROM findings WHERE code NOT IN ({measured}) GROUP BY code, severity ORDER BY code, severity"))
            sb.Append(f["code"]).Append(' ').Append(f["severity"]).Append(" x").Append(f["n"]?.ToJsonString()).Append('\n');
        return Normalise(sb.ToString());
    }

    /// <summary>This run's folder as <c>&lt;root&gt;</c>, with '/' separators.</summary>
    private string Normalise(string text) =>
        text.Replace(_root.Replace('\\', '/'), "<root>").Replace(_root, "<root>").ReplaceLineEndings("\n");

    private List<JsonObject> Query(string sql)
    {
        var q = Run(Datarepo, "query", _catalog, sql, "--format", "json", "--limit", "0");
        Assert.That(q.Exit, Is.Zero, sql + "\n" + q.Stderr);
        // One JSON object a line.
        return q.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] argv)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail($"{string.Join(' ', argv)} did not finish in 5 minutes");
        }
        return (p.ExitCode, stdout, stderr.Result);
    }

    private static string Write(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PXReprise.slnx"))) return dir.FullName;
        throw new InvalidOperationException("no PXReprise.slnx above " + TestContext.CurrentContext.TestDirectory);
    }
}
