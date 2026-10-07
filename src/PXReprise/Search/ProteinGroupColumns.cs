namespace PXReprise.Search;

/// <summary>
/// What a FlashLFQ protein-group column (<c>Intensity_x</c>, <c>SpectralCount_x</c>) measures. Without a design, x is the
/// file. With a design (every search since D31), x is the SAMPLE, <c>{Condition}_{Biorep}</c> (mzLib
/// <c>ProteinGroup.cs</c> LabelSamplesByFileName, <c>SampleGroupLabels.ForSample</c>), with that sample's fractions and
/// technical replicates combined. A run-grain metric may use a sample column only when the sample is exactly one file;
/// a sample spanning several files has no run-grain value and is left out, never guessed (PXReprise G27). The
/// peptide and peak tables stay per file and need none of this.
/// </summary>
public sealed class ProteinGroupColumns
{
    private readonly Dictionary<string, List<string>> _filesOfSample;

    private ProteinGroupColumns(Dictionary<string, List<string>> filesOfSample) => _filesOfSample = filesOfSample;

    /// <summary>The sample labels that span several files, so have no run-grain value.</summary>
    public IReadOnlyList<string> MultiFileSamples =>
        _filesOfSample.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The columns of the protein-group table at <paramref name="proteinGroupsPath"/>, resolved through the
    /// <c>ExperimentalDesign.tsv</c> MetaMorpheus writes beside it. No design file: every label is a file name.
    /// </summary>
    public static ProteinGroupColumns ForTable(string proteinGroupsPath)
    {
        string design = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(proteinGroupsPath))!, DesignStage.FileName);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!File.Exists(design)) return new ProteinGroupColumns(map);
        var rows = File.ReadAllLines(design).Where(l => l.Trim().Length > 0).ToList();
        if (rows.Count == 0) return new ProteinGroupColumns(map);
        string[] header = rows[0].Split('\t');
        int file = Array.IndexOf(header, "FileName"), condition = Array.IndexOf(header, "Condition"), biorep = Array.IndexOf(header, "Biorep");
        if (file < 0 || condition < 0 || biorep < 0) return new ProteinGroupColumns(map);
        foreach (string row in rows.Skip(1))
        {
            string[] v = row.Split('\t');
            if (v.Length <= Math.Max(file, Math.Max(condition, biorep))) continue;
            string label = $"{v[condition].Trim()}_{v[biorep].Trim()}";
            if (!map.TryGetValue(label, out var files)) map[label] = files = new List<string>();
            files.Add(Path.GetFileNameWithoutExtension(v[file].Trim()));
        }
        return new ProteinGroupColumns(map);
    }

    /// <summary>
    /// The file (name without extension) a column label measures. A label that is not a design sample is a file name
    /// and is returned as it is. Null when the label is a sample spanning several files.
    /// </summary>
    public string? FileOf(string label) =>
        _filesOfSample.TryGetValue(label, out var files) ? (files.Count == 1 ? files[0] : null) : label;
}
