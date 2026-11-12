using System.Text.Json.Nodes;
using PXReprise.Census;
using PXReprise.Config;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.46 (aging PXR-A179, their D171; their user: "it should be part of the code, not part of the project AI"): every deposit
/// the census would queue passes the question's gate first, rules over its own text. Aging's free screen of 2026-11-09 found 87
/// deposits queued or served that never should have been; 19 of 40 matched only on a lab's institute.
/// </summary>
public class GateTests
{
    private static string Dir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "gate");

    private static Question AgingGate => QuestionLoader.Load(Path.Combine(Dir, "aging-gate.toml"));

    private static GateText Fixture(string acc)
    {
        var o = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, acc + ".json")))!;
        return new GateText(o["source"]!.GetValue<string>(),
            o["fields"]!.AsObject().Select(kv => (kv.Key, kv.Value!.GetValue<string>())).ToList());
    }

    private static GateText Text(string title, string description = "", string protocol = "", string keywords = "") =>
        new(GateTextSource.Project, new List<(string, string)> { ("title", title), ("description", description), ("sample_protocol", protocol), ("keywords", keywords) });

    // Part C: aging's 59 deposits of 2026-11-09, each with the outcome their terms must give. "not_queue" is not_queued or watch.
    // disagreements.tsv: the cases where aging's terms as written give another outcome, reported to aging (they adjust the terms,
    // not the code). Each is pinned to the outcome it gives now, so a change to the terms or the code shows here.
    [Test]
    public void AgingsDepositsOfTheNinthGetTheOutcomesAgingExpects()
    {
        var gate = AgingGate.Gate!;
        var wrong = new List<string>();
        var rows = File.ReadAllLines(Path.Combine(Dir, "expected.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();
        var pinned = File.ReadAllLines(Path.Combine(Dir, "disagreements.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t'))
            .ToDictionary(c => c[0], c => c[1], StringComparer.Ordinal);
        Assert.That(rows, Has.Count.EqualTo(59));
        Assert.That(pinned, Has.Count.EqualTo(4));
        foreach (var r in rows)
        {
            var d = Gate.Evaluate(gate, Fixture(r[0]), noReferenceGroup: false);
            if (pinned.TryGetValue(r[0], out string? now))
            {
                if (d.Outcome != now) wrong.Add($"{r[0]}: pinned to {now} (a disagreement reported to aging), now {d.Outcome} ({d.Reason})");
                continue;
            }
            bool ok = r[1] switch
            {
                "queue" => d.Outcome == GateOutcome.Queue,
                "watch" => d.Outcome == GateOutcome.Watch,
                "not_queue" => d.Outcome != GateOutcome.Queue,
                _ => throw new InvalidDataException(r[1]),
            };
            if (!ok) wrong.Add($"{r[0]}: expected {r[1]}, got {d.Outcome} ({d.Reason}); matched {string.Join(", ", d.Matches.Select(m => $"{m.Class}:{m.Term}@{m.Field}"))}");
        }
        Assert.That(wrong, Is.Empty, string.Join("\n", wrong));
    }

    [Test]
    public void TermsMatchWholeWordsWithAnyDashAndSpacing()
    {
        var gate = AgingGate.Gate!;
        Assert.That(Gate.Evaluate(gate, Text("A threshold for bold peptides"), false).Outcome, Is.EqualTo(GateOutcome.NotQueued), "'old' inside a word is not 'old'");
        Assert.That(Gate.Evaluate(gate, Text("Liver of 3-month-old and 24 month old mice"), false).Outcome, Is.EqualTo(GateOutcome.Queue));
        var dash = Gate.Evaluate(gate, Text("Age–related   changes in the kidney"), false);
        Assert.That((dash.Outcome, dash.Matches[0].Term), Is.EqualTo((GateOutcome.Queue, "age-related")), "an en dash and three spaces read as one hyphen and one space");
    }

    [Test]
    public void AStrongTermInAnotherSenseACancerModelOrOnlyAWeakWordIsWatched()
    {
        var gate = AgingGate.Gate!;
        var graft = Gate.Evaluate(gate, Text("Proteomics of renal allograft longevity"), false);
        Assert.That((graft.Outcome, graft.Reason), Is.EqualTo((GateOutcome.Watch, "a term in another sense ('(graft|allograft|implant|catheter|device|battery|material) (longevity|life ?span)' in title)")));
        Assert.That(Gate.Evaluate(gate, Text("Senescence in breast cancer cells"), false).Reason, Does.StartWith("watch_if_any 'cancer'"));
        var weak = Gate.Evaluate(gate, Text("A study at a center for aging research"), false);
        Assert.That((weak.Outcome, weak.Reason), Is.EqualTo((GateOutcome.Watch, "only weak terms (aging)")));
        var later = Gate.Evaluate(gate, Text("Huntington's disease striatum"), false);
        Assert.That((later.Outcome, later.Reason), Is.EqualTo((GateOutcome.Watch, "watch_without_strong 'Huntington\\w*' in title, and no strong term")),
            "a later disease is watched even with no aging word (aging D70; their fixture PXD005753)");
        Assert.That(Gate.Evaluate(gate, Text("Glioma cell lines"), false).Outcome, Is.EqualTo(GateOutcome.NotQueued), "a cancer term alone is no aging factor");
        Assert.That(Gate.Evaluate(gate, Text("Alzheimer's disease cortex"), noReferenceGroup: true).Reason,
            Does.Contain("no reference group (PXR-A27)"), "found only by disease keywords, with no control group named");
    }

    // Part A1: never the submitters, lab heads, affiliations or countries; reference lines are read for their titles only.
    [Test]
    public void ContactsAndAuthorsAreNeverRead()
    {
        var project = new PrideProject
        {
            Title = "Phosphoproteome of mouse liver", ProjectDescription = "Liver lysates.",
            LabPIs = { new PrideContact { Name = "Anne Young", Affiliation = "Max Planck Institute for Biology of Ageing" } },
            Countries = { "Germany" },
            References = { new PrideReference { ReferenceLine = "Young A, Old B, Smith C. Phosphorylation in the liver. J Proteome Res. 2020;19(1):1-10" } },
        };
        var text = GateText.From(project);
        Assert.That(text.Fields.Single(f => f.Field == "reference_titles").Text, Is.EqualTo("Phosphorylation in the liver"));
        Assert.That(Gate.Evaluate(AgingGate.Gate!, text, false).Outcome, Is.EqualTo(GateOutcome.NotQueued),
            "not the lab's institute ('Ageing'), not the authors Young and Old");
        Assert.That(Gate.ReferenceTitle("Keele GR, Zhang JG, Gygi SP. Global and tissue-specific aging effects on murine proteomes. Cell Rep. 2023"),
            Is.EqualTo("Global and tissue-specific aging effects on murine proteomes"));
        Assert.That(Gate.ReferenceTitle("Keele, G. R. Global aging effects. Cell Rep."), Is.Empty, "an author list it cannot tell apart gives nothing");

        const string xml = """
            <ProteomeXchangeDataset><DatasetSummary title="Liver lysates"><Description>Phosphopeptides.</Description></DatasetSummary>
            <ContactList><Contact><cvParam name="contact affiliation" value="Leibniz Institute on Aging"/></Contact></ContactList>
            <KeywordList><cvParam name="submitter keyword" value="phosphorylation"/></KeywordList></ProteomeXchangeDataset>
            """;
        var px = GateText.FromProteomeXchange(xml);
        Assert.That((px.Source, Gate.Evaluate(AgingGate.Gate!, px, false).Outcome), Is.EqualTo((GateTextSource.ProteomeXchange, GateOutcome.NotQueued)));
    }

    // The census queues only what the gate lets through; the rest is recorded in gate.tsv, and a watch goes to watch.tsv.
    [Test]
    public async Task TheCensusQueuesOnlyWhatTheGateLetsThrough()
    {
        var s = new FakeSearch();
        s.Results["type 2 diabetes"] = new()
        {
            TestSupport.Record("PXD000400", "Type 2 diabetes muscle of aged mice"),
            TestSupport.Record("PXD000401", "Type 2 diabetes muscle"),
            TestSupport.Record("PXD000402", "Type 2 diabetes in the aging muscle"),
        };
        // Its lab is an ageing institute, which the census's PRIDE search matched; the gate never reads it.
        var p401 = FakeSearch.Project("PXD000401", "Homo sapiens (human)");
        p401.Title = "Type 2 diabetes muscle";
        p401.LabPIs.Add(new PrideContact { Name = "A Lab Head", Affiliation = "Leibniz Institute on Aging" });
        s.Projects["PXD000401"] = p401;
        string dir = TestSupport.TempDir();
        string q = TestSupport.WriteFile(dir, "question.toml", TestSupport.MinimalQuestion + "\n[gate]\nstrong = [\"aged\"]\nweak = [\"aging\"]\n");
        string outDir = Path.Combine(dir, "out");
        var sw = new StringWriter();
        int exit = await Program.RunAsync(new[] { "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir }, sw, CancellationToken.None, () => s);
        Assert.That(exit, Is.EqualTo(0), sw.ToString());

        var queued = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json")))!.AsArray().Select(x => x!["accession"]!.GetValue<string>()).ToList();
        Assert.That(queued, Is.EqualTo(new[] { "PXD000400" }));
        var gate = File.ReadAllLines(Path.Combine(outDir, "gate.tsv")).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0], c => (c[1], c[3]));
        Assert.That(gate, Is.EqualTo(new Dictionary<string, (string, string)>
        {
            ["PXD000400"] = (GateOutcome.Queue, GateTextSource.SearchIndex),     // no project record in the fake: the index's text
            ["PXD000401"] = (GateOutcome.NotQueued, GateTextSource.Project),
            ["PXD000402"] = (GateOutcome.Watch, GateTextSource.SearchIndex),
        }));
        Assert.That(File.ReadAllLines(Path.Combine(outDir, "watch.tsv")).Skip(1).Select(l => l.Split('\t')[0]), Is.EqualTo(new[] { "PXD000402" }));
        var summary = JsonNode.Parse(sw.ToString())!["data"]!["gate"]!.AsObject();
        Assert.That(summary.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>()),
            Is.EquivalentTo(new Dictionary<string, int> { [GateOutcome.Queue] = 1, [GateOutcome.NotQueued] = 1, [GateOutcome.Watch] = 1 }));
    }

    // Aging PXR-A180 (the user, 2026-11-11): a hand include in the question's decisions file is a person's reading, so the gate
    // is not applied; aging's 78 hand-read DIA deposits are queued even where a word in passing would send them to the watch list.
    [Test]
    public async Task AHandIncludeIsQueuedWithoutTheGate()
    {
        var s = new FakeSearch();
        s.Results["type 2 diabetes"] = new()
        {
            TestSupport.Record("PXD000410", "Type 2 diabetes muscle of aged mice, and age-related diseases such as cancer"),
            TestSupport.Record("PXD000411", "Type 2 diabetes muscle of aged mice, and age-related diseases such as cancer"),
        };
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "decisions.tsv", "accession\tverdict\treason\nPXD000410\tinclude\tread by aging's user (D181)\n");
        string q = TestSupport.WriteFile(dir, "question.toml", TestSupport.MinimalQuestion.Replace("[relevance]", "[relevance]\ndecisions = \"decisions.tsv\"")
            + "\n[gate]\nstrong = [\"aged\"]\nwatch_if_any = [\"cancer\"]\n");
        string outDir = Path.Combine(dir, "out");
        var sw = new StringWriter();
        int exit = await Program.RunAsync(new[] { "census", q, "--profiles", TestSupport.ProfilesDir, "--out", outDir }, sw, CancellationToken.None, () => s);
        Assert.That(exit, Is.EqualTo(0), sw.ToString());

        var queued = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "queue.json")))!.AsArray().Select(x => x!["accession"]!.GetValue<string>()).ToList();
        Assert.That(queued, Is.EqualTo(new[] { "PXD000410" }));
        var gate = File.ReadAllLines(Path.Combine(outDir, "gate.tsv")).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0], c => (c[1], c[2]));
        Assert.That(gate["PXD000410"], Is.EqualTo((GateOutcome.Queue, "a hand decision includes it (read by aging's user (D181)); the gate is not applied")));
        Assert.That(gate["PXD000411"].Item1, Is.EqualTo(GateOutcome.Watch));
    }

    [Test]
    public void AQuestionsGateIsCheckedWhenItIsRead()
    {
        string dir = TestSupport.TempDir();
        string Q(string gate) => TestSupport.WriteFile(dir, $"q{Guid.NewGuid():N}.toml",
            "question = \"q\"\nprofiles = [\"label-free-dda@14\"]\n[discover]\nkeywords = [\"aging\"]\n[gate]\n" + gate);
        Assert.That(QuestionLoader.Load(Q("strong = [\"aged\"]\n")).Gate!.ReferenceGroupRequired, Is.True, "required unless the question says off");
        Assert.That(QuestionLoader.Load(Q("strong = [\"aged\"]\nreference_group = \"off\"\n")).Gate!.ReferenceGroupRequired, Is.False);
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("strong = []\n")), "no strong term queues nothing");
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("strong = [\"(unclosed\"]\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("strong = [\"aged\"]\nreference_group = \"maybe\"\n")));
        Assert.Throws<ConfigException>(() => QuestionLoader.Load(Q("strong = [\"aged\"]\nstrongg = [\"old\"]\n")), "an unknown key is refused");
        Assert.That(QuestionLoader.Load(Q("strong = [\"aged\"]\n")).Gate!.Strong[0].Pattern.IsMatch("aged mice"), Is.True);
    }
}
