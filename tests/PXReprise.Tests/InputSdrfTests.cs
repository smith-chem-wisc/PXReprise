using System.Text.Json.Nodes;
using PXReprise.Config;
using PXReprise.Provenance;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// D84 (the user, 2026-11-21: "we should see output sdrf. if that depends on input sdrf then we should begin that"): under
/// <c>engine.input_sdrf</c> the SDRF a search's design came from is MetaMorpheus's <c>InputSdrfPath</c> (1.1.14+, #2934),
/// so the SDRF the search writes copies its sample columns. On PXD019690-astro the written SDRF read "not available" for
/// cell type, disease and organism part, which the curated SDRF states.
/// </summary>
public class InputSdrfTests
{
    private const string Pin = "metamorpheus = \"1.1.13\"";
    private const string WriteSdrf = "write_sdrf = true";

    private static string ProfileWith(Func<string, string> edit)
    {
        string text = edit(File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-12.toml")));
        string dir = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "lists"));
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(dir, "lists", Path.GetFileName(f)));
        return TestSupport.WriteFile(dir, "p.toml", text);
    }

    [TestCase("1.1.14")]
    [TestCase("1.1.15")]
    [TestCase("master@0da8edeaf")]
    public void TheLoaderReadsTheKeyOnAMetaMorpheusThatHasTheSetting(string pin)
    {
        var p = ProfileLoader.Load(ProfileWith(t => t.Replace(Pin, $"metamorpheus = \"{pin}\"").Replace(WriteSdrf, WriteSdrf + "\ninput_sdrf = true")));
        Assert.That(p.InputSdrf, Is.True);
        Assert.That(ProfileLoader.Load(ProfileWith(t => t)).InputSdrf, Is.False, "off unless named");
    }

    [Test]
    public void TheKeyIsRefusedWithoutAWrittenSdrfOrOnAnOlderMetaMorpheus()
    {
        var noWrite = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(t => t
            .Replace(Pin, "metamorpheus = \"1.1.15\"").Replace(WriteSdrf, "write_sdrf = false\ninput_sdrf = true"))));
        Assert.That(noWrite!.Message, Does.Contain("input_sdrf needs engine.write_sdrf"));
        var old = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(t => t.Replace(WriteSdrf, WriteSdrf + "\ninput_sdrf = true"))));
        Assert.That(old!.Message, Does.Contain("needs MetaMorpheus 1.1.14 or later").And.Contain("1.1.13"));
    }

    /// <summary>The engine writes InputSdrfPath from engine.input_sdrf, so a settings table may not write it too.</summary>
    [Test]
    public void ASettingsTableMayNotNameInputSdrfPath()
    {
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(ProfileWith(t => t
            .Replace(Pin, "metamorpheus = \"1.1.15\"")
            .Replace("[metamorpheus_settings.search]", "[metamorpheus_settings.search]\nInputSdrfPath = \"/data/design.sdrf.tsv\""))));
        Assert.That(e!.Message, Does.Contain("'InputSdrfPath' is written by the engine"));
    }

    private static (AgingProvenance Prov, string Dir) Prov()
    {
        string dir = TestSupport.TempDir();
        return (new AgingProvenance("search_metamorpheus", dir, TestSupport.WriteFile(dir, "p.json", "{}"), new JsonObject(), null), dir);
    }

    private static Profile Profile(bool inputSdrf) =>
        ProfileLoader.Load(ProfileWith(t => inputSdrf ? t.Replace(Pin, "metamorpheus = \"1.1.15\"").Replace(WriteSdrf, WriteSdrf + "\ninput_sdrf = true") : t));

    [Test]
    public void TheSearchPassesItsOwnCopyOfTheDesignsSdrfAsAFullPath()
    {
        var (prov, dir) = Prov();
        Directory.CreateDirectory(Path.Combine(dir, "design"));
        string kept = TestSupport.WriteFile(Path.Combine(dir, "design"), "PXD019690-astro.sdrf.tsv", "source name\n");
        var design = new JsonObject { ["written"] = true, ["sdrf"] = kept };

        string? passed = SearchStage.InputSdrfFor(Profile(inputSdrf: true), design, prov);
        Assert.That(passed, Is.EqualTo(Path.GetFullPath(kept)));
        Assert.That(Path.IsPathFullyQualified(passed!), Is.True);
        Assert.That(prov.Record["input_sdrf"]!.GetValue<string>(), Is.EqualTo(passed), "provenance names the SDRF given");

        var (other, _) = Prov();
        Assert.That(SearchStage.InputSdrfFor(Profile(inputSdrf: false), design, other), Is.Null, "a profile without the key passes none");
        Assert.That(other.Record.ContainsKey("input_sdrf"), Is.False, "and says nothing about it");
    }

    [Test]
    public void ASearchWithNoSdrfDesignPassesNoneAndSaysSo()
    {
        var p = Profile(inputSdrf: true);
        foreach (var design in new JsonObject?[] { null, new() { ["written"] = false }, new() { ["written"] = true, ["sdrf"] = "/nowhere/x.sdrf.tsv" } })
        {
            var (prov, _) = Prov();
            Assert.That(SearchStage.InputSdrfFor(p, design, prov), Is.Null);
            Assert.That(prov.Record.ContainsKey("input_sdrf") && prov.Record["input_sdrf"] is null, Is.True, "recorded as none");
            Assert.That(prov.Record["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.Contains("has no SDRF design"));
        }
    }
}
