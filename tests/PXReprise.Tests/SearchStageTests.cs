using System.Text.Json.Nodes;
using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>The whole search stage, offline, against tests/FakeMetaMorpheus (built as CMD).</summary>
[NonParallelizable]   // FAKE_MM_* switches are process environment variables
public class SearchStageTests
{
    // dataRepo drops excluded runs only from this block (DATAREPO-51); the C# port had lost it, and PXD034059's QC-excluded
    // blank came back as a run. The reason is the exact sentence aging's batch_runner.py wrote (PXD047293).
    [Test]
    public void QcExcludedFilesAreRecordedForDataRepoInTheShapeThePythonWrote()
    {
        var qc = new JsonObject
        {
            ["b.raw"] = new JsonObject { ["pass"] = false, ["fail_reasons"] = new JsonArray("too_few_ms2") },
            ["a.raw"] = new JsonObject { ["pass"] = false, ["fail_reasons"] = new JsonArray("too_few_ms2") },
            ["c.raw"] = new JsonObject { ["pass"] = true, ["fail_reasons"] = new JsonArray() },
        };
        var rec = SearchStage.ExcludedFilesRecord(new[] { "b.raw", "a.raw" }, qc);
        Assert.That(rec["files"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "a.raw", "b.raw" }));
        Assert.That(rec["reason"]!.GetValue<string>(), Is.EqualTo(
            "D52: failed qc_spectra on too_few_ms2 (a blank or failed injection); excluded with a record instead of dropping the deposit"));
        Assert.That(SearchStage.ExcludedFilesRecord(new[] { "c.raw" }, qc)["reason"]!.GetValue<string>(), Is.EqualTo("excluded from the search by --exclude"));
    }

    private static string FakeBin
    {
        get
        {
            // tests/PXReprise.Tests/bin/<cfg>/net10.0 -> tests/FakeMetaMorpheus/bin/<cfg>/net10.0
            var test = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            return Path.Combine(test.Parent!.Parent!.Parent!.Parent!.FullName, "FakeMetaMorpheus", "bin", test.Parent.Name, test.Name);
        }
    }

    private sealed record Rig(string Root, Machine Machine, Profile Profile, string Spectra, string Qc);

    private static Rig Setup(bool library = true, string machineExtra = "", bool depositChemistry = false)
    {
        string root = TestSupport.TempDir();
        // A MetaMorpheus "install": the fake CMD, and the Mods and Contaminants folders the engine reads beside it.
        string install = Path.Combine(root, "MetaMorpheus-1.1.11");
        Directory.CreateDirectory(install);
        foreach (string f in Directory.EnumerateFiles(FakeBin)) File.Copy(f, Path.Combine(install, Path.GetFileName(f)));
        TestSupport.WriteFile(Directory.CreateDirectory(Path.Combine(install, "Mods")).FullName, "Mods.txt",
            "ID   Acetylation\nTG   K\nMT   Common Biological\n//\nID   GG (Ubiquitination Site)\nTG   K\nMT   Trypsin Digested\n//\n");
        TestSupport.WriteFile(Directory.CreateDirectory(Path.Combine(install, "Contaminants")).FullName, "MetaMorpheusContaminants.xml",
            "<uniprot>\n<entry>\n<accession>P02769</accession>\n</entry>\n<entry>\n<accession>P04264</accession>\n</entry>\n</uniprot>\n");
        string db = Directory.CreateDirectory(Path.Combine(root, "db")).FullName;
        TestSupport.WriteFile(db, "proteome.xml", "<uniprot>\n<entry>\n<accession>Q8WZ42</accession>\n</entry>\n<entry>\n<accession>P04264</accession>\n</entry>\n</uniprot>\n");
        var machine = Machine.Load(TestSupport.WriteFile(root, "machine.toml",
            // CMD.dll, run as `dotnet CMD.dll`: the one launch that works on every OS (the apphost is CMD.exe only on Windows).
            $"work_root = '{root}'\ndatabase_dir = '{db}'\nmax_threads = 4\n{machineExtra}[metamorpheus]\n\"1.1.11\" = '{Path.Combine(install, "CMD.dll")}'\n"));
        var profile = ProfileLoader.Load(TestSupport.WriteFile(root, "profile.toml", $"""
            id = "test-dda"
            version = 1
            [accepts]
            acquisition = ["dda"]
            [engine]
            metamorpheus = "1.1.11"
            tasks = ["Calibration", "Gptmd", "Search"]
            gptmd_extra_mods = ["Trypsin Digested\tGG (Ubiquitination Site) on K"]
            spectral_library = {(library ? "true" : "false")}
            {(depositChemistry ? "chemistry = \"deposit\"" : "")}
            [databases.human]
            proteome = "proteome.xml"
            taxon = 9606
            [contaminants]
            panel = "MetaMorpheusContaminants.xml"
            [quant]
            method = "flashlfq"
            mbr = true
            [deposit]
            max_files = 60
            [qc]
            ms2_analyzer = "orbitrap"
            """));
        string spectra = Directory.CreateDirectory(Path.Combine(root, "run", "02_fetch", "spectra")).FullName;
        TestSupport.WriteFile(spectra, "a.raw", "x");
        TestSupport.WriteFile(spectra, "b.raw", "y");
        string qc = Directory.CreateDirectory(Path.Combine(root, "run", "02b_qc")).FullName;
        TestSupport.WriteFile(qc, "qc_report.json", """{"a.raw":{"pass":true,"ms2":20},"b.raw":{"pass":true,"ms2":20}}""");
        TestSupport.WriteFile(qc, "provenance.json", """{"schema":"aging-provenance/3","stage":"qc_spectra","outputs":[],"inputs":[]}""");
        return new Rig(root, machine, profile, spectra, qc);
    }

    private static SearchRequest Req(Rig r, string outName, string acc = "PXD000301") =>
        new(r.Profile, "human", r.Machine, r.Spectra, Path.Combine(r.Root, "run", outName), r.Qc, Array.Empty<string>(), Array.Empty<string>(), "2026-09-28", acc);

    [Test]
    public async Task ASearchWritesTheLibraryAndTheNextOneUpdatesItWithTheRecordIntact()
    {
        var rig = Setup();
        var first = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
        var second = await SearchStage.RunAsync(Req(rig, "04_search_2", "PXD000302"), CancellationToken.None);
        Assert.That((first.Success, second.Success), Is.EqualTo((true, true)));

        var p1 = JsonNode.Parse(File.ReadAllText(first.ProvenanceFile))!;
        var p2 = JsonNode.Parse(File.ReadAllText(second.ProvenanceFile))!;
        Assert.Multiple(() =>
        {
            Assert.That(p1["schema"]!.GetValue<string>(), Is.EqualTo("aging-provenance/3"));
            Assert.That(p1["spectral_library"]!["mode"]!.GetValue<string>(), Is.EqualTo("write"));
            Assert.That((int)p1["spectral_library"]!["written"]!["version"]!, Is.EqualTo(1));
            Assert.That(p2["spectral_library"]!["mode"]!.GetValue<string>(), Is.EqualTo("update"));
            Assert.That((int)p2["spectral_library"]!["written"]!["n_spectra"]!, Is.EqualTo(3), "2 carried + 1 new");
            Assert.That((int)p1["id_rate"]!["psms_1pct"]!, Is.EqualTo(8));
            Assert.That((int)p1["id_rate"]!["ms2"]!, Is.EqualTo(40));
            Assert.That((int)p1["mbr"]!["mbr_kept"]!, Is.EqualTo(1));
            Assert.That((double)p1["contamination"]!["psm_share"]!, Is.EqualTo(0.1111));
            // P04264 is in both the panel and the proteome: the shared list the upper bound uses.
            Assert.That((int)p1["contamination"]!["shared_accessions_n"]!, Is.EqualTo(1));
            Assert.That(p1["gptmd_extra_mods"]!.AsArray(), Has.Count.EqualTo(1));
        });
        string search = File.ReadAllText(Path.Combine(rig.Root, "run", "04_search_2", "tasks", "3_SearchTask.toml"));
        Assert.That(search, Does.Contain("UpdateSpectralLibrary = true\r\n").And.Contain("WriteSpectralLibrary = false\r\n")
            .And.Contain("MatchBetweenRuns = true\r\n").And.Contain("MaxThreadsToUsePerFile = 4\r\n"));
    }

    // G19 (D23, D24, D27): the deposit's chemistry reaches MetaMorpheus: cysteine mods in every task, a protease per raw
    // file in <raw>.toml, and the facts with their sources in the provenance.
    private static PXReprise.Discovery.DepositChemistry Nem(IReadOnlyDictionary<string, string>? perFile = null) => new(
        new("Glu-C", PXReprise.Discovery.ChemistrySource.ProtocolText, "digested with Glu-C"), perFile,
        new("Nethylmaleimide + NEM:2H(5)", PXReprise.Discovery.ChemistrySource.PridePtm),
        new[] { new PXReprise.Discovery.CysMod("Nethylmaleimide", "UNIMOD:108", false), new PXReprise.Discovery.CysMod("NEM:2H(5)", "UNIMOD:776", false) },
        new("label_free", PXReprise.Discovery.ChemistrySource.Default), null);

    [Test]
    public async Task ADepositsChemistryReachesEveryTaskAndEachRawFile()
    {
        var rig = Setup(library: false, depositChemistry: true);
        var perFile = new Dictionary<string, string> { ["a.raw"] = "Glu-C", ["b.raw"] = "Asp-N" };
        var o = await SearchStage.RunAsync(Req(rig, "04_search") with { Chemistry = Nem(perFile) }, CancellationToken.None);
        Assert.That(o.Success, Is.True);
        foreach (string task in new[] { "1_CalibrationTask.toml", "2_GptmdTask.toml", "3_SearchTask.toml" })
        {
            string text = File.ReadAllText(Path.Combine(rig.Root, "run", "04_search", "tasks", task));
            Assert.That(text, Does.Contain("ListOfModsFixed = \"\"").And.Contain(@"Unimod\tNethylmaleimide on C\t\tUnimod\tNEM:2H(5) on C"), task);
            Assert.That(text, Does.Contain("Protease = \"trypsin\""), "per-file proteases: the task keeps its default");
        }
        Assert.That(File.ReadAllText(Path.Combine(rig.Spectra, "a.toml")), Does.StartWith(SearchStage.PerFileTomlMarker).And.Contain("DigestionAgent = \"Glu-C\""));
        Assert.That(File.ReadAllText(Path.Combine(rig.Spectra, "b.toml")), Does.Contain("DigestionAgent = \"Asp-N\""));
        var prov = JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!;
        Assert.That(prov["chemistry"]!["cysteine_mods"]![1]!["metamorpheus"]!.GetValue<string>(), Is.EqualTo("Unimod\tNEM:2H(5) on C"));
        Assert.That(prov["chemistry"]!["guessed"]!.GetValue<bool>(), Is.True, "the protease came from protocol text");

        // One protease for the whole deposit goes in the task files, and our stale per-file tomls are removed.
        var single = await SearchStage.RunAsync(Req(rig, "04_search_2") with { Chemistry = Nem() }, CancellationToken.None);
        Assert.That(single.Success, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(rig.Root, "run", "04_search_2", "tasks", "3_SearchTask.toml")), Does.Contain("Protease = \"Glu-C\""));
        Assert.That(File.Exists(Path.Combine(rig.Spectra, "a.toml")), Is.False);
    }

    [Test]
    public void ADepositChemistryProfileRefusesASearchWithoutOneAndAForeignPerFileToml()
    {
        var rig = Setup(library: false, depositChemistry: true);
        Assert.That(async () => await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None),
            Throws.TypeOf<PXReprise.Cli.UsageException>().With.Message.Contains("chemistry"));
        TestSupport.WriteFile(rig.Spectra, "a.toml", "Protease = \"trypsin\"\n");
        Assert.That(async () => await SearchStage.RunAsync(Req(rig, "04_search_x") with { Chemistry = Nem(new Dictionary<string, string> { ["a.raw"] = "Glu-C", ["b.raw"] = "Glu-C" }) }, CancellationToken.None),
            Throws.TypeOf<SearchSetupException>().With.Message.Contains("not written by PXReprise"));
    }

    // D27: MetaMorpheus only warns when it cannot use a modification, and searches on without it.
    [TestCase("Unrecognized mod Unimod\tBogus on C; are you using an old .toml?")]
    [TestCase("Problem parsing the file-specific toml a.toml: Unrecognized digestion agent")]
    public async Task ASilentlyIgnoredModificationOrPerFileTomlFailsTheSearch(string warning)
    {
        var rig = Setup(library: false, depositChemistry: true);
        Environment.SetEnvironmentVariable("FAKE_MM_WARN", warning);
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search") with { Chemistry = Nem() }, CancellationToken.None);
            Assert.That(o.Success, Is.False);
            Assert.That(JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!["notes"]!.AsArray().Select(n => n!.GetValue<string>()),
                Has.Some.Contains("FAILED (G19/D27)"));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_WARN", null); }
    }

    [Test]
    public async Task FlashLfqFailingSilentlyIsNotSuccessAndTheLibraryIsNotAdvanced()
    {
        var rig = Setup();
        Environment.SetEnvironmentVariable("FAKE_MM_NO_PROTEIN_GROUPS", "1");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That(o.Success, Is.False);
            var p = JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!;
            Assert.That(p["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.Contains("FlashLFQ failed silently").And.Some.Contains("NOT registered"));
            Assert.That(File.Exists(SpectralLibrary.RegistryPath(rig.Machine.SpectralLibraryRoot)), Is.False);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_NO_PROTEIN_GROUPS", null); }
    }

    [Test]
    public async Task ADesignTheEngineCouldNotUseFailsTheSearch()
    {
        var rig = Setup(library: false);
        TestSupport.WriteFile(rig.Spectra, "ExperimentalDesign.tsv", "FileName\tCondition\tBiorep\tFraction\tTechrep\n");
        Environment.SetEnvironmentVariable("FAKE_MM_SKIP_QUANT", "1");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That(o.Success, Is.False);
            Assert.That(o.Flags, Has.Some.StartsWith("quantification_skipped"));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_SKIP_QUANT", null); }
    }

    // 2026-09-29: PXD069093 sat in MetaMorpheus's PEP step for hours on a busy box, silent but using ~49 cores, and a
    // wall-clock limit was about to kill it. Slow is not hung: only a search that uses no CPU is killed early.
    [Test]
    public async Task ASilentButWorkingSearchOutlivesTheStallWindow()
    {
        var rig = Setup(library: false, machineExtra: "search_stall_minutes = 0.05\n");   // 3 s
        Environment.SetEnvironmentVariable("FAKE_MM_BUSY_SECONDS", "8");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That((o.Success, o.TimedOut), Is.EqualTo((true, false)));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_BUSY_SECONDS", null); }
    }

    [Test]
    public async Task AHungSearchIsKilledByTheStallCheckAndSaysSo()
    {
        var rig = Setup(library: false, machineExtra: "search_stall_minutes = 0.05\n");
        Environment.SetEnvironmentVariable("FAKE_MM_HANG", "1");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That((o.Success, o.TimedOut), Is.EqualTo((false, true)));
            var p = JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!;
            Assert.That(p["stalled_after_s"], Is.Not.Null);
            Assert.That(p["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.Contains("used no CPU"));
            Assert.That((int)p["params"]!["stall_s"]!, Is.EqualTo(3));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_HANG", null); }
    }

    [Test]
    public async Task AnOutputFolderIsNeverReused()
    {
        var rig = Setup(library: false);
        await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
        Assert.ThrowsAsync<PXReprise.Cli.UsageException>(() => SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None));
    }
}
