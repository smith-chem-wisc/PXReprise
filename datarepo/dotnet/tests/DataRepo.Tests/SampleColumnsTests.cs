using System.Globalization;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>PXR-R9 (PXReprise 024, 028): with an experimental design MetaMorpheus labels the protein-group columns by
/// SAMPLE (<c>Intensity_all_1</c>), and datarepo 1.3.0 read every label as a run, so every such search was refused.</summary>
/// <remarks>The fixtures are PXReprise's own mocks of a MetaMorpheus 1.1.12 design search, built on this repository's
/// test dataset (<c>Fixtures/design-labels/PROVENANCE.md</c>). Each test names the class of input it covers: a sample of
/// one file, a sample of several, a label nothing places, a sample table with no design, and a per-run table beside a
/// design, which must be read exactly as before.</remarks>
public class SampleColumnsTests
{
    private const string Accession = "PXD999999";
    private string _root = "";

    private string Manifest => Path.Combine(_root, "manifest.yaml");
    private string Store => Path.Combine(_root, "store");
    private string Results => Path.Combine(_root, "work_root", "run_test", Accession, "04_search", "mm", "Task3SearchTask");
    private string Design => Path.Combine(Results, SampleColumns.DesignFileName);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static string Variant(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "design-labels", name);

    [SetUp]
    public void CopyTheTestDataset()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-design-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(from, file)));
    }

    [TearDown]
    public void RemoveTheCopy()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Lays PXReprise's mock over the search folder, as their 028 reproduction does.</summary>
    private void Overlay(string variant)
    {
        foreach (var file in Directory.EnumerateFiles(Variant(variant)))
            File.Copy(file, Path.Combine(Results, Path.GetFileName(file)), overwrite: true);
    }

    private IngestResult Ingest(IngestRules rules = IngestRules.Current) =>
        Ingester.Ingest(Manifest, Accession, Store, rules: rules, overwrite: true);

    private static List<Row> Rows(IngestResult result, string table)
    {
        var path = Path.Combine(result.BundlePath, table + ".parquet");
        return File.Exists(path) ? ArrowTables.ReadParquet(path).Rows : [];
    }

    /// <summary>A row as one comparable string, without the columns named.</summary>
    private static string Print(Row row, params string[] without) => string.Join("|", row
        .Where(kv => !without.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}={Convert.ToString(kv.Value, CultureInfo.InvariantCulture)}"));

    private static List<string> Printed(IEnumerable<Row> rows, params string[] without) =>
        rows.Select(r => Print(r, without)).Order(StringComparer.Ordinal).ToList();

    private static List<string> SourceRoles(IngestResult result)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BundlePath, BundleWriter.ManifestName)));
        return doc.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("role").GetString()!).ToList();
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void ASampleOfOneFileIsThatRunsAssayWithThePerFileTablesRows(string newline)
    {
        var baseline = Ingest();
        Overlay("A_one_file_per_sample");
        // MetaMorpheus writes the design with the platform's line ending; the aging batch's are CRLF.
        File.WriteAllText(Design, File.ReadAllText(Design).Replace("\r\n", "\n").Replace("\n", newline));
        var sampled = Ingest();

        Assert.That(Printed(Rows(sampled, "quant_values")), Is.EqualTo(Printed(Rows(baseline, "quant_values"))),
            "a one-file sample's values are the ones a per-file table writes, on the same assays");
        Assert.That(Rows(sampled, "quant_values").Any(r => (string)r["feature_type"]! == "protein_group"));
        // The label is MetaMorpheus's, verbatim; everything else about each occupancy row is the per-file one.
        Assert.That(Printed(Rows(sampled, "ptm_stoichiometry"), "sample_label"),
            Is.EqualTo(Printed(Rows(baseline, "ptm_stoichiometry"), "sample_label")));
        Assert.That(Rows(sampled, "ptm_stoichiometry").Select(r => (string)r["sample_label"]!).Distinct().Order(),
            Is.EqualTo(new[] { "all_1", "all_2" }));

        // The design placed the columns, so it is an input: hashed, and copied for a reader.
        Assert.That(SourceRoles(sampled), Does.Contain("experimental_design"));
        Assert.That(File.Exists(Path.Combine(sampled.BundlePath, BundleWriter.SourcesDir, SampleColumns.DesignFileName)));
        Assert.That(sampled.BundleId, Is.Not.EqualTo(baseline.BundleId));
        Assert.That(sampled.Findings.Select(f => f["code"]), Does.Not.Contain("sample_quant_not_stored"));
        Assert.That(sampled.UnmatchedRuns, Is.Empty, "a sample label is not an unmatched run name");
    }

    [Test]
    public void ASampleOfSeveralFilesIsNeverSplitOntoRuns()
    {
        var baseline = Ingest();
        Overlay("B_one_sample_two_fractions");
        var sampled = Ingest();

        var quants = Rows(sampled, "quant_values");
        Assert.That(quants.Where(r => (string)r["feature_type"]! == "protein_group"), Is.Empty,
            "a two-fraction sample's sum belongs to no run");
        Assert.That(Printed(quants), Is.EqualTo(Printed(Rows(baseline, "quant_values").Where(r => (string)r["feature_type"]! == "peptidoform"))),
            "peptide quantities stay per file and are all stored");
        Assert.That(Rows(sampled, "ptm_stoichiometry"), Is.Empty, "the sample's occupancy has no run either");

        var finding = sampled.Findings.Single(f => (string)f["code"]! == "sample_quant_not_stored");
        Assert.That(finding["severity"], Is.EqualTo("warning"));
        Assert.That((string)finding["message"]!, Does.Contain("all_1 (2 files").And.Contain("10 protein-group value(s)"));
        Assert.That(SourceRoles(sampled), Does.Contain("experimental_design"));
    }

    [Test]
    public void ALabelTheDesignDoesNotHaveIsRefusedByName()
    {
        Overlay("A_one_file_per_sample");
        File.WriteAllLines(Design, File.ReadAllLines(Design).Where(l => !l.Contains("\tall\t2\t", StringComparison.Ordinal)));

        var refusal = Assert.Throws<IngestException>(() => Ingest())!;
        Assert.That(refusal.Message, Does.Contain("'all_2'").And.Contain(SampleColumns.DesignFileName).And.Contain("'all_1'"));
        Assert.That(refusal.Message, Does.Not.Contain("do not hold together"), "named at the cause, not at the assay key");
    }

    [Test]
    public void ASampleTableWithNoDesignIsRefusedByName()
    {
        Overlay("A_one_file_per_sample");
        File.Delete(Design);

        var refusal = Assert.Throws<IngestException>(() => Ingest())!;
        Assert.That(refusal.Message, Does.Contain("'all_1', 'all_2'").And.Contain($"no {SampleColumns.DesignFileName}"));
    }

    [Test]
    public void TwoSamplesNamingOneRunAreNeitherStored()
    {
        Overlay("A_one_file_per_sample");
        File.WriteAllText(Design, File.ReadAllText(Design).Replace("QE-002107_GM1_b-calib.mzML", "QE-002106_GM1_a-calib.mzML"));

        var sampled = Ingest();
        Assert.That(Rows(sampled, "quant_values").Where(r => (string)r["feature_type"]! == "protein_group"), Is.Empty);
        Assert.That((string)sampled.Findings.Single(f => (string)f["code"]! == "sample_quant_not_stored")["message"]!,
            Does.Contain("also another sample's"));
    }

    /// <summary>The class the no-bump claim rests on: a table whose columns are runs is read as it always was, even with a
    /// design file beside it, so no bundle datarepo 1.3.0 could write changes its id or its rows.</summary>
    [Test]
    public void APerRunTableIsReadAsBeforeEvenBesideADesign()
    {
        var baseline = Ingest();
        File.Copy(Path.Combine(Variant("A_one_file_per_sample"), SampleColumns.DesignFileName), Design);
        var beside = Ingest();

        Assert.That(beside.BundleId, Is.EqualTo(baseline.BundleId));
        Assert.That(SourceRoles(beside), Does.Not.Contain("experimental_design"));
        Assert.That(File.ReadAllText(Path.Combine(beside.BundlePath, BundleWriter.ManifestName)), Does.Not.Contain(SampleColumns.DesignFileName));
    }

    /// <summary>Only a value that becomes a row decides the mode. A column naming no run whose one value sits on a row
    /// the melt skips (no accession) wrote nothing in 1.3.0, so it must neither refuse nor switch the table.</summary>
    [Test]
    public void AValueOnARowWithNoAccessionDecidesNothing()
    {
        var baseline = Ingest();
        var table = Path.Combine(Results, "AllQuantifiedProteinGroups.tsv");
        var lines = File.ReadAllLines(table).ToList();
        var header = lines[0].Split('\t').ToList();
        header.Add("Intensity_ghost");
        lines[0] = string.Join("\t", header);
        var copy = lines[1].Split('\t');
        for (int i = 1; i < lines.Count; i++) lines[i] += "\t";  // an empty ghost cell on every real group
        // A real group's row with its accession blanked, so every other field still reads as mzLib requires.
        copy[header.IndexOf("Protein Accession")] = "";
        lines.Add(string.Join("\t", copy.Append("5")));
        File.WriteAllLines(table, lines);

        var result = Ingest();
        Assert.That(SourceRoles(result), Does.Not.Contain("experimental_design"));
        Assert.That(Printed(Rows(result, "quant_values")), Is.EqualTo(Printed(Rows(baseline, "quant_values"))));
    }

    [Test]
    public void ParityRulesRefuseASampleTableAsPython0320Did()
    {
        Overlay("A_one_file_per_sample");
        var refusal = Assert.Throws<IngestException>(() => Ingest(IngestRules.Python0320))!;
        Assert.That(refusal.Message, Does.Contain("do not hold together").And.Contain("PXD999999:all_1:label_free"));
    }
}
