using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.33: a profile's <c>[metamorpheus_settings.&lt;scope&gt;]</c> writes MetaMorpheus task-file keys no other profile key
/// names (MetaMorpheus master, 2026-10-28: RespectCleavageBlockingModifications in every task,
/// AddAlternativeSitesForDatabaseMods in GPTMD, and WriteIndividualFiles, whose default turned false).
/// </summary>
public class MetaMorpheusSettingsTests
{
    private static string ProfileWith(string extra)
    {
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-11.toml")) + "\n" + extra + "\n";
        string dir = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "lists"));
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(dir, "lists", Path.GetFileName(f)));
        return TestSupport.WriteFile(dir, "p.toml", text);
    }

    [Test]
    public void SettingsAreReadInOrderAsTomlLiterals()
    {
        var p = ProfileLoader.Load(ProfileWith(
            "[metamorpheus_settings.all]\nRespectCleavageBlockingModifications = true\n" +
            "[metamorpheus_settings.search]\nWriteIndividualFiles = true\nQValueThreshold = 0.01\nRTPredictorName = \"Chronologer\"\n"));
        Assert.That(p.MetaMorpheusSettings, Is.EqualTo(new[]
        {
            new MetaMorpheusSetting("all", "RespectCleavageBlockingModifications", "true"),
            new MetaMorpheusSetting("search", "WriteIndividualFiles", "true"),
            new MetaMorpheusSetting("search", "QValueThreshold", "0.01"),
            new MetaMorpheusSetting("search", "RTPredictorName", "\"Chronologer\""),
        }));
        Assert.That(ProfileLoader.Load(ProfileWith("")).MetaMorpheusSettings, Is.Null, "no table: nothing set");
    }

    [TestCase("[metamorpheus_settings.everything]\nX = true", "scope must be one of")]
    [TestCase("[metamorpheus_settings.search]\nProtease = \"trypsin\"", "written by the engine")]
    [TestCase("[metamorpheus_settings.search]\nMaxThreadsToUsePerFile = 4", "written by the engine")]
    [TestCase("[metamorpheus_settings.all]\nA = true\n[metamorpheus_settings.gptmd]\nA = false", "twice")]
    [TestCase("[metamorpheus_settings.search]\nA = [1, 2]", "must be true, false, a number or a string")]
    [TestCase("[metamorpheus_settings.search]\n\"Bad Key\" = true", "not a MetaMorpheus task-file key")]
    public void AWrongSettingIsRefusedAtLoad(string extra, string expected)
    {
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(extra)));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void AScopeForATaskTheProfileDoesNotRunIsRefused()
    {
        string file = ProfileWith("[metamorpheus_settings.gptmd]\nAddAlternativeSitesForDatabaseMods = true");
        File.WriteAllText(file, File.ReadAllText(file).Replace("tasks = [\"Calibration\", \"Gptmd\", \"Search\"]", "tasks = [\"Calibration\", \"Search\"]")
            .Replace("gptmd_extra_mods = [\"Trypsin Digested\\tGG (Ubiquitination Site) on K\"]\n", "")
            .Replace("gptmd_filters = [\"ImprovedScoreFilter\"]", "")
            .Replace("[chemistry_artifacts]\n", "[x]\n"));
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(file));
        Assert.That(e!.Message, Does.Contain("names a task engine.tasks does not run"));
    }

    [Test]
    public void Lowres6IsLowres5OnTheNewMetaMorpheusWithTheSameDatabases()
    {
        // v0.4.33: nothing runs 1.1.12 after the engine moves to mzLib 1.0.596 (#1426 keeps SDRF replicate gaps, which
        // 1.1.12 refuses). The ion-trap profile keeps agingPTM-v3 (D44: v4's sites cost an ion-trap run 4% of its PSMs).
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var p6 = all["label-free-dda-lowres@6"];
        var p5 = all["label-free-dda-lowres@5"];
        Assert.That(p6.MetaMorpheus, Is.EqualTo(all["label-free-dda@12"].MetaMorpheus));
        Assert.That(p6.MetaMorpheusSettings, Is.EqualTo(all["label-free-dda@12"].MetaMorpheusSettings));
        Assert.That(p6.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(p5.Databases.Select(kv => (kv.Key, kv.Value.Proteome))));
        Assert.That(p6 with { Version = 5, Description = p5.Description, MetaMorpheus = p5.MetaMorpheus, MetaMorpheusSettings = null,
            Databases = p5.Databases, Accepts = p5.Accepts, Tasks = p5.Tasks, GptmdExtraMods = p5.GptmdExtraMods, Deposit = p5.Deposit, Qc = p5.Qc },
            Is.EqualTo(p5), "every other scalar setting of lowres@6 equals lowres@5's");
        Assert.That((p6.Tasks, p6.Deposit), Is.EqualTo((p5.Tasks, p5.Deposit)));
    }

    [Test]
    public void ApplySettingsWritesTheTaskScopeAndAllWhereTheKeyExists()
    {
        // MetaMorpheus master's defaults (2026-10-28): the flag in every task's DigestionParams; WriteIndividualFiles in
        // Search only; CRLF as written on Windows.
        const string search = "WriteIndividualFiles = false\r\nRespectCleavageBlockingModifications = false\r\n";
        const string calibration = "RespectCleavageBlockingModifications = false\r\n";
        var s = new[]
        {
            new MetaMorpheusSetting("all", "RespectCleavageBlockingModifications", "true"),
            new MetaMorpheusSetting("search", "WriteIndividualFiles", "true"),
        };
        var (st, sa) = TaskFiles.ApplySettings(search, TaskKind.Search, s);
        Assert.That(st, Is.EqualTo("WriteIndividualFiles = true\r\nRespectCleavageBlockingModifications = true\r\n"));
        Assert.That(sa, Is.EqualTo(s));
        var (ct, ca) = TaskFiles.ApplySettings(calibration, TaskKind.Calibration, s);
        Assert.That(ct, Is.EqualTo("RespectCleavageBlockingModifications = true\r\n"));
        Assert.That(ca, Is.EqualTo(new[] { s[0] }), "the search-scoped key is not looked for in calibration");
        Assert.That(TaskFiles.ApplySettings(calibration, TaskKind.Calibration, null).Text, Is.EqualTo(calibration));
    }

    [Test]
    public void ATaskScopedKeyThePinnedEngineLacksIsRefusedNotSkipped()
    {
        // 1.1.12 has no RespectCleavageBlockingModifications: a scoped key must find its line.
        var e = Assert.Throws<SearchSetupException>(() => TaskFiles.ApplySettings("WriteIndividualFiles = true\n", TaskKind.Gptmd,
            new[] { new MetaMorpheusSetting("gptmd", "AddAlternativeSitesForDatabaseMods", "true") }));
        Assert.That(e!.Message, Does.Contain("AddAlternativeSitesForDatabaseMods"));
        // An "all" key absent from one task is skipped there; the search stage refuses it if no task had it.
        var (_, applied) = TaskFiles.ApplySettings("WriteIndividualFiles = true\n", TaskKind.Search,
            new[] { new MetaMorpheusSetting("all", "RespectCleavageBlockingModifications", "true") });
        Assert.That(applied, Is.Empty);
    }
}
