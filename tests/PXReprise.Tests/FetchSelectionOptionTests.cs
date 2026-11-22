using PXReprise.Cli;

namespace PXReprise.Tests;

/// <summary>
/// <c>fetch --include / --exclude</c> (2026-11-22): a fetch by hand downloads only the files a search will use, chosen as a
/// question's <c>include_files</c> and <c>exclude_files</c> choose them. Before, a hand run of part of a deposit (the
/// 32 files of PXD019690's astro split) downloaded all 90 and set 58 aside.
/// </summary>
public class FetchSelectionOptionTests
{
    private static readonly string[] Names =
    {
        "a05058_TMT_F1.raw", "A05068_tmt_F11.RAW", "a05191_LFQ_1.raw", "a05202_LFQ_12.raw",
    };

    private static string[] Kept(string? include, string? exclude) =>
        Names.Where(Program.FileSelection(include, exclude)!.Keeps).ToArray();

    [Test]
    public void NeitherOptionSelectsNothing() => Assert.That(Program.FileSelection(null, null), Is.Null);

    [Test]
    public void IncludeKeepsOnlyMatchingFilesIgnoringCase() =>
        Assert.That(Kept("_tmt_", null), Is.EqualTo(new[] { "a05058_TMT_F1.raw", "A05068_tmt_F11.RAW" }));

    [Test]
    public void ExcludeLeavesOutMatchingFiles() =>
        Assert.That(Kept(null, @"^a0520\d"), Is.EqualTo(new[] { "a05058_TMT_F1.raw", "A05068_tmt_F11.RAW", "a05191_LFQ_1.raw" }));

    [Test]
    public void BothMustHold()
    {
        Assert.That(Kept("^a05", "_F11"), Is.EqualTo(new[] { "a05058_TMT_F1.raw", "a05191_LFQ_1.raw", "a05202_LFQ_12.raw" }));
        Assert.That(Program.FileSelection("_tmt_", "LFQ")!.Reason,
            Is.EqualTo("include_files /_tmt_/, exclude_files /LFQ/ (fetch by hand)"), "recorded in the fetch provenance as a question's selection is");
    }

    [TestCase("(", null, "--include is not a valid regular expression")]
    [TestCase(null, "[a-", "--exclude is not a valid regular expression")]
    [TestCase("", null, "--include is empty")]
    [TestCase(null, "", "--exclude is empty")]
    public void ABadPatternIsAUsageError(string? include, string? exclude, string message) =>
        Assert.That(Assert.Throws<UsageException>(() => Program.FileSelection(include, exclude))!.Message, Does.Contain(message));
}
