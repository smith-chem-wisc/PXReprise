using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Differential;

namespace PXReprise.Tests;

/// <summary>
/// D84 (QuantProject 155, PXR-T33): the <c>differential</c> verb and its batch stage, on small search folders written here
/// in MetaMorpheus 1.1.15's layout. The statistics are mzLib's and tested there; these test what the stage feeds them and
/// what it records.
/// </summary>
public class DifferentialStageTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Tmt10 = { "126", "127N", "127C", "128N", "128C", "129N", "129C", "130N", "130C", "131N" };

    // ------------------------------------------------------------------ fixtures

    /// <summary>A finished search's provenance, as the batch writes it.</summary>
    private static void SearchProvenance(string search, string? isobaricKit = null)
    {
        var p = new JsonObject
        {
            ["pipeline"] = new JsonObject { ["version"] = "0.4.50+test" },
            ["tools"] = new JsonObject { ["MetaMorpheus"] = new JsonObject { ["release"] = "1.1.15" } },
            ["identification_filter"] = new JsonObject { ["counts"] = new JsonArray(new JsonObject { ["of"] = "PSMs", ["filter"] = "pep q-value", ["threshold"] = 0.01 }) },
            ["experimental_design"] = new JsonObject { ["condition_columns"] = new JsonArray("factor value[condition]"), ["kit"] = isobaricKit },
        };
        if (isobaricKit is not null) p["isobaric"] = new JsonObject { ["kit"] = isobaricKit, ["sps_ms3"] = true };
        Directory.CreateDirectory(search);
        File.WriteAllText(Path.Combine(search, "provenance.json"), p.ToJsonString());
    }

    /// <summary>
    /// A label-free search: one file per sample, <paramref name="conditions"/> one per file; 8 protein groups of 3 peptides,
    /// P1 four times as abundant in B as in A. With <paramref name="mbr"/>, each file but <paramref name="lostFile"/> holds a
    /// transferred value; the peaks table gives the lost file two MBR peaks that failed.
    /// </summary>
    private static string LabelFree(string[] conditions, bool mbr = true, int? lostFile = null, Func<int, string>? individual = null)
    {
        string search = Path.Combine(TestSupport.TempDir(), "04_search"), task = Path.Combine(search, "mm", "Task3SearchTask");
        Directory.CreateDirectory(task);
        Directory.CreateDirectory(Path.Combine(search, "mm", "Task Settings"));
        File.WriteAllText(Path.Combine(search, "mm", "Task Settings", "Task3SearchTaskconfig.toml"), $"[SearchParameters]\nMatchBetweenRuns = {(mbr ? "true" : "false")}\n");
        SearchProvenance(search);
        int n = conditions.Length;
        var files = Enumerable.Range(1, n).Select(i => $"f{i}-calib").ToArray();
        var bioreps = new Dictionary<string, int>();
        var design = new List<string> { "FileName\tCondition\tBiorep\tFraction\tTechrep" };
        for (int i = 0; i < n; i++)
            design.Add($"{files[i]}.mzML\t{conditions[i]}\t{bioreps[conditions[i]] = bioreps.GetValueOrDefault(conditions[i]) + 1}\t1\t1");
        File.WriteAllLines(Path.Combine(task, "ExperimentalDesign.tsv"), design);

        var rnd = new Random(7);
        var peptides = new List<string> { string.Join('\t', new[] { "Sequence", "Base Sequence", "Protein Groups", "Gene Names", "Organism" }
            .Concat(files.Select(f => $"Intensity_{f}")).Concat(files.Select(f => $"Detection Type_{f}"))) };
        var groups = new List<string> { "Protein Accession\tGene\tOrganism\tUnique Peptides\tShared Peptides\tProtein Decoy/Contaminant/Target\tProtein QValue" };
        for (int p = 1; p <= 8; p++)
        {
            var seqs = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                string seq = $"PEPTIDE{(char)('A' + p)}{(char)('A' + k)}K";
                seqs.Add(seq);
                var values = new string[n];
                var types = new string[n];
                for (int i = 0; i < n; i++)
                {
                    double log2 = 20 + k + p * 0.3 + (p == 1 && conditions[i] == "B" ? 2 : 0) + (rnd.NextDouble() - 0.5) * 0.4;
                    bool transferred = mbr && lostFile != i + 1 && p == 2 && k == 0;
                    values[i] = Math.Pow(2, log2).ToString("R", Inv);
                    types[i] = transferred ? "MBR" : "MSMS";
                }
                peptides.Add(string.Join('\t', new[] { seq, seq, $"P{p}", $"G{p}", "Mus musculus" }.Concat(values).Concat(types)));
            }
            groups.Add($"P{p}\tG{p}\tMus musculus\t{string.Join('|', seqs)}\t\tT\t0");
        }
        File.WriteAllLines(Path.Combine(task, "AllQuantifiedPeptides.tsv"), peptides);
        File.WriteAllLines(Path.Combine(task, "AllQuantifiedProteinGroups.tsv"), groups);
        var peaks = new List<string> { "File Name\tFull Sequence\tPeak Detection Type\tPIP Q-Value\tDecoy Peptide\tRandom RT" };
        for (int i = 0; i < n; i++)
        {
            peaks.Add($"{files[i]}\tPEPTIDECAK\tMSMS\t\tFalse\tFalse");
            if (lostFile == i + 1) { peaks.Add($"{files[i]}\tPEPTIDECAK\tMBR\t0.2\tFalse\tFalse"); peaks.Add($"{files[i]}\tPEPTIDECBK\tMBR\t0.05\tFalse\tFalse"); }
            else if (mbr) peaks.Add($"{files[i]}\tPEPTIDECAK\tMBR\t0.001\tFalse\tFalse");
        }
        File.WriteAllLines(Path.Combine(task, "AllQuantifiedPeaks.tsv"), peaks);
        if (individual is not null)
        {
            var sdrf = new List<string> { "source name\tcharacteristics[individual]\tcomment[label]\tcomment[data file]\tcomment[searched data file]" };
            for (int i = 0; i < n; i++) sdrf.Add($"s{i + 1}\t{individual(i)}\tlabel free sample\tf{i + 1}.raw\t{files[i]}.mzML");
            File.WriteAllLines(Path.Combine(task, "experiment.sdrf.tsv"), sdrf);
        }
        return search;
    }

    /// <summary>
    /// A TMT10 search of one plex in two fraction files (or two plexes, one file each). <paramref name="conditionOf"/> gives
    /// each channel's condition and <paramref name="sampleOf"/> its sample name; <paramref name="typeOf"/> its Sample Type.
    /// Six protein groups of three peptides, one PSM each per file; P1 four times as abundant in B.
    /// </summary>
    private static string Isobaric(Func<string, string> conditionOf, Func<int, string, string>? sampleOf = null, Func<string, string>? typeOf = null,
        bool twoPlexes = false, string? tmtDesign = null)
    {
        string search = Path.Combine(TestSupport.TempDir(), "04_search"), task = Path.Combine(search, "mm", "Task2SearchTask");
        Directory.CreateDirectory(task);
        SearchProvenance(search, "TMT10");
        var files = new[] { "f1-calib", "f2-calib" };
        sampleOf ??= (_, c) => $"s{c}";
        if (tmtDesign is not null) File.WriteAllText(Path.Combine(task, "TmtDesign.txt"), tmtDesign);
        else
        {
            var design = new List<string> { "File\tPlex\tSample Name\tTMT Channel\tCondition\tBiological Replicate\tFraction\tTechnical Replicate\tSample Type" };
            for (int f = 0; f < files.Length; f++)
                for (int c = 0; c < Tmt10.Length; c++)
                    design.Add($"F:\\run\\04_search\\mm\\Task1CalibrationTask\\{files[f]}.mzML\t{(twoPlexes ? $"plex{f + 1}" : "plexA")}\t{sampleOf(f, Tmt10[c])}\t{Tmt10[c]}\t{conditionOf(Tmt10[c])}\t{c + 1}\t{(twoPlexes ? 1 : f + 1)}\t1\t{typeOf?.Invoke(Tmt10[c]) ?? "study sample"}");
            File.WriteAllLines(Path.Combine(task, "TmtDesign.txt"), design);
        }
        var rnd = new Random(11);
        var raw = new List<string> { "File\tScan\tBaseSequence\tFullSequence\tAccession\tIsDecoy\tScore\t" + string.Join('\t', Enumerable.Range(1, 10).Select(i => $"Reporter_{i}")) };
        var psms = new List<string> { "File Name\tScan Number\tPrecursor Charge\tFull Sequence\tBase Sequence" };
        var groups = new List<string> { "Protein Accession\tGene\tUnique Peptides\tShared Peptides\tProtein Decoy/Contaminant/Target\tProtein QValue" };
        int scan = 0;
        for (int p = 1; p <= 6; p++)
        {
            var seqs = new List<string>();
            for (int k = 0; k < 3; k++)
            {
                string seq = $"TMTPEP{(char)('A' + p)}{(char)('A' + k)}K";
                seqs.Add(seq);
                foreach (string f in files)
                {
                    scan++;
                    var values = Tmt10.Select(c => Math.Pow(2, 12 + k + (p == 1 && conditionOf(c) == "B" ? 2 : 0) + (rnd.NextDouble() - 0.5) * 0.3).ToString("R", Inv)).ToArray();
                    if (p == 3 && k == 1) values[2] = "0";   // a reporter not measured: no value, never a zero
                    raw.Add($"F:\\run\\04_search\\mm\\Task1CalibrationTask\\{f}.mzML\t{scan}\t{seq}\t{seq}\tP{p}\tT\t30\t{string.Join('\t', values)}");
                    psms.Add($"{f}\t{scan}\t2\t{seq}\t{seq}");
                    psms.Add($"{f}\t{scan}\t3\tOTHER{seq}\tOTHER{seq}");   // a second precursor in the same scan
                }
            }
            groups.Add($"P{p}\tG{p}\t{string.Join('|', seqs)}\t\tT\t0");
        }
        File.WriteAllLines(Path.Combine(task, "RawQuantification.tsv"), raw);
        File.WriteAllLines(Path.Combine(task, "AllPSMs.psmtsv"), psms);
        File.WriteAllLines(Path.Combine(task, "AllProteinGroups.tsv"), groups);
        return search;
    }

    private static async Task<(int Exit, JsonElement Data)> Verb(params string[] argv)
    {
        var sw = new StringWriter();
        int exit = await Program.RunAsync(argv, sw, CancellationToken.None);
        var doc = JsonDocument.Parse(sw.ToString());
        return (exit, doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : doc.RootElement.Clone());
    }

    private static string Out() => Path.Combine(TestSupport.TempDir(), "06_differential");

    private static string[] Header(string tsv) => File.ReadLines(tsv).First().Split('\t');

    // ------------------------------------------------------------------ label-free

    // 155 step 7: four files under mzLib's names, and the assumptions note word for word, for each basis; step 3 of the
    // plan: msms_only and mbr_kept both ship when the search ran match-between-runs.
    [Test]
    public async Task ALabelFreeSearchGetsBothBasesUnderMzLibsNames()
    {
        string search = LabelFree(new[] { "A", "A", "A", "B", "B", "B" });
        string outDir = Out();
        var (exit, data) = await Verb("differential", "--search", search, "--out", outDir, "--threads", "2");
        Assert.That(exit, Is.EqualTo(0), data.ToString());
        Assert.That(data.GetProperty("analysed").GetBoolean(), Is.True);
        Assert.That(data.GetProperty("analyses").EnumerateArray().Select(a => a.GetProperty("name").GetString()), Is.EqualTo(new[] { "msms_only", "mbr_kept" }));
        foreach (string basis in new[] { "msms_only", "mbr_kept" })
        {
            string dir = Path.Combine(outDir, basis);
            foreach (string f in new[] { "DifferentialResults.tsv", "DifferentialResults.metadata.json", "DifferentialDecisions.tsv", "StatisticalReport.md", DifferentialStage.AssumptionsFileName })
                Assert.That(File.Exists(Path.Combine(dir, f)), Is.True, $"{basis}/{f}");
            Assert.That(File.ReadAllText(Path.Combine(dir, DifferentialStage.AssumptionsFileName)), Does.StartWith("**Assumptions of this differential analysis** (QuantProject, 2026-11-21;")
                .And.Contain("5. **One search.**").And.Not.Contain("\r"));
        }
        var prov = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "provenance.json")))!;
        Assert.That(prov["schema"]!.GetValue<string>(), Is.EqualTo(DifferentialStage.Schema));
        Assert.That(prov["search"]!["metamorpheus"]!.GetValue<string>(), Is.EqualTo("1.1.15"));
        Assert.That(prov["search"]!["identification_filter"]!["counts"]![0]!["filter"]!.GetValue<string>(), Is.EqualTo("pep q-value"));
        Assert.That(prov["match_between_runs"]!.GetValue<bool>(), Is.True);
        Assert.That(prov["design"]!["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(prov["inputs"]!.AsArray().Select(i => Path.GetFileName(i!["path"]!.GetValue<string>())),
            Is.SupersetOf(new[] { "provenance.json", "ExperimentalDesign.tsv", "AllQuantifiedPeptides.tsv", "AllQuantifiedProteinGroups.tsv" }));
        // dataRepo 145 (PXR-R78): the analysis binds to a bundle by its inputs' sha256 under the bundle's own source roles.
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "msms_only", "DifferentialResults.metadata.json")))!;
        Assert.That(meta["inputs"]!.AsArray().Select(i => (i!["role"]!.GetValue<string>(), i["file"]!.GetValue<string>())), Is.EqualTo(new[]
        {
            ("observation_table", ""), ("experimental_design", "ExperimentalDesign.tsv"), ("peptide_quant", "AllQuantifiedPeptides.tsv"),
            ("protein_group_quant", "AllQuantifiedProteinGroups.tsv"),
        }));
        Assert.That(meta["inputs"]![2]!["sha256"]!.GetValue<string>(),
            Is.EqualTo(Provenance.RunRecord.Entry(Path.Combine(search, "mm", "Task3SearchTask", "AllQuantifiedPeptides.tsv")).Sha256), "the file's bytes, as the bundle hashes them");

        // The rows: one comparison, B against A, and P1 is up by about log2 4.
        var rows = File.ReadAllLines(Path.Combine(outDir, "msms_only", "DifferentialResults.tsv")).Select(l => l.Split('\t')).ToList();
        var h = rows[0].ToList();
        var p1 = rows.Skip(1).Single(r => r[h.IndexOf("feature_id")] == "P1");
        Assert.That(p1[h.IndexOf("quant_basis")], Is.EqualTo("msms_only"));
        double effect = double.Parse(p1[h.IndexOf("log2_effect")], Inv);
        bool bOverA = p1[h.IndexOf("numerator")].EndsWith("=B", StringComparison.Ordinal);
        Assert.That(bOverA ? effect : -effect, Is.EqualTo(2).Within(0.5), "P1 is four times as abundant in B");

        // Run again: the earlier outputs are replaced, not left beside new ones.
        File.WriteAllText(Path.Combine(outDir, "mbr_kept", "stale.txt"), "x");
        var again = DifferentialStage.Run(LabelFree(new[] { "A", "A", "A", "B", "B", "B" }, mbr: false), outDir, maxThreads: 2);
        Assert.That(Directory.Exists(Path.Combine(outDir, "mbr_kept")), Is.False, "no match-between-runs: msms_only only");
        Assert.That(again["match_between_runs"]!.GetValue<bool>(), Is.False);
    }

    // 155 step 4 (PXD033714's design): a sample whose condition is `not available` never becomes a group. It is left out
    // before the table is built, and listed.
    [Test]
    public void NotAvailableSamplesAreLeftOutAndListed()
    {
        string search = LabelFree(new[] { "A", "A", "A", "Not Available", "B", "B", "B", "not applicable" }, mbr: false);
        var r = DifferentialStage.Run(search, Out(), maxThreads: 2);
        Assert.That(r["analysed"]!.GetValue<bool>(), Is.True, r.ToJsonString());
        var left = r["samples_left_out"]!.AsArray();
        Assert.That(left.Select(x => (x!["sample"]!.GetValue<string>(), x["condition"]!.GetValue<string>())),
            Is.EquivalentTo(new[] { ("Not Available_1", "Not Available"), ("not applicable_1", "not applicable") }));
        Assert.That(left.First()!["files"]!.AsArray().Select(f => f!.GetValue<string>()), Is.EqualTo(new[] { "f4-calib.mzML" }));
        Assert.That(r["analyses"]![0]!["samples"]!.GetValue<int>(), Is.EqualTo(6));
        var rows = File.ReadAllLines(Path.Combine(r["analyses"]![0]!["dir"]!.GetValue<string>(), "DifferentialResults.tsv"));
        Assert.That(rows.Skip(1), Has.None.Contain("Not Available").And.None.Contain("not applicable"), "no comparison names them");
    }

    // PXR-T19: a file that lost every transfer while its siblings kept theirs is named, with its MBR peaks and the smallest
    // PIP q-value among them.
    [Test]
    public void AFileThatLostEveryTransferIsNamed()
    {
        string search = LabelFree(new[] { "A", "A", "A", "B", "B", "B" }, lostFile: 3);
        var r = DifferentialStage.Run(search, Out(), maxThreads: 2);
        var lost = r["lost_transfer_files"]!.AsArray();
        Assert.That(lost, Has.Count.EqualTo(1));
        Assert.That(lost[0]!["file"]!.GetValue<string>(), Is.EqualTo("f3-calib"));
        Assert.That(lost[0]!["mbr_peaks"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(lost[0]!["min_pip_q_value"]!.GetValue<double>(), Is.EqualTo(0.05));
        Assert.That(DifferentialStage.Summary(r, Out())["lost_transfer_files"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "f3-calib" }));

        // No file kept a transfer: nothing lost, nothing named.
        Assert.That(DifferentialStage.Run(LabelFree(new[] { "A", "A", "B", "B" }, lostFile: 99, mbr: true), Out(), maxThreads: 2)["lost_transfer_files"]!.AsArray(), Has.Count.EqualTo(4 - 4));
    }

    // 155 step 4: the individual is read from the SDRF the search wrote; on every sample or none.
    [Test]
    public void IndividualsComeFromTheWrittenSdrfOnEverySampleOrNone()
    {
        var used = DifferentialStage.Run(LabelFree(new[] { "A", "A", "A", "B", "B", "B" }, mbr: false, individual: i => $"mouse{i % 3}"), Out(), maxThreads: 2);
        Assert.That(used["analysed"]!.GetValue<bool>(), Is.True, used.ToJsonString());
        Assert.That(used["individuals"]!["source"]!.GetValue<string>(), Is.EqualTo("written_sdrf"));
        Assert.That(used["individuals"]!["individuals"]!.GetValue<int>(), Is.EqualTo(3));

        var partial = DifferentialStage.Run(LabelFree(new[] { "A", "A", "A", "B", "B", "B" }, mbr: false, individual: i => i == 0 ? "not available" : $"mouse{i}"), Out(), maxThreads: 2);
        Assert.That(partial["analysed"]!.GetValue<bool>(), Is.False);
        Assert.That(partial["findings"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("individual_partial"));

        var none = DifferentialStage.Run(LabelFree(new[] { "A", "A", "B", "B" }, mbr: false, individual: _ => "not available"), Out(), maxThreads: 2);
        Assert.That((none["analysed"]!.GetValue<bool>(), none["individuals"]!["source"]), Is.EqualTo((true, (JsonNode?)null)));
    }

    // Step 6 of the plan: a search that cannot be analysed is a finding, the call succeeds and no table is written.
    [Test]
    public async Task ASearchThatCannotBeAnalysedIsAFindingNotAFailure()
    {
        foreach (var (conditions, kind) in new[] { (new[] { "A", "A", "A" }, "one_condition"), (new[] { "A", "B", "C", "C" }, "no_replicates") })
        {
            string outDir = Out();
            var (exit, data) = await Verb("differential", "--search", LabelFree(conditions, mbr: false), "--out", outDir);
            Assert.That(exit, Is.EqualTo(0), data.ToString());
            Assert.That(data.GetProperty("analysed").GetBoolean(), Is.False);
            Assert.That(data.GetProperty("findings")[0].GetProperty("kind").GetString(), Is.EqualTo(kind));
            Assert.That(Directory.EnumerateDirectories(outDir), Is.Empty, "no table");
            Assert.That(File.Exists(Path.Combine(outDir, "provenance.json")), Is.True, "but the reason, on record");
        }

        // A search that wrote no quantification table.
        string search = LabelFree(new[] { "A", "A", "B", "B" });
        File.Delete(Path.Combine(search, "mm", "Task3SearchTask", "AllQuantifiedPeptides.tsv"));
        Assert.That(DifferentialStage.Run(search, Out())["findings"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("no_quantification"));
    }

    // Exit 1 is for inputs that cannot be read, 2 for a wrong call.
    [Test]
    public async Task BrokenInputsFailAndWrongCallsAreUsageErrors()
    {
        string search = LabelFree(new[] { "A", "A", "B", "B" });
        Assert.That((await Verb("differential", "--out", Out())).Exit, Is.EqualTo(2), "--search is required");
        Assert.That((await Verb("differential", "--search", Path.Combine(search, "nowhere"), "--out", Out())).Exit, Is.EqualTo(2));
        Assert.That((await Verb("differential", "--search", search, "--out", Out(), "--threads", "0")).Exit, Is.EqualTo(2));
        Assert.That((await Verb("differential", "--search", search, "--out", Out(), "--sdrf", "x.tsv")).Exit, Is.EqualTo(2), "--sdrf is for an isobaric search");
        Assert.That((await Verb("differential", "x", "--search", search, "--out", Out())).Exit, Is.EqualTo(2));

        string noTask = Path.Combine(TestSupport.TempDir(), "04_search");
        SearchProvenance(noTask);
        var (exit, data) = await Verb("differential", "--search", noTask, "--out", Out());
        Assert.That((exit, data.GetProperty("error").GetProperty("type").GetString()), Is.EqualTo((1, "InvalidDataException")));

        File.Delete(Path.Combine(search, "provenance.json"));
        Assert.That((await Verb("differential", "--search", search, "--out", Out())).Exit, Is.EqualTo(1), "not a finished search");

        string damaged = LabelFree(new[] { "A", "A", "B", "B" });
        File.AppendAllText(Path.Combine(damaged, "mm", "Task3SearchTask", "ExperimentalDesign.tsv"), "f9-calib.mzML\tB\tthree\t1\t1\n");
        Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(damaged, Out()));
        string extra = LabelFree(new[] { "A", "A", "B", "B" });
        File.AppendAllText(Path.Combine(extra, "mm", "Task3SearchTask", "ExperimentalDesign.tsv"), "f9-calib.mzML\tB\t3\t1\t1\n");
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(extra, Out()))!.Message, Does.Contain("does not fit"));
    }

    // ------------------------------------------------------------------ isobaric

    // Step 3: the PSMs MetaMorpheus quantified, with their charges and groups; reference and empty channels hold no sample;
    // a `not available` channel is left out; the PSMs fed are written for QuantProject's comparison with R.
    [Test]
    public void AnIsobaricSearchIsAnalysedOnItsQuantifiedPsms()
    {
        string search = Isobaric(c => c switch
        {
            "126" or "127N" or "127C" or "128N" => "A",
            "128C" or "129N" or "129C" or "130N" => "B",
            "130C" => "not available",
            _ => "pool",
        }, typeOf: c => c == "131N" ? "reference" : "study sample");
        string outDir = Out();
        var r = DifferentialStage.Run(search, outDir, maxThreads: 2);
        Assert.That(r["analysed"]!.GetValue<bool>(), Is.True, r.ToJsonString());
        Assert.That(r["quant_style"]!.GetValue<string>(), Is.EqualTo("tmt"));
        Assert.That(r["design"]!["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }), "the pool is a reference; 130C is left out");
        Assert.That(r["samples_left_out"]!.AsArray().Single()!["channels"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "f1 130C", "f2 130C" }));
        var a = r["analyses"]![0]!;
        Assert.That((a["name"]!.GetValue<string>(), a["samples"]!.GetValue<int>()), Is.EqualTo((DifferentialStage.IsobaricFolder, 8)));

        string dir = Path.Combine(outDir, DifferentialStage.IsobaricFolder);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "DifferentialResults.metadata.json")))!;
        Assert.That(meta["inputs"]!.AsArray().Select(i => i!["role"]!.GetValue<string>()),
            Is.EqualTo(new[] { "observation_table", "isobaric_design", "isobaric_raw_quant", "psms", "protein_groups" }));
        Assert.That(File.ReadLines(Path.Combine(dir, "DifferentialResults.tsv")).Skip(1).First(), Does.Contain("ms3_sps"));
        var psms = File.ReadAllLines(Path.Combine(dir, DifferentialStage.IsobaricPsmsFileName));
        Assert.That(psms[0].Split('\t'), Is.EqualTo(new[] { "File", "Scan", "FullSequence", "BaseSequence", "Charge", "ProteinGroups" }.Concat(Tmt10)));
        Assert.That(psms, Has.Length.EqualTo(1 + 6 * 3 * 2));
        var first = psms[1].Split('\t');
        Assert.That((first[0], first[4], first[5]), Is.EqualTo(("f1-calib", "2", "P1")), "the charge of this peptide's match, not the scan's other precursor");
        Assert.That(psms.Count(l => l.Split('\t')[8] == "0"), Is.EqualTo(2), "a reporter written as 0 is passed as written");
    }

    // PXD015616: one sample named in two plexes. The runner's design step refuses it, and that is a finding.
    [Test]
    public void ASampleNamedInTwoPlexesIsARecordedRefusal()
    {
        string search = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B", sampleOf: (f, c) => c == "126" ? "young1" : $"s{f}{c}", twoPlexes: true);
        var r = DifferentialStage.Run(search, Out(), maxThreads: 2);
        Assert.That(r["analysed"]!.GetValue<bool>(), Is.False);
        Assert.That(r["findings"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("design_refused"));
        Assert.That(r["findings"]![0]!["detail"]!.GetValue<string>(), Does.Contain("young1").And.Contain("display text"));
    }

    // 155 (the sweep's four deposits): a corrected SDRF writing `pooled` feeds the differential step only. Its design must
    // equal the search's in every column but Sample Type, or it is refused.
    [Test]
    public async Task ACorrectedSdrfMayChangeOnlyTheSampleType()
    {
        string sdrfDir = TestSupport.TempDir();
        string Sdrf(string name, Func<string, string> typeOf, Func<string, string>? conditionOf = null)
        {
            conditionOf ??= c => c == "131N" ? "internal pooled standard" : c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B";
            var lines = new List<string> { "source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tcharacteristics[sample type]\tassay name\tcomment[label]\tcomment[data file]\tcomment[technical replicate]\tcomment[fraction identifier]\tfactor value[condition]" };
            foreach (var (file, fraction) in new[] { ("f1.raw", 1), ("f2.raw", 2) })
                for (int c = 0; c < Tmt10.Length; c++)
                    lines.Add($"s{Tmt10[c]}\tMus musculus\t{c + 1}\t{typeOf(Tmt10[c])}\trun {fraction}\tTMT{Tmt10[c]}\t{file}\t1\t{fraction}\t{conditionOf(Tmt10[c])}");
            return TestSupport.WriteFile(sdrfDir, name, string.Join('\n', lines) + "\n");
        }
        string asSearched = Sdrf("searched.sdrf.tsv", c => c == "131N" ? "internal pooled standard" : "single animal");
        // The search's TmtDesign.txt, written from its SDRF as the search writes it, then renamed to the calibrated files.
        string written = Path.Combine(TestSupport.TempDir(), "TmtDesign.txt");
        Search.DesignStage.FindIsobaric(Path.Combine(sdrfDir, "none"), new[] { "f1.raw", "f2.raw" }, asSearched, null, "TMT10", out _)!.WriteTmtDesign(written);
        Search.DesignStage.MarkUnnamedChannelsEmpty(written, "TMT10");
        string design = File.ReadAllText(written).Replace("f1.raw", "f1-calib.mzML").Replace("f2.raw", "f2-calib.mzML");
        string search = Isobaric(c => "x", tmtDesign: design);

        var plain = DifferentialStage.Run(search, Out(), maxThreads: 2);
        Assert.That(plain["design"]!["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Does.Contain("internal pooled standard"),
            "as searched, the pool is a study sample: its channel would be a group");

        string outDir = Out();
        var (exit, data) = await Verb("differential", "--search", search, "--out", outDir, "--sdrf", Sdrf("pooled.sdrf.tsv", c => c == "131N" ? "pooled" : "single animal"));
        Assert.That(exit, Is.EqualTo(0), data.ToString());
        var r = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "provenance.json")))!;
        Assert.That(r["design"]!["conditions"]!.AsArray().Select(c => c!.GetValue<string>()), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(r["corrected_sdrf"]!["references"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "f1 131N", "f2 131N" }));
        Assert.That(File.Exists(Path.Combine(outDir, DifferentialStage.CorrectedDesignFileName)), Is.True);

        var (refused, error) = await Verb("differential", "--search", search, "--out", Out(), "--sdrf",
            Sdrf("changed.sdrf.tsv", c => c == "131N" ? "pooled" : "single animal", c => c == "126" ? "B" : c == "131N" ? "internal pooled standard" : c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B"));
        Assert.That(refused, Is.EqualTo(2));
        Assert.That(error.GetProperty("error").GetProperty("message").GetString(), Does.Contain("changes more than the Sample Type").And.Contain("Condition 'A' in the search, 'B' corrected"));
        Assert.That((await Verb("differential", "--search", search, "--out", Out(), "--sdrf", Path.Combine(sdrfDir, "absent.tsv"))).Exit, Is.EqualTo(2));
    }

    // The design MetaMorpheus 1.1.15 writes when an SDRF marks a reference: mzLib's own Sample Type column. It used to be
    // refused at search setup, so a curated SDRF writing `pooled` could not be searched.
    [Test]
    public void ADesignWithAReferenceChannelGetsItsEmptyRowsToo()
    {
        string dir = TestSupport.TempDir();
        string design = TestSupport.WriteFile(dir, "TmtDesign.txt",
            "File\tPlex\tSample Name\tTMT Channel\tCondition\tBiological Replicate\tFraction\tTechnical Replicate\tSample Type\n"
            + string.Join("", Tmt10.Take(9).Select((c, i) => $"a.raw\t1\ts{c}\t{c}\t{(c == "130C" ? "pool" : "A")}\t{i + 1}\t1\t1\t{(c == "130C" ? "reference" : "study sample")}\n")));
        var marked = Search.DesignStage.MarkUnnamedChannelsEmpty(design, "TMT10");
        Assert.That(marked["a.raw"], Is.EqualTo(new[] { "131N" }));
        var rows = File.ReadAllLines(design).Select(l => l.Split('\t')).ToList();
        Assert.That(rows[0].Count(h => h == "Sample Type"), Is.EqualTo(1), "mzLib's column is kept, not added twice");
        Assert.That(rows.Skip(1).Select(r => (r[3], r[8])), Is.EqualTo(Tmt10.Select(c => (c, c == "130C" ? "reference" : c == "131N" ? "empty" : "study sample"))));
    }

    [Test]
    public void ReadersRefuseWhatTheyCannotRead()
    {
        string dir = TestSupport.TempDir();
        Assert.Throws<InvalidDataException>(() => DifferentialStage.ReadTmtRows(TestSupport.WriteFile(dir, "t.txt", "File\tPlex\n")));
        Assert.Throws<InvalidDataException>(() => DifferentialStage.LabelFreeDesign(TestSupport.WriteFile(dir, "e.tsv", "FileName\tCondition\n")));
        Assert.Throws<InvalidDataException>(() => DifferentialStage.ParseType("t", "spike"));
        Assert.That(DifferentialStage.ParseType("t", "Pooled"), Is.EqualTo(DifferentialStage.ChannelType.Reference));
        Assert.That(DifferentialStage.ParseType("t", "unused"), Is.EqualTo(DifferentialStage.ChannelType.Empty));
        Assert.That(DifferentialStage.Normalize(@"F:\x\run_7-calib-averaged.mzML"), Is.EqualTo("run_7"));
        Assert.That((DifferentialStage.IsReserved(" Not Applicable "), DifferentialStage.IsReserved("aged")), Is.EqualTo((true, false)));

        // A design naming a channel twice, or one the kit has not.
        Omics.Modifications.IsobaricMassTag.TryGetIsobaricMassTag("TMT10", out var tag);
        var row = new DifferentialStage.TmtRow("a.raw", "1", "s", "126", "A", 1, 1, 1, "");
        Assert.Throws<InvalidDataException>(() => DifferentialStage.IsobaricDesign("t", new[] { row, row }, tag!, new HashSet<(string, string)>()));
        Assert.Throws<InvalidDataException>(() => DifferentialStage.IsobaricDesign("t", new[] { row with { Channel = "135N" } }, tag!, new HashSet<(string, string)>()));

        // Reporter columns that do not match the kit, and a match AllPSMs.psmtsv does not have.
        string search = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        string raw = Path.Combine(search, "mm", "Task2SearchTask", "RawQuantification.tsv");
        var lines = File.ReadAllLines(raw);
        File.WriteAllLines(raw, lines.Append(lines[1].Replace("\t1\t", "\t999\t")));
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(search, Out()))!.Message, Does.Contain("charge is unknown"));
        File.WriteAllLines(raw, lines.Select(l => l[..l.LastIndexOf('\t')]));
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(search, Out()))!.Message, Does.Contain("reporter column"));
    }

    // The inputs' other shapes: no task settings, individuals in a TMT SDRF or none, and the refusals of each reader.
    [Test]
    public void TheInputsOtherShapes()
    {
        // No task settings: match-between-runs is read from the peptide table's detection types.
        string noSettings = LabelFree(new[] { "A", "A", "B", "B" });
        Directory.Delete(Path.Combine(noSettings, "mm", "Task Settings"), recursive: true);
        Assert.That(DifferentialStage.Run(noSettings, Out(), maxThreads: 2)["match_between_runs"]!.GetValue<bool>(), Is.True);

        // An SDRF without the column names no individuals; one naming two for a sample is refused.
        string noColumn = LabelFree(new[] { "A", "A", "B", "B" }, mbr: false, individual: i => $"m{i}");
        string sdrf = Path.Combine(noColumn, "mm", "Task3SearchTask", "experiment.sdrf.tsv");
        File.WriteAllText(sdrf, File.ReadAllText(sdrf).Replace("characteristics[individual]", "characteristics[strain]"));
        Assert.That(DifferentialStage.Run(noColumn, Out(), maxThreads: 2)["individuals"]!["reason"]!.GetValue<string>(), Does.Contain("no characteristics[individual]"));
        string two = LabelFree(new[] { "A", "A", "B", "B" }, mbr: false, individual: i => $"m{i}");
        sdrf = Path.Combine(two, "mm", "Task3SearchTask", "experiment.sdrf.tsv");
        File.AppendAllText(sdrf, "s1b\tm9\tlabel free sample\tf1.raw\tf1-calib.mzML\n");
        Assert.That(DifferentialStage.Run(two, Out(), maxThreads: 2)["findings"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("individual_conflict"));
        string noFile = LabelFree(new[] { "A", "A", "B", "B" }, mbr: false, individual: i => $"m{i}");
        sdrf = Path.Combine(noFile, "mm", "Task3SearchTask", "experiment.sdrf.tsv");
        File.WriteAllText(sdrf, File.ReadAllText(sdrf).Replace("comment[data file]", "comment[x]").Replace("comment[searched data file]", "comment[y]"));
        Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(noFile, Out(), maxThreads: 2));

        // A TMT search's individuals, joined by file and channel; TMT10's SDRF label TMT131 is channel 131N.
        string tmt = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        var lines = new List<string> { "source name\tcharacteristics[individual]\tcomment[label]\tcomment[data file]" };
        foreach (string f in new[] { "f1", "f2" })
            foreach (string c in Tmt10) lines.Add($"s{c}\tmouse{c}\t{(c == "131N" ? "TMT131" : $"TMT{c}")}\t{f}.raw");
        File.WriteAllLines(Path.Combine(tmt, "mm", "Task2SearchTask", "experiment.sdrf.tsv"), lines);
        var withIndividuals = DifferentialStage.Run(tmt, Out(), maxThreads: 2);
        Assert.That(withIndividuals["individuals"]!["individuals"]!.GetValue<int>(), Is.EqualTo(10), withIndividuals.ToJsonString());

        // A TMT search with no reporter table is a finding; one whose provenance names no kit cannot be read.
        string noRaw = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        File.Delete(Path.Combine(noRaw, "mm", "Task2SearchTask", "RawQuantification.tsv"));
        Assert.That(DifferentialStage.Run(noRaw, Out())["findings"]![0]!["kind"]!.GetValue<string>(), Is.EqualTo("no_quantification"));
        string noKit = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        SearchProvenance(noKit);
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(noKit, Out()))!.Message, Does.Contain("names no isobaric kit"));
        string badKit = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        SearchProvenance(badKit, "TMT99");
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(badKit, Out()))!.Message, Does.Contain("not one mzLib defines"));

        // A reporter that is not a number, and a reporter table without its scan column.
        string notNumber = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        string raw = Path.Combine(notNumber, "mm", "Task2SearchTask", "RawQuantification.tsv");
        var rawLines = File.ReadAllLines(raw);
        File.WriteAllLines(raw, rawLines.Take(1).Append(rawLines[1][..rawLines[1].LastIndexOf('\t')] + "\tabc"));
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(notNumber, Out()))!.Message, Does.Contain("not a number"));
        File.WriteAllLines(raw, rawLines.Select(l => l.Replace("File\tScan\t", "File\tScanNo\t")));
        Assert.That(Assert.Throws<InvalidDataException>(() => DifferentialStage.Run(notNumber, Out()))!.Message, Does.Contain("no 'Scan' column"));

        // A design naming one file twice; a corrected SDRF that gives no design at all.
        string twice = TestSupport.WriteFile(TestSupport.TempDir(), "ExperimentalDesign.tsv", "FileName\tCondition\tBiorep\tFraction\tTechrep\na.mzML\tA\t1\t1\t1\na.mzML\tB\t1\t1\t1\n");
        Assert.Throws<InvalidDataException>(() => DifferentialStage.LabelFreeDesign(twice));
        string search = Isobaric(c => c.StartsWith("12", StringComparison.Ordinal) ? "A" : "B");
        string bad = TestSupport.WriteFile(TestSupport.TempDir(), "bad.sdrf.tsv", "source name\tcomment[data file]\ns1\tf1.raw\n");
        Assert.That(Assert.Throws<Cli.UsageException>(() => DifferentialStage.Run(search, Out(), bad))!.Message, Does.Contain("gives no channel design"));
    }
}
