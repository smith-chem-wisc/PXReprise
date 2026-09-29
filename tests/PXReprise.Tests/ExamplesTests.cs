using PXReprise.Config;

namespace PXReprise.Tests;

/// <summary>What the documentation tells a new user to run must load: every example question and the machine template.</summary>
public class ExamplesTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PXReprise.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above the test directory");
    }

    [TestCase("first-run")]
    [TestCase("t2d")]
    [TestCase("muscle-ageing")]
    public void AnExampleQuestionLoadsAndUsesOnlyShippedProfiles(string example)
    {
        var q = QuestionLoader.Load(Path.Combine(RepoRoot(), "examples", example, "question.toml"));
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(q.Profiles, Is.SubsetOf(profiles.Keys));
        Assert.That(q.Profiles, Does.Not.Contain("label-free-dda@1"), "examples must run on any machine: @1 needs aging's databases");
        Assert.That(q.Batch!.Queue, Does.StartWith(Path.Combine(RepoRoot(), "examples", example)), "batch paths are relative to the example");
    }

    [Test]
    public void TheFirstRunQueueIsReadableByTheBatch() =>
        Assert.That(Batch.BatchRunner.LoadQueue(Path.Combine(RepoRoot(), "examples", "first-run", "queue.json")).Single(),
            Is.EqualTo(new Batch.QueueEntry("PXD058082", "Installation check deposit (PXReprise first run)", "mouse")));

    [Test]
    public void TheMachineTemplateLoads()
    {
        var m = Machine.Load(Path.Combine(RepoRoot(), "machines", "example.toml"));
        Assert.Multiple(() =>
        {
            Assert.That(m.CmdFor("1.1.11"), Does.EndWith("CMD.dll"));
            Assert.That(m.AcceptThermoLicence, Is.False, "the licence is the operator's to accept, never the template's");
            Assert.That(m.DataRepo, Is.Null);
            Assert.That(m.QcPython, Is.Null);
        });
    }
}
