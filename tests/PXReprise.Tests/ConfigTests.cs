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
        Assert.That(profiles.Keys, Is.EquivalentTo(new[] { "label-free-dda@1", "label-free-dda@2", "label-free-dda@3", "label-free-dda@4", "tmt-dda@1" }));

        // D28: @4 is @3 on MetaMorpheus 1.1.12 with an SDRF design, the search's own SDRF and the agingPTM-v2 databases.
        var v3x = profiles["label-free-dda@3"];
        var v4 = profiles["label-free-dda@4"];
        Assert.That((v4.MetaMorpheus, v4.Design, v4.WriteSdrf, v4.Chemistry), Is.EqualTo(("1.1.12", "sdrf", true, "deposit")));
        Assert.That(v4.Databases.Values.Select(d => d.Proteome), Is.All.EndWith("_agingPTM-v2.xml"));
        Assert.That(v4 with { Version = 3, MetaMorpheus = "1.1.11", Design = "none", WriteSdrf = false, TimeoutHours = v3x.TimeoutHours,
            Description = v3x.Description, Databases = v3x.Databases, Accepts = v3x.Accepts, Tasks = v3x.Tasks,
            GptmdExtraMods = v3x.GptmdExtraMods, Deposit = v3x.Deposit, Qc = v3x.Qc, SourceDir = v3x.SourceDir },
            Is.EqualTo(v3x), "every other scalar setting equals /3's");
        Assert.That((v4.Deposit, v4.Qc with { Excludable = v3x.Qc.Excludable }, v4.Tasks), Is.EqualTo((v3x.Deposit, v3x.Qc, v3x.Tasks)));

        // G19: @3 is @1 with the deposit's own chemistry, and nothing else; @1 keeps MetaMorpheus's defaults.
        var v1 = profiles["label-free-dda@1"];
        var v3 = profiles["label-free-dda@3"];
        Assert.That((v1.Chemistry, v3.Chemistry), Is.EqualTo(("fixed", "deposit")));
        Assert.That(v3 with { Version = 1, Chemistry = "fixed", Description = v1.Description, Databases = v1.Databases, Accepts = v1.Accepts,
            Tasks = v1.Tasks, GptmdExtraMods = v1.GptmdExtraMods, Deposit = v1.Deposit, Qc = v1.Qc },
            Is.EqualTo(v1), "every scalar setting but chemistry equals /1's");
        Assert.That(v3.Databases.Keys, Is.EquivalentTo(v1.Databases.Keys));
        Assert.That(v3.Tasks, Is.EqualTo(v1.Tasks));
        Assert.That(v3.Deposit, Is.EqualTo(v1.Deposit));
        Assert.That(v3.Qc with { Excludable = v1.Qc.Excludable }, Is.EqualTo(v1.Qc));

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
    public void TheGenericProfileIsV1WithUniProtDatabases()
    {
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (v1, v2) = (profiles["label-free-dda@1"], profiles["label-free-dda@2"]);
        Assert.Multiple(() =>
        {
            Assert.That(v2.Databases["human"].UniProt, Is.EqualTo("UP000005640"));
            Assert.That(v2.Databases.Values.All(d => d.Proteome is null), Is.True);
            // Everything but the databases is /1's, so the two versions' results stay comparable.
            Assert.That(SameSearch(v1, v2), Is.True);
        });
    }

    private static bool SameSearch(Profile a, Profile b) =>
        a.MetaMorpheus == b.MetaMorpheus && a.Tasks.SequenceEqual(b.Tasks) && a.GptmdExtraMods.SequenceEqual(b.GptmdExtraMods)
        && a.SpectralLibrary == b.SpectralLibrary && a.TimeoutHours == b.TimeoutHours && a.ContaminantPanel == b.ContaminantPanel
        && a.ContaminantExclude == b.ContaminantExclude && a.QuantMethod == b.QuantMethod && a.MatchBetweenRuns == b.MatchBetweenRuns
        && a.Deposit == b.Deposit && a.Qc.Ms2Analyzer == b.Qc.Ms2Analyzer && a.Qc.MinMs2 == b.Qc.MinMs2
        && a.Qc.MinFractionOrbitrapHcd == b.Qc.MinFractionOrbitrapHcd && a.Qc.Excludable.SequenceEqual(b.Qc.Excludable)
        && a.Qc.MaxExcludedFraction == b.Qc.MaxExcludedFraction && a.Accepts.Modes.SequenceEqual(b.Accepts.Modes)
        && a.Accepts.Labellings.SequenceEqual(b.Accepts.Labellings) && a.Accepts.Instruments.SequenceEqual(b.Accepts.Instruments);

    [TestCase("proteome = \"a.xml\"\nuniprot = \"UP000005640\"", "exactly one of")]
    [TestCase("taxon = 9606", "exactly one of")]
    [TestCase("uniprot = \"human\"", "UniProt proteome ID")]
    public void AnOrganismDatabaseNamesExactlyOneValidSource(string table, string expected)
    {
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-2.toml")).ReplaceLineEndings("\n")
            .Replace("uniprot = \"UP000005640\"\ntaxon = 9606", table);
        string dir = TestSupport.TempDir();
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml", text)));
        Assert.That(e!.Message, Does.Contain(expected));
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
        // A Windows checkout (core.autocrlf) gives the raw string CRLF endings.
        string text = TestSupport.MinimalQuestion.ReplaceLineEndings("\n").Replace("exclude_if_any = [\"type (1|i) diabet\"]\n", "");
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
