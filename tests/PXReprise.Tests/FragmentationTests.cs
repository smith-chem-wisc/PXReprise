using System.Text.Json.Nodes;
using PXReprise.Provenance;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// D32 (G24): each file's fragmentation is read from its own MS2 headers and written into its &lt;raw&gt;.toml, never
/// taken from what the depositor wrote. The rules come from the oracle of 2026-10-07 on MetaMorpheus 1.1.12 / mzLib 1.0.594.
/// </summary>
public class FragmentationTests
{
    private static Dictionary<string, int> Pairs(params (string Pair, int N)[] p) => p.ToDictionary(x => x.Pair, x => x.N);

    [TestCase("Orbitrap/HCD", "HCD", false)]
    [TestCase("Orbitrap/CID", "CID", false)]              // beam or trap CID read in the Orbitrap: high resolution
    [TestCase("Orbitrap/ETD", "ETD", false)]
    [TestCase("Orbitrap/EThcD", "EThcD", false)]
    [TestCase("FTICR/HCD", "HCD", false)]                 // mzLib reads Thermo FTICR as FT: high resolution
    [TestCase("IonTrap2D/CID", "LowCID", true)]           // mzLib never reports LowCID: the analyzer decides
    [TestCase("IonTrap3D/CID", "LowCID", true)]
    public void OneTypeIsWrittenAsItIs(string pair, string expected, bool lowRes)
    {
        var c = Fragmentation.Decide(Pairs((pair, 5000)));
        Assert.That((c.DissociationType, c.LowRes, c.Unsupported), Is.EqualTo((expected, lowRes, (string?)null)));
    }

    [Test]
    public void SeveralHighResolutionTypesAreAutodetect()
    {
        var c = Fragmentation.Decide(Pairs(("Orbitrap/HCD", 3000), ("Orbitrap/EThcD", 2000)));
        Assert.That((c.DissociationType, c.Unsupported), Is.EqualTo(("Autodetect", (string?)null)));
    }

    [TestCase("Orbitrap/UVPD")]    // MetaMorpheus 1.1.12 silently skips it under Autodetect and has no ions for it
    [TestCase("Orbitrap/None")]    // no fragmentation in the header: Autodetect would crash on .Value
    [TestCase("Orbitrap/Unknown")]
    [TestCase("IonTrap2D/ETD")]    // low-resolution ETD: no MetaMorpheus mode for it
    [TestCase("TOF/CID")]          // timsTOF and Q-TOF: not this profile family
    [TestCase("Unknown/HCD")]      // Astral reads as Unknown in mzLib 1.0.594
    public void AnUnsupportedTypeIsRefusedWithItsReason(string pair)
    {
        var c = Fragmentation.Decide(Pairs((pair, 5000)));
        Assert.That(c.DissociationType, Is.Null);
        Assert.That(c.Unsupported, Does.Contain(pair));
    }

    [Test]
    public void IonTrapAndHighResolutionMixedInOneFileIsRefused()
    {
        var c = Fragmentation.Decide(Pairs(("Orbitrap/HCD", 3000), ("IonTrap2D/CID", 2000)));
        Assert.That((c.DissociationType, c.Unsupported), Is.EqualTo(((string?)null, "ion-trap and high-resolution MS2 are mixed in one file")));
    }

    [Test]
    public void APairUnderOnePercentIsIgnoredAndNamed()
    {
        var c = Fragmentation.Decide(Pairs(("Orbitrap/HCD", 9950), ("IonTrap2D/CID", 50)));
        Assert.That(c.DissociationType, Is.EqualTo("HCD"));
        Assert.That(c.Evidence, Does.Contain("ignored").And.Contain("IonTrap2D/CID 50"));
        Assert.That(Fragmentation.Decide(Pairs(("Orbitrap/HCD", 9900), ("IonTrap2D/CID", 100))).Unsupported, Is.Not.Null, "exactly 1% counts");
    }

    [Test]
    public void NoMs2IsRefused() => Assert.That(Fragmentation.Decide(new Dictionary<string, int>()).Unsupported, Is.EqualTo("no MS2 scans"));

    /// <summary>The keys MetaMorpheus 1.1.12's FileSpecificParameters accepts; any other key makes it drop the whole file.</summary>
    private static readonly HashSet<string> FileSpecificKeys = new(StringComparer.Ordinal)
    {
        "PrecursorMassTolerance", "ProductMassTolerance", "ProductMassTolerance_LowRes", "DigestionAgent", "Protease", "Rnase",
        "MinPeptideLength", "MaxPeptideLength", "MaxMissedCleavages", "MaxModsForPeptide", "SeparationType", "DissociationType",
        "CustomIons", "PrecursorDeconvolutionParameters", "ProductDeconvolutionParameters",
    };

    [Test]
    public void ThePerFileTomlMergesProteaseAndFragmentationWithOnlyAcceptedKeys()
    {
        string dir = TestSupport.TempDir();
        var raws = new[] { "a.raw", "b.raw", "c.raw" }.Select(f => TestSupport.WriteFile(dir, f, "")).ToList();
        var prov = new AgingProvenance("search_metamorpheus", dir, TestSupport.WriteFile(dir, "p.json", "{}"), new JsonObject(), null);
        SearchStage.WritePerFileSettings(raws,
            new Dictionary<string, string> { ["a.raw"] = "Glu-C" },
            new Dictionary<string, FragmentationCall>
            {
                ["a.raw"] = new("HCD", false, null, ""), ["b.raw"] = new("LowCID", true, null, ""), ["c.raw"] = new("Autodetect", false, null, ""),
            },
            "±0.5000 Absolute", prov);
        string a = File.ReadAllText(Path.Combine(dir, "a.toml")), b = File.ReadAllText(Path.Combine(dir, "b.toml"));
        Assert.Multiple(() =>
        {
            Assert.That(a, Does.StartWith(SearchStage.PerFileTomlMarker).And.Contain("DigestionAgent = \"Glu-C\"").And.Contain("DissociationType = \"HCD\""));
            Assert.That(a, Does.Not.Contain("ProductMassTolerance"));
            Assert.That(b, Does.Contain("DissociationType = \"LowCID\"").And.Contain("ProductMassTolerance = \"±0.5000 Absolute\""));
            Assert.That(File.ReadAllText(Path.Combine(dir, "c.toml")), Does.Contain("DissociationType = \"Autodetect\""));
            foreach (string f in new[] { "a.toml", "b.toml", "c.toml" })
                Assert.That(File.ReadAllLines(Path.Combine(dir, f)).Skip(1).Select(l => l.Split(" = ")[0]), Is.SubsetOf(FileSpecificKeys), f);
        });
    }

    [Test]
    public void AnUnsupportedFileNeverReachesTheSearch()
    {
        string dir = TestSupport.TempDir();
        var raw = TestSupport.WriteFile(dir, "a.raw", "");
        var prov = new AgingProvenance("search_metamorpheus", dir, TestSupport.WriteFile(dir, "p.json", "{}"), new JsonObject(), null);
        Assert.Throws<SearchSetupException>(() => SearchStage.WritePerFileSettings(new[] { raw }, null,
            new Dictionary<string, FragmentationCall> { ["a.raw"] = new(null, false, "Orbitrap/UVPD in 10 of 10", "") }, "±0.5000 Absolute", prov));
    }

    [Test]
    public void FragmentationIsAProfileSettingAndEveryShippedProfileKeepsItFixed()
    {
        string dir = TestSupport.TempDir();
        string src = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-4.toml")).Replace("version = 4", "version = 99");
        var p = Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml", src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"")));
        Assert.That((p.Fragmentation, p.LowResProductTolerance), Is.EqualTo(("per_file", "±0.5000 Absolute")));
        Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "b.toml", src.Replace("[engine]", "[engine]\nfragmentation = \"guess\""))));
        Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "c.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nlow_res_product_tolerance = \"0.5 Da\""))));
        var shipped = Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(shipped.Where(kv => kv.Key != "label-free-dda-lowres@1").Select(kv => kv.Value.Fragmentation), Has.All.EqualTo("fixed"),
            "no shipped profile changes behaviour (D26): per_file arrives with a new profile");
        Assert.That(shipped["label-free-dda-lowres@1"].Fragmentation, Is.EqualTo("per_file"), "the ion-trap profile is the one that writes it");
    }
}
