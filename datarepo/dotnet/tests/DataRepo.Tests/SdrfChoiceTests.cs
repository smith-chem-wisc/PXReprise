using System.Text.Json.Nodes;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>Which SDRF describes the samples (G107, PXR-R22): the instance owner's <c>operator_sdrf</c>, else the SDRF
/// the search's design came from (sha256-checked), else the deposit's <c>metadata/</c> SDRF.</summary>
/// <remarks>Built on this repository's test dataset (two runs) with PXReprise's design mocks
/// (<c>Fixtures/design-labels</c>): <c>A_one_file_per_sample</c> (samples all_1, all_2, one file each) and
/// <c>B_one_sample_two_fractions</c> (one sample, both files). Each test names the class of input it covers.</remarks>
public class SdrfChoiceTests
{
    private const string Accession = "PXD999999";
    private const string RunA = "QE-002106_GM1_a";
    private const string RunB = "QE-002107_GM1_b";
    private string _root = "";

    private string Manifest => Path.Combine(_root, "manifest.yaml");
    private string Store => Path.Combine(_root, "store");
    private string SearchDir => Path.Combine(_root, "work_root", "run_test", Accession, "04_search");
    private string Results => Path.Combine(SearchDir, "mm", "Task3SearchTask");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [SetUp]
    public void CopyTheTestDataset()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-sdrfchoice-" + Guid.NewGuid().ToString("N"));
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

    private void Design(string variant)
    {
        var from = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "design-labels", variant);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(Results, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>An SDRF in the real grammar: one label-free row per (source name, data file), with an age characteristic
    /// and a factor value.</summary>
    private string WriteSdrf(string name, params (string Source, string Age, string File)[] rows)
    {
        var path = Path.Combine(_root, "curated", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string> { "source name\tcharacteristics[organism]\tcharacteristics[age]\tassay name\tcomment[label]\tcomment[data file]\tfactor value[age]" };
        lines.AddRange(rows.Select(r => $"{r.Source}\tHomo sapiens\t{r.Age}\t{r.File}\tNT=label free sample;AC=MS:1002038\t{r.File}.raw\t{r.Age}"));
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
        return path;
    }

    private string TwoSamples(string name = "operator.sdrf.tsv") => WriteSdrf(name, ("donor A", "24M", RunA), ("donor B", "3M", RunB));

    private void NameOperatorSdrf(string path)
    {
        var text = File.ReadAllText(Manifest);
        var anchor = "    flags: [low_id_rate, no_design_file]\n";
        Assert.That(text, Does.Contain(anchor), "the fixture manifest changed; find the PXD999999 entry another way");
        File.WriteAllText(Manifest, text.Replace(anchor, anchor + $"    operator_sdrf: '{path.Replace("'", "''")}'\n"), new System.Text.UTF8Encoding(false));
    }

    private void RecordDesignSdrf(string source, string path, string? sha256 = null)
    {
        var provenancePath = Path.Combine(SearchDir, "provenance.json");
        var doc = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
        doc["experimental_design"] = new JsonObject
        {
            ["source"] = source,
            ["sdrf"] = path,
            ["sdrf_sha256"] = sha256 ?? BundleWriter.Sha256File(path),
            ["sdrf_origin"] = @"E:\somewhere\the-original.sdrf.tsv",
        };
        File.WriteAllText(provenancePath, doc.ToJsonString(), new System.Text.UTF8Encoding(false));
    }

    private IngestResult Ingest(IngestRules rules = IngestRules.Current) =>
        Ingester.Ingest(Manifest, Accession, Store, rules: rules, overwrite: true);

    private static List<Row> Rows(IngestResult result, string table)
    {
        var path = Path.Combine(result.BundlePath, table + ".parquet");
        return File.Exists(path) ? ArrowTables.ReadParquet(path).Rows : [];
    }

    private static JsonNode BundleJson(IngestResult result) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(result.BundlePath, BundleWriter.ManifestName)))!;

    private static List<string> Characteristics(IngestResult result) =>
        Rows(result, "sample_characteristics").Select(r => $"{r["sample_id"]}|{r["name"]}|{r["value"]}").Order(StringComparer.Ordinal).ToList();

    private static string? SdrfSource(IngestResult result) =>
        BundleJson(result)["sources"]!.AsArray().FirstOrDefault(s => (string?)s!["role"] == "sdrf")?["bundle_path"]?.GetValue<string>();

    // ---------------------------------------------------------------- G107: operator_sdrf

    [Test]
    public void AnOperatorSdrfIsReadForItsCharacteristicsOnly()
    {
        Design("A_one_file_per_sample");
        NameOperatorSdrf(TwoSamples());
        var result = Ingest();
        Assert.Multiple(() =>
        {
            Assert.That(Rows(result, "samples").Select(r => (string)r["source_name"]!).Order(), Is.EqualTo(new[] { "donor A", "donor B" }));
            Assert.That(Characteristics(result), Does.Contain($"{Accession}:donor A|characteristics[age]|24M"));
            Assert.That(Characteristics(result).Where(c => c.Contains("factor value[")), Is.Empty, "a curated SDRF's grouping is the question's");
            Assert.That(SdrfSource(result), Is.EqualTo("sources/operator.sdrf.tsv"), "the file is a source of the bundle");
            Assert.That((string?)BundleJson(result)["sdrf"]!["chosen_by"], Is.EqualTo("operator_sdrf"));
            Assert.That(result.Findings.Select(f => f["code"]), Does.Not.Contain("no_sdrf").And.Not.Contain("sdrf_unmatched"));
        });
    }

    /// <summary>The path is not content (it is site-specific); the bytes are.</summary>
    [Test]
    public void TheBundleIdHashesTheOperatorSdrfsBytesNotItsPath()
    {
        Design("A_one_file_per_sample");
        NameOperatorSdrf(TwoSamples("first.sdrf.tsv"));
        var first = Ingest();
        var moved = Path.Combine(_root, "elsewhere", "second.sdrf.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(Path.Combine(_root, "curated", "first.sdrf.tsv"), moved);
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace(Path.Combine(_root, "curated", "first.sdrf.tsv"), moved));
        var same = Ingest();
        File.AppendAllText(moved, "\n");
        var changed = Ingest();
        Assert.Multiple(() =>
        {
            Assert.That(same.BundleId, Is.EqualTo(first.BundleId), "same bytes under another path and name");
            Assert.That(changed.BundleId, Is.Not.EqualTo(first.BundleId), "other bytes");
        });
    }

    [Test]
    public void AnOperatorSdrfWinsOverTheSearchsOwn()
    {
        Design("A_one_file_per_sample");
        RecordDesignSdrf("question", WriteSdrf("question.sdrf.tsv", ("q A", "1M", RunA), ("q B", "2M", RunB)));
        NameOperatorSdrf(TwoSamples());
        var result = Ingest();
        Assert.That(Rows(result, "samples").Select(r => (string)r["source_name"]!).Order(), Is.EqualTo(new[] { "donor A", "donor B" }));
    }

    /// <summary>Every way an SDRF can describe other samples than the search's.</summary>
    [TestCase("A_one_file_per_sample", "one source name", "holds files of design samples")]
    [TestCase("B_one_sample_two_fractions", "two source names", "is split over source names")]
    [TestCase("A_one_file_per_sample", "a missing row", "no row for searched file")]
    [TestCase("A_one_file_per_sample", "a doubled row", "several rows for")]
    [TestCase(null, "no design", "no ExperimentalDesign.tsv")]
    public void AnOperatorSdrfThatGroupsOtherwiseIsRefused(string? design, string shape, string message)
    {
        if (design is not null) Design(design);
        var path = shape switch
        {
            "one source name" => WriteSdrf("x.sdrf.tsv", ("donor", "24M", RunA), ("donor", "24M", RunB)),
            "a missing row" => WriteSdrf("x.sdrf.tsv", ("donor A", "24M", RunA)),
            "a doubled row" => WriteSdrf("x.sdrf.tsv", ("donor A", "24M", RunA), ("donor A", "24M", RunA), ("donor B", "3M", RunB)),
            _ => TwoSamples("x.sdrf.tsv"),
        };
        NameOperatorSdrf(path);
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains(message).And.Message.Contains("x.sdrf.tsv"));
    }

    [Test]
    public void ASampleOfTwoFractionsIsOneSourceName()
    {
        Design("B_one_sample_two_fractions");
        NameOperatorSdrf(WriteSdrf("x.sdrf.tsv", ("donor", "24M", RunA), ("donor", "24M", RunB)));
        Assert.That(Rows(Ingest(), "samples").Select(r => (string)r["source_name"]!), Is.EqualTo(new[] { "donor" }));
    }

    [Test]
    public void AMissingOperatorSdrfIsRefused()
    {
        NameOperatorSdrf(Path.Combine(_root, "curated", "nowhere.sdrf.tsv"));
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains("no file there"));
    }

    [Test]
    public void AnOperatorSdrfThatIsNotAPathIsRefusedByTheManifest()
    {
        var text = File.ReadAllText(Manifest);
        File.WriteAllText(Manifest, text.Replace("    flags: [low_id_rate, no_design_file]\n", "    flags: [low_id_rate, no_design_file]\n    operator_sdrf: 3\n"));
        Assert.That(() => DataRepo.Ingest.Manifest.Load(Manifest), Throws.TypeOf<ManifestException>().With.Message.Contains("operator_sdrf"));
    }

    // ---------------------------------------------------------------- PXR-R23: pooled assays (schema 0.0.16)

    private const string Pooled = $"{Accession}:design:all_1";

    /// <summary>A design sample of two fractions that is one SDRF sample's files: its values have a sample to belong to.</summary>
    [Test]
    public void ASampleOfSeveralFilesThatIsOneSdrfSamplesIsAPooledAssay()
    {
        Design("B_one_sample_two_fractions");
        NameOperatorSdrf(WriteSdrf("x.sdrf.tsv", ("donor", "24M", RunA), ("donor", "24M", RunB)));
        var result = Ingest();
        var assays = Rows(result, "assays");
        var pooled = assays.Single(a => (string)a["assay_id"]! == Pooled);
        var quants = Rows(result, "quant_values");
        var occupancy = Rows(result, "ptm_stoichiometry");
        Assert.Multiple(() =>
        {
            Assert.That(pooled["assay_kind"], Is.EqualTo("design_sample"));
            Assert.That(pooled["run_id"], Is.Null, "a pooled sample has no single run");
            Assert.That(pooled["sample_id"], Is.EqualTo($"{Accession}:donor"));
            Assert.That(pooled["design_label"], Is.EqualTo("all_1"));
            Assert.That(assays.Where(a => (string)a["assay_kind"]! == "run").Select(a => a["run_id"]),
                Is.EquivalentTo(new[] { $"{Accession}:{RunA}", $"{Accession}:{RunB}" }), "the runs keep their own assays");
            Assert.That(Rows(result, "assay_runs").Select(r => $"{r["assay_id"]}|{r["run_id"]}|{r["fraction"]}|{r["technical_replicate"]}"),
                Is.EquivalentTo(new[] { $"{Pooled}|{Accession}:{RunA}|1|1", $"{Pooled}|{Accession}:{RunB}|2|1" }));
            var protein = quants.Where(q => (string)q["feature_type"]! == "protein_group").ToList();
            Assert.That(protein, Is.Not.Empty);
            Assert.That(protein.Select(q => q["assay_id"]).Distinct(), Is.EqualTo(new[] { Pooled }), "protein values are the sample's");
            Assert.That(quants.Where(q => (string)q["feature_type"]! == "peptidoform").Select(q => q["assay_id"]),
                Has.None.EqualTo(Pooled), "no peptide value names a pooled assay");
            Assert.That(occupancy, Is.Not.Empty);
            Assert.That(occupancy.Select(o => $"{o["assay_id"]}|{o["denominator_grouping"]}").Distinct(), Is.EqualTo(new[] { $"{Pooled}|sample_group" }));
            Assert.That(result.Findings.Select(f => f["code"]), Does.Not.Contain("sample_quant_not_stored"));
            Assert.That((string?)BundleJson(result)["protein_group_columns"]!["pooled_samples"]!["all_1"]!["assay_id"], Is.EqualTo(Pooled));
        });
    }

    /// <summary>The same sample whose files are two SDRF samples' has no sample to belong to, and stays left out.</summary>
    [Test]
    public void ASampleOfSeveralFilesOverTwoSdrfSamplesIsLeftOut()
    {
        Design("B_one_sample_two_fractions");
        var result = Ingest();  // the deposit's metadata SDRF: one sample per file
        Assert.Multiple(() =>
        {
            Assert.That(Rows(result, "assays").Select(a => a["assay_kind"]).Distinct(), Is.EqualTo(new[] { "run" }));
            Assert.That(Rows(result, "assay_runs"), Is.Empty);
            Assert.That(Rows(result, "quant_values").Where(q => (string)q["feature_type"]! == "protein_group"), Is.Empty);
            var finding = result.Findings.Single(f => (string)f["code"]! == "sample_quant_not_stored");
            Assert.That((string)finding["message"]!, Does.Contain("its files are 2 SDRF samples'"));
        });
    }

    /// <summary>A sample of one file stays on its run's assay, which says the design placed it.</summary>
    [Test]
    public void ASampleOfOneFileKeepsItsRunsAssayWithItsLabel()
    {
        Design("A_one_file_per_sample");
        NameOperatorSdrf(TwoSamples());
        var assays = Rows(Ingest(), "assays");
        Assert.Multiple(() =>
        {
            Assert.That(assays.Select(a => $"{a["run_id"]}|{a["assay_kind"]}|{a["design_label"]}"), Is.EquivalentTo(new[]
            {
                $"{Accession}:{RunA}|run|all_1", $"{Accession}:{RunB}|run|all_2",
            }));
        });
    }

    [Test]
    public void APerRunTableLabelsNoAssay()
    {
        var assays = Rows(Ingest(), "assays");
        Assert.That(assays.Select(a => $"{a["assay_kind"]}|{a["design_label"]}"), Is.EqualTo(new[] { "run|", "run|" }));
    }

    // ---------------------------------------------------------------- PXR-R22: the SDRF the search's design came from

    [TestCase("question", false)]
    [TestCase("deposit", true)]
    public void TheSdrfTheDesignCameFromIsRead(string source, bool factorValuesKept)
    {
        RecordDesignSdrf(source, WriteSdrf("design.sdrf.tsv", ("q A", "1M", RunA), ("q B", "2M", RunB)));
        var result = Ingest();
        var factors = Characteristics(result).Where(c => c.Contains("factor value[")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Rows(result, "samples").Select(r => (string)r["source_name"]!).Order(), Is.EqualTo(new[] { "q A", "q B" }));
            Assert.That(factors, factorValuesKept ? Is.Not.Empty : Is.Empty);
            Assert.That((string?)BundleJson(result)["sdrf"]!["chosen_by"], Is.EqualTo($"experimental_design ({source})"));
            Assert.That((string?)BundleJson(result)["sdrf"]!["sdrf_origin"], Is.EqualTo(@"E:\somewhere\the-original.sdrf.tsv"), "recorded, never read");
        });
    }

    /// <summary>The seven of PXReprise 052: aging corrected the file after the search, so it is not the search's record.</summary>
    [Test]
    public void ADesignSdrfEditedAfterTheSearchIsRefused()
    {
        var path = WriteSdrf("design.sdrf.tsv", ("q A", "1M", RunA), ("q B", "2M", RunB));
        RecordDesignSdrf("question", path);
        File.AppendAllText(path, "\n");
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>()
            .With.Message.Contains("design.sdrf.tsv").And.Message.Contains("not the file the search read").And.Message.Contains("operator_sdrf"));
    }

    [Test]
    public void APxrepriseDefaultDesignReadsTheMetadataSdrfAsBefore()
    {
        var before = Ingest();
        RecordDesignSdrf("pxreprise_default", WriteSdrf("default.sdrf.tsv", ("q A", "1M", RunA), ("q B", "2M", RunB)));
        var after = Ingest();
        Assert.Multiple(() =>
        {
            Assert.That(Rows(after, "samples").Select(r => (string)r["source_name"]!).Order(),
                Is.EqualTo(Rows(before, "samples").Select(r => (string)r["source_name"]!).Order()));
            Assert.That((string?)BundleJson(after)["sdrf"]!["chosen_by"], Is.EqualTo("metadata"));
        });
    }

    [Test]
    public void Python0320ReadsTheMetadataSdrfWhateverIsNamed()
    {
        // No design overlay: 0.32.0 had no sample mode, and under it the operator SDRF is never read to be checked.
        var before = Ingest(IngestRules.Python0320);
        NameOperatorSdrf(TwoSamples());
        RecordDesignSdrf("question", WriteSdrf("design.sdrf.tsv", ("q A", "1M", RunA), ("q B", "2M", RunB)));
        var after = Ingest(IngestRules.Python0320);
        Assert.That(Rows(after, "samples").Select(r => (string)r["source_name"]!).Order(),
            Is.EqualTo(Rows(before, "samples").Select(r => (string)r["source_name"]!).Order()));
    }
}
