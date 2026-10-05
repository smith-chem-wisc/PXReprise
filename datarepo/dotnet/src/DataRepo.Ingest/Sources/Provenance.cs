using System.Globalization;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using static DataRepo.Ingest.Sources.SourcesPy;

namespace DataRepo.Ingest.Sources;

/// <summary><c>provenance.json</c> -> ProvenanceRecord, Metric and Finding rows.</summary>
/// <remarks>
/// The provenance schema version is not decoration. aging thread 006: in <c>aging-provenance/2</c> and
/// earlier, <c>id_rate.psms_1pct</c> holds the FDR engine's count, not the canonical target-PSM count that
/// its name suggests; <c>/3</c> splits them into two named fields, each with its own definition. So the
/// version is read first and the fields are mapped accordingly, and a version older than <c>/2</c> is
/// refused rather than read under the wrong meaning. Ported from <c>sources/provenance.py</c>, with D37's
/// <c>definitions</c> namespace from <c>wip/d37-g81-python</c> (3e68781).
/// </remarks>
public static class Provenance
{
    /// <summary>Every provenance schema this ingester reads, as <c>(family, version) -> field layout</c>.</summary>
    /// <remarks>
    /// The LAYOUT is what the rest of this module branches on: layout 2 is the one where
    /// <c>id_rate.psms_1pct</c> holds the FDR engine's count, layout 3 the one that splits it into two named
    /// fields. A schema name is the producer's, and one layout can travel under several names:
    /// <c>pxreprise-provenance/1</c> is PXReprise's neutral name for the document PXReprise already emits as
    /// <c>aging-provenance/3</c> (PXReprise 001, PXR-D2), so it reads with layout 3. A schema not listed here is
    /// refused, never read under a guessed layout, because a count read under the wrong field meaning
    /// is a wrong number with a right-looking definition id.
    /// </remarks>
    public static readonly IReadOnlyDictionary<(string Family, int Version), int> Layouts = new Dictionary<(string, int), int>
    {
        [("aging-provenance", 2)] = 2,
        [("aging-provenance", 3)] = 3,
        [("pxreprise-provenance", 1)] = 3,
    };

    private static readonly Regex SchemaRe = new(@"^(?<family>[a-z][a-z0-9-]*-provenance)/(?<version>\d+)$", RegexOptions.CultureInvariant);

    /// <summary><see cref="SchemaRe"/> without the trailing-newline allowance of <c>$</c>, and with ASCII digits only
    /// (<c>\d</c> also matches other scripts' digits) (G83 item 16).</summary>
    private static readonly Regex SchemaReExact = new(@"^(?<family>[a-z][a-z0-9-]*-provenance)/(?<version>[0-9]+)\z", RegexOptions.CultureInvariant);

    /// <summary>How much each pipeline flag should change trust in the data.</summary>
    public static readonly IReadOnlyDictionary<string, string> FlagSeverity = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["low_id_rate"] = "warning",
        ["no_design_file"] = "warning",
        ["no_output_sdrf"] = "info",
        ["old_provenance_schema"] = "error",
    };

    public static readonly IReadOnlyDictionary<string, string> FlagMessage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["low_id_rate"] =
            "The search identified an unusually small fraction of the MS2 spectra. Treat absence of a " +
            "protein in this dataset as weak evidence.",
        ["no_design_file"] =
            "No experimental design file, so quantification treated every raw file as its own " +
            "condition. Between-group comparisons are not available for this dataset.",
        ["no_output_sdrf"] =
            "The search did not write an SDRF back out, so sample metadata comes from the deposited " +
            "SDRF only.",
    };

    /// <summary>A <c>provenance.json</c>, read as Python's <c>json.loads</c> reads it (utf-8-sig).</summary>
    /// <exception cref="InvalidDataException">The document is not a JSON object.</exception>
    public static Dictionary<string, object?> Load(string path) =>
        LoadJson(path) as Dictionary<string, object?> ?? throw new InvalidDataException($"{path} is not a JSON object");

    /// <summary>The field layout of this provenance document (see <see cref="Layouts"/>).</summary>
    /// <exception cref="UnsupportedProvenanceException">The value is missing, unparseable, or a schema whose
    /// field meanings this ingester has not been taught.</exception>
    /// <param name="rules">Under <see cref="IngestRules.Current"/> the schema must be exactly a listed name:
    /// <c>aging-provenance/3\n</c> is refused like any other unlisted string. Python 0.32.0's <c>$</c> also
    /// matched before a final newline, and its <c>\d</c> any script's digits (G83 item 16).</param>
    public static int SchemaVersion(IReadOnlyDictionary<string, object?> doc, IngestRules rules = IngestRules.Current)
    {
        var raw = Str(Get(doc, "schema", ""));
        var m = (rules == IngestRules.Current ? SchemaReExact : SchemaRe).Match(raw);
        int? layout = null;
        if (m.Success && ParseDigits(m.Groups["version"].Value) is { } version
            && Layouts.TryGetValue((m.Groups["family"].Value, version), out var l))
            layout = l;
        if (layout is null)
        {
            var supported = string.Join(", ", Layouts.Keys
                .OrderBy(k => k.Family, CodePointOrder).ThenBy(k => k.Version)
                .Select(k => $"{k.Family}/{k.Version}"));
            throw new UnsupportedProvenanceException(
                $"provenance schema {Repr(raw)} is not one this ingester reads; it reads {supported}. " +
                "A schema it has not been taught is refused rather than read under a guessed layout: " +
                "older records name their PSM count in a way that cannot be mapped to a definition " +
                "safely.");
        }
        return layout.Value;
    }

    /// <summary>Python's <c>int()</c> of a run of (any Unicode) decimal digits; null past <c>int</c>'s range,
    /// which no layout uses.</summary>
    private static int? ParseDigits(string digits)
    {
        long value = 0;
        foreach (var c in digits)
        {
            value = value * 10 + (long)char.GetNumericValue(c);
            if (value > int.MaxValue) return null;
        }
        return (int)value;
    }

    /// <summary>The namespace this record's numbers are defined in: its <c>definitions</c> field (D37).</summary>
    /// <remarks>A producer states what its numbers mean in each stage's record, never in the manifest
    /// (PXR-D5). A record that declares nothing is <c>aging</c>'s, which is what every record written
    /// before PXReprise's switch is.</remarks>
    /// <exception cref="UnsupportedProvenanceException">A namespace this ingester has not been taught. Refused
    /// rather than read as <c>aging</c>, because a number filed under the wrong owner's id is a wrong citation
    /// with a right-looking id.</exception>
    public static string DefinitionsNamespace(IReadOnlyDictionary<string, object?> doc)
    {
        var raw = Get(doc, "definitions");
        if (raw is null) return Definitions.DefaultNamespace;
        if (raw is not string s || !Definitions.Namespaces.Contains(s))
            throw new UnsupportedProvenanceException(
                $"provenance declares definitions {Repr(raw)}; this ingester reads " +
                $"[{string.Join(", ", Definitions.Namespaces.Select(n => Repr(n)))}] " +
                $"(a record that declares nothing is read as {Repr(Definitions.DefaultNamespace)})");
        return s;
    }

    /// <summary>One ProvenanceRecord row, with the heavy blocks kept verbatim as JSON.</summary>
    /// <param name="rules">Under <see cref="IngestRules.Current"/> a <c>schema</c> that is null is written as an
    /// empty string, as an absent one always was; Python 0.32.0 wrote <c>str(None)</c>, the word <c>None</c>,
    /// which reads as a schema name (G83 item 16). The column is required, so empty is the nearest to "not
    /// stated" it can hold.</param>
    public static Row RecordRow(IReadOnlyDictionary<string, object?> doc, string datasetId, string stageDirName, string bundlePath, string sha256,
        IngestRules rules = IngestRules.Current)
    {
        var pipeline = DictOrEmpty(Get(doc, "pipeline"), "pipeline");
        var resources = DictOrEmpty(Get(doc, "resources"), "resources");
        var stage = Get(doc, "stage");
        var tools = Get(doc, "tools");
        var parameters = Get(doc, "params");
        return new Row
        {
            ["dataset_id"] = datasetId,
            ["stage"] = Str(Truthy(stage) ? stage : stageDirName),
            ["provenance_schema"] = rules == IngestRules.Current ? (Get(doc, "schema", null) is { } schema ? Str(schema) : null) : Str(Get(doc, "schema", "")),
            ["started_utc"] = Get(doc, "started_utc"),
            ["finished_utc"] = Get(doc, "finished_utc"),
            ["pipeline_repo"] = Get(pipeline, "repo"),
            ["pipeline_commit"] = Get(pipeline, "commit"),
            ["params_sha256"] = Get(DictOrEmpty(Get(doc, "params_file"), "params_file"), "sha256"),
            // json.dumps(..., sort_keys=True): ensure_ascii is Python's default, so non-ASCII is escaped.
            ["tools_json"] = Truthy(tools) ? PyFormat.Json(tools, sortKeys: true, ensureAscii: true) : null,
            ["params_json"] = Truthy(parameters) ? PyFormat.Json(parameters, sortKeys: true, ensureAscii: true) : null,
            ["wall_seconds"] = Get(resources, "wall_s"),
            ["peak_rss_gib"] = Get(resources, "peak_rss_gib"),
            ["resources_json"] = resources.Count > 0 ? PyFormat.Json(resources, sortKeys: true, ensureAscii: true) : null,
            ["original_path"] = bundlePath,
            ["original_sha256"] = sha256,
        };
    }

    private static Row Metric(string scope, string scopeId, string name, object? value, string definition, string source) => new()
    {
        ["scope"] = scope,
        ["scope_id"] = scopeId,
        ["name"] = name,
        ["value"] = value,
        ["definition_id"] = definition,
        ["source"] = source,
    };

    /// <summary>Dataset-level Metric rows from the search stage's own numbers.</summary>
    /// <remarks>The PSM count is stored under the definition it actually satisfies, which depends on the
    /// provenance version, so a reader never has to know the field-naming history. Pipeline counts cite
    /// <paramref name="ns"/>'s id (D37); QuantProject's MBR block keeps its own.</remarks>
    /// <param name="version">The layout from <see cref="SchemaVersion"/>.</param>
    /// <param name="ns">The record's definitions namespace (<see cref="DefinitionsNamespace"/>).</param>
    /// <param name="rules">Under <see cref="IngestRules.Current"/> an MBR key whose value is null writes no row, as
    /// every other metric here does: a metric row is a measurement, and a null one states nothing. Python 0.32.0
    /// wrote one for any key present (G83 item 13).</param>
    public static List<Row> MetricRows(IReadOnlyDictionary<string, object?> doc, string datasetId, int version, string ns = Definitions.DefaultNamespace,
        IngestRules rules = IngestRules.Current)
    {
        var rows = new List<Row>();
        var idRate = DictOrEmpty(Get(doc, "id_rate"), "id_rate");

        void Add(string name, object? value, string definition, string source)
        {
            if (value is not null) rows.Add(Metric("dataset", datasetId, name, value, definition, source));
        }

        if (version >= 3)
        {
            Add("psms_1pct", Get(idRate, "psms_1pct"), Definitions.Cite(Definitions.Psm1pct, ns),
                "provenance.json id_rate.psms_1pct");
            Add("psms_fdr_engine_1pct", Get(idRate, "psms_fdr_engine_1pct"),
                Definitions.Cite(Definitions.PsmFdrEngine, ns), "provenance.json id_rate.psms_fdr_engine_1pct");
        }
        else
        {
            // aging 006: in /2 the field named psms_1pct holds the FDR engine's count.
            Add("psms_fdr_engine_1pct", Get(idRate, "psms_1pct"), Definitions.Cite(Definitions.PsmFdrEngine, ns),
                "provenance.json id_rate.psms_1pct (aging-provenance/2 naming)");
        }

        Add("ms2", Get(idRate, "ms2"), Definitions.Cite(Definitions.Ms2Count, ns), "provenance.json id_rate.ms2");
        Add("id_rate", Get(idRate, "rate"), Definitions.Cite(Definitions.IdRate, ns), "provenance.json id_rate.rate");

        var mbr = DictOrEmpty(Get(doc, "mbr"), "mbr");
        var mbrDefinition = Get(mbr, "definition");
        var mbrSource = Str(Truthy(mbrDefinition) ? mbrDefinition : Definitions.Mbr.DefinitionId);
        foreach (var key in new[] { "mbr_rows", "mbr_kept", "msms_peaks", "kept_over_msms", "mbr_fdr_threshold" })
        {
            // 0.32.0: present is enough, so a null was written. Current: a value is needed.
            if (mbr.TryGetValue(key, out var value) && (value is not null || rules == IngestRules.Python0320))
                rows.Add(Metric("dataset", datasetId, key, value, Definitions.Mbr.DefinitionId, $"provenance.json mbr ({mbrSource})"));
        }
        return rows;
    }

    /// <summary>Metric rows from the search stage's <c>contamination</c> block.</summary>
    /// <remarks>
    /// The block was already becoming a Finding, which says <em>that</em> a dataset is contaminated but puts
    /// the numbers in a sentence. These rows put them where a query can reach them.
    ///
    /// <b>The intensity share is emitted per run, one row per file, and is never averaged into a
    /// dataset number here.</b> It is defined per file (QuantProject DEF-QC-9), and the dataset figure
    /// is what hid the structure aging found: 7.0% overall against 2.6-18.9% per file, grouped by cell
    /// line. The producer's own median/min/max are carried as dataset-scope rows under names that say
    /// they are summaries of the per-run values, so nothing forces a caller to recompute them and
    /// nothing lets a caller mistake one for the measurement.
    /// </remarks>
    /// <param name="doc">The search stage's provenance document.</param>
    /// <param name="datasetId">ProteomeXchange accession.</param>
    /// <param name="runNames">To resolve the calibrated names the block is keyed by back to the deposited run.
    /// A file that will not resolve gets no row rather than a row under a <c>run_id</c> that matches no run.</param>
    /// <param name="ns">The record's definitions namespace (D37).</param>
    public static List<Row> ContaminationMetricRows(IReadOnlyDictionary<string, object?> doc, string datasetId, RunNameMap? runNames = null, string ns = Definitions.DefaultNamespace)
    {
        var block = DictOrEmpty(Get(doc, "contamination"), "contamination");
        var rows = new List<Row>();

        void Add(string scope, string scopeId, string name, object? value, string definition, string source)
        {
            if (value is not null) rows.Add(Metric(scope, scopeId, name, value, definition, source));
        }

        var psmShareDefinition = Get(block, "psm_share_definition");
        if (!Truthy(psmShareDefinition)) psmShareDefinition = Get(block, "definition");
        if (!Truthy(psmShareDefinition)) psmShareDefinition = "unnamed";
        Add("dataset", datasetId, "contamination_psm_share", Get(block, "psm_share"),
            Definitions.Cite(Definitions.ContamPsmShare, ns),
            $"provenance.json contamination.psm_share ({Str(psmShareDefinition)})");
        foreach (var key in new[] { "contaminant_psms", "target_plus_contaminant_psms" })
            Add("dataset", datasetId, key, Get(block, key), Definitions.Cite(Definitions.ContamPsmShare, ns),
                $"provenance.json contamination.{key}");

        var intensityDefinition = Definitions.ContamIntensityShare.DefinitionId;
        var perFile = DictOrEmpty(Get(block, "intensity_share_per_file"), "contamination.intensity_share_per_file");
        foreach (var (reported, value) in perFile.OrderBy(kv => kv.Key, CodePointOrder))
        {
            var runBase = runNames is not null ? runNames.Resolve(reported) : reported;
            if (string.IsNullOrEmpty(runBase)) continue;
            Add("run", $"{datasetId}:{runBase}", "contamination_intensity_share", value, intensityDefinition,
                "provenance.json contamination.intensity_share_per_file");
        }
        foreach (var summary in new[] { "median", "min", "max" })
            Add("dataset", datasetId, $"contamination_intensity_share_{summary}", Get(block, $"intensity_share_{summary}"),
                intensityDefinition,
                $"provenance.json contamination.intensity_share_{summary} " +
                "(a summary of the per-run values, not a measurement of the dataset)");
        return rows;
    }

    /// <summary>Finding rows from the stage's <c>flags[]</c>.</summary>
    /// <remarks>A flag reads <c>code: explanation</c>. The code drives severity; the explanation is kept,
    /// because it is the producer's own words about what is wrong.</remarks>
    public static List<Row> FindingRows(IReadOnlyDictionary<string, object?> doc, string datasetId, string source)
    {
        var rows = new List<Row>();
        foreach (var raw in ListOrEmpty(Get(doc, "flags"), "flags"))
        {
            var text = Str(raw);
            var (head, _, tail) = Partition(text, ":");
            var code = Strip(head);
            var detail = Strip(tail);
            var message = FlagMessage.TryGetValue(code, out var known) ? known : (detail.Length > 0 ? detail : text);
            if (detail.Length > 0 && FlagMessage.ContainsKey(code))
                message = $"{message} Producer's note: {detail}";
            rows.Add(new Row
            {
                ["finding_id"] = $"{datasetId}:{code}",
                ["dataset_id"] = datasetId,
                ["run_id"] = null,
                ["code"] = code,
                ["severity"] = FlagSeverity.GetValueOrDefault(code, "warning"),
                ["status"] = "open",
                ["message"] = message,
                ["source"] = source,
            });
        }
        return rows;
    }
}
