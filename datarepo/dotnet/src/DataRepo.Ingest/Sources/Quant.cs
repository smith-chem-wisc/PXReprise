using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>FlashLFQ's wide tables to ProteinGroup and QuantValue rows.</summary>
/// <remarks>
/// <para>FlashLFQ writes one column per run (<c>Intensity_&lt;run&gt;</c>, <c>SpectralCount_&lt;run&gt;</c>);
/// the schema stores quantities long, one row per (assay, feature, definition), because that shape survives
/// TMT channels and DIA without a schema change (D5). Melting is the whole job here. With an experimental design
/// the protein-group columns are per SAMPLE instead, and <see cref="SampleColumns"/> places them (PXR-R9).</para>
/// <para>Two producer-side rules are enforced while melting. <b>Missing is missing</b>: a <c>NotDetected</c>
/// cell or a zero intensity produces no row, never a zero, because a zero would read downstream as a measured
/// absence. And <b>every number carries a definition</b>, which lets intensity and spectral count share one
/// <c>value</c> column without ambiguity.</para>
/// <para>The tables are read as plain TSV, exactly as the Python 0.32.0 read them, so the port can be checked
/// row for row. mzLib 1.0.593 reads all three (re-tested on PXD036557 in pyMzLib 0.4.0), so moving to its typed
/// readers is the next step (G14), made separately and diffed on the corpus. Ported from
/// <c>sources/quant.py</c>.</para>
/// </remarks>
public static class Quant
{
    /// <summary>FlashLFQ's detection labels mapped onto the schema's DetectionType.</summary>
    public static readonly IReadOnlyDictionary<string, string?> DetectionType = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["MSMS"] = "MSMS",
        ["MBR"] = "MBR",
        ["MSMSAmbiguousPeakfinding"] = "MSMS",
        ["MSMSIdentifiedButNotQuantified"] = "not_detected",
        ["NotDetected"] = "not_detected",
        [""] = null,
    };

    // Carried into the reader log, as the Python did; requested of pyMzLib in aging 006 / pyMzLib 005 (G14).
    private const string NoReader = "no pyMzLib reader for FlashLFQ's wide tables";
    private const string PeaksNote = "pyMzLib's FlashLFQ peak reader requires an 'MBR Score' column MetaMorpheus 1.1.11 does not write";

    /// <summary><c>{run name reported by the search: column}</c> for every <c>&lt;prefix&gt;_&lt;run&gt;</c> column.</summary>
    private static List<(string Reported, string Column)> WideColumns(List<string> header, string prefix)
    {
        var tag = prefix + "_";
        // A Python dict comprehension: a repeated key keeps its first position and its last value.
        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in header)
        {
            if (!c.StartsWith(tag, StringComparison.Ordinal)) continue;
            var key = c[tag.Length..];
            if (!map.ContainsKey(key)) order.Add(key);
            map[key] = c;
        }
        return order.Select(k => (k, map[k])).ToList();
    }

    /// <summary>A quantity where zero means not measured: zero becomes no row.</summary>
    private static double? Intensity(string? raw)
    {
        var value = Number(raw);
        return value == 0.0 ? null : value;
    }

    /// <summary>A plain number where zero is a real value (a q-value of 0, a coverage of 0).</summary>
    private static double? Number(string? raw) =>
        PyFormat.TryParseFloat(raw, out var v) && !double.IsNaN(v) ? v : null;

    /// <summary>FlashLFQ's <c>Protein Decoy/Contaminant/Target</c> column to the schema's TargetDecoy.</summary>
    private static string? TargetDecoy(string? raw)
    {
        var text = (raw ?? "").ToUpperInvariant();
        if (text.Length == 0) return null;
        if (text.Contains('D')) return "decoy";
        if (text.Contains('C')) return "contaminant";
        return "target";
    }

    private static long? Int(string? raw)
    {
        if (!PyFormat.TryParseFloat((raw ?? "None").Split('|')[0], out var v) || double.IsNaN(v)) return null;
        if (double.IsInfinity(v)) throw new OverflowException("cannot convert float infinity to integer");
        return (long)Math.Truncate(v);
    }

    private static List<object?> Split(string? raw) =>
        (raw ?? "").Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).Cast<object?>().ToList();

    private static string Cell(Dictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var v) ? v : "";

    /// <summary>Best PIP q-value per (run, full sequence) from <c>AllQuantifiedPeaks.tsv</c>.</summary>
    /// <remarks>The peak table is per charge state and per peak, finer than the peptide table the quantities
    /// come from, so it is used only to attach peak-level quality the peptide table lacks: the PIP q-value
    /// and whether the MBR transfer passes the producer's threshold (QuantProject DEF-QC-MBR).</remarks>
    public static Dictionary<(string Run, string Sequence), (double? PipQValue, bool? MbrKept)> PeakQuality(
        string path, string datasetId, RunNameMap runNames, double mbrQThreshold, ReaderLog? log = null, IngestRules rules = IngestRules.Current)
    {
        var output = new Dictionary<(string, string), (double?, bool?)>();
        if (!File.Exists(path)) return output;
        var (header, rows) = Readers.ReadTsv(path, log, PeaksNote, rules);
        foreach (var row in Readers.IterDicts(header, rows))
        {
            var reported = Cell(row, "File Name");
            var run = runNames.Resolve(reported) ?? reported;
            var sequence = Cell(row, "Full Sequence");
            if (sequence.Length == 0) continue;
            double? pipQ = PyFormat.TryParseFloat(Cell(row, "PIP Q-Value"), out var q) ? q : null;
            var key = (run, sequence);
            // Python compares with `<`, which is false for NaN, and stores whatever float() returned.
            if (!output.TryGetValue(key, out var current)
                || (pipQ is not null && (current.Item1 is null || pipQ < current.Item1)))
                output[key] = (pipQ, pipQ is null ? null : pipQ <= mbrQThreshold);
        }
        return output;
    }

    /// <summary>Melts <c>AllQuantifiedPeptides.tsv</c> into QuantValue rows for peptidoforms.</summary>
    /// <param name="toProforma">The dataset's ProForma cache, so feature ids match the Peptidoform table;
    /// returns the peptidoform's ProForma, or null/empty when it has none.</param>
    public static List<Row> PeptideQuantRows(
        string path, string datasetId, RunNameMap runNames, Func<string, string?> toProforma,
        Dictionary<(string Run, string Sequence), (double? PipQValue, bool? MbrKept)>? peakQualityIndex = null,
        ReaderLog? log = null, IngestRules rules = IngestRules.Current)
    {
        var (header, rows) = Readers.ReadTsv(path, log, NoReader, rules);
        var intensityCols = WideColumns(header, "Intensity");
        var detectionCols = WideColumns(header, "Detection Type").ToDictionary(p => p.Reported, p => p.Column, StringComparer.Ordinal);
        var index = peakQualityIndex ?? [];

        var output = new List<Row>();
        foreach (var row in Readers.IterDicts(header, rows))
        {
            var sequence = Cell(row, "Sequence");
            if (sequence.Length == 0) sequence = Cell(row, "Base Sequence");
            var first = sequence.Split('|', 2)[0].Trim();
            var proforma = toProforma(first);
            if (string.IsNullOrEmpty(proforma)) continue;
            var featureId = $"{datasetId}:{proforma}";
            foreach (var (reported, column) in intensityCols)
            {
                var value = Intensity(Cell(row, column));
                if (value is null) continue;
                var run = runNames.Resolve(reported) ?? reported;
                var detectionText = detectionCols.TryGetValue(reported, out var dc) ? Cell(row, dc) : Cell(row, "");
                var detection = DetectionType.TryGetValue(detectionText.Trim(), out var d) ? d : null;
                index.TryGetValue((run, first), out var quality);
                output.Add(new Row
                {
                    ["assay_id"] = $"{datasetId}:{run}:label_free",
                    ["feature_type"] = "peptidoform",
                    ["feature_id"] = featureId,
                    ["value"] = value,
                    ["detection_type"] = detection,
                    ["pip_q_value"] = quality.PipQValue,
                    ["mbr_kept"] = quality.MbrKept,
                    ["definition_id"] = Definitions.PeptideIntensity.DefinitionId,
                });
            }
        }
        return output;
    }

    /// <summary>The producer's protein-group count at 1% FDR over ProteinGroup rows: not decoy, contaminants
    /// included, as the producer counts it. Kept in one place so it can be retaken after an exact-duplicate
    /// collapse changes the rows.</summary>
    public static long AcceptedGroupCount(IEnumerable<IReadOnlyDictionary<string, object?>> groups) =>
        groups.LongCount(g =>
            !Equals(g.GetValueOrDefault("target_decoy"), "decoy")
            && g.GetValueOrDefault("q_value") is double q && q <= 0.01);

    /// <summary>Reads <c>AllQuantifiedProteinGroups.tsv</c> into ProteinGroup rows and their QuantValues.</summary>
    /// <remarks>With an experimental design MetaMorpheus labels these columns by sample, not by run; where each
    /// one goes is <see cref="SampleColumns"/>'s decision (PXR-R9).</remarks>
    /// <returns>The groups, their quantities (intensity and spectral count as separate rows told apart by
    /// definition), the producer-style group count at 1% FDR, for reconciling, and how the columns were placed.</returns>
    /// <exception cref="IngestException">A column holding a value can be placed on no run (see
    /// <see cref="SampleColumns.ForTable"/>).</exception>
    public static (List<Row> Groups, List<Row> Quants, long ProducerCount, SampleColumns Columns) ProteinGroupRows(
        string path, string datasetId, RunNameMap runNames, ReaderLog? log = null, IngestRules rules = IngestRules.Current)
    {
        var (header, rows) = Readers.ReadTsv(path, log, NoReader, rules);
        var intensityCols = WideColumns(header, "Intensity");
        var countCols = WideColumns(header, "SpectralCount");

        // Only a value that becomes a row decides anything: a column with none, or with values only on rows the melt
        // skips, writes no row, today or before.
        var holding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Readers.IterDicts(header, rows))
        {
            if (Split(Cell(row, "Protein Accession")).Count == 0) continue;
            foreach (var (reported, column) in intensityCols)
                if (Intensity(Cell(row, column)) is not null) holding.Add(reported);
            foreach (var (reported, column) in countCols)
                if (Number(Cell(row, column)) is not null) holding.Add(reported);
        }
        var placement = SampleColumns.ForTable(path, datasetId, holding, runNames, log, rules);

        var groups = new List<Row>();
        var quants = new List<Row>();
        foreach (var row in Readers.IterDicts(header, rows))
        {
            var accessions = Split(Cell(row, "Protein Accession")).Cast<string>().ToList();
            if (accessions.Count == 0) continue;
            var sorted = accessions.Order(StringComparer.Ordinal).ToList();
            var groupId = $"{datasetId}:{string.Join(";", sorted)}";
            var nPeptides = Int(Cell(row, "Number of Peptides"));
            var nUnique = Int(Cell(row, "Number of Unique Peptides"));
            var coverage = Cell(row, "Sequence Coverage Fraction");
            groups.Add(new Row
            {
                ["protein_group_id"] = groupId,
                ["dataset_id"] = datasetId,
                ["protein_accessions"] = sorted.Cast<object?>().ToList(),
                ["genes"] = Split(Cell(row, "Gene")),
                ["target_decoy"] = TargetDecoy(Cell(row, "Protein Decoy/Contaminant/Target")),
                ["q_value"] = Number(Cell(row, "Protein QValue")),
                ["sequence_coverage"] = Number(coverage.Split('|')[0]),
                ["unique_peptides"] = nUnique,
                ["shared_peptides"] = nPeptides is not null && nUnique is not null ? nPeptides - nUnique : null,
            });
            // The two columns of a block encode "nothing" differently: an intensity of 0 (or blank) means NOT
            // measured and is no row, but a spectral count of 0 IS a measurement, no qualifying PSM in that
            // sample group (QuantProject:DEF-PROT-SPC, "0 is a real zero here"). Dropping it was a defect
            // until 0.18.0.
            foreach (var (columns, definition, parse) in new (List<(string, string)>, string, Func<string?, double?>)[]
            {
                (intensityCols, Definitions.ProteinIntensity.DefinitionId, Intensity),
                (countCols, Definitions.ProteinSpectralCount.DefinitionId, Number),
            })
            {
                foreach (var (reported, column) in columns)
                {
                    var value = parse(Cell(row, column));
                    if (value is null) continue;
                    string run;
                    if (!placement.BySample)
                    {
                        run = runNames.Resolve(reported) ?? reported;
                    }
                    else if (placement.SampleOf(reported)?.Run is { } sampleRun)
                    {
                        run = sampleRun;
                    }
                    else
                    {
                        placement.Withhold(reported, definition);
                        continue;
                    }
                    quants.Add(new Row
                    {
                        ["assay_id"] = $"{datasetId}:{run}:label_free",
                        ["feature_type"] = "protein_group",
                        ["feature_id"] = groupId,
                        ["value"] = value,
                        ["detection_type"] = null,
                        ["pip_q_value"] = null,
                        ["mbr_kept"] = null,
                        ["definition_id"] = definition,
                    });
                }
            }
        }
        return (groups, quants, AcceptedGroupCount(groups), placement);
    }
}
