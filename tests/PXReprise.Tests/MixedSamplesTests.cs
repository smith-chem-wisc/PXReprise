using PXReprise.Batch;

namespace PXReprise.Tests;

/// <summary>G16 (aging 024): file names that say a deposit holds different kinds of sample.</summary>
public class MixedSamplesTests
{
    [Test]
    public void LysateIpAndEvRunsAreFlagged()
    {
        // PXD077298's 22 runs; on 2026-10-04 the only one of 96 aging deposits this flags.
        var names = new[] { "SA", "Veh" }.SelectMany(t =>
            Enumerable.Range(1, 4).Select(i => $"Lysate_{t}_{i}.raw")
                .Concat(Enumerable.Range(1, 4).Select(i => $"IP_{t}_{i}.raw"))
                .Concat(Enumerable.Range(1, 3).Select(i => $"EV_{t}_{i}.raw")));
        var g = MixedSamples.Detect(names)!;
        Assert.That(g.Select(x => (x.Kind, x.Files)), Is.EqualTo(new[] { ("pulldown", 8), ("whole", 8), ("vesicle", 6) }));
        Assert.That(MixedSamples.Describe(g), Is.EqualTo("8 'IP' (pulldown), 8 'Lysate' (whole), 6 'EV' (vesicle)"));
    }

    [Test]
    public void NamesThatSayNothingOrOneKindAreNotFlagged()
    {
        Assert.That(MixedSamples.Detect(new[] { "20210106_PHCOQEHF_PC793-OBrien_1_R1.raw", "20210106_PHCOQEHF_PC793-OBrien_2_R2.raw" }), Is.Null);
        Assert.That(MixedSamples.Detect(new[] { "178.raw", "199.raw" }), Is.Null, "PXD058611's numbered runs: curation, not names");
        Assert.That(MixedSamples.Detect(new[] { "phospho_1.raw", "phospho_2.raw" }), Is.Null, "one kind");
        Assert.That(MixedSamples.Detect(new[] { "lysate_1.raw", "IP_1.raw", "sample_3.raw" }), Is.Null, "every file must say what it is");
        Assert.That(MixedSamples.Detect(new[] { "Lysate_IP_1.raw", "IP_2.raw" }), Is.Null, "two kinds in one name is ambiguous");
        Assert.That(MixedSamples.Detect(new[] { "ship_1.raw", "level_2.raw" }), Is.Null, "whole tokens only");
    }
}
