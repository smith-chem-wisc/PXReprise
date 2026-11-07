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
    [TestCase("muscle-ageing")]
    public void AnExampleQuestionLoadsAndUsesOnlyShippedProfiles(string example)
    {
        var q = QuestionLoader.Load(Path.Combine(RepoRoot(), "examples", example, "question.toml"));
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(q.Profiles, Is.SubsetOf(profiles.Keys));
        Assert.That(q.Profiles, Does.Not.Contain("label-free-dda@1"), "examples must run on any machine: @1 needs aging's databases");
        Assert.That(q.Batch!.Queue, Does.StartWith(Path.Combine(RepoRoot(), "examples", example)), "batch paths are relative to the example");
    }

    /// <summary>
    /// The user, 2026-11-07: "point the public guide to the new profile". A new installation runs label-free-dda-uniprot@1, and
    /// the guide installs the one MetaMorpheus release it names. So every runnable profile an example names must download its
    /// own databases, and the machine template must name its release: an example naming label-free-dda@2 (MetaMorpheus 1.1.11)
    /// would stop a new user with "names no MetaMorpheus 1.1.11".
    /// </summary>
    [TestCase("first-run")]
    [TestCase("muscle-ageing")]
    public void AnExampleRunsTheNewInstallationProfileOnTheTemplatesMetaMorpheus(string example)
    {
        var q = QuestionLoader.Load(Path.Combine(RepoRoot(), "examples", example, "question.toml"));
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var m = Machine.Load(Path.Combine(RepoRoot(), "machines", "example.toml"));
        Assert.That(q.Profiles[0], Is.EqualTo("label-free-dda-uniprot@1"));
        foreach (var name in q.Profiles.Where(n => profiles[n].Status == ProfileStatus.Available))
        {
            var p = profiles[name];
            Assert.That(p.Databases.Values.Select(d => (d.UniProt is not null, d.Proteome is null)), Has.All.EqualTo((true, true)),
                $"{name} must download its own databases");
            Assert.DoesNotThrow(() => m.CmdFor(p.MetaMorpheus), $"the machine template must name MetaMorpheus {p.MetaMorpheus} for {name}");
        }
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
            Assert.That(m.CmdFor("1.1.13"), Does.EndWith("CMD.dll"), "the release label-free-dda-uniprot@1 names");
            Assert.That(m.AcceptThermoLicence, Is.False, "the licence is the operator's to accept, never the template's");
            Assert.That(m.DataRepo, Is.Null);
            Assert.That(m.QcPython, Is.Null);
        });
    }
}
