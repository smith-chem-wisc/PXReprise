using DataRepo.Runner;
using Quantification.PtmQtl;

namespace DataRepo.Tests;

/// <summary>ptmQtl S5 (D70): one physical site under several accessions is one site within a dataset.</summary>
public class PtmQtlDuplicateSiteTests
{
    private const string Phospho = "Common Biological:Phosphorylation on S";

    private static SiteRunOccupancy Cell(string protein, int position, string run, double modified, double covering, string modification = Phospho) =>
        new(new ModificationSite(protein, position, 'S', modification), run, OccupancyState.Quantified, modified, covering, true)
        {
            ReportedFraction = modified / covering,
        };

    private static List<string> Kept(List<SiteRunOccupancy> cells, out Dictionary<string, long> leftOut)
    {
        leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        return PtmQtlCore.MergeDuplicateSites(cells, leftOut).Select(c => c.Site.ProteinAccession).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    [Test]
    public void IdenticalCellsUnderTwoAccessionsAreOneSiteKeptByTheOrdinalFirstKey()
    {
        // tau: P10636-2 S315 = P10636 S721, the same serine on one shared peptide.
        var cells = new List<SiteRunOccupancy>
        {
            Cell("P10636-2", 315, "r1", 10, 40), Cell("P10636-2", 315, "r2", 12, 30),
            Cell("P10636", 721, "r1", 10, 40), Cell("P10636", 721, "r2", 12, 30),
        };
        var first = new[] { cells[0].Site, cells[2].Site }.OrderBy(s => s.Key, StringComparer.Ordinal).First().ProteinAccession;
        Assert.That(first, Is.EqualTo("P10636-2"), "'-' sorts before ':' in mzLib's key");
        Assert.That(Kept(cells, out var leftOut), Is.EqualTo(new[] { first }), "the ordinal-first key stands for both");
        Assert.That(leftOut.Values.Single(), Is.EqualTo(1));
    }

    [Test]
    public void ACellThatDiffersInOneRunKeepsBothSites()
    {
        var cells = new List<SiteRunOccupancy>
        {
            Cell("A", 5, "r1", 10, 40), Cell("A", 5, "r2", 12, 30),
            Cell("B", 9, "r1", 10, 40), Cell("B", 9, "r2", 12, 31),
        };
        Assert.That(Kept(cells, out var leftOut), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(leftOut, Is.Empty);
    }

    [Test]
    public void ADifferentModificationOrNoModifiedIntensityIsNeverMerged()
    {
        var other = new List<SiteRunOccupancy>
        {
            Cell("A", 5, "r1", 10, 40), Cell("B", 5, "r1", 10, 40, "Common Biological:Phosphorylation on T"),
        };
        Assert.That(Kept(other, out _), Is.EqualTo(new[] { "A", "B" }), "the modification is part of the identity");

        var sparse = new List<SiteRunOccupancy> { Cell("A", 5, "r1", 0, 40), Cell("B", 9, "r1", 0, 40) };
        Assert.That(Kept(sparse, out _), Is.EqualTo(new[] { "A", "B" }), "two sites with no modified intensity are not joined by coincidence");
    }
}
