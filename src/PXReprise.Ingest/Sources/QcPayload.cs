using PXReprise.Bundle;
using static PXReprise.Ingest.Sources.SourcesPy;

namespace PXReprise.Ingest.Sources;

/// <summary>PXReprise's QC payload (<c>qc_payload.json</c>, written by its <c>qc_payload</c> stage) -> each run's PSM,
/// peptide and protein-group counts over the whole search's 1% set (1.5.0 build list item 7).</summary>
/// <remarks>
/// <para>results.txt gives per-run counts only when the search wrote its per-file lines (MetaMorpheus's
/// <c>WriteIndividualFiles</c>), and those lines recompute the FDR on each file alone. The payload's counts are another
/// number, PXReprise's <c>pxreprise:DEF-*-1PCT-RUN</c>, so they are stored under PXReprise's definitions and under names
/// of their own (<see cref="Counts"/>), never under results.txt's names. dataRepo counts nothing itself (D24).</para>
/// <para>The manifest names no stage for the payload, and a run folder can hold more than one search and more than one
/// payload. So a stage is taken only when its own <c>provenance.json</c> says <c>stage: qc_payload</c> and names, by
/// sha256, the search provenance this bundle reads, and its payload only when it is still the file that record wrote.</para>
/// <para>A count is stored only under a definition whose text this ingester carries, at that version. A payload whose
/// counts cite aging's per-file definitions (<c>qc-payload/1</c>) copies results.txt's per-file lines, which ingest reads
/// from results.txt itself, so nothing is read from it, and it is not a source of the bundle.</para>
/// </remarks>
public static class QcPayload
{
    /// <summary>The stage name PXReprise's payload stage writes in its <c>provenance.json</c>.</summary>
    public const string StageName = "qc_payload";

    public const string FileName = "qc_payload.json";

    /// <summary>The payload layouts this reader has been taught: per-file <c>files[].file</c> and
    /// <c>files[].metrics</c>, and a <c>definitions</c> block naming each metric's definition and version.</summary>
    public static readonly IReadOnlyList<string> Schemas = ["qc-payload/1", "qc-payload/2"];

    /// <summary>The payload's per-file counts this reader stores, and the <c>metrics.name</c> each is stored under.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Counts =
    [
        ("psms", "psms_1pct_whole_search"),
        ("peptides", "peptides_1pct_whole_search"),
        ("protein_groups", "protein_groups_1pct_whole_search"),
    ];

    /// <summary>A payload stage naming this bundle's search.</summary>
    /// <param name="StageDir">The stage folder's name (<c>05_qc</c>).</param>
    public sealed record Stage(string StageDir, string ProvenancePath, string PayloadPath, IReadOnlyDictionary<string, object?> Provenance);

    /// <summary>What reading the payload gave: the stage it was read from (null when none was), its metric rows, and
    /// what was left out, each for a finding.</summary>
    public sealed class Reading
    {
        public Stage? Read { get; init; }
        public List<Row> Metrics { get; } = [];
        /// <summary>Stages naming this search whose payload is missing or is not the file their record wrote.</summary>
        public List<string> Changed { get; } = [];
        /// <summary>Stages naming this search whose payload could not be read, or has a layout this reader has not
        /// been taught, and why.</summary>
        public List<(string StageDir, string Why)> Unreadable { get; } = [];
        /// <summary>More than one stage gave counts, so none was read: which one is the search's is not ours to choose.</summary>
        public List<string> Ambiguous { get; } = [];
        /// <summary>Counts left out because this ingester carries no text for their definition at that version.</summary>
        public List<string> UnknownDefinitions { get; } = [];
        /// <summary>Payload files with a count that name no run of the bundle.</summary>
        public List<string> UnplacedFiles { get; } = [];
        /// <summary>Counts that are not a JSON integer, as <c>file: key</c>.</summary>
        public List<string> NotWholeNumbers { get; } = [];
    }

    /// <summary>The definition texts a payload's counts may cite: PXReprise's per-run counts, by id.</summary>
    private static readonly IReadOnlyDictionary<string, Def> Carried =
        Definitions.PxrepriseRunCounts.ToDictionary(d => d.DefinitionId, StringComparer.Ordinal);

    /// <summary>aging's per-file definitions: a payload citing them copies results.txt's lines.</summary>
    private static readonly IReadOnlySet<string> ResultsTxtLines = new HashSet<string>(StringComparer.Ordinal)
    {
        Definitions.Psm1pctRun.DefinitionId, Definitions.PeptideCount1pctRun.DefinitionId, Definitions.ProteinGroupCount1pctRun.DefinitionId,
    };

    /// <summary>The payload stages of <paramref name="runDir"/> that name, by sha256, the search provenance
    /// <paramref name="searchProvenanceSha256"/>, in folder-name order.</summary>
    public static List<Stage> Find(string runDir, string searchProvenanceSha256)
    {
        var found = new List<Stage>();
        if (!Directory.Exists(runDir)) return found;
        foreach (var dir in Directory.GetDirectories(runDir).Order(StringComparer.Ordinal))
        {
            var provenancePath = Path.Combine(dir, "provenance.json");
            if (!File.Exists(provenancePath)) continue;
            // A record that cannot be read says nothing about which search it belongs to, so it is passed over.
            try
            {
                var doc = Provenance.Load(provenancePath);
                if (Get(doc, "stage") as string != StageName) continue;
                var namesSearch = ListOrEmpty(Get(doc, "upstream"), "upstream")
                    .Any(u => u is IReadOnlyDictionary<string, object?> m && Get(m, "sha256") as string == searchProvenanceSha256);
                if (namesSearch) found.Add(new Stage(Path.GetFileName(dir), provenancePath, Path.Combine(dir, FileName), doc));
            }
            catch (Exception e) when (Unreadable(e)) { }
        }
        return found;
    }

    /// <summary>Reads the per-run counts of the one payload stage naming this search.</summary>
    /// <param name="runIds">The bundle's run ids (<c>&lt;dataset&gt;:&lt;file stem&gt;</c>).</param>
    public static Reading Read(IReadOnlyList<Stage> stages, IReadOnlySet<string> runIds, string datasetId)
    {
        var candidates = new List<(Stage Stage, Reading Reading)>();
        var changed = new List<string>();
        var unreadable = new List<(string, string)>();
        foreach (var stage in stages)
        {
            if (!File.Exists(stage.PayloadPath) || RecordedSha256(stage.Provenance) is not { } recorded
                || BundleWriter.Sha256File(stage.PayloadPath) != recorded)
            {
                changed.Add(stage.StageDir);
                continue;
            }
            try
            {
                var payload = LoadJson(stage.PayloadPath) as IReadOnlyDictionary<string, object?>
                    ?? throw new InvalidDataException("not a JSON object");
                var schema = Get(payload, "schema") as string;
                if (schema is null || !Schemas.Contains(schema))
                {
                    unreadable.Add((stage.StageDir, schema is null ? "it names no schema" : $"its schema `{schema}` is not one this ingester reads"));
                    continue;
                }
                var reading = ReadPayload(payload, stage, runIds, datasetId);
                if (reading.Metrics.Count > 0 || reading.UnknownDefinitions.Count > 0) candidates.Add((stage, reading));
            }
            catch (Exception e) when (Unreadable(e)) { unreadable.Add((stage.StageDir, $"it could not be read ({e.Message})")); }
        }
        Reading result;
        if (candidates.Count == 1) result = candidates[0].Reading;
        else
        {
            result = new Reading();
            if (candidates.Count > 1) result.Ambiguous.AddRange(candidates.Select(c => c.Stage.StageDir));
        }
        result.Changed.AddRange(changed);
        result.Unreadable.AddRange(unreadable);
        return result;
    }

    /// <summary>A file that is not the JSON this reader expects: never a crash of the whole ingest, because the payload is
    /// not a file the bundle cannot do without.</summary>
    private static bool Unreadable(Exception e) =>
        e is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or System.Text.DecoderFallbackException;

    /// <summary>The sha256 the stage's record gives its payload (<c>outputs[]</c>), or null when it gives none.</summary>
    private static string? RecordedSha256(IReadOnlyDictionary<string, object?> provenance)
    {
        foreach (var o in ListOrEmpty(Get(provenance, "outputs"), "outputs"))
            if (o is IReadOnlyDictionary<string, object?> m && Get(m, "path") is string p
                && Path.GetFileName(p.Replace('\\', '/')) == FileName && Get(m, "sha256") is string sha && sha.Length > 0)
                return sha;
        return null;
    }

    private static Reading ReadPayload(IReadOnlyDictionary<string, object?> payload, Stage stage, IReadOnlySet<string> runIds, string datasetId)
    {
        var reading = new Reading { Read = stage };
        var definitions = DictOrEmpty(Get(payload, "definitions"), "definitions");
        var usable = new List<(string Key, string Name, string DefinitionId)>();
        foreach (var (key, name) in Counts)
        {
            var cited = DictOrEmpty(Get(definitions, key), $"definitions.{key}");
            var id = Get(cited, "id") as string;
            var version = Get(cited, "version") as string;
            if (id is null || ResultsTxtLines.Contains(id)) continue;
            if (Carried.TryGetValue(id, out var def) && def.Version == version) usable.Add((key, name, id));
            else reading.UnknownDefinitions.Add($"`{key}` under `{id} {version ?? "(no version)"}`");
        }
        if (usable.Count == 0) return reading;
        var unplaced = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in ListOrEmpty(Get(payload, "files"), "files"))
        {
            if (f is not IReadOnlyDictionary<string, object?> file || Get(file, "file") is not string fileName) continue;
            var metrics = DictOrEmpty(Get(file, "metrics"), $"files[{fileName}].metrics");
            var runId = $"{datasetId}:{fileName}";
            foreach (var (key, name, definitionId) in usable)
            {
                var value = Get(metrics, key);
                if (value is null) continue;
                if (!runIds.Contains(runId)) { unplaced.Add(fileName); continue; }
                if (value is not long count) { reading.NotWholeNumbers.Add($"{fileName}: {key}"); continue; }
                reading.Metrics.Add(new Row
                {
                    ["scope"] = "run",
                    ["scope_id"] = runId,
                    ["name"] = name,
                    ["value"] = count,
                    ["definition_id"] = definitionId,
                    ["source"] = FileName,
                });
            }
        }
        reading.UnplacedFiles.AddRange(unplaced);
        return reading;
    }
}
