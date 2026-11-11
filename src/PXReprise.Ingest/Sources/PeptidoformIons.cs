using PXReprise.Bundle;
using Quantification;

namespace PXReprise.Ingest.Sources;

/// <summary>AllPeptides.psmtsv's matched fragment ions, verbatim, for the rows a search's confidence rule passes:
/// <c>peptidoform_ions</c> (schema 0.0.17; D53, PXR-R44, 1.5.0 build list item 3).</summary>
/// <remarks>
/// <para>mzLib's psmtsv projection leaves the matched-ion fields out, having no faithful column shape for them
/// (<see cref="Readers.ReadPsmtsv"/>), so the six cells are read here, from the file's own columns, in a second pass
/// with <see cref="TsvReader"/>, one row at a time. Nothing is parsed: each cell is stored as MetaMorpheus wrote it.</para>
/// <para>Which rows (the user, 2026-11-11): targets (<c>T</c>, <c>ET</c>) that pass the search's confidence rule strictly
/// below 0.01, so a peptidoform has ions exactly when <c>peptidoforms_1pct</c> holds it; contaminants are left out, as
/// that view leaves them out. An ambiguous row (<c>Full Sequence</c> naming several options joined by <c>|</c>) is kept
/// under its first option, the one its peptidoform id names: MetaMorpheus writes "the ions matched to the best peptide
/// option", the first of the options its <c>Full Sequence</c> lists (<c>SpectralMatch.cs</c>, <c>PsmTsvWriter.cs</c>).</para>
/// </remarks>
public static class PeptidoformIons
{
    /// <summary>The six ion columns: MetaMorpheus's header, and the column it is stored in.</summary>
    public static readonly IReadOnlyList<(string Header, string Column)> IonColumns =
    [
        ("Matched Ion Series", "matched_ion_series"),
        ("Matched Ion Mass-To-Charge Ratios", "matched_ion_mz"),
        ("Matched Ion Mass Diff (Da)", "matched_ion_mass_diff_da"),
        ("Matched Ion Mass Diff (Ppm)", "matched_ion_mass_diff_ppm"),
        ("Matched Ion Intensities", "matched_ion_intensities"),
        ("Matched Ion Counts", "matched_ion_counts"),
    ];

    /// <summary>What one read found.</summary>
    /// <param name="Rows">Rows of the file, the header not counted.</param>
    /// <param name="Passed">Target rows that pass the rule.</param>
    /// <param name="Written">Rows written: passed, with an ion series and a peptidoform in the bundle.</param>
    /// <param name="NoIonSeries">Passed, but the <c>Matched Ion Series</c> cell is blank (MetaMorpheus's " " for a match
    /// with no ion list): left out, since the column is required and a blank is not "no ion matched".</param>
    /// <param name="NoPeptidoform">Passed, but no peptidoform of the bundle has its id: left out, never a dangling row.</param>
    /// <param name="MissingHeaders">Ion headers the file does not have; any, and nothing is read.</param>
    public sealed record Report(long Rows, long Passed, long Written, long NoIonSeries, long NoPeptidoform, IReadOnlyList<string> MissingHeaders);

    /// <summary>The ion rows of one AllPeptides.psmtsv under <paramref name="choice"/>'s rule.</summary>
    /// <param name="peptidoformIds">The bundle's peptidoform ids; a row whose id is not among them is left out and counted.</param>
    public static (List<Row> Rows, Report Report) Read(string path, string datasetId, ProformaCache proforma,
        Identifications.ConfidenceChoice choice, IReadOnlySet<string> peptidoformIds, ReaderLog? log = null)
    {
        using var reader = TsvReader.Open(path);
        var missing = IonColumns.Select(c => c.Header).Where(h => reader.Column(h) < 0).ToList();
        var rows = new List<Row>();
        if (missing.Count > 0)
        {
            log?.Record(path, "datarepo-tsv", 0, "matched-ion cells not read: the file has no " + string.Join(", ", missing));
            return (rows, new Report(0, 0, 0, 0, 0, missing));
        }
        var full = reader.Column("Full Sequence");
        var status = reader.Column("Decoy/Contaminant/Target");
        var q = reader.Column("QValue");
        var notch = reader.Column("QValue Notch");
        var pepQ = reader.Column("PEP_QValue");
        var ions = IonColumns.Select(c => (c.Column, Index: reader.Column(c.Header))).ToList();
        long passed = 0, noSeries = 0, noPeptidoform = 0;
        while (reader.Read())
        {
            if (Identifications.TargetDecoy(reader.Cell(status)) != "target") continue;
            var pepQValue = Value(reader.Cell(pepQ));
            var notchQValue = Value(reader.Cell(notch));
            if (!QuantifiedPsmRule.PassesConfidence(choice.Tier, Value(reader.Cell(q)) ?? double.NaN, notchQValue, pepQValue)) continue;
            passed++;
            var fullSequence = reader.Cell(full);
            var parsed = proforma.Get(Identifications.First(fullSequence));
            var id = $"{datasetId}:{parsed.Proforma}";
            if (string.IsNullOrEmpty(parsed.Proforma) || !peptidoformIds.Contains(id))
            {
                noPeptidoform++;
                continue;
            }
            var row = new Row
            {
                ["dataset_id"] = datasetId,
                ["peptidoform_id"] = id,
                ["full_sequence"] = Identifications.Verbatim(fullSequence),
                ["selected_by"] = choice.Rule,
                ["pep_q_value"] = pepQValue,
                ["q_value_notch"] = notchQValue,
            };
            foreach (var (column, index) in ions) row[column] = Identifications.Verbatim(reader.Cell(index));
            if (row["matched_ion_series"] is null)
            {
                noSeries++;
                continue;
            }
            rows.Add(row);
        }
        log?.Record(path, "datarepo-tsv", reader.Rows, "matched-ion cells, verbatim: mzLib's psmtsv projection leaves them out");
        return (rows, new Report(reader.Rows, passed, rows.Count, noSeries, noPeptidoform, missing));
    }

    /// <summary>A q-value cell as <see cref="Identifications"/> reads it: the first of <c>|</c>-joined values, null when
    /// it is not a number.</summary>
    private static double? Value(string cell) =>
        PyFormat.TryParseFloat(Identifications.First(cell), out var v) && !double.IsNaN(v) ? v : null;
}
