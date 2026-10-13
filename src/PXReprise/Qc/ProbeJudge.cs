using System.Globalization;

namespace PXReprise.Qc;

/// <summary>A park the probe measurements call for, with the numbers behind it.</summary>
public sealed record ProbeFinding(string Park, string Evidence);

/// <summary>What the probe measured, as facts, and the parks it calls for.</summary>
public sealed record ProbeVerdict(IReadOnlyList<ProbeFinding> Parks, string? MeasuredEnrichment, IReadOnlyList<string> Notes);

/// <summary>
/// Judges a deposit's probe runs against what the text said (D34: the spectra win where they can measure). Thresholds,
/// each with its source:
/// <list type="bullet">
/// <item>species (the user, 2026-10-07, from the G31 calibration: pure rat runs are only 62-88% rat because reviewed rat is
/// thin, so a share threshold mistakes a thin database for a second species): once a run has at least
/// <see cref="MinSpeciesPsms"/> species-specific PSMs, ANOTHER species with more of them than the expected one parks
/// <c>waiting_species</c>, and the expected species under <see cref="MixedBelow"/> of them parks
/// <c>waiting_mixed_species</c> (D35; PXD044773's mouse runs searched as human, aging S83);</item>
/// <item>enrichment: phospho at <see cref="EnrichedPhospho"/> or GG at <see cref="EnrichedGg"/> of a run's PSMs, the
/// rule BuildPtmDb's --exclude-enriched applies (whole proteomes run 0.1-0.3% phospho; GG has a small baseline because a
/// double carbamidomethyl on K has its mass); acetyl-K at <see cref="EnrichedAcetylK"/> (PXR-A88, aging 082: whole
/// proteomes 0.1-0.5% of 1% PSMs, the acetyl-lysine capture runs of PXD038328 and PXD061654 18-51%);</item>
/// <item>alkylation: with carbamidomethyl expected, a non-enriched deposit whose runs show under
/// <see cref="MinCysteineShare"/> cysteine-containing peptides parks <c>waiting_alkylation_check</c> (aging S82: alkylated
/// lysates 11-28%, the four unexplained deposits 0.26-0.60%, enrichments 2-5%).</item>
/// <item>identifications (v0.4.14): when no probe run reaches <see cref="MinIdentifiedPsms"/> PSMs, the search as set up
/// finds nothing to judge, and the deposit parks <c>waiting_no_identifications</c> instead of passing on no evidence.
/// PXD000046 (2026-10-13), dimethyl-labelled but read as label-free, had 0 PSMs in all three probe runs of ~23,000 MS2
/// each. The weakest probe run before it had 224 (PXD009212's 27M_ratHP3_6), and one run at 50 or more is enough.</item>
/// </list>
/// </summary>
public static class ProbeJudge
{
    public const int MinIdentifiedPsms = 50;
    public const int MinSpeciesPsms = 50;
    public const double MixedBelow = 0.50;
    public const double EnrichedPhospho = 0.05;
    public const double EnrichedGg = 0.02;
    public const double EnrichedAcetylK = 0.05;
    public const double MinCysteineShare = 0.02;
    public const int MinPeptidesForCysteine = 100;

    /// <summary>The UniProt organism name of a profile's organism key, or null for a key with no known name.</summary>
    public static string? SpeciesName(string organismKey) => organismKey.ToLowerInvariant() switch
    {
        "human" => "Homo sapiens",
        "mouse" => "Mus musculus",
        "rat" => "Rattus norvegicus",
        _ => null,
    };

    /// <param name="probeRuns">The runs the probe searched; a run with no PSM at all is absent from <paramref name="runs"/>.</param>
    public static ProbeVerdict Judge(IReadOnlyDictionary<string, RunMeasures> runs, string? expectedSpecies,
        bool expectCarbamidomethyl, bool screenedEnriched, IReadOnlyCollection<string>? probeRuns = null)
    {
        var parks = new List<ProbeFinding>();
        var notes = new List<string>();
        static string Pct(double? x) => x is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "n/a";

        // Identifications: nothing else can be judged without them.
        if (runs.Values.All(m => m.Psms < MinIdentifiedPsms))
        {
            var all = (probeRuns ?? runs.Keys).Union(runs.Keys, StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
            parks.Add(new("waiting_no_identifications",
                $"no probe run reaches {MinIdentifiedPsms} PSMs at 1% FDR: {(all.Count == 0 ? "no run" : string.Join("; ", all.Select(r => $"{r} {runs.GetValueOrDefault(r)?.Psms ?? 0}")))}"));
        }

        // Species, per run.
        var wrong = new List<string>();
        var mixed = new List<string>();
        foreach (var (run, m) in runs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            int n = m.SpeciesSpecificPsms;
            if (n < MinSpeciesPsms) { notes.Add($"{run}: species not judged ({n} species-specific PSMs < {MinSpeciesPsms})"); continue; }
            string shares = string.Join(", ", m.SpeciesPsms.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            if (expectedSpecies is null)
            {
                // No expectation to compare with: only a run that no species dominates is called.
                int top = m.SpeciesPsms.Values.DefaultIfEmpty(0).Max();
                if ((double)top / n < MixedBelow) mixed.Add($"{run} ({shares}; {m.SharedSpeciesPsms} shared)");
                continue;
            }
            int expected = m.SpeciesPsms.GetValueOrDefault(expectedSpecies);
            int topOther = m.SpeciesPsms.Where(kv => kv.Key != expectedSpecies).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            if (topOther > expected) wrong.Add($"{run} ({shares})");
            else if ((double)expected / n < MixedBelow) mixed.Add($"{run} ({shares}; {m.SharedSpeciesPsms} shared)");
        }
        if (expectedSpecies is null) notes.Add("species not compared: the deposit's organism has no known UniProt name");
        if (wrong.Count > 0)
            parks.Add(new("waiting_species", $"expected {expectedSpecies}; {wrong.Count} of {runs.Count} probe run(s) have more PSMs specific to another species: {string.Join("; ", wrong)}"));
        if (mixed.Count > 0)
            parks.Add(new("waiting_mixed_species", $"the expected species holds under {MixedBelow:P0} of the species-specific PSMs in {string.Join("; ", mixed)}"));

        // Enrichment, measured.
        var enriched = runs.Where(kv => kv.Value.PhosphoShare >= EnrichedPhospho || kv.Value.GgShare >= EnrichedGg
                                        || kv.Value.AcetylKShare >= EnrichedAcetylK).ToList();
        string? measured = null;
        if (enriched.Count > 0)
        {
            bool phospho = enriched.Any(kv => kv.Value.PhosphoShare >= EnrichedPhospho), gg = enriched.Any(kv => kv.Value.GgShare >= EnrichedGg);
            bool acetyl = enriched.Any(kv => kv.Value.AcetylKShare >= EnrichedAcetylK);
            measured = string.Join("+", new[] { phospho ? "phospho" : null, gg ? "GG" : null, acetyl ? "acetyl" : null }.Where(x => x is not null));
            notes.Add($"measured enrichment ({measured}) in {enriched.Count} of {runs.Count} probe run(s): "
                + string.Join("; ", enriched.Select(kv => $"{kv.Key} phospho {Pct(kv.Value.PhosphoShare)} GG {Pct(kv.Value.GgShare)} acetyl-K {Pct(kv.Value.AcetylKShare)}")));
        }

        // Alkylation, against the expectation.
        if (expectCarbamidomethyl && !screenedEnriched && measured is null)
        {
            var judged = runs.Where(kv => kv.Value.Peptides >= MinPeptidesForCysteine).ToList();
            if (judged.Count > 0 && judged.All(kv => kv.Value.CysteineShare < MinCysteineShare))
                parks.Add(new("waiting_alkylation_check",
                    $"carbamidomethyl expected, but cysteine-containing peptides are {string.Join("; ", judged.Select(kv => $"{kv.Key} {Pct(kv.Value.CysteineShare)} of {kv.Value.Peptides}"))} (alkylated lysates run 11-28%)"));
            else if (judged.Count == 0) notes.Add($"alkylation not judged: no probe run reaches {MinPeptidesForCysteine} peptides");
        }
        return new ProbeVerdict(parks, measured, notes);
    }
}
