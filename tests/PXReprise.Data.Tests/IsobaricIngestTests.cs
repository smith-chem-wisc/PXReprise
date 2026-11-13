using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;

namespace PXReprise.Data.Tests;

/// <summary>G105: a TMT search's tables (QuantProject DEF-TMT-*) ingest as one assay per (file, channel) of the search's
/// design, with values placed by column label, groups from AllProteinGroups.tsv and the raw table copied verbatim.</summary>
/// <remarks>The fixture's label-free search is turned into a two-file TMT10 search as MetaMorpheus 1.1.14 writes one: no
/// FlashLFQ tables, a calibrated and an uncalibrated file, and columns <c>{sample name}_{file}_{channel}</c>. Its declared
/// release moves from 1.1.11 to 1.1.14, because a multiplex search up to 1.1.13 is refused (MetaMorpheus #2941).</remarks>
public class IsobaricIngestTests
{
    private const string Accession = "PXD999999";
    private const string FileA = "QE-002106_GM1_a";
    private const string FileB = "QE-002107_GM1_b";
    private string _root = "";

    private string Manifest => Path.Combine(_root, "manifest.yaml");
    private string Results => Path.Combine(_root, "work_root", "run_test", Accession, "04_search", "mm", "Task3SearchTask");
    private string SdrfPath => Path.Combine(_root, "work_root", "run_test", Accession, "02_fetch", "metadata", $"{Accession}.sdrf.tsv");

    // (searched file, sample name, channel, sample type): file a calibrated, file b searched as acquired.
    private static readonly (string File, string Sample, string Tag, string Type)[] DesignRows =
    [
        ($"{FileA}-calib", "s1", "126", "study sample"),
        ($"{FileA}-calib", "s2", "127N", "study sample"),
        ($"{FileA}-calib", "ref", "131N", "reference"),
        (FileB, "s1", "126", "study sample"),
        (FileB, "s2", "127N", "study sample"),
        (FileB, "ref", "131N", "reference"),
    ];

    private static string Label((string File, string Sample, string Tag, string Type) d) => $"{d.Sample}_{d.File}_{d.Tag}";

    private static string RepoRoot() => DataPaths.Root;

    private static void Write(string path, IEnumerable<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));

    [SetUp]
    public void MakeTheSearchIsobaric()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-isobaric-" + Guid.NewGuid().ToString("N"));
        var from = DataPaths.Instance;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(from, file)));

        var text = File.ReadAllText(Manifest);
        Assert.That(text, Does.Contain("    quant_method: label-free\n"));
        File.WriteAllText(Manifest, text.Replace("    quant_method: label-free\n",
            "    quant_method: TMT\n    labelling: TMT\n    labelling_plex: 10\n"), new System.Text.UTF8Encoding(false));
        SetRelease("1.1.14");

        // A TMT search writes groups to AllProteinGroups.tsv and no FlashLFQ table.
        File.Move(Path.Combine(Results, "AllQuantifiedProteinGroups.tsv"), Path.Combine(Results, "AllProteinGroups.tsv"));
        var peptides = File.ReadAllLines(Path.Combine(Results, "AllQuantifiedPeptides.tsv")).Skip(1)
            .Select(l => l.Split('\t')[0]).Where(s => s.Length > 0).Take(3).ToList();
        File.Delete(Path.Combine(Results, "AllQuantifiedPeptides.tsv"));
        File.Delete(Path.Combine(Results, "AllQuantifiedPeaks.tsv"));

        Write(Path.Combine(Results, Isobaric.DesignFileName),
            ["File\tPlex\tSample Name\tTMT Channel\tCondition\tBiological Replicate\tFraction\tTechnical Replicate\tSample Type",
             .. DesignRows.Select((d, i) => $"C:\\search\\Task1CalibrationTask\\{d.File}.{(d.File.EndsWith("-calib") ? "mzML" : "raw")}\tP1\t{d.Sample}\t{d.Tag}\tall\t{i % 3 + 1}\t{(d.File.StartsWith(FileA) ? 1 : 2)}\t1\t{d.Type}"),
            ]);
        // Columns in an order the design does not have, so only the label can place them.
        var columns = DesignRows.Select(Label).Reverse().ToList();
        Write(Path.Combine(Results, Isobaric.PeptideFileName),
            [string.Join("\t", ["Peptide", .. columns]),
             .. peptides.Select((p, i) => string.Join("\t", [p, .. columns.Select((_, j) => j == i ? "0" : $"{(i + 1) * 100 + j}")]))]);
        Write(Path.Combine(Results, Isobaric.ProteinFileName),
            [string.Join("\t", ["Protein Group", .. columns]),
             string.Join("\t", ["P68363|Q71U36", .. columns.Select((_, j) => $"{1000 + j}.5")]),
             string.Join("\t", ["P05141", .. columns.Select(c => c == Label(DesignRows[0]) ? "0" : "7")]),
             string.Join("\t", ["Q00000", .. columns.Select(_ => "3")])]);
        Write(Path.Combine(Results, Isobaric.RawFileName),
            ["File\tScan\tBaseSequence\tFullSequence\tAccession\tIsDecoy\tScore\tReporter_1\tReporter_2\tReporter_3",
             $"C:\\spectra\\{FileB}.raw\t12\tPEPTIDE\tPEPTIDE\tP05141\tT\t12.5\t1\t2\t3"]);
    }

    [TearDown]
    public void Remove()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private IngestResult Ingest() => Ingester.Ingest(Manifest, Accession, Path.Combine(_root, "store"), overwrite: true);

    private string SearchProvenance => Path.Combine(_root, "work_root", "run_test", Accession, "04_search", "provenance.json");

    /// <summary>Declares the search's MetaMorpheus release in its provenance and in the manifest entry (the ingest reads
    /// the provenance first, the entry when the provenance names none); null removes both.</summary>
    private void SetRelease(string? release)
    {
        var provenance = File.ReadAllText(SearchProvenance);
        var manifest = File.ReadAllText(Manifest);
        var inProvenance = new System.Text.RegularExpressions.Regex(@"(""MetaMorpheus"":\s*\{\s*""release"":\s*)""[^""]*""");
        var inManifest = new System.Text.RegularExpressions.Regex(@"(?m)^    metamorpheus: ""[^""]*""\n");
        Assert.That(inProvenance.IsMatch(provenance) && inManifest.IsMatch(manifest), "the fixture declares its release in both");
        File.WriteAllText(SearchProvenance, inProvenance.Replace(provenance, release is null ? "$1null" : $"$1\"{release}\""),
            new System.Text.UTF8Encoding(false));
        File.WriteAllText(Manifest, inManifest.Replace(manifest, release is null ? "" : $"    metamorpheus: \"{release}\"\n"),
            new System.Text.UTF8Encoding(false));
    }

    private static List<Row> Table(IngestResult result, string table) =>
        ArrowTables.ReadParquet(Path.Combine(result.BundlePath, table + ".parquet")).Rows;

    private static List<string> Codes(IngestResult result) => Table(result, "findings").Select(r => (string)r["code"]!).ToList();

    private string QcReport => Path.Combine(_root, "work_root", "run_test", Accession, "02b_qc", "qc_report.json");

    /// <summary>Gives each searched file's QC entry an <c>sps_ms3</c>, as PXReprise's QC stage writes one (their 098).</summary>
    private void SetSpsMs3(bool? a, bool? b)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(QcReport))!.AsObject();
        foreach (var (file, value) in new[] { ($"{FileA}.raw", a), ($"{FileB}.raw", b) })
            if (value is { } v) doc[file]!["sps_ms3"] = v;
        File.WriteAllText(QcReport, doc.ToJsonString(), new System.Text.UTF8Encoding(false));
    }

    /// <summary>Turns the reference channel into one the design marks <c>empty</c>, as PXReprise's default design writes
    /// it from v0.4.41: no sample name, so MetaMorpheus labels its columns <c>{file}_{channel}</c> (PXReprise 098).</summary>
    private void MarkTheReferenceEmpty()
    {
        var design = Path.Combine(Results, Isobaric.DesignFileName);
        File.WriteAllText(design, File.ReadAllText(design).Replace("\tref\t131N\t", "\t\t131N\t").Replace("\treference\n", "\tempty\n"),
            new System.Text.UTF8Encoding(false));
        foreach (var table in new[] { Isobaric.PeptideFileName, Isobaric.ProteinFileName })
        {
            var path = Path.Combine(Results, table);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\tref_", "\t"), new System.Text.UTF8Encoding(false));
        }
    }

    /// <summary>DATAREPO-88 a and PXR-R54 (the user, 2026-11-12): every assay keeps its design's plex and sample type; a
    /// channel marked <c>empty</c> is an assay with its values and no sample.</summary>
    [Test]
    public void AnEmptyChannelKeepsItsValuesAndIsNoSample()
    {
        MarkTheReferenceEmpty();
        var result = Ingest();
        var assays = Table(result, "assays");
        var empty = assays.Where(a => Equals(a["channel"], "TMT131N")).ToList();
        var emptyIds = empty.Select(a => (string)a["assay_id"]!).ToHashSet();
        var finding = Table(result, "findings").Single(f => Equals(f["code"], "isobaric_reference_channels"));
        Assert.Multiple(() =>
        {
            Assert.That(empty, Has.Count.EqualTo(2));
            Assert.That(empty.Select(a => a["sample_id"]), Is.All.Null, "no sample was put in it");
            Assert.That(empty.Select(a => a["design_sample_type"]), Is.All.EqualTo("empty"));
            Assert.That(assays.Select(a => a["plex"]), Is.All.EqualTo("P1"));
            Assert.That(assays.Where(a => !emptyIds.Contains((string)a["assay_id"]!)).Select(a => a["design_sample_type"]), Is.All.EqualTo("study sample"));
            Assert.That(Table(result, "samples").Select(s => (string)s["sample_id"]!), Is.EquivalentTo(new[] { $"{Accession}:s1", $"{Accession}:s2" }));
            Assert.That(Table(result, "quant_values").Count(v => emptyIds.Contains((string)v["assay_id"]!)), Is.GreaterThan(0), "its values are kept");
            Assert.That((string)finding["message"]!, Does.Contain("`Sample Type` empty").And.Contain("its assay has no `sample_id`"));
        });
    }

    /// <summary>DATAREPO-88 b: each run's reporter level from its QC entry's <c>sps_ms3</c>, and the dataset's scale from
    /// its runs: one level is that scale, two are mixed, an unknown run makes it not recorded; a label-free search is
    /// <c>label_free_apex</c>. With the channel view (DATAREPO-88 a) in the catalog.</summary>
    [TestCase(true, true, "ms3", "ms3", "reporter_ms3")]
    [TestCase(false, false, "ms2", "ms2", "reporter_ms2")]
    [TestCase(true, false, "ms3", "ms2", "reporter_level_mixed")]
    [TestCase(true, null, "ms3", null, "reporter_level_not_recorded")]
    public void EachRunSaysWhereItsReportersWereReadAndTheDatasetItsScale(bool? a, bool? b, string? levelA, string? levelB, string scale)
    {
        SetSpsMs3(a, b);
        MarkTheReferenceEmpty();
        var result = Ingest();
        var levels = Table(result, "runs").ToDictionary(r => (string)r["file_name"]!, r => r["reporter_level"]);
        var manifest = PXReprise.Ingest.Manifest.Load(Manifest);
        var bundles = PXReprise.Catalog.CatalogBuilder.SelectBundles(manifest, [Accession], Path.Combine(_root, "store"));
        var catalog = PXReprise.Catalog.CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, "catalog.duckdb"), manifest: manifest);
        Assert.Multiple(() =>
        {
            Assert.That((levels[$"{FileA}.raw"], levels[$"{FileB}.raw"]), Is.EqualTo((levelA, levelB)));
            Assert.That(PXReprise.Catalog.CatalogBuilder.RunQuery(catalog.Path, "SELECT quant_scale FROM dataset_overview").Rows.Single()[0], Is.EqualTo(scale));
            var channels = PXReprise.Catalog.CatalogBuilder.RunQuery(catalog.Path,
                "SELECT plex, channel, sample_id, design_sample_type FROM isobaric_channels ORDER BY run_id, channel").Rows;
            Assert.That(channels, Has.Count.EqualTo(6));
            Assert.That(channels.Where(c => Equals(c[1], "TMT131N")).Select(c => (c[2], c[3])), Is.All.EqualTo(((object?)null, (object?)"empty")));
            Assert.That(channels.Where(c => Equals(c[1], "TMT126")).Select(c => c[2]), Is.All.EqualTo($"{Accession}:s1"),
                "one sample on every fraction of its plex");
        });
    }

    [Test]
    public void EachFileAndChannelOfTheDesignIsAnAssayOnTheDesignsSample()
    {
        var result = Ingest();
        var assays = Table(result, "assays");
        Assert.Multiple(() =>
        {
            Assert.That(assays.Select(a => (string)a["assay_id"]!), Is.EquivalentTo(DesignRows.Select(d =>
                $"{Accession}:{Usi.StripPipelineSuffix(d.File)}:TMT{d.Tag}")));
            Assert.That(assays.Select(a => (string)a["assay_kind"]!).Distinct(), Is.EqualTo(new[] { "run" }));
            // The SDRF's rows say label free, so no channel finds an SDRF sample: each is on the design's, by name.
            Assert.That(Table(result, "samples").Select(s => (string)s["sample_id"]!),
                Is.EquivalentTo(new[] { $"{Accession}:s1", $"{Accession}:s2", $"{Accession}:ref" }));
            Assert.That(assays.Where(a => (string)a["channel"]! == "TMT131N").Select(a => (string)a["sample_id"]!).Distinct(),
                Is.EqualTo(new[] { $"{Accession}:ref" }));
            Assert.That(Codes(result), Has.Member("isobaric_channels_not_in_sdrf"));
            Assert.That(Codes(result), Has.Member("isobaric_reference_channels"));
            Assert.That(Codes(result), Has.Member("isobaric_files_uncalibrated"), "file b was searched as acquired");
            Assert.That(Codes(result), Has.Member("occupancy_not_ingested"));
        });
    }

    [Test]
    public void AValueIsPlacedByItsColumnLabelAndAZeroIsNoRow()
    {
        var result = Ingest();
        var values = Table(result, "quant_values");
        var groups = values.Where(v => (string)v["definition_id"]! == Definitions.TmtProtein.DefinitionId).ToList();
        var peptides = values.Where(v => (string)v["definition_id"]! == Definitions.TmtPeptide.DefinitionId).ToList();
        // The protein table's columns are the design reversed: column j is design row 5 - j.
        double Group(string group, int designRow) => (double)groups.Single(v =>
            (string)v["feature_id"]! == $"{Accession}:{group}"
            && (string)v["assay_id"]! == $"{Accession}:{Usi.StripPipelineSuffix(DesignRows[designRow].File)}:TMT{DesignRows[designRow].Tag}")["value"]!;
        Assert.Multiple(() =>
        {
            Assert.That(Group("P68363;Q71U36", 0), Is.EqualTo(1005.5));
            Assert.That(Group("P68363;Q71U36", 5), Is.EqualTo(1000.5));
            Assert.That(groups.Count(v => (string)v["feature_id"]! == $"{Accession}:P05141"), Is.EqualTo(5), "its 0 is no value");
            Assert.That(groups.Any(v => (string)v["feature_id"]! == $"{Accession}:Q00000"), Is.False, "no written group is Q00000");
            Assert.That(peptides, Has.Count.EqualTo(3 * 6 - 3), "one 0 on each of three rows");
            Assert.That(values.All(v => v["detection_type"] is null && v["pip_q_value"] is null), Is.True);
            Assert.That(Codes(result), Has.Member("isobaric_rows_unplaced_protein_groups"));
            Assert.That(Codes(result), Has.Member("isobaric_unlabelled_peptides"), "the fixture's peptides carry no TMT label");
            Assert.That(Table(result, "definitions").Select(d => (string)d["definition_id"]!),
                Is.SupersetOf(new[] { Definitions.TmtPeptide.DefinitionId, Definitions.TmtProtein.DefinitionId }));
        });
    }

    [Test]
    public void GroupsComeFromAllProteinGroupsWithTheirQValuesAndTheRawTableIsCopiedVerbatim()
    {
        var result = Ingest();
        var groups = Table(result, "protein_groups");
        Assert.Multiple(() =>
        {
            Assert.That(groups.Select(g => (string)g["protein_group_id"]!), Has.Member($"{Accession}:P68363;Q71U36"));
            Assert.That(groups.Where(g => g["q_value"] is not null), Is.Not.Empty);
            Assert.That(File.ReadAllBytes(Path.Combine(result.BundlePath, "sources", Isobaric.RawFileName)),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(Results, Isobaric.RawFileName))));
            Assert.That(File.Exists(Path.Combine(result.BundlePath, "sources", Isobaric.DesignFileName)), Is.True);
        });
    }

    [Test]
    public void ChannelsTakeTheirSdrfSamplesAndPridesTmt131IsTmt10s131N()
    {
        Write(SdrfPath,
            ["source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tassay name\tcomment[label]\tcomment[technical replicate]\tcomment[fraction identifier]\tcomment[data file]",
             .. new[] { FileA, FileB }.SelectMany(f => new[] { ("young", "TMT126"), ("old", "TMT127N"), ("pool", "TMT131") }
                 .Select(s => $"{s.Item1}\tHomo sapiens\t1\t{f}\t{s.Item2}\t1\t1\t{f}.raw"))]);
        var result = Ingest();
        var assays = Table(result, "assays").ToDictionary(a => (string)a["assay_id"]!, a => (string)a["sample_id"]!);
        Assert.Multiple(() =>
        {
            Assert.That(assays[$"{Accession}:{FileA}:TMT126"], Is.EqualTo($"{Accession}:young"));
            Assert.That(assays[$"{Accession}:{FileB}:TMT131N"], Is.EqualTo($"{Accession}:pool"));
            Assert.That(Codes(result), Has.No.Member("isobaric_channels_not_in_sdrf"));
        });
    }

    [Test]
    public void AColumnNoDesignRowLabelsIsRefused()
    {
        var path = Path.Combine(Results, Isobaric.ProteinFileName);
        var lines = File.ReadAllLines(path);
        lines[0] = lines[0].Replace(Label(DesignRows[2]), "ref_somewhere_else_131N");
        Write(path, lines);
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains("ref_somewhere_else_131N"));
    }

    [Test]
    public void AnIsobaricSearchWhoseEntryNamesNoReagentIsRefused()
    {
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("    labelling: TMT\n", "    labelling: none\n"), new System.Text.UTF8Encoding(false));
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains("labelling is 'none'"));
    }

    [Test]
    public void AMultiplexSearchUpTo1113IsRefused()
    {
        // MetaMorpheus #2941 (QuantProject 071): up to 1.1.13 a multiplex AllPeptides.psmtsv is PSM-level. The user,
        // 2026-11-12: re-search such a deposit rather than carry a warning for it.
        SetRelease("1.1.13");
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains("MetaMorpheus 1.1.13")
            .And.Message.Contains("#2941").And.Message.Contains("Re-search the deposit on MetaMorpheus 1.1.14 or later."));
        SetRelease("1.1.14");
        var result = Ingest();
        var definitions = Table(result, "definitions").ToDictionary(d => (string)d["definition_id"]!);
        Assert.Multiple(() =>
        {
            Assert.That(Codes(result), Has.No.Member("isobaric_peptide_q_psm_level"));
            Assert.That(Isobaric.PeptideQIsPsmLevel("1.1.13"), Is.True);
            Assert.That(Isobaric.PeptideQIsPsmLevel("1.1.9"), Is.True);
            Assert.That(Isobaric.PeptideQIsPsmLevel(null), Is.True, "an unknown release is not assumed to carry the fix");
            Assert.That(Isobaric.PeptideQIsPsmLevel("1.1.14"), Is.False);
            Assert.That(Isobaric.PeptideQIsPsmLevel("1.2.0"), Is.False);
            // QuantProject's text as v3.19 and v3.22 amend it, with v3.14's DEF-TMT amendment, from DATA-DEFINITIONS.md at
            // 9478315. v3.22 retracts v3.14's per-file claim (QuantProject 073): only the combined file is PSM-level.
            Assert.That((string)definitions[Definitions.TmtPeptide.DefinitionId]["version"]!, Is.EqualTo("v3.22"));
            Assert.That((string)definitions[Definitions.TmtProtein.DefinitionId]["text"]!,
                Does.Contain("MetaMorpheus #2940").And.Contain("#2941").And.Contain("at 9478315")
                    .And.Contain("Each per-file `_Peptides.psmtsv` is peptide-level")
                    .And.Not.Contain("and each per-file `_Peptides.psmtsv`, with the best PSM's PSM-level FDR columns")
                    // dataRepo's own note, not QuantProject's text: the refusal, and no finding named.
                    .And.Contain("It refuses a multiplex search by MetaMorpheus up to and including 1.1.13")
                    .And.Not.Contain("isobaric_peptide_q_psm_level"));
        });
        // Last, because it removes the release the other two set: an unknown release is not assumed to carry the fix.
        SetRelease(null);
        Assert.That(() => Ingest(), Throws.TypeOf<IngestException>().With.Message.Contains("of unknown release")
            .And.Message.Contains("record the release in the search's provenance"));
    }

    [Test]
    public void AnUnlabelledSearchUpTo1113IsNotRefused()
    {
        // The label-free fixture, unchanged (MetaMorpheus 1.1.11): the refusal belongs to multiplex searches alone.
        var root = Path.Combine(Path.GetTempPath(), "datarepo-labelfree-" + Guid.NewGuid().ToString("N"));
        try
        {
            var from = DataPaths.Instance;
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(root, Path.GetRelativePath(from, file)));
            Assert.That(File.ReadAllText(Path.Combine(root, "manifest.yaml")), Does.Contain("    metamorpheus: \"1.1.11\"\n"));
            var result = Ingester.Ingest(Path.Combine(root, "manifest.yaml"), Accession, Path.Combine(root, "store"), overwrite: true);
            Assert.That(Directory.Exists(result.BundlePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void TheIsobaricReaderIsInTheIdOfAnIsobaricBundleOnly()
    {
        var result = Ingest();
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(result.BundlePath, BundleWriter.ManifestName)))!;
        var roles = manifest["sources"]!.AsArray().Select(s => (string)s!["role"]!).ToList();
        Assert.That(roles, Is.SupersetOf(new[] { "isobaric_reader", "isobaric_design", "isobaric_peptide_quant", "isobaric_protein_quant", "isobaric_raw_quant", "protein_groups" }));
    }
}
