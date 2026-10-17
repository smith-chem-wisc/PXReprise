using System.Globalization;
using MzLibUtil;
using Readers;

namespace PXReprise.Qc;

/// <summary>
/// One probe run's measured facts (D32, D34; PLAN_search-settings.md slice 3), over its 1% target non-contaminant PSMs
/// (q-value &lt; 0.01, the D36 q tier: a probe search is too small for PEP). Species come from MetaMorpheus's
/// <c>Organism Name</c>: a PSM whose candidate proteins all come from one organism counts for it; one shared between
/// organisms counts as shared. Cysteine, phospho, GG and acetyl-K are counted over PSMs with ONE candidate sequence only.
/// Acetyl-K is MetaMorpheus's `Acetylation on K` or a UniProt `N6-acetyllysine` site from the database: counting only the
/// first put PXD038328's capture runs at 10.8% instead of 27.9% (PXR-A88).
/// A PSM with a candidate protein from the contaminant panel counts for no species (<see cref="PanelPsms"/>, v0.4.13):
/// MetaMorpheus keeps a panel entry whose accession a species database also holds as that species' target (human
/// keratins: P13645 is KRT10 in both), so skin keratins from handling counted as human and parked PXD022196's mouse gel
/// bands, where they outnumbered mouse.
/// </summary>
public sealed record RunMeasures(
    int Psms,
    IReadOnlyDictionary<string, int> SpeciesPsms,
    int SharedSpeciesPsms,
    int UnambiguousPsms,
    int Peptides,
    int CysteinePeptides,
    int PhosphoPsms,
    int GgPsms,
    int AcetylKPsms = 0,
    int PanelPsms = 0)
{
    public double? CysteineShare => Peptides > 0 ? (double)CysteinePeptides / Peptides : null;
    public double? PhosphoShare => UnambiguousPsms > 0 ? (double)PhosphoPsms / UnambiguousPsms : null;
    public double? GgShare => UnambiguousPsms > 0 ? (double)GgPsms / UnambiguousPsms : null;
    public double? AcetylKShare => UnambiguousPsms > 0 ? (double)AcetylKPsms / UnambiguousPsms : null;
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

    /// <param name="panelAccessions">The contaminant panel's accessions; a PSM naming any of them counts for no species.</param>
    public static IReadOnlyDictionary<string, RunMeasures> FromAllPsms(string allPsms, IReadOnlySet<string>? panelAccessions = null)
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
            int shared = 0, unambiguous = 0, phospho = 0, gg = 0, acetylK = 0, panel = 0;
            var peptides = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in run)
            {
                var organisms = p.ProteinGroupInfos.Select(i => i.organism.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (panelAccessions is not null && p.ProteinGroupInfos.Any(i => panelAccessions.Contains(i.proteinAccessions.Trim()))) panel++;
                else if (organisms.Count == 1) species[organisms[0]] = species.GetValueOrDefault(organisms[0]) + 1;
                else if (organisms.Count > 1) shared++;
                if (p.FullSequence.Contains('|') || p.BaseSequence.Contains('|')) continue;
                unambiguous++;
                peptides.Add(p.BaseSequence);
                var mods = p.FullSequence.ParseModifications().Values;
                if (mods.Any(m => m.Contains("Phosphorylation on ", StringComparison.Ordinal))) phospho++;
                if (mods.Any(m => m.Contains("GG (Ubiquitination Site) on K", StringComparison.Ordinal))) gg++;
                if (mods.Any(m => m.Contains("Acetylation on K", StringComparison.Ordinal) || m.Contains("N6-acetyllysine", StringComparison.Ordinal))) acetylK++;
            }
            o[run.Key] = new RunMeasures(run.Count(), species, shared, unambiguous, peptides.Count,
                peptides.Count(s => s.Contains('C')), phospho, gg, acetylK, panel);
        }
        return o;
    }

    /// <summary>The fewest 1% target PSMs a probe run needs before its precursor offset is taken.</summary>
    public const int MinOffsetPsms = 20;

    /// <summary>An offset no larger than this keeps MetaMorpheus's ±5 ppm: a run's precursor errors spread about ±2 ppm.</summary>
    public const double OffsetWithinDefault = 2.0;

    /// <summary>The widest window <see cref="WindowPpm"/> gives: the probe search's own (±20 ppm) cannot see further.</summary>
    public const int MaxWindowPpm = 20;

    /// <summary>The spacing of MetaMorpheus's isotope notches.</summary>
    private const double IsotopeSpacing = Chemistry.Constants.C13MinusC12;

    /// <summary>
    /// Each probe run's precursor offset: the median precursor mass error, in ppm, of its 1% target PSMs, after the isotope
    /// notch MetaMorpheus matched each at. The probe takes precursors within ±20 ppm and runs no calibration, so this is the
    /// instrument's own offset (PXD045821, 2026-10-17: +8.7 ppm in every run). mzLib's light reader keeps no mass
    /// difference, so the columns are read here. A run with fewer than <see cref="MinOffsetPsms"/> such PSMs has none.
    /// </summary>
    public static IReadOnlyDictionary<string, double> PrecursorOffsets(string allPsms)
    {
        var header = Search.Tsv.Header(allPsms);
        var missing = new[] { "File Name", "QValue", "Decoy/Contaminant/Target", "Mass Diff (Da)", "Monoisotopic Mass" }.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"{Path.GetFileName(allPsms)} lacks column(s) {string.Join(", ", missing)}");
        var byRun = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var row in Search.Tsv.Read(allPsms))
        {
            if (row["Decoy/Contaminant/Target"].Trim() != "T" || !(Search.Tsv.Number(row["QValue"]) < 0.01)) continue;
            double da = Search.Tsv.Number(row["Mass Diff (Da)"].Split('|')[0]), mass = Search.Tsv.Number(row["Monoisotopic Mass"].Split('|')[0]);
            if (double.IsNaN(da) || !(mass > 0)) continue;
            double ppm = (da - Math.Round(da / IsotopeSpacing) * IsotopeSpacing) / mass * 1e6;
            if (!byRun.TryGetValue(row["File Name"], out var list)) byRun[row["File Name"]] = list = new List<double>();
            list.Add(ppm);
        }
        return byRun.Where(kv => kv.Value.Count >= MinOffsetPsms)
            .ToDictionary(kv => kv.Key, kv => Math.Round(Median(kv.Value), 2), StringComparer.Ordinal);
    }

    /// <summary>
    /// The precursor and FlashLFQ window, in ppm, that holds the largest of <paramref name="offsets"/>: 5 ppm beyond it,
    /// rounded up to a whole ppm, at most <see cref="MaxWindowPpm"/>. Null when no offset was measured or none is larger
    /// than <see cref="OffsetWithinDefault"/>: MetaMorpheus's own windows then hold the runs, and a wider one only adds
    /// random candidates (2026-10-17: ±20 ppm cost 21% of the PSMs of a PXD015170 run whose offset was 0.0 ppm).
    /// </summary>
    public static int? WindowPpm(IEnumerable<double>? offsets)
    {
        double largest = offsets?.Select(Math.Abs).DefaultIfEmpty(0).Max() ?? 0;
        return largest <= OffsetWithinDefault ? null : Math.Min(MaxWindowPpm, (int)Math.Ceiling(largest) + 5);
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
