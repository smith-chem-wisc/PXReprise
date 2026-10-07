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
/// double carbamidomethyl on K has its mass);</item>
/// <item>alkylation: with carbamidomethyl expected, a non-enriched deposit whose runs show under
/// <see cref="MinCysteineShare"/> cysteine-containing peptides parks <c>waiting_alkylation_check</c> (aging S82: alkylated
/// lysates 11-28%, the four unexplained deposits 0.26-0.60%, enrichments 2-5%).</item>
/// </list>
/// </summary>
public static class ProbeJudge
{
    public const int MinSpeciesPsms = 50;
    public const double MixedBelow = 0.50;
    public const double EnrichedPhospho = 0.05;
    public const double EnrichedGg = 0.02;
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

    public static ProbeVerdict Judge(IReadOnlyDictionary<string, RunMeasures> runs, string? expectedSpecies,
        bool expectCarbamidomethyl, bool screenedEnriched)
    {
        var parks = new List<ProbeFinding>();
        var notes = new List<string>();
        static string Pct(double? x) => x is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "n/a";

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
        var enriched = runs.Where(kv => kv.Value.PhosphoShare >= EnrichedPhospho || kv.Value.GgShare >= EnrichedGg).ToList();
        string? measured = null;
        if (enriched.Count > 0)
        {
            bool phospho = enriched.Any(kv => kv.Value.PhosphoShare >= EnrichedPhospho), gg = enriched.Any(kv => kv.Value.GgShare >= EnrichedGg);
            measured = string.Join("+", new[] { phospho ? "phospho" : null, gg ? "GG" : null }.Where(x => x is not null));
            notes.Add($"measured enrichment ({measured}) in {string.Join("; ", enriched.Select(kv => $"{kv.Key} phospho {Pct(kv.Value.PhosphoShare)} GG {Pct(kv.Value.GgShare)}"))}");
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
