using System.Globalization;
using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Runner;
using Quantification.PtmQtl;

namespace PXReprise.Data.Tests;

/// <summary>The ptmqtl.* engines (ptmQtl 025-030; our 026, 031): ptmQtl's reference case, reproduced.</summary>
/// <remarks>Fixtures/ptmqtl-reference-v4 is ptmQtl's own `results/reference_case_v4` (DEF-PTM-PAIR v3: bounds, depth,
/// D78's null; DEF-SITE-TRAIT v3: log2 odds, bounds as counts, covariates; see its PROVENANCE.md). The inputs are parsed as `tools/ptmqtl-reference`
/// reads them; the engine core then has to give `expected/`: numbers to 1e-12 relative, text exactly.</remarks>
public class PtmQtlEngineTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Dir => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "ptmqtl-reference-v4");

    private sealed record Pool(string Id, IReadOnlySet<string> Members);

    private static Pool P(string id, params string[] members) => new(id, members.ToHashSet(StringComparer.Ordinal));

    /// <summary>D96 (G134): the batch pools a species again as its deposits arrive, and the store keeps each earlier pool.
    /// A pool whose members all sit in a larger one is its earlier version, not a candidate; pools that do not nest are
    /// both kept, so the catalog still refuses two.</summary>
    [Test]
    public void AnEarlierPoolOfASpeciesGivesWayToTheLargerOneAndPoolsThatDoNotNestBothStay()
    {
        var two = P("two", "a", "b");
        var three = P("three", "a", "b", "c");
        var four = P("four", "a", "b", "c", "d");
        Assert.That(PXReprise.Catalog.CatalogBuilder.NotSuperseded([two, three, four], p => p.Members), Is.EqualTo(new[] { four }));
        Assert.That(PXReprise.Catalog.CatalogBuilder.NotSuperseded([two], p => p.Members), Is.EqualTo(new[] { two }));
        var other = P("other", "a", "c");
        Assert.That(PXReprise.Catalog.CatalogBuilder.NotSuperseded([two, other], p => p.Members), Is.EqualTo(new[] { two, other }),
            "neither holds the other's members: two candidates, which the catalog refuses");
        var same = P("same", "a", "b");
        Assert.That(PXReprise.Catalog.CatalogBuilder.NotSuperseded([two, same], p => p.Members), Has.Count.EqualTo(2), "equal members are not superseded: still two");
    }

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
    /// <remarks>The reference case predates the sequence window (D35; ptmQtl 038 item 4), so every site is given one
    /// window, the same in every dataset, and v2's pooled rows stand as written.</remarks>
    private static List<PtmQtlDataset> Datasets()
    {
        var runs = Tsv("inputs/runs.tsv");
        var enriched = Tsv("inputs/enrichment.tsv").Select(r => r["dataset"]).ToHashSet();
        var unimod = Tsv("inputs/unimod.tsv").ToDictionary(r => r["id_with_motif"], r => r["unimod"], StringComparer.Ordinal);
        var observations = Tsv("inputs/observations.tsv");
        var occupancy = Tsv("inputs/occupancy.tsv");
        // Each dataset's MetaMorpheus release and each run's target PSMs (D40).
        var release = Tsv("inputs/datasets.tsv").ToDictionary(r => r["dataset"], r => r["search_engine_version"]);
        var depth = Tsv("inputs/depth.tsv");
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
            enriched.Contains(ds))
        {
            Windows = new OneWindowEach(),
            SearchEngineVersion = release.GetValueOrDefault(ds),
            TargetPsms = depth.Where(r => r["dataset"] == ds).ToDictionary(r => r["run"], r => long.Parse(r["target_psms"], Inv), StringComparer.Ordinal),
        }).ToList();
    }

    /// <summary>A window for every (accession, position), the same in every dataset.</summary>
    private sealed class OneWindowEach : IReadOnlyDictionary<(string, int), string?>
    {
        public string? this[(string, int) key] => $"window of {key.Item1} {key.Item2}";
        public bool TryGetValue((string, int) key, out string? value) { value = this[key]; return true; }
        public bool ContainsKey((string, int) key) => true;
        public IEnumerable<(string, int)> Keys => [];
        public IEnumerable<string?> Values => [];
        public int Count => 0;
        public IEnumerator<KeyValuePair<(string, int), string?>> GetEnumerator() => Enumerable.Empty<KeyValuePair<(string, int), string?>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    /// <summary>The engine's row against the expected one: every expected column, numbers to <paramref name="tolerance"/>
    /// relative (1e-12 by default).</summary>
    private static void AssertRow(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, object?> actual, string where,
        IReadOnlyDictionary<string, Func<object?, string>>? overrides = null, double tolerance = 1e-12)
    {
        foreach (var (column, want) in expected)
        {
            Assert.That(actual.ContainsKey(column), $"{where}: no column {column}");
            var raw = actual[column];
            var got = overrides is not null && overrides.TryGetValue(column, out var f) ? f(raw) : Cell(raw);
            if (raw is double && want.Length > 0 && got.Length > 0)
            {
                var (w, g) = (double.Parse(want, Inv), double.Parse(got, Inv));
                Assert.That(Math.Abs(g - w), Is.LessThanOrEqualTo(tolerance * Math.Max(1, Math.Abs(w))), $"{where}: {column} {got} vs {want}");
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
        var depthLike = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ds in Datasets())
        {
            var result = PtmQtlCore.SitePairs(ds);
            produced.AddRange(result.Rows);
            pooled.AddRange(result.Pairs.Select(p => (ds.DatasetId, p.Pair, p.A, p.B)));
            if (result.DepthNull?.DepthLike == true) depthLike.Add(ds.DatasetId);
        }
        produced.AddRange(PtmQtlCore.Pool("mouse", pooled, depthLike).Rows);

        var expected = Tsv("expected/ptm_pairs.tsv");
        string Key(Func<string, string> get) => string.Join("|", get("result_type"), get("scope"), get("feature_key_a"), get("feature_key_b"));
        var byKey = produced.ToDictionary(r => Key(c => (string)r[c]!), r => r);
        Assert.That(byKey.Keys, Is.EquivalentTo(expected.Select(e => Key(c => e[c]))), "the same pairs, no more and no fewer");
        foreach (var e in expected)
            AssertRow(e, byKey[Key(c => e[c])], Key(c => e[c]), PairCells);
    }

    /// <summary>D78 (D83): each deposit's depth null, its counts and its label, as the reference case has them.</summary>
    [Test]
    public void TheDepthNullIsTheReferenceCasesNull()
    {
        var expected = Tsv("expected/depth_null.tsv");
        var datasets = Datasets().ToDictionary(d => d.DatasetId);
        Assert.That(expected.Select(e => e["dataset"]), Is.EquivalentTo(datasets.Values.Where(PtmQtlCore.HasOccupancyResults).Select(d => d.DatasetId)));
        foreach (var e in expected)
        {
            var n = PtmQtlCore.SitePairs(datasets[e["dataset"]]).DepthNull!;
            Assert.Multiple(() =>
            {
                Assert.That(n.RealCalls.ToString(Inv), Is.EqualTo(e["real_calls"]), e["dataset"]);
                Assert.That(string.Join(",", n.NullCalls), Is.EqualTo(e["null_calls"]), e["dataset"]);
                Assert.That(Cell(n.Share), Is.EqualTo(e["share"]), e["dataset"]);
                Assert.That(Cell(n.DepthLike), Is.EqualTo(e["depth_like"]), e["dataset"]);
            });
        }
        Assert.That(expected.Select(e => e["depth_like"]).Distinct().Order(), Is.EqualTo(new[] { "False", "True" }), "the case holds a labelled deposit and one not");
    }

    /// <summary>D1 is MetaMorpheus 1.1.12, so its covered-zeros are the engine's; D2 is 1.1.13, so they are its own and
    /// the engine adds none (S1). Every count-only cell is given its peptidoform counts.</summary>
    [Test]
    public void CoveredZerosComeFromTheEngineUpTo1112AndFromMetaMorpheusAfter()
    {
        var leftOut = Datasets().ToDictionary(d => d.DatasetId, d => PtmQtlCore.SitePairs(d).LeftOut);
        Assert.Multiple(() =>
        {
            Assert.That(leftOut["D1"]["v3 bounds: covered-zero cells added from the engine's coverage"], Is.GreaterThan(0));
            Assert.That(leftOut["D1"]["v3 bounds: covered-zero cells from MetaMorpheus"], Is.EqualTo(0));
            Assert.That(leftOut["D1"]["v3 bounds: count-only cells given peptidoform counts"], Is.EqualTo(2));
            Assert.That(leftOut["D2"]["v3 bounds: covered-zero cells added from the engine's coverage"], Is.EqualTo(0));
            Assert.That(leftOut["D2"]["v3 bounds: covered-zero cells from MetaMorpheus"], Is.GreaterThan(0));
            Assert.That(leftOut["D1"]["D40 samples with occupancy and no depth (left out of every pair)"], Is.EqualTo(1));
        });
        var d1 = Datasets().Single(d => d.DatasetId == "D1");
        Assert.That(() => PtmQtlCore.SitePairs(d1 with { SearchEngineVersion = null }), Throws.TypeOf<RunnerException>().With.Message.Contains("S1"));
        var mixed = d1 with { Occupancy = [.. d1.Occupancy, d1.Occupancy[0] with { Run = "S2", State = "covered_zero" }] };
        Assert.That(() => PtmQtlCore.SitePairs(mixed), Throws.TypeOf<RunnerException>().With.Message.Contains("never mixed"));
    }

    /// <summary>v3's bounds at their edges, each counted, none guessed: an <c>intensity_unassigned</c> cell counts as
    /// stored, so no covered-zero is added there; an engine cell that saw the modified form where nothing is stored is
    /// counted, never added; the files of a design sample MetaMorpheus pooled (P32) are read as that sample; the
    /// site-trait reading leaves a stored <c>covered_zero</c> out; a deposit with no real call has no share and no
    /// label.</summary>
    [Test]
    public void TheBoundsEdgesAreCountedNeverGuessed()
    {
        const string Added = "v3 bounds: covered-zero cells added from the engine's coverage";
        var all = Datasets().ToDictionary(d => d.DatasetId);
        var d1 = all["D1"];
        Dictionary<string, long> Counts(PtmQtlDataset d, bool bounds = true)
        {
            var left = new Dictionary<string, long>(StringComparer.Ordinal);
            PtmQtlCore.Occupancy(d, d.Categories(), left, bounds);
            return left;
        }
        var baseline = Counts(d1);

        // S3: P2 S5's covered-zero is the engine's, since nothing is stored there; an intensity_unassigned cell is.
        var p2 = d1.Occupancy.First(c => c.Protein == "P2");
        var unassigned = d1 with { Occupancy = [.. d1.Occupancy, p2 with { Run = "S3", State = "intensity_unassigned" }] };
        // S2: P1 S13's stored cell removed; the engine still sees the modified form there.
        var dropped = d1 with { Occupancy = d1.Occupancy.Where(c => !(c.Protein == "P1" && c.Position == 13 && c.Run == "S2")).ToList() };
        // S14's two injections stored as one design sample, as MetaMorpheus writes a pooled sample (P32); S2 is not a
        // whole proteome (S3b), so neither its cells nor its observations reach type A.
        var pooledRuns = d1.RunToSample.ToDictionary(kv => kv.Key, kv => kv.Key.StartsWith("S14", StringComparison.Ordinal) ? "S14" : kv.Value, StringComparer.Ordinal);
        pooledRuns["S14"] = "S14";
        var pooled = d1 with
        {
            RunToSample = pooledRuns,
            Occupancy = d1.Occupancy.Where(c => c.Run != "S14_2").Select(c => c.Run == "S14_1" ? c with { Run = "S14" } : c).ToList(),
            OccupancyRuns = pooledRuns.Keys.Where(r => r != "S2").ToHashSet(StringComparer.Ordinal),
        };
        var pooledCells = PtmQtlCore.Occupancy(pooled, pooled.Categories(), new Dictionary<string, long>(), bounds: true);
        var none = PtmQtlCore.DepthNull("D0", [], [], new Dictionary<string, double>(), []);
        Assert.Multiple(() =>
        {
            Assert.That(Counts(unassigned)[Added], Is.EqualTo(baseline[Added] - 1));
            Assert.That(Counts(unassigned)["S1 intensity_unassigned"], Is.EqualTo(1));
            Assert.That(baseline["v3 bounds: engine saw the modified form, nothing stored (not added)"], Is.EqualTo(0));
            Assert.That(Counts(dropped)["v3 bounds: engine saw the modified form, nothing stored (not added)"], Is.EqualTo(1));
            Assert.That(pooledCells.Select(c => c.Run), Has.Some.EqualTo("S14").And.None.StartsWith("S14_").And.None.EqualTo("S2"));
            Assert.That(Counts(all["D2"], bounds: false)["S1 covered_zero: a bound, read only with bounds"], Is.GreaterThan(0));
            Assert.That((none.RealCalls, none.NullCalls, double.IsNaN(none.Share), none.DepthLike), Is.EqualTo((0L, new long[] { 0, 0, 0 }, true, false)));
        });
    }

    /// <summary>D78 in the run records: <c>site_pairs</c> stores the null's counts, and <c>pool_pairs</c> reads the label
    /// back to flag a depth-like dataset's pooled rows. A deposit with no real call stores no share.</summary>
    [Test]
    public void ADepthLikeLabelIsReadBackFromItsRunRecord()
    {
        var store = Scratch();
        try
        {
            Assert.That(PtmQtlEngine.DepthNullRecord(null), Is.Null);
            Assert.That(PtmQtlEngine.DepthNullRecord(new DepthNullResult(0, [0, 0, 0], double.NaN, false))!["share"], Is.Null);
            foreach (var (like, share) in new[] { (true, 0.25), (false, 0.05) })
            {
                var record = PtmQtlEngine.DepthNullRecord(new DepthNullResult(4, [1, 1, 1], share, like))!;
                Assert.That((record["real_calls"], record["share"], record["depth_like"]), Is.EqualTo(((object?)4L, (object?)share, (object?)like)));
                var artefact = WriteArtefact(store, PtmQtlEngine.SitePairsEngine, new() { ["bundle"] = $"b-{like}", ["enrichment"] = "e1" },
                    new() { ["species"] = "mouse", ["depth_null"] = record }, []);
                Assert.That(PtmQtlEngine.DepthLike(artefact), Is.EqualTo(like), $"depth_like {like}");
            }
        }
        finally { Remove(store); }
    }

    /// <summary>DEF-SITE-TRAIT v3 on D1 and D4, sex as a covariate: one-form runs enter as counts with each sample's
    /// depth, and a sample with no depth is left out (D1's S14_2).</summary>
    [Test]
    public void TheSiteTraitRowsAreTheReferenceCasesRows()
    {
        var expected = Tsv("expected/trait_effects.tsv");
        Assert.That(expected.Select(e => e["scope"]).Distinct(), Is.EqualTo(new[] { "D1", "D4" }));
        Assert.That(expected.Select(e => e["response"]).Distinct(), Is.EqualTo(new[] { "log2_odds_occupancy" }));
        Assert.That(expected.Any(e => e["status"] == "Fitted" && int.Parse(e["counted_runs"], Inv) > 0), "the case fits a site with counted runs");
        string[] side = ["status", "df", "replicates", "median_fraction", "replicate_variance", "residual_variance", "covariates",
            "residual_variance_borrowed", "counted_runs"];
        foreach (var ds in Datasets().Where(d => d.DatasetId is "D1" or "D4"))
        {
            var samples = Tsv("inputs/runs.tsv").Where(r => r["dataset"] == ds.DatasetId).GroupBy(r => r["combine_to"]).ToList();
            var trait = samples.ToDictionary(g => g.Key, g => D(g.First()["age_months"]), StringComparer.Ordinal);
            var replicate = samples.ToDictionary(g => g.Key, g => g.First()["replicate"], StringComparer.Ordinal);
            var covariates = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["sex"] = samples.ToDictionary(g => g.Key, g => g.First()["sex"], StringComparer.Ordinal),
            };
            var result = PtmQtlCore.SiteTraits(ds, "age_months", trait, replicate, covariates);
            var mine = expected.Where(e => e["scope"] == ds.DatasetId).ToList();
            Assert.That(result.Rows.Select(r => (string)r["feature_key"]!), Is.EquivalentTo(mine.Select(e => e["feature_key"])), ds.DatasetId);
            Assert.That((result.Covariates, result.TraitDepth!.N), Is.EqualTo((new[] { "sex=M" }, trait.Count - (ds.DatasetId == "D1" ? 1 : 0))), ds.DatasetId);
            // Every row's every mismatch is reported, not only the first.
            Assert.Multiple(() =>
            {
                foreach (var e in mine)
                {
                    var row = result.Rows.Single(r => (string)r["feature_key"]! == e["feature_key"]);
                    var fit = result.Fits.Single(r => (string)r["feature_key"]! == e["feature_key"]);
                    // A site with counted runs is fitted by the logit-normal model's optimizer, whose result differs across
                    // platforms near the 10th digit (Linux CI against this Windows build: 1.4e-10 relative on a replicate
                    // variance). Every other number is compared to 1e-12.
                    var tolerance = e["counted_runs"] != "0" ? 1e-8 : 1e-12;
                    AssertRow(e.Where(kv => !side.Contains(kv.Key) && kv.Key != "covariate_effects").ToDictionary(), row, e["feature_key"],
                        new Dictionary<string, Func<object?, string>> { ["protein_accessions"] = v => (string)((List<object?>)v!).Single()! }, tolerance);
                    AssertRow(e.Where(kv => side.Contains(kv.Key)).ToDictionary(), fit, e["feature_key"] + " (side file)", tolerance: tolerance);
                    // Each covariate's effect, as numbers, like every other number (its last digits differ by platform).
                    var want = e["covariate_effects"].Split(';').Select(D).ToArray();
                    var got = ((List<object?>)fit["covariate_effects"]!).Select(v => v is double d ? d : double.NaN).ToArray();
                    Assert.That(got, Has.Length.EqualTo(want.Length), e["feature_key"]);
                    for (var j = 0; j < want.Length; j++)
                        Assert.That(double.IsNaN(want[j]) ? double.IsNaN(got[j]) : Math.Abs(got[j] - want[j]) <= tolerance * Math.Max(1, Math.Abs(want[j])),
                            $"{e["feature_key"]}: covariate effect {j} {got[j]} vs {want[j]}");
                }
            });
        }
        // A sample with no trait value (a run the consumer gave no age) is left out, and counted.
        var d1 = Datasets().Single(d => d.DatasetId == "D1");
        var ages = Tsv("inputs/runs.tsv").Where(r => r["dataset"] == "D1" && r["combine_to"] != "S2").GroupBy(r => r["combine_to"])
            .ToDictionary(g => g.Key, g => D(g.First()["age_months"]));
        var left = PtmQtlCore.SiteTraits(d1, "t", ages, d1.RunToSample.Values.Distinct().ToDictionary(s => s, s => s)).LeftOut;
        Assert.That(left["D40 samples with the trait and no depth (left out of the fit)"], Is.EqualTo(1));
        Assert.That(left["samples with occupancy and no trait value (left out of the fit)"], Is.EqualTo(1));
    }

    /// <summary>DEF-SITE-TRAIT v3's depth evidence in the run record: shown, null where it cannot be computed.</summary>
    [Test]
    public void TheTraitDepthCorrelationIsRecordedAndNullWhereNotComputable()
    {
        Assert.That(PtmQtlEngine.TraitDepthRecord(null), Is.Null);
        var rec = PtmQtlEngine.TraitDepthRecord(StatisticalModels.SpearmanCorrelation.Correlate([1.0, 2, 3, 4, 5], [2.0, 1, 4, 3, 5]))!;
        Assert.That((rec["rho"], rec["n"]), Is.EqualTo(((object?)0.8, (object?)5L)));
        var flat = PtmQtlEngine.TraitDepthRecord(StatisticalModels.SpearmanCorrelation.Correlate([1.0, 1, 1], [2.0, 1, 4]))!;
        Assert.That((flat["rho"], flat["p"]), Is.EqualTo(((object?)null, (object?)null)));
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
        var bundle = RealBundle("PXD035107") ?? throw new IgnoreException($"no schema {SchemaContract.Version} bundle of PXD035107 under {RealStore}");
        var (dataset, counts) = PtmQtlBundle.Read(bundle, enriched: false);
        TestContext.Out.WriteLine(string.Join("\n", counts.Select(kv => $"{kv.Key}: {kv.Value}")));
        Assert.That(dataset.Observations, Is.Not.Empty);
        Assert.That(dataset.Occupancy, Is.Not.Empty);
        var pairs = PtmQtlCore.SitePairs(dataset);
        TestContext.Out.WriteLine($"rows {pairs.Rows.Count}: P {pairs.Rows.Count(r => (string)r["result_type"]! == "P")}, "
            + $"A {pairs.Rows.Count(r => (string)r["result_type"]! == "A")}; left out: "
            + string.Join(", ", pairs.LeftOut.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.That(PXReprise.Bundle.ArrowTables.FromRows("ptm_pairs", pairs.Rows).Length, Is.EqualTo(pairs.Rows.Count),
            "the rows fit the ptm_pairs schema");
    }

    private static readonly Dictionary<string, object?> StandInInstall = new()
    {
        ["distribution"] = "datarepo", ["version"] = "test", ["source"] = "test-stand-in", ["commit"] = "0",
    };

    private static string Scratch() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "datarepo-ptmqtl-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Remove(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* a DuckDB file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    [Test]
    public void PairsStoredAndReadBackPoolExactlyAsInMemory()
    {
        // P22: pool_pairs works from site_pairs artefacts, never from bundles, so the stored pairs must rebuild
        // mzLib's pairs exactly. Here the reference case is pooled both ways and every cell compared.
        var dir = Scratch();
        try
        {
            var inMemory = new List<(string, PtmPair, PairSite, PairSite)>();
            var stored = new List<(string, PtmPair, PairSite, PairSite)>();
            foreach (var ds in Datasets())
            {
                var pairs = PtmQtlCore.SitePairs(ds).Pairs;
                inMemory.AddRange(pairs.Select(p => (ds.DatasetId, p.Pair, p.A, p.B)));
                var file = PtmQtlEngine.WritePairSites(pairs, Path.Combine(dir, ds.DatasetId + ".tsv"));
                stored.AddRange(PtmQtlEngine.ReadPairSites(file).Select(p => (ds.DatasetId, p.Pair, p.A, p.B)));
            }
            var (a, b) = (PtmQtlCore.Pool("mouse", inMemory).Rows, PtmQtlCore.Pool("mouse", stored).Rows);
            Assert.That(b, Has.Count.EqualTo(a.Count).And.Count.GreaterThan(0));
            for (var i = 0; i < a.Count; i++)
                foreach (var (column, value) in a[i])
                    Assert.That(Cell(b[i][column]), Is.EqualTo(Cell(value)), $"row {i}, {column}");
        }
        finally { Remove(dir); }
    }

    /// <summary>S2c and S2d leave a site out of type A (and site-trait fits), never out of type P; S3b with no
    /// whole-proteome run gives type P only.</summary>
    [Test]
    public void AnExcludedSiteLeavesTypeAAndANonWholeProteomeHasTypePOnly()
    {
        var d1 = Datasets().Single(d => d.DatasetId == "D1");
        var all = PtmQtlCore.SitePairs(d1);
        var (pair, a, _) = all.Pairs.First(p => p.Pair.ResultType == PairResultType.A);
        var excluded = d1 with { Excluded = [new ExcludedSite(a.Protein, a.Position, a.Residue, PtmQtlCore.IdWithMotif(a.Modification), "S2c", "test")] };
        var without = PtmQtlCore.SitePairs(excluded);
        bool Holds((PtmPair Pair, PairSite A, PairSite B) p) => p.A.CanonicalKey == a.CanonicalKey || p.B.CanonicalKey == a.CanonicalKey;
        Assert.Multiple(() =>
        {
            Assert.That(without.Pairs.Where(p => p.Pair.ResultType == PairResultType.A).Any(Holds), Is.False);
            Assert.That(without.Pairs.Count(p => p.Pair.ResultType == PairResultType.P), Is.EqualTo(all.Pairs.Count(p => p.Pair.ResultType == PairResultType.P)));
            Assert.That(without.LeftOut["S2c cells left out"], Is.GreaterThan(0));
        });

        var noWholeProteome = d1 with { OccupancyRuns = new HashSet<string>() };
        var typeP = PtmQtlCore.SitePairs(noWholeProteome);
        Assert.Multiple(() =>
        {
            Assert.That(typeP.Rows.Select(r => r["result_type"]).Distinct(), Is.EqualTo(new[] { "P" }));
            Assert.That(typeP.LeftOut, Does.ContainKey("S3b no whole-proteome run: type A not computed"));
            Assert.That(() => PtmQtlCore.SiteTraits(noWholeProteome, "t", new Dictionary<string, double>(), new Dictionary<string, string>()),
                Throws.TypeOf<RunnerException>().With.Message.Contains("S3b"));
        });
    }

    /// <summary>Six target PSMs for S7's filter tests, in a table named <paramref name="table"/>. a sits at exactly 0.01
    /// on every value; b fails the notch; c has no notch; d is notch-ambiguous; e passes on its notch alone; f fails
    /// everything. PEP q-values: a 0.01, b 2 (MetaMorpheus's "not computed"), c 0.5, d 0.001, e and f none. With
    /// <paramref name="pepsDiffer"/> the targets' PEPs differ, so mzLib's whole-search test finds PEP usable; without, every
    /// target's PEP is 0 (training failed), and a decoy's different PEP must not change that.</summary>
    private static DuckDB.NET.Data.DuckDBConnection FilterPsms(string table, bool pepsDiffer)
    {
        var con = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = $"""
            CREATE TABLE {table} AS SELECT id, 'target' AS target_decoy, CASE WHEN {(pepsDiffer ? "true" : "false")} THEN pep ELSE 0.0 END AS pep,
                   pep_q_value, q_value_notch, q_value, notch_ambiguous
            FROM (VALUES
              ('a', 0.001, 0.01, 0.01, 0.01, false), ('b', 0.002, 2.0, 0.02, 0.005, false), ('c', 0.003, 0.5, NULL, 0.005, NULL),
              ('d', 0.004, 0.001, 0.005, 0.005, true), ('e', 0.005, NULL, 0.005, 0.02, false), ('f', 0.006, NULL, 0.0101, 0.0101, false)
            ) v(id, pep, pep_q_value, q_value_notch, q_value, notch_ambiguous)
            UNION ALL SELECT 'decoy', 'decoy', 0.9, 0.9, 0.9, 0.9, false
            """;
        c.ExecuteNonQuery();
        return con;
    }

    /// <summary>S7 (ptmQtl D82): the search's own filter, as results.txt names it and the ingest reads it
    /// (<c>Identifications.FilterOf</c>), cut as the search cut it: a value of exactly 0.01 passes. Up to 1.1.14 the
    /// filter is the q-value and notch, and a notch-ambiguous PSM fails it; 1.1.15 names its filter.</summary>
    [TestCase(null, null, "1.1.14", false, true, "QValueAndNotch", "a c")]
    [TestCase("q-value", false, "1.1.12", false, true, "QValueAndNotch", "a c")]
    [TestCase("q-value", true, "1.1.15", false, true, "QValue", "a b c d")]
    [TestCase("q-value notch", true, "1.1.15", false, true, "QValueNotch", "a c d e")]
    [TestCase("pep q-value", true, "1.1.15", false, true, "PepQValue (Every)", "a d")]
    [TestCase("pep q-value", true, "1.1.15", true, true, "PepQValue (Some)", "a d e")]
    [TestCase("pep q-value", true, "1.1.15", true, false, "PepQValue (None)", "a c d e")]
    public void S7KeepsWhatTheSearchsOwnFilterKept(string? named, bool? fdrLineNamesFilter, string version, bool pepNotTrained,
        bool pepsDiffer, string filter, string kept)
    {
        using var con = FilterPsms("t", pepsDiffer);
        var results = named is null && fdrLineNamesFilter is null ? null
            : new ResultsTxtFilter(named, fdrLineNamesFilter, pepNotTrained ? new HashSet<string> { "psms" } : new HashSet<string>());
        var chosen = PtmQtlBundle.Filter(con, "t", results, version, "test");
        using var q = con.CreateCommand();
        q.CommandText = $"SELECT string_agg(id, ' ' ORDER BY id) FROM t WHERE target_decoy = 'target' AND {chosen.Sql}";
        Assert.Multiple(() =>
        {
            Assert.That(chosen.ToString(), Is.EqualTo(filter));
            Assert.That(q.ExecuteScalar(), Is.EqualTo(kept));
        });
    }

    /// <summary>S7 (D82) never guesses a filter: a search from 1.1.15 on, or from a release that cannot be read as one,
    /// whose results.txt names none (or that has no results.txt) is refused.</summary>
    [TestCase(false, "1.1.15")]
    [TestCase(true, "1.1.15")]
    [TestCase(false, null)]
    [TestCase(false, "a1b2c3d")]
    public void S7RefusesASearchWhoseFilterIsUnknown(bool hasResultsTxt, string? version)
    {
        using var con = FilterPsms("t", pepsDiffer: true);
        var results = hasResultsTxt ? new ResultsTxtFilter(null, null, new HashSet<string>()) : null;
        Assert.That(() => PtmQtlBundle.Filter(con, "t", results, version, "test"),
            Throws.TypeOf<RunnerException>().With.Message.Contains("ptmQtl S7 (D82)"));
    }

    /// <summary>D40 with S4: a sample's depth is log2 of the target PSMs of all its runs, so its fractions add up; an
    /// injection is its own sample; a sample with no target PSM, and a run with no sample, give no depth.</summary>
    [Test]
    public void DepthIsLog2OfASamplesTargetPsmsOverAllItsRuns()
    {
        var dataset = new PtmQtlDataset("D", "mouse", [], [], new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["f1"] = "s1", ["f2"] = "s1", ["inj1"] = "inj1", ["inj2"] = "inj2", ["empty"] = "empty",
        }, new Dictionary<string, string>(), Enriched: false)
        {
            TargetPsms = new Dictionary<string, long>(StringComparer.Ordinal) { ["f1"] = 1000, ["f2"] = 3000, ["inj1"] = 512, ["inj2"] = 0, ["stray"] = 7 },
        };
        Assert.That(PtmQtlCore.Depth(dataset), Is.EquivalentTo(new Dictionary<string, double>
        {
            ["s1"] = Math.Log2(4000), ["inj1"] = 9.0,
        }));
    }

    /// <summary>D35: a site is pooled only where every dataset holding it gives it one window; otherwise it, and every
    /// pair holding it, is left out and counted.</summary>
    [TestCase("another window", "D35 sites left out: windows disagree")]
    [TestCase(null, "D35 sites left out: no window")]
    public void APooledSiteNeedsOneWindowInEveryDatasetHoldingIt(string? window, string reason)
    {
        var all = Datasets().SelectMany(ds => PtmQtlCore.SitePairs(ds).Pairs.Select(p => (ds.DatasetId, p.Pair, p.A, p.B))).ToList();
        var agreed = PtmQtlCore.Pool("mouse", all);
        var key = (string)agreed.Rows[0]["feature_key_a"]!;
        var one = all.First(x => x.A.CanonicalKey == key || x.B.CanonicalKey == key).DatasetId;
        PairSite Move(PairSite s) => s.CanonicalKey == key ? s with { Window = window } : s;
        var moved = PtmQtlCore.Pool("mouse", all.Select(x => x.DatasetId == one ? (x.DatasetId, x.Pair, Move(x.A), Move(x.B)) : x));
        bool Holds(Row r) => (string)r["feature_key_a"]! == key || (string)r["feature_key_b"]! == key;
        Assert.Multiple(() =>
        {
            Assert.That(agreed.LeftOut.Where(kv => kv.Key.StartsWith("D35 sites", StringComparison.Ordinal)).Sum(kv => kv.Value), Is.Zero);
            Assert.That(moved.Rows.Any(Holds), Is.False);
            Assert.That(moved.Rows, Has.Count.EqualTo(agreed.Rows.Count(r => !Holds(r))).And.Count.GreaterThan(0));
            Assert.That(moved.LeftOut[reason], Is.EqualTo(1));
            Assert.That(moved.LeftOut["D35 dataset pairs left out"], Is.EqualTo(all.Count(x => x.A.CanonicalKey == key || x.B.CanonicalKey == key)));
        });
    }

    [Test]
    public void FractionsOfOneSampleCombineAndAnythingElseIsRefused()
    {
        var map = PtmQtlBundle.RunToSample([("f1", "S1", 1, null), ("f2", "S1", 2, null), ("r3", "S3", null, null)], "test");
        Assert.That(map, Is.EquivalentTo(new Dictionary<string, string> { ["f1"] = "S1", ["f2"] = "S1", ["r3"] = "r3" }));
        Assert.That(() => PtmQtlBundle.RunToSample([("i1", "S8", null, null), ("i2", "S8", null, null)], "test"),
            Throws.TypeOf<RunnerException>().With.Message.Contains("not distinct fractions").And.Message.Contains("ptmQtl S4"));
        Assert.That(() => PtmQtlBundle.RunToSample([("i1", "S8", 1, null), ("i2", "S8", 1, null)], "test"), Throws.TypeOf<RunnerException>());

        // Repeat injections (distinct technical replicates) stay runs; fractions within one injection combine.
        Assert.That(PtmQtlBundle.RunToSample([("i1", "S8", 1, 1), ("i2", "S8", 1, 2)], "test"),
            Is.EquivalentTo(new Dictionary<string, string> { ["i1"] = "i1", ["i2"] = "i2" }));
        Assert.That(PtmQtlBundle.RunToSample([("a1", "S9", 1, 1), ("a2", "S9", 2, 1), ("b1", "S9", 1, 2), ("b2", "S9", 2, 2)], "test"),
            Is.EquivalentTo(new Dictionary<string, string> { ["a1"] = "S9:tr1", ["a2"] = "S9:tr1", ["b1"] = "S9:tr2", ["b2"] = "S9:tr2" }));
        Assert.That(() => PtmQtlBundle.RunToSample([("i1", "S8", 1, 1), ("i2", "S8", 1, null)], "test"),
            Throws.TypeOf<RunnerException>().With.Message.Contains("some runs and not others"));
        Assert.That(() => PtmQtlBundle.RunToSample([("a1", "S9", 1, 1), ("a2", "S9", 1, 1), ("b1", "S9", 1, 2)], "test"),
            Throws.TypeOf<RunnerException>().With.Message.Contains("injection 1"));
    }

    [Test]
    public void ATraitTableMustNameOneTraitAndAgreeWithinASample()
    {
        var dir = Scratch();
        try
        {
            var runToSample = new Dictionary<string, string> { ["f1"] = "S1", ["f2"] = "S1", ["r3"] = "r3" };
            string Write(string body)
            {
                var path = Path.Combine(dir, "t.tsv");
                File.WriteAllText(path, "dataset\trun\treplicate\ttrait_id\tvalue\n" + body);
                return path;
            }
            var (id, trait, replicate, covariates) = PtmQtlEngine.Traits(Write("D\tf1\tA\tage\t3\nD\tf2\tA\tage\t3\nD\tr3\tB\tage\t12\n"), "D", runToSample);
            Assert.That((id, trait["S1"], replicate["r3"], covariates.Count), Is.EqualTo(("age", 3.0, "B", 0)));
            Assert.That(() => PtmQtlEngine.Traits(Write("D\tf1\tA\tage\t3\nD\tf2\tA\tage\t4\n"), "D", runToSample),
                Throws.TypeOf<RunnerException>().With.Message.Contains("disagree"));
            Assert.That(() => PtmQtlEngine.Traits(Write("D\tf1\tA\tage\t3\nD\tr3\tB\tweight\t9\n"), "D", runToSample),
                Throws.TypeOf<RunnerException>().With.Message.Contains("one trait per file"));
            Assert.That(() => PtmQtlEngine.Traits(Write("D\tzz\tA\tage\t3\n"), "D", runToSample),
                Throws.TypeOf<RunnerException>().With.Message.Contains("not a run of the bundle"));
        }
        finally { Remove(dir); }
    }

    /// <summary>DEF-SITE-TRAIT v3: every column past the five is a categorical covariate, per sample; an empty value, or
    /// two runs of one sample that disagree, are refused.</summary>
    [Test]
    public void ATraitTablesFurtherColumnsAreCovariatesPerSample()
    {
        var dir = Scratch();
        try
        {
            var runToSample = new Dictionary<string, string> { ["f1"] = "S1", ["f2"] = "S1", ["r3"] = "r3" };
            string Write(string body)
            {
                var path = Path.Combine(dir, "t.tsv");
                File.WriteAllText(path, "dataset\trun\treplicate\ttrait_id\tvalue\tsex\tacquisition_batch\n" + body);
                return path;
            }
            var (_, _, _, covariates) = PtmQtlEngine.Traits(
                Write("D\tf1\tA\tage\t3\tF\t20160523\nD\tf2\tA\tage\t3\tF\t20160523\nD\tr3\tB\tage\t12\tM\t20160601\n"), "D", runToSample);
            Assert.That(covariates.Keys, Is.EqualTo(new[] { "sex", "acquisition_batch" }));
            Assert.That((covariates["sex"]["S1"], covariates["acquisition_batch"]["r3"]), Is.EqualTo(("F", "20160601")));
            Assert.That(() => PtmQtlEngine.Traits(Write("D\tf1\tA\tage\t3\tF\t1\nD\tf2\tA\tage\t3\tM\t1\n"), "D", runToSample),
                Throws.TypeOf<RunnerException>().With.Message.Contains("disagree on the covariate sex"));
            Assert.That(() => PtmQtlEngine.Traits(Write("D\tf1\tA\tage\t3\t\t1\n"), "D", runToSample),
                Throws.TypeOf<RunnerException>().With.Message.Contains("no value of the covariate sex"));
        }
        finally { Remove(dir); }
    }

    /// <summary>DEF-SITE-TRAIT v3's covariate coding: indicators against the level that sorts first, a one-level
    /// covariate not fitted, a sample with no level refused; and when a covariate cannot be told apart from the trait,
    /// no site is fitted and no term dropped, and every site's row says so (aging's not_estimable:confounded).</summary>
    [Test]
    public void CovariatesAreIndicatorsAndOneConfoundedWithTheTraitFitsNoSite()
    {
        var levels = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sex"] = new Dictionary<string, string> { ["a"] = "M", ["b"] = "F", ["c"] = "M" },
            ["tissue"] = new Dictionary<string, string> { ["a"] = "liver", ["b"] = "liver", ["c"] = "liver" },
        };
        var (values, terms, notFitted) = PtmQtlCore.CovariateDesign(levels, ["a", "b", "c"]);
        Assert.Multiple(() =>
        {
            Assert.That(terms, Is.EqualTo(new[] { "sex=M" }));
            Assert.That(notFitted, Is.EqualTo(new[] { "tissue" }));
            Assert.That((values["a"].Single(), values["b"].Single()), Is.EqualTo((1.0, 0.0)));
        });
        Assert.That(() => PtmQtlCore.CovariateDesign(levels, ["a", "z"]), Throws.TypeOf<RunnerException>().With.Message.Contains("no value of the covariate sex"));

        var d4 = Datasets().Single(d => d.DatasetId == "D4");
        var runs = Tsv("inputs/runs.tsv").Where(r => r["dataset"] == "D4").ToList();
        var trait = runs.ToDictionary(r => r["combine_to"], r => D(r["age_months"]), StringComparer.Ordinal);
        var replicate = runs.ToDictionary(r => r["combine_to"], r => r["replicate"], StringComparer.Ordinal);
        var batch = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            // One batch per age: the batches span every function of the three ages, so age cannot be told apart from them.
            ["acquisition_batch"] = runs.ToDictionary(r => r["combine_to"], r => "b" + r["age_months"], StringComparer.Ordinal),
            ["sex"] = runs.ToDictionary(r => r["combine_to"], r => r["sex"], StringComparer.Ordinal),
        };
        var confounded = PtmQtlCore.SiteTraits(d4, "age_months", trait, replicate, batch);
        var fine = PtmQtlCore.SiteTraits(d4, "age_months", trait, replicate,
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["sex"] = batch["sex"] });
        Assert.Multiple(() =>
        {
            Assert.That(confounded.Confounded, Does.StartWith("the covariate(s) acquisition_batch cannot be told apart from age_months"));
            Assert.That(confounded.Fits.Select(f => f["status"]).Distinct(), Is.EqualTo(new[] { PtmQtlCore.CovariateConfounded }));
            Assert.That(confounded.Rows.Select(r => (r["beta_int"], r["p_int"], r["q"], r["estimable_int"])).Distinct(),
                Is.EqualTo(new[] { ((object?)null, (object?)null, (object?)null, (object?)false) }));
            // Every site the fit would see keeps its row and mzLib's counts of its runs and replicates.
            Assert.That(confounded.Rows.Select(r => r["feature_key"]), Is.EquivalentTo(fine.Rows.Select(r => r["feature_key"])));
            Assert.That(confounded.Rows.Select(r => r["n_detected"]), Is.EqualTo(fine.Rows.Select(r => r["n_detected"])));
            Assert.That(confounded.Fits.Select(f => f["covariates"]).Distinct(), Is.EqualTo(new[] { "acquisition_batch=b24;acquisition_batch=b3;sex=M" }));
            Assert.That(fine.Confounded, Is.Null);
        });
        var together = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sex"] = batch["sex"],
            ["sex_again"] = batch["sex"],
        };
        Assert.That(PtmQtlCore.SiteTraits(d4, "age_months", trait, replicate, together).Confounded,
            Does.StartWith("the covariate(s) sex, sex_again cannot be told apart"));
    }

    /// <summary>site_pairs on two real mouse bundles, then pool_pairs over their artefacts (scratch store; the
    /// bundles are read in place, read-only).</summary>
    [Test, Category("RealData")]
    public void TwoRealBundlesPairAndPool()
    {
        var found = new[] { "PXD035107", "PXD017944" }.Select(RealBundle).ToList();
        if (found.Any(b => b is null)) Assert.Ignore($"no schema {SchemaContract.Version} bundles of PXD035107 and PXD017944 under {RealStore}");
        var bundles = found.Select(b => b!).ToList();
        var store = Scratch();
        try
        {
            var enrichment = Path.Combine(store, "enrichment.tsv");
            File.WriteAllText(enrichment, "dataset\tenriched_ptm\n");
            var cysteine = Path.Combine(store, "cysteine.tsv");
            File.WriteAllText(cysteine, "dataset\tclass\treason\n");
            var inputs = new Dictionary<string, string> { ["enrichment"] = enrichment, ["cysteine"] = cysteine };
            var written = PtmQtlEngine.SitePairs(store, bundles, inputs, StandInInstall).Written;
            Assert.That(written, Has.Count.EqualTo(2));
            foreach (var a in written)
            {
                var summary = (IReadOnlyDictionary<string, object?>)a.Record["engine_summary"]!;
                var gate = (IReadOnlyDictionary<string, object?>)summary["search_gate"]!;
                // Both were 1.1.11 searches when this was written; PXReprise has since re-searched them under 1.1.12
                // (2026-10-30), so the expectation follows the bundle's own release.
                if (Equals(gate["search_engine_version"], "1.1.11"))
                    Assert.That(gate["lacks_met_removed_protein_n_term"], Is.EqualTo(true), "MetaMorpheus 1.1.11 pins mzLib 1.0.591 (S6)");
                Assert.That(a.RowCounts["ptm_pairs"], Is.EqualTo((long)summary["rows_p"]! + (long)summary["rows_a"]!));
                TestContext.Out.WriteLine($"{summary["dataset_id"]}: P {summary["rows_p"]}, A {summary["rows_a"]}");
            }
            Assert.That(PtmQtlEngine.SitePairs(store, bundles, inputs, StandInInstall).AlreadyDone, Has.Count.EqualTo(2), "the same inputs are already done");

            var pooledRun = PtmQtlEngine.PoolPairs(store, bundles, inputs, StandInInstall);
            if (pooledRun.Written.Count == 0)
                Assert.Inconclusive("the two deposits no longer both pool (S3b: whole proteomes only) on this store's bundles; "
                    + "PXD035107's 1.1.12 re-search has no type A rows");
            var pooled = pooledRun.Written.Single();
            var pooledSummary = (IReadOnlyDictionary<string, object?>)pooled.Record["engine_summary"]!;
            Assert.That(pooledSummary["species"], Is.EqualTo("NCBITaxon:10090"));
            TestContext.Out.WriteLine($"meta: P {pooledSummary["rows_p"]}, A {pooledSummary["rows_a"]}");
            var rows = PXReprise.Bundle.ArrowTables.ReadParquet(pooled.TablePath("ptm_pairs")!).Rows;
            Assert.That(rows.All(r => (string)r["scope"]! == "meta:NCBITaxon:10090" && (long)r["n_datasets"]! == 2));

            // Another enrichment list is other artefacts: pooling refuses until site_pairs ran under it.
            File.WriteAllText(enrichment, "dataset\tenriched_ptm\nPXD017944\tphospho\n");
            Assert.That(() => PtmQtlEngine.PoolPairs(store, bundles, inputs, StandInInstall),
                Throws.TypeOf<RunnerException>().With.Message.Contains("Run ptmqtl.site_pairs on them first"));
        }
        finally { Remove(store); }
    }

    /// <summary>The store the RealData tests read: aging's, or a scratch re-ingest named by DATAREPO_TEST_STORE. The
    /// engines read only schema 0.0.16 bundles, which aging's store holds from its 1.4.0 re-ingest on.</summary>
    private static string RealStore => Environment.GetEnvironmentVariable("DATAREPO_TEST_STORE") ?? "F:/aging_data/repo/store";

    /// <summary>The newest current-schema bundle of a deposit in <see cref="RealStore"/>, read in place; null when none.</summary>
    private static PXReprise.Catalog.BundleRef? RealBundle(string deposit)
    {
        var folder = Path.Combine(RealStore, deposit);
        return !Directory.Exists(folder) ? null
            : Directory.EnumerateDirectories(folder).Where(d => File.Exists(Path.Combine(d, BundleWriter.ManifestName)))
                .Select(PXReprise.Catalog.BundleRef.Load).Where(b => b.SchemaVersion == SchemaContract.Version)
                .OrderByDescending(b => b.WrittenUtc, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string RepoRoot() => DataPaths.Root;

    private static void CopyTree(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    /// <summary>The engine's artefact writer, with given rows, as if a ptmqtl engine had run.</summary>
    private static PXReprise.Catalog.ArtefactRef WriteArtefact(string store, string engine, Dictionary<string, string> inputs, Dictionary<string, object?> summary, List<Row> rows) =>
        EngineRunner.WriteArtefact(store, engine, EngineRunner.ArtefactId(engine, new Dictionary<string, string> { ["mzlib"] = "test" }, inputs, PtmQtlCore.PairDefinition),
            new Dictionary<string, object?>
            {
                ["definition_id"] = PtmQtlCore.PairDefinition,
                ["inputs"] = inputs.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["engine_summary"] = summary,
            },
            new Dictionary<string, List<IReadOnlyDictionary<string, object?>>> { ["ptm_pairs"] = rows.Cast<IReadOnlyDictionary<string, object?>>().ToList() });

    /// <summary>The fixture search ingested into a scratch store, a dataset artefact and a pooled one on it (holding the
    /// reference case's rows: the fixture stores no occupancy, so the engine gives it no pairs), and a catalog built.</summary>
    [Test]
    public void TheCatalogLoadsDatasetAndPooledRowsEachUnderItsArtefact()
    {
        var root = Scratch();
        try
        {
            CopyTree(DataPaths.Instance, Path.Combine(root, "data"));
            var manifestPath = Path.Combine(root, "data", "manifest.yaml");
            var store = Path.Combine(root, "store");
            var bundle = PXReprise.Catalog.BundleRef.Load(PXReprise.Ingest.Ingester.Ingest(manifestPath, "PXD999999", store).BundlePath);
            var reference = Datasets();
            var d1 = PtmQtlCore.SitePairs(reference.Single(d => d.DatasetId == "D1"));
            var pooledRows = PtmQtlCore.Pool("mouse", reference.SelectMany(ds => PtmQtlCore.SitePairs(ds).Pairs.Select(p => (ds.DatasetId, p.Pair, p.A, p.B)))).Rows;
            var artefact = WriteArtefact(store, PtmQtlEngine.SitePairsEngine, new() { ["bundle"] = bundle.BundleId, ["enrichment"] = "e1" },
                new() { ["species"] = "mouse" }, d1.Rows);
            var pool = WriteArtefact(store, PtmQtlEngine.PoolPairsEngine, new() { ["site_pairs:PXD999999"] = artefact.ArtefactId, ["enrichment"] = "e1" },
                new() { ["species"] = "mouse" }, pooledRows);
            var expected = (long)d1.Rows.Count;

            var manifest = PXReprise.Ingest.Manifest.Load(manifestPath);
            var bundles = PXReprise.Catalog.CatalogBuilder.SelectBundles(manifest, ["PXD999999"], store);
            var (artefacts, checks) = PXReprise.Catalog.CatalogBuilder.SelectArtefacts(store, bundles);
            Assert.That(artefacts.Select(a => a.ArtefactId), Does.Contain(artefact.ArtefactId));
            var output = Path.Combine(root, "catalog.duckdb");
            PXReprise.Catalog.CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance, artefacts: artefacts, engineChecks: checks);
            using (var con = new DuckDB.NET.Data.DuckDBConnection($"DataSource={output};ACCESS_MODE=READ_ONLY"))
            {
                con.Open();
                long Scalar(string sql) { using var c = con.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
                Assert.That(Scalar($"SELECT count(*) FROM ptm_pairs WHERE artefact_id = '{artefact.ArtefactId}' AND dataset_id = 'PXD999999' AND bundle_id = '{bundle.BundleId}'"), Is.EqualTo(expected));
                Assert.That(Scalar("SELECT count(*) FROM catalog_tables WHERE table_name = 'ptm_pairs' AND kind = 'engine:ptmqtl'"), Is.EqualTo(1));
                Assert.That(Scalar($"SELECT count(*) FROM catalog_engine_artefacts WHERE artefact_id = '{artefact.ArtefactId}'"), Is.EqualTo(1));
                // Pooled rows: no bundle, their own artefact, scope meta:<species>.
                Assert.That(Scalar($"SELECT count(*) FROM ptm_pairs WHERE artefact_id = '{pool.ArtefactId}' AND dataset_id IS NULL AND bundle_id IS NULL AND scope = 'meta:mouse'"),
                    Is.EqualTo(pooledRows.Count).And.GreaterThan(0));
                Assert.That(Scalar("SELECT count(*) FROM catalog_checks WHERE kind = 'row_count' AND name LIKE 'ptmqtl.%' AND ok"), Is.EqualTo(2));
            }

            // A folder build (1.3.5's layout) serves the same rows through views over the artefacts' Parquet, with the
            // copied table's columns in its order, and checks them from the files' footers.
            var folder = PXReprise.Catalog.CatalogBuilder.BuildCatalog(bundles, Path.Combine(root, "catalogs"), instance: manifest.Instance, artefacts: artefacts, engineChecks: checks);
            List<string> Columns(string catalog, string table)
            {
                using var con = new DuckDB.NET.Data.DuckDBConnection($"DataSource={catalog};ACCESS_MODE=READ_ONLY");
                con.Open();
                using var c = con.CreateCommand();
                c.CommandText = $"SELECT column_name || ' ' || data_type FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position";
                using var reader = c.ExecuteReader();
                var names = new List<string>();
                while (reader.Read()) names.Add(reader.GetString(0));
                return names;
            }
            foreach (var table in new[] { "ptm_pairs", "trait_effects" })
                Assert.That(Columns(folder.Path, table), Is.EqualTo(Columns(output, table)), table);
            using (var con = new DuckDB.NET.Data.DuckDBConnection($"DataSource={folder.Path};ACCESS_MODE=READ_ONLY"))
            {
                con.Open();
                long Scalar(string sql) { using var c = con.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
                Assert.That(Scalar("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('ptm_pairs', 'trait_effects') AND table_type = 'VIEW'"), Is.EqualTo(2));
                Assert.That(Scalar($"SELECT count(*) FROM ptm_pairs WHERE artefact_id = '{artefact.ArtefactId}' AND dataset_id = 'PXD999999' AND bundle_id = '{bundle.BundleId}'"), Is.EqualTo(expected));
                Assert.That(Scalar($"SELECT count(*) FROM ptm_pairs WHERE artefact_id = '{pool.ArtefactId}' AND dataset_id IS NULL AND bundle_id IS NULL"), Is.EqualTo(pooledRows.Count));
                Assert.That(Scalar("SELECT count(*) FROM trait_effects"), Is.EqualTo(0));
                Assert.That(Scalar("SELECT count(*) FROM catalog_checks WHERE kind = 'row_count' AND name LIKE 'ptmqtl.%' AND ok"), Is.EqualTo(2));
            }

            // A pooled artefact whose input site_pairs artefact is not in the catalog is left out, not loaded.
            WriteArtefact(store, PtmQtlEngine.PoolPairsEngine, new() { ["site_pairs:PXD999999"] = "0000000000000000", ["enrichment"] = "e1" },
                new() { ["species"] = "rat" }, pooledRows);
            Assert.That(PXReprise.Catalog.CatalogBuilder.SelectArtefacts(store, bundles).Artefacts.Count(a => a.Engine == PtmQtlEngine.PoolPairsEngine), Is.EqualTo(1));

            // D96: a second mouse pool on the same member does not nest in the first (equal members), so two candidates
            // stay, and the catalog refuses them rather than picking one.
            WriteArtefact(store, PtmQtlEngine.PoolPairsEngine, new() { ["site_pairs:PXD999999"] = artefact.ArtefactId, ["enrichment"] = "e1", ["capture_runs"] = "c2" },
                new() { ["species"] = "mouse" }, pooledRows);
            Assert.That(() => PXReprise.Catalog.CatalogBuilder.SelectArtefacts(store, bundles),
                Throws.TypeOf<PXReprise.Bundle.CatalogException>().With.Message.Contains("2 pooled artefacts for mouse rest on this catalog's datasets"));

            // A second enrichment list for the same bundle: two artefacts for one slot are refused, not picked.
            WriteArtefact(store, PtmQtlEngine.SitePairsEngine, new() { ["bundle"] = bundle.BundleId, ["enrichment"] = "e2" }, new() { ["species"] = "mouse" }, d1.Rows);
            Assert.That(() => PXReprise.Catalog.CatalogBuilder.SelectArtefacts(store, bundles),
                Throws.TypeOf<PXReprise.Bundle.CatalogException>().With.Message.Contains("another enrichment list, or other capture or cysteine lists"));
        }
        finally { Remove(root); }
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
