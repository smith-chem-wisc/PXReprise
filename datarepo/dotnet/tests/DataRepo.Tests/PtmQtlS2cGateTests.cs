using DataRepo.Runner;

namespace DataRepo.Tests;

/// <summary>ptmQtl D60 (DEFINITIONS S2c): where S2c is defined, where it is refused, and when it lifts, from the task
/// files a bundle keeps.</summary>
public class PtmQtlS2cGateTests
{
    private static string Task(string protease = "trypsin", string mode = "Full", bool? respects = null) =>
        "TaskType = \"Search\"\n[CommonParameters.DigestionParams]\n"
        + $"SearchModeType = \"{mode}\"\nSpecificProtease = \"{protease}\"\nProtease = \"{protease}\"\n"
        + (respects is { } r ? $"RespectCleavageBlockingModifications = {(r ? "true" : "false")}\n" : "");

    [Test]
    public void TrypsinFullySpecificBeforeTheLiftApplies()
    {
        var gate = PtmQtlRules.Gate([Task(), Task(), Task()], "1.1.12");
        Assert.That((gate.Refusal, gate.Lifted, gate.CutResidues), Is.EqualTo(((string?)null, false, "KR")));
    }

    [Test]
    public void ItLiftsOnlyWhenEveryTaskRespectsBlockingOnARecentRelease()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PtmQtlRules.Gate([Task(respects: true), Task(respects: true)], "1.1.13").Lifted, Is.True);
            Assert.That(PtmQtlRules.Gate([Task(), Task(respects: true)], "1.1.13").Lifted, Is.False, "the calibration task did not respect it");
            Assert.That(PtmQtlRules.Gate([Task(respects: true), Task(respects: false)], "1.1.13").Lifted, Is.False);
            Assert.That(PtmQtlRules.Gate([Task(respects: true)], "1.1.12").Lifted, Is.False, "1.1.12 has no such digestion");
            Assert.That(PtmQtlRules.Gate([Task(respects: true)], null).Lifted, Is.False, "an unknown release never lifts");
        });
    }

    [Test]
    public void AProteaseNotCuttingAfterKOrRAndANonSpecificSearchAreRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PtmQtlRules.Gate([Task("Asp-N")], "1.1.12").Refusal, Does.Contain("'Asp-N'"));
            Assert.That(PtmQtlRules.Gate([Task(mode: "Semi")], "1.1.12").Refusal, Does.Contain("'Semi'"));
            Assert.That(PtmQtlRules.Gate([], "1.1.12").Refusal, Is.Not.Null, "no task file: unknown, never guessed");
            Assert.That(PtmQtlRules.Gate(["TaskType = \"Search\"\n"], "1.1.12").Refusal, Does.Contain("none named"));
        });
    }

    [Test]
    public void TheSearchTaskDecidesTheProteaseAndEachProteaseItsResidues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PtmQtlRules.Gate([Task("Asp-N"), Task("Lys-C|P")], "1.1.12").CutResidues, Is.EqualTo("K"), "the last task is the search");
            Assert.That(PtmQtlRules.Gate([Task("Arg-C")], "1.1.12").CutResidues, Is.EqualTo("R"));
            Assert.That(PtmQtlRules.Gate([Task("SpecificProtease")], "1.1.12").Refusal, Is.Not.Null,
                "only the exact `Protease` key is read, never `SpecificProtease`");
        });
    }
}
