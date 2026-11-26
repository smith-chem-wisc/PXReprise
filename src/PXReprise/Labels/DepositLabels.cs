using System.Globalization;
using PXReprise.Discovery;
using PXReprise.Qc;
using PXReprise.Search;

namespace PXReprise.Labels;

/// <summary>A run's sample type (D94), in ptmQtl's words (their D86, DEFINITIONS S3 and S3b).</summary>
public static class SampleType
{
    /// <summary>A normal sample, nothing captured.</summary>
    public const string WholeProteome = "whole_proteome";
    /// <summary>The run was enriched for a modified form; the target names the modification.</summary>
    public const string PtmCapture = "ptm_capture";
    /// <summary>Proteins captured by something other than a modification; the target names the kind.</summary>
    public const string ProteinPulldown = "protein_pulldown";
    /// <summary>Nobody can say, or the record's text and the data disagree.</summary>
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [WholeProteome, PtmCapture, ProteinPulldown, Unknown];
}

/// <summary>Whether a search's cysteine chemistry is the one the lab used (D94; ptmQtl's S2d).</summary>
public static class CysteineVerdict
{
    public const string Matches = "matches";
    /// <summary>The lab's chemistry is not the search's; the reason names the reagent.</summary>
    public const string Mismatch = "mismatch";
    /// <summary>Nobody can say, or what was named and the data disagree.</summary>
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Matches, Mismatch, Unknown];
}

/// <summary>Where a label came from. A label the text and the data agree on carries both.</summary>
public static class LabelSource
{
    /// <summary>The deposit's own words: PRIDE's record, its SDRF, its protocol or its PTM list.</summary>
    public const string RepositoryText = "repository_text";
    /// <summary>PXReprise's measurement of the deposit's spectra.</summary>
    public const string DataCheck = "data_check";
    /// <summary>A person's decision, with who, when and why.</summary>
    public const string Person = "person";
}

/// <summary>
/// A person's decision about one label, kept across reprocessing: it outranks the text and the data.
/// </summary>
/// <param name="Dataset">The dataset id (a split deposit's key, e.g. <c>PXD032900-lfq</c>) or its accession.</param>
/// <param name="Run">For a sample type: the run's file name, its stem or its run id, or <c>*</c> for every run of the
/// dataset. For a cysteine verdict: <c>*</c> (one search per dataset).</param>
/// <param name="Label"><c>sample_type</c> or <c>cysteine</c>.</param>
/// <param name="Value">A <see cref="SampleType"/> or a <see cref="CysteineVerdict"/>.</param>
/// <param name="Target">The modification or pulldown kind of a sample type; the reagent of a cysteine verdict.</param>
public sealed record LabelDecision(string Dataset, string Run, string Label, string Value, string? Target, string By,
    string Date, string Reason)
{
    public const string SampleTypeLabel = "sample_type";
    public const string CysteineLabel = "cysteine";
    public const string EveryRun = "*";
}

/// <summary>What the probe search measured in one run (<c>01_experiment/experiment.json</c>, <see cref="RunMeasures"/>).</summary>
public sealed record RunShares(int Psms, double? Phospho, double? Gg, double? AcetylK, double? Cysteine);

/// <summary>A run's sample type, where it came from, and the evidence behind it.</summary>
/// <param name="RunId">As the bundle names the run: <c>&lt;dataset&gt;:&lt;file stem&gt;</c>.</param>
/// <param name="Target">The captured modification(s), "+"-joined, or the pulldown kind; null for a whole proteome.</param>
/// <param name="Text">The record's enrichment as the screen read it (the screen's vocabulary), and the words.</param>
/// <param name="Data">The probe's measures of this run, or null when the probe did not search it.</param>
public sealed record RunSampleType(string RunId, string File, string Value, string? Target,
    IReadOnlyList<string> Sources, string Reason, string? Text, string? TextEvidence, RunShares? Data,
    LabelDecision? Decision);

/// <summary>A cysteine modification the search assumed.</summary>
public sealed record SearchCysMod(string Name, string Unimod, bool Fixed);

/// <summary>A search's cysteine chemistry: the reagent, what the search assumed, what the data say, and the verdict.</summary>
/// <param name="Reagent">The reagent the lab used, in words (<c>iodoacetamide</c>, <c>NEM</c>, <c>MMTS</c>, ...), or
/// <c>not stated</c>.</param>
/// <param name="ReagentSource">The chemistry step's source for it (<see cref="ChemistrySource"/>).</param>
/// <param name="CysteinePeptideShare">The share of the search's 1% target peptides that contain C.</param>
public sealed record SearchCysteine(string Reagent, string ReagentSource, string? ReagentEvidence,
    IReadOnlyList<SearchCysMod> SearchAssumed, double? CysteinePeptideShare, string Verdict,
    IReadOnlyList<string> Sources, string Reason, LabelDecision? Decision);

/// <summary>
/// The two labels ptmQtl reads in place of its hand lists (their D86, PXR-P25; our D94): each run's sample type and
/// the search's cysteine chemistry. Built from what the pipeline already records, never from the spectra again: the
/// screen's reading of the record's text, the probe search's per-run measures, the chemistry step's alkylation and the
/// search's cysteine-peptide share. A person's decision outranks both. A disagreement between the text and the data is
/// <c>unknown</c>, never the text repeated.
/// </summary>
public sealed record DepositLabels(string Dataset, IReadOnlyList<RunSampleType> Runs, SearchCysteine Cysteine,
    IReadOnlyList<string> Notes)
{
    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public string Schema { get; init; } = "pxreprise-labels/1";
}

/// <summary>The rules that set the labels (D94). Pure: the deposit folder is read by <see cref="DepositLabelReader"/>.</summary>
public static class DepositLabeller
{
    /// <summary>The fewest 1% PSMs a probe run needs before its shares are read (the probe judge's floor).</summary>
    public const int MinCheckedPsms = ProbeJudge.MinIdentifiedPsms;

    /// <summary>The cysteine-peptide share below which a search's cysteines are in doubt (the search's own flag).</summary>
    public const double CysteinePoorBelow = SearchStage.CysteinePoorBelow;

    /// <summary>The captures the probe search can measure (it searches phospho STY, GG K and acetyl K).</summary>
    public static readonly IReadOnlySet<string> Measurable = new HashSet<string>(StringComparer.Ordinal) { "phospho", "ubiquitin_GG", "acetyl" };

    private static readonly IReadOnlySet<string> PtmCaptures = new HashSet<string>(StringComparer.Ordinal) { "phospho", "acetyl", "ubiquitin_GG", "succinyl", "glyco" };
    private static readonly IReadOnlySet<string> Pulldowns = new HashSet<string>(StringComparer.Ordinal) { "immunoprecipitation", "proximity_labelling", "affinity_purification", "chemical_probe" };

    /// <summary>The sample type the record's text gives, from the screen's enrichment (datarepo's Enrichment vocabulary).</summary>
    /// <remarks>As ptmQtl's own engine maps a bundle's <c>runs.enrichment</c> (PtmQtlRules.RunType). A chemical probe is a
    /// pulldown here; the data make it a capture when the run carries a modification (<see cref="SampleTypes"/>).</remarks>
    public static (string Value, string? Target) FromText(string? enrichment) => enrichment switch
    {
        "none" => (SampleType.WholeProteome, null),
        { } e when PtmCaptures.Contains(e) => (SampleType.PtmCapture, e),
        { } e when Pulldowns.Contains(e) => (SampleType.ProteinPulldown, e),
        _ => (SampleType.Unknown, enrichment),
    };

    /// <summary>The modifications a run carries at the probe judge's capture thresholds, in the vocabulary's words.</summary>
    public static IReadOnlyList<string> Captured(RunShares m)
    {
        var o = new List<string>();
        if (m.Phospho >= ProbeJudge.EnrichedPhospho) o.Add("phospho");
        if (m.Gg >= ProbeJudge.EnrichedGg) o.Add("ubiquitin_GG");
        if (m.AcetylK >= ProbeJudge.EnrichedAcetylK) o.Add("acetyl");
        return o;
    }

    /// <summary>Each run's sample type.</summary>
    /// <param name="files">The search's spectra files, by file name.</param>
    /// <param name="text">The screen's enrichment for the deposit (null when it was never screened).</param>
    /// <param name="probe">The probe's measures, by file name; most deposits have three probe runs.</param>
    public static IReadOnlyList<RunSampleType> SampleTypes(string dataset, IReadOnlyList<string> files, string? text,
        string? textEvidence, IReadOnlyDictionary<string, RunShares> probe, IReadOnlyList<LabelDecision> decisions)
    {
        var (textValue, textTarget) = FromText(text);
        string said = text is null ? "the record was never screened" : text == "none" ? "the record's text names no enrichment" : $"the record's text names {text}";
        var labelled = new Dictionary<string, RunSampleType>(StringComparer.Ordinal);
        bool disagreed = false;
        string RunId(string file) => $"{dataset}:{Path.GetFileNameWithoutExtension(file)}";

        foreach (var file in files)
        {
            if (DecisionFor(decisions, dataset, LabelDecision.SampleTypeLabel, file, RunId(file)) is { } d)
            {
                labelled[file] = new(RunId(file), file, d.Value, d.Target, [LabelSource.Person], Decided(d), text, textEvidence,
                    probe.GetValueOrDefault(file), d);
                continue;
            }
            if (probe.GetValueOrDefault(file) is not { } m || m.Psms < MinCheckedPsms) continue;
            var captured = Captured(m);
            string carries = captured.Count == 0 ? "the run carries no capture's share" : $"the run carries {Shares(m, captured)}";
            string? joined = captured.Count == 0 ? null : string.Join("+", captured);
            (string Value, string? Target, string[] Sources, bool Disagree) r = textValue switch
            {
                SampleType.WholeProteome when captured.Count == 0 => (SampleType.WholeProteome, null, [LabelSource.RepositoryText, LabelSource.DataCheck], false),
                SampleType.WholeProteome => (SampleType.Unknown, joined, [LabelSource.RepositoryText, LabelSource.DataCheck], true),
                SampleType.PtmCapture when Measurable.Contains(textTarget!) => captured.Contains(textTarget!)
                    ? (SampleType.PtmCapture, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], false)
                    : (SampleType.Unknown, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], true),
                SampleType.PtmCapture => captured.Count == 0
                    ? (SampleType.PtmCapture, textTarget, [LabelSource.RepositoryText], false)
                    : (SampleType.Unknown, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], true),
                SampleType.ProteinPulldown when captured.Count == 0 => (SampleType.ProteinPulldown, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], false),
                SampleType.ProteinPulldown when textTarget == "chemical_probe" => (SampleType.PtmCapture, joined, [LabelSource.RepositoryText, LabelSource.DataCheck], false),
                SampleType.ProteinPulldown => (SampleType.Unknown, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], true),
                _ when captured.Count > 0 => (SampleType.PtmCapture, joined, [LabelSource.RepositoryText, LabelSource.DataCheck], false),
                _ => (SampleType.Unknown, textTarget, [LabelSource.RepositoryText, LabelSource.DataCheck], false),
            };
            disagreed |= r.Disagree;
            string reason = r switch
            {
                { Disagree: true } => $"{said}, but {carries}: they disagree",
                { Value: SampleType.PtmCapture } when textValue == SampleType.PtmCapture && !Measurable.Contains(textTarget!) => $"{said}, which the probe cannot measure; {carries}",
                { Value: SampleType.PtmCapture } when textTarget == "chemical_probe" => $"{said}, and {carries}: a probe aimed at a modification",
                _ => $"{said}, and {carries}",
            };
            labelled[file] = new(RunId(file), file, r.Value, r.Target, r.Sources, reason, text, textEvidence, m, null);
        }

        // A run the probe did not search has only the text.
        foreach (var file in files.Where(f => !labelled.ContainsKey(f)))
        {
            var m = probe.GetValueOrDefault(file);
            string why = m is null ? "the probe did not search this run" : $"the probe found {m.Psms} PSMs in this run, under {MinCheckedPsms}";
            labelled[file] = new(RunId(file), file, textValue, textTarget, [LabelSource.RepositoryText],
                $"{said}; not checked: {why}", text, textEvidence, m, null);
        }

        // Once a probed run contradicts the text, the text holds for no run of the deposit: a fraction of an enriched
        // sample is enriched too (PXD039172's SCX fractions run 17% phospho falling to 1%), and only a design or a
        // person can say which runs are the capture and which the lysate. Every run without a decision is unknown.
        if (disagreed)
            foreach (var file in files.Where(f => labelled[f].Decision is null && labelled[f].Value != SampleType.Unknown))
                labelled[file] = labelled[file] with
                {
                    Value = SampleType.Unknown,
                    Reason = $"{labelled[file].Reason}; but another run of the deposit contradicts the text, so no run of it takes the text's label",
                };
        return files.Select(f => labelled[f]).ToList();
    }

    /// <summary>The reagent the lab used, in words, from the chemistry step's alkylation fact.</summary>
    /// <remarks>The fact holds the UniMod name the search was given (D23), so iodoacetamide and chloroacetamide both read
    /// Carbamidomethyl; the words it was read from tell them apart.</remarks>
    public static string Reagent(ChemistryFact alkylation)
    {
        if (alkylation.Source == ChemistrySource.Default) return "not stated";
        string words = alkylation.Evidence ?? "";
        bool Says(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(words, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return alkylation.Value switch
        {
            "none" => "none",
            "Carbamidomethyl" when Says(@"chloroacetamide|\bCAA\b") && !Says(@"iodoacetamide|\bIA[AM]\b") => "chloroacetamide",
            "Carbamidomethyl" when Says(@"iodoacetamide|\bIA[AM]\b") && !Says(@"chloroacetamide|\bCAA\b") => "iodoacetamide",
            "Carbamidomethyl" => "iodoacetamide or chloroacetamide",
            "Nethylmaleimide" => "NEM",
            "NEM:2H(5)" => "d5-NEM",
            "Nethylmaleimide + NEM:2H(5)" => "NEM and d5-NEM",
            "Propionamide" => "acrylamide",
            "Methylthio" => "MMTS",
            "Carboxymethyl" => "iodoacetic acid",
            "Pyridylethyl" => "4-vinylpyridine",
            "Ethanolyl" => "iodoethanol",
            var v => v,
        };
    }

    /// <summary>The search's cysteine verdict.</summary>
    /// <param name="alkylation">The chemistry the search was given (its provenance's <c>chemistry.alkylation</c>).</param>
    /// <param name="assumed">The cysteine modifications the search declared.</param>
    /// <param name="share">The search's <c>cysteine_peptide_share</c>, or null when it recorded none.</param>
    public static SearchCysteine Cysteine(string dataset, ChemistryFact alkylation, IReadOnlyList<SearchCysMod> assumed,
        double? share, IReadOnlyList<LabelDecision> decisions)
    {
        string reagent = Reagent(alkylation);
        string reagentSource = alkylation.Source;
        // The question's [exceptions] and a curated SDRF are a person's word; the deposit's own are its text.
        string said = alkylation.Source is ChemistrySource.QuestionException or ChemistrySource.CuratedSdrf ? LabelSource.Person : LabelSource.RepositoryText;
        string mods = assumed.Count == 0 ? "no cysteine modification" : string.Join(", ", assumed.Select(m => $"{m.Name} ({(m.Fixed ? "fixed" : "variable")})"));
        string data = $"{Pct(share)} of its 1% target peptides contain C (whole proteomes run 12-26%)";

        if (DecisionFor(decisions, dataset, LabelDecision.CysteineLabel, null, null) is { } d)
            return new(d.Target ?? reagent, reagentSource, alkylation.Evidence, assumed, share, d.Value, [LabelSource.Person], Decided(d), d);
        // D95, D100: the reagent probe named the reagent from the probe's own spectra, and the search declared what it named
        // (Qc.ReagentProbe), so the data decide, not the 5% share line (ptmQtl PXR-P28).
        if (alkylation.Source == ChemistrySource.ProbeSpectra)
            return new(reagent, reagentSource, alkylation.Evidence, assumed, share, CysteineVerdict.Matches, [LabelSource.DataCheck],
                $"the reagent probe named {reagent} from the probe's own spectra, and the search declares {mods}", null);

        bool nemNamed = reagent.Contains("NEM", StringComparison.Ordinal);
        bool nemDeclared = assumed.Any(m => m.Name is "Nethylmaleimide" or "NEM:2H(5)");
        (string Verdict, string[] Sources, string Reason) v =
            nemNamed && !nemDeclared ? (CysteineVerdict.Mismatch, [said], $"{reagent} named, and the search declares {mods}: NEM not declared")
            : share is null ? (CysteineVerdict.Unknown, [said], $"the search recorded no cysteine-peptide share; it declares {mods}")
            : share >= CysteinePoorBelow ? (CysteineVerdict.Matches, reagent == "not stated" ? [LabelSource.DataCheck] : [said, LabelSource.DataCheck],
                $"{(reagent == "not stated" ? "no reagent stated" : $"{reagent} named")}, the search declares {mods}, and {data}")
            : reagent == "not stated" ? (CysteineVerdict.Mismatch, [LabelSource.DataCheck],
                $"no reagent stated, the search declares {mods}, and only {data}: a chemistry the search did not assume")
            : (CysteineVerdict.Unknown, [said, LabelSource.DataCheck], $"{reagent} named and declared as {mods}, but only {data}: they disagree");
        return new(reagent, reagentSource, alkylation.Evidence, assumed, share, v.Verdict, v.Sources, v.Reason, null);
    }

    /// <summary>The decision for one label of a dataset: one naming the run first, then one for every run.</summary>
    private static LabelDecision? DecisionFor(IReadOnlyList<LabelDecision> decisions, string dataset, string label, string? file, string? runId)
    {
        string accession = dataset.Split('-')[0];
        var mine = decisions.Where(d => d.Label == label && (d.Dataset == dataset || d.Dataset == accession)).ToList();
        if (file is not null)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (mine.FirstOrDefault(d => d.Run == file || d.Run == stem || d.Run == runId) is { } exact) return exact;
        }
        return mine.FirstOrDefault(d => d.Run is LabelDecision.EveryRun or "");
    }

    private static string Decided(LabelDecision d) => $"a person's decision ({d.By}, {d.Date}): {d.Reason}";

    private static string Pct(double? x) => x is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "n/a";

    private static string Shares(RunShares m, IReadOnlyList<string> captured) => string.Join(", ", captured.Select(c => c switch
    {
        "phospho" => $"phospho on {Pct(m.Phospho)} of its PSMs",
        "ubiquitin_GG" => $"GG on {Pct(m.Gg)} of its PSMs",
        _ => $"acetyl-K on {Pct(m.AcetylK)} of its PSMs",
    }));
}
