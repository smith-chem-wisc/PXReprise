using System.Globalization;
using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>Where each sample column of a protein-group table belongs: a run, or a design's sample (PXR-R9).</summary>
/// <remarks>
/// <para>MetaMorpheus names the protein-group columns (<c>Intensity_</c>, <c>SpectralCount_</c>, <c>CountOccupancy_</c>,
/// <c>IntensityOccupancy_</c>) by FILE only when the search defined no condition and no fraction. Otherwise every
/// column is a SAMPLE, <c>{Condition}_{Biorep}</c>, over all of that sample's fractions and technical replicates
/// (mzLib <c>SampleGroupBuilder.Build</c> and <c>SampleGroupLabels.ForSample</c>, 1.0.593 and 1.0.594). The switch is
/// table-wide. The peptide and peak tables stay per file either way.</para>
/// <para>So the ingest makes the same switch, once per table. When every column that holds a value names a searched
/// run, the table is per run and is read exactly as before (<see cref="BySample"/> false); the design is not even
/// opened, so no bundle that could be written before changes. Otherwise the search's own <c>ExperimentalDesign.tsv</c>,
/// which MetaMorpheus writes beside the table, says which files each sample holds:</para>
/// <list type="bullet">
/// <item>a sample of exactly one searched file is that run's assay: its values are the ones a per-file table would
/// have written;</item>
/// <item>a sample of several files (fractions, technical replicates) belongs to no run, and a stored quantity belongs
/// to one assay, so its values are left out and said so (G92). They are never split back onto runs;</item>
/// <item>a column holding a value that names neither a run nor a design sample, or a sample table with no design,
/// is refused by name.</item>
/// </list>
/// <para>The labels are built from the design's <c>Condition</c> and <c>Biorep</c> as MetaMorpheus builds them, never
/// from an SDRF's <c>source name</c>, which MetaMorpheus 1.1.12 writes with a space (<c>all 1</c>; PXR-F3).</para>
/// </remarks>
public sealed class SampleColumns
{
    /// <summary>The design file's name, as MetaMorpheus writes it beside the protein-group table.</summary>
    public const string DesignFileName = "ExperimentalDesign.tsv";

    // Carried into the reader log, as every in-house read is (G14).
    private const string DesignNote =
        "no mzLib reader for MetaMorpheus's ExperimentalDesign.tsv (EngineLayer.ExperimentalDesign); read by position, as MetaMorpheus reads it";

    /// <summary>One sample of the design: its label, the files it holds and, when it is one searched file, that run.</summary>
    /// <param name="Label">The column label, <c>{Condition}_{Biorep}</c>.</param>
    /// <param name="Files">The design's <c>FileName</c> cells for this sample, in file order.</param>
    /// <param name="Run">The deposited run whose assay the values belong to, or null.</param>
    /// <param name="WhyNoRun">Why <paramref name="Run"/> is null, in words for a finding; null when it is not.</param>
    public sealed record Sample(string Label, IReadOnlyList<string> Files, string? Run, string? WhyNoRun);

    private readonly IReadOnlyDictionary<string, Sample> _samples;

    private SampleColumns(string? designPath, IReadOnlyDictionary<string, Sample> samples)
    {
        DesignPath = designPath;
        _samples = samples;
    }

    /// <summary>True when the table's columns are samples placed through the design; false when they are runs.</summary>
    public bool BySample => DesignPath is not null;

    /// <summary>The design that placed the columns, or null when the table is per run.</summary>
    public string? DesignPath { get; }

    /// <summary><c>{label: values}</c> left out because the sample has no run, by label then definition.</summary>
    public SortedDictionary<string, SortedDictionary<string, long>> Withheld { get; } = new(StringComparer.Ordinal);

    /// <summary>The design's sample for a column label, or null when the design has none.</summary>
    public Sample? SampleOf(string label) => _samples.TryGetValue(label, out var sample) ? sample : null;

    /// <summary>Counts one value of <paramref name="label"/> left out for having no run.</summary>
    public void Withhold(string label, string definitionId)
    {
        if (!Withheld.TryGetValue(label, out var byDefinition))
            Withheld[label] = byDefinition = new SortedDictionary<string, long>(StringComparer.Ordinal);
        byDefinition[definitionId] = byDefinition.GetValueOrDefault(definitionId) + 1;
    }

    /// <summary>Decides how one protein-group table's columns are placed.</summary>
    /// <param name="tablePath">The protein-group table; the design is looked for beside it.</param>
    /// <param name="labels">Every column label that holds at least one value the ingest would store.</param>
    /// <param name="runNames">The searched runs. Not written to: a sample label is not an unmatched run name.</param>
    /// <exception cref="IngestException">A label is neither a run nor a design sample, there is no design to place
    /// sample labels with, or the design is not one MetaMorpheus could have read.</exception>
    public static SampleColumns ForTable(
        string tablePath, string datasetId, IEnumerable<string> labels, RunNameMap runNames, ReaderLog? log = null,
        IngestRules rules = IngestRules.Current)
    {
        var probe = new RunNameMap(runNames.Deposited);
        var holding = labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var notRuns = holding.Where(l => probe.Resolve(l) is null).ToList();
        // Python 0.32.0 had no sample mode; its rows are kept reproducible by leaving such a table to fail as it did.
        if (notRuns.Count == 0 || rules != IngestRules.Current)
            return new SampleColumns(null, new Dictionary<string, Sample>());

        var table = Path.GetFileName(tablePath);
        var designPath = Path.Combine(Path.GetDirectoryName(tablePath) ?? ".", DesignFileName);
        if (!File.Exists(designPath))
            throw new IngestException(
                $"{datasetId}: {table} has column label(s) that name no searched run: {Names(notRuns)}. MetaMorpheus labels "
                + "these columns by sample when the search had an experimental design, and there is no "
                + $"{DesignFileName} beside the table to say which files each sample holds, so the values cannot be placed.");

        var samples = ReadDesign(designPath, datasetId, runNames, log, rules);
        var unknown = holding.Where(l => !samples.ContainsKey(l)).ToList();
        if (unknown.Count > 0)
            throw new IngestException(
                $"{datasetId}: {table} labels its columns by sample, and label(s) {Names(unknown)} are not samples of the "
                + $"search's {DesignFileName} (it has {Names(samples.Keys.Order(StringComparer.Ordinal).ToList())}), so their "
                + "values cannot be placed on any run or sample. A label is the design's Condition and Biorep joined by '_'.");
        return new SampleColumns(designPath, samples);
    }

    /// <summary>Up to ten names, quoted, then how many more.</summary>
    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(10).Select(n => $"'{n}'")) + (names.Count > 10 ? $" and {names.Count - 10} more" : "");

    /// <summary>Reads the design as MetaMorpheus does: by position (FileName, Condition, Biorep, Fraction, Techrep),
    /// header skipped, the label <c>{Condition}_{Biorep}</c> with Biorep as the integer written.</summary>
    private static Dictionary<string, Sample> ReadDesign(
        string path, string datasetId, RunNameMap runNames, ReaderLog? log, IngestRules rules)
    {
        var (_, rows) = Readers.ReadTsv(path, log, DesignNote, rules);
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // MetaMorpheus refuses the same rows (EngineLayer.ExperimentalDesign.ReadExperimentalDesign), so a search
            // that ran on this design cannot have written either.
            if (row.Count < 5)
                throw new IngestException(
                    $"{datasetId}: {DesignFileName} row {i + 2} has {row.Count} cell(s); MetaMorpheus's design has five "
                    + "(FileName, Condition, Biorep, Fraction, Techrep).");
            if (!int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var biorep))
                throw new IngestException($"{datasetId}: {DesignFileName} row {i + 2} has Biorep '{row[2]}', not an integer.");
            // SampleGroupLabels.ForSample: $"{Condition}_{BiologicalReplicate + 1}", where MetaMorpheus read Biorep - 1.
            var label = $"{row[1]}_{biorep.ToString(CultureInfo.InvariantCulture)}";
            if (!files.TryGetValue(label, out var list))
            {
                files[label] = list = [];
                order.Add(label);
            }
            list.Add(row[0]);
        }

        // A private map, so a design file that is not a run is not reported as an unmatched USI run name.
        var probe = new RunNameMap(runNames.Deposited);
        var runOf = order.ToDictionary(
            l => l,
            l => files[l].Count == 1 ? probe.Resolve(Path.GetFileNameWithoutExtension(files[l][0])) : null,
            StringComparer.Ordinal);
        // Two samples on one run would give that assay two values for one feature: neither is stored.
        var shared = runOf.Values.Where(r => r is not null).GroupBy(r => r!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var samples = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var label in order)
        {
            var held = files[label];
            var run = runOf[label];
            string? why = null;
            if (held.Count > 1)
                why = $"{held.Count} files (fractions or technical replicates), summed into one value";
            else if (run is null)
                why = $"its file '{held[0]}' is not a searched run";
            else if (shared.Contains(run))
                (run, why) = (null, $"its run '{run}' is also another sample's");
            samples[label] = new Sample(label, held, run, why);
        }
        return samples;
    }
}
