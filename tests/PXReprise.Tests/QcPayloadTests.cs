using System.Text;
using System.Text.Json.Nodes;
using PXReprise.Qc;

namespace PXReprise.Tests;

/// <summary>
/// The qc-payload port. Expected values were produced by aging's pipeline/bin/qc_payload.py on the same inputs; the three
/// historical bugs (stem() and .toml, nearest-rank RT coverage, mbr_kept = 0) and the ± tolerance parse are pinned here.
/// </summary>
public class QcPayloadTests
{
    [TestCase("QE-002106_GM1_a-calib.toml", "QE-002106_GM1_a")]   // bug 1: .toml left on made calibration_ok false for every file
    [TestCase("run.v2.RAW", "run.v2")]                           // a dot inside the name survives; the extension is case-blind
    [TestCase("x.mzML", "x")]
    [TestCase(@"sub\dir\a-calib.mzXML", "a")]
    public void StemMatchesTheContractsFileKey(string name, string expected) =>
        Assert.That(QcPayloadBuilder.Stem(name), Is.EqualTo(expected));

    [Test]
    public void IqrIsPythonsInclusiveQuantiles()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QcPayloadBuilder.Iqr(Enumerable.Range(1, 10).Select(i => (double)i).ToList()), Is.EqualTo(4.5));
            Assert.That(QcPayloadBuilder.Iqr(new[] { 2.0, 1.0 }), Is.EqualTo(0.5));
            Assert.That(QcPayloadBuilder.Iqr(new[] { 1.0 }), Is.Null);
        });
    }

    [Test]
    public void HistogramClipsTailsIntoTheEndBins()
    {
        var h = QcPayloadBuilder.Histogram(new[] { -100.0, 0.0, 100.0 }, new[] { -1.0, 0.0, 1.0 });
        Assert.That(h["counts"]!.AsArray().Select(x => x!.GetValue<int>()), Is.EqualTo(new[] { 1, 2 }));
    }

    /// <summary>Bug 2: truncating int(0.99*(n-1)) on three IDs picks the middle one as the "99th", understating coverage ninefold.</summary>
    [Test]
    public void RtCoverageUsesNearestRank()
    {
        double cov = QcPayloadBuilder.IdRtCoverage(new[] { 50.0, 10.0, 11.0 }, 100);
        Assert.That(cov, Is.EqualTo(0.4));          // (50 - 10) / 100; truncation would give (11 - 10) / 100
    }

    /// <summary>The tolerances are "±3.1000 PPM" strings in UTF-8: match the number, never the sign.</summary>
    [Test]
    public void CalibrationTolerancesParseFromUtf8PlusMinusStrings()
    {
        string dir = TestSupport.TempDir();
        File.WriteAllText(Path.Combine(dir, "a-calib.toml"),
            "PrecursorMassTolerance = \"\u00b13.1000 PPM\"\nProductMassTolerance = \"\u00b112.5 PPM\"\n", new UTF8Encoding(false));
        var cal = QcPayloadBuilder.ParseCalibration(dir);
        Assert.Multiple(() =>
        {
            Assert.That(cal.Keys, Is.EquivalentTo(new[] { "a" }));
            Assert.That(cal["a"]["calibration_ok"]!.GetValue<bool>(), Is.True);
            Assert.That(cal["a"]["cal_precursor_tol_ppm"]!.GetValue<double>(), Is.EqualTo(3.1));
            Assert.That(cal["a"]["cal_product_tol_ppm"]!.GetValue<double>(), Is.EqualTo(12.5));
        });
    }

    /// <summary>Bug 3: a file whose every MBR candidate was rejected has mbr_kept = 0, not an absent key.</summary>
    [Test]
    public void AFileWithOnlyRejectedTransfersHasMbrKeptZero()
    {
        string dir = TestSupport.TempDir();
        string peaks = TestSupport.WriteFile(dir, "peaks.tsv",
            "File Name\tPeak Detection Type\tRandom RT\tPIP Q-Value\tDecoy Peptide\n" +
            "a.raw\tMSMS\tFalse\t\tFalse\n" +
            "a.raw\tMBR\tFalse\t0.001\tFalse\n" +
            "b.raw\tMBR\tFalse\t0.5\tFalse\n");
        var p = QcPayloadBuilder.ParsePeaks(peaks);
        Assert.Multiple(() =>
        {
            Assert.That(p["a"]["msms_peaks"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(p["a"]["mbr_kept"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(p["b"]["mbr_kept"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(p["b"]["msms_peaks"]!.GetValue<int>(), Is.EqualTo(0));
        });
    }

    private static (string Search, string Qc) MiniRun(bool spectralCounts)
    {
        string root = TestSupport.TempDir();
        string task = Path.Combine(root, "04_search", "mm", "Task3SearchTask");
        string cal = Path.Combine(root, "04_search", "mm", "Task1CalibrationTask");
        string qc = Path.Combine(root, "02b_qc");
        Directory.CreateDirectory(task);
        Directory.CreateDirectory(cal);
        Directory.CreateDirectory(qc);
        File.WriteAllText(Path.Combine(task, "results.txt"),
            "All target PSMs with q-value <= 0.01: 3\r\nAll target peptides with q-value <= 0.01: 3\r\n" +
            "All target protein groups with q-value <= 0.01 (1% FDR): 2\r\n" +
            "a - MS2 Scans: 100\r\na - Target PSMs with q-value <= 0.01: 2\r\nb - MS2 Scans: 90\r\nb - Target PSMs with q-value <= 0.01: 1\r\n");
        File.WriteAllText(Path.Combine(cal, "a-calib.toml"), "PrecursorMassTolerance = \"\u00b14.0 PPM\"\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(task, "AllPSMs.psmtsv"),
            "File Name\tDecoy/Contaminant/Target\tQValue\tQValue Notch\tNotch\tPrecursor Charge\tMissed Cleavages\tScan Retention Time\tMass Diff (ppm)\tMatched Ion Mass Diff (Ppm)\n" +
            "a.raw\tT\t0.001\t0.001\t0\t2\t0\t10\t1.5\t[b2+1:1.0, y3+1:-2.5]\n" +
            "a.raw\tT\t0.001\t0.001\t1\t3\t1\t50\t0.5\t[b2+1:0.5]\n" +
            "a.raw\tC\t0.001\t0.001\t0\t2\t0\t20\t0.1\t\n" +
            "b.raw\tT\t0.001\t0.001\t0|1\t2\t0\t30\t0.0\t\n" +
            "b.raw\tT\t0.005\t0.005\t0\t2\t0\t30\t-1.0\t[y1+1:3.0]\n" +
            "b.raw\tD\t0.001\t0.001\t0\t2\t0\t30\t9\t\n");
        File.WriteAllText(Path.Combine(task, "AllQuantifiedPeaks.tsv"),
            "File Name\tPeak Detection Type\tRandom RT\tPIP Q-Value\tDecoy Peptide\na.raw\tMSMS\tFalse\t\tFalse\nb.raw\tMBR\tFalse\t0.9\tFalse\n");
        string sc = spectralCounts ? "\tSpectralCount_a\tSpectralCount_b" : "";
        File.WriteAllText(Path.Combine(task, "AllQuantifiedProteinGroups.tsv"),
            $"Protein Accession\tProtein Decoy/Contaminant/Target\tProtein QValue\tIntensity_a\tIntensity_b{sc}\n" +
            $"P1\tT\t0.001\t100\t50{(spectralCounts ? "\t2\t0" : "")}\n" +
            $"P2\tC\t0.001\t100\t{(spectralCounts ? "\t1\t1" : "")}\n" +
            $"P3\tD\t0.001\t999\t999{(spectralCounts ? "\t5\t5" : "")}\n");
        File.WriteAllText(Path.Combine(qc, "qc_report.json"), "{\"a.raw\": {\"run_minutes\": 100.0}, \"b.raw\": {\"run_minutes\": 90.0}}");
        return (Path.Combine(root, "04_search"), qc);
    }

    [Test]
    public void BuildsTheContractsShapeFromASmallSearch()
    {
        var (search, qc) = MiniRun(spectralCounts: true);
        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "2026-09-28/PXD000001", "1.1.11", QcBins.Vendored(),
            schema: QcPayloadBuilder.SchemaV1);
        var files = r.Payload["files"]!.AsArray().ToDictionary(f => f!["file"]!.GetValue<string>(), f => f!["metrics"]!.AsObject());
        var dm = r.Payload["dataset_metrics"]!;
        Assert.Multiple(() =>
        {
            Assert.That(r.Payload["schema"]!.GetValue<string>(), Is.EqualTo("qc-payload/1"));
            Assert.That(dm["dataset_psms"]!.GetValue<int>(), Is.EqualTo(3));
            Assert.That(dm["dataset_protein_groups"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(dm["protein_groups_quantified"]!.GetValue<int>(), Is.EqualTo(2));   // P1 and P2; the decoy never counts
            Assert.That(files["a"]["calibration_ok"]!.GetValue<bool>(), Is.True);
            Assert.That(files["a"]["cal_precursor_tol_ppm"]!.GetValue<double>(), Is.EqualTo(4.0));
            Assert.That(files["b"]["calibration_ok"]!.GetValue<bool>(), Is.False);
            Assert.That(files["a"]["contaminant_psm_share"]!.GetValue<double>(), Is.EqualTo(0.3333));
            Assert.That(files["a"]["notch_frac"]!.GetValue<double>(), Is.EqualTo(0.5));
            Assert.That(files["a"]["id_rt_coverage"]!.GetValue<double>(), Is.EqualTo(0.4));
            Assert.That(files["a"]["contaminant_intensity_frac"]!.GetValue<double>(), Is.EqualTo(0.5));
            Assert.That(files["b"]["mbr_kept"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(files["b"]["pg_missing_frac_msms"]!.GetValue<double>(), Is.EqualTo(0.5));   // b identified P2 only
            // The ambiguous-notch b row fails the 1% set; the q = 0.005 row stays: one PSM, Notch 0.
            Assert.That(files["b"]["precursor_ppm_median"]!.GetValue<double>(), Is.EqualTo(-1.0));
            Assert.That(files["b"]["precursor_ppm_iqr"], Is.Null);
            Assert.That(r.Payload["dataset"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.Contains("VENDORED"));
        });
    }

    /// <summary>
    /// qc-payload/2 (qc D13, our qc 010): the per-file counts are the whole search's 1% set restricted to the file
    /// (pxreprise:DEF-*-1PCT-RUN v1), and the payload lists the databases the search read (qc D14).
    /// </summary>
    [Test]
    public void SchemaTwoCountsTheWholeSearchsOnePercentSetPerFile()
    {
        var (search, qc) = MiniRun(spectralCounts: true);
        string task = Path.Combine(search, "mm", "Task3SearchTask");
        File.WriteAllText(Path.Combine(task, "AllPSMs.psmtsv"),
            "File Name\tDecoy/Contaminant/Target\tQValue\tQValue Notch\tNotch\tFull Sequence\tBase Sequence\tPrecursor Charge\tMissed Cleavages\tScan Retention Time\tMass Diff (ppm)\tMatched Ion Mass Diff (Ppm)\n" +
            "a.raw\tT\t0.001\t0.001\t0\tPEPTIDEK\tPEPTIDEK\t2\t0\t10\t1.5\t\n" +
            "a.raw\tT\t0.001\t0.001\t1\tPEPTM[Common Variable:Oxidation on M]IDEK\tPEPTMIDEK\t3\t1\t50\t0.5\t\n" +
            "a.raw\tC\t0.001\t0.001\t0\tKERATINK\tKERATINK\t2\t0\t20\t0.1\t\n" +
            "b.raw\tT\t0.001\t0.001\t0|1\tPEPTIDEK\tPEPTIDEK\t2\t0\t30\t0.0\t\n" +            // ambiguous notch: not in the set
            "b.raw\tT\t0.005\t0.005\t0\tANOTHERK\tANOTHERK\t2\t0\t30\t-1.0\t\n" +
            "b.raw\tT\t0.002\t0.002\t0\tANOTHERK|ANOTHEKR\tANOTHERK|ANOTHEKR\t2\t0\t31\t0.2\t\n" +  // ambiguous sequence: supports none
            "b.raw\tD\t0.001\t0.001\t0\tDECOYK\tDECOYK\t2\t0\t30\t9\t\n");
        File.WriteAllText(Path.Combine(task, "AllPeptides.psmtsv"),
            "File Name\tDecoy/Contaminant/Target\tQValue\tQValue Notch\tFull Sequence\n" +
            "a.raw\tT\t0.001\t0.001\tPEPTIDEK\n" +
            "a.raw\tT\t0.5\t0.5\tPEPTM[Common Variable:Oxidation on M]IDEK\n" +    // above 1% at peptide level
            "b.raw\tT\t0.004\t0.004\tANOTHERK\n");
        File.WriteAllText(Path.Combine(task, "AllQuantifiedProteinGroups.tsv"),
            "Protein Accession\tUnique Peptides\tShared Peptides\tProtein Decoy/Contaminant/Target\tProtein QValue\tIntensity_a\tIntensity_b\tSpectralCount_a\tSpectralCount_b\n" +
            "P1\tPEPTIDEK\t\tT\t0.001\t100\t50\t2\t0\n" +
            "P2\tANOTHERK|KERATINK\t\tC\t0.001\t100\t\t1\t1\n" +       // a contaminant group counts (DEF-PROTEINGROUP-1PCT)
            "P4\tPEPTMIDEK\t\tT\t0.2\t10\t10\t1\t0\n" +                 // above 1%
            "P3\tDECOYK\t\tD\t0.001\t999\t999\t5\t5\n");
        File.WriteAllText(Path.Combine(search, "provenance.json"),
            "{\"inputs\": [" +
            "{\"path\": \"run/PXD000001/04_search/tasks/3_SearchTask.toml\", \"sha256\": \"" + new string('a', 64) + "\"}," +
            "{\"path\": \"db/UP000000589_all_genecentric_2026_03_agingPTM-v4.xml\", \"sha256\": \"" + new string('b', 64) + "\"}," +
            "{\"path\": \"F:\\\\aging_batch\\\\pxreprise-x\\\\profiles\\\\lists\\\\MetaMorpheusContaminants_NoUPS.xml\", \"sha256\": \"" + new string('c', 64) + "\"}," +
            "{\"path\": \"db/uniprotkb_proteome_UP000000589_AND_revi_2026-09-22.xml\", \"sha256\": \"" + new string('d', 64) + "\"}," +
            "{\"path\": \"run/PXD000001/02_fetch/spectra/a.raw\", \"sha256\": \"" + new string('e', 64) + "\"}]}");

        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.12", QcBins.Vendored(), excludedFiles: new[] { "c.raw" });
        var files = r.Payload["files"]!.AsArray().ToDictionary(f => f!["file"]!.GetValue<string>(), f => f!["metrics"]!.AsObject());
        var dbs = r.Payload["dataset"]!["search_databases"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(r.Payload["schema"]!.GetValue<string>(), Is.EqualTo("qc-payload/2"));
            Assert.That(r.Payload["definitions"]!["psms"]!["id"]!.GetValue<string>(), Is.EqualTo("pxreprise:DEF-PSM-1PCT-RUN"));
            Assert.That(r.Payload["definitions"]!["protein_groups"]!["id"]!.GetValue<string>(), Is.EqualTo("pxreprise:DEF-PROTEINGROUP-1PCT-RUN"));
            // psms: exactly T, both q <= 0.01, unambiguous notch. a: 2. b: the q 0.005 row and the ambiguous-sequence row.
            Assert.That((files["a"]["psms"]!.GetValue<int>(), files["b"]["psms"]!.GetValue<int>()), Is.EqualTo((2, 2)));
            // peptides: a's oxidised form is above 1% at peptide level; b's ambiguous sequence supports none.
            Assert.That((files["a"]["peptides"]!.GetValue<int>(), files["b"]["peptides"]!.GetValue<int>()), Is.EqualTo((1, 1)));
            // groups: a supports P1 (P4 is above 1%); b supports P2, a contaminant group. The decoy never counts.
            Assert.That((files["a"]["protein_groups"]!.GetValue<int>(), files["b"]["protein_groups"]!.GetValue<int>()), Is.EqualTo((1, 1)));
            Assert.That(files["a"]["ms2_scans"]!.GetValue<int>(), Is.EqualTo(100), "ms2_scans stays MetaMorpheus's per-file line");
            Assert.That(files.ContainsKey("c"), Is.False, "c is listed only when the QC report knows it");
            Assert.That(dbs.Select(d => d!["file"]!.GetValue<string>()), Is.EqualTo(new[]
                { "UP000000589_all_genecentric_2026_03_agingPTM-v4.xml", "MetaMorpheusContaminants_NoUPS.xml", "uniprotkb_proteome_UP000000589_AND_revi_2026-09-22.xml" }));
            Assert.That((dbs[0]!["kind"]!.GetValue<string>(), dbs[0]!["uniprot_release"]!.GetValue<string>()), Is.EqualTo(("uniprot_canonical", "2026_03")));
            Assert.That((dbs[1]!["kind"], dbs[1]!["uniprot_release"]), Is.EqualTo(((JsonNode?)null, (JsonNode?)null)));
            Assert.That((dbs[2]!["kind"]!.GetValue<string>(), dbs[2]!["uniprot_release"]), Is.EqualTo(("uniprot_reviewed", (JsonNode?)null)),
                "the reviewed file's name holds a download date, not a UniProt release");
            Assert.That(r.Payload["dataset"]!["notes"]!.AsArray()[0]!.GetValue<string>(), Does.StartWith("One PSM population per file"));
        });

        // The same search as /1: MetaMorpheus's per-file lines, aging's definitions, no database list.
        var v1 = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.12", QcBins.Vendored(), schema: QcPayloadBuilder.SchemaV1);
        var f1 = v1.Payload["files"]!.AsArray().ToDictionary(f => f!["file"]!.GetValue<string>(), f => f!["metrics"]!.AsObject());
        Assert.Multiple(() =>
        {
            Assert.That((f1["a"]["psms"]!.GetValue<int>(), f1["b"]["psms"]!.GetValue<int>()), Is.EqualTo((2, 1)));
            Assert.That(f1["a"].ContainsKey("peptides"), Is.False);
            Assert.That(v1.Payload["dataset"]!.AsObject().ContainsKey("search_databases"), Is.False);
            Assert.That(v1.Payload["definitions"]!["psms"]!["id"]!.GetValue<string>(), Is.EqualTo("aging:DEF-PSM-1PCT-RUN"));
        });
    }

    /// <summary>/2 with no AllPeptides.psmtsv and no group peptide columns: those counts are absent (not measured), not 0.</summary>
    [Test]
    public void SchemaTwoLeavesOutACountItCannotMeasure()
    {
        var (search, qc) = MiniRun(spectralCounts: true);
        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.12", QcBins.Vendored());
        var a = r.Payload["files"]!.AsArray().Single(f => f!["file"]!.GetValue<string>() == "a")!["metrics"]!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(a["psms"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(a.ContainsKey("peptides") || a.ContainsKey("protein_groups"), Is.False);
            Assert.That(r.Payload["dataset"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.StartsWith("peptides is NOT REPORTED"));
            Assert.That(r.Payload["dataset"]!.AsObject().ContainsKey("search_databases"), Is.False, "no provenance, no list");
        });
    }

    /// <summary>
    /// qc PXR-C1 (qctemplates 0.2.0, qc D12, QC-Q20): a file left out of the search is listed with `excluded` = why. qc's
    /// schema takes a non-empty string; the `true` qc 001 asked for was rejected on PXD014274 (2026-10-12).
    /// </summary>
    [Test]
    public void AFileLeftOutOfTheSearchIsMarkedExcludedWithItsReason()
    {
        var (search, qc) = MiniRun(spectralCounts: true);
        File.WriteAllText(Path.Combine(qc, "qc_report.json"),
            "{\"a.raw\": {\"run_minutes\": 100.0}, \"b.raw\": {\"run_minutes\": 90.0}, "
            + "\"blank.raw\": {\"run_minutes\": 90.0, \"pass\": false, \"fail_reasons\": [\"too_few_ms2\"]}, \"c.raw\": {\"run_minutes\": 80.0, \"pass\": true}}");
        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.11", QcBins.Vendored(), excludedFiles: new[] { "blank.raw", "c.raw" },
            excludedReason: "excluded from the search by --exclude");
        var files = r.Payload["files"]!.AsArray().ToDictionary(f => f!["file"]!.GetValue<string>(), f => f!.AsObject());
        Assert.Multiple(() =>
        {
            Assert.That(files["blank"]["excluded"]!.GetValue<string>(), Is.EqualTo("qc: too_few_ms2"), "the QC report's own reason");
            Assert.That(files["c"]["excluded"]!.GetValue<string>(), Is.EqualTo("excluded from the search by --exclude"),
                "no QC reason: the search's excluded_files.reason");
            Assert.That(files["a"].ContainsKey("excluded"), Is.False, "a searched file is never marked, even with zero results (qc D6)");
        });
    }

    /// <summary>An excluded file with no QC reason and no reason from the search still gets a non-empty string (qc's minLength 1).</summary>
    [Test]
    public void AnExcludedFileWithNoRecordedReasonStillGetsOne()
    {
        var (search, qc) = MiniRun(spectralCounts: true);
        File.WriteAllText(Path.Combine(qc, "qc_report.json"),
            "{\"a.raw\": {\"run_minutes\": 100.0}, \"b.raw\": {\"run_minutes\": 90.0}, \"c.raw\": {\"run_minutes\": 80.0}}");
        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.11", QcBins.Vendored(), excludedFiles: new[] { "c.raw" }, excludedReason: " ");
        var c = r.Payload["files"]!.AsArray().Single(f => f!["file"]!.GetValue<string>() == "c")!;
        Assert.That(c["excluded"]!.GetValue<string>(), Is.EqualTo("excluded from the search"));
    }

    /// <summary>No SpectralCount_ columns: the _msms variant is not measurable, so it is OMITTED (not 0) and the notes say so.</summary>
    [Test]
    public void PgMissingFracIsOmittedWhenNotMeasurable()
    {
        var (search, qc) = MiniRun(spectralCounts: false);
        var r = QcPayloadBuilder.Build(search, qc, "PXD000001", "x", "1.1.11", QcBins.Vendored());
        Assert.Multiple(() =>
        {
            Assert.That(r.ProteinGroupsQuantified, Is.Null);
            Assert.That(r.Payload["files"]!.AsArray().All(f => !f!["metrics"]!.AsObject().ContainsKey("pg_missing_frac_msms")), Is.True);
            Assert.That(r.Payload["dataset"]!["notes"]!.AsArray().Select(n => n!.GetValue<string>()), Has.Some.Contains("NOT REPORTED"));
        });
    }

    [Test]
    public void APsmTableWithoutQValueNotchIsRefused()
    {
        string dir = TestSupport.TempDir();
        string p = TestSupport.WriteFile(dir, "AllPSMs.psmtsv", "File Name\tQValue\n");
        Assert.Throws<InvalidDataException>(() => QcPayloadBuilder.ParsePsms(p, new Dictionary<string, double?>(), QcBins.Vendored()));
    }

    [Test]
    public void MachineReadsTheOptionalQcPython()
    {
        string dir = TestSupport.TempDir();
        string m = TestSupport.WriteFile(dir, "m.toml",
            "work_root = \"F:/w\"\nqc_python = \"C:/py/python.exe\"\n[metamorpheus]\n\"1.1.11\" = \"C:/mm/CMD.exe\"\n");
        Assert.That(Config.Machine.Load(m).QcPython, Is.EqualTo("C:/py/python.exe"));
    }
}
