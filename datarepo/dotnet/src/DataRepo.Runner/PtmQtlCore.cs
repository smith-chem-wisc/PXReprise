using DataRepo.Bundle;
using Quantification.PtmQtl;

namespace DataRepo.Runner;

/// <summary>One stored occupancy cell, as <c>ptm_stoichiometry</c> joined to <c>ptm_sites</c> gives it (ptmQtl S1).</summary>
/// <param name="Modification">The IdWithMotif, without category, as the catalog names it.</param>
/// <param name="SiteType">The site's <c>site_type</c>: <c>protein_n_term</c> marks a protein N-terminal site (S5).</param>
/// <param name="State">The stored state: <c>quantified</c>, <c>floor</c>, <c>count_only</c>, <c>not_detected</c> or
/// <c>intensity_unassigned</c>.</param>
/// <param name="Fraction">The stored fraction (4 dp), read only for <c>quantified</c>.</param>
public sealed record StoredOccupancyCell(string Run, string Protein, int Position, char Residue, string SiteType, string Modification,
    string State, double Fraction, double IntensityModified, double IntensityTotal, bool UnmodifiedQuantified);

/// <summary>Everything one dataset gives the ptmQtl engines, already read from its bundle.</summary>
/// <param name="RunToSample">Every run's sample: a fraction maps to its sample, an injection to itself (S4).</param>
/// <param name="Unimod">IdWithMotif -> UNIMOD accession, for the cross-dataset key (S5). An IdWithMotif without one keys by itself.</param>
/// <param name="Enriched">The deposit captures a modified form (S3): type P only.</param>
/// <param name="CategoryNames">Every MetaMorpheus name the dataset's peptidoforms carry, from which each IdWithMotif's
/// category is read (S2). Null means the observations' own names, as in ptmQtl's reference case.</param>
public sealed record PtmQtlDataset(string DatasetId, string Species, IReadOnlyList<PeptidoformObservation> Observations,
    IReadOnlyList<StoredOccupancyCell> Occupancy, IReadOnlyDictionary<string, string> RunToSample,
    IReadOnlyDictionary<string, string> Unimod, bool Enriched, IReadOnlyList<string>? CategoryNames = null)
{
    /// <summary>Each IdWithMotif's category (S2).</summary>
    public Dictionary<string, string> Categories() => PtmQtlCore.Categories(CategoryNames ?? Observations.Select(o => o.FullSequence));

    /// <summary>Each (accession, position)'s sequence window (S5, D35), as the bundle stores it; absent or null when
    /// it has none.</summary>
    public IReadOnlyDictionary<(string, int), string?> Windows { get; init; } = new Dictionary<(string, int), string?>();

    /// <summary>Each search's quantified-PSM tier (S7), for the run record.</summary>
    public IReadOnlyDictionary<string, string> Tiers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The runs (and pooled samples' keys) whose occupancy enters type A and site-trait fits (S3, S3b): the
    /// whole-proteome ones. Null means every run, as in ptmQtl's reference case.</summary>
    public IReadOnlySet<string>? OccupancyRuns { get; init; }

    /// <summary>Each run's sample type (S3b), for the run record.</summary>
    public IReadOnlyDictionary<string, string> RunTypes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Sites S2c and S2d leave out of every occupancy-based result.</summary>
    public IReadOnlyList<ExcludedSite> Excluded { get; init; } = [];

    /// <summary>The observations type P reads, when S2d (a) leaves some out (an NEM deposit's Cys-containing
    /// peptidoforms); null means <see cref="Observations"/>.</summary>
    public IReadOnlyList<PeptidoformObservation>? PhysicalObservations { get; init; }

    /// <summary>Whether this dataset's rows may be pooled: every run a whole proteome (S3b).</summary>
    public bool Poolable { get; init; } = true;
}

/// <summary>One site an artefact rule leaves out (S2c, S2d), with the rule and its reason, as ptmQtl's
/// <c>excluded_sites.tsv</c> writes them.</summary>
/// <param name="Modification">The IdWithMotif, or null for every modification at that place (S2d (a)).</param>
public sealed record ExcludedSite(string Protein, int Position, char? Residue, string? Modification, string Rule, string Reason);

/// <summary>One site of a stored pair, with what pooling needs to rebuild the mzLib site and key it (S5).</summary>
/// <param name="Window">The site's sequence window in its dataset (D35), or null when it has none: such a site is never
/// pooled.</param>
public sealed record PairSite(string Protein, int Position, char Residue, string Modification, bool ProteinNTerm, string CanonicalKey, string? Window = null)
{
    public ModificationSite ToSite() => new(Protein, Position, Residue, Modification);
}

/// <summary>One species' pooled rows, and the sites left out of them (D35), counted by reason.</summary>
public sealed record PoolResult(List<Row> Rows, Dictionary<string, long> LeftOut);

/// <summary>One dataset's pairs: the <c>ptm_pairs</c> rows, and the mzLib pairs behind them, for pooling.</summary>
public sealed record SitePairsResult(List<Row> Rows, List<(PtmPair Pair, PairSite A, PairSite B)> Pairs, Dictionary<string, long> LeftOut);

/// <summary>One dataset's site-trait fits: the <c>trait_effects</c> rows and the side fields (our ptmQtl 031).</summary>
public sealed record SiteTraitsResult(List<Row> Rows, List<Row> Fits, Dictionary<string, long> LeftOut);

/// <summary>ptmQtl's definitions, applied through mzLib only: <c>ptmQtl:DEF-PTM-PAIR v2</c> and
/// <c>ptmQtl:DEF-SITE-TRAIT v1</c> (<c>ptmQtl/design/DEFINITIONS.md</c>).</summary>
/// <remarks>
/// <para>This computes nothing the definitions do not say. Every number comes from mzLib's
/// <see cref="SiteOccupancyCalculator"/>, <see cref="PtmPairEngine"/>, <see cref="GlobalPairEngine"/> and
/// <see cref="SiteTraitEffects"/>. What is here is the shared rules: which modifications (S2), which deposits (S3),
/// runs and samples (S4), and site keys (S5). It also shapes the rows to the column contract.</para>
/// <para>It follows ptmQtl's reference program (<c>tools/ReferenceCase</c>) step for step, and its output must match
/// <c>results/reference_case_v1/expected</c> (G37).</para>
/// </remarks>
public static class PtmQtlCore
{
    public const string PairDefinition = "ptmQtl:DEF-PTM-PAIR v2";
    public const string TraitDefinition = "ptmQtl:DEF-SITE-TRAIT v1";

    /// <summary>S2: the mzLib categories whose modifications are biological (D26, D27; our ptmQtl 027, 029).</summary>
    public static readonly IReadOnlyList<string> BiologicalCategories = ["Common Biological", "UniProt", "Trypsin Digested", "AspN Digested"];

    /// <summary>S2: modifications of those categories that are not biological (ptmQtl D27).</summary>
    public static readonly IReadOnlyList<string> Excluded = ["Common Biological:Hydroxylation on N", "Common Biological:Formylation on K"];

    /// <summary>The fraction of a dataset's runs in which a site must be quantified to enter type A.</summary>
    public const double MinQuantifiedFraction = 0.7;

    /// <summary>The <c>Category:IdWithMotif</c> names in a MetaMorpheus full sequence, outermost brackets only.</summary>
    /// <remarks>Scanned by bracket depth, not by a pattern: a name may itself hold brackets, as the metal adducts do
    /// (<c>[Metal:Cu[I] on D]</c>), and a flat pattern silently misses those.</remarks>
    public static IEnumerable<string> ModificationNames(string fullSequence)
    {
        int depth = 0, start = -1;
        for (var i = 0; i < fullSequence.Length; i++)
        {
            if (fullSequence[i] == '[')
            {
                if (depth++ == 0) start = i + 1;
            }
            else if (fullSequence[i] == ']' && depth > 0 && --depth == 0)
                yield return fullSequence[start..i];
        }
    }

    /// <summary>S2, on a <c>Category:IdWithMotif</c> name.</summary>
    /// <remarks>An <c>AspN Digested</c> site on D is refused: the human SUMO entries target D, but they are lysine
    /// remnants (mzLib #1431, fixed by #1432), until the data are re-searched.</remarks>
    public static bool IsBiological(string modification)
    {
        var colon = modification.IndexOf(':');
        if (colon <= 0) return false;
        var category = modification[..colon];
        if (!BiologicalCategories.Contains(category) || Excluded.Contains(modification)) return false;
        return !(category == "AspN Digested" && modification.EndsWith(" on D", StringComparison.Ordinal));
    }

    /// <summary>The IdWithMotif of a <c>Category:IdWithMotif</c> name.</summary>
    public static string IdWithMotif(string modification) => modification[(modification.IndexOf(':') + 1)..];

    /// <summary>Each IdWithMotif's category, from the MetaMorpheus names of the dataset's peptidoforms (S2).</summary>
    /// <exception cref="RunnerException">One IdWithMotif carries two categories in one dataset: its class is undecidable.</exception>
    public static Dictionary<string, string> Categories(IEnumerable<PeptidoformObservation> observations) =>
        Categories(observations.Select(o => o.FullSequence));

    /// <summary>Each IdWithMotif's category, from MetaMorpheus full sequences (S2).</summary>
    /// <exception cref="RunnerException">One IdWithMotif carries two categories: its class is undecidable.</exception>
    public static Dictionary<string, string> Categories(IEnumerable<string> fullSequences)
    {
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sequence in fullSequences)
            foreach (var name in ModificationNames(sequence))
            {
                var colon = name.IndexOf(':');
                if (colon <= 0) continue;
                var (category, idm) = (name[..colon], name[(colon + 1)..]);
                if (!categories.TryAdd(idm, category) && categories[idm] != category)
                    throw new RunnerException(
                        $"the modification '{idm}' is written with two categories ('{categories[idm]}', '{category}') in one dataset, "
                        + "so whether it is biological cannot be decided (ptmQtl S2).");
            }
        return categories;
    }

    /// <summary>The protein N-terminal sites, (accession, position), from the stored cells' <c>site_type</c> (S5).</summary>
    public static HashSet<(string, int)> ProteinNTerms(PtmQtlDataset dataset) =>
        dataset.Occupancy.Where(c => c.SiteType == "protein_n_term").Select(c => (c.Protein, c.Position)).ToHashSet();

    /// <summary>S5: <c>accession:&lt;residue&gt;&lt;position&gt;:&lt;modification&gt;</c>, or
    /// <c>accession:@protein_n_term:&lt;modification&gt;</c> for a protein N-terminal site. The modification is the
    /// <c>Category:IdWithMotif</c> within a dataset, and the UNIMOD accession (else the IdWithMotif) across datasets.</summary>
    public static string Key(ModificationSite site, bool canonical, ISet<(string, int)> nterms, IReadOnlyDictionary<string, string> unimod)
    {
        var idm = IdWithMotif(site.Modification);
        var modification = canonical ? unimod.GetValueOrDefault(idm, idm) : site.Modification;
        return nterms.Contains((site.ProteinAccession, site.Position))
            ? $"{site.ProteinAccession}:@protein_n_term:{modification}"
            : $"{site.ProteinAccession}:{site.Residue}{site.Position}:{modification}";
    }

    private static OccupancyState? StateOf(string state) => state switch
    {
        "quantified" => OccupancyState.Quantified,
        "floor" => OccupancyState.Floor,
        "count_only" => OccupancyState.CountOnly,
        "not_detected" => OccupancyState.NotDetected,
        "intensity_unassigned" => null,
        _ => throw new RunnerException($"occupancy state '{state}' is not one ptmQtl S1 knows"),
    };

    private static void Count(Dictionary<string, long> counts, string reason) => counts[reason] = counts.GetValueOrDefault(reason) + 1;

    /// <summary>S1 + S2 (with S2c, S2d) + S3b + S4: the stored cells as mzLib cells, biological and not left out by an
    /// artefact rule, of whole-proteome runs only, combined per sample.</summary>
    public static List<SiteRunOccupancy> Occupancy(PtmQtlDataset dataset, Dictionary<string, string> categories, Dictionary<string, long> leftOut)
    {
        var anyModification = dataset.Excluded.Where(e => e.Modification is null).Select(e => (e.Protein, e.Position)).ToHashSet();
        var oneModification = dataset.Excluded.Where(e => e.Modification is not null).Select(e => (e.Protein, e.Position, e.Modification!)).ToHashSet();
        var ruleOf = dataset.Excluded.GroupBy(e => (e.Protein, e.Position, e.Modification)).ToDictionary(g => g.Key, g => g.First().Rule);
        var cells = new List<SiteRunOccupancy>();
        foreach (var c in dataset.Occupancy)
        {
            if (dataset.OccupancyRuns is { } runs && !runs.Contains(c.Run))
            {
                Count(leftOut, "S3b run is not a whole proteome");
                continue;
            }
            // Matched on accession, position and IdWithMotif, as ptmQtl's runner does (their 044 item 6).
            if (oneModification.Contains((c.Protein, c.Position, c.Modification)) || anyModification.Contains((c.Protein, c.Position)))
            {
                var rule = ruleOf.GetValueOrDefault((c.Protein, c.Position, c.Modification)) ?? ruleOf[(c.Protein, c.Position, null)];
                Count(leftOut, $"{rule} cells left out");
                continue;
            }
            if (StateOf(c.State) is not OccupancyState state)
            {
                Count(leftOut, "S1 intensity_unassigned");
                continue;
            }
            if (!categories.TryGetValue(c.Modification, out var category))
            {
                Count(leftOut, "S2 no peptidoform names the modification's category");
                continue;
            }
            var name = $"{category}:{c.Modification}";
            if (!IsBiological(name))
            {
                Count(leftOut, "S2 not biological");
                continue;
            }
            var site = new ModificationSite(c.Protein, c.Position, c.Residue, name);
            cells.Add(new SiteRunOccupancy(site, c.Run, state, c.IntensityModified, c.IntensityTotal, c.UnmodifiedQuantified)
            {
                ReportedFraction = state == OccupancyState.Quantified ? c.Fraction : null,
            });
        }
        return SiteOccupancyCalculator.CombineRuns(MergeDuplicateSites(cells, leftOut), dataset.RunToSample).ToList();
    }

    /// <summary>S5 (ptmQtl D70): one physical site under several accessions is one site within a dataset. MetaMorpheus
    /// writes a site on a peptide shared by isoforms or paralogs once per accession with identical cells; counted apart,
    /// it pairs with itself at rho = 1 and each of its pairs is counted once per accession.</summary>
    /// <remarks>ptmQtl's rule as their runner applies it (<c>tools/PtmQtlRunner/Program.cs</c>, <c>d9a7ee6</c>): sites with
    /// the same IdWithMotif and identical cells in every run (state, modified and covering intensity, fraction, compared
    /// as their round-trip text), at least one with a modified intensity, are one site, and the ordinal-first mzLib key
    /// stands for them. A site with no modified intensity is never merged. Per run, before runs combine into samples.</remarks>
    public static List<SiteRunOccupancy> MergeDuplicateSites(List<SiteRunOccupancy> cells, Dictionary<string, long> leftOut)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => double.IsNaN(v) ? "NaN" : v.ToString("R", inv);
        var duplicateOf = cells.GroupBy(c => c.Site)
            .Where(g => g.Any(c => double.IsFinite(c.ModifiedIntensity) && c.ModifiedIntensity > 0))
            .GroupBy(g => IdWithMotif(g.Key.Modification) + "|" + string.Join(";", g.OrderBy(c => c.Run, StringComparer.Ordinal)
                .Select(c => $"{c.Run},{c.State},{F(c.ModifiedIntensity)},{F(c.CoveringIntensity)},{c.ReportedFraction?.ToString("R", inv) ?? ""}")))
            .Where(g => g.Count() > 1)
            .SelectMany(g =>
            {
                var keep = g.Select(s => s.Key).OrderBy(s => s.Key, StringComparer.Ordinal).First();
                return g.Select(s => s.Key).Where(s => s != keep).Select(s => (Dup: s, Keep: keep));
            })
            .ToDictionary(t => t.Dup, t => t.Keep);
        if (duplicateOf.Count == 0) return cells;
        leftOut["S5 (D70) sites merged into a site with identical cells under another accession"] = duplicateOf.Count;
        return cells.Where(c => !duplicateOf.ContainsKey(c.Site)).ToList();
    }

    private static PairSite Site(ModificationSite s, ISet<(string, int)> nterms, PtmQtlDataset dataset) =>
        new(s.ProteinAccession, s.Position, s.Residue, s.Modification, nterms.Contains((s.ProteinAccession, s.Position)), Key(s, true, nterms, dataset.Unimod),
            dataset.Windows.GetValueOrDefault((s.ProteinAccession, s.Position)));

    private static string Family(bool same) => $"A.{(same ? "intra" : "inter")}_protein.biological x biological";

    private static double? Num(double v) => double.IsFinite(v) ? v : null;

    /// <summary>A <c>ptm_pairs</c> row, columns as <c>DEF-PTM-PAIR v2</c> gives them.</summary>
    private static Row PairRow(bool isA, string scope, string keyA, string keyB, string proteinA, string proteinB, bool overlapping,
        double value, long? n, double p, double q, IReadOnlyList<string>? datasets = null, long? agreeing = null, bool meta = false)
    {
        string? sign = isA && double.IsFinite(value) ? (value >= 0 ? "+" : "-") : null;
        return new Row
        {
            ["result_type"] = isA ? "A" : "P",
            ["scope"] = scope,
            ["feature_type"] = "ptm_site_pair",
            ["datasets"] = datasets?.Cast<object?>().ToList(),
            ["n_datasets"] = datasets is null ? null : (long?)datasets.Count,
            ["n_datasets_agreeing"] = agreeing,
            ["feature_key_a"] = keyA,
            ["protein_accessions_a"] = new List<object?> { proteinA },
            ["feature_key_b"] = keyB,
            ["protein_accessions_b"] = new List<object?> { proteinB },
            ["same_protein"] = proteinA == proteinB,
            ["flags"] = overlapping || !isA ? new List<object?> { "overlapping" } : new List<object?>(),
            ["class_pair"] = "biological x biological",
            ["trait_id"] = null,
            ["stratum"] = null,
            ["sign"] = sign,
            ["statistic"] = isA ? (meta ? "stouffer_z" : "spearman_rho") : "median_co_occupancy",
            ["value"] = Num(value),
            ["n"] = n,
            ["p"] = isA ? Num(p) : null,
            ["q"] = isA ? Num(q) : null,
            ["fdr_family"] = isA ? Family(proteinA == proteinB) : null,
            ["definition_id"] = PairDefinition,
        };
    }

    /// <summary>Whether a dataset has occupancy-based results at all: not a PTM-capture deposit (S3), and some
    /// whole-proteome run (S3b).</summary>
    public static bool HasOccupancyResults(PtmQtlDataset dataset) => !dataset.Enriched && dataset.OccupancyRuns is not { Count: 0 };

    /// <summary><c>ptmqtl.site_pairs</c> on one dataset: type P always, from every run (S3, S3b); type A from its
    /// whole-proteome runs, unless the deposit captures a modified form (S3).</summary>
    /// <remarks>Type P leaves out only what S2d (a) says of it: an NEM deposit's Cys-containing peptidoforms. S2c's and
    /// S2d's site exclusions reach the occupancy-based results, type A and site-trait fits.</remarks>
    public static SitePairsResult SitePairs(PtmQtlDataset dataset)
    {
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        var nterms = ProteinNTerms(dataset);
        var physical = SiteOccupancyCalculator.CombineObservations(dataset.PhysicalObservations ?? dataset.Observations, dataset.RunToSample);
        if (dataset.PhysicalObservations is not null)
            leftOut["S2d (a) observations left out of type P: Cys-containing peptidoform"] = dataset.Observations.Count - dataset.PhysicalObservations.Count;
        var produced = PtmPairEngine.Physical(physical, IsBiological).Select(p => (p, isA: false)).ToList();
        if (HasOccupancyResults(dataset))
        {
            // The observation side of type A (which sites share a peptide) reads the runs its occupancy reads.
            var observations = SiteOccupancyCalculator.CombineObservations(
                dataset.OccupancyRuns is { } runs ? dataset.Observations.Where(o => runs.Contains(o.Run)).ToList() : dataset.Observations, dataset.RunToSample);
            var occupancy = Occupancy(dataset, dataset.Categories(), leftOut);
            produced.AddRange(PtmPairEngine.CoVarying(occupancy, observations, minQuantifiedFraction: MinQuantifiedFraction, excludeCeiling: true)
                .Select(p => (p, isA: true)));
        }
        else if (dataset.Enriched) leftOut["S3 enriched deposit: type A not computed"] = 1;
        else leftOut["S3b no whole-proteome run: type A not computed"] = 1;

        var rows = new List<Row>();
        var pairs = new List<(PtmPair, PairSite, PairSite)>();
        foreach (var (p, isA) in produced)
        {
            var (a, b) = (Site(p.SiteA, nterms, dataset), Site(p.SiteB, nterms, dataset));
            var (ka, kb) = (Key(p.SiteA, false, nterms, dataset.Unimod), Key(p.SiteB, false, nterms, dataset.Unimod));
            if (string.CompareOrdinal(ka, kb) > 0) (ka, kb) = (kb, ka);
            rows.Add(PairRow(isA, dataset.DatasetId, ka, kb, p.SiteA.ProteinAccession, p.SiteB.ProteinAccession, p.Overlapping,
                p.Statistic, p.N, isA ? p.PValue : double.NaN, isA ? p.Q : double.NaN));
            pairs.Add((p, a, b));
        }
        return new SitePairsResult(rows, pairs, leftOut);
    }

    /// <summary><c>ptmqtl.pool_pairs</c>: one species' dataset pairs matched on the cross-dataset key, in two or more datasets.</summary>
    /// <param name="pairs">Each dataset's pairs, with their sites as the dataset's artefact stored them.</param>
    /// <remarks>D35: a site joins only where its sequence window agrees. When the datasets holding a site (by its
    /// cross-dataset key) disagree on its window, or any of them has none, the site is left out of the pooled rows and
    /// counted, and so is every pair holding it. Datasets are never refused for their search databases.</remarks>
    public static PoolResult Pool(string species, IEnumerable<(string Scope, PtmPair Pair, PairSite A, PairSite B)> pairs)
    {
        var keys = new Dictionary<ModificationSite, string>();
        var windows = new Dictionary<string, HashSet<string?>>(StringComparer.Ordinal);
        var given = new List<(string, PtmPair, PairSite, PairSite)>();
        foreach (var (scope, pair, a, b) in pairs)
        {
            foreach (var s in new[] { a, b })
            {
                if (keys.TryGetValue(s.ToSite(), out var known) && known != s.CanonicalKey)
                    throw new RunnerException($"the site {s.ToSite().Key} has two cross-dataset keys ({known}, {s.CanonicalKey})");
                keys[s.ToSite()] = s.CanonicalKey;
                if (!windows.TryGetValue(s.CanonicalKey, out var seen)) windows[s.CanonicalKey] = seen = [];
                seen.Add(s.Window);
            }
            given.Add((scope, pair, a, b));
        }
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["D35 sites left out: no window"] = windows.Count(w => w.Value.Contains(null)),
            ["D35 sites left out: windows disagree"] = windows.Count(w => !w.Value.Contains(null) && w.Value.Count > 1),
        };
        bool Joins(PairSite s) => windows[s.CanonicalKey] is { Count: 1 } w && !w.Contains(null);
        var input = given.Where(g => Joins(g.Item3) && Joins(g.Item4)).Select(g => (g.Item1, g.Item2)).ToList();
        leftOut["D35 dataset pairs left out"] = given.Count - input.Count;
        var rows = new List<Row>();
        foreach (var g in GlobalPairEngine.Combine(input, s => keys[s], minScopes: 2))
        {
            var isA = g.ResultType == PairResultType.A;
            rows.Add(PairRow(isA, $"meta:{species}", g.SiteA, g.SiteB, g.ProteinA, g.ProteinB, false,
                isA ? g.CombinedZ : g.Statistic, null, isA ? g.PValue : double.NaN, isA ? g.Q : double.NaN,
                g.Scopes, g.ScopesAgreeing, meta: true));
        }
        return new PoolResult(rows, leftOut);
    }

    /// <summary><c>ptmqtl.site_traits</c> on one dataset and one trait (<c>DEF-SITE-TRAIT v1</c>).</summary>
    /// <param name="trait">The trait per sample (after S4).</param>
    /// <param name="replicate">The biological replicate per sample: the animal, never the injection.</param>
    /// <exception cref="RunnerException">The deposit is enriched (S3): no site-trait fit is computed on it.</exception>
    public static SiteTraitsResult SiteTraits(PtmQtlDataset dataset, string traitId, IReadOnlyDictionary<string, double> trait,
        IReadOnlyDictionary<string, string> replicate)
    {
        if (dataset.Enriched)
            throw new RunnerException($"{dataset.DatasetId} captures a modified form (ptmQtl S3): no site-trait fit is computed on it.");
        if (!HasOccupancyResults(dataset))
            throw new RunnerException($"{dataset.DatasetId} has no whole-proteome run (ptmQtl S3b): no site-trait fit is computed on it.");
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        var nterms = ProteinNTerms(dataset);
        var occupancy = Occupancy(dataset, dataset.Categories(), leftOut);
        var rows = new List<Row>();
        var fits = new List<Row>();
        foreach (var e in SiteTraitEffects.Fit(occupancy, trait, replicate, options: new SiteTraitOptions()))
        {
            var key = Key(e.Site, false, nterms, dataset.Unimod);
            rows.Add(new Row
            {
                ["feature_type"] = "ptm_site",
                ["feature_key"] = key,
                ["feature_key_unimod"] = Key(e.Site, true, nterms, dataset.Unimod),
                ["protein_accessions"] = new List<object?> { e.Site.ProteinAccession },
                ["trait_id"] = traitId,
                ["response"] = "logit_occupancy",
                ["scope"] = dataset.DatasetId,
                ["beta_detect"] = null, ["se_detect"] = null, ["p_detect"] = null,
                ["beta_int"] = Num(e.Effect), ["se_int"] = Num(e.StandardError), ["p_int"] = Num(e.PValue),
                ["p_combined"] = Num(e.PValue),
                ["q"] = Num(e.Q),
                ["fdr_family"] = "site_trait.intensity",
                ["n_detected"] = (long)e.Runs,
                ["n_absent_protein_present"] = null,
                ["n_protein_present"] = null,
                ["estimable_detect"] = false,
                ["estimable_int"] = e.Status == "Fitted",
                ["mbr_dependent"] = false,
                ["mod_class"] = "biological",
                ["ambiguity_level"] = "1",
                ["definition_id"] = TraitDefinition,
            });
            fits.Add(new Row
            {
                ["scope"] = dataset.DatasetId,
                ["trait_id"] = traitId,
                ["feature_key"] = key,
                ["status"] = e.Status,
                ["df"] = Num(e.DegreesOfFreedom),
                ["replicates"] = (long)e.Replicates,
                ["median_fraction"] = Num(e.MedianFraction),
                ["covariate_effects"] = e.CovariateEffects.Select(v => (object?)Num(v)).ToList(),
                ["replicate_variance"] = Num(e.ReplicateVariance),
                ["residual_variance"] = Num(e.ResidualVariance),
            });
        }
        return new SiteTraitsResult(rows, fits, leftOut);
    }
}
