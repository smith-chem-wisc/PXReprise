using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Cli;

namespace PXReprise.Tests;

// v0.4.18: after delivery, the search outputs nothing reads go (971 GB of them filled F: on 2026-10-15).
public class PruneStageTests
{
    private static (string Run, string Task, string Libraries, string Copy, string Store) Delivered(bool storeMatches = true)
    {
        string dir = TestSupport.TempDir();
        string run = Path.Combine(dir, "PXD000001"), task = Path.Combine(run, "04_search", "mm", "Task3SearchTask");
        string perFile = Path.Combine(task, "Individual File Results"), libraries = Path.Combine(dir, "spectral_libraries");
        Directory.CreateDirectory(perFile);
        Directory.CreateDirectory(Path.Combine(libraries, "human"));
        foreach (string f in new[] { "AllPSMs.psmtsv", "AllPeptides.psmtsv", "AllQuantifiedPeaks.tsv", "AllQuantifiedProteinGroups.tsv", "results.txt", "AllPSMs_FormattedForPercolator.tab" })
            File.WriteAllText(Path.Combine(task, f), f);
        foreach (string f in new[] { "a_PSMs.psmtsv", "a_Peptides.psmtsv", "a_QuantifiedPeaks.tsv", "a.mzID" })
            File.WriteAllText(Path.Combine(perFile, f), f);
        string copy = Path.Combine(task, "updateSpectralLibrary_2026-10-15.msp"), store = Path.Combine(libraries, "human", "human.v002.msp");
        File.WriteAllText(copy, "Name: PEPTIDE/2\n");
        File.WriteAllText(store, storeMatches ? "Name: PEPTIDE/2\n" : "Name: PEPTIDE/3\n");
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(copy)));
        var prov = new JsonObject
        {
            ["success"] = true,
            ["spectral_library"] = new JsonObject
            {
                ["written"] = new JsonObject
                {
                    ["version"] = 2, ["path"] = "human/human.v002.msp", ["sha256"] = sha,
                    ["produced_by"] = new JsonObject { ["metamorpheus_path"] = copy },
                },
            },
        };
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), prov.ToJsonString());
        return (run, task, libraries, copy, store);
    }

    [Test]
    public void PerFileOutputPercolatorAndTheRegisteredLibraryCopyGoAndTheCombinedTablesStay()
    {
        var (run, task, libraries, copy, store) = Delivered();
        var r = PruneStage.Run(run, run, libraries);
        Assert.That(((int)r["files"]!, (int)r["not_deleted"]!), Is.EqualTo((6, 0)));
        Assert.That(Directory.Exists(Path.Combine(task, "Individual File Results")), Is.False);
        Assert.That(File.Exists(Path.Combine(task, "AllPSMs_FormattedForPercolator.tab")), Is.False);
        Assert.That(File.Exists(copy), Is.False);
        Assert.That(File.Exists(store), Is.True, "the registry's version is never touched");
        foreach (string f in new[] { "AllPSMs.psmtsv", "AllPeptides.psmtsv", "AllQuantifiedPeaks.tsv", "AllQuantifiedProteinGroups.tsv", "results.txt" })
            Assert.That(File.Exists(Path.Combine(task, f)), Is.True, f);

        string record = Path.Combine(run, PruneStage.Folder, "provenance.json");
        var deleted = JsonNode.Parse(File.ReadAllText(record))!["deleted"]!.AsArray();
        Assert.That(deleted.Select(d => Path.GetFileName(d!["path"]!.GetValue<string>())),
            Is.EquivalentTo(new[] { "a_PSMs.psmtsv", "a_Peptides.psmtsv", "a_QuantifiedPeaks.tsv", "a.mzID", "AllPSMs_FormattedForPercolator.tab", Path.GetFileName(copy) }));

        // A re-ingest (store correction) prunes again: nothing is left, and the first record is not overwritten.
        string before = File.ReadAllText(record);
        Assert.That((int)PruneStage.Run(run, run, libraries)["files"]!, Is.Zero);
        Assert.That(File.ReadAllText(record), Is.EqualTo(before));
    }

    [Test]
    public void ALibraryCopyIsKeptWhenTheRegistrysBytesDiffer()
    {
        var (run, _, libraries, copy, _) = Delivered(storeMatches: false);
        PruneStage.Run(run, run, libraries);
        Assert.That(File.Exists(copy), Is.True);
    }

    [Test]
    public void AnUnsuccessfulSearchIsRefusedAndNothingIsDeleted()
    {
        var (run, task, libraries, _, _) = Delivered();
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), "{\"success\": false}");
        Assert.Throws<UsageException>(() => PruneStage.Run(run, run, libraries));
        Assert.That(File.Exists(Path.Combine(task, "Individual File Results", "a.mzID")), Is.True);
    }
}
