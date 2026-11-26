using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Discovery;

namespace PXReprise.Labels;

/// <summary>
/// Reads what a deposit's folder already records and sets its labels (<see cref="DepositLabeller"/>, D94): the
/// search's spectra files, chemistry and cysteine-peptide share (<c>04_search/provenance.json</c>), and the screen's
/// enrichment and the probe's per-run measures (<c>01_experiment/experiment.json</c>). It reads no spectra.
/// </summary>
public static class DepositLabelReader
{
    public const string Folder = "07_labels";
    public const string JsonFile = "labels.json";

    /// <summary>The columns of a person's decisions file, in order.</summary>
    public static readonly string[] DecisionColumns = ["dataset", "run", "label", "value", "target", "by", "date", "reason"];

    /// <param name="depositDir">A deposit's folder in a run root; its name is the dataset id.</param>
    /// <exception cref="InvalidDataException">The deposit has no search record, or the record lists no spectra file.</exception>
    public static DepositLabels Read(string depositDir, IReadOnlyList<LabelDecision> decisions)
    {
        string dataset = new DirectoryInfo(depositDir).Name;
        string provFile = Path.Combine(depositDir, "04_search", "provenance.json");
        if (!File.Exists(provFile)) throw new InvalidDataException($"{dataset}: no search record ({provFile}); a deposit is labelled after its search");
        var prov = Parse(provFile);
        var notes = new List<string>();

        var files = (prov["inputs"] as JsonArray ?? new JsonArray())
            .Select(i => i?["path"]?.GetValue<string>()?.Replace('\\', '/'))
            .Select(p => p is null ? null : SpectraFile(p))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new InvalidDataException($"{dataset}: {provFile} lists no spectra file under 02_fetch/spectra");

        // The chemistry the search was given: its own record first, then the chemistry step's, else the default it ran.
        var chem = prov["chemistry"] as JsonObject;
        string chemFile = Path.Combine(depositDir, "01_chemistry", "chemistry.json");
        if (chem?["alkylation"] is null && File.Exists(chemFile))
        {
            chem = Parse(chemFile);
            notes.Add("the search record has no chemistry; read 01_chemistry/chemistry.json");
        }
        ChemistryFact alkylation;
        List<SearchCysMod> assumed;
        if (chem?["alkylation"] is JsonObject a)
        {
            alkylation = new(Str(a["value"]) ?? "Carbamidomethyl", Str(a["source"]) ?? ChemistrySource.Default, Str(a["evidence"]));
            assumed = (chem["cysteine_mods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(m => new SearchCysMod(Str(m["name"]) ?? "", Str(m["unimod"]) ?? "", m["fixed"]?.GetValue<bool>() ?? false)).ToList();
        }
        else
        {
            alkylation = new("Carbamidomethyl", ChemistrySource.Default);
            assumed = [new("Carbamidomethyl", "UNIMOD:4", true)];
            notes.Add("no chemistry recorded: the search ran its profile's default, carbamidomethyl fixed");
        }
        double? share = prov["cysteine_peptide_share"] is JsonValue v && v.TryGetValue<double>(out var s) ? s : null;

        // The screen's text and the probe's measures.
        string? text = null, textEvidence = null;
        var probe = new Dictionary<string, RunShares>(StringComparer.Ordinal);
        string expFile = Path.Combine(depositDir, ExperimentRecord.Folder, ExperimentRecord.JsonFile);
        if (File.Exists(expFile))
        {
            var exp = Parse(expFile);
            text = Str(exp["facts"]?["enrichment"]?["value"]);
            textEvidence = Str(exp["facts"]?["enrichment"]?["evidence"]);
            foreach (var (file, run) in exp["runs"] as JsonObject ?? new JsonObject())
                if (run?["probe_search"] is JsonObject p)
                    probe[file] = new(Int(p["psms_1pct"]), Num(p["phospho_share"]), Num(p["gg_share"]), Num(p["acetyl_k_share"]), Num(p["cysteine_share"]));
        }
        else notes.Add($"no {ExperimentRecord.Folder}/{ExperimentRecord.JsonFile}: the record's text was never screened");
        if (probe.Count == 0) notes.Add("no probe measures: every run is labelled from the text alone");

        return new DepositLabels(dataset,
            DepositLabeller.SampleTypes(dataset, files, text, textEvidence, probe, decisions),
            DepositLabeller.Cysteine(dataset, alkylation, assumed, share, decisions),
            notes);
    }

    /// <summary>Writes <c>labels.json</c> into <paramref name="outDir"/> and returns its path.</summary>
    public static string Write(DepositLabels labels, string outDir)
    {
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, JsonFile);
        File.WriteAllText(file, JsonSerializer.Serialize(labels, Envelope.Json));
        return file;
    }

    /// <summary>
    /// A person's decisions: a TSV with <see cref="DecisionColumns"/>, one decision per row. Blank lines and lines
    /// starting with <c>#</c> are skipped. <c>by</c>, <c>date</c> (yyyy-MM-dd) and <c>reason</c> are required.
    /// </summary>
    /// <exception cref="InvalidDataException">A missing column, an unknown label or value, a missing field, or the same
    /// label of the same run decided twice.</exception>
    public static IReadOnlyList<LabelDecision> LoadDecisions(string path)
    {
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var header = lines[0].Split('\t').Select(h => h.Trim()).ToList();
        var missing = DecisionColumns.Where(c => !header.Contains(c)).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"{path}: no column(s) {string.Join(", ", missing)}");
        var o = new List<LabelDecision>();
        var seen = new HashSet<(string, string, string)>();
        for (int n = 1; n < lines.Length; n++)
        {
            if (lines[n].Trim().Length == 0 || lines[n].StartsWith('#')) continue;
            var cells = lines[n].Split('\t');
            string Cell(string c) => header.IndexOf(c) is var i && i < cells.Length ? cells[i].Trim() : "";
            string where = $"{path} line {n + 1}";
            var d = new LabelDecision(Cell("dataset"), Cell("run") is { Length: > 0 } r ? r : LabelDecision.EveryRun, Cell("label"),
                Cell("value"), Cell("target") is { Length: > 0 } t ? t : null, Cell("by"), Cell("date"), Cell("reason"));
            var allowed = d.Label switch
            {
                LabelDecision.SampleTypeLabel => SampleType.All,
                LabelDecision.CysteineLabel => CysteineVerdict.All,
                _ => throw new InvalidDataException($"{where}: label '{d.Label}' is not {LabelDecision.SampleTypeLabel} or {LabelDecision.CysteineLabel}"),
            };
            if (!allowed.Contains(d.Value)) throw new InvalidDataException($"{where}: {d.Label} '{d.Value}' is not one of {string.Join(", ", allowed)}");
            if (d.Dataset.Length == 0 || d.By.Length == 0 || d.Reason.Length == 0) throw new InvalidDataException($"{where}: dataset, by and reason are required");
            if (!DateOnly.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException($"{where}: date '{d.Date}' is not yyyy-MM-dd");
            if (d.Label == LabelDecision.CysteineLabel && d.Run != LabelDecision.EveryRun)
                throw new InvalidDataException($"{where}: a cysteine decision is the search's, so its run is '{LabelDecision.EveryRun}'");
            if (!seen.Add((d.Dataset, d.Run, d.Label))) throw new InvalidDataException($"{where}: {d.Dataset} {d.Run} {d.Label} is decided twice");
            o.Add(d);
        }
        return o;
    }

    /// <summary>The spectra formats a search reads; the per-file settings (<c>&lt;file&gt;.toml</c>) beside them are not runs.</summary>
    private static readonly string[] SpectraExtensions = [".raw", ".mzml", ".mgf", ".mzxml", ".d"];

    /// <summary>The spectra file a search input names (<c>.../02_fetch/spectra/&lt;file&gt;</c>), or null.</summary>
    internal static string? SpectraFile(string path)
    {
        const string marker = "02_fetch/spectra/";
        int i = path.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        string rest = path[(i + marker.Length)..];
        int slash = rest.IndexOf('/');
        string name = slash < 0 ? rest : rest[..slash];   // a Bruker .d folder's files name the folder
        return SpectraExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) ? name : null;
    }

    private static JsonObject Parse(string file) =>
        JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException($"{file} is not a JSON object");

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
