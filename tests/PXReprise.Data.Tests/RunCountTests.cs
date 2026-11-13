using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 7: a bundle whose results.txt gives no per-run count says which counts are missing
/// and why, so a run with no row reads as "not written", never as "none".</summary>
public class RunCountTests
{
    private const string Dataset = "PXD000001";

    private static List<Row> Runs(int n) =>
        Enumerable.Range(1, n).Select(i => new Row { ["run_id"] = $"{Dataset}:run{i}" }).ToList();

    private static Row Metric(int run, string name, string source) => new()
    {
        ["scope"] = "run", ["scope_id"] = $"{Dataset}:run{run}", ["name"] = name, ["value"] = 1L, ["source"] = source,
    };

    private static IEnumerable<Row> Lines(int runs, params string[] names) =>
        Enumerable.Range(1, runs).SelectMany(r => names.Select(name => Metric(r, name, "results.txt")));

    private static IEnumerable<Row> QcMs2(int runs) => Enumerable.Range(1, runs).Select(r => Metric(r, "ms2", "qc_report.json"));

    private static List<Row> Findings(int runs, IEnumerable<Row> metrics, bool resultsTxt = true, bool? writeIndividualFiles = false) =>
        Ingester.RunCountFindings(Runs(runs), metrics.ToList(), resultsTxt, writeIndividualFiles, Dataset).ToList();

    [Test]
    public void ASearchThatWroteEveryPerRunLineHasNoFinding()
    {
        var metrics = Lines(3, [.. Ingester.RunCountNames]).Concat(QcMs2(3))
            .Append(new Row { ["scope"] = "dataset", ["scope_id"] = Dataset, ["name"] = "psms_1pct", ["source"] = "results.txt" });
        Assert.That(Findings(3, metrics, writeIndividualFiles: true), Is.Empty);
    }

    /// <summary>MetaMorpheus 1.1.14 with <c>WriteIndividualFiles = false</c> (PXD034029): the precursor and MS2 lines
    /// are back (MetaMorpheus #2955), the PSM, peptide and protein-group lines are not.</summary>
    [Test]
    public void TheFindingNamesTheMissingCountsAndTheSettingThatTurnedThemOff()
    {
        var found = Findings(2, Lines(2, "precursors", "ms2_scans").Concat(QcMs2(2)));
        Assert.That(found, Has.Count.EqualTo(1));
        var f = found[0];
        var message = (string)f["message"]!;
        Assert.Multiple(() =>
        {
            Assert.That(f["code"], Is.EqualTo("run_counts_missing"));
            Assert.That(f["severity"], Is.EqualTo("info"));
            Assert.That(f["finding_id"], Is.EqualTo($"{Dataset}:run_counts_missing"));
            Assert.That(f["run_id"], Is.Null, "one finding for the deposit, not one per run");
            Assert.That(message, Does.Contain("`psms_1pct` (2 of 2 runs), `peptides` (2 of 2 runs), `protein_groups` (2 of 2 runs)."));
            Assert.That(message, Does.Not.Contain("`precursors`").And.Not.Contain("`ms2_scans`"));
            Assert.That(message, Does.Contain("not a count of zero"));
            Assert.That(message, Does.Contain("`WriteIndividualFiles = false`"));
            Assert.That(message, Does.Not.Contain("QC report"), "the MS2 line is there, so nothing points elsewhere");
        });
    }

    /// <summary>MetaMorpheus 1.1.13 with the setting off (PXD005035): no per-run line at all; the QC report still
    /// counts every run's MS2 spectra.</summary>
    [Test]
    public void WithNoMs2LineTheFindingPointsToTheQcReport()
    {
        var message = (string)Findings(4, QcMs2(3)).Single()["message"]!;
        Assert.Multiple(() =>
        {
            foreach (var name in Ingester.RunCountNames)
                Assert.That(message, Does.Contain($"`{name}` (4 of 4 runs)"), name);
            Assert.That(message, Does.Contain("`runs.ms2_spectra`"));
            Assert.That(message, Does.Contain("for 3 of the 4 runs"));
        });
        Assert.That((string)Findings(2, []).Single()["message"]!, Does.Contain("The QC report gives these runs no MS2 count either."));
    }

    /// <summary>A one-file search writes no per-file line whatever the setting says (PXD027232), and its deposit-wide
    /// rows are that run's.</summary>
    [Test]
    public void AOneRunSearchSaysItsDepositRowsAreThatRun()
    {
        var message = (string)Findings(1, QcMs2(1), writeIndividualFiles: true).Single()["message"]!;
        Assert.That(message, Does.Contain("two or more files").And.Contain("count that run"));
        Assert.That(message, Does.Not.Contain("WriteIndividualFiles"));
    }

    [Test]
    public void TheReasonIsOnlyWhatTheFilesSay()
    {
        Assert.Multiple(() =>
        {
            Assert.That((string)Findings(2, QcMs2(2), resultsTxt: false).Single()["message"]!, Does.Contain("The search left no results.txt."));
            foreach (var setting in new bool?[] { true, null })
            {
                var message = (string)Findings(2, Lines(1, [.. Ingester.RunCountNames]), writeIndividualFiles: setting).Single()["message"]!;
                Assert.That(message, Does.Contain("results.txt has no per-file line naming these runs."), $"{setting}");
                Assert.That(message, Does.Contain("`psms_1pct` (1 of 2 runs)"), $"{setting}");
                Assert.That(message, Does.Not.Contain("WriteIndividualFiles"), $"{setting}: an unstated setting is not assumed");
            }
        });
    }

    /// <summary>A row for a run the bundle does not hold, or of another source, fills nothing.</summary>
    [Test]
    public void OnlyTheBundlesOwnRunsAndResultsTxtRowsCount()
    {
        var metrics = Lines(1, [.. Ingester.RunCountNames]).ToList();
        metrics.Add(new Row { ["scope"] = "run", ["scope_id"] = $"{Dataset}:elsewhere", ["name"] = "peptides", ["source"] = "results.txt" });
        metrics.Add(Metric(2, "psms_1pct", "provenance"));
        var message = (string)Findings(2, metrics).Single()["message"]!;
        Assert.That(message, Does.Contain("`psms_1pct` (1 of 2 runs), `peptides` (1 of 2 runs)"));
    }

    [Test]
    public void TheSettingIsReadFromTheSearchTasksOnly()
    {
        var dir = Directory.CreateTempSubdirectory("datarepo-runcounts-");
        try
        {
            string Task(string name, string text)
            {
                var path = Path.Combine(dir.FullName, name);
                File.WriteAllText(path, text);
                return path;
            }
            var off = Task("off.toml", "TaskType = \"Search\"\n[SearchParameters]\nWriteIndividualFiles = false\n");
            var on = Task("on.toml", "TaskType = \"Search\"\n[SearchParameters]\nWriteIndividualFiles = true\n");
            var unstated = Task("unstated.toml", "TaskType = \"Search\"\n[SearchParameters]\nWriteMzId = true\n");
            var calibration = Task("calibration.toml", "TaskType = \"Calibrate\"\n[SearchParameters]\nWriteIndividualFiles = true\n");
            Assert.Multiple(() =>
            {
                Assert.That(SearchParams.WriteIndividualFiles([off]), Is.False);
                Assert.That(SearchParams.WriteIndividualFiles([on]), Is.True);
                Assert.That(SearchParams.WriteIndividualFiles([calibration, off]), Is.False, "a calibration task is not the search");
                Assert.That(SearchParams.WriteIndividualFiles([unstated]), Is.Null, "the release's default is not assumed");
                Assert.That(SearchParams.WriteIndividualFiles([off, unstated]), Is.Null);
                Assert.That(SearchParams.WriteIndividualFiles([off, on]), Is.Null, "two search tasks that disagree");
                Assert.That(SearchParams.WriteIndividualFiles([]), Is.Null);
            });
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
