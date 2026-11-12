using NUnit.Framework;
using PXReprise.Batch;
using PXReprise.Config;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.46 (aging PXR-A51, DATAREPO-74; the user, 2026-11-11: "yes b"): the manifest's <c>restrictions</c> come from the
/// engine's rule (any enrichment restricts occupancy) and the question's <c>[restrictions]</c>, never from a hand edit.
/// </summary>
public class RestrictionsTests
{
    private const string Occupancy = "the material was selected on purpose, so a site's occupancy here describes the enriched material, not the proteome";

    private static string Entry(string acc, string body) => $"  - accession: {acc}\n    status: include\n{body}";

    [Test]
    public void AnyEnrichmentRestrictsOccupancyAndNoneRestrictsNothing()
    {
        Assert.That(Restrictions.Of(new[] { "none" }, null), Is.Empty);
        Assert.That(Restrictions.Of(Array.Empty<string>(), null), Is.Empty, "an entry with no enrichment key (a hand-written exclusion)");
        Assert.That(Restrictions.Of(new[] { "phospho" }, null)["occupancy"], Is.EqualTo($"enriched (phospho): {Occupancy}"));
        Assert.That(Restrictions.Of(new[] { "immunoprecipitation", "other" }, null)["occupancy"], Does.StartWith("enriched (immunoprecipitation, other): "));
        // PXD007035: enrichment [ubiquitin_GG] with a run_enrichment map naming `none` runs too.
        Assert.That(Restrictions.Of(new[] { "ubiquitin_GG", "ubiquitin_GG", "none" }, null)["occupancy"],
            Is.EqualTo($"enriched (ubiquitin_GG) in some runs (run_enrichment names which): {Occupancy}"));
    }

    [Test]
    public void TheQuestionAddsUsesAndItsReasonReplacesTheRules()
    {
        var declared = new Dictionary<string, string> { ["any_analysis"] = "MHC immunopeptidome searched as tryptic", ["occupancy"] = "TUBE: K48 chains captured" };
        var uses = Restrictions.Of(new[] { "affinity_purification" }, declared);
        Assert.That(uses.Keys, Is.EqualTo(new[] { "any_analysis", "occupancy" }), "sorted by use, as dataRepo sorts them");
        Assert.That(uses["occupancy"], Is.EqualTo("TUBE: K48 chains captured"));
        Assert.That(Restrictions.Of(new[] { "none" }, declared).Count, Is.EqualTo(2), "a declared use holds whatever the enrichment");
    }

    [Test]
    public void TheKeyGoesBeforeNotesReplacesAHandWrittenOneAndLeavesWhenNothingRestricts()
    {
        string text = "datasets:\n\n"
            + Entry("PXD000001", "    enrichment: [phospho]\n    flags: [enriched]\n    notes: >\n      Searched.\n\n")
            + Entry("PXD000002", "    enrichment: [none]\n    restrictions: {protein_statistics: \"few PSMs\"}\n    notes: n\n\n")
            + Entry("PXD000003", "    enrichment: [none]\n    restrictions:\n      cysteine: \"NEM searched as carbamidomethyl\"\n      occupancy: \"x\"\n    flags: []\n");
        var (synced, r) = Restrictions.SyncText(text, null);
        Assert.That(synced, Does.Contain($"    flags: [enriched]\n    restrictions:\n      occupancy: \"enriched (phospho): {Occupancy}\"\n    notes: >\n      Searched.\n\n  - accession: PXD000002\n"));
        Assert.That(synced, Does.Contain("  - accession: PXD000002\n    status: include\n    enrichment: [none]\n    notes: n\n\n"), "a hand-written restriction the question does not declare goes");
        Assert.That(synced, Does.EndWith("  - accession: PXD000003\n    status: include\n    enrichment: [none]\n    flags: []\n"), "a nested one goes with all its lines");
        Assert.That(r.Changed.Keys, Is.EqualTo(new[] { "PXD000001", "PXD000002", "PXD000003" }));
        Assert.That(r.Restricted, Is.EqualTo(1));

        var declared = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["PXD000002"] = new Dictionary<string, string> { ["protein_statistics"] = "5 of 10 runs under 400 PSMs" },
            ["PXD000009"] = new Dictionary<string, string> { ["any_analysis"] = "not delivered yet" },
        };
        var (again, r2) = Restrictions.SyncText(synced, declared);
        Assert.That(again, Does.Contain("    enrichment: [none]\n    restrictions:\n      protein_statistics: \"5 of 10 runs under 400 PSMs\"\n    notes: n\n"));
        Assert.That(r2.Changed.Keys, Is.EqualTo(new[] { "PXD000002" }));
        Assert.That(r2.Waiting, Is.EqualTo(new[] { "PXD000009" }), "a key with no entry waits for its delivery");
        Assert.That(Restrictions.SyncText(again, declared).Result.Changed, Is.Empty, "a second sync changes nothing");
    }

    [Test]
    public void ASplitSearchsEntryIsItsOwnKeyAndAReasonIsQuotedForYaml()
    {
        string text = "datasets:\n\n" + Entry("PXD030407-imac", "    split_reason: enrichment\n    enrichment: [phospho]\n")
            + "\n" + Entry("PXD030407-tp", "    split_reason: enrichment\n    enrichment: [none]\n");
        var declared = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["PXD030407-tp"] = new Dictionary<string, string> { ["age_analysis"] = "ages \"young\" and \"old\" only, with a \\ in it" },
        };
        var (synced, r) = Restrictions.SyncText(text, declared);
        Assert.That(r.Changed.Keys, Is.EqualTo(new[] { "PXD030407-imac", "PXD030407-tp" }));
        Assert.That(synced, Does.Contain("    restrictions:\n      age_analysis: \"ages \\\"young\\\" and \\\"old\\\" only, with a \\\\ in it\"\n"));
        Assert.That(synced, Does.Contain("a site's occupancy"), "an apostrophe is not escaped as \\u0027");
    }

    [Test]
    public void SyncKeepsTheFilesLineEndsAndWritesNothingWhenNothingChanges()
    {
        string dir = TestSupport.TempDir();
        string mf = TestSupport.WriteFile(dir, "manifest.yaml",
            ("datasets:\n\n" + Entry("PXD000001", "    enrichment: [acetyl]\n    notes: >\n      x\n")).ReplaceLineEndings("\r\n"));
        var preview = Restrictions.Sync(mf, null, write: false);
        Assert.That(preview.Changed.Keys, Is.EqualTo(new[] { "PXD000001" }));
        Assert.That(File.ReadAllText(mf), Does.Not.Contain("restrictions"), "a preview writes nothing");
        Restrictions.Sync(mf, null);
        string written = File.ReadAllText(mf);
        Assert.That(written, Does.Contain("    restrictions:\r\n      occupancy: \"enriched (acetyl): "));
        Assert.That(written.Replace("\r\n", "").Contains('\n'), Is.False, "every line end stays CRLF");
        var stamp = File.GetLastWriteTimeUtc(mf);
        File.SetLastWriteTimeUtc(mf, stamp.AddMinutes(-5));
        Assert.That(Restrictions.Sync(mf, null).Changed, Is.Empty);
        Assert.That(File.GetLastWriteTimeUtc(mf), Is.EqualTo(stamp.AddMinutes(-5)), "an unchanged manifest is not rewritten");
    }

    // v0.4.46 (G69; the user and QuantProject 120, 2026-11-11): the 47 isobaric deposits searched on the default design
    // (each file its own plex) are restricted for quantitation, found from each search's own provenance, never by hand.
    [Test]
    public void AnIsobaricEntrySearchedOnTheDefaultDesignIsRestrictedForQuantitation()
    {
        string work = TestSupport.TempDir();
        void Provenance(string run, string? flag) => TestSupport.WriteFile(Directory.CreateDirectory(Path.Combine(work, run, "04_search")).FullName, "provenance.json",
            flag is null ? "{\"experimental_design\":{\"source\":\"question\"}}" : $"{{\"experimental_design\":{{\"source\":\"pxreprise_default\",\"flag\":\"{flag}\"}}}}");
        Provenance("run_1/PXD034564", Search.DesignStage.NoChannelDesign);   // one TMT10 plex in 32 files, searched as 32
        Provenance("run_1/PXD000002", null);                                  // a TMT deposit whose SDRF stated its plexes
        Provenance("run_1/PXD000003", Search.DesignStage.NoChannelDesign);   // label-free: never judged, whatever its file says
        string Tmt(string acc, string enrichment) => Entry(acc, $"    run: run_1/{acc}\n    quant_method: TMT\n    labelling: TMT\n    labelling_plex: 10\n    enrichment: [{enrichment}]\n    notes: n\n\n");
        string text = "datasets:\n\n" + Tmt("PXD034564", "none") + Tmt("PXD000002", "none")
            + Entry("PXD000003", "    run: run_1/PXD000003\n    quant_method: label-free\n    enrichment: [none]\n\n")
            + Tmt("PXD000004", "phospho");                                     // its search folder is gone
        var keys = Restrictions.NoChannelDesignKeys(text, work, out var unread);
        Assert.That(keys, Is.EquivalentTo(new[] { "PXD034564" }));
        Assert.That(unread, Is.EqualTo(new[] { "PXD000004" }), "an isobaric entry whose provenance does not read is said, not guessed");

        var (synced, r) = Restrictions.SyncText(text, null, keys);
        Assert.That(synced, Does.Contain($"    enrichment: [none]\n    restrictions:\n      quantitation: \"{Restrictions.NoChannelDesignReason}\"\n    notes: n\n"));
        Assert.That(Restrictions.NoChannelDesignReason, Does.Contain("A sample here is one file's channel, not a biological sample")
            .And.Contain("fractions and repeat injections of one plex appear as separate samples"), "QuantProject 120's wording");
        Assert.That(r.Changed.Keys, Is.EqualTo(new[] { "PXD000004", "PXD034564" }), "PXD000004 for its enrichment only");
        Assert.That(Restrictions.Of(new[] { "phospho" }, null, noChannelDesign: true).Keys, Is.EqualTo(new[] { "occupancy", "quantitation" }));
        var declared = new Dictionary<string, string> { ["quantitation"] = "re-searched with its SDRF on 2026-12-01: the question says so" };
        Assert.That(Restrictions.Of(new[] { "none" }, declared, noChannelDesign: true)["quantitation"], Does.StartWith("re-searched"), "the question's reason wins, as for occupancy");
        Assert.That(Restrictions.SyncText(synced, null, keys).Result.Changed, Is.Empty, "a second sync changes nothing");
    }

    [Test]
    public void TheQuestionsRestrictionsAreCheckedAgainstDataReposUses()
    {
        string dir = TestSupport.TempDir();
        string Q(string table) => TestSupport.WriteFile(dir, $"q{Guid.NewGuid():N}.toml",
            "question = \"q\"\nprofiles = [\"label-free-dda@14\"]\n[discover]\nkeywords = [\"aging\"]\n" + table);
        var q = QuestionLoader.Load(Q("[restrictions.PXD058775]\nany_analysis = \"MHC immunopeptidome searched as tryptic\"\n"
            + "[restrictions.PXD030407-imac]\noccupancy = \" IMAC \"\n"));
        Assert.That(q.RestrictionsFor("PXD058775")!["any_analysis"], Is.EqualTo("MHC immunopeptidome searched as tryptic"));
        Assert.That(q.RestrictionsFor("PXD030407-imac")!["occupancy"], Is.EqualTo("IMAC"), "a split search's key; the reason is trimmed");
        Assert.That(q.RestrictionsFor("PXD000001"), Is.Null);
        Assert.That(QuestionLoader.Load(Q("")).Restrictions, Is.Empty);
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.PXD058775]\nany_analyses = \"typo\"\n")), "dataRepo refuses the whole manifest over an unknown use");
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.PXD058775]\noccupancy = \"  \"\n")), "a use needs a reason");
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.PXD058775]\n")), "a table that restricts nothing");
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.MSV000102265]\noccupancy = \"x\"\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.PXD030407-IMAC]\noccupancy = \"x\"\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("[restrictions.PXD058775]\noccupancy = 3\n")));
    }
}
