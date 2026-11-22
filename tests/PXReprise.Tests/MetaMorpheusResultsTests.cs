using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// Reader fixes R3 and R4 (v0.4.34): the lines MetaMorpheus 1.1.13 writes about its own run, in the exact formats of
/// PXD045861's lowres@6 search of 2026-10-29 and of MetaMorpheus 315e02b5e's source.
/// </summary>
public class MetaMorpheusResultsTests
{
    private const string Human = "fd80cf8839f8c1551bddc2d24905df6098188196934f4f43fa8c3e9a276eb14c";
    private const string Cont = "5b6e2059abf229f3d52b894f6abe4c949d6c37dc78667f19f447eab8205004a9";
    private const string Other = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly string[] SearchResults =
    {
        "MetaMorpheus: version 1.1.13",
        "",
        "All target PSMs with q-value <= 0.01: 312908",
        "All target peptides with q-value <= 0.01: 10572",
        "All target protein groups with q-value <= 0.01 (1% FDR): 2682",
        "",
        "Engine Results:",
        "",
        "Engine type: DatabaseLoadingEngine",
        "Time to run engine: 00:00:23.9207116",
        "Databases loaded:",
        "\tUP000005640_all_genecentric_2026_03_agingPTM-v3.xml: 41304 Proteins",
        "\t\t20652 Target Proteins \t 20652 Decoy Proteins",
        @"		Path: F:\aging_data\db\UP000005640_all_genecentric_2026_03_agingPTM-v3.xml",
        $"\t\tSHA-256 {Human}, 1013873486 bytes",
        "\tMetaMorpheusContaminants_NoUPS.xml: 432 Proteins",
        "\t\t216 Target Proteins \t 216 Decoy Proteins",
        @"		Path: F:\aging_batch\pxreprise-314f6ae\profiles\lists\MetaMorpheusContaminants_NoUPS.xml",
        $"\t\tSHA-256 {Cont}, 5227000 bytes",
        "Total Proteins loaded: 41944",
    };

    private static readonly Dictionary<string, string> Ours = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"F:\aging_data\db\UP000005640_all_genecentric_2026_03_agingPTM-v3.xml"] = Human,
        [@"F:\aging_batch\pxreprise-314f6ae\profiles\lists\MetaMorpheusContaminants_NoUPS.xml"] = Cont,
    };

    [Test]
    public void TheDatabasesATaskLoadedAreReadWithPathAndSha256()
    {
        var dbs = MetaMorpheusResults.Databases("Task1SearchTask", SearchResults);
        Assert.That(dbs.Select(d => d.File), Is.EqualTo(new[] { "UP000005640_all_genecentric_2026_03_agingPTM-v3.xml", "MetaMorpheusContaminants_NoUPS.xml" }));
        Assert.That(dbs[0].Sha256, Is.EqualTo(Human));
        Assert.That(dbs[0].Bytes, Is.EqualTo(1013873486));
        Assert.That(dbs[1].Path, Is.EqualTo(@"F:\aging_batch\pxreprise-314f6ae\profiles\lists\MetaMorpheusContaminants_NoUPS.xml"));
        Assert.That(MetaMorpheusResults.Mismatch(dbs, Ours), Is.Null, "the bytes we gave are the bytes it searched");
    }

    [Test]
    public void ADatabaseWhoseBytesDifferFromWhatTheSearchWasGivenIsAMismatch()
    {
        var changed = SearchResults.Select(l => l.Replace(Human, Other)).ToList();
        string? why = MetaMorpheusResults.Mismatch(MetaMorpheusResults.Databases("Task1SearchTask", changed), Ours);
        Assert.That(why, Does.Contain("UP000005640_all_genecentric_2026_03_agingPTM-v3.xml as SHA-256 000000000000"));
    }

    [Test]
    public void AGptmdDatabaseIsCheckedThroughTheParentsItWasDerivedFrom()
    {
        string[] task3 =
        {
            "Databases loaded:",
            "\tUP000005640_all_genecentric_2026_03_agingPTM-v3GPTMD.xml: 41304 Proteins",
            "\t\t20652 Target Proteins \t 20652 Decoy Proteins",
            @"		Path: F:\x\mm\Task2GPTMDTask\UP000005640_all_genecentric_2026_03_agingPTM-v3GPTMD.xml",
            $"\t\tSHA-256 {Other}, 999 bytes",
            $"\t\tDerived from: UP000005640_all_genecentric_2026_03_agingPTM-v3.xml (SHA-256 {Human}, 1013873486 bytes); MetaMorpheusContaminants_NoUPS.xml (SHA-256 unavailable (locked))",
            "Total Proteins loaded: 41944",
        };
        var dbs = MetaMorpheusResults.Databases("Task3SearchTask", task3);
        Assert.That(dbs.Single().DerivedFrom, Is.EqualTo(new (string, string?)[]
        {
            ("UP000005640_all_genecentric_2026_03_agingPTM-v3.xml", Human), ("MetaMorpheusContaminants_NoUPS.xml", null),
        }));
        Assert.That(MetaMorpheusResults.Mismatch(dbs, Ours), Is.Null, "a GPTMD output is not one of ours; its parents agree; an unhashed parent is not a mismatch");
        var wrongParent = MetaMorpheusResults.Databases("Task3SearchTask", task3.Select(l => l.Replace($"(SHA-256 {Human}", $"(SHA-256 {Other}")));
        Assert.That(MetaMorpheusResults.Mismatch(wrongParent, Ours), Does.Contain("was derived from UP000005640_all_genecentric_2026_03_agingPTM-v3.xml SHA-256 000000000000"));
    }

    [Test]
    public void AReleaseThatWritesNoDatabaseBlockGivesNothingToCheck()
    {
        string[] old = { "MetaMorpheus: version 1.1.12", "All target PSMs with q-value <= 0.01: 5", "Engine Results:" };
        Assert.That(MetaMorpheusResults.Databases("Task1SearchTask", old), Is.Empty);
    }

    [Test]
    public void TheIdentificationFilterIsTheOneTheCountLinesState()
    {
        var (counts, notes) = MetaMorpheusResults.Filter(SearchResults);
        Assert.That(counts, Is.EqualTo(new[] { ("PSMs", "q-value", 0.01), ("peptides", "q-value", 0.01), ("protein groups", "q-value", 0.01) }));
        Assert.That(notes, Is.Empty);

        string[] pep =
        {
            "PEP filtering was requested, but there were fewer than 100 PSMs (88, counting decoys), so results were not filtered.",
            "All target PSMs with q-value <= 1: 70",
            "All target peptides with pep q-value <= 0.01: 40",
        };
        (counts, notes) = MetaMorpheusResults.Filter(pep);
        Assert.That(notes, Is.EqualTo(new[] { pep[0] }));
        Assert.That(counts, Is.EqualTo(new[] { ("PSMs", "q-value", 1.0), ("peptides", "pep q-value", 0.01) }));
    }

    // PXR-R80: identification_filter.pep_trained, read from the sentence MetaMorpheus 1.1.15 writes on the line above a count
    // it took partly or wholly by the notch because PEP was not trained (FilteredPsms.PepNotTrainedSentence).
    [Test]
    public void PepTrainedIsTheSentenceAboveThePsmCountUnderThePepFilter()
    {
        static string[] Lines(string s) => s.Split('\n');
        static (string, bool)[] Agents(System.Text.Json.Nodes.JsonNode? n) => n!.AsObject().Select(kv => (kv.Key, kv.Value!.GetValue<bool>())).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Agents(MetaMorpheusResults.PepTrained(Lines(ResultsTxtSamples.Lfq1115PepNotTrained))), Is.EqualTo(new[] { ("trypsin", false) }),
                "the one agent the sentence names");
            Assert.That(MetaMorpheusResults.PepTrained(Lines(ResultsTxtSamples.Pxd031782PepTrained))!.GetValue<bool>(), Is.True, "no sentence: trained");
            foreach (string other in new[] { ResultsTxtSamples.Lfq1114, ResultsTxtSamples.Lfq1115QValue, ResultsTxtSamples.Lfq1115Notch })
                Assert.That(MetaMorpheusResults.PepTrained(Lines(other)), Is.Null, "another filter");
        });

        // Two agents, one in its semi-specific category, in the form FilteredPsms writes them, with Windows line endings.
        string[] two =
        {
            "PEP filtering was requested, but PEP was not trained for 300 target PSMs (chymotrypsin (don't cleave before proline): 120, trypsin (Semi): 180), so those were filtered by q-value notch <= 0.01.\r",
            "All target PSMs with pep q-value <= 0.01: 5000\r",
        };
        Assert.That(Agents(MetaMorpheusResults.PepTrained(two)), Is.EqualTo(new[] { ("chymotrypsin (don't cleave before proline)", false), ("trypsin (Semi)", false) }));
        // A sentence that names no agent, as up to 1.1.14 (which wrote it above a "q-value" line).
        string[] none = { "PEP filtering was requested, but PEP was not computed for these PSMs, so results were filtered by q-value.", "All target PSMs with pep q-value <= 0.01: 50" };
        Assert.That(MetaMorpheusResults.PepTrained(none)!.GetValue<bool>(), Is.False);
        // Only the all-files PSM line is read, and only before the engine results.
        string[] peptidesFirst = { "PEP filtering was requested, but PEP was not trained for 3 target PSMs (trypsin: 3), so those were filtered by q-value notch <= 0.01.",
                                   "All target peptides with pep q-value <= 0.01: 40", "All target PSMs with pep q-value <= 0.01: 70" };
        Assert.That(MetaMorpheusResults.PepTrained(peptidesFirst)!.GetValue<bool>(), Is.True, "the sentence is not above the PSM line");
        Assert.That(MetaMorpheusResults.PepTrained(new[] { "Engine Results:", "All target PSMs with pep q-value <= 0.01: 5" }), Is.Null);
        Assert.That(MetaMorpheusResults.PepTrained(new[] { "nothing here" }), Is.Null);
    }

    [Test]
    public void DesignGapWarningsAreReadFromTheConsoleLog()
    {
        string[] log =
        {
            "1.8\t\tWARN: SDRF output is on: the SDRF will record ProteomeXchange accession 'PXD045861' ...",
            "40.2\t\tWARN: Condition \"aged\": biological replicates 1, 3, quantified as numbered; biological replicate 2 is not in the design. A missing number may be a sample or file that was lost or not searched.",
            "40.2\t\tWARN: Condition \"aged\": biological replicates 1, 3, quantified as numbered; biological replicate 2 is not in the design. A missing number may be a sample or file that was lost or not searched.\r",
        };
        Assert.That(MetaMorpheusResults.DesignGaps(log), Is.EqualTo(new[]
        {
            "Condition \"aged\": biological replicates 1, 3, quantified as numbered; biological replicate 2 is not in the design. A missing number may be a sample or file that was lost or not searched.",
        }));
    }
}
