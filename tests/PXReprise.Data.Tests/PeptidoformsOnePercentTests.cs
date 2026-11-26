using System.Globalization;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;
using Quantification;

namespace PXReprise.Data.Tests;

/// <summary>Catalog format 17 (D78, replacing D55): <c>peptidoforms_1pct</c> reads each search's recorded 1% rule, the
/// filter its results.txt names, with exactly the test the ingest applied to <c>peptidoform_ions</c>
/// (<see cref="Identifications.PassesRule(string?, IReadOnlyDictionary{string, object?})"/>).</summary>
public class PeptidoformsOnePercentTests
{
    /// <summary>Every value a peptidoform's three q-values can take that the rule could read differently: none, NaN,
    /// negative, 0, below, just below, at and just above 0.01, MetaMorpheus's unset 2, and real values above 1
    /// (PXD020462).</summary>
    private static readonly double?[] Values = [null, double.NaN, -1.0, 0.0, 0.005, 0.00999999, 0.01, 0.01000001, 0.5, 1.0, 1.0212, 2.0];

    private static readonly string[] Statuses = ["target", "decoy", "contaminant"];

    /// <summary>The schema's rules, D55's word for one it no longer holds, a word it never held, and none.</summary>
    private static readonly string?[] Rules = [.. Identifications.RuleValues, "pep", null];

    private static string Sql(double? v) =>
        v is not { } d ? "NULL" : double.IsNaN(d) ? "'NaN'::DOUBLE" : d.ToString("R", CultureInfo.InvariantCulture) + "::DOUBLE";

    private static void Exec(DuckDBConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static List<string> Strings(DuckDBConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        using var r = c.ExecuteReader();
        var values = new List<string>();
        while (r.Read()) values.Add(r.GetString(0));
        return values;
    }

    private static HashSet<string> Ids(DuckDBConnection con, string sql) => new(Strings(con, sql), StringComparer.Ordinal);

    private static string ViewBody() =>
        CatalogBuilder.AcceptedViews.Single(kv => kv.Key == "peptidoforms_1pct").Value
            .Replace("{t}", PyFormat.FloatRepr(Identifications.ProducerThreshold));

    /// <summary>The CLASS, not one case: every combination of rule, status and the three q-values, through the view's own
    /// SQL in DuckDB, against the ingest's test in C#. One row per combination; a dataset per rule.</summary>
    [Test]
    public void TheViewPassesExactlyTheRowsTheIngestPasses()
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        Exec(con, "CREATE TABLE datasets (dataset_id VARCHAR, confidence_rule VARCHAR)");
        Exec(con, "CREATE TABLE peptidoforms (dataset_id VARCHAR, peptidoform_id VARCHAR, target_decoy VARCHAR, "
                  + "best_q_value DOUBLE, best_q_value_notch DOUBLE, best_pep_q_value DOUBLE)");
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<string>();
        for (var d = 0; d < Rules.Length; d++)
        {
            var dataset = $"PXD{d}";
            Exec(con, $"INSERT INTO datasets VALUES ('{dataset}', {(Rules[d] is { } rule ? $"'{rule}'" : "NULL")})");
            var n = 0;
            foreach (var status in Statuses)
                foreach (var q in Values)
                    foreach (var notch in Values)
                        foreach (var pepQ in Values)
                        {
                            var id = $"{dataset}:{n++}";
                            rows.Add($"('{dataset}', '{id}', '{status}', {Sql(q)}, {Sql(notch)}, {Sql(pepQ)})");
                            var row = new Row
                            {
                                ["target_decoy"] = status, ["best_q_value"] = q, ["best_q_value_notch"] = notch, ["best_pep_q_value"] = pepQ,
                            };
                            if (status == "target" && Identifications.PassesRule(Rules[d], row)) expected.Add(id);
                        }
        }
        foreach (var chunk in rows.Chunk(2000)) Exec(con, "INSERT INTO peptidoforms VALUES " + string.Join(", ", chunk));
        Exec(con, $"CREATE VIEW peptidoforms_1pct AS {ViewBody()}");

        var passed = Ids(con, "SELECT peptidoform_id FROM peptidoforms_1pct");
        Assert.Multiple(() =>
        {
            Assert.That(passed.Except(expected).Order(), Is.Empty, "passed by the view, not by the ingest");
            Assert.That(expected.Except(passed).Order(), Is.Empty, "passed by the ingest, not by the view");
            foreach (var (rule, d) in Rules.Select((r, i) => (r, i)))
            {
                var any = passed.Any(id => id.StartsWith($"PXD{d}:", StringComparison.Ordinal));
                Assert.That(any, Is.EqualTo(rule is not null && Identifications.RuleValues.Contains(rule)),
                    $"rule {rule ?? "NULL"}: a rule the schema does not hold passes nothing");
            }
            Assert.That(passed, Has.Count.GreaterThan(100), "the grid reaches passing rows on every rule");
        });
    }

    /// <summary>The view's columns are <c>peptidoforms</c>' own, in order, whatever it joins to read the rule.</summary>
    [Test]
    public void TheViewHasThePeptidoformsColumns()
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        Exec(con, "CREATE TABLE datasets (dataset_id VARCHAR, bundle_id VARCHAR, confidence_rule VARCHAR)");
        Exec(con, "CREATE TABLE peptidoforms (dataset_id VARCHAR, bundle_id VARCHAR, peptidoform_id VARCHAR, target_decoy VARCHAR, "
                  + "best_q_value DOUBLE, best_q_value_notch DOUBLE, best_pep_q_value DOUBLE)");
        Exec(con, $"CREATE VIEW peptidoforms_1pct AS {ViewBody()}");
        Assert.That(Strings(con, "SELECT column_name FROM information_schema.columns WHERE table_name = 'peptidoforms_1pct' ORDER BY ordinal_position"),
            Is.EqualTo(Strings(con, "SELECT column_name FROM information_schema.columns WHERE table_name = 'peptidoforms' ORDER BY ordinal_position")));
    }

    /// <summary>A parity catalog of Python 0.32.0's schema records no rule, and keeps 0.32.0's q-value view.</summary>
    [Test]
    public void AParityCatalogKeepsTheQValueRule()
    {
        string parity;
        using (SchemaContract.Python0320()) parity = CatalogBuilder.AcceptedViews.Single(kv => kv.Key == "peptidoforms_1pct").Value;
        Assert.Multiple(() =>
        {
            Assert.That(parity, Does.Contain("best_q_value <= {t}").And.Not.Contain("confidence_rule"));
            Assert.That(ViewBody(), Does.Contain("CASE d.confidence_rule").And.Contain("WHEN 'q_value_and_notch'").And.Not.Contain(" < "),
                "the search's own cut, at or below the threshold (D78), never D55's strict one");
        });
    }
}
