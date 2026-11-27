using System.Text.Json.Nodes;
using PXReprise.Batch;
using PXReprise.Config;

namespace PXReprise.Tests;

/// <summary>
/// G144 (2026-11-27): an entry is kept only when it describes this search. Until then <see cref="ManifestEntry.Append"/>
/// kept every existing entry except once after <c>batch research</c>. So a requeued deposit whose entry an earlier search
/// wrote (PXD027318 and PXD036557: the Python batch's, under MetaMorpheus 1.1.11) was ingested from that search's outputs,
/// "unchanged", while the batch logged "MANIFEST entry appended" and "delivery: delivered".
/// </summary>
public class ManifestReplaceTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);

    /// <summary>A run folder searched under <paramref name="mm"/> with <paramref name="files"/> files, and a manifest holding
    /// <paramref name="entries"/>.</summary>
    private static (string Dir, string Run, string Manifest) Searched(string acc, string mm, int files, string entries)
    {
        string dir = TestSupport.TempDir(), run = Path.Combine(dir, "runs", acc);
        Directory.CreateDirectory(Path.Combine(run, "02b_qc"));
        Directory.CreateDirectory(Path.Combine(run, "04_search"));
        var qc = new JsonObject();
        for (int i = 0; i < files; i++) qc[$"f{i}.raw"] = new JsonObject { ["pass"] = true, ["fail_reasons"] = new JsonArray() };
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), qc.ToJsonString());
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), Provenance(mm));
        return (dir, run, TestSupport.WriteFile(dir, "manifest.yaml", "manifest_version: 1\ndatasets:\n" + entries));
    }

    private static string Provenance(string mm) => "{\"tools\":{\"MetaMorpheus\":{\"release\":\"" + mm + "\"}}}";

    private static string EntryOf(string manifest, string acc)
    {
        string text = ManifestEntry.ReadLf(manifest);
        var (start, end) = ManifestEntry.Block(text, acc);
        return text[start..end];
    }

    // PXD036557's entry as the Python batch wrote it, in brief: another run root, MetaMorpheus 1.1.11, an owner's `sdrf`
    // field, YAML comments and a curated flag.
    private const string PythonEra = """

          - accession: PXD036557
            status: include
            delivery: delivered
            delivered_utc: 2026-11-27T08:56:02Z
            title: "Modelling Premature Cardiac Aging"
            run: run_2026-09-18/PXD036557_n18
            stages:
              qc: 02b_qc
              search: 04_search_mm1111
            search_results: 04_search_mm1111/mm/Task3SearchTask
            files: 18
            metamorpheus: "1.1.11"
            provenance_schema: aging-provenance/3   # re-derived 2026-09-19 by pipeline/bin/reprovenance.py
            # The search ran under /2, where id_rate.psms_1pct held the FDR-engine count.
            flags: [low_id_rate, no_design_file, no_output_sdrf]
            sdrf: deposited SDRF is an empty skeleton (S2); no usable sample metadata yet
            notes: >
              HGPS iPSC-derived cardiomyocytes.
              The benchmark's O1 is asked of this dataset.

          - accession: PXD018999
            status: include
            notes: kept

        """;

    [Test]
    public void ARequeuedDepositsEntryFromAnEarlierSearchIsReplacedKeepingWhatItsOwnerWrote()
    {
        var (dir, run, manifest) = Searched("PXD036557", "1.1.16", 18, PythonEra);
        var e = new QueueEntry("PXD036557", "Modelling Premature Cardiac Aging", "human");

        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir), Is.EqualTo(ManifestWrite.Replaced));

        string text = ManifestEntry.ReadLf(manifest);
        Assert.That(text.Split("accession: PXD036557").Length - 1, Is.EqualTo(1), "one entry, the new search's");
        Assert.That(text, Does.Contain("accession: PXD018999\n    status: include\n    notes: kept"), "the other entries are untouched");
        string entry = EntryOf(manifest, "PXD036557");
        Assert.That(entry, Does.Contain("    run: runs/PXD036557\n").And.Contain("metamorpheus: \"1.1.16\"").And.Contain("search_results: 04_search/mm/"));
        Assert.That(entry, Does.Contain("delivery: pending").And.Not.Contain("delivered"), "delivered only once this search is ingested");
        Assert.That(entry, Does.Contain("REPLACED: this entry described an earlier search (run run_2026-09-18/PXD036557_n18, MetaMorpheus 1.1.11, 18 files)")
            .And.Contain("BEFORE THIS SEARCH this entry said: \"HGPS iPSC-derived cardiomyocytes. The benchmark's O1 is asked of this dataset.\""));
        Assert.That(entry, Does.Contain("    sdrf: deposited SDRF is an empty skeleton (S2)"), "an owner's field survives");
        Assert.That(entry, Does.Contain("low_id_rate"), "an owner's flag survives");
        Assert.That(entry, Does.Not.Contain("RE-SEARCHED"), "it was not a re-search");

        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir), Is.EqualTo(ManifestWrite.Kept));
        Assert.That(EntryOf(manifest, "PXD036557"), Is.EqualTo(entry), "a second delivery keeps it: replaced once, never nested");
    }

    [Test]
    public void AnEntryThatDescribesThisSearchIsKeptAsItsOwnerLeftIt()
    {
        var (dir, run, manifest) = Searched("PXD000401", "1.1.16", 2, "");
        var e = new QueueEntry("PXD000401", "t", "human");
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir), Is.EqualTo(ManifestWrite.Appended));
        // A store correction edits the entry by hand (an operator_sdrf, a note); the driver's next start delivers it again.
        string edited = File.ReadAllText(manifest).Replace("    status: include\n", "    status: include\n    operator_sdrf: designs/PXD000401.sdrf.tsv\n");
        File.WriteAllText(manifest, edited);
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir), Is.EqualTo(ManifestWrite.Kept));
        Assert.That(File.ReadAllText(manifest), Is.EqualTo(edited), "byte for byte");
    }

    [TestCase("1.1.15", 2, Description = "another MetaMorpheus release")]
    [TestCase("1.1.16", 3, Description = "another file count")]
    public void AnEntryFromAnotherSearchOfTheSameRunFolderIsReplaced(string mm, int files)
    {
        var (dir, run, manifest) = Searched("PXD000402", "1.1.16", 2, "");
        var e = new QueueEntry("PXD000402", "t", "human");
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir);
        // The same run folder searched again, as a requeue does without `batch research`.
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), Provenance(mm));
        var qc = new JsonObject();
        for (int i = 0; i < files; i++) qc[$"f{i}.raw"] = new JsonObject { ["pass"] = true, ["fail_reasons"] = new JsonArray() };
        File.WriteAllText(Path.Combine(run, "02b_qc", "qc_report.json"), qc.ToJsonString());

        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir), Is.EqualTo(ManifestWrite.Replaced));
        Assert.That(EntryOf(manifest, "PXD000402"), Does.Contain($"metamorpheus: \"{mm}\"").And.Contain($"files: {files}\n").And.Contain("REPLACED:"));
    }

    [Test]
    public void AnOldResearchsMarkerDoesNotShieldALaterRequeue()
    {
        var (dir, run, manifest) = Searched("PXD000403", "1.1.15", 2, "");
        var e = new QueueEntry("PXD000403", "t", "human");
        var state = new JsonObject
        {
            ["researched"] = new JsonArray(new JsonObject { ["utc"] = "2026-10-22T18:00:48Z", ["previous_profile"] = "label-free-dda@1", ["moved_to"] = "x", ["reason"] = "October" }),
        };
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir), Is.EqualTo(ManifestWrite.Appended));
        Assert.That(EntryOf(manifest, "PXD000403"), Does.Contain("RE-SEARCHED 2026-10-22T18:00:48Z"));
        // Requeued a month later and searched under a newer MetaMorpheus; the state still holds October's research record.
        File.WriteAllText(Path.Combine(run, "04_search", "provenance.json"), """{"tools":{"MetaMorpheus":{"release":"1.1.16"}}}""");

        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir), Is.EqualTo(ManifestWrite.Replaced));
        string entry = EntryOf(manifest, "PXD000403");
        Assert.That(entry, Does.Contain("metamorpheus: \"1.1.16\"").And.Contain("REPLACED: this entry described an earlier search (run runs/PXD000403, MetaMorpheus 1.1.15, 2 files)"));
        Assert.That(entry.Split("RE-SEARCHED 2026-10-22").Length - 1, Is.EqualTo(1), "October's marker only as quoted in BEFORE THIS SEARCH, never written afresh");
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir), Is.EqualTo(ManifestWrite.Kept));
    }

    [Test]
    public void AReSearchReportsThatItReplacedTheEntry()
    {
        var (dir, run, manifest) = Searched("PXD000404", "1.1.16", 1, "");
        var e = new QueueEntry("PXD000404", "t", "human");
        ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, new JsonObject(), dir);
        var state = new JsonObject
        {
            ["researched"] = new JsonArray(new JsonObject { ["utc"] = "2026-11-27T20:00:00Z", ["previous_profile"] = "label-free-dda@1", ["moved_to"] = "x", ["reason"] = "the curated SDRF" }),
        };
        // Same profile, release and file count: only the research record tells this search from the last.
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir), Is.EqualTo(ManifestWrite.Replaced));
        Assert.That(EntryOf(manifest, "PXD000404"), Does.Contain("RE-SEARCHED 2026-11-27T20:00:00Z").And.Not.Contain("REPLACED:"));
        Assert.That(ManifestEntry.Append(manifest, e, Profiles["label-free-dda@1"], run, state, dir), Is.EqualTo(ManifestWrite.Kept));
    }
}
