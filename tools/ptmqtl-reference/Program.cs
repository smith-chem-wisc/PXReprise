// G37, G62, G64: the ptmqtl.* reference case, v4. Builds a small synthetic catalog slice (inputs/), then applies
// design/DEFINITIONS.md (DEF-PTM-PAIR v3, DEF-SITE-TRAIT v3) using mzLib calls only, and writes the rows the engine must
// reproduce (expected/). Every rule the definitions state is exercised once:
//   S2 categories in (Common Biological, UniProt, Trypsin Digested) and out (Common Variable, the exclusion list,
//      AspN Digested on D); S3 an enriched deposit gives P rows only; S4 two fractions combined, two injections kept;
//   S5 protein N-terminus keyed @protein_n_term; pooled type A and P across two datasets.
// v3 adds type A's bounds and depth (D37, D40, D42-D44, D82):
//   - D1 is a MetaMorpheus 1.1.12 search: it stores no covered-zero, so the engine reads them from the observations'
//     coverage (S3: P2 S5's modified form absent). It also holds a ceiling (S4: P3's N-terminus seen only acetylated), a
//     floor (S5: P1 S13's modified forms identified, not quantified), a count-only cell with both forms (S6: P2) and one
//     with the modified form only (S7: P2), and an injection with no target PSM, so no depth (S14_2).
//   - D2 is a 1.1.13 search: MetaMorpheus stores its covered-zeros (R3: P2 S5), and the engine adds none. Its sites rise
//     with its runs' depth.
//   - D78's depth null (D83) runs on each deposit's type A and labels it; a labelled deposit's type A rows, and a pooled
//     row with one behind it, carry the depth_like flag. expected/depth_null.tsv holds the null's counts.
// v4 adds DEF-SITE-TRAIT v3 (D37, D90): the site-trait fit reads the cells as type A does, as counts (Sampling) with
// each sample's depth, and a sample with no depth is left out; sex is a categorical covariate. D1 and D4 are fitted.
//   - D4 is a 1.1.13 whole proteome of 12 animals built for the fit. A1-A4 were injected twice, A5-A12 once, and age
//     and sex are crossed. Ten phospho sites give the detection link its 100 cells; three rise with age, and one-form
//     runs of every kind occur. Site 8 is never seen in a second injection, so no animal holds two of its runs (D88);
//     site 9 is seen only modified there, so no animal is measured twice (D87).
// Reference cases v1 and v2 were built by this program in ptmQtl, at 1ff86a2; v3 by this program at 8fc5ae8.
// usage: dotnet run -c Release --project tools/ptmqtl-reference -- <out_dir>
// (moved here from ptmQtl's tools/ReferenceCase, 2026-11-23)
using System.Globalization;
using Quantification.PtmQtl;

var inv = CultureInfo.InvariantCulture;
string outDir = args[0];
Directory.CreateDirectory(Path.Combine(outDir, "inputs"));
Directory.CreateDirectory(Path.Combine(outDir, "expected"));
string F(double v) => double.IsNaN(v) ? "" : v.ToString("R", inv);
const string PairDefinition = "ptmQtl:DEF-PTM-PAIR v3", TraitDefinition = "ptmQtl:DEF-SITE-TRAIT v3";

const string Ph = "Common Biological:Phosphorylation on S", GG = "Trypsin Digested:GG (Ubiquitination Site) on K",
    PhU = "UniProt:Phosphoserine on S", Ac = "UniProt:N-acetylalanine on A", Formyl = "Common Biological:Formylation on K",
    Ox = "Common Variable:Oxidation on M", Sumo = "AspN Digested:DVIEVYQEQTGG (SUMO-1 Site human) on D";
var unimod = new Dictionary<string, string> { ["Phosphorylation on S"] = "UNIMOD:21", ["Phosphoserine on S"] = "UNIMOD:21",
    ["GG (Ubiquitination Site) on K"] = "UNIMOD:121", ["N-acetylalanine on A"] = "UNIMOD:1", ["Formylation on K"] = "UNIMOD:122",
    ["Oxidation on M"] = "UNIMOD:35" };
string[] biologicalCategories = ["Common Biological", "UniProt", "Trypsin Digested", "AspN Digested"];
string[] excluded = ["Common Biological:Hydroxylation on N", "Common Biological:Formylation on K"];
var release = new Dictionary<string, string> { ["D1"] = "1.1.12", ["D2"] = "1.1.13", ["D3"] = "1.1.12", ["D4"] = "1.1.13" };

// ---- inputs: runs (dataset, run, combine_to, replicate, age), occupancy levels per measured unit, target PSMs per run
var runs = new List<(string ds, string run, string combineTo, string replicate, double age)>();
var level = new Dictionary<string, (double a, double b, double c, double d)>();   // per raw run
var psms = new Dictionary<string, long>();                                        // per raw run; absent = none passed S7
void Unit(string ds, string run, string combineTo, string rep, double age, double a, double b, double c, double d, long targetPsms)
{
    runs.Add((ds, run, combineTo, rep, age));
    level[run] = (a, b, c, d);
    if (targetPsms > 0) psms[run] = targetPsms;
}
// D1: 13 samples plus two injections of one animal. P1 S13 (a) and P2 S5 (c) co-vary, P3's N-terminus (d) falls as they
// rise, and P1 K17 (b) follows neither. Depth is unrelated to all of them, so D78's null should find little.
double[] noise = [.012, -.018, .007, .015, -.009, -.014, .019, -.004, .011, -.016, .003, .008, -.012];
double[] b1 = [.50, .20, .60, .30, .70, .40, .55, .25, .65, .35, .45, .15, .50];
long[] p1 = [1000, 800, 1500, 1200, 2000, 900, 1700, 1000, 1300, 1600, 950, 1400, 1800];
double[] ages = [3, 12, 24];
for (int k = 1; k <= 13; k++)
{
    double a = .25 + .03 * k, c = .9 * a + noise[k - 1], d = .85 - .02 * k + noise[13 - k] / 2;
    if (k == 1)
    {
        Unit("D1", "S1_F1", "S1", "S1", 3, a, b1[0], c, d, 600);           // S4: two fractions of sample S1
        Unit("D1", "S1_F2", "S1", "S1", 3, a, b1[0], c, d, 400);
    }
    else Unit("D1", $"S{k}", $"S{k}", $"S{k}", ages[k % 3], a, b1[k - 1], c, d, p1[k - 1]);
}
Unit("D1", "S14_1", "S14_1", "S14", 3, .30, .15, .28, .88, 1300);         // S4: two injections of animal S14, kept apart
Unit("D1", "S14_2", "S14_2", "S14", 3, .32, .12, .30, .86, 0);            // D40: no target PSM, so no depth
// D2: 12 runs, depth rising with the run. P1 S13 and P2 S5 both follow a U in depth, which a linear partial on depth's
// ranks cannot take out; the null keeps each site's U, so it should find calls too (depth-like).
double[] b2 = [.40, .10, .50, .20, .60, .30, .70, .25, .45, .15, .55, .35];
for (int i = 0; i < 12; i++)
{
    double u = Math.Pow(2.0 * i / 11 - 1, 2);
    Unit("D2", $"R{i + 1}", $"R{i + 1}", $"R{i + 1}", double.NaN, .2 + .4 * u + noise[i] / 2, b2[i], .15 + .35 * u - noise[11 - i] / 2, .8, 500 + 150 * i);
}
for (int i = 0; i < 4; i++) Unit("D3", $"Q{i + 1}", $"Q{i + 1}", $"Q{i + 1}", double.NaN, .5 + .1 * i, .3, .4, .8, 1000);   // S3: phospho-enriched
// v4: each sample's sex, a categorical covariate of the site-trait fit (D1, D4).
var sexOf = runs.Where(r => r.ds == "D1").ToDictionary(r => r.run, r => int.Parse(r.replicate[1..], inv) % 2 == 1 ? "F" : "M");
// D4: animals A1-A12, A1-A4 injected twice; age by k % 3 and sex by k % 2, so each age and sex pair holds two animals.
var d4Runs = new List<(string run, int k, bool second)>();
for (int k = 1; k <= 12; k++)
    for (int inj = 1; inj <= (k <= 4 ? 2 : 1); inj++)
    {
        string run = k <= 4 ? $"A{k}_{inj}" : $"A{k}";
        d4Runs.Add((run, k, inj == 2));
        Unit("D4", run, run, $"A{k}", ages[k % 3], 0, 0, 0, 0, 700 + 60 * ((5 * k + 3 * inj) % 11));
        sexOf[run] = k % 2 == 1 ? "F" : "M";
    }

// Peptidoform observations per raw run; fraction S1_F2 carries 40 % of the sample's signal. v3's one-form cells come
// from forms left out (absent) or identified without an intensity (NaN), per run.
var obs = new List<(string ds, PeptidoformObservation o)>();
foreach (var (ds, run, _, _, _) in runs.Where(r => r.ds != "D4"))
{
    var (a, b, c, d) = level[run];
    double T = run == "S1_F2" ? 400 : run == "S1_F1" ? 600 : 1000;
    bool p2Covered = run is "S3" or "R3", p3Ceiling = run is "S4" or "R5", p1Floor = run == "S5", p2Both = run == "S6", p2ModOnly = run == "S7";
    void Add(string full, string prot, int start, double intensity, char? prev = null) =>
        obs.Add((ds, new PeptidoformObservation(run, full, prot, start, start + MzLibUtil.ClassExtensions.GetBaseSequenceFromFullSequence(full).Length - 1, intensity, prev)));
    Add($"PEPS[{Ph}]KAAK[{GG}]LR", "P1", 10, p1Floor ? double.NaN : T * a * b);   // P1 S13 and K17 on one molecule (type P)
    Add($"PEPS[{Ph}]KAAKLR", "P1", 10, p1Floor ? double.NaN : T * a * (1 - b));
    Add($"PEPSKAAK[{GG}]LR", "P1", 10, T * (1 - a) * b);
    Add("PEPSKAAKLR", "P1", 10, T * (1 - a) * (1 - b));
    Add($"MTK[{Formyl}]R", "P1", 30, .10 * T);                          // S2: on the exclusion list
    Add($"M[{Ox}]TKR", "P1", 30, .05 * T);                              // S2: Common Variable
    Add("MTKR", "P1", 30, .85 * T);
    if (!p2Covered) Add($"LLS[{PhU}]GEK", "P2", 3, p2Both || p2ModOnly ? double.NaN : .5 * T * c);   // P2 S5, co-varies with P1 S13
    if (!p2ModOnly) Add("LLSGEK", "P2", 3, p2Both ? double.NaN : .5 * T * (1 - c));
    Add($"[{Ac}]AGVKR", "P3", 2, .5 * T * d, 'M');                      // S5: protein N-terminus after Met removal
    if (!p3Ceiling) Add("AGVKR", "P3", 2, .5 * T * (1 - d), 'M');
    Add($"AD[{Sumo}]K", "P4", 7, .05 * T);                              // S2: AspN Digested on D, refused (mzLib #1431)
    Add("ADK", "P4", 7, .95 * T);
}
// D4's ten sites, one peptide each (LGS<x>R, the S at position start + 2), two per protein R1-R5. Log odds per animal and
// run: the site's level, an age slope (sites 0-2), a sex shift, the animal's own offset and run noise. One-form runs: a
// ceiling (unmodified form absent), a covered zero (modified absent), a floor (modified identified without intensity), a
// count-only cell (neither quantified).
var d4Rng = new Random(20261125);
var d4Animal = Enumerable.Range(0, 13).Select(_ => Enumerable.Range(0, 10).Select(_ => d4Rng.NextDouble() - .5).ToArray()).ToArray();
string[] d4Tails = ["A", "D", "E", "F", "G", "H", "I", "L", "N", "Q"];
for (int r = 0; r < d4Runs.Count; r++)
{
    var (run, k, second) = d4Runs[r];
    for (int i = 0; i < 10; i++)
    {
        double logit = -1.2 + .25 * i + (i < 3 ? .9 : .05) * (ages[k % 3] - 12) / 12 + (sexOf[run] == "M" ? .3 : 0) + .6 * d4Animal[k][i]
            + .3 * (d4Rng.NextDouble() - .5);
        double f = 1 / (1 + Math.Exp(-logit));
        if (i == 8 && second) continue;                                                 // D88: never in a second injection
        bool ceiling = (i == 9 && second) || ((r + i) % 7 == 3 && f > .5);             // D87: site 9 only modified there
        bool coveredZero = !ceiling && (r + 2 * i) % 9 == 4 && f < .5;
        bool floor = !ceiling && !coveredZero && (3 * r + i) % 11 == 5;
        bool countOnly = !ceiling && !coveredZero && !floor && (r + 5 * i) % 13 == 6;
        string prot = $"R{i / 2 + 1}", tail = d4Tails[i];
        int start = 10 + 20 * (i % 2);
        void Add4(string full, double intensity) =>
            obs.Add(("D4", new PeptidoformObservation(run, full, prot, start, start + MzLibUtil.ClassExtensions.GetBaseSequenceFromFullSequence(full).Length - 1, intensity, null)));
        if (!coveredZero) Add4($"LGS[{Ph}]{tail}R", floor || countOnly ? double.NaN : 1000 * f);
        if (!ceiling) Add4($"LGS{tail}R", countOnly ? double.NaN : 1000 * (1 - f));
    }
}

// Stored occupancy (stand-in for MetaMorpheus's IntensityOccupancy_ cells): per raw run, every site, named as the
// catalog names them (IdWithMotif, no category), fraction to 4 dp, intensities to 4 significant figures. A covered site
// never seen modified is written only by 1.1.13 on (#2889), as covered_zero; up to 1.1.12 MetaMorpheus writes nothing.
static double Sig4(double v) => v == 0 ? 0 : Math.Round(v, 3 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
string Idm(string mod) => mod[(mod.IndexOf(':') + 1)..];
bool WritesCoveredZero(string ds) => Version.Parse(release[ds]) >= new Version(1, 1, 13);
string State(OccupancyState s) => s switch { OccupancyState.CountOnly => "count_only", OccupancyState.NotDetected => "covered_zero", _ => s.ToString().ToLowerInvariant() };
var stored = new List<(string ds, SiteRunOccupancy cell)>();
foreach (var g in obs.GroupBy(x => x.ds))
    foreach (var c in SiteOccupancyCalculator.Calculate(g.Select(x => x.o)))
        if (c.State != OccupancyState.NotDetected || WritesCoveredZero(g.Key))
            stored.Add((g.Key, c));
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "occupancy.tsv")))
{
    w.WriteLine("dataset\trun\tprotein\tposition\tresidue\tsite_type\tmodification\tstate\tfraction\tintensity_modified\tintensity_total\tunmodified_quantified");
    foreach (var (ds, c) in stored)
        w.WriteLine($"{ds}\t{c.Run}\t{c.Site.ProteinAccession}\t{c.Site.Position}\t{c.Site.Residue}\t{(c.Site.ProteinAccession == "P3" && c.Site.Position == 2 ? "protein_n_term" : "residue")}\t{Idm(c.Site.Modification)}\t{State(c.State)}\t{F(Math.Round(c.Fraction, 4))}\t{F(Sig4(c.ModifiedIntensity))}\t{F(Sig4(c.CoveringIntensity))}\t{c.UnmodifiedQuantified}");
}
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "observations.tsv")))
{
    w.WriteLine("dataset\trun\tfull_sequence\tprotein\tstart\tend\tintensity\tprevious_residue");
    foreach (var (ds, o) in obs) w.WriteLine($"{ds}\t{o.Run}\t{o.FullSequence}\t{o.ProteinAccession}\t{o.StartResidue}\t{o.EndResidue}\t{F(o.Intensity)}\t{o.PreviousResidue}");
}
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "runs.tsv")))
{
    w.WriteLine("dataset\tspecies\trun\tcombine_to\treplicate\tage_months\tsex");
    foreach (var r in runs) w.WriteLine($"{r.ds}\tmouse\t{r.run}\t{r.combineTo}\t{r.replicate}\t{F(r.age)}\t{sexOf.GetValueOrDefault(r.run, "")}");
}
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "depth.tsv")))
{
    w.WriteLine("dataset\trun\ttarget_psms");
    foreach (var r in runs.Where(r => psms.ContainsKey(r.run))) w.WriteLine($"{r.ds}\t{r.run}\t{psms[r.run]}");
}
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "datasets.tsv")))
{
    w.WriteLine("dataset\tsearch_engine_version");
    foreach (var (ds, v) in release) w.WriteLine($"{ds}\t{v}");
}
File.WriteAllText(Path.Combine(outDir, "inputs", "enrichment.tsv"), "dataset\tenriched_ptm\nD3\tphospho\n");
using (var w = new StreamWriter(Path.Combine(outDir, "inputs", "unimod.tsv")))
{
    w.WriteLine("id_with_motif\tunimod");
    foreach (var (k, v) in unimod) w.WriteLine($"{k}\t{v}");
}

// ---- the definitions, applied. Read the stored cells back exactly as written (4 dp fraction is the ReportedFraction).
// Names are read by bracket depth, as mzLib's GetModificationDictionaryFromFullSequence does, so a name that holds
// brackets ("Metal:Cu[I] on D") stays whole. The category is everything before the first ':'.
IEnumerable<string> ModNames(string fullSequence)
{
    int depth = 0, start = 0;
    for (int i = 0; i < fullSequence.Length; i++)
    {
        if (fullSequence[i] == '[' && depth++ == 0) start = i + 1;
        else if (fullSequence[i] == ']' && --depth == 0) yield return fullSequence[start..i];
    }
}
var categoryOf = obs.SelectMany(x => ModNames(x.o.FullSequence)).Where(n => n.Contains(':'))
    .GroupBy(n => n[(n.IndexOf(':') + 1)..]).ToDictionary(g => g.Key, g => g.First()[..g.First().IndexOf(':')]);
bool Biological(string mod)     // S2, on Category:IdWithMotif
{
    string cat = mod[..mod.IndexOf(':')];
    if (!biologicalCategories.Contains(cat) || excluded.Contains(mod)) return false;
    return !(cat == "AspN Digested" && mod.EndsWith(" on D"));
}
var enriched = new HashSet<string> { "D3" };
var combineTo = runs.ToDictionary(r => r.run, r => r.combineTo);
string Key(ModificationSite s, bool canonical)   // S5
{
    string mod = canonical ? unimod.GetValueOrDefault(Idm(s.Modification), Idm(s.Modification)) : s.Modification;
    bool nterm = s.ProteinAccession == "P3" && s.Position == 2;      // from site_type = protein_n_term
    return nterm ? $"{s.ProteinAccession}:@protein_n_term:{mod}" : $"{s.ProteinAccession}:{s.Residue}{s.Position}:{mod}";
}

var readBack = File.ReadLines(Path.Combine(outDir, "inputs", "occupancy.tsv")).Skip(1).Select(l => l.Split('\t')).Select(f =>
{
    var state = f[7] switch { "quantified" => OccupancyState.Quantified, "floor" => OccupancyState.Floor, "count_only" => OccupancyState.CountOnly,
        "covered_zero" => OccupancyState.NotDetected, var other => throw new InvalidDataException(other) };
    var site = new ModificationSite(f[2], int.Parse(f[3], inv), f[4][0], $"{categoryOf[f[6]]}:{f[6]}");
    double D(string v) => v.Length == 0 ? double.NaN : double.Parse(v, inv);
    return (ds: f[0], covered: f[7] == "covered_zero", cell: new SiteRunOccupancy(site, f[1], state, D(f[9]), D(f[10]), bool.Parse(f[11]))
        { ReportedFraction = state == OccupancyState.Quantified ? D(f[8]) : null });
}).Where(x => Biological(x.cell.Site.Modification)).ToList();

// Type A's cells (v3, S1), which the site-trait fit reads too (v4): the stored ones, a stored covered-zero a bound; up to 1.1.12 the engine's covered-zeros where
// nothing is stored; every count-only cell with the engine's peptidoform counts. Matched on accession, position and
// IdWithMotif. Per run, before S4 combines fractions.
List<SiteRunOccupancy> TypeACells(string ds)
{
    var cells = readBack.Where(x => x.ds == ds).Select(x => x.cell).ToList();
    var siteOf = cells.Select(c => c.Site).Distinct().ToDictionary(s => (s.ProteinAccession, s.Position, Idm(s.Modification)));
    var storedAt = cells.Select(c => (c.Site, c.Run)).ToHashSet();
    var counts = new Dictionary<(ModificationSite, string), (int?, int?)>();
    var every = new Dictionary<(ModificationSite, string), (int?, int?)>();       // v4: every cell's counts, for the site-trait fit
    var added = new List<SiteRunOccupancy>();
    foreach (var e in SiteOccupancyCalculator.Calculate(obs.Where(x => x.ds == ds).Select(x => x.o), Biological))
    {
        if (!siteOf.TryGetValue((e.Site.ProteinAccession, e.Site.Position, Idm(e.Site.Modification)), out var site)) continue;
        every[(site, e.Run)] = (e.CoveringPeptidoforms, e.ModifiedPeptidoforms);
        if (e.State != OccupancyState.NotDetected) counts[(site, e.Run)] = (e.CoveringPeptidoforms, e.ModifiedPeptidoforms);
        else if (!WritesCoveredZero(ds) && !storedAt.Contains((site, e.Run))) added.Add(e with { Site = site });
    }
    return cells.Select(c => (c.State == OccupancyState.CountOnly ? counts : every).TryGetValue((c.Site, c.Run), out var k)
        ? c with { CoveringPeptidoforms = k.Item1, ModifiedPeptidoforms = k.Item2 } : c).Concat(added).ToList();
}

// D40: each sample's depth, log2 of the target PSMs over all its runs.
Dictionary<string, double> DepthOf(string ds) => runs.Where(r => r.ds == ds && psms.ContainsKey(r.run)).GroupBy(r => r.combineTo)
    .ToDictionary(g => g.Key, g => Math.Log2(g.Sum(r => psms[r.run])));

IReadOnlyList<PtmPair> TypeA(IReadOnlyList<SiteRunOccupancy> occ, IReadOnlyList<PeptidoformObservation> o, Dictionary<string, double> depth) =>
    PtmPairEngine.CoVarying(occ, o, minQuantifiedFraction: 0.7, includeBounds: true, runCovariate: depth);
long Calls(IEnumerable<PtmPair> pairs) => pairs.LongCount(p => p.TestStatus == PairTestStatus.Tested && !p.Overlapping && p.Q <= 0.1);
static int Seed(params long[] parts) { long h = 1469598103; foreach (var p in parts) h = unchecked(h * 1099511 + p); return (int)(h & 0x7fffffff); }
static long Text(string s) { long h = 0; foreach (char c in s) h = unchecked(h * 31 + c); return h; }

// D78: each site's sample cells permuted within blocks of 3 samples adjacent in depth (no depth first, then by name),
// 3 replicates, seed 20261110; depth-like when the null's calls per replicate reach 10 % of the real ones.
(long Real, long[] Null, double Share, bool Like) DepthNull(string ds, IReadOnlyList<SiteRunOccupancy> occ, IReadOnlyList<PeptidoformObservation> o,
    Dictionary<string, double> depth, IReadOnlyList<PtmPair> real)
{
    var samples = occ.Select(c => c.Run).Distinct().OrderBy(s => depth.TryGetValue(s, out var d) ? d : double.NaN).ThenBy(s => s, StringComparer.Ordinal).ToArray();
    var index = samples.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => t.i);
    var nulls = new long[3];
    for (int rep = 1; rep <= 3; rep++)
    {
        var rng = new Random(Seed(20261110, Text(ds), 3, rep));
        var permuted = new List<SiteRunOccupancy>();
        foreach (var site in occ.GroupBy(c => c.Site))
        {
            var perm = Enumerable.Range(0, samples.Length).ToArray();
            for (int start = 0; start < samples.Length; start += 3)
            {
                int end = Math.Min(samples.Length, start + 3);
                for (int i = end - 1; i > start; i--) { int j = rng.Next(start, i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
            }
            foreach (var c in site) permuted.Add(c with { Run = samples[perm[index[c.Run]]] });
        }
        nulls[rep - 1] = Calls(TypeA(permuted, o, depth));
    }
    long realCalls = Calls(real);
    double share = realCalls > 0 ? nulls.Average() / realCalls : double.NaN;
    return (realCalls, nulls, share, share >= 0.10);
}

var pairRows = new List<string>();
var traitRows = new List<string>();
var nullRows = new List<string>();
var pooledInput = new List<(string Scope, PtmPair Pair)>();
var depthLike = new HashSet<string>();
string Flags(bool overlapping, bool isA, bool like) =>
    "[" + string.Join(",", new[] { overlapping || !isA ? "overlapping" : null, isA && like ? "depth_like" : null }.OfType<string>().Select(x => $"\"{x}\"")) + "]";
string Row(string type, string scope, string ka, string kb, string pa, string pb, bool same, string flags, string sign, string stat,
    double value, string n, double p, double q, string family, string datasets = "", string nDatasets = "", string agreeing = "") =>
    string.Join('\t', type, scope, ka, kb, pa, pb, same, flags, "biological x biological",
        "", "", sign, stat, F(value), n, F(p), F(q), family, "ptm_site_pair", datasets, nDatasets, agreeing, PairDefinition);
string Family(bool same) => $"A.{(same ? "intra" : "inter")}_protein.biological x biological";

foreach (var ds in runs.Select(r => r.ds).Distinct())
{
    var dsObs = SiteOccupancyCalculator.CombineObservations(obs.Where(x => x.ds == ds).Select(x => x.o), combineTo);        // S4
    var physical = PtmPairEngine.Physical(dsObs, Biological);
    var rowsOut = physical.Select(p => (p, isA: false)).ToList();
    bool like = false;
    if (!enriched.Contains(ds))                                                                                            // S3
    {
        var occ = SiteOccupancyCalculator.CombineRuns(TypeACells(ds), combineTo);                                          // S4
        var depth = DepthOf(ds);
        var typeA = TypeA(occ, dsObs, depth);
        var n = DepthNull(ds, occ, dsObs, depth, typeA);
        like = n.Like;
        if (like) depthLike.Add(ds);
        nullRows.Add(string.Join('\t', ds, n.Real, string.Join(",", n.Null), F(n.Share), n.Like));
        rowsOut.AddRange(typeA.Select(p => (p, isA: true)));
        if (ds is "D1" or "D4")
        {
            // DEF-SITE-TRAIT v3 reads type A's cells (bounds) as counts (Sampling), with each sample's depth in the
            // detection link; a sample with no depth is left out (D1's S14_2). Sex is coded against its first level.
            var samples = runs.Where(r => r.ds == ds).GroupBy(r => r.combineTo).Where(g => depth.ContainsKey(g.Key)).ToList();
            var trait = samples.ToDictionary(g => g.Key, g => g.First().age);
            var replicate = samples.ToDictionary(g => g.Key, g => g.First().replicate);
            var sexes = samples.Select(g => sexOf[g.First().run]).Distinct().Order(StringComparer.Ordinal).ToList();
            var terms = sexes.Skip(1).Select(l => $"sex={l}").ToList();
            var covariates = samples.ToDictionary(g => g.Key, g => sexes.Skip(1).Select(l => sexOf[g.First().run] == l ? 1.0 : 0.0).ToArray());
            var options = new SiteTraitOptions { Scale = OccupancyScale.Log2Odds, BoundCells = BoundCellModel.Sampling, RunDepth = depth };
            foreach (var e in SiteTraitEffects.Fit(occ, trait, replicate, covariates, terms, options))
            {
                bool fitted = e.Status == "Fitted";
                traitRows.Add(string.Join('\t', "ptm_site", Key(e.Site, false), Key(e.Site, true), e.Site.ProteinAccession, "age_months",
                    "log2_odds_occupancy", ds, "", "", "", F(e.Effect), F(e.StandardError), F(e.PValue), F(e.PValue), F(e.Q),
                    "site_trait.intensity", e.Runs, false, fitted, "biological", "1", TraitDefinition,
                    e.Status, F(e.DegreesOfFreedom), e.Replicates, F(e.MedianFraction), F(e.ReplicateVariance), F(e.ResidualVariance),
                    string.Join(";", terms), string.Join(";", e.CovariateEffects.Select(F)), e.ResidualVarianceBorrowed, e.CountedRuns));
            }
        }
    }
    foreach (var (p, isA) in rowsOut)
    {
        var (ka, kb) = (Key(p.SiteA, false), Key(p.SiteB, false));
        if (string.CompareOrdinal(ka, kb) > 0) (ka, kb) = (kb, ka);
        pairRows.Add(Row(isA ? "A" : "P", ds, ka, kb, p.SiteA.ProteinAccession, p.SiteB.ProteinAccession, p.SameProtein, Flags(p.Overlapping, isA, like),
            isA && double.IsFinite(p.Statistic) ? (p.Statistic >= 0 ? "+" : "-") : "", isA ? "spearman_rho" : "median_co_occupancy", p.Statistic, p.N.ToString(inv),
            isA ? p.PValue : double.NaN, isA ? p.Q : double.NaN, isA ? Family(p.SameProtein) : ""));
        pooledInput.Add((ds, p));
    }
}
foreach (var g in GlobalPairEngine.Combine(pooledInput, s => Key(s, true), minScopes: 2))
{
    bool isA = g.ResultType == PairResultType.A;
    pairRows.Add(Row(isA ? "A" : "P", "meta:mouse", g.SiteA, g.SiteB, g.ProteinA, g.ProteinB, g.SameProtein, Flags(false, isA, g.Scopes.Any(depthLike.Contains)),
        isA && double.IsFinite(g.CombinedZ) ? (g.CombinedZ >= 0 ? "+" : "-") : "", isA ? "stouffer_z" : "median_co_occupancy", isA ? g.CombinedZ : g.Statistic, "",
        isA ? g.PValue : double.NaN, isA ? g.Q : double.NaN, isA ? Family(g.SameProtein) : "",
        "[" + string.Join(",", g.Scopes.Select(s => $"\"{s}\"")) + "]", g.Scopes.Count.ToString(inv), g.ScopesAgreeing.ToString(inv)));
}

File.WriteAllLines(Path.Combine(outDir, "expected", "ptm_pairs.tsv"),
    new[] { "result_type\tscope\tfeature_key_a\tfeature_key_b\tprotein_accessions_a\tprotein_accessions_b\tsame_protein\tflags\tclass_pair\ttrait_id\tstratum\tsign\tstatistic\tvalue\tn\tp\tq\tfdr_family\tfeature_type\tdatasets\tn_datasets\tn_datasets_agreeing\tdefinition_id" }
    .Concat(pairRows));
File.WriteAllLines(Path.Combine(outDir, "expected", "trait_effects.tsv"),
    new[] { "feature_type\tfeature_key\tfeature_key_unimod\tprotein_accessions\ttrait_id\tresponse\tscope\tbeta_detect\tse_detect\tp_detect\tbeta_int\tse_int\tp_int\tp_combined\tq\tfdr_family\tn_detected\testimable_detect\testimable_int\tmod_class\tambiguity_level\tdefinition_id\tstatus\tdf\treplicates\tmedian_fraction\treplicate_variance\tresidual_variance\tcovariates\tcovariate_effects\tresidual_variance_borrowed\tcounted_runs" }
    .Concat(traitRows));
File.WriteAllLines(Path.Combine(outDir, "expected", "depth_null.tsv"),
    new[] { "dataset\treal_calls\tnull_calls\tshare\tdepth_like" }.Concat(nullRows));
Console.WriteLine($"wrote {pairRows.Count} ptm_pairs rows, {traitRows.Count} trait_effects rows, {nullRows.Count} depth_null rows to {outDir}");
