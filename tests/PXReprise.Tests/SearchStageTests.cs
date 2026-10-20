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

    private static Rig Setup(bool library = true, string machineExtra = "", bool depositChemistry = false, string mm = "1.1.11", bool design = false)
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
            $"work_root = '{root}'\ndatabase_dir = '{db}'\nmax_threads = 4\n{machineExtra}[metamorpheus]\n\"{mm}\" = '{Path.Combine(install, "CMD.dll")}'\n"));
        var profile = ProfileLoader.Load(TestSupport.WriteFile(root, "profile.toml", $"""
            id = "test-dda"
            version = 1
            [accepts]
            acquisition = ["dda"]
            [engine]
            metamorpheus = "{mm}"
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
            {(design ? "design = \"sdrf\"" : "")}
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
            // G33: every database GPTMD wrote is an output with its sha256, the proteome the search loaded included,
            // not just the first file found (that was the contaminants).
            var gptmdOutputs = p1["outputs"]!.AsArray().Where(o => o!["path"]!.GetValue<string>().EndsWith("GPTMD.xml", StringComparison.Ordinal)).ToList();
            Assert.That(gptmdOutputs.Select(o => Path.GetFileName(o!["path"]!.GetValue<string>())),
                Is.EquivalentTo(new[] { "db-GPTMD.xml", "MetaMorpheusContaminantsGPTMD.xml" }));
            Assert.That(gptmdOutputs.All(o => o!["sha256"]!.GetValue<string>().Length == 64));
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

    // v0.4.28 (PXD006815, a MED-FASP chymotrypsin fraction): a custom protease named per file reaches MetaMorpheus. Its
    // entry is installed into MetaMorpheus's settings folder before the search, and the provenance records it.
    [Test]
    public async Task ACustomProteaseNamedPerFileIsInstalledBeforeTheSearchAndRecorded()
    {
        CustomProteases.Load(Path.Combine(TestSupport.ProfilesDir, "lists", CustomProteases.FileName));
        var rig = Setup(library: false, depositChemistry: true);
        // The fake MetaMorpheus does not seed a settings folder as the real one does (-g --mmsettings): one is made here.
        Directory.CreateDirectory(Path.Combine(rig.Root, "mm_settings", "1.1.11", "Mods"));
        var perFile = new Dictionary<string, string> { ["a.raw"] = "trypsin", ["b.raw"] = CustomProteases.TrypsinChymotrypsin };
        var o = await SearchStage.RunAsync(Req(rig, "04_search") with { Chemistry = Nem(perFile) }, CancellationToken.None);
        Assert.That(o.Success, Is.True);
        Assert.That(File.ReadAllText(Path.Combine(rig.Spectra, "b.toml")), Does.Contain($"DigestionAgent = \"{CustomProteases.TrypsinChymotrypsin}\""));

        var prov = JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!;
        string installed = prov["inputs"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).Single(p => p.EndsWith(CustomProteases.MetaMorpheusFileName));
        // Provenance records an input under the work root relative to it.
        Assert.That(File.ReadAllText(Path.Combine(rig.Root, installed)), Does.Contain("trypsin+chymotrypsin\tK|,R|,F[P]|,W[P]|,Y[P]|,L[P]|\tfull"));
        static IEnumerable<string> Notes(JsonNode p) => p["notes"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.That(Notes(prov), Has.Some.StartsWith($"custom protease(s) {CustomProteases.TrypsinChymotrypsin} from {CustomProteases.FileName}"));

        // A search naming no custom protease installs nothing.
        var plain = await SearchStage.RunAsync(Req(rig, "04_search_2") with { Chemistry = Nem() }, CancellationToken.None);
        Assert.That(Notes(JsonNode.Parse(File.ReadAllText(plain.ProvenanceFile))!), Has.None.Contains("custom protease"));
    }

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

    // v0.4.26 (the user, 2026-10-19: "explore using metamorpheus master instead of the release"): a build of master says
    // "Not a release version." where a release prints its number, so a commit pin is checked by the commit --help names.
    [Test]
    public async Task ACommitPinnedBuildIsCheckedByItsCommitAndRecordsNoRelease()
    {
        var rig = Setup(library: false, mm: "master@0123456789a");
        Environment.SetEnvironmentVariable("FAKE_MM_RELEASE", "Not a release version.");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That(o.Success, Is.True);
            var tool = JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!["tools"]!["MetaMorpheus"]!;
            Assert.Multiple(() =>
            {
                Assert.That(tool["release"], Is.Null, "a commit build is no release");
                Assert.That(tool["commit"]!.GetValue<string>(), Is.EqualTo("0123456789abcdef0123456789abcdef01234567"));
                Assert.That(tool["expected_release"]!.GetValue<string>(), Is.EqualTo("master@0123456789a"));
            });
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_RELEASE", null); }
    }

    [TestCase("master@fedcba9876", null, "built from commit 0123456789abcdef")]
    [TestCase("1.1.11", "Not a release version.", "MetaMorpheus is unknown, test-dda@1 expects 1.1.11")]
    public void ABuildOtherThanTheOnePinnedIsRefused(string mm, string? banner, string message)
    {
        var rig = Setup(library: false, mm: mm);
        Environment.SetEnvironmentVariable("FAKE_MM_RELEASE", banner);
        try
        {
            Assert.That(async () => await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None),
                Throws.TypeOf<SearchSetupException>().With.Message.Contains(message));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_RELEASE", null); }
    }

    [TestCase("1.1.12", true)]
    [TestCase("master@3a6a766", false)]
    [TestCase("pxr-integration@d49de5f87281bfb7418d46a9f7f260b39fab538a", false)]
    public void APinNamesAReleaseOrACommit(string value, bool release)
    {
        var pin = MetaMorpheusPin.Parse(value);
        Assert.That((pin.Name, pin.IsRelease), Is.EqualTo((value, release)));
    }

    [TestCase("master")]
    [TestCase("master@3a6a76")]       // fewer than 7 hex digits
    [TestCase("master@3A6A76632")]    // git prints commits in lower case
    [TestCase("@3a6a76632")]
    [TestCase("1.1.12+3a6a76632")]
    public void AValueThatNamesNoBuildIsRefusedWhenTheProfileLoads(string value)
    {
        Assert.That(MetaMorpheusPin.TryParse(value, out _), Is.False);
        string root = TestSupport.TempDir();
        string file = TestSupport.WriteFile(root, "profile.toml", $"id = \"x\"\nversion = 1\n[accepts]\n[engine]\nmetamorpheus = \"{value}\"\ntasks = [\"Search\"]\n");
        Assert.That(() => ProfileLoader.Load(file), Throws.TypeOf<ConfigException>().With.Message.Contains("commit pin"));
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

    // QuantProject 049 (PXR-T8): MetaMorpheus 1.1.12 (fix #2784, unmerged) drops label-free quantification in
    // Calibrate -> GPTMD -> Search when a file fails calibration and its name is inside another file's (A1 and A10). Its
    // only sign is this warning; AllQuantifiedProteinGroups.tsv is still written. A search that logs it is never delivered.
    [Test]
    public async Task TheCalibrationNameCollisionThatSkipsQuantificationFailsTheSearch()
    {
        var rig = Setup(library: false);
        TestSupport.WriteFile(rig.Spectra, "ExperimentalDesign.tsv", "FileName\tCondition\tBiorep\tFraction\tTechrep\n");
        Environment.SetEnvironmentVariable("FAKE_MM_WARN",
            "Error reading experimental design file: Experimental design file did not contain the file(s): 20160312_02_A1-calib.mzML. Skipping quantification");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search"), CancellationToken.None);
            Assert.That(o.Success, Is.False);
            Assert.That(o.Flags, Has.Some.StartsWith("quantification_skipped"));
            Assert.That(JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!["notes"]!.AsArray().Select(n => n!.GetValue<string>()),
                Has.Some.Contains("FAILED (D48)").And.Some.Contains("A1-calib.mzML"));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_WARN", null); }
    }

    // QuantProject U15: MetaMorpheus can drop an invalid design after one Warn and quantify design-less, with no marker.
    [Test]
    public async Task ADesignQuantificationSilentlyIgnoredFailsTheSearch()
    {
        var rig = Setup(library: false);
        var names = Directory.GetFiles(rig.Spectra, "*.raw").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
        TestSupport.WriteFile(rig.Spectra, "ExperimentalDesign.tsv", "FileName\tCondition\tBiorep\tFraction\tTechrep\n"
            + string.Concat(names.Select((n, i) => $"{n}\t{(i % 2 == 0 ? "young" : "old")}\t{i / 2 + 1}\t1\t1\n")));
        var used = await SearchStage.RunAsync(Req(rig, "04_search_used"), CancellationToken.None);
        Assert.That(used.Success, Is.True, "a design the search used passes");

        Environment.SetEnvironmentVariable("FAKE_MM_IGNORE_DESIGN", "1");
        try
        {
            var o = await SearchStage.RunAsync(Req(rig, "04_search_ignored"), CancellationToken.None);
            Assert.That(o.Success, Is.False);
            string prov = File.ReadAllText(Path.Combine(rig.Root, "run", "04_search_ignored", "provenance.json"));
            Assert.That(prov, Does.Contain("QuantProject U15"));
        }
        finally { Environment.SetEnvironmentVariable("FAKE_MM_IGNORE_DESIGN", null); }
    }

    // v0.4.27 (dataRepo 052, PXR-R28): seven of aging's question SDRFs were edited after their searches, and no search
    // folder kept the bytes it used. The search now keeps a copy, and its record names the copy.
    [Test]
    public async Task TheSearchKeepsACopyOfTheSdrfItsDesignCameFrom()
    {
        var rig = Setup(library: false, design: true);
        string curated = TestSupport.WriteFile(TestSupport.TempDir(), "PXD000301.sdrf.tsv",
            "source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tassay name\tcomment[label]\tcomment[data file]\t"
            + "comment[technical replicate]\tcomment[fraction identifier]\tfactor value[age group]\n"
            + "young 1\thomo sapiens\t1\trun 1\tlabel free sample\ta.raw\t1\t1\tyoung\n"
            + "old 1\thomo sapiens\t1\trun 2\tlabel free sample\tb.raw\t1\t1\told\n");
        var o = await SearchStage.RunAsync(Req(rig, "04_search") with { CuratedDesign = curated }, CancellationToken.None);
        Assert.That(o.Success, Is.True);
        File.AppendAllText(curated, "edited after the search\n");

        var design = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(o.ProvenanceFile))!["experimental_design"]!;
        string kept = design["sdrf"]!.GetValue<string>();
        Assert.Multiple(() =>
        {
            Assert.That(design["source"]!.GetValue<string>(), Is.EqualTo("question"));
            Assert.That(kept, Is.EqualTo(Path.Combine(rig.Root, "run", "04_search", "design", "PXD000301.sdrf.tsv")));
            Assert.That(design["sdrf_origin"]!.GetValue<string>(), Is.EqualTo(curated));
            Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(kept))),
                Is.EqualTo(design["sdrf_sha256"]!.GetValue<string>()), "the copy is the bytes the search used, though the question's file changed since");
        });
    }

    // aging 043 (PXR-A37): the Jarome NEM deposits ran 0.2-3.2% Cys; whole proteomes 12-26%.
    [Test]
    public void CysteineShareCountsTargetPeptidesAtOnePercent()
    {
        string dir = TestSupport.TempDir();
        string f = TestSupport.WriteFile(dir, "AllPeptides.psmtsv", "Base Sequence\tQValue\tDecoy/Contaminant/Target\n"
            + "PEPCK\t0.001\tT\nPEPTIDEK\t0.001\tT\nPEPTIDER\t0.001\tT\nAAAAK\t0.001\tT\nCCCK\t0.001\tD\nCCCR\t0.5\tT\nCCK\t0.001\tC\n");
        Assert.That(SearchStage.CysteineShare(f), Is.EqualTo(0.25), "1 of 4 target peptides at 1%; decoys, contaminants and q > 1% left out");
        Assert.That(SearchStage.CysteineShare(TestSupport.WriteFile(dir, "old.psmtsv", "QValue\n0.001\n")), Is.Null, "not measurable is null, not 0");
    }

    [Test]
    public void QuantMissesDesignCountsSamplesNotFiles()
    {
        string dir = TestSupport.TempDir();
        string design = TestSupport.WriteFile(dir, "ExperimentalDesign.tsv", "FileName\tCondition\tBiorep\tFraction\tTechrep\n"
            + "a1\tyoung\t1\t1\t1\na2\tyoung\t1\t2\t1\nb1\told\t1\t1\t1\nb2\told\t2\t1\t1\n");
        string ok = TestSupport.WriteFile(dir, "ok.tsv", "Protein Accession\tIntensity_young_1\tIntensity_old_1\tIntensity_old_2\n");
        string perFile = TestSupport.WriteFile(dir, "files.tsv", "Protein Accession\tIntensity_a1\tIntensity_a2\tIntensity_b1\tIntensity_b2\n");
        Assert.That(SearchStage.QuantMissesDesign(design, ok), Is.Null, "two fractions of one sample are one column");
        Assert.That(SearchStage.QuantMissesDesign(design, perFile), Does.Contain("4 Intensity_ columns for 3 design samples"));
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
