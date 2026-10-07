using System.Text.Json.Nodes;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// G27: with an experimental design, FlashLFQ labels protein-group columns by SAMPLE ({Condition}_{Biorep}), not by file.
/// Run-grain metrics must map each label to its one file, and leave out a sample that spans several files. Before the
/// fix the 1.1.12 trial (PXD017058) reported pg_missing_frac_msms = 1 for every run and dropped
/// contaminant_intensity_frac.
/// </summary>
public class ProteinGroupColumnsTests
{
    private const string PgHeader = "Protein Accession\tProtein Full Name\tOrganism\tProtein Decoy/Contaminant/Target\tProtein QValue";

    /// <summary>A search folder with a two-group protein table over the given column labels, and an optional design.</summary>
    private static string Table(string[] labels, string? design, params (string td, double[] intensity, int[] counts)[] rows)
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>
        {
            PgHeader + string.Concat(labels.Select(l => $"\tIntensity_{l}")) + string.Concat(labels.Select(l => $"\tSpectralCount_{l}")),
        };
        int i = 0;
        foreach (var (td, inten, counts) in rows)
            lines.Add($"P{i}\tprotein {i++}\tHomo sapiens\t{td}\t0.001" + string.Concat(inten.Select(v => $"\t{v}")) + string.Concat(counts.Select(c => $"\t{c}")));
        File.WriteAllLines(Path.Combine(dir, "AllQuantifiedProteinGroups.tsv"), lines);
        if (design is not null) File.WriteAllText(Path.Combine(dir, DesignStage.FileName), design);
        return Path.Combine(dir, "AllQuantifiedProteinGroups.tsv");
    }

    private const string OneFilePerSample =
        "FileName\tCondition\tBiorep\tFraction\tTechrep\nrunA-calib.mzML\taged\t1\t1\t1\nrunB-calib.mzML\taged\t2\t1\t1\n";

    [Test]
    public void WithoutADesignEveryLabelIsAFile()
    {
        var c = ProteinGroupColumns.ForTable(Table(new[] { "runA-calib" }, null, ("T", new[] { 1.0 }, new[] { 1 })));
        Assert.That((c.FileOf("runA-calib"), c.MultiFileSamples.Count), Is.EqualTo(("runA-calib", 0)));
    }

    [Test]
    public void ASampleOfOneFileMapsToThatFileAndASampleOfSeveralToNone()
    {
        string design = OneFilePerSample + "runC_F1-calib.mzML\tyoung\t1\t1\t1\nrunC_F2-calib.mzML\tyoung\t1\t2\t1\n";
        var c = ProteinGroupColumns.ForTable(Table(new[] { "aged_1" }, design, ("T", new[] { 1.0 }, new[] { 1 })));
        Assert.Multiple(() =>
        {
            Assert.That(c.FileOf("aged_1"), Is.EqualTo("runA-calib"));
            Assert.That(c.FileOf("aged_2"), Is.EqualTo("runB-calib"));
            Assert.That(c.FileOf("young_1"), Is.Null, "two fractions summed into one value belong to the sample, not a run");
            Assert.That(c.MultiFileSamples, Is.EqualTo(new[] { "young_1" }));
            Assert.That(c.FileOf("not_a_sample"), Is.EqualTo("not_a_sample"));
        });
    }

    /// <summary>The trial's case: sample-labelled columns, each one file. Presence and contaminant share land on the runs.</summary>
    [Test]
    public void QcProteinGroupPassFindsTheRunsBehindSampleLabels()
    {
        string pg = Table(new[] { "aged_1", "aged_2" }, OneFilePerSample,
            ("T", new[] { 90.0, 50.0 }, new[] { 2, 0 }),
            ("C", new[] { 10.0, 50.0 }, new[] { 1, 1 }));
        var pass = QcPayloadBuilder.ParseProteinGroups(pg);
        Assert.Multiple(() =>
        {
            Assert.That(pass.Quantified, Is.EqualTo(2));
            Assert.That(pass.Present["runA"], Is.EqualTo(2));
            Assert.That(pass.Present["runB"], Is.EqualTo(1));
            Assert.That(pass.ContaminantIntensity["runA"], Is.EqualTo(0.1));
            Assert.That(pass.ContaminantIntensity["runB"], Is.EqualTo(0.5));
            Assert.That(pass.Measures("runA") && pass.Measures("runB"), Is.True);
            Assert.That(pass.Present.ContainsKey("aged_1"), Is.False, "a sample label is never taken for a run");
        });
    }

    [Test]
    public void ARunWhoseSampleSpansSeveralFilesIsNotMeasured()
    {
        string design = "FileName\tCondition\tBiorep\tFraction\tTechrep\nf1-calib.mzML\tyoung\t1\t1\t1\nf2-calib.mzML\tyoung\t1\t2\t1\n";
        var pass = QcPayloadBuilder.ParseProteinGroups(Table(new[] { "young_1" }, design, ("T", new[] { 5.0 }, new[] { 1 })));
        Assert.Multiple(() =>
        {
            Assert.That(pass.Measures("f1"), Is.False);
            Assert.That(pass.Measures("f2"), Is.False);
            Assert.That(pass.ContaminantIntensity, Is.Empty);
            Assert.That(pass.MultiFileSamples, Is.EqualTo(new[] { "young_1" }));
        });
    }

    [Test]
    public void SearchContaminationKeysTheSharePerFileThroughTheDesign()
    {
        string pg = Table(new[] { "aged_1", "aged_2" }, OneFilePerSample,
            ("T", new[] { 90.0, 50.0 }, new[] { 2, 0 }),
            ("C", new[] { 10.0, 50.0 }, new[] { 1, 1 }));
        string psms = TestSupport.WriteFile(Path.GetDirectoryName(pg)!, "AllPSMs.psmtsv", "QValue\tDecoy/Contaminant/Target\n0.001\tT\n");
        var c = SearchMetrics.Contamination(psms, pg, null, null);
        Assert.That(c.IntensitySharePerFile.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)),
            Is.EqualTo(new[] { ("runA-calib", (double?)0.1), ("runB-calib", (double?)0.5) }));
    }

    /// <summary>The real 1.1.12 trial output: no run reads as missing every protein group any more.</summary>
    [Test, Category("LocalCorpus")]
    public void TheTrialsRunsAreMeasuredAgain()
    {
        const string pg = @"F:\pxreprise_trial_1112\runs\PXD017058\04_search\mm\Task3SearchTask\AllQuantifiedProteinGroups.tsv";
        if (!File.Exists(pg)) Assert.Ignore("the trial store is not attached");
        var pass = QcPayloadBuilder.ParseProteinGroups(pg);
        Assert.That(pass.Present.Count, Is.EqualTo(9));
        Assert.That(pass.Present.Keys, Has.All.StartsWith("OTF18-"), "every key is a run, never a sample label such as aged_1");
        Assert.That(pass.ContaminantIntensity.Keys, Has.All.StartsWith("OTF18-"));
        Assert.That(pass.Present.Values.Select(v => (double)(pass.Quantified!.Value - v) / pass.Quantified.Value), Has.All.LessThan(0.9));
        Assert.That(pass.ContaminantIntensity.Count, Is.EqualTo(9));
    }
}
