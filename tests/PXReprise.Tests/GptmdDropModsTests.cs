using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.38 (results/acyl-gptmd_2026-11-03): <c>engine.gptmd_drop_mods</c> takes entries out of MetaMorpheus's default
/// GPTMD list, the mirror of <c>gptmd_extra_mods</c>. A drop the list does not hold is refused, never skipped.
/// </summary>
public class GptmdDropModsTests
{
    private static readonly IReadOnlySet<(string, string)> Known = new HashSet<(string, string)>
    {
        ("Common Biological", "Acetylation on K"), ("Common Biological", "Malonylation on K"), ("Metal", "Sodium on E"),
        ("Trypsin Digested", "GG (Ubiquitination Site) on K"),
    };

    private const string Gptmd =
        "ListOfModsGptmd = \"Common Biological\\tAcetylation on K\\t\\tCommon Biological\\tMalonylation on K\\t\\tMetal\\tSodium on E\"\n" +
        "MaxThreadsToUsePerFile = 8\n";

    private static TaskSettings Drop(params string[] drop) => new(8, false, Array.Empty<string>(), GptmdDropMods: drop);

    [TestCase("Common Biological\tMalonylation on K", "Common Biological\\tAcetylation on K\\t\\tMetal\\tSodium on E")]
    [TestCase("Common Biological\tAcetylation on K", "Common Biological\\tMalonylation on K\\t\\tMetal\\tSodium on E")]
    [TestCase("Metal\tSodium on E", "Common Biological\\tAcetylation on K\\t\\tCommon Biological\\tMalonylation on K")]
    public void ADroppedEntryLeavesTheListAndEveryOtherEntryKeepsItsPlace(string drop, string expectedList)
    {
        var (text, _) = TaskFiles.Edit(Gptmd, TaskKind.Gptmd, Drop(drop), Known);
        Assert.That(text, Is.EqualTo($"ListOfModsGptmd = \"{expectedList}\"\nMaxThreadsToUsePerFile = 8\n"));
    }

    [Test]
    public void ADropTheListDoesNotHoldIsRefused()
    {
        // A misspelt drop would leave the class searched while the profile and provenance say it is not.
        var e = Assert.Throws<SearchSetupException>(() => TaskFiles.Edit(Gptmd, TaskKind.Gptmd, Drop("Common Biological\tMalonylation on R"), Known));
        Assert.That(e!.Message, Does.Contain("Malonylation on R").And.Contain("cannot be dropped"));
        Assert.Throws<SearchSetupException>(() => TaskFiles.Edit(Gptmd, TaskKind.Gptmd, Drop("Less Common\tMalonylation on K"), Known),
            "the category is part of the entry");
    }

    [Test]
    public void DropAndAddWorkTogetherAndOnlyOnTheGptmdTask()
    {
        var s = new TaskSettings(8, false, new[] { "Trypsin Digested\tGG (Ubiquitination Site) on K" },
            GptmdDropMods: new[] { "Common Biological\tMalonylation on K" });
        var (text, added) = TaskFiles.Edit(Gptmd.Replace("\n", "\r\n"), TaskKind.Gptmd, s, Known);
        Assert.That(text, Is.EqualTo("ListOfModsGptmd = \"Common Biological\\tAcetylation on K\\t\\tMetal\\tSodium on E\\t\\t" +
            "Trypsin Digested\\tGG (Ubiquitination Site) on K\"\r\nMaxThreadsToUsePerFile = 8\r\n"), "CRLF kept");
        Assert.That(added, Has.Count.EqualTo(1));
        var search = "ListOfModsGptmd = \"Common Biological\\tMalonylation on K\"\nMaxThreadsToUsePerFile = 8\n";
        Assert.That(TaskFiles.Edit(search, TaskKind.Search, Drop("Common Biological\tMalonylation on K"), Known).Text, Is.EqualTo(search),
            "the Search task is left alone");
        Assert.That(TaskFiles.Edit(Gptmd, TaskKind.Gptmd, new TaskSettings(8, false, Array.Empty<string>()), Known).Text, Is.EqualTo(Gptmd),
            "no drop: unchanged");
    }

    private static string ProfileWith(Func<string, string> edit)
    {
        string text = edit(File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-12.toml")));
        string dir = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "lists"));
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(dir, "lists", Path.GetFileName(f)));
        return TestSupport.WriteFile(dir, "p.toml", text);
    }

    private const string FiltersLine = "gptmd_filters = [\"ImprovedScoreFilter\"]";

    [Test]
    public void TheLoaderReadsTheDropsWithARealTab()
    {
        var p = ProfileLoader.Load(ProfileWith(t => t.Replace(FiltersLine,
            FiltersLine + "\ngptmd_drop_mods = [\"Common Biological\\tMalonylation on K\"]")));
        Assert.That(p.GptmdDropMods, Is.EqualTo(new[] { "Common Biological\tMalonylation on K" }));
        Assert.That(ProfileLoader.LoadDirectory(TestSupport.ProfilesDir).Where(kv => kv.Value.GptmdDropMods is not null).Select(kv => kv.Key),
            Is.EquivalentTo(new[] { "label-free-dda@13", "label-free-dda@14" }), "only @13 and @14 drop anything (a profile never changes)");
    }

    [TestCase("gptmd_drop_mods = [\"Malonylation on K\"]", "is not")]
    [TestCase("gptmd_drop_mods = [\"Trypsin Digested\\tGG (Ubiquitination Site) on K\"]", "both name")]
    public void AWrongDropIsRefusedAtLoad(string line, string expected)
    {
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(t => t.Replace(FiltersLine, FiltersLine + "\n" + line))));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void ADropWithoutAGptmdTaskIsRefused()
    {
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(t => t
            .Replace("tasks = [\"Calibration\", \"Gptmd\", \"Search\"]", "tasks = [\"Calibration\", \"Search\"]")
            .Replace("gptmd_extra_mods = [\"Trypsin Digested\\tGG (Ubiquitination Site) on K\"]\n", "")
            .Replace(FiltersLine, "gptmd_drop_mods = [\"Common Biological\\tMalonylation on K\"]")
            .Replace("[chemistry_artifacts]\n", "[x]\n"))));
        Assert.That(e!.Message, Does.Contain("gptmd_drop_mods needs a Gptmd task"));
    }
}
