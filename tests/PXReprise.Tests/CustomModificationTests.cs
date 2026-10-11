using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// G40 (aging 072 PXR-A69, our 074): a profile ships its own MetaMorpheus modification file for what no UniMod entry
/// describes (NBF), maps a thiol reagent to it, and the engine installs the file before a search that uses it.
/// </summary>
public class CustomModificationTests
{
    private const string Nbf = "PXReprise\tNBF on C";

    private static Profile V6 => ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@6"];

    /// <summary>A copy of the shipped @6 and its lists in a scratch folder, with <paramref name="edit"/> applied.</summary>
    private static string ProfileCopy(Func<string, string> edit, string? modsText = null)
    {
        string dir = TestSupport.TempDir();
        string lists = Path.Combine(dir, "lists");
        Directory.CreateDirectory(lists);
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(lists, Path.GetFileName(f)));
        if (modsText is not null) File.WriteAllText(Path.Combine(lists, "PXReprise_custom_modifications_v1.txt"), modsText);
        return TestSupport.WriteFile(dir, "p.toml", edit(File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-6.toml"))));
    }

    [Test]
    public void TheShippedProfileModelsNbfOnCysteineFromItsOwnFile()
    {
        var p = V6;
        Assert.That(p.CustomModifications, Is.EqualTo("PXReprise_custom_modifications_v1.txt"));
        Assert.That(p.ThiolReagents, Is.EqualTo(new Dictionary<string, string> { ["NBF-Cl"] = Nbf }));
        // mzLib reads the shipped file as MetaMorpheus will: one modification, NBF on C, +163.00179 Da.
        var mods = Omics.Modifications.IO.ModificationLoader.ReadModsFromFile(p.ListPath(p.CustomModifications!), out var warnings).ToList();
        Assert.That(warnings, Is.Empty);
        Assert.That(mods.Select(m => $"{m.ModificationType}\t{m.IdWithMotif}"), Is.EqualTo(new[] { Nbf }));
        Assert.That(mods.Single().MonoisotopicMass, Is.EqualTo(163.00179).Within(0.00001));
    }

    private static string LoadError(Func<string, string> edit)
    {
        string path = ProfileCopy(edit);
        var ex = Assert.Throws<ConfigException>(() => ProfileLoader.Load(path));
        return ex!.Message;
    }

    [Test]
    public void AReagentTheChemistryDoesNotReadIsRefused()
    {
        string message = LoadError(t => t.Replace("\"NBF-Cl\" =", "\"NBD-F\" ="));
        Assert.That(message, Does.Contain("not a reagent the chemistry reads"));
    }

    [Test]
    public void AModificationTheFileDoesNotDefineIsRefused()
    {
        string message = LoadError(t => t.Replace(@"PXReprise\tNBF on C", @"PXReprise\tNBF on K"));
        Assert.That(message, Does.Contain("does not define"));
    }

    [Test]
    public void AMappingNeedsTheCustomFile()
    {
        string message = LoadError(t => string.Join("\n", t.Split('\n').Where(l => !l.StartsWith("custom_modifications", StringComparison.Ordinal))));
        Assert.That(message, Does.Contain("custom_modifications"));
    }

    [Test]
    public void AFileWithoutAModificationIsRefused() =>
        Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileCopy(t => t, "PXReprise custom modifications\n#### nothing here\n")));

    [Test]
    public void TheFileIsInstalledIntoMetaMorpheussModsFolderAndRecognisedThere()
    {
        string settings = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(settings, "Mods"));
        bool seeded = false;
        string installed = CustomModificationFile.Install(V6, settings, new[] { Nbf }, () => seeded = true);
        Assert.That(installed, Is.EqualTo(Path.Combine(settings, "Mods", "PXReprise_custom_modifications_v1.txt")));
        Assert.That(seeded, Is.False, "an existing settings folder is never seeded again");
        Assert.That(TaskFiles.KnownMods(Path.Combine(settings, "Mods")), Does.Contain(("PXReprise", "NBF on C")));
        Assert.That(Directory.GetFiles(settings), Is.Empty, "no staging file is left beside Mods");

        Assert.That(CustomModificationFile.Install(V6, settings, new[] { Nbf }, () => { }), Is.EqualTo(installed), "an identical file is accepted");
        File.AppendAllText(installed, "ID   Extra\n");
        Assert.That(Assert.Throws<SearchSetupException>(() => CustomModificationFile.Install(V6, settings, new[] { Nbf }, () => { }))!.Message,
            Does.Contain("new file name"), "a changed file of that name is refused, never overwritten");
    }

    [Test]
    public void AMissingSettingsFolderIsSeededByMetaMorpheusFirst()
    {
        string settings = Path.Combine(TestSupport.TempDir(), "1.1.12");
        CustomModificationFile.Install(V6, settings, new[] { Nbf }, () => Directory.CreateDirectory(Path.Combine(settings, "Mods")));
        Assert.That(File.Exists(Path.Combine(settings, "Mods", "PXReprise_custom_modifications_v1.txt")));

        string unseeded = Path.Combine(TestSupport.TempDir(), "1.1.12");
        Assert.Throws<SearchSetupException>(() => CustomModificationFile.Install(V6, unseeded, new[] { Nbf }, () => { }),
            "a seed that did not create Mods is refused, never papered over");
        Assert.That(Directory.Exists(unseeded), Is.False, "the settings folder is never created by hand: an empty one crashes MetaMorpheus");
    }

    [Test]
    public void AModificationTheProfileDoesNotShipIsRefused()
    {
        string settings = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(settings, "Mods"));
        var v5 = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@5"];
        Assert.Throws<SearchSetupException>(() => CustomModificationFile.Install(v5, settings, new[] { Nbf }, () => { }));
        Assert.That(Assert.Throws<SearchSetupException>(() => CustomModificationFile.Install(V6, settings, new[] { "PXReprise\tNBF on K" }, () => { }))!.Message,
            Does.Contain("read under another profile"));
    }
}
