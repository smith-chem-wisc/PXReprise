using System.Text.Json.Nodes;
using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>The whole search stage, offline, against tests/FakeMetaMorpheus (built as CMD).</summary>
[NonParallelizable]   // FAKE_MM_* switches are process environment variables
public class SearchStageTests
{
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

    private static Rig Setup(bool library = true, string machineExtra = "")
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
