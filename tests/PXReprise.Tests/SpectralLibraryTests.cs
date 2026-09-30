using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PXReprise.Search;

namespace PXReprise.Tests;

public class SpectralLibraryTests
{
    private static string Msp(string dir, string name, int spectra) =>
        TestSupport.WriteFile(dir, name, string.Concat(Enumerable.Range(0, spectra).Select(i => $"Name: PEP{i}/2\nMW: 1\n\n")));

    [Test]
    public void TheFirstSearchWritesEveryLaterOneUpdatesAndTheChainIsAppendOnly()
    {
        string root = TestSupport.TempDir(), run1 = TestSupport.TempDir(), run2 = TestSupport.TempDir();
        var p1 = SpectralLibrary.Plan(root, "Human");
        Assert.That(p1, Is.EqualTo(new LibraryPlan("human", "write", null, null, root)));
        Msp(run1, "SpectralLibrary_2026-09-28-10-00-00.msp", 3);
        var v1 = SpectralLibrary.Register(p1, run1, "run1", "PXD000001", "1.1.11");
        Assert.That((int)v1["version"]!, Is.EqualTo(1));
        Assert.That((int)v1["n_spectra"]!, Is.EqualTo(3));

        var p2 = SpectralLibrary.Plan(root, "human");
        Assert.That(p2.Mode, Is.EqualTo("update"));
        Assert.That(p2.LibraryIn, Is.EqualTo(Path.Combine(root, "human/human.v001.msp")));
        Assert.That(p2.ParentVersion, Is.EqualTo(1));
        Msp(run2, "updateSpectralLibrary_2026-09-28-11-00-00.msp", 5);
        Msp(run2, "SpectralLibrary_old.msp", 1);   // a stray write-name file must not be taken in update mode
        var v2 = SpectralLibrary.Register(p2, run2, "run2", "PXD000002", "1.1.11");
        Assert.That(((int)v2["version"]!, (int?)v2["parent_version"], (int)v2["n_spectra"]!), Is.EqualTo((2, (int?)1, 5)));

        var reg = SpectralLibrary.ReadRegistry(root);
        Assert.That(reg["organisms"]!["human"]!["current"]!.GetValue<string>(), Is.EqualTo("human/human.v002.msp"));
        Assert.That(reg["organisms"]!["human"]!["versions"]!.AsArray(), Has.Count.EqualTo(2));
    }

    [Test]
    public void ARaceIsRefusedAndNothingIsLost()
    {
        string root = TestSupport.TempDir(), a = TestSupport.TempDir(), b = TestSupport.TempDir();
        var first = SpectralLibrary.Plan(root, "mouse");
        Msp(a, "SpectralLibrary_a.msp", 2);
        SpectralLibrary.Register(first, a, "a", "PXD000003", "1.1.11");
        var p = SpectralLibrary.Plan(root, "mouse");      // both runs resolve parent v1 ...
        var q = SpectralLibrary.Plan(root, "mouse");
        Msp(a, "updateSpectralLibrary_a.msp", 3);
        Msp(b, "updateSpectralLibrary_b.msp", 4);
        SpectralLibrary.Register(p, a, "a2", "PXD000004", "1.1.11");                              // ... one registers v2
        var e = Assert.Throws<SearchSetupException>(() => SpectralLibrary.Register(q, b, "b", "PXD000005", "1.1.11"));
        Assert.That(e!.Message, Does.Contain("moved from version 1 to 2"));
    }

    [Test]
    public void AMissingCurrentLibraryIsRefusedNotRestartedAndRollbackMovesCurrent()
    {
        string root = TestSupport.TempDir(), run = TestSupport.TempDir();
        Msp(run, "SpectralLibrary_x.msp", 2);
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run, "r", "PXD000006", "1.1.11");
        Msp(run, "updateSpectralLibrary_y.msp", 4);
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run, "r2", "PXD000007", "1.1.11");
        SpectralLibrary.Rollback(root, "rat", 1);
        Assert.That(SpectralLibrary.Plan(root, "rat").ParentVersion, Is.EqualTo(1));
        File.Delete(Path.Combine(root, "rat/rat.v001.msp"));
        Assert.Throws<SearchSetupException>(() => SpectralLibrary.Plan(root, "rat"));
    }

    /// <summary>
    /// The live registry the aging batch writes is read as-is (read only): every organism plans an update from its current
    /// version, and the current file's sha256 and spectrum count are the ones registered. Needs the aging work disk.
    /// </summary>
    [Test, Category("LocalCorpus")]
    public void TheLiveAgingRegistryIsReadAndItsCurrentLibrariesVerify()
    {
        const string root = @"F:\aging_data\spectral_libraries";
        if (!File.Exists(SpectralLibrary.RegistryPath(root))) Assert.Ignore("the aging work disk is not attached");
        var reg = SpectralLibrary.ReadRegistry(root);
        foreach (var (organism, node) in reg["organisms"]!.AsObject())
        {
            var plan = SpectralLibrary.Plan(root, organism);
            Assert.That(plan.Mode, Is.EqualTo("update"), organism);
            var rec = node!["versions"]!.AsArray().Single(v => v!["path"]!.GetValue<string>() == node["current"]!.GetValue<string>())!;
            Assert.That(plan.ParentVersion, Is.EqualTo((int)rec["version"]!), organism);
            using var s = File.OpenRead(plan.LibraryIn!);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(s)), Is.EqualTo(rec["sha256"]!.GetValue<string>()), organism);
            Assert.That(SpectralLibrary.CountSpectra(plan.LibraryIn!), Is.EqualTo((int)rec["n_spectra"]!), organism);
        }
    }
}
