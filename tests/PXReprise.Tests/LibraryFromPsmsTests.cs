using Chemistry;
using PXReprise.Search;
using LibraryReader = Readers.SpectralLibrary.SpectralLibrary;

namespace PXReprise.Tests;

/// <summary>
/// Decision 0092: the library a search of low-resolution MS2 adds is built by PXReprise from its PSM table, by
/// MetaMorpheus 1.1.15's own update rule, because MetaMorpheus cannot write or update one there. The table is a real
/// MetaMorpheus search task's (the datarepo seam's fixture): 58 target PSMs at q <= 0.01, 48 precursors.
/// </summary>
public class LibraryFromPsmsTests
{
    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PXReprise.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above the test directory");
    }

    /// <summary>A real MetaMorpheus search task's folder (tests/datarepo-instance, the seam fixture).</summary>
    internal static string Task => Path.Combine(RepoRoot(), "tests", "datarepo-instance", "work_root", "run_test",
        "PXD999999", "04_search", "mm", "Task3SearchTask");
    private static string Psms => Path.Combine(Task, "AllPSMs.psmtsv");

    /// <summary>The best passing PSM's matched fragments, as mzLib parses them.</summary>
    private static int Fragments(string sequence, int z) =>
        LibraryFromPsms.BestPerPrecursor(Psms, "QValue", 0.01, out _)[(sequence, z)].MatchedIons.Count;

    /// <summary>A library spectrum with <paramref name="fragments"/> b/y ions, as MetaMorpheus writes one.</summary>
    private static string Entry(string sequence, int z, int fragments) =>
        $"Name: {sequence}/{z}\nMW: 1000\nComment: Parent=1000 RT=10\nNum peaks: {fragments}\n" +
        string.Concat(Enumerable.Range(1, fragments).Select(i => $"{100 + i}.0\t{(i == 1 ? 1 : 0.5)}\t\"{(i % 2 == 0 ? "b" : "y")}{(i + 1) / 2}^1/0ppm\"\n"));

    [Test]
    public void AChainsFirstVersionHoldsEachPrecursorsBestPsmAsMetaMorpheusWritesIt()
    {
        string out1 = Path.Combine(TestSupport.TempDir(), "SpectralLibrary_x.msp");
        var r = LibraryFromPsms.Write(Psms, LibraryFromPsms.FilterColumnNamed("q-value"), null, out1);
        Assert.That((r.PsmsPassing, r.Precursors, r.ParentKept, r.Replaced, r.Added, r.Spectra), Is.EqualTo((58, 48, 0, 0, 48, 48)));

        using var lib = new LibraryReader(out1);
        var all = lib.GetAllLibrarySpectra().ToList();
        Assert.That(all, Has.Count.EqualTo(48));
        Assert.That(lib.TryGetSpectrum("VGATAAVYSAAILEYLTAEVLELAGNASK", 3, out var s), Is.True);
        Assert.Multiple(() =>
        {
            // MetaMorpheus's own: the theoretical precursor m/z (SpectralMatch.ToLibrarySpectrum), not the measured one ...
            Assert.That(s!.PrecursorMz, Is.EqualTo(2894.52770.ToMz(3)).Within(1e-6));
            int n = Fragments("VGATAAVYSAAILEYLTAEVLELAGNASK", 3);
            Assert.That(s.MatchedFragmentIons, Has.Count.EqualTo(n));
            // ... and intensities scaled to the most intense on writing, not divided by their sum.
            Assert.That(File.ReadLines(out1).SkipWhile(l => !l.StartsWith("Name: VGATAAVYSAAILEYLTAEVLELAGNASK/3", StringComparison.Ordinal))
                .Skip(4).Take(n).Max(l => double.Parse(l.Split('\t')[1], System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo(1));
        });
    }

    [Test]
    public void AParentSpectrumStaysWhenItHoldsMoreFragmentsThanTheNewBestScoreAndIsReplacedOtherwise()
    {
        string dir = TestSupport.TempDir();
        string parent = TestSupport.WriteFile(dir, "mouse_lowcid.v001.msp",
            Entry("VGATAAVYSAAILEYLTAEVLELAGNASK", 3, 30)        // 30 fragments > Truncate(27.229): kept
            + Entry("DDEAAAVALSSLIHALDDLDMVAIVR", 2, 1)          // 1 fragment, best score 25.337: replaced
            + Entry("PEPTIDEK", 2, 3));                          // not seen by this search: kept
        var r = LibraryFromPsms.Write(Psms, LibraryFromPsms.FilterColumnNamed("q-value"), parent, Path.Combine(dir, "updateSpectralLibrary_x.msp"));
        Assert.That((r.ParentKept, r.Replaced, r.Added, r.Spectra), Is.EqualTo((2, 1, 46, 49)));

        using var lib = new LibraryReader(r.Path);
        Assert.That(lib.TryGetSpectrum("VGATAAVYSAAILEYLTAEVLELAGNASK", 3, out var kept) && kept!.MatchedFragmentIons.Count == 30, "the parent's");
        Assert.That(lib.TryGetSpectrum("DDEAAAVALSSLIHALDDLDMVAIVR", 2, out var replaced), Is.True);
        Assert.That(replaced!.MatchedFragmentIons, Has.Count.EqualTo(Fragments("DDEAAAVALSSLIHALDDLDMVAIVR", 2)), "this search's");
        Assert.That(lib.TryGetSpectrum("PEPTIDEK", 2, out _), Is.True);
    }

    [Test]
    public void TheFilterIsTheOneResultsTxtNamesAndATableWithoutItsColumnsIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MetaMorpheusResults.FilterNamed(File.ReadLines(Path.Combine(Task, "results.txt"))), Is.EqualTo("q-value"));
            Assert.That(MetaMorpheusResults.FilterNamed(new[] { "All target peptides with pep q-value <= 0.01: 3", "All target PSMs with q-value notch <= 0.01: 5" }),
                Is.EqualTo("q-value notch"), "the PSM line, not the peptide one");
            Assert.That(LibraryFromPsms.FilterColumnNamed("pep q-value"), Is.EqualTo("PEP_QValue"));
            Assert.That(LibraryFromPsms.FilterColumnNamed("q-value notch"), Is.EqualTo("QValue Notch"));
            Assert.That(LibraryFromPsms.FilterColumnNamed(null), Is.EqualTo("QValue"), "every MetaMorpheus before 1.1.15");
            Assert.That(LibraryFromPsms.FilterColumn("QValueNotch"), Is.EqualTo("QValue Notch"));
            Assert.Throws<SearchSetupException>(() => LibraryFromPsms.FilterColumnNamed("score"));
            Assert.Throws<SearchSetupException>(() => LibraryFromPsms.FilterColumn("Score"));
        });
        string thin = TestSupport.WriteFile(TestSupport.TempDir(), "AllPSMs.psmtsv", "QValue\tDecoy/Contaminant/Target\n0.001\tT\n");
        var e = Assert.Throws<SearchSetupException>(() => LibraryFromPsms.Write(thin, "QValue", null, Path.Combine(TestSupport.TempDir(), "x.msp")));
        Assert.That(e!.Message, Does.Contain("has no 'Full Sequence' column"));
    }
}
