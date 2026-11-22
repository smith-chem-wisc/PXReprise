using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlashLFQ;
using MassSpectrometry;
using Omics.Modifications;
using PXReprise.Cli;
using PXReprise.Provenance;
using Quantification.Differential;
using Readers;

namespace PXReprise.Differential;

/// <summary>
/// The differential abundance of one finished search (D84; QuantProject 155, PXR-T33): mzLib's
/// <see cref="DifferentialAnalysis.Run"/> once per quantification basis, on the tables the search wrote, with QuantProject's
/// defaults. The methods are QuantProject's (<c>DEF-DIFF v1</c>); this class only reads the search folder, builds the
/// request as 155 rules, and writes mzLib's own files under mzLib's own names.
/// </summary>
/// <remarks>
/// <para><b>Label-free:</b> <c>AllQuantifiedPeptides.tsv</c> through <see cref="FlashLfqObservations.FromPeptideTable"/>
/// (never <c>AllQuantifiedPeaks.tsv</c>, written unfiltered), every row of <c>AllQuantifiedProteinGroups.tsv</c>, the runs
/// of <c>ExperimentalDesign.tsv</c>. One analysis per basis: <c>msms_only</c>, and <c>mbr_kept</c> when the search ran
/// match-between-runs (155: both ship).</para>
/// <para><b>Isobaric:</b> the PSMs MetaMorpheus quantified (<c>RawQuantification.tsv</c>, its own filter), each with its
/// charge from <c>AllPSMs.psmtsv</c> and its protein groups from <c>AllProteinGroups.tsv</c>, through
/// <see cref="IsobaricObservationTable.Build"/>, on the channels of <c>TmtDesign.txt</c>. Reporter values are passed as
/// written: 0 is no value.</para>
/// <para><b>Samples:</b> the design's condition is the only factor; the individual comes from
/// <c>characteristics[individual]</c> of the SDRF the search wrote; the plex stays the isobaric batch; no strata, no
/// covariates. A sample whose condition is <c>not available</c> or <c>not applicable</c> is left out first, and listed
/// (until mzLib's follow-up does it, 155 step 4).</para>
/// <para><b>Findings, not failures:</b> a search that cannot be analysed (one condition, no replicates, a design or request
/// the runner refuses) is recorded in the provenance with no table, and the call succeeds. An input that cannot be read is
/// an exception.</para>
/// <para><b>Beside the bundle, never in it</b> (dataRepo 145, PXR-R78): the stage's folder is <c>06_differential</c>, its
/// <c>provenance.json</c> says <c>stage: differential</c>, and no search names it upstream, so it moves no bundle id. The
/// analysis binds to a bundle by its inputs: each is named in <c>DifferentialResults.metadata.json</c> under the role the
/// bundle records the same file under (<c>peptide_quant</c>, <c>protein_group_quant</c>, <c>experimental_design</c>;
/// <c>isobaric_design</c>, <c>isobaric_raw_quant</c>, <c>psms</c>, <c>protein_groups</c>), with the sha256 of its bytes.
/// <c>written_sdrf</c> and <c>corrected_sdrf</c> have no bundle role; <c>observation_table</c> is mzLib's hash of the
/// table it built, not of a file.</para>
/// </remarks>
public static class DifferentialStage
{
    /// <summary>The stage provenance's schema.</summary>
    public const string Schema = "pxreprise-differential/1";

    /// <summary>The note delivered beside <c>StatisticalReport.md</c> until W1 (155).</summary>
    public const string AssumptionsFileName = "DifferentialAssumptions.md";

    /// <summary>The isobaric analysis's folder (a label-free one is named by its basis).</summary>
    public const string IsobaricFolder = "isobaric";

    /// <summary>The PSMs the isobaric table was built from, for QuantProject's comparison with R (155, PXD007683).</summary>
    public const string IsobaricPsmsFileName = "IsobaricPsms.tsv";

    /// <summary>The TMT design written from a corrected SDRF (<c>--sdrf</c>).</summary>
    public const string CorrectedDesignFileName = "TmtDesign.corrected.txt";

    /// <summary>155's assumptions note for 1.0.0, word for word.</summary>
    public const string AssumptionsText = """
        **Assumptions of this differential analysis** (QuantProject, 2026-11-21; replaced by the report's own Assumptions and
        References sections in a later mzLib release)

        1. **Missing values.** A peptide with no value in a sample is left out of that sample's fit and treated as missing at
           random. A value below the detection limit is not modelled. Where a protein is absent because it is scarce, the
           estimated change can be smaller than the true one.
        2. **Grouping.** Conditions are the search's experimental design, exactly. Samples whose condition is `not available`
           were left out and are listed in the provenance.
        3. **Match-between-runs.** Rows of `quant_basis` with transfers include transferred values. A file named in the
           provenance as having lost every transfer has no transferred values, though its sibling files do. In those rows its
           samples look absent, not low.
        4. **Independence.** Samples of different individuals are independent. Repeated samples of one individual share a random
           intercept, where the design names the individual.
        5. **One search.** Every number here comes from one search of one deposit. Results from searches under different
           identification filters are compared only with that difference stated.

        """;

    private static readonly string[] OwnOutputs = { "msms_only", "mbr_kept", IsobaricFolder };
    private static readonly string[] OwnFiles = { "provenance.json", CorrectedDesignFileName };

    /// <summary>The SDRF's reserved words for an unknown value (any case).</summary>
    internal static bool IsReserved(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("not available", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("not applicable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the stage and returns its provenance, also written to <c>provenance.json</c> in <paramref name="outDir"/>.</summary>
    /// <param name="searchDir">A finished search's folder (<c>04_search</c>): <c>provenance.json</c> and <c>mm/Task*SearchTask</c>.</param>
    /// <param name="outDir">Where to write (<c>06_differential</c>); this stage's earlier outputs there are replaced.</param>
    /// <param name="correctedSdrf">An isobaric search's corrected SDRF; see <see cref="CorrectedDesign"/>.</param>
    /// <param name="maxThreads">As <see cref="DifferentialAnalysis.Run"/>; the output does not depend on it.</param>
    /// <exception cref="InvalidDataException">A search folder whose tables cannot be read.</exception>
    /// <exception cref="UsageException">A corrected SDRF for a label-free search, or one that changes more than the sample type.</exception>
    public static JsonObject Run(string searchDir, string outDir, string? correctedSdrf = null, int maxThreads = -1)
    {
        string started = Now();
        searchDir = Path.GetFullPath(searchDir);
        string mm = Path.Combine(searchDir, "mm");
        string task = (Directory.Exists(mm) ? Directory.EnumerateDirectories(mm, "Task*SearchTask").Order(StringComparer.Ordinal).LastOrDefault() : null)
                      ?? throw new InvalidDataException($"no Task*SearchTask under {mm}: the search has not run");
        string searchProvFile = Path.Combine(searchDir, "provenance.json");
        if (!File.Exists(searchProvFile)) throw new InvalidDataException($"{searchProvFile} does not exist: not a finished search");
        var searchProv = JsonNode.Parse(File.ReadAllText(searchProvFile)) as JsonObject
                         ?? throw new InvalidDataException($"{searchProvFile} is not a JSON object");
        bool isobaric = File.Exists(Path.Combine(task, "TmtDesign.txt"));
        if (correctedSdrf is not null && !isobaric)
            throw new UsageException("--sdrf corrects an isobaric search's channel design; this search is label-free");
        if (correctedSdrf is not null && !File.Exists(correctedSdrf)) throw new UsageException($"--sdrf {correctedSdrf} does not exist");

        Directory.CreateDirectory(outDir);
        foreach (string d in OwnOutputs.Select(o => Path.Combine(outDir, o)).Where(Directory.Exists)) Directory.Delete(d, recursive: true);
        foreach (string f in OwnFiles.Select(o => Path.Combine(outDir, o)).Where(File.Exists)) File.Delete(f);

        var run = new StageRun(searchDir, task, searchProv, Path.GetFullPath(outDir), maxThreads);
        run.Input(searchProvFile);
        try
        {
            if (isobaric) run.Isobaric(correctedSdrf);
            else run.LabelFree();
        }
        catch (StageRefusal r)
        {
            run.Findings.Add(new JsonObject { ["kind"] = r.Kind, ["detail"] = r.Message });
        }

        var record = new JsonObject
        {
            ["schema"] = Schema,
            ["stage"] = "differential",
            ["started_utc"] = started,
            ["host"] = Environment.MachineName,
            ["tools"] = JsonSerializer.SerializeToNode(RunRecord.Versions()),
            ["definition"] = DifferentialConstants.DefinitionId,
            ["method_version"] = DifferentialConstants.MethodVersion,
            ["search"] = new JsonObject
            {
                ["dir"] = searchDir,
                ["task_folder"] = Path.GetFileName(task),
                ["pipeline"] = searchProv["pipeline"]?["version"]?.DeepClone(),
                ["metamorpheus"] = searchProv["tools"]?["MetaMorpheus"]?["release"]?.DeepClone(),
                ["identification_filter"] = searchProv["identification_filter"]?.DeepClone(),
            },
            ["quant_style"] = isobaric ? "tmt" : "lfq",
            ["match_between_runs"] = run.MatchBetweenRuns,
            ["design"] = run.Design,
            ["corrected_sdrf"] = run.Corrected,
            ["individuals"] = run.IndividualsRecord,
            ["samples_left_out"] = run.LeftOut,
            ["lost_transfer_files"] = run.LostTransfers,
            ["analysed"] = run.Analyses.Count > 0,
            ["analyses"] = run.Analyses,
            ["findings"] = run.Findings,
            ["assumptions"] = run.Analyses.Count > 0 ? AssumptionsFileName : null,
            ["inputs"] = new JsonArray(run.Inputs.Values.Select(e => (JsonNode?)JsonSerializer.SerializeToNode(e, Envelope.Json)).ToArray()),
            ["finished_utc"] = Now(),
        };
        File.WriteAllText(Path.Combine(outDir, "provenance.json"), record.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }));
        return record;
    }

    /// <summary>
    /// The channel design an isobaric search's corrected SDRF gives (155: a pooled standard written <c>pooled</c>), written by
    /// mzLib's <see cref="SdrfIsobaricDesign"/> as the search's own was, and checked against the search's
    /// <c>TmtDesign.txt</c>: every column but <c>Sample Type</c> must be equal, file by file and channel by channel.
    /// MetaMorpheus normalizes nothing, so the sample type changes no search value (155), only which channels the
    /// differential step reads as references.
    /// </summary>
    /// <returns>The rows to analyse: the search's own, with the corrected SDRF's sample types.</returns>
    /// <exception cref="UsageException">The SDRF gives no design, or a design that differs beyond the sample type.</exception>
    internal static List<TmtRow> CorrectedDesign(string correctedSdrf, IReadOnlyList<TmtRow> searchRows, string kit,
        IReadOnlyList<string>? conditionColumns, string writeTo)
    {
        // The SDRF names the raw files; the search's design names the calibrated ones it read.
        var searched = searchRows.Select(r => Normalize(r.File)).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => s + ".raw").ToList();
        string none = Path.Combine(Path.GetDirectoryName(writeTo)!, "no-deposit-sdrfs");
        var d = Search.DesignStage.FindIsobaric(none, searched, correctedSdrf, conditionColumns, kit, out var rec);
        if (d is null)
            throw new UsageException($"--sdrf {correctedSdrf} gives no channel design: "
                + string.Join(" | ", rec["sdrfs_refused"]?.AsArray().SelectMany(x => x!["refusals"]!.AsArray()).Select(x => x!.GetValue<string>()) ?? []));
        d.WriteTmtDesign(writeTo);
        Search.DesignStage.MarkUnnamedChannelsEmpty(writeTo, kit);
        var corrected = ReadTmtRows(writeTo);

        string Key(TmtRow r) => $"{Normalize(r.File)}\t{r.Channel}";
        var byKey = corrected.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var differences = new List<string>();
        foreach (var s in searchRows)
        {
            if (!byKey.TryGetValue(Key(s), out var c)) { differences.Add($"{Path.GetFileName(s.File)} channel {s.Channel}: not in the corrected design"); continue; }
            foreach (var (name, a, b) in new[]
                     {
                         ("Plex", s.Plex, c.Plex), ("Sample Name", s.SampleName, c.SampleName), ("Condition", s.Condition, c.Condition),
                         ("Biological Replicate", s.Biorep.ToString(CultureInfo.InvariantCulture), c.Biorep.ToString(CultureInfo.InvariantCulture)),
                         ("Fraction", s.Fraction.ToString(CultureInfo.InvariantCulture), c.Fraction.ToString(CultureInfo.InvariantCulture)),
                         ("Technical Replicate", s.Techrep.ToString(CultureInfo.InvariantCulture), c.Techrep.ToString(CultureInfo.InvariantCulture)),
                     })
                if (!string.Equals(a, b, StringComparison.Ordinal))
                    differences.Add($"{Path.GetFileName(s.File)} channel {s.Channel}: {name} '{a}' in the search, '{b}' corrected");
        }
        var searchKeys = searchRows.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        differences.AddRange(corrected.Where(c => !searchKeys.Contains(Key(c))).Select(c => $"{Path.GetFileName(c.File)} channel {c.Channel}: not in the search's design"));
        if (differences.Count > 0)
            throw new UsageException($"--sdrf {correctedSdrf} changes more than the Sample Type, so it is not this search's design: "
                + string.Join("; ", differences.Take(5)) + (differences.Count > 5 ? $"; and {differences.Count - 5} more" : ""));
        return searchRows.Select(s => s with { SampleType = byKey[Key(s)].SampleType }).ToList();
    }

    /// <summary>A table's file cell without its folder, whichever separator it was written with (CI runs on Linux).</summary>
    internal static string FileName(string cell) => cell.Replace('\\', '/').Split('/')[^1];

    /// <summary>What the verb prints and the batch records: the stage's provenance without its inputs and per-row detail.</summary>
    public static JsonObject Summary(JsonObject record, string outDir) => new()
    {
        ["out_dir"] = Path.GetFullPath(outDir),
        ["provenance"] = Path.GetFullPath(Path.Combine(outDir, "provenance.json")),
        ["quant_style"] = record["quant_style"]?.DeepClone(),
        ["analysed"] = record["analysed"]?.DeepClone(),
        ["analyses"] = new JsonArray(record["analyses"]!.AsArray().Select(a => (JsonNode?)new JsonObject
        {
            ["name"] = a!["name"]?.DeepClone(), ["rows"] = a["rows"]?.DeepClone(), ["contrasts"] = a["contrasts"]?.DeepClone(),
            ["by_status"] = a["by_status"]?.DeepClone(), ["analysis_id"] = a["analysis_id"]?.DeepClone(),
        }).ToArray()),
        ["findings"] = record["findings"]?.DeepClone(),
        ["samples_left_out"] = record["samples_left_out"]!.AsArray().Count,
        ["lost_transfer_files"] = new JsonArray(record["lost_transfer_files"]!.AsArray().Select(f => (JsonNode?)f!["file"]!.GetValue<string>()).ToArray()),
    };

    /// <summary>A file's name without folder, extension or MetaMorpheus's <c>-calib</c> / <c>-averaged</c> suffixes.</summary>
    internal static string Normalize(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(FileName(file));
        foreach (string suffix in new[] { "-averaged", "-calib" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) stem = stem[..^suffix.Length];
        return stem;
    }

    // ------------------------------------------------------------------ the search's tables

    /// <summary>One row of a <c>TmtDesign.txt</c>.</summary>
    internal sealed record TmtRow(string File, string Plex, string SampleName, string Channel, string Condition, int Biorep,
        int Fraction, int Techrep, string SampleType);

    /// <summary>Reads a <c>TmtDesign.txt</c> by its header (<c>Sample Type</c> optional, as MetaMorpheus reads it).</summary>
    internal static List<TmtRow> ReadTmtRows(string path)
    {
        var header = Search.Tsv.Header(path);
        foreach (string c in new[] { "File", "Plex", "Sample Name", "TMT Channel", "Condition", "Biological Replicate", "Fraction", "Technical Replicate" })
            if (!header.Contains(c)) throw new InvalidDataException($"{path} has no '{c}' column");
        return Search.Tsv.Read(path).Select(r => new TmtRow(r["File"], r["Plex"], r["Sample Name"], r["TMT Channel"], r["Condition"],
            Int(path, r["Biological Replicate"]), Int(path, r["Fraction"]), Int(path, r["Technical Replicate"]),
            r.GetValueOrDefault("Sample Type") ?? "")).ToList();
    }

    /// <summary>A TMT sample type as MetaMorpheus 1.1.15 reads it (<c>TmtExperimentalDesign.TryParseSampleType</c>).</summary>
    internal enum ChannelType { Study, Reference, Empty }

    internal static ChannelType ParseType(string path, string value) => value.Trim().ToLowerInvariant() switch
    {
        "" or "study sample" or "studysample" or "sample" or "carrier" or "carrier channel" => ChannelType.Study,
        "reference" or "reference channel" or "pooled" or "bridge" or "bridge channel" => ChannelType.Reference,
        "empty" or "unused" => ChannelType.Empty,
        _ => throw new InvalidDataException($"{path}: '{value}' is not a Sample Type MetaMorpheus reads"),
    };

    /// <summary>
    /// The isobaric design MetaMorpheus 1.1.15 builds from these rows (<c>TmtExperimentalDesign.ToMzLibDesign</c>): every
    /// channel of the kit in the kit's order, which is the order of a match's reporter intensities; plexes numbered by their
    /// names in order; a reference or bridge channel a reference; an empty one, or one not annotated, unused.
    /// <paramref name="leaveOut"/> channels become unused too.
    /// </summary>
    internal static SampleExperimentalDesign IsobaricDesign(string path, IReadOnlyList<TmtRow> rows, IsobaricMassTag tag,
        IReadOnlySet<(string File, string Channel)> leaveOut)
    {
        var plexIds = rows.Select(r => r.Plex).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select((p, i) => (p, i + 1)).ToDictionary(x => x.p, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        var design = new SampleExperimentalDesign();
        foreach (var file in rows.GroupBy(r => FileName(r.File), StringComparer.OrdinalIgnoreCase))
        {
            var byChannel = new Dictionary<string, TmtRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in file)
                if (!byChannel.TryAdd(r.Channel, r)) throw new InvalidDataException($"{path}: {file.Key} names channel {r.Channel} twice");
            if (byChannel.Keys.FirstOrDefault(c => !tag.ChannelLabels.Contains(c, StringComparer.OrdinalIgnoreCase)) is { } unknown)
                throw new InvalidDataException($"{path}: {file.Key} names channel {unknown}, which the kit {tag.TagType} has not");
            var first = file.First();
            var samples = new ISampleInfo[tag.ChannelLabels.Count];
            for (int i = 0; i < samples.Length; i++)
            {
                string label = tag.ChannelLabels[i];
                byChannel.TryGetValue(label, out var a);
                var type = a is null ? ChannelType.Empty : ParseType(path, a.SampleType);
                bool unused = a is null || type == ChannelType.Empty || leaveOut.Contains((file.Key, label));
                samples[i] = new IsobaricQuantSampleInfo(file.Key, unused ? "" : a!.Condition, a?.Biorep ?? 0, first.Techrep, first.Fraction,
                    plexIds[first.Plex], label, tag.ReporterIonMzs[i], !unused && type == ChannelType.Reference)
                { SampleName = unused || string.IsNullOrWhiteSpace(a!.SampleName) ? null : a.SampleName };
            }
            design.Add(file.Key, samples);
        }
        return design;
    }

    /// <summary>
    /// A label-free <c>ExperimentalDesign.tsv</c> as MetaMorpheus reads it (1-based replicates and fractions, stored 0-based),
    /// with mzLib's optional <c>Sample Type</c> column marking a pooled reference file.
    /// </summary>
    internal static SampleExperimentalDesign LabelFreeDesign(string path)
    {
        var header = Search.Tsv.Header(path);
        foreach (string c in new[] { "FileName", "Condition", "Biorep", "Fraction", "Techrep" })
            if (!header.Contains(c)) throw new InvalidDataException($"{path} has no '{c}' column");
        var design = new SampleExperimentalDesign();
        foreach (var r in Search.Tsv.Read(path))
        {
            string type = (r.GetValueOrDefault(SdrfLabelFreeDesign.ExperimentalDesignSampleTypeColumn) ?? "").Trim();
            var info = new SpectraFileInfo(r["FileName"], r["Condition"], Int(path, r["Biorep"]) - 1, Int(path, r["Techrep"]) - 1, Int(path, r["Fraction"]) - 1)
            {
                IsReference = type.Equals("reference", StringComparison.OrdinalIgnoreCase) || type.Equals("pooled", StringComparison.OrdinalIgnoreCase),
            };
            try { design.Add(r["FileName"], info); }
            catch (ArgumentException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
        }
        return design;
    }

    /// <summary>Whether the search ran match-between-runs: its task's settings, else whether any value was transferred.</summary>
    internal static bool MatchBetweenRunsOn(string task, QuantifiedPeptideFile peptides)
    {
        string settings = Path.Combine(Path.GetDirectoryName(task)!, "Task Settings", Path.GetFileName(task) + "config.toml");
        if (File.Exists(settings) && Regex.Match(File.ReadAllText(settings), @"(?m)^\s*MatchBetweenRuns\s*=\s*(true|false)\s*$") is { Success: true } m)
            return m.Groups[1].Value == "true";
        return peptides.Results.Any(p => p.Samples.Values.Any(s => s.DetectionType == "MBR"));
    }

    /// <summary>
    /// The files that lost every transfer (155, PXR-T19): match-between-runs was on, the file has no value of detection type
    /// MBR in the peptide table, and another file of the search has at least one. Each with its MBR peaks in
    /// <c>AllQuantifiedPeaks.tsv</c> and the smallest PIP q-value among them.
    /// </summary>
    internal static JsonArray LostTransfers(QuantifiedPeptideFile peptides, string? peaks)
    {
        var transfers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in peptides.Results)
            foreach (var (label, s) in row.Samples)
                transfers[label] = transfers.GetValueOrDefault(label) + (s.DetectionType == "MBR" && s.Intensity > 0 ? 1 : 0);
        var lost = new JsonArray();
        if (!transfers.Values.Any(n => n > 0)) return lost;
        var none = transfers.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        if (none.Count == 0) return lost;
        var mbrPeaks = none.ToDictionary(f => f, _ => (Peaks: 0, MinQ: double.NaN), StringComparer.Ordinal);
        if (peaks is not null && File.Exists(peaks))
            foreach (var r in Search.Tsv.Read(peaks))
            {
                if (r.GetValueOrDefault("Peak Detection Type") != "MBR") continue;
                // FlashLFQ writes the label the peptide table's columns carry; a name with an extension is taken without it.
                string file = r.GetValueOrDefault("File Name") ?? "";
                if (!mbrPeaks.ContainsKey(file)) file = Path.GetFileNameWithoutExtension(FileName(file));
                if (!mbrPeaks.TryGetValue(file, out var c)) continue;
                double q = Search.Tsv.Number(r.GetValueOrDefault("PIP Q-Value"));
                mbrPeaks[file] = (c.Peaks + 1, double.IsFinite(q) && !(q >= c.MinQ) ? q : c.MinQ);
            }
        foreach (string f in none)
            lost.Add(new JsonObject
            {
                ["file"] = f, ["mbr_peaks"] = mbrPeaks[f].Peaks,
                ["min_pip_q_value"] = double.IsFinite(mbrPeaks[f].MinQ) ? mbrPeaks[f].MinQ : null,
            });
        return lost;
    }

    private static int Int(string path, string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : throw new InvalidDataException($"{path}: '{s}' is not a whole number");

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A search the stage cannot analyse: recorded as a finding, never a failure.</summary>
    internal sealed class StageRefusal(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    /// <summary>One run of the stage over one search folder.</summary>
    private sealed class StageRun(string searchDir, string task, JsonObject searchProv, string outDir, int maxThreads)
    {
        public JsonArray Analyses { get; } = new();
        public JsonArray Findings { get; } = new();
        public JsonArray LeftOut { get; } = new();
        public JsonArray LostTransfers { get; private set; } = new();
        public JsonObject? Design { get; private set; }
        public JsonObject? Corrected { get; private set; }
        public JsonObject IndividualsRecord { get; private set; } = new() { ["source"] = null };
        public bool? MatchBetweenRuns { get; private set; }
        private bool _individualsUsed;
        public Dictionary<string, FileEntry> Inputs { get; } = new(StringComparer.Ordinal);

        private static readonly DesignSpec Spec = new() { Factors = new[] { new FactorSpec(AnalysisSample.ConditionFactor) } };

        public FileEntry Input(string path)
        {
            path = Path.GetFullPath(path);
            if (!Inputs.TryGetValue(path, out var e)) Inputs[path] = e = RunRecord.Entry(path);
            return e;
        }

        private DifferentialInput Role(string role, string path) => new(role, Path.GetFileName(path), Input(path).Sha256);

        public void LabelFree()
        {
            string designFile = Path.Combine(task, "ExperimentalDesign.tsv"), peptidesFile = Path.Combine(task, "AllQuantifiedPeptides.tsv"),
                groupsFile = Path.Combine(task, "AllQuantifiedProteinGroups.tsv"), peaks = Path.Combine(task, "AllQuantifiedPeaks.tsv");
            var missing = new[] { designFile, peptidesFile, groupsFile }.Where(f => !File.Exists(f)).Select(Path.GetFileName).ToList();
            if (missing.Count > 0) throw new StageRefusal("no_quantification", $"the search wrote no label-free quantification table ({string.Join(", ", missing)})");

            var design = LabelFreeDesign(designFile);
            var entries = design.FileNameSampleInfoDictionary.Values.SelectMany(s => s).ToList();
            var samples = Samples(design, entries, designFile, tag: null);
            var peptides = new QuantifiedPeptideFile(peptidesFile);
            IReadOnlyList<ProteinGroupInfo> groups;
            try
            {
                _ = peptides.Results;
                groups = FlashLfqObservations.ReadProteinGroups(new ProteinGroupFromTsvFile(groupsFile));
            }
            catch (Exception e) when (e is not OperationCanceledException and not StageRefusal)
            {
                throw new InvalidDataException($"{task}: the quantification tables cannot be read: {e.Message}", e);
            }
            MatchBetweenRuns = MatchBetweenRunsOn(task, peptides);
            if (MatchBetweenRuns == true) LostTransfers = DifferentialStage.LostTransfers(peptides, peaks);

            var runs = ObservationRun.FromExperimentalDesign(design);
            var inputs = new List<DifferentialInput>
            {
                Role("experimental_design", designFile), Role("peptide_quant", peptidesFile), Role("protein_group_quant", groupsFile),
            };
            if (_individualsUsed) inputs.Add(Role("written_sdrf", Path.Combine(task, "experiment.sdrf.tsv")));
            foreach (var basis in MatchBetweenRuns == true ? new[] { QuantBasis.MsmsOnly, QuantBasis.MbrKept } : new[] { QuantBasis.MsmsOnly })
            {
                ObservationTable table;
                try
                {
                    table = FlashLfqObservations.FromPeptideTable(peptides, runs, groups, basis).ForSamples(samples.Select(s => s.SampleId));
                }
                catch (ArgumentException e)
                {
                    throw new InvalidDataException($"{peptidesFile} does not fit {Path.GetFileName(designFile)}: {e.Message}", e);
                }
                Analyse(basis.ToMachineName(), new DifferentialRequest { Table = table, Samples = samples, Spec = Spec, Inputs = inputs });
            }
        }

        public void Isobaric(string? correctedSdrf)
        {
            string designFile = Path.Combine(task, "TmtDesign.txt"), raw = Path.Combine(task, "RawQuantification.tsv"),
                psmsFile = Path.Combine(task, "AllPSMs.psmtsv"), groupsFile = Path.Combine(task, "AllProteinGroups.tsv");
            var missing = new[] { raw, psmsFile, groupsFile }.Where(f => !File.Exists(f)).Select(Path.GetFileName).ToList();
            if (missing.Count > 0) throw new StageRefusal("no_quantification", $"the search wrote no isobaric quantification table ({string.Join(", ", missing)})");
            string kit = (searchProv["isobaric"]?["kit"] ?? searchProv["experimental_design"]?["kit"])?.GetValue<string>()
                         ?? throw new InvalidDataException($"{searchDir}: the search's provenance names no isobaric kit");
            if (!IsobaricMassTag.TryGetIsobaricMassTag(kit, out var tag) || tag is null)
                throw new InvalidDataException($"{searchDir}: the kit {kit} is not one mzLib defines");

            var rows = ReadTmtRows(designFile);
            var inputs = new List<DifferentialInput> { Role("isobaric_design", designFile) };
            if (correctedSdrf is not null)
            {
                string written = Path.Combine(outDir, CorrectedDesignFileName);
                var columns = searchProv["experimental_design"]?["condition_columns"]?.AsArray().Select(x => x!.GetValue<string>()).ToList();
                rows = CorrectedDesign(correctedSdrf, rows, kit, columns, written);
                Corrected = new JsonObject
                {
                    ["sdrf"] = Path.GetFullPath(correctedSdrf), ["sdrf_sha256"] = Input(correctedSdrf).Sha256,
                    ["design"] = written, ["design_sha256"] = RunRecord.Entry(written).Sha256,
                    ["references"] = new JsonArray(rows.Where(r => ParseType(written, r.SampleType) == ChannelType.Reference)
                        .Select(r => (JsonNode?)$"{Normalize(r.File)} {r.Channel}").ToArray()),
                };
                inputs.Add(Role("corrected_sdrf", correctedSdrf));
            }

            // `not available` channels: unused, so they hold no sample (155 step 4, until mzLib's follow-up).
            var leaveOut = new HashSet<(string, string)>();
            var full = IsobaricDesign(designFile, rows, tag, leaveOut);
            var all = full.FileNameSampleInfoDictionary.Values.SelectMany(s => s).OfType<IsobaricQuantSampleInfo>().ToList();
            var dropped = all.Where(c => !string.IsNullOrWhiteSpace(c.Condition) && IsReserved(c.Condition) && !c.IsReferenceChannel).ToList();
            foreach (var c in dropped) leaveOut.Add((c.FullFilePathWithExtension, c.ChannelLabel));
            foreach (var g in dropped.GroupBy(AnalysisSample.IdFor, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                LeftOut.Add(new JsonObject
                {
                    ["sample"] = g.Key, ["condition"] = g.First().Condition,
                    ["channels"] = new JsonArray(g.Select(c => (JsonNode?)$"{Normalize(c.FullFilePathWithExtension)} {c.ChannelLabel}").ToArray()),
                });
            var design = leaveOut.Count > 0 ? IsobaricDesign(designFile, rows, tag, leaveOut) : full;
            var entries = design.FileNameSampleInfoDictionary.Values.SelectMany(s => s).ToList();
            var samples = Samples(design, entries, designFile, tag, leftOutAlready: true);

            var (psms, groups) = ReadPsms(raw, psmsFile, groupsFile, tag);
            inputs.AddRange(new[] { Role("isobaric_raw_quant", raw), Role("psms", psmsFile), Role("protein_groups", groupsFile) });
            if (_individualsUsed) inputs.Add(Role("written_sdrf", Path.Combine(task, "experiment.sdrf.tsv")));
            IsobaricObservationTable table;
            try { table = IsobaricObservationTable.Build(design, psms, groups); }
            catch (ArgumentException e) { throw new InvalidDataException($"{raw} does not fit {Path.GetFileName(designFile)}: {e.Message}", e); }
            bool ms3 = searchProv["isobaric"]?["sps_ms3"]?.GetValue<bool>() == true;
            string dir = Analyse(IsobaricFolder, new DifferentialRequest
            {
                IsobaricTable = table, Samples = samples, Spec = Spec, Inputs = inputs, ReporterAcquisition = ms3 ? "ms3_sps" : "ms2",
            });
            WritePsms(Path.Combine(dir, IsobaricPsmsFileName), psms, tag);
        }

        /// <summary>
        /// The design's samples with their individuals, after leaving out the <c>not available</c> ones (label-free; the
        /// isobaric design has left them out already). Refuses what the runner cannot analyse.
        /// </summary>
        private IReadOnlyList<AnalysisSample> Samples(IExperimentalDesign design, IReadOnlyList<ISampleInfo> entries, string designFile,
            IsobaricMassTag? tag, bool leftOutAlready = false)
        {
            IReadOnlyList<AnalysisSample> samples;
            try { samples = AnalysisSample.FromExperimentalDesign(design); }
            catch (ArgumentException e) { throw new StageRefusal("design_refused", $"{Path.GetFileName(designFile)}: {e.Message}"); }
            Design = new JsonObject
            {
                ["file"] = designFile, ["sha256"] = Input(designFile).Sha256,
                ["samples"] = samples.Count,
                ["conditions"] = new JsonArray(samples.Select(s => s.Factors[AnalysisSample.ConditionFactor]).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
            };
            if (!leftOutAlready)
            {
                var drop = samples.Where(s => IsReserved(s.Factors[AnalysisSample.ConditionFactor])).ToList();
                foreach (var s in drop)
                    LeftOut.Add(new JsonObject
                    {
                        ["sample"] = s.SampleId, ["condition"] = s.Factors[AnalysisSample.ConditionFactor],
                        ["files"] = new JsonArray(entries.Where(e => AnalysisSample.IdFor(e) == s.SampleId).Select(e => (JsonNode?)Path.GetFileName(e.FullFilePathWithExtension)).ToArray()),
                    });
                samples = samples.Where(s => !IsReserved(s.Factors[AnalysisSample.ConditionFactor])).ToList();
            }

            var kept = samples.Select(s => s.SampleId).ToHashSet(StringComparer.Ordinal);
            var individuals = Individuals(entries.Where(e => kept.Contains(AnalysisSample.IdFor(e))).ToList(), tag);
            if (individuals is not null)
            {
                samples = samples.Select(s => s with { Individual = individuals[s.SampleId] }).ToList();
                _individualsUsed = true;
            }

            var byCondition = samples.GroupBy(s => s.Factors[AnalysisSample.ConditionFactor], StringComparer.Ordinal).ToList();
            if (byCondition.Count < 2)
                throw new StageRefusal("one_condition", $"{samples.Count} sample(s) in {byCondition.Count} condition(s) after {LeftOut.Count} left out: there is nothing to compare");
            if (byCondition.Count(g => g.Count() >= 2) < 2)
                throw new StageRefusal("no_replicates", $"no two conditions have 2 samples each ({string.Join(", ", byCondition.Select(g => $"{g.Key}: {g.Count()}"))}): no comparison has a variance");
            return samples;
        }

        /// <summary>
        /// Each sample's individual from <c>characteristics[individual]</c> of the SDRF the search wrote, joined by file (and,
        /// isobaric, channel). Null when the SDRF names none. A sample with two, or a deposit naming some samples' and not
        /// others', is refused: the runner takes an individual on every sample or none.
        /// </summary>
        private Dictionary<string, string>? Individuals(IReadOnlyList<ISampleInfo> entries, IsobaricMassTag? tag)
        {
            bool isobaric = tag is not null;
            string sdrf = Path.Combine(task, "experiment.sdrf.tsv");
            if (!File.Exists(sdrf)) { IndividualsRecord = new JsonObject { ["source"] = null, ["reason"] = "the search wrote no SDRF" }; return null; }
            var lines = File.ReadAllLines(sdrf).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToList();
            int Col(string name) => Array.FindIndex(lines[0], h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            int individual = Col("characteristics[individual]"), searched = Col("comment[searched data file]"), data = Col("comment[data file]"), label = Col("comment[label]");
            if (individual < 0) { IndividualsRecord = new JsonObject { ["source"] = null, ["reason"] = "the SDRF has no characteristics[individual]" }; return null; }
            int file = searched >= 0 ? searched : data;
            if (file < 0 || (isobaric && label < 0)) throw new InvalidDataException($"{sdrf} names no data file{(isobaric ? " or label" : "")} to join its individuals by");

            var byKey = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in lines.Skip(1))
            {
                string Cell(int i) => i < row.Length ? row[i].Trim() : "";
                if (IsReserved(Cell(individual))) continue;
                string key = Normalize(Cell(file)) + (isobaric ? "\t" + Channel(tag!, Cell(label)) : "");
                if (!byKey.TryGetValue(key, out var set)) byKey[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(Cell(individual));
            }
            var bySample = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                string id = AnalysisSample.IdFor(e);
                if (!bySample.TryGetValue(id, out var set)) bySample[id] = set = new HashSet<string>(StringComparer.Ordinal);
                string key = Normalize(e.FullFilePathWithExtension) + (e is IsobaricQuantSampleInfo c ? "\t" + c.ChannelLabel : "");
                if (byKey.TryGetValue(key, out var found)) set.UnionWith(found);
            }
            IndividualsRecord = new JsonObject { ["source"] = "written_sdrf", ["sdrf"] = sdrf, ["column"] = "characteristics[individual]" };
            if (bySample.Values.All(s => s.Count == 0)) { IndividualsRecord["source"] = null; IndividualsRecord["reason"] = "every individual is not available"; return null; }
            if (bySample.FirstOrDefault(kv => kv.Value.Count > 1) is { Key: not null } two)
                throw new StageRefusal("individual_conflict", $"sample {two.Key} is individual {string.Join(" and ", two.Value.Order(StringComparer.Ordinal))} in different runs; every run of one sample must give one individual");
            var none = bySample.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            if (none.Count > 0)
                throw new StageRefusal("individual_partial", $"the SDRF names the individual of {bySample.Count - none.Count} of {bySample.Count} samples, not of {string.Join(", ", none.Take(5))}{(none.Count > 5 ? ", ..." : "")}; the runner takes one on every sample or none");
            IndividualsRecord["individuals"] = bySample.Values.Select(s => s.Single()).Distinct(StringComparer.Ordinal).Count();
            return bySample.ToDictionary(kv => kv.Key, kv => kv.Value.Single(), StringComparer.Ordinal);
        }

        /// <summary>The kit's channel an SDRF label names, as mzLib reads it (TMT10's <c>TMT131</c> is <c>131N</c>).</summary>
        private static string Channel(IsobaricMassTag tag, string label) =>
            SdrfIsobaricLabels.TryGetChannel(tag.TagType, label, out string? channel) && channel is not null ? channel : label;

        /// <summary>Runs one analysis into its folder and records it; a request the runner refuses is a finding.</summary>
        private string Analyse(string name, DifferentialRequest request)
        {
            DifferentialAnalysisResult result;
            try { result = DifferentialAnalysis.Run(request, maxThreads); }
            catch (ArgumentException e) { throw new StageRefusal("runner_refused", $"{name}: {e.Message}"); }
            string dir = Path.Combine(outDir, name);
            result.Write(dir);
            File.WriteAllText(Path.Combine(dir, AssumptionsFileName), AssumptionsText.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(false));
            Analyses.Add(new JsonObject
            {
                ["name"] = name,
                ["quant_basis"] = request.Table?.Basis.ToMachineName(),
                ["dir"] = dir,
                ["analysis_id"] = result.Metadata.AnalysisId,
                ["samples"] = request.Samples.Count,
                ["rows"] = result.Rows.Count,
                ["by_status"] = new JsonObject(result.Rows.GroupBy(r => r.Status, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                ["contrasts"] = result.Rows.Select(r => r.ContrastId).Distinct(StringComparer.Ordinal).Count(),
                ["decisions"] = result.Decisions.Count,
                ["design_warnings"] = new JsonArray(result.Metadata.DesignWarnings.Select(w => (JsonNode?)w).ToArray()),
            });
            return dir;
        }

        /// <summary>
        /// The PSMs MetaMorpheus quantified (<c>RawQuantification.tsv</c>), each with its charge from <c>AllPSMs.psmtsv</c> and
        /// the protein groups whose unique or shared peptides list it in <c>AllProteinGroups.tsv</c>, read unfiltered.
        /// </summary>
        private static (List<IsobaricPsm> Psms, IReadOnlyList<ProteinGroupInfo> Groups) ReadPsms(string raw, string psmsFile, string groupsFile,
            IsobaricMassTag tag)
        {
            var header = Search.Tsv.Header(raw);
            foreach (string c in new[] { "File", "Scan", "BaseSequence", "FullSequence" })
                if (!header.Contains(c)) throw new InvalidDataException($"{raw} has no '{c}' column");
            var reporters = header.Where(h => h.StartsWith("Reporter_", StringComparison.Ordinal)).ToList();
            if (reporters.Count != tag.ChannelLabels.Count)
                throw new InvalidDataException($"{raw} has {reporters.Count} reporter column(s), but the kit {tag.TagType} has {tag.ChannelLabels.Count} channels");
            var quantified = new List<(string File, int Scan, string Full, string Base, double[] Values)>();
            foreach (var r in Search.Tsv.Read(raw))
            {
                var values = reporters.Select(c => Search.Tsv.Number(r[c])).ToArray();
                if (values.Any(v => !double.IsFinite(v))) throw new InvalidDataException($"{raw}: scan {r["Scan"]} of {r["File"]} has a reporter that is not a number");
                quantified.Add((Path.GetFileNameWithoutExtension(FileName(r["File"])), Int(raw, r["Scan"]), r["FullSequence"], r["BaseSequence"], values));
            }

            var wanted = quantified.Select(q => (q.File, q.Scan)).ToHashSet();
            var charges = new Dictionary<(string, int), List<(string Full, int Charge)>>();
            foreach (var r in Search.Tsv.Read(psmsFile))
            {
                // MetaMorpheus writes the file's name without extension here.
                string file = FileName(r.GetValueOrDefault("File Name") ?? "");
                if (!int.TryParse(r.GetValueOrDefault("Scan Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scan) || !wanted.Contains((file, scan))) continue;
                double charge = Search.Tsv.Number(r.GetValueOrDefault("Precursor Charge"));
                if (!double.IsFinite(charge)) continue;
                if (!charges.TryGetValue((file, scan), out var list)) charges[(file, scan)] = list = new();
                list.Add((r.GetValueOrDefault("Full Sequence") ?? "", (int)charge));
            }

            var table = new ProteinGroupFromTsvFile(groupsFile);
            IReadOnlyList<ProteinGroupInfo> groups;
            var bySequence = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            try
            {
                groups = FlashLfqObservations.ReadProteinGroups(table);
                foreach (var g in table.Results)
                    foreach (string seq in $"{g.UniqueSequences}|{g.SharedSequences}".Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!bySequence.TryGetValue(seq, out var set)) bySequence[seq] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(g.ProteinGroupName);
                    }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidDataException($"{groupsFile} cannot be read: {e.Message}", e);
            }

            var psms = new List<IsobaricPsm>(quantified.Count);
            foreach (var q in quantified)
            {
                if (!charges.TryGetValue((q.File, q.Scan), out var candidates))
                    throw new InvalidDataException($"{raw}: scan {q.Scan} of {q.File} is not in {Path.GetFileName(psmsFile)}, so its charge is unknown");
                // A scan can hold several precursors' matches: the one of this peptide, else the scan's first.
                var same = candidates.Where(c => c.Full == q.Full).ToList();
                int charge = (same.Count > 0 ? same[0] : candidates[0]).Charge;
                var names = bySequence.GetValueOrDefault(q.Full) ?? bySequence.GetValueOrDefault(q.Base) ?? new SortedSet<string>();
                psms.Add(new IsobaricPsm(q.File, q.Scan, q.Full, q.Base, charge, names.ToList(), q.Values));
            }
            return (psms, groups);
        }

        /// <summary>The PSMs the table was built from, one line each, reporters in the kit's channel order.</summary>
        private static void WritePsms(string path, IReadOnlyList<IsobaricPsm> psms, IsobaricMassTag tag)
        {
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            w.WriteLine(string.Join('\t', new[] { "File", "Scan", "FullSequence", "BaseSequence", "Charge", "ProteinGroups" }.Concat(tag.ChannelLabels)));
            foreach (var p in psms)
                w.WriteLine(string.Join('\t', new[] { p.FileName, p.ScanNumber.ToString(CultureInfo.InvariantCulture), p.FullSequence, p.BaseSequence,
                    p.Charge.ToString(CultureInfo.InvariantCulture), string.Join(';', p.ProteinGroups) }
                    .Concat(p.ReporterIntensities.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));
        }
    }
}
