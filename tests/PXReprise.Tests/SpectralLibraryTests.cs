using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PXReprise.Search;

namespace PXReprise.Tests;

public class SpectralLibraryTests
{
    private static string Msp(string dir, string name, int spectra) =>
        TestSupport.WriteFile(dir, name, string.Concat(Enumerable.Range(0, spectra).Select(i => $"Name: PEP{i}/2\nMW: 1\n\n")));

    // v0.4.34 (the user, 2026-10-29: "tmt will have to start with a blank spectrum library"): a labelled search has its own
    // chain per kit, which starts blank beside a label-free chain that already exists, and never touches it.
    [Test]
    public void AnIsobaricKitHasItsOwnChainThatStartsBlank()
    {
        string root = TestSupport.TempDir(), run1 = TestSupport.TempDir(), run2 = TestSupport.TempDir();
        Msp(run1, "SpectralLibrary_2026-10-29-10-00-00.msp", 4);
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run1, "run1", "PXD000001", "1.1.13");
        var tmt = SpectralLibrary.Plan(root, "mouse", "TMT10");
        Assert.That(tmt, Is.EqualTo(new LibraryPlan("mouse_tmt10", "write", null, null, root)), "blank, though mouse has a library");
        Msp(run2, "SpectralLibrary_2026-10-29-11-00-00.msp", 2);
        SpectralLibrary.Register(tmt, run2, "run2", "PXD034564", "1.1.13");
        Assert.That(SpectralLibrary.Plan(root, "mouse", "TMT10").LibraryIn, Is.EqualTo(Path.Combine(root, "mouse_tmt10/mouse_tmt10.v001.msp")));
        Assert.That(SpectralLibrary.Plan(root, "mouse").LibraryIn, Is.EqualTo(Path.Combine(root, "mouse/mouse.v001.msp")), "the label-free chain is untouched");
        Assert.That(SpectralLibrary.Plan(root, "mouse", "TMT6-plex").Organism, Is.EqualTo("mouse_tmt6plex"));
    }

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

    /// <summary>One library spectrum as MetaMorpheus writes it (mouse.v065.msp, 2026-10-15), with two fragments.</summary>
    private static string Entry(string sequence, int z, string nl = "\n") =>
        $"Name: {sequence}/{z}{nl}MW: 500.25{nl}Comment: Parent=500.25 RT=42.5{nl}Num peaks: 2{nl}" +
        $"147.11280\t1\t\"y1^1/0ppm\"{nl}244.16556\t0.5\t\"b2^1/0ppm\"{nl}";

    private const string Nem = "C[Unimod:NEM:2H(5) on C]DVGAENWQR";

    [Test]
    public void ASpectrumMzLibWouldTakeForACrosslinkIsLeftOutOfTheRegisteredVersionAndTheRestKeepTheirBytes()
    {
        foreach (string nl in new[] { "\n", "\r\n" })
        {
            string root = TestSupport.TempDir(), run = TestSupport.TempDir();
            string produced = TestSupport.WriteFile(run, "SpectralLibrary_x.msp",
                Entry("PEPTIDEK", 2, nl) + Entry(Nem, 2, nl) + Entry("AHC[Unimod:NEM:2H(5) on C]LSEK", 3, nl) + Entry("VDPVNFK", 1, nl));
            var v1 = SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run, "r", "PXD010091", "1.1.12");
            Assert.That(((int)v1["n_spectra"]!, (int?)v1["dropped_unreadable"]), Is.EqualTo((2, (int?)2)), nl);
            Assert.That(File.ReadAllText(Path.Combine(root, "mouse/mouse.v001.msp")), Is.EqualTo(Entry("PEPTIDEK", 2, nl) + Entry("VDPVNFK", 1, nl)), nl);
            Assert.That(File.ReadAllText(produced), Does.Contain(Nem), "MetaMorpheus's own file is left whole");

            // A clean library is copied byte for byte, and its record keeps the old shape.
            TestSupport.WriteFile(run, "updateSpectralLibrary_y.msp", Entry("PEPTIDEK", 2, nl) + Entry("LGEHNIDVLEGNEQFINAAK", 3, nl));
            var v2 = SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run, "r2", "PXD000002", "1.1.12");
            Assert.That(v2.ContainsKey("dropped_unreadable"), Is.False, nl);
            Assert.That(File.ReadAllBytes(Path.Combine(root, "mouse/mouse.v002.msp")), Is.EqualTo(File.ReadAllBytes(Path.Combine(run, "updateSpectralLibrary_y.msp"))), nl);
        }
    }

    [Test]
    public void RepairWritesANewCurrentVersionWithoutTheUnreadableSpectraAndLeavesACleanOneAlone()
    {
        string root = TestSupport.TempDir();
        // A registry as v0.4.19 left it: the current version holds a NEM:2H(5) spectrum.
        Directory.CreateDirectory(Path.Combine(root, "human"));
        TestSupport.WriteFile(Path.Combine(root, "human"), "human.v057.msp", Entry("PEPTIDEK", 2) + Entry(Nem, 2) + Entry("VDPVNFK", 1));
        TestSupport.WriteFile(root, "registry.json", $$"""
            { "schema": "{{SpectralLibrary.Schema}}", "organisms": { "human": { "current": "human/human.v057.msp", "versions": [
              { "version": 57, "path": "human/human.v057.msp", "mode": "update", "parent_version": 56 } ] } } }
            """);
        var r = SpectralLibrary.Repair(root, "human", "PXD039172 failed on it");
        Assert.That(((bool)r["repaired"]!, (int)r["version"]!, (string)r["mode"]!, (int)r["parent_version"]!, (int)r["dropped_unreadable"]!, (int)r["n_spectra"]!),
            Is.EqualTo((true, 58, "repair", 57, 1, 2)));
        Assert.That(r["produced_by"]!["repair_of"]!.GetValue<string>(), Is.EqualTo("human/human.v057.msp"));
        var plan = SpectralLibrary.Plan(root, "human");
        Assert.That((plan.LibraryIn, plan.ParentVersion), Is.EqualTo((Path.Combine(root, "human/human.v058.msp"), (int?)58)));
        Assert.That(File.Exists(Path.Combine(root, "human/human.v057.msp")), "versions are never removed");

        var again = SpectralLibrary.Repair(root, "human", "twice");
        Assert.That(((bool)again["repaired"]!, (int)again["version"]!), Is.EqualTo((false, 58)));
        Assert.That(SpectralLibrary.ReadRegistry(root)["organisms"]!["human"]!["versions"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That((int)SpectralLibrary.List(root)["human"]!["version"]!, Is.EqualTo(58));
    }

    /// <summary>One spectrum, told apart from another of the same peptide by its retention time.</summary>
    private static string At(string sequence, int rt, string mw = "500.25", string ion = "y1") =>
        $"Name: {sequence}/2\nMW: {mw}\nComment: Parent={mw} RT={rt}\nNum peaks: 1\n147.11280\t1\t\"{ion}^1/0ppm\"\n";

    // 2026-11-24 (D91's rule; the maintainer: "yes, take them out"): a deposit with a stated quality problem leaves the
    // libraries too. What its search added goes; what it replaced comes back; what a later search replaced stays.
    [Test]
    public void ARepairCanTakeOutWhatOneDepositsSearchPutInTheChain()
    {
        string root = TestSupport.TempDir();
        string dir = Path.Combine(root, "human");
        Directory.CreateDirectory(dir);
        TestSupport.WriteFile(dir, "human.v001.msp", At("AAA", 1) + At("BBB", 2));
        // BAD replaced BBB and added CCC. It also rewrote AAA's annotation ("Y1"), as MetaMorpheus versions did ("D126" to
        // "d126"): the same spectrum, which is not BAD's.
        TestSupport.WriteFile(dir, "human.v002.msp", At("AAA", 1, ion: "Y1") + At("BBB", 20) + At("CCC", 3));
        TestSupport.WriteFile(dir, "human.v003.msp", At("AAA", 10, "500.3") + At("BBB", 20, "500.3") + At("CCC", 3) + At("DDD", 4)); // a later search replaced AAA, added DDD
        TestSupport.WriteFile(root, "registry.json", $$"""
            { "schema": "{{SpectralLibrary.Schema}}", "organisms": { "human": { "current": "human/human.v003.msp", "versions": [
              { "version": 1, "path": "human/human.v001.msp", "mode": "write", "parent_version": null, "produced_by": { "accession": "PXD000001" } },
              { "version": 2, "path": "human/human.v002.msp", "mode": "update", "parent_version": 1, "produced_by": { "accession": "PXD044773" } },
              { "version": 3, "path": "human/human.v003.msp", "mode": "update", "parent_version": 2, "produced_by": { "accession": "PXD000003" } } ] } } }
            """);

        var r = SpectralLibrary.Repair(root, "human", "PXD044773: mouse runs searched as human", removeDeposits: new[] { "PXD044773" });

        Assert.That(((bool)r["repaired"]!, (int)r["version"]!, (int)r["n_spectra"]!), Is.EqualTo((true, 4, 3)));
        Assert.That(((int)r["removed_deposits"]!["spectra_removed"]!, (int)r["removed_deposits"]!["spectra_restored"]!), Is.EqualTo((1, 1)));
        Assert.That((int)r["removed_deposits"]!["versions"]![0]!["spectra_added_or_replaced"]!, Is.EqualTo(2), "BBB and CCC, not AAA's new annotation");
        // BBB's spectrum is compared without its precursor lines, which a later version had moved (500.25 -> 500.3).
        Assert.That(File.ReadAllText(Path.Combine(dir, "human.v004.msp")), Is.EqualTo(At("AAA", 10, "500.3") + At("BBB", 2) + At("DDD", 4)));
        Assert.That(SpectralLibrary.Plan(root, "human").ParentVersion, Is.EqualTo(4));

        var again = SpectralLibrary.Repair(root, "human", "twice", removeDeposits: new[] { "PXD044773" });
        Assert.That(((bool)again["repaired"]!, (int)again["version"]!), Is.EqualTo((false, 4)), "nothing of it is left to take out");
    }

    /// <summary>
    /// The premise, against mzLib's own reader (the version MetaMorpheus 1.1.12 runs): a library holding a NEM:2H(5)
    /// spectrum cannot be read back, and the same library without it can.
    /// </summary>
    [Test]
    public void MzLibCannotReadALibraryHoldingACountInAModNameAndReadsItOnceRepaired()
    {
        string dir = TestSupport.TempDir();
        var readable = new[] { ("PEPTIDEK", 2), ("VDPVNFK", 1), ("LGEHNIDVLEGNEQFINAAK", 3) };
        string bad = TestSupport.WriteFile(dir, "lib.msp", Entry("PEPTIDEK", 2) + Entry(Nem, 2) + Entry("VDPVNFK", 1) + Entry("LGEHNIDVLEGNEQFINAAK", 3));
        string good = Path.Combine(dir, "repaired.msp");
        Assert.That(SpectralLibrary.CopyClean(bad, good).Unreadable, Is.EqualTo(1));
        Assert.That(MzLibReads(bad, readable), Is.False, "mzLib read back a library holding NEM:2H(5): the repair may no longer be needed");
        Assert.That(MzLibReads(good, readable), Is.True);
    }

    private static bool MzLibReads(string path, (string Sequence, int Z)[] spectra)
    {
        using var lib = new Readers.SpectralLibrary.SpectralLibrary(new List<string> { path });
        try
        {
            return spectra.All(s => lib.TryGetSpectrum(s.Sequence, s.Z, out var found) && found.Name == $"{s.Sequence}/{s.Z}");
        }
        catch (MzLibUtil.MzLibException)
        {
            return false;
        }
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
