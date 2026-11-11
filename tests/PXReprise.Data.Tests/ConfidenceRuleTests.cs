using System.Globalization;
using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;
using PXReprise.Runner;
using Quantification;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 2 (D55, schema 0.0.17): each search's confidence rule, decided once at ingest by
/// mzLib's <see cref="QuantifiedPsmRule.ChooseTier"/> over its target PSMs, recorded in <c>datasets.confidence_rule</c>;
/// <c>peptidoforms.best_pep_q_value</c>; and the two findings on the rule.</summary>
public class ConfidenceRuleTests
{
    private static Row Psm(string status, double? pep, double? pepQ, double? notch, double? q = 0.001) => new()
    {
        ["target_decoy"] = status, ["pep"] = pep, ["pep_q_value"] = pepQ, ["q_value_notch"] = notch, ["q_value"] = q,
    };

    /// <summary>The searches of <c>PtmQtlEngineTests.EachSearchFiltersOnTheTierMzLibChoosesForIt</c> (ptmQtl S7).
    /// trained: PEP q-values, and target PEPs that differ. untrained: MetaMorpheus's 2 for every PEP q-value, with notch
    /// q-values. failed: PEP 0 for every target, and no notch. The decoys' and the contaminant's values change nothing.</summary>
    private static readonly Dictionary<string, Row[]> Searches = new(StringComparer.Ordinal)
    {
        ["trained"] = [Psm("target", 0.0, 0.005, 0.5, 0.5), Psm("target", 0.02, 0.01, 0.001, 0.001), Psm("decoy", 0.95, 0.9, 0.9, 0.9)],
        ["untrained"] = [Psm("target", 2.0, 2.0, 0.009, 0.5), Psm("target", 2.0, 2.0, null, 0.001), Psm("decoy", 2.0, 2.0, 0.9, 0.9)],
        ["failed"] = [Psm("target", 0.0, 0.001, null, 0.009), Psm("contaminant", 0.7, 0.001, 0.001, 0.01), Psm("decoy", 0.9, 0.5, 0.5, 0.5)],
    };

    /// <summary>The ingest and ptmQtl's S7 decide a search's tier from the same rows the same way, so one search can
    /// never be on two rules (the user, 2026-11-11: decide from target PSMs, as S7 does).</summary>
    [Test]
    public void TheIngestChoosesTheTierPtmQtlS7Chooses()
    {
        using var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        static string Sql(object? v) => v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : v is string s ? $"'{s}'" : "NULL";
        var values = Searches.SelectMany(kv => kv.Value.Select(r =>
            $"('{kv.Key}', {Sql(r["target_decoy"])}, {Sql(r["pep"])}, {Sql(r["pep_q_value"])}, {Sql(r["q_value_notch"])}, {Sql(r["q_value"])})"));
        using (var c = con.CreateCommand())
        {
            c.CommandText = "CREATE TABLE t AS SELECT * FROM (VALUES " + string.Join(", ", values)
                + ") v(search, target_decoy, pep, pep_q_value, q_value_notch, q_value)";
            c.ExecuteNonQuery();
        }
        var s7 = PtmQtlBundle.Tiers(con, "t");
        Assert.Multiple(() =>
        {
            foreach (var (search, rows) in Searches)
                Assert.That(Identifications.ChooseConfidence(rows).Tier, Is.EqualTo(s7[search]), search);
            Assert.That(s7.Values, Is.EquivalentTo(new[] { QuantifiedPsmTier.PepQValue, QuantifiedPsmTier.QValueNotch, QuantifiedPsmTier.QValue }),
                "the three searches reach the three tiers");
        });
    }

    [Test]
    public void TheChoiceSaysWhatDecidedIt()
    {
        var trained = Identifications.ChooseConfidence(Searches["trained"]);
        var untrained = Identifications.ChooseConfidence(Searches["untrained"]);
        var failed = Identifications.ChooseConfidence(Searches["failed"]);
        Assert.Multiple(() =>
        {
            Assert.That((trained.Rule, trained.TargetPsms, trained.PepQValueInRange, trained.SolePep), Is.EqualTo(("pep_q_value", 2L, true, (double?)null)));
            Assert.That((untrained.Rule, untrained.PepQValueInRange, untrained.NotchQValueInRange, untrained.SolePep),
                Is.EqualTo(("q_value_notch", false, true, (double?)2.0)));
            Assert.That((failed.Rule, failed.PepQValueInRange, failed.NotchQValueInRange, failed.SolePep), Is.EqualTo(("q_value", true, false, (double?)0.0)));
        });
    }

    /// <summary>Only targets decide. A search whose targets all carry PEP 0 has not shown that PEP trained, however its
    /// decoys vary (mzLib's own comment would pass the decoys' PEPs too; no served deposit is decided differently,
    /// <c>results/confidence_rule_2026-11-11/tier_targets_vs_all.out</c>).</summary>
    [Test]
    public void DecoysDoNotDecideTheRule()
    {
        Row[] rows = [Psm("target", 0.0, 0.001, 0.001), Psm("target", 0.0, 0.002, 0.002), Psm("decoy", 0.6, 0.5, 0.5), Psm("decoy", 0.9, 0.7, 0.7)];
        Assert.That(Identifications.ChooseConfidence(rows).Rule, Is.EqualTo("q_value_notch"));
    }

    private static Dictionary<string, List<object?>> PeptideColumns(params (string Full, string Status, double Q, double PepQ)[] rows) => new(StringComparer.Ordinal)
    {
        ["full_sequence"] = rows.Select(r => (object?)r.Full).ToList(),
        ["accession"] = rows.Select(_ => (object?)"P12345").ToList(),
        ["q_value"] = rows.Select(r => (object?)r.Q.ToString("R", CultureInfo.InvariantCulture)).ToList(),
        ["q_value_notch"] = rows.Select(r => (object?)r.Q.ToString("R", CultureInfo.InvariantCulture)).ToList(),
        ["pep"] = rows.Select(_ => (object?)"0.1").ToList(),
        ["pep_q_value"] = rows.Select(r => (object?)r.PepQ.ToString("R", CultureInfo.InvariantCulture)).ToList(),
        ["decoy_contam_target"] = rows.Select(r => (object?)r.Status).ToList(),
    };

    /// <summary>The lowest PEP q-value among a peptidoform's rows, as written, 2 (MetaMorpheus's "none computed")
    /// included; and no such column at all for a parity ingest of Python 0.32.0's schema.</summary>
    [Test]
    public void APeptidoformKeepsItsBestPepQValueAsWritten()
    {
        var columns = PeptideColumns(("PEPK", "T", 0.002, 0.3), ("PEPK", "T", 0.004, 0.004), ("SEQK", "T", 0.001, 2.0));
        var cache = new ProformaCache(new ModRegistry(), IngestRules.Current);
        var rows = Identifications.PeptidoformRows(columns, "PXD1", cache, new Dictionary<string, long>(), pepQValue: true)
            .ToDictionary(r => (string)r["peptidoform"]!);
        var parity = Identifications.PeptidoformRows(columns, "PXD1", cache, new Dictionary<string, long>());
        Assert.Multiple(() =>
        {
            Assert.That(rows["PEPK"]["best_pep_q_value"], Is.EqualTo(0.004));
            Assert.That(rows["PEPK"]["best_q_value"], Is.EqualTo(0.002), "each column takes its own best row");
            Assert.That(rows["SEQK"]["best_pep_q_value"], Is.EqualTo(2.0));
            Assert.That(parity.All(r => !r.ContainsKey("best_pep_q_value")), "no column under Python 0.32.0's schema");
        });
    }

    private static Row Peptidoform(string status, double? q, double? notch, double? pepQ) => new()
    {
        ["target_decoy"] = status, ["best_q_value"] = q, ["best_q_value_notch"] = notch, ["best_pep_q_value"] = pepQ,
    };

    /// <summary>A row passes strictly below 0.01 on the rule's own value (mzLib's <c>PassesConfidence</c>), while the
    /// q-value rule of 1.4.5's view passes at 0.01.</summary>
    [Test]
    public void TheRulePassesStrictlyBelowOnePercentAndTheOldRuleAtIt()
    {
        var atThreshold = Peptidoform("target", 0.01, 0.01, 0.01);
        var below = Peptidoform("target", 0.02, 0.009, 0.009);
        var noPepQ = Peptidoform("target", 0.001, 0.001, null);
        Assert.Multiple(() =>
        {
            Assert.That(Identifications.PassesConfidence(QuantifiedPsmTier.PepQValue, atThreshold), Is.False);
            Assert.That(Identifications.PassesQValueRule(atThreshold), Is.True);
            Assert.That(Identifications.PassesConfidence(QuantifiedPsmTier.PepQValue, below), Is.True);
            Assert.That(Identifications.PassesConfidence(QuantifiedPsmTier.QValueNotch, below), Is.True);
            Assert.That(Identifications.PassesConfidence(QuantifiedPsmTier.QValue, below), Is.False);
            Assert.That(Identifications.PassesConfidence(QuantifiedPsmTier.PepQValue, noPepQ), Is.False, "no value for the tier fails");
        });
    }

    [Test]
    public void ASearchOnPepWithAsManyPeptidesHasNoFinding()
    {
        var trained = Identifications.ChooseConfidence(Searches["trained"]);
        Row[] peptidoforms = [Peptidoform("target", 0.001, 0.001, 0.001), Peptidoform("target", 0.02, 0.02, 0.005), Peptidoform("decoy", 0.001, 0.001, 0.001)];
        Assert.That(Ingester.ConfidenceRuleFindings(trained, peptidoforms, "PXD1"), Is.Empty);
    }

    /// <summary>The 21 deposits whose PEP rule passes fewer peptidoforms than the q-value rule (PXD057502: none) say so,
    /// with both counts, so a reader does not take them for small studies (the user, 2026-11-11). Decoys are not
    /// counted.</summary>
    [Test]
    public void FewerPeptidesUnderTheRuleIsAFindingWithBothCounts()
    {
        var trained = Identifications.ChooseConfidence(Searches["trained"]);
        Row[] peptidoforms =
        [
            Peptidoform("target", 0.001, 0.001, 0.001), Peptidoform("target", 0.005, 0.005, 0.2),
            Peptidoform("target", 0.01, 0.01, 0.01), Peptidoform("decoy", 0.001, 0.001, 0.001),
        ];
        var finding = Ingester.ConfidenceRuleFindings(trained, peptidoforms, "PXD1").Single();
        Assert.Multiple(() =>
        {
            Assert.That(finding["code"], Is.EqualTo("confidence_rule_fewer_peptides"));
            Assert.That(finding["finding_id"], Is.EqualTo("PXD1:confidence_rule_fewer_peptides"));
            Assert.That((string)finding["message"]!, Does.Contain("passes 1 target peptidoform(s)").And.Contain("passed 3."));
        });
    }

    /// <summary>A search that trained no PEP says so, and says why, whatever its counts (PXD058775).</summary>
    [Test]
    public void ASearchOffPepSaysWhy()
    {
        Row[] peptidoforms = [Peptidoform("target", 0.001, 0.001, 2.0)];
        var untrained = Ingester.ConfidenceRuleFindings(Identifications.ChooseConfidence(Searches["untrained"]), peptidoforms, "PXD1").ToList();
        var failed = Ingester.ConfidenceRuleFindings(Identifications.ChooseConfidence(Searches["failed"]), peptidoforms, "PXD1").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(untrained.Select(f => f["code"]), Is.EqualTo(new[] { "confidence_rule_fallback" }));
            Assert.That((string)untrained[0]["message"]!, Does.Contain("no target PSM has a PEP q-value between 0 and 1").And.Contain("`q_value_notch`"));
            Assert.That(failed.Select(f => f["code"]), Is.EqualTo(new[] { "confidence_rule_fallback" }));
            Assert.That((string)failed[0]["message"]!, Does.Contain("carry one PEP value (0.0)").And.Contain("`q_value`"));
        });
    }
}
