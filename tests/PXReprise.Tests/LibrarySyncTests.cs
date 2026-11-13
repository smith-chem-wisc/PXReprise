using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Search;
using PXReprise.Storage;

namespace PXReprise.Tests;

/// <summary>Spectral library chains shared through the cloud (Alex and the user, 2026-11-11; PXReprise thread diabetes 004):
/// <see cref="LibraryMerge"/>, <see cref="LibraryCloud"/>, and Plan/Register/Pull/Seed with a remote. The remote here is a
/// folder; S3 runs the same <see cref="IStore"/> calls.</summary>
public class LibrarySyncTests
{
    /// <summary>A library of the given entries, each "name=content": the content line tells two spectra of one name apart.</summary>
    private static string Lib(string dir, string file, params string[] entries) =>
        TestSupport.WriteFile(dir, file, string.Concat(entries.Select(e =>
        {
            var (name, body) = (e.Split('=')[0], e.Split('=')[1]);
            return $"Name: {name}/2\nMW: 1\nComment: {body}\n\n";
        })));

    private static string[] Entries(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Split('\n')[0][6..^2] + "=" + b.Split('\n')[2][9..]).ToArray();

    [Test]
    public void AMergeKeepsTheNewestAndAddsOnlyWhatTheSearchChanged()
    {
        string d = TestSupport.TempDir();
        string parent = Lib(d, "parent.msp", "A=a", "B=b", "E=e");
        string newest = Lib(d, "newest.msp", "A=a", "B=b-newer", "E=e", "C=c");     // another machine: changed B, added C
        string output = Lib(d, "output.msp", "A=a-ours", "B=b-ours", "E=e", "D=d");  // this search: changed A and B, added D
        string dest = Path.Combine(d, "merged.msp");
        var counts = LibraryMerge.Onto(parent, output, newest, dest);
        Assert.That(counts, Is.EqualTo(new LibraryMerge.Counts(Added: 1, Replaced: 1, KeptNewer: 1)));
        Assert.That(Entries(dest), Is.EqualTo(new[] { "A=a-ours", "B=b-newer", "E=e", "C=c", "D=d" }));
    }

    [Test]
    public void AFirstLibraryMergesOntoOneAnotherMachineWroteMeanwhile()
    {
        string d = TestSupport.TempDir();
        string newest = Lib(d, "newest.msp", "A=a", "B=b");
        string output = Lib(d, "output.msp", "B=b-ours", "C=c");
        string dest = Path.Combine(d, "merged.msp");
        var counts = LibraryMerge.Onto(null, output, newest, dest);
        Assert.That(counts, Is.EqualTo(new LibraryMerge.Counts(Added: 1, Replaced: 0, KeptNewer: 1)), "with no parent, every entry is the search's own");
        Assert.That(Entries(dest), Is.EqualTo(new[] { "A=a", "B=b", "C=c" }));
    }

    [Test]
    public void TwoMachinesSearchFromOneVersionAndNeitherLosesItsSpectra()
    {
        string cloudDir = TestSupport.TempDir(), rootA = TestSupport.TempDir(), rootB = TestSupport.TempDir();
        string runA = TestSupport.TempDir(), runB = TestSupport.TempDir(), runA2 = TestSupport.TempDir();
        using var cloudA = new LibraryCloud(new LocalStore(cloudDir), "A");
        using var cloudB = new LibraryCloud(new LocalStore(cloudDir), "B");

        Lib(runA, "SpectralLibrary_1.msp", "P=p", "Q=q");
        var v1 = SpectralLibrary.Register(SpectralLibrary.Plan(rootA, "mouse", cloud: cloudA), runA, "a1", "PXD000001", "1.1.13", cloud: cloudA);
        Assert.That((int)v1["version"]!, Is.EqualTo(1));

        var planA = SpectralLibrary.Plan(rootA, "mouse", cloud: cloudA);
        var planB = SpectralLibrary.Plan(rootB, "mouse", cloud: cloudB);    // B fetches v1
        Assert.That((planA.ParentVersion, planB.ParentVersion), Is.EqualTo(((int?)1, (int?)1)));
        Assert.That(File.ReadAllText(planB.LibraryIn!), Is.EqualTo(File.ReadAllText(planA.LibraryIn!)));

        Lib(runA2, "updateSpectralLibrary_2.msp", "P=p", "Q=q", "R=r-from-a");
        Lib(runB, "updateSpectralLibrary_2.msp", "P=p-from-b", "Q=q", "S=s-from-b");
        var v2 = SpectralLibrary.Register(planA, runA2, "a2", "PXD000002", "1.1.13", cloud: cloudA);
        var v3 = SpectralLibrary.Register(planB, runB, "b1", "PXD000003", "1.1.13", cloud: cloudB);   // not refused: merged
        Assert.That(((int)v2["version"]!, (int)v3["version"]!), Is.EqualTo((2, 3)));
        Assert.That((int?)v3["parent_version"], Is.EqualTo(1), "the version the search read");
        Assert.That((int)v3["merged_onto"]!["version"]!, Is.EqualTo(2));
        Assert.That(Entries(Path.Combine(rootB, v3["path"]!.GetValue<string>())), Is.EqualTo(new[] { "P=p-from-b", "Q=q", "R=r-from-a", "S=s-from-b" }));

        var pulled = SpectralLibrary.Pull(rootA, "mouse", cloudA);
        Assert.That((int)pulled["added"]!, Is.EqualTo(1));
        Assert.That(SpectralLibrary.Plan(rootA, "mouse").LibraryIn, Is.EqualTo(Path.Combine(rootA, "mouse/mouse.v003.msp")));
        Assert.That(File.ReadAllText(Path.Combine(rootA, "mouse/mouse.v003.msp")), Is.EqualTo(File.ReadAllText(Path.Combine(rootB, "mouse/mouse.v003.msp"))));
        Assert.That(cloudA.Records("mouse").Keys, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(Directory.EnumerateFiles(Path.Combine(rootB, "mouse"), ".merge-*"), Is.Empty, "the merge's scratch file is gone");
    }

    [Test]
    public void AClaimedNumberIsNotClaimedAgain()
    {
        string cloudDir = TestSupport.TempDir(), d = TestSupport.TempDir();
        using var cloud = new LibraryCloud(new LocalStore(cloudDir), "A");
        string lib = Lib(d, "x.msp", "P=p");
        string other = Lib(d, "y.msp", "P=other");
        JsonObject Rec(string file) => new() { ["version"] = 1, ["path"] = "rat/rat.v001.msp", ["sha256"] = Stores.Sha256File(file) };
        Assert.That(cloud.Claim("rat", Rec(lib), lib), Is.True);
        Assert.That(cloud.Claim("rat", Rec(other), other), Is.False, "v001 is taken");
        Assert.That(cloud.Records("rat")[1]["sha256"]!.GetValue<string>(), Is.EqualTo(Stores.Sha256File(lib)));
    }

    [Test]
    public void AChainThatDivergedIsRefusedNotOverwritten()
    {
        string cloudDir = TestSupport.TempDir(), rootA = TestSupport.TempDir(), rootB = TestSupport.TempDir();
        string runA = TestSupport.TempDir(), runB = TestSupport.TempDir();
        using var cloudA = new LibraryCloud(new LocalStore(cloudDir), "A");
        using var cloudB = new LibraryCloud(new LocalStore(cloudDir), "B");
        Lib(runA, "SpectralLibrary_1.msp", "P=p");
        SpectralLibrary.Register(SpectralLibrary.Plan(rootA, "rat", cloud: cloudA), runA, "a", "PXD000001", "1.1.13", cloud: cloudA);
        Lib(runB, "SpectralLibrary_1.msp", "Z=z");
        SpectralLibrary.Register(SpectralLibrary.Plan(rootB, "rat"), runB, "b", "PXD000002", "1.1.13");   // B searched before it synced
        var e = Assert.Throws<SearchSetupException>(() => SpectralLibrary.Pull(rootB, "rat", cloudB));
        Assert.That(e!.Message, Does.Contain("diverged"));
        Assert.That(Entries(Path.Combine(rootB, "rat/rat.v001.msp")), Is.EqualTo(new[] { "Z=z" }), "B's own chain is untouched");
    }

    [Test]
    public void ASeededChainCarriesItsHistoryAndOnlyTheCurrentLibrary()
    {
        string cloudDir = TestSupport.TempDir(), rootA = TestSupport.TempDir(), rootB = TestSupport.TempDir();
        string run1 = TestSupport.TempDir(), run2 = TestSupport.TempDir();
        Lib(run1, "SpectralLibrary_1.msp", "P=p");
        SpectralLibrary.Register(SpectralLibrary.Plan(rootA, "human"), run1, "1", "PXD000001", "1.1.13");
        Lib(run2, "updateSpectralLibrary_2.msp", "P=p", "Q=q");
        SpectralLibrary.Register(SpectralLibrary.Plan(rootA, "human"), run2, "2", "PXD000002", "1.1.13");

        using var cloudA = new LibraryCloud(new LocalStore(cloudDir), "MOZZLE");
        using var cloudB = new LibraryCloud(new LocalStore(cloudDir), "B");
        var seeded = SpectralLibrary.Seed(rootA, "human", cloudA);
        Assert.That((int)seeded["seeded_versions"]!, Is.EqualTo(2));
        Assert.That(Directory.EnumerateFiles(Path.Combine(cloudDir, "human"), "*.msp").Count(), Is.EqualTo(1), "only the current library is uploaded");
        Assert.Throws<UsageException>(() => SpectralLibrary.Seed(rootA, "human", cloudA));

        var plan = SpectralLibrary.Plan(rootB, "human", cloud: cloudB);
        Assert.That((plan.Mode, plan.ParentVersion), Is.EqualTo(("update", (int?)2)));
        Assert.That(File.Exists(Path.Combine(rootB, "human/human.v001.msp")), Is.False, "older versions are records only");
        var record = SpectralLibrary.ReadRegistry(rootB)["organisms"]!["human"]!["versions"]![1]!;
        Assert.That(record["synced_from"]!["machine"]!.GetValue<string>(), Is.EqualTo("MOZZLE"));
        Assert.That(record["produced_by"]!["accession"]!.GetValue<string>(), Is.EqualTo("PXD000002"));
    }

    [Test]
    public void ARollbackIsRefusedOnASyncedChain()
    {
        using var cloud = new LibraryCloud(new LocalStore(TestSupport.TempDir()), "A");
        Assert.Throws<UsageException>(() => SpectralLibrary.Rollback(TestSupport.TempDir(), "rat", 1, cloud));
    }

    [Test]
    public void TheMachineFileNamesTheRemote()
    {
        string d = TestSupport.TempDir();
        string file = TestSupport.WriteFile(d, "machine.toml",
            "work_root = \"" + d.Replace('\\', '/') + "\"\nspectral_library_remote = \"s3://some-bucket/libraries\"\n[metamorpheus]\n\"1.1.13\" = \"x/CMD.dll\"\n");
        Assert.That(PXReprise.Config.Machine.Load(file).SpectralLibraryRemote, Is.EqualTo("s3://some-bucket/libraries"));
    }

    [Test]
    public void TheBucketNameComesFromTheEnvironment()
    {
        Assert.That(PXReprise.Config.Machine.ExpandBucket("m.toml", "s3://$PXR_S3_BUCKET/libraries", "lab-bucket"),
            Is.EqualTo("s3://lab-bucket/libraries"));
        Assert.That(PXReprise.Config.Machine.ExpandBucket("m.toml", "//share/libraries", null), Is.EqualTo("//share/libraries"),
            "a remote without the variable is taken as written");
        Assert.That(PXReprise.Config.Machine.ExpandBucket("m.toml", null, "lab-bucket"), Is.Null);
        Assert.Throws<PXReprise.Config.ConfigException>(() => PXReprise.Config.Machine.ExpandBucket("m.toml", "s3://$PXR_S3_BUCKET/libraries", null),
            "the variable named but unset is refused, never sent to S3 as text");
    }
}
