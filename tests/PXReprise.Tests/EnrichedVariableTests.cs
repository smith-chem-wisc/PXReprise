using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.40 (G66, D32 slice 4; results/tmtphospho-trial_2026-11-02): under <c>engine.enriched_variable</c> (tmt-dda@3) the
/// PTM classes a deposit enriches are variable in every task, with MaxModsForPeptide 3. Under tmt-dda@2 a TMT phospho
/// deposit was searched with phospho only at the database's annotated sites; the trial's variable arm found 39% more PSMs.
/// </summary>
public class EnrichedVariableTests
{
    private static readonly IReadOnlySet<(string, string)> Known = new HashSet<(string, string)>
    {
        ("Common Biological", "Phosphorylation on S"), ("Common Biological", "Phosphorylation on T"), ("Common Biological", "Phosphorylation on Y"),
        ("Common Biological", "Acetylation on K"), ("Common Biological", "Succinylation on K"), ("Trypsin Digested", "GG (Ubiquitination Site) on K"),
        ("Common Variable", "Oxidation on M"),
    };

    // The lines of MetaMorpheus 1.1.13's CMD -g task files these edits touch (PXD034564's 1_CalibrationTask.toml, 2_SearchTask.toml).
    private const string Task =
        "ListOfModsVariable = \"Common Variable\\tOxidation on M\"\n" +
        "MaxModsForPeptide = 2\n" +
        "MaxThreadsToUsePerFile = 8\n";

    private static TaskSettings Enriched(IReadOnlyList<string> mods, int? maxMods = Profile.EnrichedMaxModsForPeptide) =>
        new(8, false, Array.Empty<string>(), EnrichedVariableMods: mods, MaxModsForPeptide: maxMods);

    [Test]
    public void TmtDda3_IsTmtDda2_WithEnrichedVariable_OnAgingPtmV7()
    {
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Profile p2 = all["tmt-dda@2"], p3 = all["tmt-dda@3"];
        Assert.That(p3.EnrichedVariable, Is.True);
        Assert.That(p3.Status, Is.EqualTo(ProfileStatus.Available));
        Assert.That(all.Where(kv => kv.Value.EnrichedVariable).Select(kv => kv.Key), Is.EquivalentTo(new[] { "tmt-dda@3", "tmt-dda@4" }));
        Assert.That(p3.Databases.Select(kv => (kv.Key, kv.Value.Proteome)),
            Is.EquivalentTo(p2.Databases.Select(kv => (kv.Key, kv.Value.Proteome.Replace("_agingPTM-v6.xml", "_agingPTM-v7.xml")))));
        Assert.That(p3.Databases.Values.Select(d => d.Proteome), Is.EquivalentTo(all["label-free-dda@13"].Databases.Values.Select(d => d.Proteome)),
            "the same databases as label-free-dda@13");
        Assert.That(p3 with { Version = 2, Description = p2.Description, Databases = p2.Databases, EnrichedVariable = false,
            Accepts = p2.Accepts, Tasks = p2.Tasks, GptmdExtraMods = p2.GptmdExtraMods, Deposit = p2.Deposit, Qc = p2.Qc,
            MetaMorpheusSettings = p2.MetaMorpheusSettings }, Is.EqualTo(p2), "nothing else differs");
        Assert.That(System.Text.Json.JsonSerializer.Serialize(p3.Accepts), Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(p2.Accepts)));
        // One setting differs: @3 writes no individual-file tables (the user, 2026-11-05, before @3's release; D63).
        Assert.That(p3.MetaMorpheusSettings, Is.EqualTo(p2.MetaMorpheusSettings!.Select(s =>
            s.Key == "WriteIndividualFiles" ? s with { TomlValue = "false" } : s).ToList()));
    }

    [Test]
    public void EachClassHasItsVariableMods_InClassOrder()
    {
        Assert.That(LibraryRouting.VariableModsFor(new[] { "phospho" }), Is.EqualTo(new[]
            { "Common Biological\tPhosphorylation on S", "Common Biological\tPhosphorylation on T", "Common Biological\tPhosphorylation on Y" }));
        Assert.That(LibraryRouting.VariableModsFor(new[] { "GG", "phospho" }), Has.Count.EqualTo(4).And.Contains("Trypsin Digested\tGG (Ubiquitination Site) on K"));
        Assert.That(LibraryRouting.VariableModsFor(new[] { "GG", "phospho" })[0], Is.EqualTo("Common Biological\tPhosphorylation on S"), "class order, not input order");
        Assert.That(LibraryRouting.VariableModsFor(new[] { "acetylK" }), Is.EqualTo(new[] { "Common Biological\tAcetylation on K" }));
        Assert.That(LibraryRouting.VariableModsFor(new[] { "succinylK" }), Is.EqualTo(new[] { "Common Biological\tSuccinylation on K" }));
        Assert.That(LibraryRouting.VariableModsFor(Array.Empty<string>()), Is.Empty);
        Assert.Throws<ArgumentException>(() => LibraryRouting.VariableModsFor(new[] { "methylK" }), "an unknown class would go unsearched");
        Assert.That(LibraryRouting.Classes.SelectMany(c => c.VariableMods).All(m => Known.Contains((m.Split('\t')[0], m.Split('\t')[1]))),
            "every class's mods are in the pinned Mods.txt's spelling");
    }

    [TestCase(TaskKind.Calibration)]
    [TestCase(TaskKind.Search)]
    public void TheEnrichedClassesAreVariableInEveryTask_WithThreeModsAPeptide(TaskKind kind)
    {
        var (text, _) = TaskFiles.Edit(Task, kind, Enriched(LibraryRouting.VariableModsFor(new[] { "phospho" })), Known);
        Assert.That(text, Is.EqualTo(
            "ListOfModsVariable = \"Common Variable\\tOxidation on M\\t\\tCommon Biological\\tPhosphorylation on S\\t\\t" +
            "Common Biological\\tPhosphorylation on T\\t\\tCommon Biological\\tPhosphorylation on Y\"\n" +
            "MaxModsForPeptide = 3\n" +
            "MaxThreadsToUsePerFile = 8\n"));
    }

    [Test]
    public void WithoutEnrichedMods_TheTaskIsUnchanged_AndCrlfIsKept()
    {
        var (plain, _) = TaskFiles.Edit(Task, TaskKind.Search, new TaskSettings(8, false, Array.Empty<string>()), Known);
        Assert.That(plain, Is.EqualTo(Task));
        var (crlf, _) = TaskFiles.Edit(Task.Replace("\n", "\r\n"), TaskKind.Search, Enriched(new[] { "Common Biological\tAcetylation on K" }), Known);
        Assert.That(crlf, Does.Contain("MaxModsForPeptide = 3\r\n").And.Contain("\\t\\tCommon Biological\\tAcetylation on K\"\r\n"));
        Assert.That(crlf.Replace("\r\n", ""), Does.Not.Contain("\n"), "every line keeps its CRLF");
    }

    [Test]
    public void AModTheMetaMorpheusLacks_OrNoMaxModsLine_IsRefused()
    {
        var e = Assert.Throws<SearchSetupException>(() => TaskFiles.Edit(Task, TaskKind.Search, Enriched(new[] { "Common Biological\tPhosphorylation on H" }), Known));
        Assert.That(e!.Message, Does.Contain("Phosphorylation on H"));
        Assert.Throws<SearchSetupException>(() => TaskFiles.Edit(Task.Replace("MaxModsForPeptide = 2\n", ""), TaskKind.Search, Enriched(Array.Empty<string>()), Known),
            "a MetaMorpheus whose task has no MaxModsForPeptide line is not the one this was written for");
        Assert.Throws<SearchSetupException>(() => TaskFiles.Edit(Task, TaskKind.Search, Enriched(Array.Empty<string>(), maxMods: 0), Known));
    }

    [Test]
    public void AProfileMaySetMaxModsForPeptideOnlyThroughEnrichedVariable()
    {
        // The engine writes MaxModsForPeptide under enriched_variable, so [metamorpheus_settings] may not name it too.
        Assert.That(Profile.EngineOwnedTaskKeys, Does.Contain("MaxModsForPeptide"));
        string dir = TestSupport.TempDir();
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "tmt-dda-3.toml"))
            .Replace("[metamorpheus_settings.search]\n", "[metamorpheus_settings.search]\nMaxModsForPeptide = 4\n")
            .Replace("[metamorpheus_settings.search]\r\n", "[metamorpheus_settings.search]\r\nMaxModsForPeptide = 4\r\n");
        Assert.That(text, Does.Contain("MaxModsForPeptide = 4"));
        Directory.CreateDirectory(Path.Combine(dir, "lists"));
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(dir, "lists", Path.GetFileName(f)));
        Assert.That(ProfileLoader.Load(TestSupport.WriteFile(dir, "unchanged.toml", File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "tmt-dda-3.toml")))).EnrichedVariable,
            Is.True, "the copy loads as shipped");
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(TestSupport.WriteFile(dir, "tmt-dda-3.toml", text)));
        Assert.That(e!.Message, Does.Contain("'MaxModsForPeptide' is written by the engine"));
    }
}
