using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>G105: a TMT search's tables (QuantProject DEF-TMT-*) ingest as one assay per (file, channel) of the search's
/// design, with values placed by column label, groups from AllProteinGroups.tsv and the raw table copied verbatim.</summary>
/// <remarks>The fixture's label-free search is turned into a two-file TMT10 search as MetaMorpheus 1.1.13 writes one: no
/// FlashLFQ tables, a calibrated and an uncalibrated file, and columns <c>{sample name}_{file}_{channel}</c>.</remarks>
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

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static void Write(string path, IEnumerable<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));

    [SetUp]
    public void MakeTheSearchIsobaric()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-isobaric-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(from, file)));

        var text = File.ReadAllText(Manifest);
        Assert.That(text, Does.Contain("    quant_method: label-free\n"));
        File.WriteAllText(Manifest, text.Replace("    quant_method: label-free\n",
            "    quant_method: TMT\n    labelling: TMT\n    labelling_plex: 10\n"), new System.Text.UTF8Encoding(false));

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

    private static List<Row> Table(IngestResult result, string table) =>
        ArrowTables.ReadParquet(Path.Combine(result.BundlePath, table + ".parquet")).Rows;

    private static List<string> Codes(IngestResult result) => Table(result, "findings").Select(r => (string)r["code"]!).ToList();

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
    public void TheIsobaricReaderIsInTheIdOfAnIsobaricBundleOnly()
    {
        var result = Ingest();
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(result.BundlePath, BundleWriter.ManifestName)))!;
        var roles = manifest["sources"]!.AsArray().Select(s => (string)s!["role"]!).ToList();
        Assert.That(roles, Is.SupersetOf(new[] { "isobaric_reader", "isobaric_design", "isobaric_peptide_quant", "isobaric_protein_quant", "isobaric_raw_quant", "protein_groups" }));
    }
}
