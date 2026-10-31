using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>A TMT or iTRAQ search's quantification tables (QuantProject <c>DEF-TMT-*</c>) to assays and QuantValue rows.</summary>
/// <remarks>
/// <para>MetaMorpheus 1.1.13 writes, for a multiplex search with a <c>TmtDesign.txt</c> beside its spectra, three tables
/// in place of FlashLFQ's: <c>PeptideQuantification.tsv</c> (<c>DEF-TMT-PEP</c>), <c>ProteinGroupQuantification.tsv</c>
/// (<c>DEF-TMT-PROT</c>) and <c>RawQuantification.tsv</c> (<c>DEF-TMT-RAW</c>). The first two are melted here, one row per
/// (feature, file and channel); the raw one is copied into the bundle verbatim and gives no rows (QuantProject 068,
/// DATAREPO-D9). Nothing is summed, normalized or combined: a plex's fractions stay separate assays, and combining them
/// is statistics' job (STAT1 M11).</para>
/// <para>QuantProject's reading rules (their 066) are applied when reading, never by rewriting a file:</para>
/// <list type="number">
/// <item>Protein rows hang on the groups <c>AllProteinGroups.tsv</c> writes, with their q-value and label, so the 1% view
/// is <c>protein_groups_1pct</c>, as for label-free. The table itself carries neither.</item>
/// <item>A 0 is no value: no row.</item>
/// <item>A column is placed by its label, <c>{sample name}_{file}_{channel}</c>, on the design row that names that file
/// and channel, never by position; <c>-calib</c> and <c>-averaged</c> are stripped from the file to reach the run.</item>
/// <item>A peptide carrying no multiplex label is stored as written and counted in a finding, until MetaMorpheus quantifies
/// labelled matches only (QuantProject 068, ruling 1).</item>
/// </list>
/// <para>Only isobaric bundles are read this way, and each declares <see cref="ReaderVersion"/> into its id, so a change
/// here moves isobaric bundles alone.</para>
/// </remarks>
public static class Isobaric
{
    public const string DesignFileName = "TmtDesign.txt";
    public const string PeptideFileName = "PeptideQuantification.tsv";
    public const string ProteinFileName = "ProteinGroupQuantification.tsv";
    public const string RawFileName = "RawQuantification.tsv";

    /// <summary>The isobaric reader's version, declared into every isobaric bundle's id and no other bundle's. Bump it in
    /// the same commit as any change to what this reader reads or writes.</summary>
    public const string ReaderVersion = "1";

    /// <summary>The reagents whose channels this reader names, as the schema's <c>Labelling</c> spells them.</summary>
    public static readonly IReadOnlyList<string> Reagents = ["TMT", "iTRAQ"];

    // A design's column names, as MetaMorpheus 1.1.13 writes TmtDesign.txt (mzLib TmtDesignFile).
    private const string FileColumn = "File";
    private const string PlexColumn = "Plex";
    private const string SampleColumn = "Sample Name";
    private const string ChannelColumn = "TMT Channel";
    private const string SampleTypeColumn = "Sample Type";

    // A multiplex label in a MetaMorpheus full sequence: `[Multiplex Label:TMT10 on K]`.
    private const string LabelToken = "[Multiplex Label:";

    private const string NoReader = "no pyMzLib reader for MetaMorpheus's isobaric quantification tables";

    /// <summary>Whether the search wrote isobaric quantification tables.</summary>
    public static bool Wrote(string resultsDir) =>
        File.Exists(Path.Combine(resultsDir, PeptideFileName)) || File.Exists(Path.Combine(resultsDir, ProteinFileName));

    /// <summary>One channel of one searched file, as the search's design names it.</summary>
    /// <param name="Label">The column label the tables write for it.</param>
    /// <param name="File">The spectra file's name as searched, without extension (<c>-calib</c> after calibration).</param>
    /// <param name="Run">The deposited run it was acquired as.</param>
    /// <param name="Tag">The design's channel, e.g. <c>126</c> or <c>131N</c>.</param>
    /// <param name="Channel">The assay channel: the reagent plus the tag, e.g. <c>TMT126</c>.</param>
    public sealed record DesignChannel(string Label, string File, string Run, string Tag, string Channel, string Plex,
        string SampleName, string? SampleType)
    {
        public string AssayId(string datasetId) => $"{datasetId}:{Run}:{Channel}";

        /// <summary>Whether the search read this file calibrated (or averaged): its searched name carries the suffix.</summary>
        public bool Recalibrated => Usi.StripPipelineSuffix(File) != File;
    }

    /// <summary>The search's design: every (file, channel), by column label.</summary>
    public sealed class Design
    {
        public required string Path { get; init; }
        public required List<DesignChannel> Channels { get; init; }
        public Dictionary<string, DesignChannel> ByLabel { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Reads the search's <c>TmtDesign.txt</c>.</summary>
    /// <param name="reagent">The manifest's <c>labelling</c>, one of <see cref="Reagents"/>: it names the channels.</param>
    /// <exception cref="IngestException">A column is missing, a file is no run of the dataset, or two rows name one
    /// (file, channel) or one column label: the tables could then not be placed without a guess.</exception>
    public static Design ReadDesign(string path, string datasetId, string reagent, RunNameMap runNames, ReaderLog? log = null)
    {
        var (header, rows) = Readers.ReadTsv(path, log, NoReader);
        foreach (var column in new[] { FileColumn, PlexColumn, SampleColumn, ChannelColumn })
            if (!header.Contains(column))
                throw new IngestException($"{datasetId}: the search's {DesignFileName} has no '{column}' column, so its channels cannot be placed.");
        var design = new Design { Path = path, Channels = [] };
        var byAssay = new HashSet<(string, string)>();
        foreach (var row in Readers.IterDicts(header, rows))
        {
            var cell = row[FileColumn].Trim();
            if (cell.Length == 0) continue;
            var file = SearchedName(cell);
            var tag = row[ChannelColumn].Trim();
            var sample = row[SampleColumn].Trim();
            var run = runNames.Resolve(file)
                ?? throw new IngestException($"{datasetId}: the search's {DesignFileName} names the file '{cell}', which is no run of the dataset.");
            var label = sample.Length > 0 ? $"{sample}_{file}_{tag}" : $"{file}_{tag}";
            var channel = new DesignChannel(label, file, run, tag, reagent + tag, row[PlexColumn].Trim(), sample,
                row.TryGetValue(SampleTypeColumn, out var type) && type.Trim().Length > 0 ? type.Trim() : null);
            if (!byAssay.Add((run, tag)))
                throw new IngestException($"{datasetId}: the search's {DesignFileName} names channel {tag} of run {run} twice.");
            if (!design.ByLabel.TryAdd(label, channel))
                throw new IngestException($"{datasetId}: two rows of the search's {DesignFileName} give the column label '{label}'.");
            design.Channels.Add(channel);
        }
        if (design.Channels.Count == 0) throw new IngestException($"{datasetId}: the search's {DesignFileName} names no channel.");
        return design;
    }

    /// <summary>A design's file cell as a column label holds it: the name without folder or extension.</summary>
    private static string SearchedName(string cell)
    {
        var name = cell.Split('\\', '/')[^1];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>The design's assays, each on the SDRF sample its (run, channel) names, or else on the design's own
    /// sample (its <c>Sample Name</c>).</summary>
    /// <param name="sdrfAssays">The assays the SDRF gave, after the data-file gate; their channels are the SDRF's labels.</param>
    /// <returns>The assays; the samples they name (SDRF samples kept, design samples added); the SDRF characteristics of
    /// the kept samples; and how many assays found an SDRF sample.</returns>
    public static (List<Row> Assays, List<Row> Samples, List<Row> Characteristics, int FromSdrf) PlaceAssays(
        Design design, string datasetId, List<Row> sdrfAssays, List<Row> samples, List<Row> characteristics, string? organism)
    {
        var sdrfSample = new Dictionary<(string Run, string Channel), string>();
        foreach (var assay in sdrfAssays)
            sdrfSample.TryAdd(((string)assay["run_id"]!, (string)assay["channel"]!), (string)assay["sample_id"]!);
        var plexTags = design.Channels.GroupBy(c => c.Plex, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Tag).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        string? SdrfSampleOf(DesignChannel c)
        {
            var runId = $"{datasetId}:{c.Run}";
            if (sdrfSample.TryGetValue((runId, c.Channel), out var id)) return id;
            // PRIDE's CV names TMT10's one 131 channel `TMT131`; mzLib reads it as 131N (QuantProject 068, D11). Only where
            // the plex has no 131C, so a TMT11 131N is never taken for it.
            if (c.Channel == "TMT131N" && !plexTags[c.Plex].Contains("131C") && sdrfSample.TryGetValue((runId, "TMT131"), out id)) return id;
            return null;
        }

        var sdrfIds = samples.Select(s => (string)s["sample_id"]!).ToHashSet(StringComparer.Ordinal);
        var assays = new List<Row>();
        var designSamples = new Dictionary<string, Row>(StringComparer.Ordinal);
        int fromSdrf = 0;
        foreach (var c in design.Channels)
        {
            var sampleId = SdrfSampleOf(c);
            if (sampleId is not null) fromSdrf++;
            else
            {
                var name = c.SampleName.Length > 0 ? c.SampleName : $"{c.File} {c.Tag}";
                sampleId = $"{datasetId}:{name}";
                if (sdrfIds.Contains(sampleId)) sampleId += "#design";  // a design sample named as an SDRF sample it is not
                if (!designSamples.ContainsKey(sampleId))
                    designSamples[sampleId] = new Row { ["sample_id"] = sampleId, ["dataset_id"] = datasetId, ["source_name"] = name, ["organism"] = organism };
            }
            assays.Add(new Row { ["assay_id"] = c.AssayId(datasetId), ["run_id"] = $"{datasetId}:{c.Run}", ["channel"] = c.Channel, ["sample_id"] = sampleId });
        }
        var used = assays.Select(a => (string)a["sample_id"]!).ToHashSet(StringComparer.Ordinal);
        var kept = samples.Where(s => used.Contains((string)s["sample_id"]!)).Concat(designSamples.Values).ToList();
        var keptCharacteristics = characteristics.Where(c => used.Contains((string)c["sample_id"]!)).ToList();
        return (assays, kept, keptCharacteristics, fromSdrf);
    }

    /// <summary>What reading one isobaric table left out or noted, for its findings.</summary>
    public sealed class TableReport
    {
        public long Rows { get; set; }
        public long Values { get; set; }
        /// <summary>Rows whose key names several features (`|`), by key, with their values: not stored.</summary>
        public Dictionary<string, long> Ambiguous { get; } = new(StringComparer.Ordinal);
        /// <summary>Keys written on more than one row, with their values: not stored under any of them.</summary>
        public Dictionary<string, long> Repeated { get; } = new(StringComparer.Ordinal);
        /// <summary>Keys that name no feature of the bundle (no ProForma, or no written group), with their values.</summary>
        public Dictionary<string, long> Unplaced { get; } = new(StringComparer.Ordinal);
        /// <summary>Peptide rows carrying no multiplex label, stored as written, with their values.</summary>
        public Dictionary<string, long> Unlabelled { get; } = new(StringComparer.Ordinal);
        /// <summary>Design channels the table has no column for.</summary>
        public List<string> MissingColumns { get; } = [];
    }

    /// <summary>A 0 (or blank, or unreadable) cell is no value (<c>DEF-TMT-*</c>: "0 is no value").</summary>
    private static double? Value(string? raw) =>
        PyFormat.TryParseFloat(raw, out var v) && !double.IsNaN(v) && v != 0.0 ? v : null;

    /// <summary>Reads one table, placing every column on its design channel.</summary>
    /// <exception cref="IngestException">The key column is not first, or a column names no design channel.</exception>
    private static (List<(string Key, List<(DesignChannel Channel, double Value)> Values)> Rows, TableReport Report) Melt(
        string path, string keyColumn, Design design, string datasetId, ReaderLog? log)
    {
        var (header, rows) = Readers.ReadTsv(path, log, NoReader);
        var file = System.IO.Path.GetFileName(path);
        if (header.Count == 0 || header[0] != keyColumn)
            throw new IngestException($"{datasetId}: {file} does not start with its '{keyColumn}' column.");
        var columns = new List<(int Index, DesignChannel Channel)>();
        for (int i = 1; i < header.Count; i++)
            columns.Add((i, design.ByLabel.TryGetValue(header[i], out var c) ? c
                : throw new IngestException(
                    $"{datasetId}: {file} has the column '{header[i]}', which no row of the search's {DesignFileName} labels. "
                    + "A column is placed by its file and channel, never by its position, so it is not stored.")));
        var report = new TableReport();
        var present = columns.Select(c => c.Channel.Label).ToHashSet(StringComparer.Ordinal);
        report.MissingColumns.AddRange(design.Channels.Where(c => !present.Contains(c.Label)).Select(c => c.Label));
        var output = new List<(string, List<(DesignChannel, double)>)>();
        foreach (var row in rows)
        {
            var key = row.Count > 0 ? row[0].Trim() : "";
            if (key.Length == 0) continue;
            var values = new List<(DesignChannel, double)>();
            foreach (var (index, channel) in columns)
                if (index < row.Count && Value(row[index]) is { } v) values.Add((channel, v));
            output.Add((key, values));
        }
        report.Rows = output.Count;
        return (output, report);
    }

    /// <summary>Melts <c>PeptideQuantification.tsv</c> into peptidoform QuantValue rows (<c>DEF-TMT-PEP</c>).</summary>
    /// <param name="toProforma">The dataset's ProForma cache, so feature ids match the Peptidoform table.</param>
    public static (List<Row> Rows, TableReport Report) PeptideRows(string path, Design design, string datasetId,
        Func<string, string?> toProforma, ReaderLog? log = null)
    {
        var (melted, report) = Melt(path, "Peptide", design, datasetId, log);
        var seen = melted.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var output = new List<Row>();
        foreach (var (sequence, values) in melted)
        {
            if (sequence.Contains('|')) { Add(report.Ambiguous, sequence, values.Count); continue; }
            // One sequence on several rows is one per protein copy (before mzLib #1463): which copy a value belongs to
            // is not ours to choose.
            if (seen[sequence] > 1) { Add(report.Repeated, sequence, values.Count); continue; }
            var proforma = toProforma(sequence);
            if (string.IsNullOrEmpty(proforma)) { Add(report.Unplaced, sequence, values.Count); continue; }
            if (!sequence.Contains(LabelToken, StringComparison.Ordinal)) Add(report.Unlabelled, sequence, values.Count);
            foreach (var (channel, value) in values)
                output.Add(QuantRow(channel.AssayId(datasetId), "peptidoform", $"{datasetId}:{proforma}", value, Definitions.TmtPeptide.DefinitionId));
        }
        report.Values = output.Count;
        return (output, report);
    }

    /// <summary>Melts <c>ProteinGroupQuantification.tsv</c> into protein-group QuantValue rows (<c>DEF-TMT-PROT</c>).</summary>
    /// <param name="groupIds">The bundle's protein groups, from <c>AllProteinGroups.tsv</c>: a value hangs on one of them.</param>
    public static (List<Row> Rows, TableReport Report) ProteinRows(string path, Design design, string datasetId,
        IReadOnlySet<string> groupIds, ReaderLog? log = null)
    {
        var (melted, report) = Melt(path, "Protein Group", design, datasetId, log);
        var seen = melted.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var output = new List<Row>();
        foreach (var (group, values) in melted)
        {
            if (seen[group] > 1) { Add(report.Repeated, group, values.Count); continue; }
            var accessions = group.Split('|').Select(a => a.Trim()).Where(a => a.Length > 0).Order(StringComparer.Ordinal).ToList();
            var groupId = $"{datasetId}:{string.Join(";", accessions)}";
            if (!groupIds.Contains(groupId)) { Add(report.Unplaced, group, values.Count); continue; }
            foreach (var (channel, value) in values)
                output.Add(QuantRow(channel.AssayId(datasetId), "protein_group", groupId, value, Definitions.TmtProtein.DefinitionId));
        }
        report.Values = output.Count;
        return (output, report);
    }

    private static void Add(Dictionary<string, long> counts, string key, long values) =>
        counts[key] = counts.GetValueOrDefault(key) + values;

    private static Row QuantRow(string assayId, string featureType, string featureId, double value, string definitionId) => new()
    {
        ["assay_id"] = assayId,
        ["feature_type"] = featureType,
        ["feature_id"] = featureId,
        ["value"] = value,
        ["detection_type"] = null,
        ["pip_q_value"] = null,
        ["mbr_kept"] = null,
        ["definition_id"] = definitionId,
    };
}
