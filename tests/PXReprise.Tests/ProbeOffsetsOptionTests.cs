using PXReprise.Cli;

namespace PXReprise.Tests;

/// <summary>
/// D84: <c>search --probe-offsets</c> passes a probe's measured precursor offsets, as the batch does from a state entry's
/// <c>probe_precursor_offsets_ppm</c>, so a search re-run by hand under a profile with <c>precursor_window = "probe"</c>
/// gets the window the batch would give it.
/// </summary>
public class ProbeOffsetsOptionTests
{
    [Test]
    public void TheFileIsAStateEntrysOffsets()
    {
        string dir = TestSupport.TempDir();
        var offsets = Program.ReadProbeOffsets(TestSupport.WriteFile(dir, "o.json",
            """{"1967_br1_tr1": 9.4, "1967_br3_tr3": 9.39, "whole": 10}"""));
        Assert.That(offsets, Is.EqualTo(new Dictionary<string, double> { ["1967_br1_tr1"] = 9.4, ["1967_br3_tr3"] = 9.39, ["whole"] = 10 }));
    }

    [TestCase("not json", "is not JSON")]
    [TestCase("[9.4]", "must be a JSON object")]
    [TestCase("{}", "must be a JSON object")]
    [TestCase("""{"run": "9.4"}""", "run 'run' has no number")]
    [TestCase("""{"run": null}""", "run 'run' has no number")]
    public void AnythingElseIsAUsageError(string text, string message)
    {
        string dir = TestSupport.TempDir();
        var e = Assert.Throws<UsageException>(() => Program.ReadProbeOffsets(TestSupport.WriteFile(dir, "o.json", text)));
        Assert.That(e!.Message, Does.Contain(message));
    }

    [Test]
    public void AMissingFileIsAUsageError() =>
        Assert.That(Assert.Throws<UsageException>(() => Program.ReadProbeOffsets(Path.Combine(TestSupport.TempDir(), "none.json")))!.Message,
            Does.Contain("does not exist"));
}
