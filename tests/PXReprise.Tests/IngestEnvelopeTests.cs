using PXReprise.Batch;

namespace PXReprise.Tests;

/// <summary>
/// `datarepo ingest --json` (datarepo 1.0.0). The fixtures are real envelopes from 1.0.0 on aging's manifest, written to
/// a scratch store on 2026-10-05; only bundle_path and the listed datasets were shortened.
/// </summary>
public class IngestEnvelopeTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "datarepo", name));

    [Test]
    public void AnIngestedDepositIsDeliveredWithItsBundleAndMismatches()
    {
        var o = IngestEnvelope.Read(Fixture("ingest-1.0.0-ingested.json"), "PXD034059", 0);
        Assert.That(o, Is.EqualTo(o with { Rc = 0, Status = "ingested", BundleId = "24e3acd215f6ceb5", Mismatches = 2 }));
        Assert.That(o.Reasons, Is.Empty, "a count mismatch is not a refusal (dataRepo 010)");
    }

    [Test]
    public void ARefusalIsNotDeliveredAndKeepsDataReposOwnWords()
    {
        var o = IngestEnvelope.Read(Fixture("ingest-1.0.0-refused.json"), "PXD999999", 1);
        Assert.That((o.Rc, o.Status, o.BundleId), Is.EqualTo((1, "refused", (string?)null)));
        Assert.That(o.Reasons.Single(), Does.StartWith("PXD999999 is not in F:/aging_data/batch/manifest.yaml"));
    }

    [Test]
    public void ExitCodeZeroIsNotEnough()
    {
        string unchanged = Fixture("ingest-1.0.0-ingested.json").Replace("\"ingested\"", "\"unchanged\"");
        Assert.That(IngestEnvelope.Read(unchanged, "PXD034059", 0).Rc, Is.EqualTo(0), "already written: delivered");
        string excluded = Fixture("ingest-1.0.0-ingested.json").Replace("\"ingested\"", "\"excluded\"");
        var x = IngestEnvelope.Read(excluded, "PXD034059", 0);
        Assert.That((x.Rc, x.Status), Is.EqualTo((1, "excluded")), "the manifest does not say include: not delivered");
        Assert.That(x.Reasons[0], Does.Contain("not delivered"));
        var missing = IngestEnvelope.Read(Fixture("ingest-1.0.0-ingested.json"), "PXD000001", 0);
        Assert.That((missing.Rc, missing.Reasons[0]), Is.EqualTo((1, "PXD000001 is not in the envelope")));
    }

    [Test]
    public void NoEnvelopeFallsBackOnTheExitCode()
    {
        Assert.That(IngestEnvelope.Read("", "PXD034059", 0).Rc, Is.EqualTo(0));
        var o = IngestEnvelope.Read("Traceback (most recent call last):", "PXD034059", 2);
        Assert.That((o.Rc, o.Status), Is.EqualTo((2, (string?)null)));
        Assert.That(o.Reasons.Single(), Does.Contain("no JSON envelope"));
        var crash = IngestEnvelope.Read("""{"datarepo":"1.0.0","ok":false,"exit_code":1,"error":{"type":"IOException"},"datasets":[]}""", "PXD034059", 1);
        Assert.That(crash.Reasons, Has.Some.Contains("IOException"));
    }

    [Test]
    public void OnlyDatarepoOneOrLaterTakesJson()
    {
        Assert.That(BatchRunner.TakesIngestJson("datarepo 1.0.0"), Is.True);
        Assert.That(BatchRunner.TakesIngestJson("datarepo 1.2.3"), Is.True);
        Assert.That(BatchRunner.TakesIngestJson("datarepo 0.32.0"), Is.False, "the Python release has no --json");
        Assert.That(BatchRunner.TakesIngestJson("datarepo 0.0.0-dev"), Is.False, "a source build; dataRepo's run refuses it too");
        Assert.That(BatchRunner.TakesIngestJson(""), Is.False);
    }
}
