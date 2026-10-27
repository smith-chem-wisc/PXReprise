using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.32's library cleaning (the user, 2026-10-27; cleavageRules PXR-V8): a spectrum goes when the modification on its
/// last residue blocks trypsin at EVERY place its sequence occurs, or when it carries a GPTMD hydroxy-P/K site.
/// </summary>
public class LibraryCleaningTests
{
    private const string Acetyl = "[Common Biological:Acetylation on K]";

    // P1: PEPTIDEK sits inside (trypsin cut after its K). P2: VDPVNFK is the protein's C-terminus. P3: LGEHNIDK occurs twice,
    // once inside and once at the C-terminus.
    private static string Database(string dir) => TestSupport.WriteFile(dir, "db.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <uniprot xmlns="http://uniprot.org/uniprot">
        <entry dataset="Swiss-Prot"><accession>P00001</accession><sequence length="17">MAAAPEPTIDEKAAAAR</sequence></entry>
        <entry dataset="Swiss-Prot"><accession>P00002</accession><sequence length="11">GGGGVDPVNFK</sequence></entry>
        <entry dataset="Swiss-Prot"><accession>P00003</accession><sequence length="26">SSLGEHNIDKAAWWWRLGEHNIDK</sequence></entry>
        </uniprot>
        """);

    private static LibraryCleaning Cleaning(string dir) => LibraryCleaning.For(Array.Empty<string>(), new[] { Database(dir) });

    [Test]
    public void ABlockedEndingGoesOnlyWhenEveryOccurrenceWasACut()
    {
        var v = Cleaning(TestSupport.TempDir()).Judge(new[]
        {
            "PEPTIDEK" + Acetyl,           // inside P1: trypsin cut after an acetyl-K it could not cut after
            "VDPVNFK" + Acetyl,            // P2's own C-terminus: no cut made it
            "LGEHNIDK" + Acetyl,           // inside P3 once, and P3's C-terminus once: one occurrence is possible
            "NOWHERESK" + Acetyl,          // in no protein: cannot be judged
            "PEPTIDEK",                    // unmodified
            "PEPTIDEK[Common Biological:Methylation on K]",   // methyl-K does not block trypsin
        });
        Assert.That(v.Keys, Is.EquivalentTo(new[] { "PEPTIDEK" + Acetyl }));
        Assert.That(v["PEPTIDEK" + Acetyl], Is.EqualTo(("blocked_cterm", "Common Biological:Acetylation on K")));
    }

    [Test]
    public void AGptmdHydroxylationGoesAndUniProtsOwnStays()
    {
        var v = Cleaning(TestSupport.TempDir()).Judge(new[]
        {
            "AAP[Common Biological:Hydroxylation on P]GEK",
            "AAK[Common Biological:Hydroxylation on K]GEK",
            "AAP[UniProt:4-hydroxyproline on P]GEK",
        });
        Assert.That(v.Keys, Is.EquivalentTo(new[] { "AAP[Common Biological:Hydroxylation on P]GEK", "AAK[Common Biological:Hydroxylation on K]GEK" }));
        Assert.That(v.Values.Select(x => x.Reason).Distinct(), Is.EqualTo(new[] { "doubtful_class" }));
    }

    [TestCase("PEPTIDEK[Common Biological:Acetylation on K]", true, "PEPTIDEK", "Common Biological:Acetylation on K")]
    [TestCase("AAD[Metal:Fe[III] on D]K[Common Biological:Acetylation on K]", true, "AADK", "Common Biological:Acetylation on K")]
    [TestCase("AAK[Metal:Fe[III] on D]", true, "AAK", "Metal:Fe[III] on D")]
    [TestCase("PEPTIDEK-[Common Artifact:Amidation on X]", false, "", "")]
    [TestCase("PEPTIDEK", false, "", "")]
    public void TheLastResiduesModificationIsReadThroughNestedBrackets(string full, bool has, string baseSequence, string mod)
    {
        Assert.That(LibraryCleaning.TryLastResidueMod(full, out var b, out var m), Is.EqualTo(has));
        if (has) Assert.That((b, m), Is.EqualTo((baseSequence, mod)));
    }

    [Test]
    public void ACleanedCopyDropsWholeEntriesAndRecordsWhy()
    {
        string dir = TestSupport.TempDir();
        string Entry(string seq, int z) => $"Name: {seq}/{z}\nMW: 500.25\nComment: Parent=500.25 RT=42.5\nNum peaks: 1\n147.11280\t1\t\"y1^1/0ppm\"\n";
        string src = TestSupport.WriteFile(dir, "lib.msp",
            Entry("PEPTIDEK" + Acetyl, 2) + Entry("PEPTIDEK", 2) + Entry("PEPTIDEK" + Acetyl, 3) + Entry("AAP[Common Biological:Hydroxylation on P]GEK", 2));
        string dest = Path.Combine(dir, "clean.msp");
        var d = SpectralLibrary.CopyClean(src, dest, Cleaning(dir));
        Assert.That((d.Unreadable, d.BlockedCTerm, d.DoubtfulClass), Is.EqualTo((0, 2, 1)));
        Assert.That(d.ByModification["Common Biological:Acetylation on K"], Is.EqualTo(2));
        Assert.That(File.ReadAllText(dest), Is.EqualTo(Entry("PEPTIDEK", 2)));
    }
}
