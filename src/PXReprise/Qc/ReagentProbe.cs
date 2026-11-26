using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MzLibUtil;
using PXReprise.Config;
using PXReprise.Discovery;
using Readers;

namespace PXReprise.Qc;

/// <summary>
/// D95, D100 (G136): the reagent-naming cysteine probe. ptmQtl asked for a definitive call on each search's cysteine
/// chemistry, made from spectra before delivery (their 055, PXR-P29). The user: "add the reagent probe to the worker when
/// the cysteine count is suspicious" (D100). So it runs inside the probe stage, wherever the probe runs (the worker runs
/// nearly all of them), and only when the probe's cysteine share is suspicious (<see cref="SuspiciousCysteineShare"/>).
/// <para>
/// <b>How</b> (the oracle and calibration of 2026-11-25, project <c>results/cysteine-probe-calibration_2026-11-25</c>;
/// nothing in MetaMorpheus or mzLib names the reagent): the probe search again, on the same files, with no fixed cysteine
/// modification and every candidate's offered as a variable one (<see cref="Candidates"/>). Each cysteine of a counted
/// match is one observation of the form at its position, or <c>unmodified</c>. Counted: target matches the search wrote
/// (its identification filter), with one candidate sequence, and one or two cysteines. MetaMorpheus allows two
/// modifications a peptide, and mzLib cuts a peptide's forms at 1,024 silently, so a peptide with more C cannot carry its
/// true forms.
/// </para>
/// <para>
/// <b>The call</b> (<see cref="Call"/>), from the calibration's 13 deposits:
/// <list type="bullet">
/// <item>the reagent is named from the MODIFIED cysteines, by family (NEM, d5-NEM and ring-opened NEM are one reagent;
/// carboxymethyl beside carbamidomethyl is mostly carbamidomethyl plus a deamidation, the isobaric trap);</item>
/// <item>completeness, the unmodified share, is reported apart: a reagent can block only some cysteines (PXD018969: MMTS
/// on 36%, 61% free);</item>
/// <item><see cref="Mixed"/> when no family holds <see cref="MinFamilyShare"/> of the modified ones (PXD007999: DeStreak
/// 56%), <see cref="None"/> when no reagent's form reaches <see cref="MinLeadingShare"/> of every cysteine (PXD015079,
/// PXD035171: mostly free), <see cref="Unknown"/> under <see cref="MinCysteines"/> observations or when the search
/// failed.</item>
/// </list>
/// A <see cref="Reagent"/> call becomes the alkylation the full search declares (<see cref="AlkylationJson"/>,
/// <see cref="Apply"/>), as the probe's label call does (<see cref="ChemistrySource.ProbeSpectra"/>), so a mismatch such as
/// PXD057499's (iodoethanol searched as carbamidomethyl) is never delivered. The other calls change nothing and are
/// reported.
/// </para>
/// </summary>
public static class ReagentProbe
{
    /// <summary>
    /// D100: the probe's pooled share of peptides with C below which its cysteines are suspicious. On the calibration's 13
    /// deposits every one whose call mattered (a reagent the search did not assume, a mixture, an incomplete or absent
    /// alkylation) read 0.3% to 7.6% in its probe, and every one alkylated as its search assumed read 10.3% to 21.2%.
    /// </summary>
    public const double SuspiciousCysteineShare = 0.10;

    /// <summary>Fewer counted cysteines than this give <see cref="Unknown"/>. The calibration's smallest count was 405.</summary>
    public const int MinCysteines = 100;

    /// <summary>A family names the reagent when it holds this share of the modified cysteines: 75% to 99% on every clear
    /// call of the calibration, 56% on its one mixture (PXD007999).</summary>
    public const double MinFamilyShare = 0.60;

    /// <summary>Below this share of every counted cysteine for the leading family, the deposit is <see cref="None"/>: 5% and
    /// 6% on the two unalkylated deposits, 36% on PXD018969, the least complete real reagent.</summary>
    public const double MinLeadingShare = 0.20;

    /// <summary>A called reagent is declared fixed only when this share of the cysteines carries it, else variable: a
    /// fixed modification misses the free cysteines (PXD058611: 21% free).</summary>
    public const double FixedWhenComplete = 0.95;

    /// <summary>A form of the called family is declared when it holds this share of the family (d5-NEM: 17% on PXD001054).</summary>
    public const double MinFormShare = 0.05;

    /// <summary>The reagent probe's time limit, a cap rather than a target: 8 to 38 min on three Orbitrap files, and 38 min
    /// on one ion-trap file, where three ran out of 60 min.</summary>
    public const double Minutes = 300;

    public const string Reagent = "reagent", Mixed = "mixed", None = "none", Unknown = "unknown";

    /// <summary>The deposit-state keys: the whole call, and (for a <see cref="Reagent"/> call) the alkylation it sets.</summary>
    public const string StateKey = "reagent_probe", AlkylationKey = "alkylation_from_spectra";

    public const string UnmodifiedForm = "unmodified";

    /// <summary>A candidate cysteine form: its family (the reagent), its UniMod name and accession, and whether a search
    /// declares it when its family is called (ring-opened NEM stays GPTMD's, as <see cref="ChemistryDetector.RingOpenedNem"/>).</summary>
    public sealed record Candidate(string Form, string Family, string UnimodName, string Unimod, bool Declared)
    {
        public CysMod Variable => new(UnimodName, Unimod, false);
    }

    /// <summary>The ten candidates of the calibration, each once under one name per mass. Every one resolves to a
    /// MetaMorpheus modification on C through its UniMod accession (checked 2026-11-25).</summary>
    public static readonly IReadOnlyList<Candidate> Candidates =
    [
        new("carbamidomethyl", "carbamidomethyl", "Carbamidomethyl", "UNIMOD:4", true),
        new("carboxymethyl", "carboxymethyl", "Carboxymethyl", "UNIMOD:6", true),
        new("NEM", "NEM", "Nethylmaleimide", "UNIMOD:108", true),
        new("NEM, ring-opened", "NEM", "Nethylmaleimide+water", "UNIMOD:320", false),
        new("d5-NEM", "NEM", "NEM:2H(5)", "UNIMOD:776", true),
        new("propionamide", "propionamide", "Propionamide", "UNIMOD:24", true),
        new("methylthio", "methylthio", "Methylthio", "UNIMOD:39", true),
        new("ethanolyl", "ethanolyl", "Ethanolyl", "UNIMOD:278", true),
        new("beta-mercaptoethanol", "beta-mercaptoethanol", "DeStreak", "UNIMOD:303", true),
        new("NBF", "NBF", "NBF", "UNIMOD:2079", true),
    ];

    /// <summary>What the second search counted: each form's cysteines, in total and per run.</summary>
    public sealed record Count(int PsmsRead, int PsmsCounted, int Ambiguous, int MoreThanTwoC,
        IReadOnlyDictionary<string, int> Forms, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PerRun)
    {
        public int Cysteines => Forms.Values.Sum();
        public int Unmodified => Forms.GetValueOrDefault(UnmodifiedForm);
    }

    /// <summary>The call: <see cref="Verdict"/> is <see cref="Reagent"/>, <see cref="Mixed"/>, <see cref="None"/> or
    /// <see cref="Unknown"/>; <see cref="Declare"/> holds what a search declares for a <see cref="Reagent"/> call.</summary>
    public sealed record ReagentCall(string Verdict, string? Family, double? FamilyShare, double? UnmodifiedShare, int Cysteines,
        IReadOnlyList<CysMod> Declare, string Evidence, IReadOnlyList<string>? Files = null, Count? Counted = null);

    /// <summary>The probe's pooled share of peptides with C, over the runs that have any; null when none has.</summary>
    public static double? CysteineShare(IReadOnlyDictionary<string, RunMeasures> runs)
    {
        int peptides = runs.Values.Sum(r => r.Peptides);
        return peptides > 0 ? (double)runs.Values.Sum(r => r.CysteinePeptides) / peptides : null;
    }

    /// <summary>True when the probe's pooled cysteine share is under <see cref="SuspiciousCysteineShare"/>.</summary>
    public static bool IsSuspicious(IReadOnlyDictionary<string, RunMeasures> runs) => CysteineShare(runs) < SuspiciousCysteineShare;

    /// <summary>The second search's chemistry: the deposit's own, with no fixed cysteine modification and every candidate
    /// variable.</summary>
    public static DepositChemistry SearchChemistry(DepositChemistry read) =>
        read with { Alkylation = new("cysteine probe candidates", "cysteine_probe"), CysMods = Candidates.Select(c => c.Variable).ToList() };

    /// <summary>
    /// Runs the reagent probe when <paramref name="runs"/> are suspicious, else returns null. An ion-trap deposit (any probe
    /// file read at low resolution) is searched on one file, the one with the most cysteine peptides. A failed search is an
    /// <see cref="Unknown"/> call, never a failed deposit.
    /// </summary>
    public static async Task<ReagentCall?> RunAsync(
        Func<Profile, Machine, IReadOnlyList<string>, DepositChemistry?, IReadOnlyDictionary<string, FragmentationCall>?, string, CancellationToken, Task<string>> search,
        Profile profile, Machine machine, IReadOnlyList<string> spectra, DepositChemistry? chemistry,
        IReadOnlyDictionary<string, FragmentationCall>? fragmentation, IReadOnlyDictionary<string, RunMeasures> runs, string outDir,
        CancellationToken ct)
    {
        if (chemistry is null || !IsSuspicious(runs)) return null;
        bool ionTrap = fragmentation?.Values.Any(f => f.LowRes) == true;
        var files = ionTrap
            ? spectra.OrderByDescending(s => runs.GetValueOrDefault(Path.GetFileNameWithoutExtension(s))?.CysteinePeptides ?? -1)
                .ThenBy(s => Path.GetFileName(s), StringComparer.Ordinal).Take(1).ToList()
            : spectra.ToList();
        var names = files.Select(f => Path.GetFileName(f)).ToList();
        try
        {
            string psms = await search(profile with { ProbeSearchMinutes = Minutes }, machine, files, SearchChemistry(chemistry), fragmentation, outDir, ct).ConfigureAwait(false);
            var call = Call(CountForms(psms)) with { Files = names };
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "reagent_probe.json"), ToJson(call).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return call;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new ReagentCall(Unknown, null, null, null, 0, [], $"the reagent probe search failed ({ex.GetType().Name}): {ex.Message}", names);
        }
    }

    /// <summary>Counts the form each cysteine of the search's counted matches carries (the class remarks say which count).</summary>
    public static Count CountForms(string allPsms)
    {
        var formOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Candidates)
            if (c.Variable.MetaMorpheusId is { } id) formOf[id.Replace('\t', ':')] = c.Form;
        var keep = new Dictionary<string, Func<string, bool>> { ["Decoy/Contaminant/Target"] = c => c.Trim() == "T" };
        var rows = LightWeightSpectralMatchReader.ReadTsv(allPsms, out var warnings, keep);
        if (warnings.Count > 0) throw new InvalidDataException($"{Path.GetFileName(allPsms)}: mzLib could not read every row ({warnings[0]})");
        var total = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perRun = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        int counted = 0, ambiguous = 0, tooMany = 0;
        foreach (var p in rows)
        {
            if (p.FullSequence.Contains('|') || p.BaseSequence.Contains('|')) { ambiguous++; continue; }
            var cs = Enumerable.Range(1, p.BaseSequence.Length).Where(i => p.BaseSequence[i - 1] == 'C').ToList();
            if (cs.Count == 0) continue;
            if (cs.Count > 2) { tooMany++; continue; }
            counted++;
            var mods = p.FullSequence.ParseModifications();
            var run = perRun.TryGetValue(p.FileName, out var r) ? r : perRun[p.FileName] = new(StringComparer.Ordinal);
            foreach (int i in cs)
            {
                string form = mods.TryGetValue(i, out var m) ? formOf.GetValueOrDefault(m, $"other: {m}") : UnmodifiedForm;
                run[form] = run.GetValueOrDefault(form) + 1;
                total[form] = total.GetValueOrDefault(form) + 1;
            }
        }
        return new Count(rows.Count, counted, ambiguous, tooMany, total,
            perRun.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, int>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>The call from a count (the class remarks give the rules and their evidence).</summary>
    public static ReagentCall Call(Count c)
    {
        int n = c.Cysteines, unmodified = c.Unmodified, modified = n - unmodified;
        double? unmodifiedShare = n > 0 ? (double)unmodified / n : null;
        string tally = $"{n.ToString("N0", CultureInfo.InvariantCulture)} cysteines counted, {Pct(unmodifiedShare)} unmodified";
        if (n < MinCysteines)
            return new(Unknown, null, null, unmodifiedShare, n, [], $"{tally}: fewer than {MinCysteines} to call", Counted: c);

        var familyOf = Candidates.ToDictionary(x => x.Form, x => x.Family, StringComparer.Ordinal);
        var families = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (form, count) in c.Forms)
            if (familyOf.TryGetValue(form, out var family)) families[family] = families.GetValueOrDefault(family) + count;
        // The isobaric trap: carboxymethyl is carbamidomethyl plus a deamidation, so beside carbamidomethyl it is mostly
        // carbamidomethyl misplaced (PXD031782, PXD007035). Only where it leads is it iodoacetic acid.
        if (families.GetValueOrDefault("carbamidomethyl") >= families.GetValueOrDefault("carboxymethyl") && families.Remove("carboxymethyl", out int cm))
            families["carbamidomethyl"] = families.GetValueOrDefault("carbamidomethyl") + cm;
        var lead = families.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).FirstOrDefault();
        string ranked = string.Join(", ", families.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3)
            .Select(kv => $"{kv.Key} {Pct((double)kv.Value / n)}"));
        if (lead.Key is null || lead.Value < MinLeadingShare * n)
            return new(None, null, null, unmodifiedShare, n, [],
                $"{tally}; no reagent's form reaches {Pct(MinLeadingShare)} of them ({(ranked.Length > 0 ? ranked : "none modified")}): mostly free cysteines", Counted: c);

        double share = (double)lead.Value / modified;
        string of = $"{lead.Key} on {lead.Value.ToString("N0", CultureInfo.InvariantCulture)} of {modified.ToString("N0", CultureInfo.InvariantCulture)} modified cysteines ({Pct(share)})";
        if (share < MinFamilyShare)
            return new(Mixed, lead.Key, share, unmodifiedShare, n, [], $"{tally}; no reagent holds {Pct(MinFamilyShare)} of the modified ones: {of}; then {ranked}", Counted: c);

        var forms = Candidates.Where(x => x.Family == lead.Key && x.Declared && c.Forms.GetValueOrDefault(x.Form) >= MinFormShare * lead.Value).ToList();
        if (forms.Count == 0) forms = Candidates.Where(x => x.Family == lead.Key && x.Declared).Take(1).ToList();
        bool isFixed = forms.Count == 1 && 1 - unmodifiedShare >= FixedWhenComplete;
        var declare = forms.Select(x => new CysMod(x.UnimodName, x.Unimod, isFixed)).ToList();
        return new(Reagent, lead.Key, share, unmodifiedShare, n, declare,
            $"{tally}; {of}; declared {string.Join(" + ", declare.Select(m => m.Name))} {(isFixed ? "fixed" : "variable")}", Counted: c);
    }

    /// <summary>The call as the deposit's state keeps it (<see cref="StateKey"/>).</summary>
    public static JsonObject ToJson(ReagentCall call) => new()
    {
        ["verdict"] = call.Verdict,
        ["family"] = call.Family,
        ["family_share"] = call.FamilyShare is { } f ? Math.Round(f, 4) : null,
        ["unmodified_share"] = call.UnmodifiedShare is { } u ? Math.Round(u, 4) : null,
        ["cysteines"] = call.Cysteines,
        ["declare"] = new JsonArray(call.Declare.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["unimod"] = m.Unimod, ["fixed"] = m.Fixed }).ToArray()),
        ["evidence"] = call.Evidence,
        ["files"] = call.Files is null ? null : new JsonArray(call.Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        ["forms"] = call.Counted is null ? null : new JsonObject(call.Counted.Forms.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    };

    /// <summary>
    /// The alkylation a call sets (<see cref="AlkylationKey"/>), or null when it sets none:
    /// <list type="bullet">
    /// <item>a <see cref="Reagent"/> call: the declared forms;</item>
    /// <item>a <see cref="None"/> call where the deposit named no reagent (the default carbamidomethyl): no cysteine
    /// modification (PXD015079). Where it named one, the spectra's "none" is a finding to report, not a call that
    /// overrules the record (the calibration, point 4);</item>
    /// <item><see cref="Mixed"/> and <see cref="Unknown"/>: none.</item>
    /// </list>
    /// The evidence keeps the words' own, so iodoacetamide and chloroacetamide stay told apart.
    /// </summary>
    public static JsonObject? AlkylationJson(ReagentCall call, DepositChemistry? read)
    {
        bool none = call.Verdict == None && read?.Alkylation.Source == ChemistrySource.Default;
        if (!none && (call.Verdict != Reagent || call.Declare.Count == 0)) return null;
        string evidence = $"the reagent probe{(call.Files is { Count: > 0 } files ? $" on {string.Join(", ", files)}" : "")}: {call.Evidence}"
            + (read?.Alkylation is { } was ? $" | the deposit's words: {was.Value} [{was.Source}]{(was.Evidence is { } w ? $" {w}" : "")}" : "");
        return new JsonObject
        {
            ["value"] = none ? "none" : string.Join(" + ", call.Declare.Select(m => m.Name)),
            ["evidence"] = evidence,
            ["cys_mods"] = new JsonArray(call.Declare.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["unimod"] = m.Unimod, ["fixed"] = m.Fixed }).ToArray()),
        };
    }

    /// <summary>
    /// The chemistry with the spectra's alkylation (<see cref="AlkylationJson"/>) in place of the words': it outranks every
    /// source the detector reads, as the probe's label does. Only the question's own exception outranks it, and the caller
    /// applies that after. A wait on an unmodelled thiol reagent goes, since the spectra named a modelled one.
    /// </summary>
    public static DepositChemistry Apply(DepositChemistry chem, JsonNode? alkylation)
    {
        if (alkylation is not JsonObject a || a["value"]?.GetValue<string>() is not { } value || a["cys_mods"] is not JsonArray mods) return chem;
        return chem with
        {
            Alkylation = new(value, ChemistrySource.ProbeSpectra, a["evidence"]?.GetValue<string>()),
            CysMods = mods.Select(m => new CysMod(m!["name"]!.GetValue<string>(), m["unimod"]!.GetValue<string>(), m["fixed"]!.GetValue<bool>())).ToList(),
            Park = chem.Park == "waiting_unknown_alkylant" ? null : chem.Park,
        };
    }

    private static string Pct(double? x) => x is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "n/a";
}
