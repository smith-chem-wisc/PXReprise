using System.Globalization;
using MzLibUtil;
using Readers;

namespace PXReprise.Qc;

/// <summary>
/// One probe run's measured facts (D32, D34; PLAN_search-settings.md slice 3), over its 1% target non-contaminant PSMs
/// (q-value &lt; 0.01, the D36 q tier: a probe search is too small for PEP). Species come from MetaMorpheus's
/// <c>Organism Name</c>: a PSM whose candidate proteins all come from one organism counts for it; one shared between
/// organisms counts as shared. Cysteine, phospho and GG are counted over PSMs with ONE candidate sequence only.
/// </summary>
public sealed record RunMeasures(
    int Psms,
    IReadOnlyDictionary<string, int> SpeciesPsms,
    int SharedSpeciesPsms,
    int UnambiguousPsms,
    int Peptides,
    int CysteinePeptides,
    int PhosphoPsms,
    int GgPsms)
{
    public double? CysteineShare => Peptides > 0 ? (double)CysteinePeptides / Peptides : null;
    public double? PhosphoShare => UnambiguousPsms > 0 ? (double)PhosphoPsms / UnambiguousPsms : null;
    public double? GgShare => UnambiguousPsms > 0 ? (double)GgPsms / UnambiguousPsms : null;
    public int SpeciesSpecificPsms => SpeciesPsms.Values.Sum();
}

/// <summary>
/// Reads a probe search's <c>AllPSMs.psmtsv</c> through mzLib's streaming <see cref="LightWeightSpectralMatchReader"/>
/// (the oracle of 2026-10-07: reuse mzLib's reader and its <c>ParseModifications</c>; the per-run summary itself exists
/// nowhere and is ours). mzLib's readers return NOTHING, with only warnings, when a required column is missing, so the
/// header is checked here first and any reader warning is fatal.
/// </summary>
public static class ProbeMeasures
{
    public static readonly string[] RequiredColumns =
        { "File Name", "Base Sequence", "Full Sequence", "QValue", "Decoy/Contaminant/Target", "Organism Name" };

    public static IReadOnlyDictionary<string, RunMeasures> FromAllPsms(string allPsms)
    {
        string header = File.ReadLines(allPsms).FirstOrDefault() ?? throw new InvalidDataException($"{allPsms} is empty");
        var columns = header.Split('\t');
        var missing = RequiredColumns.Where(c => !columns.Contains(c)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"{Path.GetFileName(allPsms)} lacks column(s) {string.Join(", ", missing)}");

        var keep = new Dictionary<string, Func<string, bool>>
        {
            // Target only: "T|C" (shared with a contaminant) is left out, the conservative reading.
            ["Decoy/Contaminant/Target"] = c => c.Trim() == "T",
            ["QValue"] = c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double q) && q < 0.01,
        };
        var psms = LightWeightSpectralMatchReader.ReadTsv(allPsms, out var warnings, keep);
        if (warnings.Count > 0) throw new InvalidDataException($"{Path.GetFileName(allPsms)}: mzLib could not read every row ({warnings[0]})");

        var o = new Dictionary<string, RunMeasures>(StringComparer.Ordinal);
        foreach (var run in psms.GroupBy(p => p.FileName))
        {
            var species = new Dictionary<string, int>(StringComparer.Ordinal);
            int shared = 0, unambiguous = 0, phospho = 0, gg = 0;
            var peptides = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in run)
            {
                var organisms = p.ProteinGroupInfos.Select(i => i.organism.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (organisms.Count == 1) species[organisms[0]] = species.GetValueOrDefault(organisms[0]) + 1;
                else if (organisms.Count > 1) shared++;
                if (p.FullSequence.Contains('|') || p.BaseSequence.Contains('|')) continue;
                unambiguous++;
                peptides.Add(p.BaseSequence);
                var mods = p.FullSequence.ParseModifications().Values;
                if (mods.Any(m => m.Contains("Phosphorylation on ", StringComparison.Ordinal))) phospho++;
                if (mods.Any(m => m.Contains("GG (Ubiquitination Site) on K", StringComparison.Ordinal))) gg++;
            }
            o[run.Key] = new RunMeasures(run.Count(), species, shared, unambiguous, peptides.Count,
                peptides.Count(s => s.Contains('C')), phospho, gg);
        }
        return o;
    }
}
