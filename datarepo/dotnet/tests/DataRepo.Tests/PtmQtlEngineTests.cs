using System.Globalization;
using DataRepo.Bundle;
using DataRepo.Runner;
using Quantification.PtmQtl;

namespace DataRepo.Tests;

/// <summary>The ptmqtl.* engines (ptmQtl 025-030; our 026, 031): ptmQtl's reference case, reproduced.</summary>
/// <remarks>Fixtures/ptmqtl-reference-v1 is ptmQtl's own `results/reference_case_v1` (see its PROVENANCE.md). The
/// inputs are parsed as ptmQtl's `tools/ReferenceCase` reads them; the engine core then has to give `expected/`:
/// numbers to 1e-12 relative, text exactly.</remarks>
public class PtmQtlEngineTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Dir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "ptmqtl-reference-v1");

    private static List<Dictionary<string, string>> Tsv(string relative)
    {
        var lines = File.ReadAllLines(Path.Combine(Dir, relative)).Where(l => l.Length > 0).ToList();
        var names = lines[0].Split('\t');
        return lines.Skip(1).Select(l =>
        {
            var cells = l.Split('\t');
            Assert.That(cells, Has.Length.EqualTo(names.Length), $"{relative}: {l}");
            return names.Select((n, i) => (n, cells[i])).ToDictionary(t => t.n, t => t.Item2, StringComparer.Ordinal);
        }).ToList();
    }

    private static double D(string v) => v.Length == 0 ? double.NaN : double.Parse(v, Inv);

    /// <summary>The reference inputs, one <see cref="PtmQtlDataset"/> per dataset.</summary>
    private static List<PtmQtlDataset> Datasets()
    {
        var runs = Tsv("inputs/runs.tsv");
        var enriched = Tsv("inputs/enrichment.tsv").Select(r => r["dataset"]).ToHashSet();
        var unimod = Tsv("inputs/unimod.tsv").ToDictionary(r => r["id_with_motif"], r => r["unimod"], StringComparer.Ordinal);
        var observations = Tsv("inputs/observations.tsv");
        var occupancy = Tsv("inputs/occupancy.tsv");
        return runs.Select(r => r["dataset"]).Distinct().Select(ds => new PtmQtlDataset(
            ds,
            runs.First(r => r["dataset"] == ds)["species"],
            observations.Where(o => o["dataset"] == ds).Select(o => new PeptidoformObservation(o["run"], o["full_sequence"], o["protein"],
                int.Parse(o["start"], Inv), int.Parse(o["end"], Inv), D(o["intensity"]),
                o["previous_residue"].Length > 0 ? o["previous_residue"][0] : null)).ToList(),
            occupancy.Where(c => c["dataset"] == ds).Select(c => new StoredOccupancyCell(c["run"], c["protein"], int.Parse(c["position"], Inv),
                c["residue"][0], c["site_type"], c["modification"], c["state"], D(c["fraction"]), D(c["intensity_modified"]),
                D(c["intensity_total"]), bool.Parse(c["unmodified_quantified"]))).ToList(),
            runs.Where(r => r["dataset"] == ds).ToDictionary(r => r["run"], r => r["combine_to"], StringComparer.Ordinal),
            unimod,
            enriched.Contains(ds))).ToList();
    }

    /// <summary>A row cell as the reference program writes it.</summary>
    private static string Cell(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        double d => double.IsNaN(d) ? "" : d.ToString("R", Inv),
        long l => l.ToString(Inv),
        string s => s,
        IEnumerable<object?> list => "[" + string.Join(",", list.Select(x => $"\"{x}\"")) + "]",
        _ => throw new ArgumentException($"unexpected cell {value.GetType()}"),
    };

    /// <summary>The engine's row against the expected one: every expected column, numbers to 1e-12 relative.</summary>
    private static void AssertRow(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, object?> actual, string where,
        IReadOnlyDictionary<string, Func<object?, string>>? overrides = null)
    {
        foreach (var (column, want) in expected)
        {
            Assert.That(actual.ContainsKey(column), $"{where}: no column {column}");
            var raw = actual[column];
            var got = overrides is not null && overrides.TryGetValue(column, out var f) ? f(raw) : Cell(raw);
            if (raw is double && want.Length > 0 && got.Length > 0)
            {
                var (w, g) = (double.Parse(want, Inv), double.Parse(got, Inv));
                Assert.That(Math.Abs(g - w), Is.LessThanOrEqualTo(1e-12 * Math.Max(1, Math.Abs(w))), $"{where}: {column} {got} vs {want}");
            }
            else Assert.That(got, Is.EqualTo(want), $"{where}: {column}");
        }
    }

    /// <summary>A one-accession list column, written by the reference as the bare accession.</summary>
    private static readonly Dictionary<string, Func<object?, string>> PairCells = new()
    {
        ["protein_accessions_a"] = v => (string)((List<object?>)v!).Single()!,
        ["protein_accessions_b"] = v => (string)((List<object?>)v!).Single()!,
    };

    [Test]
    public void ThePairRowsAreTheReferenceCasesRows()
    {
        var produced = new List<Row>();
        var pooled = new List<(string, PtmPair, PairSite, PairSite)>();
        foreach (var ds in Datasets())
        {
            var result = PtmQtlCore.SitePairs(ds);
            produced.AddRange(result.Rows);
            pooled.AddRange(result.Pairs.Select(p => (ds.DatasetId, p.Pair, p.A, p.B)));
        }
        produced.AddRange(PtmQtlCore.Pool("mouse", pooled));

        var expected = Tsv("expected/ptm_pairs.tsv");
        string Key(Func<string, string> get) => string.Join("|", get("result_type"), get("scope"), get("feature_key_a"), get("feature_key_b"));
        var byKey = produced.ToDictionary(r => Key(c => (string)r[c]!), r => r);
        Assert.That(byKey.Keys, Is.EquivalentTo(expected.Select(e => Key(c => e[c]))), "the same pairs, no more and no fewer");
        foreach (var e in expected)
            AssertRow(e, byKey[Key(c => e[c])], Key(c => e[c]), PairCells);
    }

    [Test]
    public void TheSiteTraitRowsAreTheReferenceCasesRows()
    {
        var d1 = Datasets().Single(d => d.DatasetId == "D1");
        var runs = Tsv("inputs/runs.tsv").Where(r => r["dataset"] == "D1").ToList();
        var trait = runs.GroupBy(r => r["combine_to"]).ToDictionary(g => g.Key, g => D(g.First()["age_months"]), StringComparer.Ordinal);
        var replicate = runs.GroupBy(r => r["combine_to"]).ToDictionary(g => g.Key, g => g.First()["replicate"], StringComparer.Ordinal);
        var result = PtmQtlCore.SiteTraits(d1, "age_months", trait, replicate);

        var expected = Tsv("expected/trait_effects.tsv");
        Assert.That(result.Rows.Select(r => (string)r["feature_key"]!), Is.EquivalentTo(expected.Select(e => e["feature_key"])));
        string[] side = ["status", "df", "replicates", "median_fraction", "replicate_variance", "residual_variance"];
        foreach (var e in expected)
        {
            var row = result.Rows.Single(r => (string)r["feature_key"]! == e["feature_key"]);
            var fit = result.Fits.Single(r => (string)r["feature_key"]! == e["feature_key"]);
            AssertRow(e.Where(kv => !side.Contains(kv.Key)).ToDictionary(), row, e["feature_key"],
                new Dictionary<string, Func<object?, string>> { ["protein_accessions"] = v => (string)((List<object?>)v!).Single()! });
            AssertRow(e.Where(kv => side.Contains(kv.Key)).ToDictionary(), fit, e["feature_key"] + " (side file)");
        }
    }

    [TestCase("Common Biological:Phosphorylation on S", true)]
    [TestCase("UniProt:Phosphoserine on S", true)]
    [TestCase("Trypsin Digested:GG (Ubiquitination Site) on K", true)]
    [TestCase("AspN Digested:DVFQQQTGG (SUMO-2/3 Site human) on K", true)]
    [TestCase("AspN Digested:DVFQQQTGG (SUMO-2/3 Site human) on D", false)]
    [TestCase("Common Biological:Hydroxylation on N", false)]
    [TestCase("Common Biological:Formylation on K", false)]
    [TestCase("Common Variable:Oxidation on M", false)]
    [TestCase("Common Artifact:Ammonia loss on N", false)]
    [TestCase("Phosphorylation on S", false)]
    public void TheBiologicalRuleIsS2(string modification, bool biological) =>
        Assert.That(PtmQtlCore.IsBiological(modification), Is.EqualTo(biological));

    [Test]
    public void AnEnrichedDepositGivesSameMoleculePairsOnlyAndNoSiteTraitFit()
    {
        var d3 = Datasets().Single(d => d.DatasetId == "D3");
        Assert.That(d3.Enriched);
        var result = PtmQtlCore.SitePairs(d3);
        Assert.That(result.Rows.Select(r => r["result_type"]).Distinct(), Is.EqualTo(new[] { "P" }));
        Assert.That(() => PtmQtlCore.SiteTraits(d3, "t", new Dictionary<string, double>(), new Dictionary<string, string>()),
            Throws.TypeOf<RunnerException>().With.Message.Contains("ptmQtl S3"));
    }

    /// <summary>A real bundle from aging's store, read and run (scratch, read-only; skipped where the store is absent).</summary>
    [Test, Category("RealData")]
    public void ARealBundleReadsAndRuns()
    {
        const string bundlePath = "F:/aging_data/repo/store/PXD035107/8d3e89df26f138e5";
        if (!Directory.Exists(bundlePath)) Assert.Ignore("aging's store is not on this machine");
        var (dataset, counts) = PtmQtlBundle.Read(DataRepo.Catalog.BundleRef.Load(bundlePath), enriched: false);
        TestContext.Out.WriteLine(string.Join("\n", counts.Select(kv => $"{kv.Key}: {kv.Value}")));
        Assert.That(dataset.Observations, Is.Not.Empty);
        Assert.That(dataset.Occupancy, Is.Not.Empty);
        var pairs = PtmQtlCore.SitePairs(dataset);
        TestContext.Out.WriteLine($"rows {pairs.Rows.Count}: P {pairs.Rows.Count(r => (string)r["result_type"]! == "P")}, "
            + $"A {pairs.Rows.Count(r => (string)r["result_type"]! == "A")}; left out: "
            + string.Join(", ", pairs.LeftOut.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.That(DataRepo.Bundle.ArrowTables.FromRows("ptm_pairs", pairs.Rows).Length, Is.EqualTo(pairs.Rows.Count),
            "the rows fit the ptm_pairs schema");
    }

    [Test]
    public void ANameHoldingBracketsIsReadWhole()
    {
        Assert.That(PtmQtlCore.ModificationNames("PEPD[Metal:Cu[I] on D]S[Common Biological:Phosphorylation on S]K"),
            Is.EqualTo(new[] { "Metal:Cu[I] on D", "Common Biological:Phosphorylation on S" }));
        Assert.That(PtmQtlCore.Categories(new[] { "AD[Metal:Fe[III] on D]K" })["Fe[III] on D"], Is.EqualTo("Metal"));
    }

    [Test]
    public void AModificationWrittenWithTwoCategoriesIsRefused()
    {
        var obs = new[]
        {
            new PeptidoformObservation("r1", "PEPS[Common Biological:Phosphorylation on S]K", "P1", 1, 6, 10),
            new PeptidoformObservation("r1", "PEPS[UniProt:Phosphorylation on S]K", "P1", 1, 6, 10),
        };
        Assert.That(() => PtmQtlCore.Categories(obs), Throws.TypeOf<RunnerException>().With.Message.Contains("two categories"));
    }
}
