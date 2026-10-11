using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PXReprise.Discovery;

/// <summary>
/// What experiment a deposit is (D30, D32): every fact the pipeline knows before the full download, each with its value,
/// its source, its evidence and whether it is a guess, per deposit and, where the probe files tell, per run. Written at
/// every exit of the probe stage, a parked deposit included, as <c>01_experiment/experiment.json</c> (for machines) and
/// <c>experiment_notes.md</c> (for people). It records; it decides nothing (PLAN_search-settings.md slice 2).
/// </summary>
public sealed class ExperimentRecord
{
    public const string Folder = "01_experiment";
    public const string JsonFile = "experiment.json";
    public const string NotesFile = "experiment_notes.md";

    private readonly string _accession;
    private readonly JsonObject _facts = new();
    private readonly JsonObject _runs = new();

    public ExperimentRecord(string accession) => _accession = accession;

    /// <summary>One fact. A later call for the same name replaces it (a better source has spoken).</summary>
    public void Fact(string name, string? value, string source, string? evidence = null, bool guessed = false) =>
        _facts[name] = new JsonObject { ["value"] = value, ["source"] = source, ["evidence"] = evidence, ["guessed"] = guessed };

    /// <summary>The species the deposit is searched against, and where that came from.</summary>
    public void Species(string organism, string source) => Fact("species", organism, source);

    /// <summary>What the screen read from the PRIDE record's text and instrument list.</summary>
    public void Screen(Acquisition a)
    {
        const string source = "pride_record_text";
        Fact("acquisition", a.Mode.ToString(), source, a.Evidence);
        Fact("labelling", a.Labelling.ToString(), source, a.Evidence);
        Fact("instrument", a.Instrument.ToString(), "pride_record_instruments");
        Fact("enrichment", a.Enrichment, source, a.Enrichment == "none" ? null : a.Evidence);
        if (a.Crosslinked) Fact("crosslinked", "yes", source, a.Evidence);
        if (a.NonspecificCleavage) Fact("nonspecific_cleavage", "yes", source, a.Evidence);
        if (a.TopDown) Fact("top_down", "yes", source, a.Evidence);
    }

    /// <summary>The deposit's chemistry (G19): protease, cysteine chemistry and label, each from its own source.</summary>
    public void Chemistry(DepositChemistry c)
    {
        Fact("protease", c.Protease.Value, c.Protease.Source, c.Protease.Evidence, c.Protease.Guessed);
        if (c.ProteasePerFile is { } perFile)
            foreach (var (file, protease) in perFile) Run(file)["protease"] = protease;
        Fact("alkylation", c.Alkylation.Value, c.Alkylation.Source, c.Alkylation.Evidence, c.Alkylation.Guessed);
        Fact("cysteine_mods", string.Join(" + ", c.CysMods.Select(m => $"{m.Name} ({(m.Fixed ? "fixed" : "variable")})")), c.Alkylation.Source);
        Fact("label", c.Label.Value, c.Label.Source, c.Label.Evidence, c.Label.Guessed);
    }

    /// <summary>What the probe files' own scans say (measured: they win over text, D34).</summary>
    public void Probe(JsonObject qcReport)
    {
        foreach (var (file, node) in qcReport)
        {
            if (node is not JsonObject r) continue;
            var run = Run(file);
            run["probe"] = true;
            if (r["ms2_analyzer_dissociation"] is JsonObject pairs) run["ms2_analyzer_dissociation"] = pairs.DeepClone();
            if (r["fragmentation"] is JsonObject fr) run["fragmentation"] = fr.DeepClone();
            if (r["isobaric_reporters"] is JsonObject iso) run["isobaric_reporters"] = iso.DeepClone();
            if (r["instrument_model"] is JsonNode im) run["instrument_model"] = im.DeepClone();
            run["qc_pass"] = r["pass"]?.DeepClone();
        }
    }

    /// <summary>The probe search's measurements per run (D34: measured facts), keyed by the raw file's name.</summary>
    public void ProbeSearch(IReadOnlyDictionary<string, Qc.RunMeasures> runs)
    {
        foreach (var (stem, m) in runs)
        {
            var run = Run(_runs.Select(kv => kv.Key).FirstOrDefault(k => Path.GetFileNameWithoutExtension(k) == stem) ?? stem);
            run["probe_search"] = new JsonObject
            {
                ["psms_1pct"] = m.Psms,
                ["species_psms"] = new JsonObject(m.SpeciesPsms.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["shared_species_psms"] = m.SharedSpeciesPsms,
                ["peptides"] = m.Peptides,
                ["cysteine_share"] = m.CysteineShare is { } c ? Math.Round(c, 4) : null,
                ["phospho_share"] = m.PhosphoShare is { } p ? Math.Round(p, 4) : null,
                ["gg_share"] = m.GgShare is { } g ? Math.Round(g, 4) : null,
            };
        }
    }

    private JsonObject Run(string file)
    {
        if (_runs[file] is not JsonObject o) _runs[file] = o = new JsonObject();
        return o;
    }

    /// <summary>
    /// Writes both files into <c>&lt;runDir&gt;/01_experiment</c>. <paramref name="outcome"/> is the deposit's status after
    /// the probe stage; <paramref name="decision"/> is set when a person must decide (D34).
    /// </summary>
    public void Write(string runDir, string outcome, string? detail, DecisionsNeeded.Decision? decision)
    {
        string dir = Path.Combine(runDir, Folder);
        Directory.CreateDirectory(dir);
        var json = new JsonObject
        {
            ["schema"] = "pxreprise-experiment/1",
            ["accession"] = _accession,
            ["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["outcome"] = outcome,
            ["detail"] = detail,
            ["facts"] = _facts.DeepClone(),
            ["runs"] = _runs.DeepClone(),
            ["decision_needed"] = decision is null ? null : new JsonObject { ["question"] = decision.Question, ["choices"] = decision.Choices },
        };
        File.WriteAllText(Path.Combine(dir, JsonFile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, NotesFile), Notes(outcome, detail, decision));
    }

    /// <summary>The same record for people: every fact with its source, guesses marked, and what must be decided.</summary>
    public string Notes(string outcome, string? detail, DecisionsNeeded.Decision? decision)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {_accession}: experiment notes").AppendLine();
        md.AppendLine("Written by PXReprise at the probe stage, before the full download (D30, D32). Each fact names where it came "
            + "from; a fact marked GUESS was read from free text alone. Measured facts (from the probe files' own scans) win over "
            + "text (D34).").AppendLine();
        md.AppendLine($"**Outcome:** `{outcome}`" + (detail is null ? "" : $": {detail}")).AppendLine();
        if (decision is not null)
            md.AppendLine("## A person must decide").AppendLine().AppendLine(decision.Question).AppendLine()
              .AppendLine($"Choices: {decision.Choices}").AppendLine();
        md.AppendLine("## Facts").AppendLine().AppendLine("| fact | value | source | evidence |").AppendLine("|---|---|---|---|");
        foreach (var (name, node) in _facts)
        {
            var f = node!.AsObject();
            string value = f["value"]?.GetValue<string>() ?? "";
            if (f["guessed"]?.GetValue<bool>() == true) value += " (GUESS)";
            md.AppendLine($"| {name} | {Cell(value)} | {Cell(f["source"]?.GetValue<string>())} | {Cell(Trim(f["evidence"]?.GetValue<string>()))} |");
        }
        if (_runs.Count > 0)
        {
            md.AppendLine().AppendLine("## Runs").AppendLine().AppendLine("| run | protease | MS2 analyzer/fragmentation | written fragmentation | reporter ions |")
              .AppendLine("|---|---|---|---|---|");
            foreach (var (file, node) in _runs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var r = node!.AsObject();
                string pairs = r["ms2_analyzer_dissociation"] is JsonObject p ? string.Join(", ", p.Select(kv => $"{kv.Key} {kv.Value}")) : "";
                string frag = r["fragmentation"] is JsonObject fr
                    ? fr["dissociation_type"]?.GetValue<string>() ?? $"refused: {fr["unsupported"]?.GetValue<string>()}" : "";
                string iso = r["isobaric_reporters"]?["tag"]?.GetValue<string>() ?? "";
                md.AppendLine($"| {Cell(file)} | {Cell(r["protease"]?.GetValue<string>())} | {Cell(pairs)} | {Cell(frag)} | {Cell(iso)} |");
            }
        }
        return md.ToString();
    }

    private static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string? Trim(string? s) => s is { Length: > 200 } ? s[..200] + "..." : s;
}
