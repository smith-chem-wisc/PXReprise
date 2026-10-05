using System.Net;
using System.Text.Json;
using PXReprise.Census;
using PXReprise.Cli;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

public class CensusTests
{
    private static (FakeSearch Search, string QuestionFile, string OutDir) Setup()
    {
        var s = new FakeSearch();
        s.Results["type 2 diabetes"] = new()
        {
            TestSupport.Record("PXD000100", "Type 2 diabetes muscle"),
            TestSupport.Record("PXD000101", "Type 2 diabetes islets", "TMT 10-plex"),
            TestSupport.Record("PXD000102", "Type 2 diabetes plasma", "DIA-NN library-free"),
            TestSupport.Record("PXD000103", "Type 2 diabetes in mice", organisms: new[] { "Mus musculus (mouse)" }),
        };
        s.Results["insulin resistance"] = new()
        {
            TestSupport.Record("PXD000100", "Type 2 diabetes muscle"),     // found by both keywords: counted once
            TestSupport.Record("PXD000104", "Insulin resistance in adipose"),
            TestSupport.Record("PXD000105", "Cell cycle in yeast"),       // the keyword matched elsewhere; not relevant
        };
        string dir = TestSupport.TempDir();
        return (s, TestSupport.WriteFile(dir, "question.toml", TestSupport.MinimalQuestion), Path.Combine(dir, "out"));
    }

    private static async Task<(int Exit, JsonElement Data)> Run(FakeSearch s, params string[] argv)
    {
        var sw = new StringWriter();
        int exit = await Program.RunAsync(argv, sw, CancellationToken.None, () => s);
        var doc = JsonDocument.Parse(sw.ToString());
        return (exit, doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : doc.RootElement.Clone());
    }

    // Aging 031/032 (PXR-A25 to A27): disease keywords queue a deposit only with a reference group; watch keywords never.
    [Test]
    public async Task DiseaseKeywordsNeedAReferenceGroupAndWatchKeywordsAreNeverQueued()
    {
        var s = new FakeSearch();
        s.Results["type 2 diabetes"] = new() { TestSupport.Record("PXD000300", "Type 2 diabetes muscle") };
        s.Results["alzheimer"] = new()
        {
            TestSupport.Record("PXD000301", "Alzheimer brain proteome versus age-matched controls"),
            TestSupport.Record("PXD000302", "Alzheimer brain proteome of APP/PS1 mice"),
            TestSupport.Record("PXD000303", "Type 2 diabetes and Alzheimer brain"),     // also an ordinary keyword
        };
        s.Results["heart failure"] = new() { TestSupport.Record("PXD000304", "Heart failure versus healthy hearts") };
        s.Results["type 2 diabetes"].Add(TestSupport.Record("PXD000303", "Type 2 diabetes and Alzheimer brain"));
        string dir = TestSupport.TempDir();
        string q = TestSupport.WriteFile(dir, "question.toml", TestSupport.MinimalQuestion.Replace("[discover]",
            "[discover]\ndisease_keywords = [\"alzheimer\"]\nwatch_keywords = [\"heart failure\"]"));
        q = TestSupport.WriteFile(dir, "question.toml", File.ReadAllText(q).Replace("require_any = [", "require_any = [\"alzheimer\", \"heart failure\", "));
        string outDir = Path.Combine(dir, "out");
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(0), data.ToString());

        var queued = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json"))).RootElement.EnumerateArray()
            .Select(x => x.GetProperty("accession").GetString()).ToList();
        Assert.That(queued, Is.EquivalentTo(new[] { "PXD000300", "PXD000301", "PXD000303" }));
        var watch = File.ReadAllLines(Path.Combine(outDir, "watch.tsv")).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0], c => c[^2]);
        Assert.That(watch, Is.EqualTo(new Dictionary<string, string>
        {
            ["PXD000302"] = "no_reference_group_found",     // disease only, no control named
            ["PXD000304"] = "watch_list",                   // found only by a watch keyword, though it names a control
        }));
        Assert.That(data.GetProperty("queued_by_keyword").GetProperty("alzheimer").GetInt32(), Is.EqualTo(2));
        Assert.That(data.GetProperty("queued_by_keyword").GetProperty("type 2 diabetes").GetInt32(), Is.EqualTo(2));
    }

    [TestCase("brains of AD patients and age-matched controls", true)]
    [TestCase("compared with healthy donors", true)]
    [TestCase("vehicle-treated mice", true)]
    [TestCase("young and old mice", true)]
    [TestCase("WT littermates", true)]
    [TestCase("quality control was performed with HeLa digests", false)]
    [TestCase("GAPDH served as a loading control", false)]
    [TestCase("with control of the false discovery rate at 1%", false)]
    [TestCase("a controlled vocabulary was used", false)]
    [TestCase("APP/PS1 mice at 6 months", false)]
    public void AReferenceGroupIsReadFromTheRecord(string text, bool found)
    {
        var r = TestSupport.Record("PXD000305", "Alzheimer brain", text);
        Assert.That(PXReprise.Discovery.ReferenceGroup.Find(r) is not null, Is.EqualTo(found));
    }

    [Test]
    public void AKeywordInTwoListsIsRefused()
    {
        string dir = TestSupport.TempDir();
        string q = TestSupport.WriteFile(dir, "q.toml", TestSupport.MinimalQuestion.Replace("[discover]", "[discover]\nwatch_keywords = [\"Type 2 Diabetes\"]"));
        Assert.That(() => PXReprise.Config.QuestionLoader.Load(q), Throws.TypeOf<PXReprise.Config.ConfigException>().With.Message.Contains("more than one"));
    }

    // Aging 030 asked whether validate reads [designs]: it lists the deposits with a design, and refuses a missing folder.
    [Test]
    public async Task ValidateListsTheQuestionsDesignsAndRefusesAMissingFolder()
    {
        string dir = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "designs"));
        TestSupport.WriteFile(Path.Combine(dir, "designs"), "PXD000201.sdrf.tsv", "source name\n");
        TestSupport.WriteFile(Path.Combine(dir, "designs"), "PXD000200.sdrf.tsv", "source name\n");
        string q = TestSupport.WriteFile(dir, "question.toml", TestSupport.MinimalQuestion + "\n[designs]\ndir = \"designs\"\n");
        var (exit, data) = await Run(new FakeSearch(), "validate", q, "--profiles", TestSupport.ProfilesDir);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(data.GetProperty("designs").GetProperty("deposits").EnumerateArray().Select(x => x.GetString()),
            Is.EqualTo(new[] { "PXD000200", "PXD000201" }));

        string bad = TestSupport.WriteFile(dir, "bad.toml", TestSupport.MinimalQuestion + "\n[designs]\ndir = \"nowhere\"\n");
        Assert.That((await Run(new FakeSearch(), "validate", bad, "--profiles", TestSupport.ProfilesDir)).Exit, Is.EqualTo(2), "a config error is a usage error");
        var plain = (await Run(new FakeSearch(), "validate", TestSupport.WriteFile(dir, "plain.toml", TestSupport.MinimalQuestion),
            "--profiles", TestSupport.ProfilesDir)).Data;
        Assert.That(plain.TryGetProperty("designs", out var none) && none.ValueKind != JsonValueKind.Null, Is.False, "no [designs]: none reported");
    }

    [Test]
    public async Task TheCensusRoutesEveryDepositAndWritesItsRecord()
    {
        var (s, q, outDir) = Setup();
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(s.Calls, Is.EqualTo(new[] { "type 2 diabetes", "insulin resistance" }));
        Assert.That(data.GetProperty("deposits").GetInt32(), Is.EqualTo(6));
        var routes = data.GetProperty("routes");
        Assert.Multiple(() =>
        {
            Assert.That(routes.GetProperty("search").GetInt32(), Is.EqualTo(2));                  // 100, 104
            Assert.That(routes.GetProperty("waiting_on_capability").GetInt32(), Is.EqualTo(2));   // 101 TMT, 102 DIA
            Assert.That(routes.GetProperty("out_of_scope").GetInt32(), Is.EqualTo(2));            // 103 mouse, 105 yeast
            Assert.That(data.GetProperty("waiting_on").GetProperty("tmt-dda@1").GetInt32(), Is.EqualTo(1));
            Assert.That(data.GetProperty("waiting_on").GetProperty("dia").GetInt32(), Is.EqualTo(1));
        });

        foreach (string f in new[] { "census.tsv", "summary.json", "provenance.json" })
            Assert.That(File.Exists(Path.Combine(outDir, f)), f);
        var tsv = File.ReadAllLines(Path.Combine(outDir, "census.tsv"));
        Assert.That(tsv, Has.Length.EqualTo(7));
        Assert.That(tsv.Single(l => l.StartsWith("PXD000100")), Does.Contain("insulin resistance;type 2 diabetes"));   // sorted, so the row is stable
        var prov = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "provenance.json"))).RootElement;
        Assert.That(prov.GetProperty("inputs")[0].GetProperty("sha256").GetString(), Has.Length.EqualTo(64));
        Assert.That(prov.GetProperty("tools").GetProperty("mzlib").GetString(), Does.StartWith("1.0.593"));
    }

    [Test]
    public async Task TheCensusWritesTheBatchQueueAndInstallsItOnlyWhenAsked()
    {
        var (s, q, outDir) = Setup();
        File.AppendAllText(q, "\n[batch]\nrun_root = \"runs\"\nstate_dir = \"batch\"\nqueue = \"batch/queue.json\"\n");
        string installed = Path.Combine(Path.GetDirectoryName(q)!, "batch", "queue.json");

        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(File.Exists(installed), Is.False, "without --queue the census only writes its own copy");
        var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json"))).RootElement;
        Assert.That(queue.EnumerateArray().Select(e => (e.GetProperty("accession").GetString(), e.GetProperty("organism").GetString())),
            Is.EqualTo(new[] { ("PXD000100", "human"), ("PXD000104", "human") }));
        Assert.That(data.GetProperty("queued").GetInt32(), Is.EqualTo(2));

        (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir + "2", "--queue");
        Assert.That((exit, data.GetProperty("queue_installed").GetString()), Is.EqualTo((0, installed)));
        var batchQueue = PXReprise.Batch.BatchRunner.LoadQueue(installed);   // the batch reads what the census wrote
        Assert.That(batchQueue.Select(e => e.Organism), Is.EqualTo(new[] { "human", "human" }));

        (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir + "3", "--queue");
        Assert.That(exit, Is.EqualTo(Envelope.ExitUsage), "an existing queue belongs to a batch and is never overwritten");
    }

    [Test]
    public async Task TheReviewCountsEachKeywordAndRuleAndShowsEveryExclusion()
    {
        var (s, q, outDir) = Setup();
        s.Results["insulin resistance"].Add(TestSupport.Record("PXD000106", "Type 1 diabetes and insulin resistance"));
        var (exit, _) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(0));
        string md = File.ReadAllText(Path.Combine(outDir, "review.md"));
        Assert.Multiple(() =>
        {
            Assert.That(md, Does.Contain("| found by the keywords | 7 |"));
            Assert.That(md, Does.Contain("| excluded (an `exclude_if_any` rule matched) | 1 |"));
            Assert.That(md, Does.Contain("| not relevant: organism not in `[discover] organisms` | 1 |"));   // the mouse deposit
            Assert.That(md, Does.Contain("| insulin resistance | 4 | 2 | 3 |"));   // found 100, 104, 105, 106; relevant 100, 104; only here 104, 105, 106
            Assert.That(md, Does.Contain("| exclude_if_any | `type (1|i) diabet` | 1 excluded |".Replace("|i)", "\\|i)")));
            Assert.That(md, Does.Contain("[PXD000106]").And.Contain("Type 1 diabetes and insulin resistance"), "every exclusion is listed");
            Assert.That(md, Does.Contain("Cell cycle in yeast"), "a keyword hit no rule matched is shown for checking");
        });
    }

    [Test]
    public async Task OrganismsTheCensusActsOnComeFromTheProjectRecordNotTheSearchIndex()
    {
        // PXD043476 (pride 001) and PXD068369: the search index carried SDRF headers as organisms, even FIRST.
        var (s, q, outDir) = Setup();
        s.Results["type 2 diabetes"][0] = TestSupport.Record("PXD000100", "Type 2 diabetes muscle",
            organisms: new[] { "Characteristics[age]", "Homo sapiens (human)" });
        s.Results["insulin resistance"][0] = s.Results["type 2 diabetes"][0];
        s.Projects["PXD000100"] = FakeSearch.Project("PXD000100", "Homo sapiens (human)");
        // The search says human; the record says mouse. The record wins: not in [discover] organisms.
        s.Projects["PXD000104"] = FakeSearch.Project("PXD000104", "Mus musculus (mouse)");

        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);

        Assert.That(exit, Is.EqualTo(0));
        var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json"))).RootElement;
        Assert.That(queue.EnumerateArray().Select(e => (e.GetProperty("accession").GetString(), e.GetProperty("organism").GetString(),
                e.GetProperty("organism_source").GetString())),
            Is.EqualTo(new[] { ("PXD000100", "human", "project") }));
        var tsv = File.ReadAllLines(Path.Combine(outDir, "census.tsv"));
        var header = tsv[0].Split('	');
        string[] Row(string acc) => tsv.Single(l => l.StartsWith(acc)).Split('	');
        string Col(string acc, string col) => Row(acc)[Array.IndexOf(header, col)];
        Assert.Multiple(() =>
        {
            Assert.That(Col("PXD000100", "organisms"), Is.EqualTo("Characteristics[age];Homo sapiens (human)"), "the search snapshot is kept as found");
            Assert.That(Col("PXD000100", "organisms_of_record"), Is.EqualTo("Homo sapiens (human)"));
            Assert.That(Col("PXD000104", "relevance"), Is.EqualTo("not_relevant"));
            Assert.That(Col("PXD000104", "organism_source"), Is.EqualTo("project"));
            Assert.That(Col("PXD000105", "organism_source"), Is.EqualTo("search"), "an out-of-scope deposit costs no record fetch");
        });
        Assert.That(s.ProjectCalls, Does.Not.Contain("PXD000105"));
        Assert.That(s.ProjectCalls, Is.Unique);
    }

    [Test]
    public async Task AnUnavailableProjectRecordFallsBackToTheCleanedSearchIndexAndSaysSo()
    {
        var (s, q, outDir) = Setup();
        s.Results["insulin resistance"][1] = TestSupport.Record("PXD000104", "Insulin resistance in adipose",
            organisms: new[] { "Characteristics[cell type]", "Homo sapiens (human)" });
        // .NET's real shape for an outage after mzLib's status check: an HttpRequestException carrying the status.
        s.ProjectFailures["PXD000104"] = new HttpRequestException("PRIDE failed with status 503", null, HttpStatusCode.ServiceUnavailable);

        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);

        Assert.That(exit, Is.EqualTo(0));
        var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json"))).RootElement;
        Assert.That(queue.EnumerateArray().Select(e => (e.GetProperty("accession").GetString(), e.GetProperty("organism_source").GetString())),
            Is.EqualTo(new[] { ("PXD000100", "search_fallback"), ("PXD000104", "search_fallback") }), "never dropped");
        Assert.That(File.ReadAllLines(Path.Combine(outDir, "census.tsv")).Single(l => l.StartsWith("PXD000104")),
            Does.EndWith("	Homo sapiens (human)	search_fallback		"), "SDRF headers are removed from the fallback (watch and reference_group follow, empty)");
        Assert.That(data.GetProperty("organism_sources").GetProperty("search_fallback").GetInt32(), Is.EqualTo(5));   // every relevant deposit: 100-104; this fake has no records
        Assert.That(File.ReadAllText(Path.Combine(outDir, "provenance.json")), Does.Contain("PXD000104 organisms from the search index (project record unavailable"));
    }

    [Test]
    public async Task ABrokenProjectRecordFailsTheCensus()
    {
        var (s, q, outDir) = Setup();
        s.ProjectFailures["PXD000100"] = new MzLibUtil.MzLibException("PRIDE answered with an empty project");
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(Envelope.ExitFailure));
        Assert.That(data.GetProperty("error").GetProperty("type").GetString(), Is.EqualTo("MzLibException"));
    }

    [TestCase("Characteristics[organism]", true)]
    [TestCase("comment[technical replicate]", true)]
    [TestCase("Factor Value[disease]", true)]
    [TestCase("Homo sapiens (human)", false)]
    [TestCase("Nt=oxidation", false)]
    public void SdrfHeadersAreRecognised(string term, bool header) =>
        Assert.That(CensusRunner.IsSdrfHeader(term), Is.EqualTo(header));

    [TestCase("Homo sapiens (human)", "human", true)]
    [TestCase("Mus musculus (mouse)", "mouse", true)]
    [TestCase("Homo sapiens (human)", "homo_sapiens", true)]
    [TestCase("Mus musculus (mouse)", "human", false)]
    [TestCase("Rattus norvegicus (rat)", "ra", false)]
    public void APrideOrganismMatchesAProfileDatabaseKey(string pride, string key, bool match) =>
        Assert.That(CensusRunner.OrganismMatches(pride, key), Is.EqualTo(match));

    [Test]
    public async Task AnUnavailableKeywordIsReportedNotSilentlyDropped()
    {
        var (s, q, outDir) = Setup();
        s.Failures["insulin resistance"] = new HttpRequestException("PRIDE failed with status 503", null, HttpStatusCode.ServiceUnavailable);
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(data.GetProperty("failed_keywords")[0].GetString(), Is.EqualTo("insulin resistance"));
        Assert.That(data.GetProperty("deposits").GetInt32(), Is.EqualTo(4));
    }

    [Test]
    public async Task AContractBreakFailsTheCensusRatherThanPassingAsAnOutage()
    {
        var (s, q, outDir) = Setup();
        s.Failures["insulin resistance"] = new MzLibUtil.MzLibException("PRIDE returned an identical page twice");
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(Envelope.ExitFailure));
        Assert.That(data.GetProperty("error").GetProperty("type").GetString(), Is.EqualTo("MzLibException"));
    }

    [Test]
    public async Task AQuestionNamingAnUnknownProfileIsAUsageError()
    {
        var (s, q, outDir) = Setup();
        File.WriteAllText(q, File.ReadAllText(q).Replace("tmt-dda@1", "tmt-dda@9"));
        var (exit, data) = await Run(s, "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir);
        Assert.That(exit, Is.EqualTo(Envelope.ExitUsage));
        Assert.That(data.GetProperty("error").GetProperty("message").GetString(), Does.Contain("tmt-dda@9"));
        Assert.That(s.Calls, Is.Empty, "no PRIDE call is made before the question is known to be valid");
    }

    [Test]
    public async Task VersionListsEveryVerbAndTheMzLibPin()
    {
        var (exit, data) = await Run(new FakeSearch(), "version");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(data.GetProperty("verbs").EnumerateArray().Select(v => v.GetString()), Is.EqualTo(Program.Verbs));
        Assert.That(data.GetProperty("tools").GetProperty("mzlib").GetString(), Does.StartWith("1.0.593"));
    }

    [TestCase(new string[0], "a verb is required")]
    [TestCase(new[] { "census" }, "1 positional")]
    [TestCase(new[] { "version", "--frobnicate", "x" }, "unknown option")]
    [TestCase(new[] { "version", "--profiles" }, "needs a value")]
    public async Task UsageErrorsExitTwo(string[] argv, string expected)
    {
        var (exit, data) = await Run(new FakeSearch(), argv);
        Assert.That(exit, Is.EqualTo(Envelope.ExitUsage));
        Assert.That(data.GetProperty("error").GetProperty("message").GetString(), Does.Contain(expected));
    }

    [Test]
    public void UnavailabilityIsOnly408And429And5xx()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Envelope.IsUnavailable(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)), Is.True);
            Assert.That(Envelope.IsUnavailable(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)), Is.True);
            Assert.That(Envelope.IsUnavailable(new HttpRequestException("x", null, HttpStatusCode.RequestTimeout)), Is.True);
            Assert.That(Envelope.IsUnavailable(new HttpRequestException("x", null, HttpStatusCode.Forbidden)), Is.False);
            Assert.That(Envelope.IsUnavailable(new MzLibUtil.MzLibException("contract")), Is.False);
        });
    }

    [Test, Category("ExternalService")]
    public async Task LivePrideSearchReturnsProjects() =>
        await ExternalServiceTestHelper.RunAsync("PRIDE", async () =>
        {
            using var client = new PrideArchiveClient();
            var hits = await new PrideProjectSearch(client, attempts: 1).SearchAsync("SCoPE2", CancellationToken.None);
            Assert.That(hits, Is.Not.Empty);
            Assert.That(hits.All(h => h.Accession.StartsWith("PXD")), Is.True);
        });
}
