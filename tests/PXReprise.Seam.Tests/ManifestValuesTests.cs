using PXReprise.Config;
using PXReprise.Ingest;

namespace PXReprise.Seam.Tests;

/// <summary>
/// The values the engine may write into the manifest from a question, against the values datarepo's manifest reader
/// accepts. datarepo refuses the whole manifest over one value it does not know, so every deposit's ingest fails, not only
/// the one that used it: `split_reason: tissue` did this for two days in November 2026, because the engine accepted it and
/// the datarepo of the time did not. One release now ships both programs (decision 0073), and a batch refuses a datarepo
/// from another release; these tests keep each pair of lists one list, so no release can ship the engine ahead of datarepo.
/// datarepo may accept more than the engine writes.
/// </summary>
public class ManifestValuesTests
{
    [Test]
    public void EverySplitReasonTheEngineAcceptsIsOneDatarepoReads() =>
        Assert.That(SplitGroup.Reasons, Is.SubsetOf(DatasetEntry.SplitReasons.Keys),
            "a question's [splits] `by` becomes the manifest's split_reason");

    [Test]
    public void EveryRestrictionUseTheEngineAcceptsIsOneDatarepoReads() =>
        Assert.That(Question.RestrictionUses, Is.SubsetOf(DatasetEntry.RestrictionUses.Keys),
            "a question's [restrictions] uses become the manifest's restrictions");
}
