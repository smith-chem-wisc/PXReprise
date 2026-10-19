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

    /// <summary>Sample-level columns whose rows for one source name disagree, and so got no value (G106).</summary>
    public List<SdrfConflict> Conflicts { get; init; } = [];
}

/// <summary>One sample-level SDRF column whose rows for one source name write different values (G106).</summary>
/// <param name="SampleId">The sample the rows share.</param>
/// <param name="Column">The column header, verbatim.</param>
/// <param name="Values">Each value as written, in first-seen order, with the runs of the rows that wrote it (null for a
/// row naming no data file).</param>
public sealed record SdrfConflict(string SampleId, string Column, IReadOnlyList<(string Value, IReadOnlyList<string?> Runs)> Values);

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

    /// <summary><c>sdrf._name</c>: the NT part of a cell, NULL for a reserved word. Also read by the catalog builder
    /// (G74), so there is one reading of an SDRF cell, not two.</summary>
    internal static string? Name(string? raw) => Clean(ParseValue(raw).GetValueOrDefault("NT", ""));

    /// <summary>Python's <c>int(x) if x and x.isdigit() else None</c>.</summary>
    /// <remarks>Under <see cref="IngestRules.Current"/> only decimal digits count: a superscript or other digit that
    /// is not a decimal (<c>²</c>) is not a number here and gives null. Python's <c>isdigit</c> accepted it and
    /// <c>int()</c> then raised, so one such cell refused the whole ingest (G83 item 8).</remarks>
    private static long? Digits(string? value, IngestRules rules) =>
        string.IsNullOrEmpty(value) ? null
        : rules == IngestRules.Current ? (value.All(char.IsDigit) ? PyText.ParseDecimalDigits(value) : null)
        : PyText.IsDigit(value) ? PyText.ParseDecimalDigits(value) : null;

    /// <summary>Reads one SDRF into schema rows.</summary>
    /// <param name="path">The deposited (or repaired) SDRF.</param>
    /// <param name="datasetId">ProteomeXchange accession, which prefixes every generated ID.</param>
    /// <param name="defaultOrganism">NCBITaxon CURIE from the ingest manifest, used when the SDRF names an
    /// organism this module has no term for.</param>
    /// <param name="log">Reader log to record the backend in.</param>
    /// <param name="rules">Under <see cref="IngestRules.Current"/>, a run named on several rows (a multiplexed run,
    /// one row per channel) keeps a run-level fact only where every row agrees, and NULL where they differ; Python
    /// 0.32.0 kept the last row's (G83 item 6). Likewise a source name on several rows keeps a sample-level column
    /// only where every row that writes it agrees, and gets no value where they differ, named in
    /// <see cref="SdrfTable.Conflicts"/>; Python 0.32.0 kept the first row's and dropped the rest silently (G106).</param>
    public static SdrfTable Parse(string path, string datasetId, string? defaultOrganism = null, ReaderLog? log = null, IngestRules rules = IngestRules.Current)
    {
        (List<string> header, List<List<string>> rows) = Readers.ReadSdrf(path, log);
        var samples = new Dictionary<string, Row>(StringComparer.Ordinal);
        var characteristics = new List<Row>();
        var assays = new List<Row>();
        var runFacts = new Dictionary<string, Row>(StringComparer.Ordinal);
        var sampleOfRun = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<SdrfConflict>();
        // Under Current a sample is written once all its rows are read, so every row has its say (G106).
        var rowsOfSample = new Dictionary<string, (string SourceName, List<(List<(string Column, string Raw)> Pairs, string? Run)> Rows)>(StringComparer.Ordinal);
        var sampleOrder = new List<string>();

        foreach (List<string> cells in rows)
        {
            // An SDRF column name is a POSITION, not a key: `comment[modification parameters]` can appear
            // many times in one file, and a name-keyed map keeps only the last. Lookups below use the map,
            // because no column they read repeats; copying characteristics verbatim walks the pairs, so a
            // repeated one is not silently dropped.
            var pairs = new List<(string Column, string Raw)>(header.Count);
            for (int i = 0; i < header.Count; i++)
                pairs.Add((header[i], i < cells.Count ? cells[i] ?? "" : ""));
            var row = KeyedRow(pairs);
            string Get(string key) => row.GetValueOrDefault(key, "");

            string sourceName = PyText.Strip(Get("source name"));
            if (sourceName.Length == 0)
                continue;
            string sampleId = $"{datasetId}:{sourceName}";
            string? dataFile = Clean(Get("comment[data file]")) ?? Clean(Get("assay name"));
            // An SDRF MetaMorpheus writes names the file it SEARCHED, which after Calibrate is `X-calib.mzML`,
            // not the deposited `X.raw` (sdrf D40, aging 069 DATAREPO-54). Runs are keyed on the deposited
            // name, so without the same stripping the USI path does, that run would silently get no sample,
            // instrument or fraction.
            string? runName = dataFile is null ? null : Usi.StripPipelineSuffix(PyText.PathStem(dataFile));

            if (rules == IngestRules.Current)
            {
                if (!rowsOfSample.TryGetValue(sampleId, out var held))
                {
                    rowsOfSample[sampleId] = held = (sourceName, []);
                    sampleOrder.Add(sampleId);
                }
                held.Rows.Add((pairs, runName));
            }
            else if (!samples.ContainsKey(sampleId))
                AddSample(sampleId, sourceName, pairs, row);

            if (runName is null)
                continue;
            sampleOfRun[runName] = sampleId;
            string? fraction = Clean(Get("comment[fraction identifier]"));
            string? technical = Clean(Get("comment[technical replicate]"));
            var facts = new Row
            {
                ["instrument_model"] = Name(Get("comment[instrument]")),
                ["instrument_term"] = Term(Get("comment[instrument]")),
                ["fraction"] = Digits(fraction, rules),
                ["technical_replicate"] = Digits(technical, rules),
                // SDRF-DR10: a drafted `1` is a default nothing established, so where it came from is
                // carried beside it. NULL until the SDRF writes the column.
                ["fraction_source"] = Verbatim(Get("comment[fraction identifier source]")),
                ["technical_replicate_source"] = Verbatim(Get("comment[technical replicate source]")),
                ["acquisition"] = Name(Get("comment[proteomics data acquisition method]")),
            };
            if (rules == IngestRules.Current && runFacts.TryGetValue(runName, out var earlier))
            {
                // One run, several rows: a fact of the RUN is whatever every row says, and no row's alone.
                foreach (var key in facts.Keys.ToList())
                    if (!Equals(earlier.GetValueOrDefault(key), facts[key])) facts[key] = null;
            }
            runFacts[runName] = facts;
            string channel = Channel(Get("comment[label]"));
            assays.Add(new Row
            {
                ["assay_id"] = $"{datasetId}:{runName}:{channel}",
                ["run_id"] = $"{datasetId}:{runName}",
                ["channel"] = channel,
                ["sample_id"] = sampleId,
            });
        }

        if (rules == IngestRules.Current)
        {
            bool[] sampleLevel = SampleLevelColumns(header);
            foreach (string sampleId in sampleOrder)
            {
                var (sourceName, held) = rowsOfSample[sampleId];
                int before = conflicts.Count;
                var merged = MergeSampleRows(sampleId, header, sampleLevel, held, conflicts);
                AddSample(sampleId, sourceName, merged, KeyedRow(merged),
                    conflicts.Skip(before).Select(c => c.Column).ToHashSet(StringComparer.Ordinal));
            }
        }

        return new SdrfTable
        {
            Samples = samples.Values.ToList(),
            Characteristics = characteristics,
            Assays = assays,
            RunFacts = runFacts,
            SampleOfRun = sampleOfRun,
            Columns = header.ToList(),
            Conflicts = conflicts,
        };

        // The Sample row and its SampleCharacteristic rows, from one row of the SDRF (Python0320: the source name's
        // first; Current: its rows merged, with `conflicted` naming the cells the rows disagreed on).
        void AddSample(string sampleId, string sourceName, List<(string Column, string Raw)> pairs, Dictionary<string, string> row,
            IReadOnlySet<string>? conflicted = null)
        {
            string Get(string key) => row.GetValueOrDefault(key, "");
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
                ["biological_replicate"] = Digits(replicate, rules),
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
                    // nothing in the file says so (G62). An own source the rows disagree on is not absent, so
                    // it is NULL too, never the row default (G75, G106).
                    ["source"] = conflicted?.Contains($"comment[{inner} source]") == true ? null
                        : Verbatim(Get($"comment[{inner} source]")) ?? defaultSource,
                    ["source_reference"] = Verbatim(Get($"comment[{inner} source reference]")),
                    ["source_method"] = Verbatim(Get($"comment[{inner} source method]")),
                });
            }
        }
    }

    /// <summary>A row's cells keyed by column name; a repeated name keeps the last.</summary>
    private static Dictionary<string, string> KeyedRow(List<(string Column, string Raw)> pairs)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string column, string raw) in pairs)
            row[column] = raw;
        return row;
    }

    /// <summary>Which columns describe the SAMPLE rather than the row's file: <c>characteristics[...]</c>,
    /// <c>factor value[...]</c>, <c>material type</c>, and the provenance comments the sample rows read
    /// (<c>comment[characteristics source]</c>, <c>comment[&lt;name&gt; source|source reference|source method]</c> for
    /// a characteristic or factor <c>&lt;name&gt;</c>).</summary>
    /// <remarks><c>comment[fraction identifier source]</c> is not among them: a fraction is a fact of the file.</remarks>
    private static bool[] SampleLevelColumns(List<string> header)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string column in header)
        {
            Match m = Column.Match(PyText.Strip(column));
            if (m.Success && m.Groups["kind"].Value.ToLowerInvariant() != "comment")
                names.Add(PyText.Strip(m.Groups["name"].Value));
        }
        var provenance = new HashSet<string>(StringComparer.Ordinal) { "comment[characteristics source]" };
        foreach (string name in names)
        {
            provenance.Add($"comment[{name} source]");
            provenance.Add($"comment[{name} source reference]");
            provenance.Add($"comment[{name} source method]");
        }
        return header.Select(column =>
        {
            string c = PyText.Strip(column);
            Match m = Column.Match(c);
            return (m.Success && m.Groups["kind"].Value.ToLowerInvariant() != "comment")
                || c == "material type" || provenance.Contains(c);
        }).ToArray();
    }

    /// <summary>One source name's rows as the single row its sample is read from (G106).</summary>
    /// <remarks>
    /// A sample-level cell is whatever every row that WRITES it says: an empty cell says nothing, so it neither
    /// agrees nor disagrees. Where two rows write different values (a reserved word included), the cell is left
    /// empty, so the sample gets no value there, and the conflict is recorded. The first row's value is never
    /// taken: PXD012307's SDRF names two animals, 24 and 3 months old, one source name, and the first row made all
    /// 32 runs 24 months. A cell that is not sample-level keeps the first row's; the sample rows never read it.
    /// </remarks>
    private static List<(string Column, string Raw)> MergeSampleRows(
        string sampleId, List<string> header, bool[] sampleLevel,
        List<(List<(string Column, string Raw)> Pairs, string? Run)> held, List<SdrfConflict> conflicts)
    {
        var first = held[0].Pairs;
        if (held.Count == 1)
            return first;
        var merged = new List<(string Column, string Raw)>(header.Count);
        for (int i = 0; i < header.Count; i++)
        {
            if (!sampleLevel[i])
            {
                merged.Add(first[i]);
                continue;
            }
            var written = new List<(string Value, string Raw, List<string?> Runs)>();
            foreach (var (pairs, run) in held)
            {
                string raw = pairs[i].Raw;
                string value = PyText.Strip(raw);
                if (value.Length == 0)
                    continue;
                int at = written.FindIndex(w => w.Value == value);
                if (at < 0)
                    written.Add((value, raw, [run]));
                else
                    written[at].Runs.Add(run);
            }
            if (written.Count > 1)
                conflicts.Add(new SdrfConflict(sampleId, PyText.Strip(header[i]),
                    written.Select(w => (w.Value, (IReadOnlyList<string?>)w.Runs)).ToList()));
            merged.Add((header[i], written.Count == 1 ? written[0].Raw : ""));
        }
        return merged;
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
