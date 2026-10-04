using System.Text.RegularExpressions;
using DataRepo.Bundle;

namespace DataRepo.Ingest.Sources;

/// <summary>A parsed SDRF: the rows the schema wants, plus the run-level facts it carries.</summary>
public sealed class SdrfTable
{
    /// <summary>Sample rows, one per distinct source name, in first-seen order.</summary>
    public required List<Row> Samples { get; init; }

    /// <summary>SampleCharacteristic rows: every <c>characteristics[...]</c> / <c>factor value[...]</c> cell, verbatim.</summary>
    public required List<Row> Characteristics { get; init; }

    /// <summary>Assay rows, one per SDRF row that names a data file.</summary>
    public required List<Row> Assays { get; init; }

    /// <summary>Deposited run base name -> instrument, fraction and technical replicate.</summary>
    public required Dictionary<string, Row> RunFacts { get; init; }

    /// <summary>Deposited run base name -> sample_id.</summary>
    public required Dictionary<string, string> SampleOfRun { get; init; }

    /// <summary>The SDRF's column names, in order, repeats included.</summary>
    public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary><c>*.sdrf.tsv</c> -> Sample, SampleCharacteristic and Assay rows.</summary>
/// <remarks>
/// <para>
/// The harmonized sample table is the most valuable thing the repository builds (FRAMEWORK section 1), and
/// it is also the thinnest: most deposited SDRFs are skeletons with <c>not available</c> where the biology
/// should be. So two rules hold here.
/// </para>
/// <para>
/// <i>Nothing is invented.</i> A curated column is filled only when the SDRF carries an ontology accession
/// for it; <c>not available</c> becomes a null, never a guess.
/// </para>
/// <para>
/// <i>Nothing is lost.</i> Every <c>characteristics[...]</c> column is also copied verbatim into
/// SampleCharacteristic, so a column this ingester does not yet understand is still in the bundle and still
/// queryable.
/// </para>
/// <para>
/// Age is deliberately absent. It is a study-layer column (<c>schema/study/aging.yaml</c>), and normalizing
/// "P60D" or "60-65" into years belongs to sdrf/mzLib, not here. Ported from <c>sources/sdrf.py</c>.
/// </para>
/// </remarks>
public static class Sdrf
{
    /// <summary>Values SDRF writers use to mean "absent". They become nulls, not strings.</summary>
    public static readonly IReadOnlySet<string> NotAvailable =
        new HashSet<string>(StringComparer.Ordinal) { "", "not available", "not applicable", "na", "n/a", "unknown", "none" };

    /// <summary>Enough of the organism axis to read the datasets in scope (D5: human and rodent).</summary>
    public static readonly IReadOnlyDictionary<string, string> OrganismTaxa = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["homo sapiens"] = "NCBITaxon:9606",
        ["mus musculus"] = "NCBITaxon:10090",
        ["rattus norvegicus"] = "NCBITaxon:10116",
    };

    /// <summary>PATO terms for the sex axis.</summary>
    public static readonly IReadOnlyDictionary<string, string> SexTerms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["male"] = "PATO:0000384",
        ["female"] = "PATO:0000383",
    };

    private static readonly Regex Column = new(
        @"^(?<kind>characteristics|comment|factor value)\[(?<name>.+)\]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Splits an SDRF <c>NT=...;AC=...;TA=...</c> cell into its parts.</summary>
    /// <remarks>A plain value comes back as <c>{"NT": value}</c>, so callers can treat every cell the same way.</remarks>
    public static Dictionary<string, string> ParseValue(string? raw)
    {
        string text = PyText.Strip(raw);
        if (!text.Contains('='))
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["NT"] = text };
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string piece in text.Split(';'))
        {
            int eq = piece.IndexOf('=');
            if (eq >= 0)
                parts[PyText.Strip(piece[..eq]).ToUpperInvariant()] = PyText.Strip(piece[(eq + 1)..]);
            else if (PyText.Strip(piece).Length > 0 && !parts.ContainsKey("NT"))
                parts["NT"] = PyText.Strip(piece);
        }
        return parts.Count > 0 ? parts : new Dictionary<string, string>(StringComparer.Ordinal) { ["NT"] = text };
    }

    private static string? Clean(string? raw)
    {
        string value = PyText.Strip(raw);
        return NotAvailable.Contains(value.ToLowerInvariant()) ? null : value;
    }

    /// <summary>A provenance cell as written: NULL only when empty. A reserved word stays a word (G75).</summary>
    /// <remarks><see cref="Clean"/> is for values, where <c>not available</c> answers the question. A source
    /// column saying <c>not applicable</c> is the SDRF recording something, and NULL there reads "the SDRF
    /// records no source", which it does.</remarks>
    private static string? Verbatim(string? raw)
    {
        string value = PyText.Strip(raw);
        return value.Length > 0 ? value : null;
    }

    private static string? Term(string? raw)
    {
        string? accession = ParseValue(raw).GetValueOrDefault("AC");
        return !string.IsNullOrEmpty(accession) && !NotAvailable.Contains(accession.ToLowerInvariant()) ? accession : null;
    }

    private static string? Name(string? raw) => Clean(ParseValue(raw).GetValueOrDefault("NT", ""));

    /// <summary>Python's <c>int(x) if x and x.isdigit() else None</c>.</summary>
    private static long? Digits(string? value) =>
        !string.IsNullOrEmpty(value) && PyText.IsDigit(value) ? PyText.ParseDecimalDigits(value) : null;

    /// <summary>Reads one SDRF into schema rows.</summary>
    /// <param name="path">The deposited (or repaired) SDRF.</param>
    /// <param name="datasetId">ProteomeXchange accession, which prefixes every generated ID.</param>
    /// <param name="defaultOrganism">NCBITaxon CURIE from the ingest manifest, used when the SDRF names an
    /// organism this module has no term for.</param>
    /// <param name="log">Reader log to record the backend in.</param>
    public static SdrfTable Parse(string path, string datasetId, string? defaultOrganism = null, ReaderLog? log = null)
    {
        (List<string> header, List<List<string>> rows) = Readers.ReadSdrf(path, log);
        var samples = new Dictionary<string, Row>(StringComparer.Ordinal);
        var characteristics = new List<Row>();
        var assays = new List<Row>();
        var runFacts = new Dictionary<string, Row>(StringComparer.Ordinal);
        var sampleOfRun = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (List<string> cells in rows)
        {
            // An SDRF column name is a POSITION, not a key: `comment[modification parameters]` can appear
            // many times in one file, and a name-keyed map keeps only the last. Lookups below use the map,
            // because no column they read repeats; copying characteristics verbatim walks the pairs, so a
            // repeated one is not silently dropped.
            var pairs = new List<(string Column, string Raw)>(header.Count);
            for (int i = 0; i < header.Count; i++)
                pairs.Add((header[i], i < cells.Count ? cells[i] ?? "" : ""));
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string column, string raw) in pairs)
                row[column] = raw;
            string Get(string key) => row.GetValueOrDefault(key, "");

            string sourceName = PyText.Strip(Get("source name"));
            if (sourceName.Length == 0)
                continue;
            string sampleId = $"{datasetId}:{sourceName}";

            if (!samples.ContainsKey(sampleId))
            {
                string organismName = Name(Get("characteristics[organism]")) ?? "";
                string? organism = Term(Get("characteristics[organism]"))
                    ?? OrganismTaxa.GetValueOrDefault(organismName.ToLowerInvariant())
                    ?? defaultOrganism;
                string sexName = (Name(Get("characteristics[sex]")) ?? "").ToLowerInvariant();
                string? replicate = Clean(Get("characteristics[biological replicate]"));
                samples[sampleId] = new Row
                {
                    ["sample_id"] = sampleId,
                    ["dataset_id"] = datasetId,
                    ["source_name"] = sourceName,
                    ["organism"] = organism,
                    ["sex"] = Term(Get("characteristics[sex]")) ?? SexTerms.GetValueOrDefault(sexName),
                    ["organism_part"] = Term(Get("characteristics[organism part]")),
                    ["cell_type"] = Term(Get("characteristics[cell type]")),
                    ["disease"] = Term(Get("characteristics[disease]")),
                    ["condition"] = Name(Get("characteristics[disease]")),
                    ["material_type"] = Name(Get("material type")) ?? Name(Get("characteristics[material type]")),
                    ["cell_line"] = Name(Get("characteristics[cell line]")),
                    ["individual_id"] = Name(Get("characteristics[individual]")),
                    ["biological_replicate"] = Digits(replicate),
                    ["timepoint"] = Name(Get("characteristics[time]")),
                };
                string? defaultSource = Verbatim(Get("comment[characteristics source]"));
                foreach ((string column, string raw) in pairs)
                {
                    Match m = Column.Match(PyText.Strip(column));
                    if (!m.Success || m.Groups["kind"].Value.ToLowerInvariant() == "comment")
                        continue;
                    string written = PyText.Strip(raw);
                    if (written.Length == 0)
                        continue;  // an empty cell says nothing, not even "not available"
                    string inner = PyText.Strip(m.Groups["name"].Value);
                    // A reserved word is kept, flagged: `not available` is an answer to a question that was
                    // asked, and dropping it made it look like one never asked (G42).
                    characteristics.Add(new Row
                    {
                        ["sample_id"] = sampleId,
                        ["name"] = PyText.Strip(column),
                        ["value"] = written,
                        ["value_reserved"] = Clean(written) is null,
                        ["term"] = Term(raw),
                        // sdrf's D31 grain: a column's own `comment[<name> source]` overrides the row's
                        // `comment[characteristics source]`. Neither present means NULL, never `deposited`:
                        // nothing in the file says so (G62).
                        ["source"] = Verbatim(Get($"comment[{inner} source]")) ?? defaultSource,
                        ["source_reference"] = Verbatim(Get($"comment[{inner} source reference]")),
                        ["source_method"] = Verbatim(Get($"comment[{inner} source method]")),
                    });
                }
            }

            string? dataFile = Clean(Get("comment[data file]")) ?? Clean(Get("assay name"));
            if (dataFile is null)
                continue;
            // An SDRF MetaMorpheus writes names the file it SEARCHED, which after Calibrate is `X-calib.mzML`,
            // not the deposited `X.raw` (sdrf D40, aging 069 DATAREPO-54). Runs are keyed on the deposited
            // name, so without the same stripping the USI path does, that run would silently get no sample,
            // instrument or fraction.
            string runName = Usi.StripPipelineSuffix(PyText.PathStem(dataFile));
            sampleOfRun[runName] = sampleId;
            string? fraction = Clean(Get("comment[fraction identifier]"));
            string? technical = Clean(Get("comment[technical replicate]"));
            runFacts[runName] = new Row
            {
                ["instrument_model"] = Name(Get("comment[instrument]")),
                ["instrument_term"] = Term(Get("comment[instrument]")),
                ["fraction"] = Digits(fraction),
                ["technical_replicate"] = Digits(technical),
                // SDRF-DR10: a drafted `1` is a default nothing established, so where it came from is
                // carried beside it. NULL until the SDRF writes the column.
                ["fraction_source"] = Verbatim(Get("comment[fraction identifier source]")),
                ["technical_replicate_source"] = Verbatim(Get("comment[technical replicate source]")),
                ["acquisition"] = Name(Get("comment[proteomics data acquisition method]")),
            };
            string channel = Channel(Get("comment[label]"));
            assays.Add(new Row
            {
                ["assay_id"] = $"{datasetId}:{runName}:{channel}",
                ["run_id"] = $"{datasetId}:{runName}",
                ["channel"] = channel,
                ["sample_id"] = sampleId,
            });
        }

        return new SdrfTable
        {
            Samples = samples.Values.ToList(),
            Characteristics = characteristics,
            Assays = assays,
            RunFacts = runFacts,
            SampleOfRun = sampleOfRun,
            Columns = header.ToList(),
        };
    }

    /// <summary>Assay channel from the SDRF label comment: <c>label_free</c>, or the reporter channel.</summary>
    private static string Channel(string raw)
    {
        string name = Name(raw) ?? "";
        if (name.Length == 0 || name.ToLowerInvariant().Contains("label free") || name.ToLowerInvariant() == "none")
            return "label_free";
        return name.Replace(" ", "");
    }
}
