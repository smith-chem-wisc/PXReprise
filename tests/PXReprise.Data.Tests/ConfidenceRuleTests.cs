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

    /// <summary>trained: PEP q-values, and target PEPs that differ. untrained: MetaMorpheus's 2 for every PEP q-value, with
    /// notch q-values. failed: PEP 0 for every target, and no notch. The decoys' and the contaminant's values change
    /// nothing.</summary>
    private static readonly Dictionary<string, Row[]> Searches = new(StringComparer.Ordinal)
    {
        ["trained"] = [Psm("target", 0.0, 0.005, 0.5, 0.5), Psm("target", 0.02, 0.01, 0.001, 0.001), Psm("decoy", 0.95, 0.9, 0.9, 0.9)],
        ["untrained"] = [Psm("target", 2.0, 2.0, 0.009, 0.5), Psm("target", 2.0, 2.0, null, 0.001), Psm("decoy", 2.0, 2.0, 0.9, 0.9)],
        ["failed"] = [Psm("target", 0.0, 0.001, null, 0.009), Psm("contaminant", 0.7, 0.001, 0.001, 0.01), Psm("decoy", 0.9, 0.5, 0.5, 0.5)],
    };

    /// <summary>The ingest decides each search's tier from its target PSMs (the user, 2026-11-11). ptmQtl's S7 decided it
    /// the same way until ptmQtl D82, which reads the search's own filter instead (<c>PtmQtlBundle.Filter</c>); PXR-R81
    /// asks the same of this rule.</summary>
    [Test]
    public void TheIngestChoosesOneTierPerSearchFromItsTargetPsms()
    {
        var tiers = Searches.ToDictionary(kv => kv.Key, kv => Identifications.ChooseConfidence(kv.Value).Tier, StringComparer.Ordinal);
        Assert.That(tiers, Is.EquivalentTo(new Dictionary<string, QuantifiedPsmTier>
        {
            ["trained"] = QuantifiedPsmTier.PepQValue,
            ["untrained"] = QuantifiedPsmTier.QValueNotch,
            ["failed"] = QuantifiedPsmTier.QValue,
        }), "the three searches reach the three tiers");
    }

    [Test]
    public void TheChoiceSaysWhatDecidedIt()
    {
        var trained = Identifications.ChooseConfidence(Searches["trained"]);
        var untrained = Identifications.ChooseConfidence(Searches["untrained"]);
        var failed = Identifications.ChooseConfidence(Searches["failed"]);
        Assert.Multiple(() =>
        {
            Assert.That((trained.Tier, trained.TargetPsms, trained.PepQValueInRange, trained.SolePep),
                Is.EqualTo((QuantifiedPsmTier.PepQValue, 2L, true, (double?)null)));
            Assert.That((untrained.Tier, untrained.PepQValueInRange, untrained.NotchQValueInRange, untrained.SolePep),
                Is.EqualTo((QuantifiedPsmTier.QValueNotch, false, true, (double?)2.0)));
            Assert.That((failed.Tier, failed.PepQValueInRange, failed.NotchQValueInRange, failed.SolePep),
                Is.EqualTo((QuantifiedPsmTier.QValue, true, false, (double?)0.0)));
            Assert.That(Searches.Where(kv => Identifications.PepIsUsable(kv.Value)).Select(kv => kv.Key), Is.EqualTo(new[] { "trained" }),
                "the whole-search PEP test the PEP filter's training reading uses");
        });
    }

    /// <summary>Only targets decide. A search whose targets all carry PEP 0 has not shown that PEP trained, however its
    /// decoys vary (mzLib's own comment would pass the decoys' PEPs too; no served deposit is decided differently,
    /// <c>results/confidence_rule_2026-11-11/tier_targets_vs_all.out</c>).</summary>
    [Test]
    public void DecoysDoNotDecideWhetherPepIsUsable()
    {
        Row[] rows = [Psm("target", 0.0, 0.001, 0.001), Psm("target", 0.0, 0.002, 0.002), Psm("decoy", 0.6, 0.5, 0.5), Psm("decoy", 0.9, 0.7, 0.7)];
        Assert.That(Identifications.PepIsUsable(rows), Is.False);
    }

    /// <summary>D78 (PXR-R81): the rule is the filter the search names, read as MetaMorpheus applies it, and under the
    /// PEP q-value the training decides the row test (ptmQtl's point: <c>pep_q_value</c> alone cannot say how an
    /// untrained match is read).</summary>
    [Test]
    public void TheRuleIsTheSearchsOwnFilter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Identifications.RuleOf(ProducerFilter.QValueAndNotch), Is.EqualTo("q_value_and_notch"));
            Assert.That(Identifications.RuleOf(ProducerFilter.QValueNotch), Is.EqualTo("q_value_notch"));
            Assert.That(Identifications.RuleOf(ProducerFilter.QValue), Is.EqualTo("q_value"));
            Assert.That(Identifications.RuleOf(ProducerFilter.PepQValue, PepTraining.Every), Is.EqualTo("pep_q_value"));
            Assert.That(Identifications.RuleOf(ProducerFilter.PepQValue, PepTraining.None), Is.EqualTo("q_value_notch"),
                "trained for none: MetaMorpheus judges every match by its notch");
            Assert.That(Identifications.RuleOf(ProducerFilter.PepQValue, PepTraining.Some), Is.EqualTo("pep_q_value_or_notch"));
            Assert.That(Identifications.RuleOf(ProducerFilter.QValue, PepTraining.Some), Is.EqualTo("q_value"), "training matters under PEP only");
        });
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

    /// <summary>Each rule's row test, at or below 0.01 as the search cuts (D78; D55's rule cut strictly below).</summary>
    [Test]
    public void EachRulePassesAtOnePercentOnItsOwnValues()
    {
        bool Passes(string rule, double? q, double? notch, double? pepQ) => Identifications.PassesRule(rule, Peptidoform("target", q, notch, pepQ));
        Assert.Multiple(() =>
        {
            Assert.That(Passes("q_value_and_notch", 0.01, 0.01, null), Is.True, "at the threshold passes");
            Assert.That(Passes("q_value_and_notch", 0.01, 0.02, null), Is.False, "the notch too, where there is one");
            Assert.That(Passes("q_value_and_notch", 0.01, null, null), Is.True, "no notch: the q-value alone");
            Assert.That(Passes("q_value", 0.01, 0.5, 0.5), Is.True, "the pooled q-value alone");
            Assert.That(Passes("q_value_notch", 0.5, 0.01, 0.5), Is.True);
            Assert.That(Passes("q_value_notch", 0.01, null, null), Is.True, "no notch: its q-value, as MetaMorpheus has it");
            Assert.That(Passes("pep_q_value", 0.001, 0.001, 2.0), Is.False, "MetaMorpheus's 2 (not computed) fails");
            Assert.That(Passes("pep_q_value", 0.5, 0.5, 0.01), Is.True);
            Assert.That(Passes("pep_q_value", 0.001, 0.001, null), Is.False, "no value for the rule fails");
            Assert.That(Passes("pep_q_value_or_notch", 0.5, 0.005, 2.0), Is.True, "outside [0, 1]: read on its notch");
            Assert.That(Passes("pep_q_value_or_notch", 0.001, 0.001, 0.5), Is.False, "inside [0, 1]: read on its PEP q-value");
            Assert.That(Passes("no_such_rule", 0.0, 0.0, 0.0), Is.False, "a rule it does not name passes nothing");
        });
    }

    /// <summary>The SQL the catalog's <c>peptidoforms_1pct</c> and ptmQtl's S7 run is the C# test the ingest runs, on every
    /// combination of values, NULLs included; and up to 1.1.14 it is 1.4.5's view.</summary>
    [Test]
    public void TheSqlAndTheIngestAgreeOnEveryRow()
    {
        double?[] values = [null, -1.0, 0.0, 0.005, 0.01, 0.010000001, 0.5, 1.0, 2.0];
        var rows = (from q in values from notch in values from pep in values select (q, notch, pep)).ToList();
        using var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        static string Sql(double? v) => v is { } d ? d.ToString("R", CultureInfo.InvariantCulture) + "::DOUBLE" : "NULL::DOUBLE";
        using (var c = con.CreateCommand())
        {
            c.CommandText = "CREATE TABLE p AS SELECT * FROM (VALUES "
                + string.Join(", ", rows.Select((r, i) => $"({i}, {Sql(r.q)}, {Sql(r.notch)}, {Sql(r.pep)})"))
                + ") v(i, best_q_value, best_q_value_notch, best_pep_q_value)";
            c.ExecuteNonQuery();
        }
        HashSet<long> Passing(string predicate)
        {
            using var c = con.CreateCommand();
            c.CommandText = $"SELECT i FROM p WHERE {predicate}";
            using var reader = c.ExecuteReader();
            var ids = new HashSet<long>();
            while (reader.Read()) ids.Add(Convert.ToInt64(reader.GetValue(0)));
            return ids;
        }
        Assert.Multiple(() =>
        {
            foreach (var rule in Identifications.RuleValues)
            {
                var expected = rows.Select((r, i) => (r, i)).Where(x => Identifications.PassesRule(rule, x.r.q, x.r.notch, x.r.pep)).Select(x => (long)x.i).ToHashSet();
                Assert.That(Passing(Identifications.PassesRuleSql(rule, "best_")), Is.EquivalentTo(expected), rule);
            }
            Assert.That(Passing(Identifications.PassesRuleSql("q_value_and_notch", "best_")),
                Is.EquivalentTo(Passing("best_q_value <= 0.01 AND (best_q_value_notch IS NULL OR best_q_value_notch <= 0.01)")),
                "up to 1.1.14 the rule is the one datarepo 1.4.5's peptidoforms_1pct read");
        });
    }

    /// <summary>D55's two findings are gone; the one thing the rule cannot settle, a PEP filter trained for some
    /// peptides only, says so.</summary>
    [Test]
    public void OnlyAPartlyTrainedPepFilterIsAFinding()
    {
        Assert.Multiple(() =>
        {
            foreach (var rule in Identifications.RuleValues.Where(r => r != "pep_q_value_or_notch"))
                Assert.That(Ingester.ConfidenceRuleFindings(rule, "PXD1"), Is.Empty, rule);
            var finding = Ingester.ConfidenceRuleFindings("pep_q_value_or_notch", "PXD1").Single();
            Assert.That(finding["code"], Is.EqualTo("confidence_rule_partly_trained"));
            Assert.That((string)finding["message"]!, Does.Contain("only some of its target peptides"));
        });
    }
}

/// <summary>The rule at ingest, on a copy of the fixture instance (a MetaMorpheus 1.1.11 search).</summary>
[NonParallelizable]
public class ConfidenceRuleIngestTests
{
    private string _root = "";
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string SearchDir => Path.Combine(_root, "data", "work_root", "run_test", "PXD999999", "04_search");

    [SetUp]
    public void CopyInstance()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-rule-" + Guid.NewGuid().ToString("N"));
        var to = Path.Combine(_root, "data");
        foreach (var dir in Directory.EnumerateDirectories(DataPaths.Instance, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(DataPaths.Instance, dir)));
        foreach (var file in Directory.EnumerateFiles(DataPaths.Instance, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(DataPaths.Instance, file)));
    }

    [TearDown]
    public void RemoveScratch()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    private IngestResult Ingest() => Ingester.Ingest(ManifestPath, "PXD999999", Path.Combine(_root, "store"), overwrite: true);

    private static List<Row> Table(IngestResult result, string table) =>
        ArrowTables.ReadParquet(Path.Combine(result.BundlePath, table + ".parquet")).Rows;

    [Test]
    public void AnOlderSearchIsOnTheQValueAndNotchAndItsIonsFollowIt()
    {
        var result = Ingest();
        var ions = Table(result, "peptidoform_ions");
        Assert.Multiple(() =>
        {
            Assert.That(Table(result, "datasets").Single()["confidence_rule"], Is.EqualTo("q_value_and_notch"));
            Assert.That(ions, Is.Not.Empty);
            Assert.That(ions.Select(r => r["selected_by"]).Distinct(), Is.EqualTo(new[] { "q_value_and_notch" }));
            var passing = Table(result, "peptidoforms").Where(p => Equals(p["target_decoy"], "target") && Identifications.PassesRule("q_value_and_notch", p))
                .Select(p => (string)p["peptidoform_id"]!).ToHashSet();
            Assert.That(ions.Select(r => (string)r["peptidoform_id"]!).ToHashSet(), Is.SubsetOf(passing), "ions only for peptidoforms the rule passes");
            Assert.That(result.Findings.Select(f => (string)f["code"]!), Has.None.StartsWith("confidence_rule_"));
        });
    }

    [Test]
    public void AnOlderSearchWithoutResultsTxtIsStillOnTheOlderRule()
    {
        File.Delete(Directory.EnumerateFiles(SearchDir, "results.txt", SearchOption.AllDirectories).Single());
        Assert.That(Table(Ingest(), "datasets").Single()["confidence_rule"], Is.EqualTo("q_value_and_notch"));
    }

    /// <summary>A release from 1.1.15 names its filter, so a search of one whose results.txt does not cannot be given a
    /// rule: the ingest refuses rather than write a guessed one, as ptmQtl's S7 refuses it.</summary>
    [Test]
    public void ALaterSearchThatNamesNoFilterIsRefused()
    {
        File.Delete(Directory.EnumerateFiles(SearchDir, "results.txt", SearchOption.AllDirectories).Single());
        var provenance = Path.Combine(SearchDir, "provenance.json");
        File.WriteAllText(provenance, File.ReadAllText(provenance).Replace("\"release\": \"1.1.11\"", "\"release\": \"1.1.15\""));
        Assert.That(() => Ingest(), Throws.InstanceOf<IngestException>().With.Message.Contains("is not one with a single filter"));
    }
}
