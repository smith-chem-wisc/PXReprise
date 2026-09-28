using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Discovery;

namespace PXReprise.Tests;

public class ConfigTests
{
    [Test]
    public void ShippedProfilesLoadWithTheAgingV1Settings()
    {
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(profiles.Keys, Is.EquivalentTo(new[] { "label-free-dda@1", "tmt-dda@1" }));

        var lf = profiles["label-free-dda@1"];
        Assert.Multiple(() =>
        {
            Assert.That(lf.Status, Is.EqualTo(ProfileStatus.Available));
            Assert.That(lf.MetaMorpheus, Is.EqualTo("1.1.11"));
            Assert.That(lf.Tasks, Is.EqualTo(new[] { "Calibration", "Gptmd", "Search" }));
            Assert.That(lf.GptmdExtraMods, Is.EqualTo(new[] { "Trypsin Digested\tGG (Ubiquitination Site) on K" }));
            Assert.That(lf.MatchBetweenRuns, Is.True);
            Assert.That(lf.Deposit.MaxFiles, Is.EqualTo(60));
            Assert.That(lf.Deposit.MaxEstimatedMs2, Is.EqualTo(1_500_000));
            Assert.That(lf.Qc.MinMs2, Is.EqualTo(5000));
            Assert.That(lf.Qc.MinFractionOrbitrapHcd, Is.EqualTo(0.9));
            Assert.That(lf.Databases.Keys, Is.EquivalentTo(new[] { "human", "mouse", "rat" }));
            Assert.That(lf.Databases["human"].Proteome, Does.EndWith("_agingPTM-v1.xml"));
            Assert.That(lf.Accepts.Instruments, Does.Not.Contain(InstrumentClass.Astral));
        });
        Assert.That(profiles["tmt-dda@1"].Status, Is.EqualTo(ProfileStatus.Pending));
    }

    [Test]
    public void AMisspeltProfileKeyIsRefusedNotIgnored()
    {
        string dir = TestSupport.TempDir();
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-1.toml"))
            .Replace("mbr = true", "mbr = true\nmatch_between_run = true");
        TestSupport.WriteFile(dir, "p.toml", text);
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(Path.Combine(dir, "p.toml")));
        Assert.That(e!.Message, Does.Contain("'quant.match_between_run'"));
    }

    [Test]
    public void AnUnknownEnumValueNamesTheAllowedOnes()
    {
        string dir = TestSupport.TempDir();
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-1.toml"))
            .Replace("labelling = [\"label_free\"]", "labelling = [\"labelfree\"]");
        TestSupport.WriteFile(dir, "p.toml", text);
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(Path.Combine(dir, "p.toml")));
        Assert.That(e!.Message, Does.Contain("label_free").And.Contain("isobaric"));
    }

    [Test]
    public void AQuestionLoadsAsData()
    {
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "decisions.tsv", "accession\tverdict\treason\nPXD000001\texclude\tstreptozotocin model, not T2D\n");
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml",
            TestSupport.MinimalQuestion.Replace("unless_any = [\"type (2|ii) diabet\"]",
                "unless_any = [\"type (2|ii) diabet\"]\ndecisions = \"decisions.tsv\"")
            + "\n[holds]\nPXD000002 = \"waits for the authors\"\n[overlays.human]\nextra_xml = [\"iso.xml\"]\n"));
        Assert.Multiple(() =>
        {
            Assert.That(q.Name, Is.EqualTo("t2d"));
            Assert.That(q.Profiles, Is.EqualTo(new[] { "label-free-dda@1", "tmt-dda@1" }));
            Assert.That(q.Relevance.Decisions["PXD000001"].Verdict, Is.EqualTo(DecisionVerdict.Exclude));
            Assert.That(q.Holds["PXD000002"], Is.EqualTo("waits for the authors"));
            Assert.That(q.Overlays["human"], Is.EqualTo(new[] { "iso.xml" }));
        });
    }

    [TestCase("profiles = [\"label-free-dda\"]", "id@version")]
    [TestCase("question = \"T2D\"", "lower-case")]
    [TestCase("require_any = [\"(unclosed\"]", "not a valid regex")]
    public void AnInvalidQuestionSaysWhatIsWrong(string replacement, string expected)
    {
        string key = replacement.Split(" = ")[0];
        string text = string.Join("\n", TestSupport.MinimalQuestion.Split('\n')
            .Select(l => l.TrimStart().StartsWith(key + " =") ? replacement : l));
        string dir = TestSupport.TempDir();
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "q.toml", text)));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void UnlessWithoutExcludeIsRefused()
    {
        string text = TestSupport.MinimalQuestion.Replace("exclude_if_any = [\"type (1|i) diabet\"]\n", "");
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(TestSupport.TempDir(), "q.toml", text)));
        Assert.That(e!.Message, Does.Contain("unless_any"));
    }

    [Test]
    public void ADecisionWithoutAReasonIsRefused()
    {
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "d.tsv", "accession\tverdict\treason\nPXD000001\tinclude\t \n");
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.LoadDecisions(Path.Combine(dir, "d.tsv")));
        Assert.That(e!.Message, Does.Contain("needs a reason"));
    }

    [Test]
    public void AConfigErrorIsAUsageError()
    {
        Assert.That(Envelope.Classify(new ConfigException("q.toml", "bad")), Is.EqualTo(("usage", Envelope.ExitUsage)));
    }
}
