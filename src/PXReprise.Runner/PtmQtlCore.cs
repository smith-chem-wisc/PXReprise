using PXReprise.Bundle;
using Quantification.PtmQtl;

namespace PXReprise.Runner;

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

    /// <summary>The search's own identification filter (S7, D82), for the run record; null where the dataset was not read
    /// from a bundle, as in ptmQtl's reference case.</summary>
    public string? IdentificationFilter { get; init; }

    /// <summary>Each run's target PSMs that S7's filter keeps, at any ambiguity level and on any protein: what its depth
    /// counts (D40, <see cref="PtmQtlCore.Depth"/>). A run with none is absent.</summary>
    public IReadOnlyDictionary<string, long> TargetPsms { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The MetaMorpheus release of the search (<c>datasets.search_engine_version</c>). It says where type A's
    /// covered-zero cells come from (S1): MetaMorpheus writes them from 1.1.13 on, and up to 1.1.12 the engine reads them
    /// from the observations' coverage.</summary>
    public string? SearchEngineVersion { get; init; }

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
public sealed record SitePairsResult(List<Row> Rows, List<(PtmPair Pair, PairSite A, PairSite B)> Pairs, Dictionary<string, long> LeftOut)
{
    /// <summary>D78's depth null for the dataset's type A, or null where type A was not computed (S3, S3b).</summary>
    public DepthNullResult? DepthNull { get; init; }
}

/// <summary>D78: a deposit's type A calls against those of a null that keeps each site's relation to depth and breaks
/// every link between sites (<see cref="PtmQtlCore.DepthNull"/>).</summary>
/// <param name="RealCalls">Type A pairs tested, not overlapping, at q &lt;= <see cref="PtmQtlCore.CallQ"/>.</param>
/// <param name="NullCalls">The same count in each null replicate.</param>
/// <param name="Share">Null calls per replicate over real calls; NaN when there is no real call.</param>
/// <param name="DepthLike">Share at least <see cref="PtmQtlCore.DepthLikeShare"/>.</param>
public sealed record DepthNullResult(long RealCalls, IReadOnlyList<long> NullCalls, double Share, bool DepthLike);

/// <summary>One dataset's site-trait fits: the <c>trait_effects</c> rows and the side fields.</summary>
public sealed record SiteTraitsResult(List<Row> Rows, List<Row> Fits, Dictionary<string, long> LeftOut)
{
    /// <summary>The covariate terms fitted, in the order of each fit's <c>covariate_effects</c>.</summary>
    public IReadOnlyList<string> Covariates { get; init; } = [];

    /// <summary>The covariates given with one level among the samples fitted, so not fitted.</summary>
    public IReadOnlyList<string> CovariatesNotFitted { get; init; } = [];

    /// <summary>The trait's Spearman correlation with depth over the samples fitted: evidence, never a filter
    /// (DEF-SITE-TRAIT v3).</summary>
    public StatisticalModels.SpearmanResult? TraitDepth { get; init; }
}

/// <summary>ptmQtl's definitions, applied through mzLib only: <c>ptmQtl:DEF-PTM-PAIR v3</c> and
/// <c>ptmQtl:DEF-SITE-TRAIT v3</c> (<c>ptmQtl/design/DEFINITIONS.md</c>).</summary>
/// <remarks>
/// <para>This computes nothing the definitions do not say. Every number comes from mzLib's
/// <see cref="SiteOccupancyCalculator"/>, <see cref="PtmPairEngine"/>, <see cref="GlobalPairEngine"/> and
/// <see cref="SiteTraitEffects"/>. What is here is the shared rules: which modifications (S2), which deposits (S3),
/// runs and samples (S4), and site keys (S5). It also shapes the rows to the column contract.</para>
/// <para>It follows ptmQtl's reference program (<c>tools/ptmqtl-reference</c>) step for step, and its output must match
/// <c>tests/PXReprise.Data.Tests/Fixtures/ptmqtl-reference-v4/expected</c> (G37, G64).</para>
/// </remarks>
public static class PtmQtlCore
{
    public const string PairDefinition = "ptmQtl:DEF-PTM-PAIR v3";

    /// <summary>D78: the q-value at or below which a type A pair is a call, for the depth null.</summary>
    public const double CallQ = 0.1;

    /// <summary>D78: the null's replicates, its block of samples adjacent in depth, and its seed (deterministic).</summary>
    public const int NullReplicates = 3, NullBlock = 3, NullSeed = 20261110;

    /// <summary>D78: a deposit is depth-like when the null's calls per replicate reach this share of its real calls.
    /// ptmQtl's threshold, stated as such; not a rule from data.</summary>
    public const double DepthLikeShare = 0.10;
    public const string TraitDefinition = "ptmQtl:DEF-SITE-TRAIT v3";

    /// <summary>S2: the mzLib categories whose modifications are biological (D26, D27).</summary>
    public static readonly IReadOnlyList<string> BiologicalCategories = ["Common Biological", "UniProt", "Trypsin Digested", "AspN Digested"];

    /// <summary>S2: modifications of those categories that are not biological.</summary>
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
        // MetaMorpheus 1.1.13 on (#2889): covered, the modified form never seen. A bound below (S1, D37).
        "covered_zero" => OccupancyState.NotDetected,
        "intensity_unassigned" => null,
        _ => throw new RunnerException($"occupancy state '{state}' is not one ptmQtl S1 knows"),
    };

    private static void Count(Dictionary<string, long> counts, string reason) => counts[reason] = counts.GetValueOrDefault(reason) + 1;

    /// <summary>D40: each sample's depth, log2 of the target PSMs that S7's filter keeps in all of its runs (S4: a
    /// sample's fractions are summed; an injection is its own sample), keyed as <see cref="Occupancy"/> keys its cells.
    /// A sample with no such PSM has no depth and is absent.</summary>
    /// <remarks>D40 partials it out of type A's correlation when one-form cells enter as bounds, since a shallow run
    /// sees fewer of a site's forms (<c>results/g46_bounds_v1</c>: the covered-zero share falls with depth in 31 of 68
    /// deposits).</remarks>
    public static Dictionary<string, double> Depth(PtmQtlDataset dataset)
    {
        var psms = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (run, sample) in dataset.RunToSample)
            if (dataset.TargetPsms.TryGetValue(run, out var n)) psms[sample] = psms.GetValueOrDefault(sample) + n;
        return psms.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => Math.Log2(kv.Value), StringComparer.Ordinal);
    }

    /// <summary>S1 + S2 (with S2c, S2d) + S3b + S4: the stored cells as mzLib cells, biological and not left out by an
    /// artefact rule, of whole-proteome runs only, combined per sample.</summary>
    /// <param name="bounds">Type A's and site-trait fits' reading (DEF-PTM-PAIR v3, DEF-SITE-TRAIT v3; D37, D40, D44): a
    /// cell seen in one form is a bound. A stored covered-zero enters (MetaMorpheus 1.1.13 on), and so do the engine's
    /// own (up to 1.1.12) and each count-only cell's peptidoform counts (<see cref="WithEngineBounds"/>). Without it, a
    /// stored covered-zero is left out and counted.</param>
    public static List<SiteRunOccupancy> Occupancy(PtmQtlDataset dataset, Dictionary<string, string> categories, Dictionary<string, long> leftOut,
        bool bounds = false)
    {
        var anyModification = dataset.Excluded.Where(e => e.Modification is null).Select(e => (e.Protein, e.Position)).ToHashSet();
        var oneModification = dataset.Excluded.Where(e => e.Modification is not null).Select(e => (e.Protein, e.Position, e.Modification!)).ToHashSet();
        var ruleOf = dataset.Excluded.GroupBy(e => (e.Protein, e.Position, e.Modification)).ToDictionary(g => g.Key, g => g.First().Rule);
        var cells = new List<SiteRunOccupancy>();
        var unassigned = new HashSet<(string, int, string, string)>();
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
                unassigned.Add((c.Protein, c.Position, c.Modification, c.Run));
                continue;
            }
            if (c.State == "covered_zero" && !bounds)
            {
                Count(leftOut, "S1 covered_zero: a bound, read only with bounds");
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
        cells = MergeDuplicateSites(cells, leftOut);
        if (bounds) cells = WithEngineBounds(dataset, cells, unassigned, leftOut);
        return SiteOccupancyCalculator.CombineRuns(cells, dataset.RunToSample).ToList();
    }

    /// <summary>What type A's bounds need beyond the stored cells, from the engine's own reading of the observations
    /// (DEF-PTM-PAIR v3; S1, D37, D44): after the artefact rules and the duplicate merge, so a site left out or merged
    /// gains nothing.</summary>
    /// <remarks>
    /// <para>The engine reads the whole-proteome runs' observations with mzLib's <see cref="SiteOccupancyCalculator"/>,
    /// per run, and per design sample for the files MetaMorpheus pooled into one (P32). Its cells are matched to the
    /// stored sites by accession, position and IdWithMotif; a key two stored sites share is ambiguous, gains nothing, and
    /// is counted.</para>
    /// <list type="bullet">
    /// <item><b>Covered-zeros, searches up to MetaMorpheus 1.1.12 only:</b> an engine cell covered and never seen modified
    /// (<see cref="OccupancyState.NotDetected"/>) is added where no cell is stored for that site and run (an
    /// <c>intensity_unassigned</c> cell counts as stored). From 1.1.13 on MetaMorpheus writes its own (#2889), and the
    /// two are never mixed: a search up to 1.1.12 that stores covered-zeros is refused.</item>
    /// <item><b>Count-only cells, every release:</b> each takes the engine's distinct covering and modified peptidoforms
    /// for its run, by which type A ranks it: all modified, a bound above; both forms, no rank.</item>
    /// <item><b>Every other stored cell</b> takes them too, for DEF-SITE-TRAIT v3: a one-form cell enters the fit as its
    /// count, and the detection link is fitted on every cell's counts. Type A never reads them. A one-form cell the
    /// engine does not see has no count, and is counted.</item>
    /// <item>An engine cell that saw the modified form where nothing is stored is counted, never added.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="RunnerException">The search's MetaMorpheus release is unknown, or a search up to 1.1.12 stores
    /// covered-zero cells.</exception>
    public static List<SiteRunOccupancy> WithEngineBounds(PtmQtlDataset dataset, List<SiteRunOccupancy> cells,
        IReadOnlySet<(string Protein, int Position, string IdWithMotif, string Run)> unassigned, Dictionary<string, long> leftOut)
    {
        if (!Version.TryParse(dataset.SearchEngineVersion, out var release))
            throw new RunnerException($"{dataset.DatasetId}: type A's bounds need the search's MetaMorpheus release, to know whether it wrote "
                + $"covered-zero cells itself (ptmQtl S1); the bundle records '{dataset.SearchEngineVersion ?? "none"}'.");
        var engineCoveredZeros = release < new Version(1, 1, 13);
        if (engineCoveredZeros && dataset.Occupancy.Any(c => c.State == "covered_zero"))
            throw new RunnerException($"{dataset.DatasetId}: a MetaMorpheus {release} search stores covered-zero cells, which only 1.1.13 on writes; "
                + "the engine's and MetaMorpheus's are never mixed (ptmQtl S1).");

        // The engine's cells keyed as the stored ones: a file MetaMorpheus pooled into a design sample (P32) by that sample.
        var storedKeys = cells.Select(c => c.Run).ToHashSet(StringComparer.Ordinal);
        var pooled = dataset.RunToSample.Where(kv => kv.Key != kv.Value && storedKeys.Contains(kv.Value)).Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal);
        var engineKey = dataset.RunToSample.ToDictionary(kv => kv.Key, kv => pooled.Contains(kv.Value) ? kv.Value : kv.Key, StringComparer.Ordinal);
        var observations = dataset.Observations.Where(o => (dataset.OccupancyRuns is not { } runs || runs.Contains(o.Run)) && engineKey.ContainsKey(o.Run));
        var engine = SiteOccupancyCalculator.Calculate(observations, engineKey, IsBiological);

        var byName = cells.Select(c => c.Site).Distinct().GroupBy(s => (s.ProteinAccession, s.Position, IdWithMotif(s.Modification))).ToList();
        var siteOf = byName.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var seen = cells.Select(c => (c.Site, c.Run)).ToHashSet();
        var countsOf = new Dictionary<(ModificationSite, string), (int? Covering, int? Modified)>();
        var everyCount = new Dictionary<(ModificationSite, string), (int? Covering, int? Modified)>();
        var added = new List<SiteRunOccupancy>();
        long modifiedNotStored = 0;
        foreach (var e in engine)
        {
            var name = (e.Site.ProteinAccession, e.Site.Position, IdWithMotif(e.Site.Modification));
            if (!siteOf.TryGetValue(name, out var site)) continue;
            everyCount[(site, e.Run)] = (e.CoveringPeptidoforms, e.ModifiedPeptidoforms);
            if (e.State != OccupancyState.NotDetected) countsOf[(site, e.Run)] = (e.CoveringPeptidoforms, e.ModifiedPeptidoforms);
            if (seen.Contains((site, e.Run)) || unassigned.Contains((name.ProteinAccession, name.Position, name.Item3, e.Run))) continue;
            if (e.State != OccupancyState.NotDetected) modifiedNotStored++;
            else if (engineCoveredZeros) added.Add(e with { Site = site });
        }
        // A count-only cell takes the counts by which type A ranks it. Every other stored cell takes the engine's counts
        // too, which type A never reads: under DEF-SITE-TRAIT v3 a one-form cell enters as its count, and the detection
        // link is fitted on the counts of every cell, measured ones included.
        var result = cells.Select(c => (c.State == OccupancyState.CountOnly ? countsOf : everyCount).TryGetValue((c.Site, c.Run), out var k)
            ? c with { CoveringPeptidoforms = k.Covering, ModifiedPeptidoforms = k.Modified } : c).ToList();
        result.AddRange(added);
        leftOut["v3 bounds: covered-zero cells from MetaMorpheus"] = cells.LongCount(c => c.State == OccupancyState.NotDetected);
        leftOut["v3 bounds: covered-zero cells added from the engine's coverage"] = added.Count;
        leftOut["v3 bounds: count-only cells given peptidoform counts"] = cells.LongCount(c => c.State == OccupancyState.CountOnly && countsOf.ContainsKey((c.Site, c.Run)));
        leftOut["v3 bounds: other stored cells given peptidoform counts (site-trait fits)"] =
            cells.LongCount(c => c.State != OccupancyState.CountOnly && everyCount.ContainsKey((c.Site, c.Run)));
        leftOut["v3 bounds: one-form stored cells with no peptidoform counts (left out of site-trait fits)"] =
            cells.LongCount(c => !(c.State == OccupancyState.Quantified && c.UnmodifiedQuantified)
                && !(c.State == OccupancyState.CountOnly ? countsOf : everyCount).ContainsKey((c.Site, c.Run)));
        leftOut["v3 bounds: engine saw the modified form, nothing stored (not added)"] = modifiedNotStored;
        leftOut["v3 bounds: site names two stored sites share (gain nothing)"] = byName.Count(g => g.Count() > 1);
        return result;
    }

    /// <summary>S5: one physical site under several accessions is one site within a dataset. MetaMorpheus
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

    /// <summary>A <c>ptm_pairs</c> row, columns as <c>DEF-PTM-PAIR v3</c> gives them.</summary>
    /// <param name="depthLike">D78: a type A row of a depth-like deposit, or a pooled row with one behind it; flagged
    /// <c>depth_like</c>, never dropped.</param>
    private static Row PairRow(bool isA, string scope, string keyA, string keyB, string proteinA, string proteinB, bool overlapping,
        double value, long? n, double p, double q, IReadOnlyList<string>? datasets = null, long? agreeing = null, bool meta = false,
        bool depthLike = false)
    {
        string? sign = isA && double.IsFinite(value) ? (value >= 0 ? "+" : "-") : null;
        var flags = overlapping || !isA ? new List<object?> { "overlapping" } : new List<object?>();
        if (isA && depthLike) flags.Add("depth_like");
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
            ["flags"] = flags,
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
    /// <remarks>
    /// <para>Type P leaves out only what S2d (a) says of it: an NEM deposit's Cys-containing peptidoforms. S2c's and
    /// S2d's site exclusions reach the occupancy-based results, type A and site-trait fits.</para>
    /// <para>Type A is <c>DEF-PTM-PAIR v3</c>: a cell seen in one form is a bound, tied at its end of the site's ranks
    /// (<see cref="Occupancy"/> with bounds), and the correlation is partial given each sample's depth
    /// (<see cref="Depth"/>, D40). A sample with no depth is left out of every pair. D78's null labels the deposit
    /// (<see cref="DepthNull"/>).</para>
    /// </remarks>
    public static SitePairsResult SitePairs(PtmQtlDataset dataset)
    {
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        var nterms = ProteinNTerms(dataset);
        var physical = SiteOccupancyCalculator.CombineObservations(dataset.PhysicalObservations ?? dataset.Observations, dataset.RunToSample);
        if (dataset.PhysicalObservations is not null)
            leftOut["S2d (a) observations left out of type P: Cys-containing peptidoform"] = dataset.Observations.Count - dataset.PhysicalObservations.Count;
        var produced = PtmPairEngine.Physical(physical, IsBiological).Select(p => (p, isA: false)).ToList();
        DepthNullResult? depthNull = null;
        if (HasOccupancyResults(dataset))
        {
            // The observation side of type A (which sites share a peptide) reads the runs its occupancy reads.
            var observations = SiteOccupancyCalculator.CombineObservations(
                dataset.OccupancyRuns is { } runs ? dataset.Observations.Where(o => runs.Contains(o.Run)).ToList() : dataset.Observations, dataset.RunToSample);
            var occupancy = Occupancy(dataset, dataset.Categories(), leftOut, bounds: true);
            var depth = Depth(dataset);
            leftOut["D40 samples with occupancy and no depth (left out of every pair)"] =
                occupancy.Select(c => c.Run).Distinct().LongCount(s => !depth.ContainsKey(s));
            var typeA = TypeA(occupancy, observations, depth);
            depthNull = DepthNull(dataset.DatasetId, occupancy, observations, depth, typeA);
            produced.AddRange(typeA.Select(p => (p, isA: true)));
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
                p.Statistic, p.N, isA ? p.PValue : double.NaN, isA ? p.Q : double.NaN, depthLike: depthNull?.DepthLike == true));
            pairs.Add((p, a, b));
        }
        return new SitePairsResult(rows, pairs, leftOut) { DepthNull = depthNull };
    }

    /// <summary>Type A under <c>DEF-PTM-PAIR v3</c>: mzLib's <see cref="PtmPairEngine.CoVarying"/> with bounds, partial
    /// given depth. A site enters when measured, in either form or both, in at least <see cref="MinQuantifiedFraction"/>
    /// of the samples.</summary>
    public static IReadOnlyList<PtmPair> TypeA(IReadOnlyList<SiteRunOccupancy> occupancy, IReadOnlyList<PeptidoformObservation> observations,
        IReadOnlyDictionary<string, double> depth) =>
        PtmPairEngine.CoVarying(occupancy, observations, minQuantifiedFraction: MinQuantifiedFraction, includeBounds: true, runCovariate: depth);

    private static long Calls(IEnumerable<PtmPair> pairs) =>
        pairs.LongCount(p => p.TestStatus == PairTestStatus.Tested && !p.Overlapping && p.Q <= CallQ);

    /// <summary>D78: the deposit's depth-preserving null. Each site's sample cells are permuted within blocks of
    /// <see cref="NullBlock"/> samples adjacent in depth, independently of every other site, <see cref="NullReplicates"/>
    /// times, and type A is taken again (<see cref="TypeA"/>) with the same observations and depths.</summary>
    /// <remarks>
    /// <para>It keeps each site's own relation to depth, and anything else samples of similar depth share, biology
    /// included, and breaks every link between sites. A call it makes again cannot be told from depth acting on each
    /// site alone, so a deposit whose null calls per replicate reach <see cref="DepthLikeShare"/> of its real calls is
    /// depth-like; with no real call, it is not.</para>
    /// <para>Samples are ordered by depth (a sample with none first), then by name; blocks are taken from the start.
    /// Sites are drawn in the occupancy's order, each replicate from its own seed of <see cref="NullSeed"/>, the dataset
    /// and the replicate, so a run reproduces exactly. As ptmQtl's harness (<c>results/g59_runlevel_sim_v1/sim</c>,
    /// <c>perm</c>), except that the observations are passed, so overlapping pairs are counted in neither.</para>
    /// </remarks>
    public static DepthNullResult DepthNull(string datasetId, IReadOnlyList<SiteRunOccupancy> occupancy, IReadOnlyList<PeptidoformObservation> observations,
        IReadOnlyDictionary<string, double> depth, IReadOnlyList<PtmPair> real)
    {
        var samples = occupancy.Select(c => c.Run).Distinct()
            .OrderBy(s => depth.TryGetValue(s, out var d) ? d : double.NaN).ThenBy(s => s, StringComparer.Ordinal).ToArray();
        var index = samples.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => t.i, StringComparer.Ordinal);
        var nullCalls = new List<long>();
        for (var rep = 1; rep <= NullReplicates; rep++)
        {
            var rng = new Random(Seed(NullSeed, Text(datasetId), NullBlock, rep));
            var permuted = new List<SiteRunOccupancy>(occupancy.Count);
            foreach (var site in occupancy.GroupBy(c => c.Site))
            {
                var perm = Enumerable.Range(0, samples.Length).ToArray();
                for (var start = 0; start < samples.Length; start += NullBlock)
                {
                    var end = Math.Min(samples.Length, start + NullBlock);
                    for (var i = end - 1; i > start; i--)
                    {
                        var j = rng.Next(start, i + 1);
                        (perm[i], perm[j]) = (perm[j], perm[i]);
                    }
                }
                foreach (var c in site) permuted.Add(c with { Run = samples[perm[index[c.Run]]] });
            }
            nullCalls.Add(Calls(TypeA(permuted, observations, depth)));
        }
        var realCalls = Calls(real);
        var share = realCalls > 0 ? nullCalls.Average() / realCalls : double.NaN;
        return new DepthNullResult(realCalls, nullCalls, share, share >= DepthLikeShare);
    }

    /// <summary>Seeds combined deterministically (.NET's <c>HashCode.Combine</c> is randomized per process).</summary>
    private static int Seed(params long[] parts)
    {
        long h = 1469598103;
        foreach (var p in parts) h = unchecked(h * 1099511 + p);
        return (int)(h & 0x7fffffff);
    }

    private static long Text(string s)
    {
        long h = 0;
        foreach (var c in s) h = unchecked(h * 31 + c);
        return h;
    }

    /// <summary><c>ptmqtl.pool_pairs</c>: one species' dataset pairs matched on the cross-dataset key, in two or more datasets.</summary>
    /// <param name="pairs">Each dataset's pairs, with their sites as the dataset's artefact stored them.</param>
    /// <remarks>D35: a site joins only where its sequence window agrees. When the datasets holding a site (by its
    /// cross-dataset key) disagree on its window, or any of them has none, the site is left out of the pooled rows and
    /// counted, and so is every pair holding it. Datasets are never refused for their search databases.</remarks>
    /// <param name="depthLike">D78: the datasets labelled depth-like; a pooled type A row with any of them behind it is
    /// flagged <c>depth_like</c>.</param>
    public static PoolResult Pool(string species, IEnumerable<(string Scope, PtmPair Pair, PairSite A, PairSite B)> pairs,
        IReadOnlySet<string>? depthLike = null)
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
                g.Scopes, g.ScopesAgreeing, meta: true, depthLike: depthLike is not null && g.Scopes.Any(depthLike.Contains)));
        }
        return new PoolResult(rows, leftOut);
    }

    /// <summary>DEF-SITE-TRAIT v3's covariates as mzLib takes them: each one categorical, coded as indicator columns
    /// against the level that sorts first (ordinal), each term named <c>&lt;covariate&gt;=&lt;level&gt;</c>, covariates
    /// in ordinal order of their names. A covariate with one level among the samples is not fitted.</summary>
    /// <param name="covariates">Each covariate's level per sample.</param>
    /// <param name="samples">The samples fitted; each needs a level of every covariate.</param>
    /// <exception cref="RunnerException">A sample has no level for a covariate.</exception>
    public static (Dictionary<string, double[]> Values, List<string> Terms, List<string> NotFitted) CovariateDesign(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> covariates, IReadOnlyCollection<string> samples)
    {
        var terms = new List<string>();
        var notFitted = new List<string>();
        var columns = new List<(IReadOnlyDictionary<string, string> LevelOf, string Level)>();
        foreach (var (name, levelOf) in covariates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var levels = samples.Select(s => levelOf.TryGetValue(s, out var l) && l.Length > 0 ? l
                    : throw new RunnerException($"sample {s} has no value of the covariate {name} (ptmQtl DEF-SITE-TRAIT v3)"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (levels.Count < 2)
            {
                notFitted.Add(name);
                continue;
            }
            foreach (var level in levels.Skip(1))
            {
                terms.Add($"{name}={level}");
                columns.Add((levelOf, level));
            }
        }
        var values = samples.ToDictionary(s => s, s => columns.Select(c => c.LevelOf[s] == c.Level ? 1.0 : 0.0).ToArray(), StringComparer.Ordinal);
        return (values, terms, notFitted);
    }

    /// <summary>Whether the intercept, the trait and the given covariate terms can be told apart over the samples: the
    /// design has full column rank.</summary>
    private static bool Separable(IReadOnlyList<string> samples, IReadOnlyDictionary<string, double> trait,
        IReadOnlyDictionary<string, double[]> values, IReadOnlyList<int> terms)
    {
        var x = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.Dense(samples.Count, 2 + terms.Count,
            (i, j) => j == 0 ? 1 : j == 1 ? trait[samples[i]] : values[samples[i]][terms[j - 2]]);
        return x.Rank() == x.ColumnCount;
    }

    /// <summary><c>ptmqtl.site_traits</c> on one dataset and one trait (<c>DEF-SITE-TRAIT v3</c>).</summary>
    /// <remarks>
    /// <para>v3 (ptmQtl G64, D37, D90) reads the cells as type A does (<see cref="Occupancy"/> with bounds): a run seen in
    /// one form, a floor, a count-only cell or a covered zero enters, never dropped. mzLib fits it as a count
    /// (<see cref="BoundCellModel.Sampling"/>), with each sample's depth (<see cref="Depth"/>) in the detection link, so a
    /// sample with no depth is left out of the fit and counted. The covariates are categorical
    /// (<see cref="CovariateDesign"/>), and a deposit whose covariates cannot be told apart from the trait is refused.
    /// Depth is never a covariate (aging's DEF-AGE-EFFECT §3.4); the trait's rank correlation with depth over the samples
    /// fitted is recorded as evidence (<see cref="SiteTraitsResult.TraitDepth"/>), never a filter.</para>
    /// <para>As v2 (D33), the response is the log2 odds of occupancy, <c>response</c> = <c>log2_odds_occupancy</c>.</para>
    /// </remarks>
    /// <param name="trait">The trait per sample (after S4).</param>
    /// <param name="replicate">The biological replicate per sample: the animal, never the injection.</param>
    /// <param name="covariates">Each covariate's level per sample; none when null.</param>
    /// <exception cref="RunnerException">The deposit is enriched (S3), has no whole-proteome run (S3b), or its covariates
    /// cannot be told apart from the trait (<c>covariate_confounded</c>): no site-trait fit is computed on it.</exception>
    public static SiteTraitsResult SiteTraits(PtmQtlDataset dataset, string traitId, IReadOnlyDictionary<string, double> trait,
        IReadOnlyDictionary<string, string> replicate, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? covariates = null)
    {
        if (dataset.Enriched)
            throw new RunnerException($"{dataset.DatasetId} captures a modified form (ptmQtl S3): no site-trait fit is computed on it.");
        if (!HasOccupancyResults(dataset))
            throw new RunnerException($"{dataset.DatasetId} has no whole-proteome run (ptmQtl S3b): no site-trait fit is computed on it.");
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        var nterms = ProteinNTerms(dataset);
        var occupancy = Occupancy(dataset, dataset.Categories(), leftOut, bounds: true);
        var depth = Depth(dataset);
        var samples = trait.Keys.Where(depth.ContainsKey).Order(StringComparer.Ordinal).ToList();
        leftOut["D40 samples with the trait and no depth (left out of the fit)"] = trait.Count - samples.Count;
        var fitted = samples.ToDictionary(s => s, s => trait[s], StringComparer.Ordinal);
        var (values, terms, notFitted) = CovariateDesign(covariates ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(), samples);
        if (terms.Count > 0 && !Separable(samples, fitted, values, Enumerable.Range(0, terms.Count).ToList()))
        {
            var named = terms.Select((t, j) => (Covariate: t[..t.IndexOf('=')], j)).GroupBy(t => t.Covariate)
                .Where(g => !Separable(samples, fitted, values, g.Select(t => t.j).ToList())).Select(g => g.Key).ToList();
            throw new RunnerException($"{dataset.DatasetId}: the covariate(s) {string.Join(", ", named.Count > 0 ? named : terms.Select(t => t[..t.IndexOf('=')]).Distinct())} "
                + $"cannot be told apart from {traitId} over the {samples.Count} samples fitted (covariate_confounded, ptmQtl DEF-SITE-TRAIT v3): "
                + "no site-trait fit is computed on it.");
        }
        var traitDepth = StatisticalModels.SpearmanCorrelation.Correlate(samples.Select(s => fitted[s]).ToList(), samples.Select(s => depth[s]).ToList());
        var rows = new List<Row>();
        var fits = new List<Row>();
        var options = new SiteTraitOptions { Scale = OccupancyScale.Log2Odds, BoundCells = BoundCellModel.Sampling, RunDepth = depth };
        foreach (var e in SiteTraitEffects.Fit(occupancy, fitted, replicate, values, terms, options))
        {
            var key = Key(e.Site, false, nterms, dataset.Unimod);
            rows.Add(new Row
            {
                ["feature_type"] = "ptm_site",
                ["feature_key"] = key,
                ["feature_key_unimod"] = Key(e.Site, true, nterms, dataset.Unimod),
                ["protein_accessions"] = new List<object?> { e.Site.ProteinAccession },
                ["trait_id"] = traitId,
                ["response"] = "log2_odds_occupancy",
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
                ["covariates"] = string.Join(";", terms),
                ["covariate_effects"] = e.CovariateEffects.Select(v => (object?)Num(v)).ToList(),
                ["replicate_variance"] = Num(e.ReplicateVariance),
                ["residual_variance"] = Num(e.ResidualVariance),
                ["residual_variance_borrowed"] = e.ResidualVarianceBorrowed,
                ["counted_runs"] = (long)e.CountedRuns,
            });
        }
        return new SiteTraitsResult(rows, fits, leftOut) { Covariates = terms, CovariatesNotFitted = notFitted, TraitDepth = traitDepth };
    }
}
