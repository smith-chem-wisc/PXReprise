using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;
using Quantification;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 3 (schema 0.0.17, D53): <c>peptidoform_ions</c>, the six matched-ion cells of every
/// AllPeptides.psmtsv target row the search's confidence rule passes, verbatim. The fixture's cells are in
/// MetaMorpheus's own format (<c>PsmTsvWriter.AddMatchedIonsData</c>), and it holds one row of each case the rule
/// decides (the user, 2026-11-11: ambiguous rows kept under their first option, contaminants left out).</summary>
public class PeptidoformIonsTests
{
    private const char Tab = (char)9;
    private const char Newline = (char)10;

    private static readonly string[] Header =
    [
        "File Name", "Full Sequence", "Decoy/Contaminant/Target", "QValue", "QValue Notch", "PEP_QValue",
        "Matched Ion Series", "Matched Ion Mass-To-Charge Ratios", "Matched Ion Mass Diff (Da)", "Matched Ion Mass Diff (Ppm)",
        "Matched Ion Intensities", "Matched Ion Counts",
    ];

    private static string[] Line(string full, string status, string q, string notch, string pepQ, string series = "[b2+1, b3+1];[y1+1]") =>
    [
        "run1", full, status, q, notch, pepQ, series,
        series == " " ? " " : "[b2+1:227.10263, b3+1:324.15540];[y1+1:147.11280]",
        series == " " ? " " : "[b2+1:0.00012, b3+1:-0.00031];[y1+1:0.00002]",
        series == " " ? " " : "[b2+1:0.53, b3+1:-0.96];[y1+1:0.14]",
        series == " " ? " " : "[b2+1:10450, b3+1:8812];[y1+1:30211]",
        series == " " ? " " : "3",
    ];

    /// <summary>One row per case: passes; at exactly 0.01 on PEP q-value (fails: strictly below); contaminant; decoy;
    /// ambiguous (two options, first kept); ET, a target; passes with a blank ion series; passes with no peptidoform in
    /// the bundle; passes on the notch only (PEP q-value is MetaMorpheus's unset 2).</summary>
    private static string Fixture()
    {
        string[][] lines =
        [
            Header,
            Line("PEPK", "T", "0.001", "0.001", "0.001"),
            Line("SEQK", "T", "0.001", "0.001", "0.01"),
            Line("CONK", "C", "0.001", "0.001", "0.001"),
            Line("DECK", "D", "0.5", "0.5", "0.5"),
            Line("PEPTIDEK|PEPTLDEK", "T", "0.002", "0.002", "0.002"),
            Line("ETK", "ET", "0.003", "0.003", "0.003"),
            Line("BLANKK", "T", "0.001", "0.001", "0.001", series: " "),
            Line("GONEK", "T", "0.001", "0.001", "0.001"),
            Line("NOTCHK", "T", "0.001", "0.004", "2"),
        ];
        var path = Path.Combine(Path.GetTempPath(), "datarepo-ions-" + Guid.NewGuid().ToString("N") + ".psmtsv");
        File.WriteAllText(path, string.Join(Newline, lines.Select(l => string.Join(Tab, l))) + Newline);
        return path;
    }

    private static readonly HashSet<string> Ids = new(
        new[] { "PEPK", "SEQK", "CONK", "DECK", "PEPTIDEK", "ETK", "BLANKK", "NOTCHK" }.Select(p => "PXD1:" + p), StringComparer.Ordinal);

    private static Identifications.ConfidenceChoice Choice(QuantifiedPsmTier tier) => new(tier, 10, tier == QuantifiedPsmTier.PepQValue, true, null);

    private static (List<Row> Rows, PeptidoformIons.Report Report) Read(QuantifiedPsmTier tier)
    {
        var path = Fixture();
        try
        {
            return PeptidoformIons.Read(path, "PXD1", new ProformaCache(new ModRegistry(), IngestRules.Current), Choice(tier), Ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void OnlyTargetsThatPassThePepRuleHaveIons()
    {
        var (rows, report) = Read(QuantifiedPsmTier.PepQValue);
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r["full_sequence"]), Is.EqualTo(new[] { "PEPK", "PEPTIDEK|PEPTLDEK", "ETK" }),
                "not at 0.01, not the contaminant or the decoy, not the notch-only row");
            Assert.That(report, Is.EqualTo(report with { Rows = 9, Passed = 5, Written = 3, NoIonSeries = 1, NoPeptidoform = 1 }));
            Assert.That(report.MissingHeaders, Is.Empty);
            Assert.That(rows.All(r => Equals(r["selected_by"], "pep_q_value")));
        });
    }

    [Test]
    public void ARowsCellsAreStoredAsWritten()
    {
        var pep = Read(QuantifiedPsmTier.PepQValue).Rows.Single(r => Equals(r["full_sequence"], "PEPK"));
        Assert.That(pep, Is.EquivalentTo(new Row
        {
            ["dataset_id"] = "PXD1",
            ["peptidoform_id"] = "PXD1:PEPK",
            ["full_sequence"] = "PEPK",
            ["selected_by"] = "pep_q_value",
            ["pep_q_value"] = 0.001,
            ["q_value_notch"] = 0.001,
            ["matched_ion_series"] = "[b2+1, b3+1];[y1+1]",
            ["matched_ion_mz"] = "[b2+1:227.10263, b3+1:324.15540];[y1+1:147.11280]",
            ["matched_ion_mass_diff_da"] = "[b2+1:0.00012, b3+1:-0.00031];[y1+1:0.00002]",
            ["matched_ion_mass_diff_ppm"] = "[b2+1:0.53, b3+1:-0.96];[y1+1:0.14]",
            ["matched_ion_intensities"] = "[b2+1:10450, b3+1:8812];[y1+1:30211]",
            ["matched_ion_counts"] = "3",
        }));
    }

    /// <summary>An ambiguous row keeps every option in <c>full_sequence</c> and is joined to its first option's
    /// peptidoform: MetaMorpheus wrote the ions of that option.</summary>
    [Test]
    public void AnAmbiguousRowIsKeptUnderItsFirstOption()
    {
        var ambiguous = Read(QuantifiedPsmTier.PepQValue).Rows.Single(r => ((string)r["full_sequence"]!).Contains('|'));
        Assert.That((ambiguous["peptidoform_id"], ambiguous["full_sequence"]), Is.EqualTo(("PXD1:PEPTIDEK", "PEPTIDEK|PEPTLDEK")));
    }

    [Test]
    public void ASearchOnTheNotchSelectsByTheNotch()
    {
        var rows = Read(QuantifiedPsmTier.QValueNotch).Rows;
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r["full_sequence"]), Is.EqualTo(new[] { "PEPK", "SEQK", "PEPTIDEK|PEPTLDEK", "ETK", "NOTCHK" }));
            Assert.That(rows.All(r => Equals(r["selected_by"], "q_value_notch")));
            Assert.That(rows.Single(r => Equals(r["full_sequence"], "NOTCHK"))["pep_q_value"], Is.EqualTo(2.0), "stored as written");
        });
    }

    [Test]
    public void AFileWithoutTheIonColumnsIsNotReadAndSaysSo()
    {
        var path = Path.Combine(Path.GetTempPath(), "datarepo-ions-" + Guid.NewGuid().ToString("N") + ".psmtsv");
        File.WriteAllText(path, string.Join(Tab, Header.Take(6)) + Newline + string.Join(Tab, Line("PEPK", "T", "0.001", "0.001", "0.001").Take(6)) + Newline);
        try
        {
            var (rows, report) = PeptidoformIons.Read(path, "PXD1", new ProformaCache(new ModRegistry(), IngestRules.Current), Choice(QuantifiedPsmTier.PepQValue), Ids);
            var finding = Ingester.PeptidoformIonFindings(report, "PXD1").Single();
            Assert.Multiple(() =>
            {
                Assert.That(rows, Is.Empty);
                Assert.That(report.MissingHeaders, Has.Count.EqualTo(6));
                Assert.That(finding["code"], Is.EqualTo("peptidoform_ions_not_read"));
                Assert.That((string)finding["message"]!, Does.Contain("`Matched Ion Series`"));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NoAllPeptidesFileIsAFindingAndRowsLeftOutAreCounted()
    {
        var report = Read(QuantifiedPsmTier.PepQValue).Report;
        Assert.Multiple(() =>
        {
            Assert.That(Ingester.PeptidoformIonFindings(null, "PXD1").Single()["code"], Is.EqualTo("peptidoform_ions_not_read"));
            var leftOut = Ingester.PeptidoformIonFindings(report, "PXD1").Single();
            Assert.That(leftOut["code"], Is.EqualTo("peptidoform_ions_left_out"));
            Assert.That((string)leftOut["message"]!, Does.Contain("Of 5 AllPeptides.psmtsv target row(s)").And.Contain("2 have no row").And.Contain("1 with a blank"));
            Assert.That(Ingester.PeptidoformIonFindings(report with { NoIonSeries = 0, NoPeptidoform = 0 }, "PXD1"), Is.Empty);
        });
    }

    /// <summary>A bundle holding ion rows passes the writer's checks only when every row names a peptidoform of the
    /// bundle and (peptidoform_id, full_sequence) is unique.</summary>
    [Test]
    public void TheWriterChecksTheJoinAndTheKey()
    {
        IReadOnlyDictionary<string, object?> Ion(string id, string full) => new Row { ["dataset_id"] = "PXD1", ["peptidoform_id"] = id, ["full_sequence"] = full };
        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
        {
            ["datasets"] = [new Row { ["dataset_id"] = "PXD1" }],
            ["peptidoforms"] = [new Row { ["peptidoform_id"] = "PXD1:PEPK", ["dataset_id"] = "PXD1" }],
            ["peptidoform_ions"] = [Ion("PXD1:PEPK", "PEPK"), Ion("PXD1:PEPK", "PEPK"), Ion("PXD1:GONEK", "GONEK")],
        };
        var problems = Integrity.Check(tables);
        Assert.Multiple(() =>
        {
            Assert.That(problems, Has.Some.Contains("peptidoform_ions.(peptidoform_id, full_sequence)"));
            Assert.That(problems, Has.Some.Contains("peptidoform_ions.peptidoform_id -> peptidoforms.peptidoform_id"));
        });
    }
}
