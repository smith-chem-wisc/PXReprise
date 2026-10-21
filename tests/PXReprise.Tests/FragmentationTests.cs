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

    [TestCase("IonTrap2D/HCD")]    // a Fusion/Lumos/Eclipse "HCD-IT" method: PXD057407 (aging 072, PXR-A68)
    [TestCase("IonTrap3D/HCD")]
    public void HcdReadInAnIonTrapIsLowResHcdOnlyWhenTheProfileTurnsItOn(string pair)
    {
        var on = Fragmentation.Decide(Pairs((pair, 212493)), ionTrapHcd: true);
        Assert.That((on.DissociationType, on.LowRes, on.Unsupported), Is.EqualTo(("HCD", true, (string?)null)));

        var off = Fragmentation.Decide(Pairs((pair, 212493)));
        Assert.That(off.DissociationType, Is.Null, "a profile version without ion_trap_hcd keeps refusing it, as before");
        Assert.That(off.Unsupported, Does.Contain(pair));
    }

    // v0.4.28: under the probe's settings one PXD022393 file gave 107 PSMs as HCD and 2,442 as LowCID; one PXD016430 file
    // 3,300 and 9,173. In the full search LowCID lost 1-16% (2026-10-21): lowres@5 takes it in the probe only.
    [TestCase("IonTrap2D/HCD")]
    [TestCase("IonTrap3D/HCD")]
    public void HcdReadInAnIonTrapIsLowCidUnderTheLowCidSwitch(string pair)
    {
        var c = Fragmentation.Decide(Pairs((pair, 70386)), ionTrapHcd: true, ionTrapHcdLowCid: true);
        Assert.That((c.DissociationType, c.LowRes, c.Unsupported), Is.EqualTo(("LowCID", true, (string?)null)));
        var alone = Fragmentation.Decide(Pairs((pair, 70386)), ionTrapHcdLowCid: true);
        Assert.That(alone.DissociationType, Is.Null, "the switch says how ion-trap HCD is searched, only where ion_trap_hcd searches it");
    }

    [Test]
    public void UnderTheLowCidSwitchIonTrapCidAndHcdInOneFileAreOneLowCidFile()
    {
        var c = Fragmentation.Decide(Pairs(("IonTrap2D/CID", 3000), ("IonTrap2D/HCD", 2000)), ionTrapHcd: true, ionTrapHcdLowCid: true);
        Assert.That((c.DissociationType, c.LowRes, c.Unsupported), Is.EqualTo(("LowCID", true, (string?)null)));
        var mixed = Fragmentation.Decide(Pairs(("Orbitrap/HCD", 3000), ("IonTrap2D/HCD", 2000)), ionTrapHcd: true, ionTrapHcdLowCid: true);
        Assert.That(mixed.Unsupported, Is.EqualTo("ion-trap and high-resolution MS2 are mixed in one file"), "Orbitrap and ion-trap MS2 still never merge");
        foreach (var p in new[] { "Orbitrap/HCD", "Orbitrap/CID", "IonTrap2D/CID", "Orbitrap/UVPD", "IonTrap2D/ETD" })
            Assert.That(Fragmentation.Decide(Pairs((p, 5000)), ionTrapHcd: true, ionTrapHcdLowCid: true), Is.EqualTo(Fragmentation.Decide(Pairs((p, 5000)))), p);
    }

    [Test]
    public void IonTrapHcdNeverMergesWithOrbitrapHcd()
    {
        var c = Fragmentation.Decide(Pairs(("Orbitrap/HCD", 3000), ("IonTrap2D/HCD", 2000)), ionTrapHcd: true);
        Assert.That((c.DissociationType, c.Unsupported), Is.EqualTo(((string?)null, "ion-trap and high-resolution MS2 are mixed in one file")));
    }

    [Test]
    public void IonTrapCidAndIonTrapHcdInOneFileAreRefused()
    {
        var c = Fragmentation.Decide(Pairs(("IonTrap2D/CID", 3000), ("IonTrap2D/HCD", 2000)), ionTrapHcd: true);
        Assert.That((c.DissociationType, c.Unsupported), Is.EqualTo(((string?)null, "ion-trap CID and ion-trap HCD are mixed in one file")));
    }

    [Test]
    public void TheIonTrapHcdSwitchChangesNothingElse()
    {
        // Every other case decides the same with the switch on: an existing profile's files are unaffected.
        foreach (var p in new[] { "Orbitrap/HCD", "Orbitrap/CID", "IonTrap2D/CID", "Orbitrap/UVPD", "IonTrap2D/ETD" })
            Assert.That(Fragmentation.Decide(Pairs((p, 5000)), ionTrapHcd: true), Is.EqualTo(Fragmentation.Decide(Pairs((p, 5000)))), p);
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
    public void AnIonTrapHcdFileGetsHcdAtTheLowResTolerance()
    {
        // PXR-A68: what lowres@2 writes for PXD057407's files, from the decision QC records.
        string dir = TestSupport.TempDir();
        string raw = TestSupport.WriteFile(dir, "Phospho_Old-2_1_A.raw", "");
        var prov = new AgingProvenance("search_metamorpheus", dir, TestSupport.WriteFile(dir, "p.json", "{}"), new JsonObject(), null);
        var call = Fragmentation.Decide(new Dictionary<string, int> { ["IonTrap2D/HCD"] = 196184 }, ionTrapHcd: true);
        SearchStage.WritePerFileSettings(new[] { raw }, null, new Dictionary<string, FragmentationCall> { ["Phospho_Old-2_1_A.raw"] = call },
            "±0.5000 Absolute", prov);
        var lines = File.ReadAllLines(Path.Combine(dir, "Phospho_Old-2_1_A.toml"));
        Assert.That(lines.Skip(1), Is.EqualTo(new[] { "DissociationType = \"HCD\"", "ProductMassTolerance = \"±0.5000 Absolute\"" }));
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
        // PXR-A68: ion-trap HCD is decided per file, so it needs per_file; a fixed profile naming it is a mistake.
        Assert.That(Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "d.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nion_trap_hcd = true"))).IonTrapHcd, Is.True);
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "e.toml",
            src.Replace("[engine]", "[engine]\nion_trap_hcd = true"))))!.Message, Does.Contain("per_file"));
        // v0.4.28: ion_trap_hcd_lowcid says how ion-trap HCD is searched, so it needs ion_trap_hcd.
        Assert.That(Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "f.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nion_trap_hcd = true\nion_trap_hcd_lowcid = true"))).IonTrapHcdLowCid, Is.True);
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "g.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nion_trap_hcd_lowcid = true"))))!.Message, Does.Contain("ion_trap_hcd = true"));
        // v0.4.29: ion_trap_hcd_probe_lowcid is about the probe's ion-trap HCD, so it needs ion_trap_hcd and probe_search,
        // and ion_trap_hcd_lowcid already covers the probe.
        string probing = src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nprobe_search = true\nion_trap_hcd = true");
        Assert.That(Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "h.toml",
            probing.Replace("ion_trap_hcd = true", "ion_trap_hcd = true\nion_trap_hcd_probe_lowcid = true"))).IonTrapHcdProbeLowCid, Is.True);
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "i.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nprobe_search = true\nion_trap_hcd_probe_lowcid = true"))))!.Message, Does.Contain("ion_trap_hcd = true"));
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "j.toml",
            src.Replace("[engine]", "[engine]\nfragmentation = \"per_file\"\nion_trap_hcd = true\nion_trap_hcd_probe_lowcid = true"))))!.Message, Does.Contain("probe_search"));
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "k.toml",
            probing.Replace("ion_trap_hcd = true", "ion_trap_hcd = true\nion_trap_hcd_lowcid = true\nion_trap_hcd_probe_lowcid = true"))))!.Message, Does.Contain("together"));
        var shipped = Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        string[] ionTrap = { "label-free-dda-lowres@1", "label-free-dda-lowres@2", "label-free-dda-lowres@3", "label-free-dda-lowres@4", "label-free-dda-lowres@5" };
        Assert.That(shipped.Where(kv => !ionTrap.Contains(kv.Key)).Select(kv => kv.Value.Fragmentation), Has.All.EqualTo("fixed"),
            "no shipped profile changes behaviour (D26): per_file arrives with a new profile");
        Assert.That(ionTrap.Select(k => shipped[k].Fragmentation), Has.All.EqualTo("per_file"), "the ion-trap profiles are the ones that write it");
        Assert.That(shipped.Where(kv => kv.Value.IonTrapHcd).Select(kv => kv.Key),
            Is.EquivalentTo(new[] { "label-free-dda-lowres@2", "label-free-dda-lowres@3", "label-free-dda-lowres@4", "label-free-dda-lowres@5" }));
    }

    [Test]
    public void Lowres5TakesIonTrapHcdAsLowCidInTheProbeOnly()
    {
        // 2026-10-21: lowres@4's LowCID full searches lost 1-16% of PSMs (PXD052631, PXD016430, PXD015170); its evidence was
        // the probe's settings, under which HCD finds far less (PXD022393's lowres@3 probe: 0 PSMs in 3 runs).
        var shipped = Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var ionTrapHcd = Pairs(("IonTrap2D/HCD", 70386));
        string? Type(string key, bool probe) =>
            Fragmentation.Decide(ionTrapHcd, shipped[key].IonTrapHcd, QcStage.IonTrapHcdAsLowCid(shipped[key], probe)).DissociationType;
        Assert.Multiple(() =>
        {
            Assert.That((Type("label-free-dda-lowres@5", probe: true), Type("label-free-dda-lowres@5", probe: false)), Is.EqualTo(("LowCID", "HCD")));
            Assert.That((Type("label-free-dda-lowres@4", probe: true), Type("label-free-dda-lowres@4", probe: false)), Is.EqualTo(("LowCID", "LowCID")),
                "a profile version never changes");
            Assert.That((Type("label-free-dda-lowres@3", probe: true), Type("label-free-dda-lowres@3", probe: false)), Is.EqualTo(("HCD", "HCD")));
            Assert.That(shipped.Where(kv => kv.Value.IonTrapHcdProbeLowCid).Select(kv => kv.Key), Is.EquivalentTo(new[] { "label-free-dda-lowres@5" }));
        });
        // The full search's ion-trap HCD file is lowres@3's: HCD at the low-res tolerance, and a CID/HCD mix refused again.
        var full = Fragmentation.Decide(ionTrapHcd, true, QcStage.IonTrapHcdAsLowCid(shipped["label-free-dda-lowres@5"], probe: false));
        Assert.That(full, Is.EqualTo(Fragmentation.Decide(ionTrapHcd, true, QcStage.IonTrapHcdAsLowCid(shipped["label-free-dda-lowres@3"], probe: false))));
        Assert.That(full.LowRes, Is.True);
        var mix = Pairs(("IonTrap2D/CID", 3000), ("IonTrap2D/HCD", 2000));
        Assert.That(Fragmentation.Decide(mix, true, QcStage.IonTrapHcdAsLowCid(shipped["label-free-dda-lowres@5"], probe: false)).Unsupported,
            Is.EqualTo("ion-trap CID and ion-trap HCD are mixed in one file"));
        Assert.That(Fragmentation.Decide(mix, true, QcStage.IonTrapHcdAsLowCid(shipped["label-free-dda-lowres@5"], probe: true)).DissociationType, Is.EqualTo("LowCID"));
    }

    [Test]
    public void AProfileWithoutCalibrationSearchesWithinTheProbesMeasuredOffset()
    {
        // PXD045821 (2026-10-17): lowres@2 runs no Calibration task, so MetaMorpheus's ±5 ppm precursor window and FlashLFQ's
        // 5 ppm met precursors +8.7 ppm off, and 0.37% of its spectra were identified.
        string dir = TestSupport.TempDir();
        string src = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-lowres-2.toml")).Replace("version = 2", "version = 99");
        var p = Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml",
            src.Replace("[engine]", "[engine]\nprobe_search = true\nprecursor_window = \"probe\"")));
        Assert.That(p.PrecursorWindow, Is.EqualTo("probe"));
        Assert.That(Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "q.toml", src)).PrecursorWindow, Is.EqualTo("default"));
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "b.toml",
            src.Replace("[engine]", "[engine]\nprecursor_window = \"probe\""))))!.Message, Does.Contain("probe_search"), "the probe measures the offset");
        Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "c.toml",
            src.Replace("[engine]", "[engine]\nprobe_search = true\nprecursor_window = \"20 ppm\""))));
        string calibrated = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-5.toml")).Replace("version = 5", "version = 99");
        Assert.That(Assert.Throws<Config.ConfigException>(() => Config.ProfileLoader.Load(TestSupport.WriteFile(dir, "d.toml",
            calibrated.Replace("[engine]", "[engine]\nprobe_search = true\nprecursor_window = \"probe\""))))!.Message, Does.Contain("Calibration"),
            "a calibrated search has calibration's windows");

        // The window goes into the Search task as MetaMorpheus writes it, and FlashLFQ's with it.
        string defaults = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "mm-1.1.12", "SearchTask.toml"));
        var none = new HashSet<(string, string)>();
        var (wide, _) = TaskFiles.Edit(defaults, TaskKind.Search,
            new TaskSettings(8, true, Array.Empty<string>(), PrecursorMassTolerance: "±14.0000 PPM", QuantifyPpmTol: 14), none);
        var (plain, _) = TaskFiles.Edit(defaults, TaskKind.Search, new TaskSettings(8, true, Array.Empty<string>()), none);
        Assert.Multiple(() =>
        {
            // No line ending in the strings: the fixture is CRLF on a Windows checkout and LF on public CI's Linux one.
            Assert.That(wide, Does.Match(@"(?m)^PrecursorMassTolerance = ""±14\.0000 PPM""\r?$").And.Match(@"(?m)^QuantifyPpmTol = 14\.0\r?$"));
            Assert.That(plain, Does.Match(@"(?m)^PrecursorMassTolerance = ""±5\.0000 PPM""\r?$").And.Match(@"(?m)^QuantifyPpmTol = 5\.0\r?$"),
                "unset, both keep the pinned MetaMorpheus's defaults");
        });

        // The search's window, and what provenance says about it.
        var prov = new AgingProvenance("search_metamorpheus", dir, TestSupport.WriteFile(dir, "p.json", "{}"), new JsonObject(), null);
        var offsets = new Dictionary<string, double> { ["a"] = 8.7, ["b"] = 8.1, ["c"] = 9.0 };
        Assert.Multiple(() =>
        {
            Assert.That(SearchStage.PrecursorWindow(p, offsets, prov), Is.EqualTo(14));
            Assert.That(prov.Record["precursor_window"]!["window_ppm"]!.GetValue<int>(), Is.EqualTo(14));
            Assert.That(prov.Record["precursor_window"]!["probe_offsets_ppm"]!["c"]!.GetValue<double>(), Is.EqualTo(9.0));
            Assert.That(SearchStage.PrecursorWindow(p, new Dictionary<string, double> { ["a"] = 0.4 }, null), Is.Null, "no offset: MetaMorpheus's windows");
            Assert.That(SearchStage.PrecursorWindow(p, null, null), Is.Null, "nothing measured: MetaMorpheus's windows");
            Assert.That(SearchStage.PrecursorWindow(p with { PrecursorWindow = "default" }, offsets, null), Is.Null, "the default never widens");
        });

        var shipped = Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(shipped.Where(kv => kv.Value.PrecursorWindow != "default").Select(kv => kv.Key),
            Is.EquivalentTo(new[] { "label-free-dda-lowres@3", "label-free-dda-lowres@4", "label-free-dda-lowres@5" }), "no shipped profile changes behaviour (D26): the window arrives with a new profile");
    }
}
