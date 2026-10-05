using PXReprise.Census;
using Readers;

namespace PXReprise.Tests;

public class RankingTests
{
    // PXD058775's own 18 names: four groups; WT1..3 are biological replicates, M1/M2 two injections of each.
    [Test]
    public void FileNamesGiveGroupsAndCountReplicatesNotInjections()
    {
        var names = new[] { "WT1", "WT2", "WT3", "KO1", "KO2", "KO3", "LPS1", "LPS2" }.SelectMany((g, i) =>
            new[] { $"SCR096_X{i:000}_{g}_M1.raw", $"SCR096_X{i:000}_{g}_M2.raw" }).Append("SCR096_X012_OVA_M1.raw").Append("SCR096_X012_OVA_M2.raw");
        var d = Ranking.FromNames(names);
        Assert.That(d.Source, Is.EqualTo("file_names"));
        Assert.That(d.Replicates, Is.EquivalentTo(new Dictionary<string, int> { ["WT"] = 3, ["KO"] = 3, ["LPS"] = 2, ["OVA"] = 1 }));
        Assert.That(d.Power, Is.EqualTo(3), "the second-largest group");
    }

    [Test]
    public void FractionsAreNotReplicates()
    {
        var names = from g in new[] { "Young", "Old" } from r in new[] { "a", "b", "c" } from f in Enumerable.Range(1, 8)
                    select $"Liver_{g}_{r}_F{f}.raw";
        var d = Ranking.FromNames(names);
        Assert.That(d.Fractions, Is.EqualTo(8));
        Assert.That(d.Replicates["Young"], Is.EqualTo(3));
    }

    [Test]
    public void NamesThatNameNoGroupGiveNoDesign()
    {
        var d = Ranking.FromNames(Enumerable.Range(1, 10).Select(i => $"20210330_PHCOQEHF_PC825_{i}.raw"));
        Assert.That((d.Groups, d.Power), Is.EqualTo((0, 0)));
        Assert.That(Ranking.FromNames(new[] { "one.raw" }).Groups, Is.EqualTo(0));
    }

    [Test]
    public void ADepositedSdrfGivesGroupsBySourceNameAndIsSkippedWithoutAVaryingFactor()
    {
        string dir = TestSupport.TempDir();
        string rows = "source name\tcharacteristics[organism]\tcomment[data file]\tcomment[fraction identifier]\tfactor value[disease]\n"
            + string.Join("", from g in new[] { "AD", "control" } from s in Enumerable.Range(1, 6) from f in new[] { "1", "2" }
                              select $"{g}_{s}\tHomo sapiens\t{g}_{s}_{f}.raw\t{f}\t{g}\n");
        var d = Ranking.FromSdrf(new SdrfDocument(TestSupport.WriteFile(dir, "a.sdrf.tsv", rows)))!;
        Assert.That((d.Source, d.Fractions, d.Power), Is.EqualTo(("sdrf", 2, 6)));
        Assert.That(d.Replicates["AD"], Is.EqualTo(6));

        string flat = "source name\tcharacteristics[organism]\tcomment[data file]\tfactor value[disease]\n"
            + string.Join("", Enumerable.Range(1, 4).Select(i => $"s{i}\tHomo sapiens\tf{i}.raw\tnot available\n"));
        Assert.That(Ranking.FromSdrf(new SdrfDocument(TestSupport.WriteFile(dir, "b.sdrf.tsv", flat))), Is.Null, "no factor varies: as good as no SDRF");
    }

    // The user's rule (2026-10-05): bigger is better, within the limits; deposits rarely state their design well enough to rank on.
    [Test]
    public void BiggerIsBetterWithinTheLimitsAndTooLargeGoesLast()
    {
        Assert.That(Ranking.Tier(60, 150, 60, 150), Is.EqualTo(1));
        Assert.That(Ranking.Tier(61, 10, 60, 150), Is.EqualTo(2), "more raw files than the batch takes whole");
        Assert.That(Ranking.Tier(20, 151, 60, 150), Is.EqualTo(2), "a very large download");

        var items = new[] { ("small", 6, 2.0), ("big", 48, 90.0), ("big-cheaper", 48, 40.0), ("mid", 20, 20.0), ("huge", 40, 900.0), ("too-many", 120, 50.0) };
        var order = Ranking.Order(items, x => Ranking.Tier(x.Item2, x.Item3, 60, 150), x => x.Item2, x => x.Item3).Select(x => x.Item1).ToList();
        Assert.That(order, Is.EqualTo(new[] { "big-cheaper", "big", "mid", "small", "too-many", "huge" }));
    }
}
