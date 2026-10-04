using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper.Configuration.Attributes;
using DataRepo.Bundle;
using MassSpectrometry;
using Omics.BioPolymerGroup;
using Readers;

namespace DataRepo.Ingest;

/// <summary>Which backend read which file, for the bundle manifest's <c>readers</c> block.</summary>
public sealed class ReaderLog
{
    /// <summary>One entry per file read: <c>file</c> (the file name only), <c>backend</c>, <c>rows</c>, and
    /// <c>note</c> only when there is one.</summary>
    public List<Row> Entries { get; } = [];

    /// <summary>Records that <paramref name="backend"/> read <paramref name="rows"/> rows from <paramref name="path"/>.</summary>
    public void Record(string path, string backend, long rows, string? note = null)
    {
        var entry = new Row { ["file"] = PyText.PathName(path), ["backend"] = backend, ["rows"] = rows };
        // Python: `**({"note": note} if note else {})`, so an empty note is no note.
        if (!string.IsNullOrEmpty(note))
            entry["note"] = note;
        Entries.Add(entry);
    }

    /// <summary>The distinct backends, sorted.</summary>
    public List<string> Backends() =>
        Entries.Select(e => (string)e["backend"]!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>PTM site occupancy cells of a MetaMorpheus protein-group table, one record per site entry.</summary>
/// <remarks>The C# counterpart of pyMzLib's <c>OccupancyRecords</c>, carrying the members the ingester reads.</remarks>
public sealed class OccupancyRecords
{
    /// <summary>One dict per site entry, keyed by <see cref="ColumnNames"/> in that order.</summary>
    public required List<Dictionary<string, object?>> Records { get; init; }

    /// <summary>Protein groups in the whole file: groups, not rows.</summary>
    public required long RecordCount { get; init; }

    /// <summary>Groups carried back: groups, not rows.</summary>
    public required long ReturnedCount { get; init; }

    /// <summary>Site rows in <see cref="Records"/>.</summary>
    public required long RowCount { get; init; }

    /// <summary>Occupancy cells the writer cut short or replaced with "Output too long for Excel".</summary>
    public required long TruncatedCellCount { get; init; }

    /// <summary><c>"count_occupancy: FormatException"</c> (or <c>intensity_occupancy</c>) when a cell was not
    /// an occupancy cell at all, sorted.</summary>
    public required List<string> FailedFields { get; init; }

    /// <summary>Every site column, when the file has no occupancy columns at all.</summary>
    public required List<string> AbsentFields { get; init; }

    /// <summary>The field names, in order.</summary>
    public required List<string> ColumnNames { get; init; }

    /// <summary>The file's sample-group labels, in header order.</summary>
    public required List<string> SampleLabels { get; init; }
}

/// <summary>Where each producer file is parsed, and by whom.</summary>
/// <remarks>
/// <para>
/// The rule (FRAMEWORK section 3): <b>mzLib's typed readers parse producer formats</b>, so dataRepo does
/// not grow a second, drifting implementation of somebody else's file format. The Python read them through
/// pyMzLib, whose bridge ran mzLib in a subprocess and projected its records into named columns; this port
/// calls the same mzLib types directly and reproduces that projection (pyMzLib bridge v0.4.0, mzLib 1.0.593),
/// so the code above sees the same column names, order and values, typed as Python decoded the bridge's JSON.
/// </para>
/// <para>
/// | File | Parsed by | Why |
/// |---|---|---|
/// | <c>AllPSMs.psmtsv</c>, <c>AllPeptides.psmtsv</c> | mzLib (pyMzLib <c>read_records</c>) | covered, typed |
/// | <c>AllQuantifiedPeaks.tsv</c> | in-house TSV | pyMzLib's reader wants an <c>MBR Score</c> column MetaMorpheus 1.1.11 does not write (DATAREPO-13) |
/// | <c>AllQuantifiedPeptides.tsv</c>, <c>AllQuantifiedProteinGroups.tsv</c> | in-house TSV | no pyMzLib reader (aging 006; asked as pyMzLib 005) |
/// | <c>*.sdrf.tsv</c> | mzLib <c>SdrfDocument</c> (pyMzLib <c>sdrf.read</c>) | covered since 0.1.1 |
/// | <c>results.txt</c>, <c>*.toml</c>, <c>Mods/*.txt</c>, <c>*.json</c> | in-house | not file formats pyMzLib owns |
/// | the searched protein database (<c>.xml</c>, <c>.fasta</c>) | in-house, <c>Sources/ProteinDb</c> | pyMzLib has no protein-database reader; only <c>(accession, sequence)</c> is read, to place PTM sites (DATAREPO-32) |
/// | go's <c>*_go_annotation.tsv</c>, <c>*_go_category_*.tsv</c> | in-house, <c>Sources/Go</c> | go's writer (mzLib PR B, draft #1353) is unreleased, so pyMzLib has no verb for it yet (pyMzLib 009, S9) |
/// </para>
/// <para>
/// <c>*.sdrf.tsv</c> is the one that has already gone the other way. dataRepo read it as plain TSV because
/// pyMzLib's <i>generic</i> projection joined header and cells with <c>;</c>, which SDRF values contain
/// themselves, so the columns could not be recovered (DATAREPO-13). pyMzLib 0.1.1 answered that with a
/// dedicated <c>pymzlib.sdrf</c> module, which on the real PXD036557 file agreed with the in-house read cell
/// for cell, including all 144 cells that contain a <c>;</c>. The generic records projection is still lossy on
/// an SDRF, so it must not be used for one.
/// </para>
/// <para>
/// None of the in-house reading interprets: it splits on tabs and hands back strings. Interpretation
/// (peptidoform notation, age normalization, metric definitions) stays with the projects that own it.
/// </para>
/// <para>
/// The Python windowed large <c>.psmtsv</c> reads (<c>WINDOW_BYTES</c>) only because the bridge returned
/// one JSON document and .NET cannot build a string past ~2 GB. Reading mzLib in-process has no such
/// answer to build, so the whole file is read at once; a window was the same parse, so the values match.
/// </para>
/// </remarks>
public static class Readers
{
    /// <summary>The backend name written to the reader log for a file mzLib read.</summary>
    public const string MzLibBackend = "mzlib";

    /// <summary>Read with mzLib; anything else falls to the in-house TSV reader with a reason.</summary>
    public static readonly IReadOnlyList<string> MzLibFormats = [".psmtsv"];

    // -------------------------------------------------------------------------------------------
    // SDRF
    // -------------------------------------------------------------------------------------------

    /// <summary>Reads an SDRF through mzLib's <see cref="SdrfDocument"/>, as a header and rows of raw cells.</summary>
    /// <remarks>
    /// <para>
    /// The dedicated SDRF reader is used rather than the generic records projection, which joins header
    /// and cells with <c>;</c> and cannot be split back apart because SDRF values contain <c>;</c> themselves.
    /// </para>
    /// <para>
    /// Column names are a <i>list</i> and may repeat -- <c>comment[modification parameters]</c> legitimately
    /// appears more than once -- so a name identifies a position, not a column. Cells are raw strings: the
    /// <c>NT=...;AC=...</c> grammar is left exactly as written, and interpreting it is <c>Sources.Sdrf</c>'s
    /// job, not the reader's. Rows are not padded: a ragged row stays short, as mzLib keeps it.
    /// </para>
    /// </remarks>
    /// <returns><c>(column names, rows)</c>, the same shape <see cref="ReadTsv"/> returns, so callers need not
    /// care which backend read the file.</returns>
    public static (List<string> Columns, List<List<string>> Rows) ReadSdrf(string path, ReaderLog? log = null)
    {
        RequireExists(path);
        SdrfDocument document;
        try
        {
            document = new SdrfDocument(path);
            document.LoadResults();
        }
        catch (MzLibUtil.MzLibException exception)
        {
            throw new ReaderUnavailableException($"{exception.Message}: '{path}'");
        }

        List<string> columns = document.Header.ToList();
        List<List<string>> rows = document.Results.Select(r => r.Cells.ToList()).ToList();
        log?.Record(path, MzLibBackend, rows.Count);
        return (columns, rows);
    }

    // -------------------------------------------------------------------------------------------
    // .psmtsv through mzLib's records projection
    // -------------------------------------------------------------------------------------------

    /// <summary>Reads a MetaMorpheus <c>.psmtsv</c> through mzLib, as columns.</summary>
    /// <remarks>
    /// The columns are mzLib's own record fields in snake_case, projected exactly as pyMzLib's
    /// <c>read_records</c> did (see <see cref="RecordProjection"/>). Composite fields that have no faithful
    /// column shape (matched fragment ions, protein-group tuples) are excluded; the verbatim text of the ion
    /// series is taken from the file's own column where needed.
    /// </remarks>
    /// <returns>The reader's native fields, one list per column, in column order.</returns>
    /// <exception cref="ReaderUnavailableException">mzLib does not recognise or cannot open the file.</exception>
    public static Dictionary<string, List<object?>> ReadPsmtsv(string path, ReaderLog? log = null)
    {
        IResultFile resultFile = OpenAny(path);

        // MsDataFileToResultFileAdapter.Results stays null until LoadResults() is called; on a
        // ResultFile<T> it is a harmless re-parse of a file about to be parsed anyway (bridge comment).
        resultFile.LoadResults();

        IReadOnlyList<object> all = RecordsOf(resultFile);
        Type? declared = RecordTypeOf(resultFile.GetType());
        RecordProjection projection = RecordProjection.For(declared ?? (all.Count > 0 ? all[0].GetType() : typeof(MsDataScan)));

        // Properties whose column this file's header does not have cross as null (absent_fields).
        HashSet<string> absentProperties = AbsentProperties(declared, path);
        HashSet<string> absent = projection.ColumnNamesOf(absentProperties).ToHashSet(StringComparer.Ordinal);

        var columns = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        var lists = projection.ColumnNames.Select(_ => new List<object?>(all.Count)).ToArray();
        foreach (object record in all)
        {
            int c = 0;
            foreach (object? cell in projection.Cells(record))
                lists[c++].Add(cell);
        }
        for (int c = 0; c < projection.ColumnNames.Count; c++)
        {
            string name = projection.ColumnNames[c];
            if (absent.Contains(name))
                lists[c] = Enumerable.Repeat<object?>(null, all.Count).ToList();
            // The bridge built a Dictionary too; Python's json kept the last of a repeated key at the
            // first one's position, which an indexer assignment also does.
            columns[name] = lists[c];
        }

        log?.Record(path, MzLibBackend, all.Count);
        return columns;
    }

    // -------------------------------------------------------------------------------------------
    // Occupancy
    // -------------------------------------------------------------------------------------------

    private static readonly string[] OccupancyColumnNames =
    [
        "protein_group_name", "sample_label", "basis", "entity_index", "position", "is_n_terminus",
        "modification", "fraction", "numerator", "denominator", "cell_is_truncated",
    ];

    // mzLib matches the per-sample columns by these prefixes; mirrored by the bridge ONLY to report which
    // of them a file has at all. "IntensityOccupancy_" must be tried before "Intensity_".
    private static readonly string[] ProteinGroupSamplePrefixes =
        ["IntensityOccupancy_", "CountOccupancy_", "SpectralCount_", "Intensity_"];

    /// <summary>MetaMorpheus's PTM site occupancy cells, one record per site entry, through mzLib.</summary>
    /// <remarks>
    /// Both <c>CountOccupancy_</c> and <c>IntensityOccupancy_</c> cells are parsed with mzLib's own
    /// <see cref="ModificationOccupancyCell"/> (#1347), so the delimiter traps QuantProject documents
    /// (DEF-OCC-DELIMITERS) are mzLib's to get right, not ours. A protein-group table is small, so it is read
    /// whole. Projected as pyMzLib's <c>read_occupancy</c> was.
    /// </remarks>
    /// <returns>The records (one dict per entry, with <c>basis</c>), and <c>TruncatedCellCount</c> /
    /// <c>FailedFields</c>, which the caller must report.</returns>
    /// <exception cref="ReaderUnavailableException">The file is not a protein-group table mzLib reads, or fewer
    /// groups came back than the file holds.</exception>
    public static OccupancyRecords ReadOccupancy(string path, ReaderLog? log = null)
    {
        IResultFile resultFile = OpenAny(path);
        if (resultFile is not ProteinGroupFromTsvFile file)
            throw new ReaderUnavailableException(
                $"'{resultFile.FileType}' files cannot be read by read-occupancy, which reads a MetaMorpheus " +
                $"protein-group or transcript-group table (file type {nameof(SupportedFileType.MetaMorpheusQuantifiedProteinGroups)}).");

        List<ProteinGroupFromTsv> all = file.Results;
        var rows = new List<object?[]>();
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        int truncatedCells = 0;

        foreach (ProteinGroupFromTsv group in all)
        {
            foreach (SampleGroupMeasurement sample in group.SampleGroups.Values)
            {
                AddSites(group, sample.Label, "count", "count_occupancy", sample.CountOccupancyText, () => sample.CountOccupancy);
                AddSites(group, sample.Label, "intensity", "intensity_occupancy", sample.IntensityOccupancyText, () => sample.IntensityOccupancy);
            }
        }

        void AddSites(ProteinGroupFromTsv group, string label, string basis, string field, string? text,
            Func<ModificationOccupancyCell> parse)
        {
            if (text is null)
                return;

            ModificationOccupancyCell cell;
            try
            {
                cell = parse();
            }
            catch (FormatException exception)
            {
                failures.Add($"{field}: {exception.GetType().Name}");
                return;
            }

            if (cell.IsTruncated)
                truncatedCells++;

            for (int entity = 0; entity < cell.Entities.Count; entity++)
            {
                foreach (OccupancySite site in cell.Entities[entity])
                {
                    rows.Add(TypedCells(
                        () => group.ProteinGroupName, () => label, () => basis, () => entity, () => site.Position,
                        () => site.IsNTerminus, () => site.ModificationIdWithMotif, () => site.Fraction,
                        () => site.Numerator, () => site.Denominator, () => cell.IsTruncated));
                }
            }
        }

        (HashSet<string> prefixes, List<string> labels) = SampleHeaderOf(path, ProteinGroupSamplePrefixes);
        var absent = new List<string>();
        if (!prefixes.Contains("CountOccupancy_") && !prefixes.Contains("IntensityOccupancy_"))
            absent.AddRange(OccupancyColumnNames.Skip(2));
        int[] absentPositions = absent.Select(n => Array.IndexOf(OccupancyColumnNames, n)).Where(i => i >= 0).ToArray();

        var records = new List<Dictionary<string, object?>>(rows.Count);
        foreach (object?[] cells in rows)
        {
            foreach (int position in absentPositions)
                cells[position] = null;
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int c = 0; c < OccupancyColumnNames.Length; c++)
                record[OccupancyColumnNames[c]] = cells[c];
            records.Add(record);
        }

        var result = new OccupancyRecords
        {
            Records = records,
            RecordCount = all.Count,
            ReturnedCount = all.Count,
            RowCount = rows.Count,
            TruncatedCellCount = truncatedCells,
            FailedFields = [.. failures],
            AbsentFields = absent,
            ColumnNames = [.. OccupancyColumnNames],
            SampleLabels = labels,
        };
        if (result.ReturnedCount != result.RecordCount)
            throw new ReaderUnavailableException($"mzLib returned part of {path}'s occupancy");
        log?.Record(path, MzLibBackend, result.Records.Count, "read_occupancy: one record per site entry");
        return result;
    }

    /// <summary>One record's cells through a typed view: a column that throws gives a null cell.</summary>
    private static object?[] TypedCells(params Func<object?>[] columns)
    {
        var cells = new object?[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            try
            {
                cells[c] = Wire.AsPython(Wire.WireValue(columns[c]()));
            }
            catch (Exception)
            {
                cells[c] = null;
            }
        }
        return cells;
    }

    /// <summary>The per-sample columns of a header: which prefixes it has, and its labels in order.</summary>
    private static (HashSet<string> Prefixes, List<string> Labels) SampleHeaderOf(string path, string[] prefixes)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (string cell in HeaderCells(path))
        {
            string? prefix = prefixes.FirstOrDefault(p => cell.StartsWith(p, StringComparison.Ordinal));
            if (prefix is null)
                continue;
            present.Add(prefix);
            string label = cell[prefix.Length..];
            if (!labels.Contains(label))
                labels.Add(label);
        }
        return (present, labels);
    }

    /// <summary>The header cells of a tab-separated file, or an empty list when it cannot be read.</summary>
    private static IReadOnlyList<string> HeaderCells(string path)
    {
        try
        {
            string? line = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            return line is null ? [] : line.Split('\t').Select(cell => cell.Trim()).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    // -------------------------------------------------------------------------------------------
    // In-house TSV and results.txt
    // -------------------------------------------------------------------------------------------

    /// <summary>Python's <c>csv</c> default <c>field_size_limit</c>: a longer field is an error there.</summary>
    private const int CsvFieldSizeLimit = 131072;

    /// <summary>Reads a tab-separated file as a header and a list of rows, with no interpretation.</summary>
    /// <remarks>
    /// Python's <c>csv.reader(delimiter='\t', quoting=QUOTE_NONE)</c> over the file opened <c>utf-8-sig</c>
    /// with <c>newline=""</c>: a row ends at <c>\n</c>, <c>\r</c> or <c>\r\n</c>; quotes are ordinary
    /// characters; the first row is the header even when it is empty; later empty rows are dropped.
    /// </remarks>
    public static (List<string> Header, List<List<string>> Rows) ReadTsv(string path, ReaderLog? log = null, string? note = null)
    {
        string text = PyText.DecodeUtf8Sig(File.ReadAllBytes(path), strict: true);
        var header = new List<string>();
        var rows = new List<List<string>>();
        bool first = true;
        foreach (string line in PyText.SplitCsvLines(text))
        {
            List<string> row = line.Length == 0 ? [] : line.Split('\t').ToList();
            foreach (string field in row)
            {
                if (field.Length > CsvFieldSizeLimit)
                    throw new InvalidDataException($"field larger than field limit ({CsvFieldSizeLimit})");
            }
            if (first)
            {
                header = row;
                first = false;
            }
            else if (row.Count > 0)
            {
                rows.Add(row);
            }
        }
        log?.Record(path, "datarepo-tsv", rows.Count, note);
        return (header, rows);
    }

    /// <summary>Zips a header onto rows, tolerating short rows (trailing empty cells are dropped by writers).</summary>
    /// <remarks>A repeated header name keeps its first position and its last value, as Python's
    /// <c>dict(zip(...))</c> does; cells past the header are dropped.</remarks>
    public static IEnumerable<Dictionary<string, string>> IterDicts(List<string> header, List<List<string>> rows)
    {
        int width = header.Count;
        foreach (List<string> row in rows)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < width; i++)
                d[header[i]] = i < row.Count ? row[i] : "";
            yield return d;
        }
    }

    private static readonly Regex ResultsTotal = new(
        @"^All target (?<what>\w+(?: \w+)*) with q-value <= 0\.01.*?:\s*(?<n>\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex ResultsPerFile = new(
        @"^(?<run>\S+) - Target (?<what>\w+(?: \w+)*) with q-value <= 0\.01.*?:\s*(?<n>\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex ResultsScans = new(
        @"^(?:(?<run>\S+) - )?All (?<what>MS2 Scans|Precursors):\s*(?<n>\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex ResultsScansPerFile = new(
        @"^(?<run>\S+) - (?<what>MS2 Scans|Precursors):\s*(?<n>\d+)", RegexOptions.CultureInvariant);

    /// <summary>Pulls the counted totals out of MetaMorpheus's free-text <c>results.txt</c>.</summary>
    /// <remarks>
    /// These are the numbers the ingest reconciles against: <c>All target PSMs with q-value &lt;= 0.01</c> is
    /// aging's canonical <c>DEF-PSM-1PCT v1</c> (aging 006), and it differs on purpose from the FDR engine's
    /// own log line that older provenance records as <c>psms_1pct</c>.
    /// </remarks>
    /// <returns><c>{scope: {name: count}}</c> where scope is <c>""</c> for dataset-wide totals or the run name.</returns>
    public static Dictionary<string, Dictionary<string, long>> ReadResultsTxt(string path, ReaderLog? log = null)
    {
        var output = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        string text = PyText.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false);
        foreach (string raw in PyText.SplitLines(text))
        {
            string line = PyText.Strip(raw);
            foreach (Regex pattern in (Regex[])[ResultsPerFile, ResultsTotal, ResultsScansPerFile, ResultsScans])
            {
                Match m = pattern.Match(line);
                if (!m.Success)
                    continue;
                Group run = m.Groups["run"];
                string scope = PyText.Strip(run.Success ? run.Value : "");
                string name = PyText.Strip(m.Groups["what"].Value).ToLowerInvariant().Replace(" ", "_");
                if (!output.TryGetValue(scope, out var counts))
                    output[scope] = counts = new Dictionary<string, long>(StringComparer.Ordinal);
                counts[name] = PyText.ParseDecimalDigits(m.Groups["n"].Value);
                break;
            }
        }
        log?.Record(path, "datarepo-text", output.Values.Sum(v => (long)v.Count));
        return output;
    }

    // -------------------------------------------------------------------------------------------
    // Opening, and the records projection (pyMzLib bridge v0.4.0, Reading.Records.cs)
    // -------------------------------------------------------------------------------------------

    private static void RequireExists(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new ReaderUnavailableException($"File not found: '{path}'.");
    }

    /// <summary>Opens a path as whatever mzLib says it is, or explains why it cannot.</summary>
    private static IResultFile OpenAny(string path)
    {
        RequireExists(path);
        try
        {
            return FileReader.ReadResultFile(path);
        }
        catch (MzLibUtil.MzLibException exception)
        {
            throw new ReaderUnavailableException(
                $"{exception.Message}: '{path}'. The formats listing enumerates every file type mzLib recognises.");
        }
    }

    /// <summary>The records mzLib parsed, whatever the reader's record type is (its <c>Results</c>, by name).</summary>
    private static IReadOnlyList<object> RecordsOf(IResultFile resultFile)
    {
        PropertyInfo? results = resultFile.GetType().GetProperty("Results",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

        if (results?.GetValue(resultFile) is IEnumerable enumerable and not string)
            return enumerable.Cast<object>().Where(item => item is not null).ToList();

        return [];
    }

    /// <summary>The <c>T</c> of the <c>ResultFile&lt;T&gt;</c> a reader derives from, if it does.</summary>
    private static Type? RecordTypeOf(Type readerType)
    {
        for (Type? type = readerType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ResultFile<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>
    /// The properties of a CsvHelper-mapped record type whose column this file's header does not have, so
    /// every value of that property is mzLib's default, not something the file said.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative, as in the bridge: a type with no <c>[Name]</c> attributes, a compressed input,
    /// or a header lacking any REQUIRED column gives an empty answer, which means "no basis to say".
    /// </remarks>
    private static HashSet<string> AbsentProperties(Type? recordType, string path)
    {
        var absent = new HashSet<string>(StringComparer.Ordinal);
        if (recordType is null || !File.Exists(path) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return absent;

        var mapped = new List<(PropertyInfo Property, string[] Names, bool Optional)>();
        bool anyNamed = false;
        foreach (PropertyInfo property in recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0 || property.IsDefined(typeof(IgnoreAttribute), true))
                continue;
            NameAttribute? name = property.GetCustomAttribute<NameAttribute>(true);
            if (name is null)
                continue;
            anyNamed = true;
            mapped.Add((property, name.Names, property.IsDefined(typeof(OptionalAttribute), true)));
        }

        if (!anyNamed)
            return absent;

        string? headerLine;
        try
        {
            headerLine = File.ReadLines(path).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        }
        catch (IOException)
        {
            return absent;
        }

        if (headerLine is null)
            return absent;

        char delimiter = headerLine.Contains('\t') ? '\t' : ',';
        var header = new HashSet<string>(
            headerLine.Split(delimiter).Select(cell => cell.Trim().Trim('"')), StringComparer.OrdinalIgnoreCase);

        if (mapped.Any(m => !m.Optional && !m.Names.Any(header.Contains)))
            return absent;

        foreach ((PropertyInfo property, string[] names, bool optional) in mapped)
        {
            if (optional && !names.Any(header.Contains))
                absent.Add(property.Name);
        }
        return absent;
    }

    /// <summary>The columns an arbitrary mzLib record type projects onto, worked out once per type.</summary>
    /// <remarks>
    /// <para>
    /// Public instance properties, base class first and in declaration order, each named by
    /// <see cref="SnakeCase"/>. A scalar crosses as itself; a list of scalars crosses <c>;</c>-joined;
    /// anything else (a dictionary, a list of composites, a composite) does not cross at all. An SDRF row's
    /// <c>Header</c> and <c>Cells</c> never cross (PYB-2).
    /// </para>
    /// <para>
    /// Non-finite doubles cross as null. The <c>-1</c> "absent" sentinel is applied ONLY to
    /// <see cref="IQuantifiableRecord.RetentionTime"/> and <see cref="IQuantifiableRecord.MonoisotopicMass"/>,
    /// the two members mzLib documents it on; elsewhere -1 is often a real measurement. A property that throws
    /// gives a null cell.
    /// </para>
    /// </remarks>
    private sealed class RecordProjection
    {
        private static readonly Dictionary<Type, RecordProjection> Cache = [];

        private readonly List<(string Name, PropertyInfo Property)> _fields;
        private readonly HashSet<string> _sentinelFields;

        public IReadOnlyList<string> ColumnNames { get; }

        private RecordProjection(Type recordType)
        {
            var fields = new List<(string, PropertyInfo)>();
            foreach (PropertyInfo property in PropertiesOf(recordType))
            {
                bool sdrfList = typeof(SdrfRow).IsAssignableFrom(recordType)
                    && property.Name is nameof(SdrfRow.Header) or nameof(SdrfRow.Cells);
                if (!sdrfList && IsColumn(property))
                    fields.Add((SnakeCase(property.Name), property));
            }
            _fields = fields;
            ColumnNames = fields.Select(f => f.Item1).ToList();
            _sentinelFields = typeof(IQuantifiableRecord).IsAssignableFrom(recordType)
                ? [nameof(IQuantifiableRecord.RetentionTime), nameof(IQuantifiableRecord.MonoisotopicMass)]
                : [];
        }

        public static RecordProjection For(Type recordType)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(recordType, out RecordProjection? projection))
                    Cache[recordType] = projection = new RecordProjection(recordType);
                return projection;
            }
        }

        private static IEnumerable<PropertyInfo> PropertiesOf(Type recordType)
        {
            var chain = new List<Type>();
            for (Type? type = recordType; type is not null && type != typeof(object); type = type.BaseType)
                chain.Insert(0, type);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type type in chain)
            {
                foreach (PropertyInfo property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length > 0)
                        continue;
                    if (property.GetMethod is null || !property.CanRead)
                        continue;
                    if (seen.Add(property.Name))
                        yield return property;
                }
            }
        }

        private static bool IsColumn(PropertyInfo property)
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsScalar(type))
                return true;
            return SequenceElementOf(type) is Type element && IsScalar(Nullable.GetUnderlyingType(element) ?? element);
        }

        private static bool IsScalar(Type type) =>
            type.IsPrimitive || type.IsEnum
            || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(Guid);

        private static Type? SequenceElementOf(Type type)
        {
            if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
                return null;
            if (type.IsArray)
                return type.GetElementType();
            foreach (Type contract in type.GetInterfaces().Prepend(type))
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return contract.GetGenericArguments()[0];
            }
            return typeof(object);
        }

        private object? Read(PropertyInfo property, object record)
        {
            object? value;
            try
            {
                value = property.GetValue(record);
            }
            catch (Exception)
            {
                return null;
            }

            if (value is double number && _sentinelFields.Contains(property.Name))
                return Wire.NullIfSentinel(number);

            return Normalize(value);
        }

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            double or float => Wire.WireValue(value),
            DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan span => span.ToString(null, CultureInfo.InvariantCulture),
            Enum name => name.ToString(),
            string text => text,
            IEnumerable sequence => string.Join(";", sequence.Cast<object?>()
                .Select(item => Convert.ToString(Normalize(item), CultureInfo.InvariantCulture) ?? string.Empty)),
            _ => value,
        };

        /// <summary>The cell values of one record, in column order, as Python received them.</summary>
        public IEnumerable<object?> Cells(object record) =>
            _fields.Select(field => Wire.AsPython(Read(field.Property, record)));

        public List<string> ColumnNamesOf(ISet<string> propertyNames) =>
            _fields.Where(field => propertyNames.Contains(field.Property.Name)).Select(field => field.Name).ToList();
    }

    /// <summary>mzLib's PascalCase property names as the bridge's snake_case column names.</summary>
    /// <remarks>Consecutive capitals stay together (<c>EValue</c> -> <c>e_value</c>, <c>MIScore</c> ->
    /// <c>mi_score</c>); a digit starts no new word (<c>MS2RetentionTime</c> -> <c>ms2_retention_time</c>); a
    /// pluralising <c>s</c> belongs to its acronym (<c>FixedPTMs</c> -> <c>fixed_ptms</c>).</remarks>
    internal static string SnakeCase(string name)
    {
        var built = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];
            if (char.IsUpper(current) && i > 0)
            {
                bool previousWasLowerOrDigit = char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]);
                if (previousWasLowerOrDigit || (char.IsUpper(name[i - 1]) && StartsNewWord(name, i)))
                    built.Append('_');
            }
            built.Append(char.ToLowerInvariant(current));
        }
        return built.ToString();
    }

    private static bool StartsNewWord(string name, int index)
    {
        if (index + 1 >= name.Length || !char.IsLower(name[index + 1]))
            return false;
        bool pluralising = name[index + 1] == 's'
            && (index + 2 >= name.Length || char.IsUpper(name[index + 2]));
        return !pluralising;
    }

    /// <summary>The bridge's wire rules, and what Python's <c>json</c> made of them.</summary>
    private static class Wire
    {
        private const double AbsentSentinel = -1;

        /// <summary>mzLib's -1 "absent" sentinel, and any non-finite value, as null.</summary>
        public static double? NullIfSentinel(double value) =>
            double.IsFinite(value) && Math.Abs(value - AbsentSentinel) > 1e-9 ? value : null;

        /// <summary>Non-finite doubles as null: JSON cannot carry them.</summary>
        public static object? WireValue(object? value) => value switch
        {
            double number => double.IsFinite(number) ? number : null,
            float number => float.IsFinite(number) ? number : null,
            _ => value,
        };

        /// <summary>
        /// A wire value as Python received it: System.Text.Json's text for it, decoded by <c>json.loads</c>.
        /// </summary>
        /// <remarks>
        /// <b>A whole-valued double reaches Python as an int.</b> System.Text.Json writes a double in its
        /// shortest round-trip form, so <c>2.0</c> goes out as <c>2</c>, and <c>json.loads</c> reads a number
        /// with no fraction or exponent as <c>int</c>. So mzLib's <c>-1.0</c> spectral angle was <c>-1</c> in
        /// Python and its <c>0.25</c> stayed a float. Every integer type becomes a <c>long</c>; an enum or a
        /// char is a string.
        /// </remarks>
        public static object? AsPython(object? value) => value switch
        {
            null => null,
            double d => FromJsonNumber(d.ToString("R", CultureInfo.InvariantCulture)),
            float f => FromJsonNumber(f.ToString("R", CultureInfo.InvariantCulture)),
            decimal m => FromJsonNumber(m.ToString(CultureInfo.InvariantCulture)),
            bool b => b,
            string s => s,
            char c => c.ToString(),
            sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ulong u => u <= long.MaxValue ? (long)u : FromJsonNumber(u.ToString(CultureInfo.InvariantCulture) + ".0"),
            Enum e => e.ToString(),
            Guid g => g.ToString("D"),
            _ => value,
        };

        // Both arms boxed explicitly: a bare `? double : long` would widen the long to a double.
        private static object FromJsonNumber(string text) =>
            text.IndexOfAny(['.', 'e', 'E']) >= 0
                ? (object)double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : (object)long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}

/// <summary>Python's string semantics where the ported modules depend on them.</summary>
internal static class PyText
{
    /// <summary>Python's <c>str.isspace</c> set: .NET's <see cref="char.IsWhiteSpace(char)"/> plus U+001C..U+001F.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');

    /// <summary>Python's <c>str.strip()</c>.</summary>
    public static string Strip(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Python's <c>str.isdigit()</c>: non-empty, every character a digit (Nd, or a No digit such as ²).</summary>
    public static bool IsDigit(string s) =>
        s.Length > 0 && s.All(c => char.IsDigit(c) || IsOtherDigit(c));

    private static bool IsOtherDigit(char c)
    {
        if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.OtherNumber)
            return false;
        double v = CharUnicodeInfo.GetNumericValue(c);
        return v >= 0 && v <= 9 && v == Math.Floor(v);
    }

    /// <summary>Python's <c>int(s)</c> for a string <see cref="IsDigit"/> accepted.</summary>
    /// <exception cref="FormatException">A digit character <c>int()</c> rejects (e.g. a superscript), as
    /// Python's <c>ValueError</c>.</exception>
    public static long ParseDecimalDigits(string s)
    {
        long value = 0;
        foreach (char c in s)
        {
            if (!char.IsDigit(c))
                throw new FormatException($"invalid literal for int() with base 10: '{s}'");
            value = checked(value * 10 + (long)CharUnicodeInfo.GetNumericValue(c));
        }
        return value;
    }

    /// <summary>Decodes UTF-8 as Python's <c>utf-8-sig</c>: one leading BOM is dropped.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="strict">Raise on invalid bytes (<c>errors="strict"</c>) rather than replace them.</param>
    public static string DecodeUtf8Sig(byte[] bytes, bool strict)
    {
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var encoding = new UTF8Encoding(false, throwOnInvalidBytes: strict);
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    /// <summary>The lines a file opened with <c>newline=""</c> yields to <c>csv.reader</c>, terminators removed:
    /// a line ends at <c>\n</c>, <c>\r</c> or <c>\r\n</c>.</summary>
    public static IEnumerable<string> SplitCsvLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r')
                continue;
            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'))
                continue;
            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>pathlib's <c>WindowsPath(path).name</c>: the last component, <c>/</c> and <c>\</c> both separators,
    /// a drive (<c>C:</c>, <c>\\server\share</c>) never a name.</summary>
    public static string PathName(string path)
    {
        string rest = path;
        if (rest.Length >= 2 && (rest[0] is '/' or '\\') && (rest[1] is '/' or '\\'))
        {
            // UNC: \\server\share is the drive.
            string[] unc = rest[2..].Split('/', '\\');
            rest = unc.Length > 2 ? string.Join('\\', unc[2..]) : "";
        }
        else if (rest.Length >= 2 && rest[1] == ':' && char.IsAsciiLetter(rest[0]))
        {
            rest = rest[2..];
        }
        string[] parts = rest.Split('/', '\\').Where(p => p.Length > 0 && p != ".").ToArray();
        return parts.Length == 0 ? "" : parts[^1];
    }

    /// <summary>pathlib's <c>WindowsPath(path).stem</c> (Python 3.13): the name without its last suffix, where a
    /// suffix needs a dot that is neither first nor last.</summary>
    public static string PathStem(string path)
    {
        string name = PathName(path);
        int i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[..i] : name;
    }
}
