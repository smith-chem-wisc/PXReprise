using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using static DataRepo.Ingest.Sources.SourcesPy;

namespace DataRepo.Ingest.Sources;

/// <summary>One parsed and checked go annotation file.</summary>
public sealed class GoAnnotation
{
    public required string Path { get; init; }

    /// <summary>The <c>#!key value</c> lines, in file order.</summary>
    public required Dictionary<string, string> Header { get; init; }

    /// <summary>The table, one mapping per row, columns by name.</summary>
    public required List<Dictionary<string, string>> Rows { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>Rows read but not stored, by reason, so nothing is dropped without a count (insertion order, as a
    /// Python <c>Counter</c> keeps it).</summary>
    public Dictionary<string, long> NotStored { get; } = new(StringComparer.Ordinal);

    public string GoRelease => Header["go_release"];

    public string MzlibRelease => Header.GetValueOrDefault("mzlib_release", "");
}

/// <summary>One parsed category file: one consumer map's categories under one ontology release.</summary>
public sealed class GoCategories
{
    public required string Path { get; init; }
    public required Dictionary<string, string> Header { get; init; }
    public required string MapName { get; init; }
    public required string MapVersion { get; init; }
    public required string MapSha256 { get; init; }
    public required List<Dictionary<string, string>> Rows { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary><c>go</c>'s two output files -> <c>protein_localizations</c> and <c>organelle_term_categories</c> rows (G53).</summary>
/// <remarks>
/// <para>go (the engine) annotates every non-decoy protein group of a search with GO terms, and writes two files
/// per run (go D28): an <b>annotation file</b>, one row per (group, term), map-independent; and one <b>category
/// file per consumer map</b>, one row per (term, category, subcategory). The contract is go's own,
/// <c>go/design/PLAN.md</c> S5-S6 and rulings D1-D30. This module reads it and refuses a file that does not keep
/// it; it computes nothing about GO.</para>
/// <para>What it checks, each refusing the file on failure (<see cref="IngestException"/>):</para>
/// <list type="bullet">
/// <item><b>A released mzLib wrote it.</b> <c>#!mzlib_release none</c> is a build with no release tag. A reader
/// may be built and tested against such a file (GO-A1), but it is never ingested; the refusal names the commit
/// from <c>#!mzlib_version</c> (go 010 section 2). <c>allowPrerelease</c> exists for tests only.</item>
/// <item><b>The five header counters are the rows' own</b> (go D26), each counting groups at
/// <c>q_value &lt;= counter_q_value_max</c> (D29), recounted here so a reader tests go's writer.</item>
/// <item><b>Every row agrees with the header</b> on <c>go_release</c> and both sha256s, a non-empty <c>go_id</c>
/// means <c>annotated</c> (D19), and <c>n_with</c> is the size of <c>accession_used</c> (D22).</item>
/// <item><b>No group has entrapment members</b> (go D36's <c>entrapment_members</c>). An entrapment protein
/// carries its target's GO (#1271), so no file from an entrapment search is ingested until GO-E1 is answered
/// (our go 013). Before D36 the reader could not see this; now it refuses. go 018/020 (GO-D2): the refusal
/// stays until go's D39 is in a released mzLib; our go 019 then takes go 018 section 1.3's narrower check.</item>
/// <item><b>No accession sits in two groups.</b> go has never seen it (go 010 section 3), and a per-accession row
/// would then have two group q-values. Refused until MetaMorpheus says whether parsimony can do it (our go
/// 011).</item>
/// <item><b>Coverage</b> (go 009 section 4): every <c>go_id</c> of a category file is a term of its annotation
/// file, under the same <c>go_release</c> and ontology sha256. A mismatched pair stored would be a join that
/// silently drops categories.</item>
/// </list>
/// <para>What it does NOT store, and says so in the result rather than dropping it silently:</para>
/// <list type="bullet">
/// <item><b>Terms outside cellular_component.</b> <c>protein_localizations</c> is GO-CC by definition; go's file
/// also carries biological_process and molecular_function rows. They are not <c>protein_annotations</c> rows
/// either: that table holds an owner's per-protein facts (half-life, ELLP, ...), and go's BP/MF rows are counted
/// in <see cref="GoAnnotation.NotStored"/>, as the Python did.</item>
/// <item><b><c>annotation_status</c>.</b> Every stored row carries a term, so it would always read
/// <c>annotated</c>; a group with no term has no <c>protein_localizations</c> row at all and is counted in
/// <c>NotStored</c>. go's other per-row evidence (<c>protein_group</c>, <c>q_value</c>, <c>n_members</c>,
/// <c>n_with</c>, <c>inherited</c>, <c>propagated</c>) IS stored, from schema 0.0.10 (D28: one schema change
/// with the runner's <c>gene_resolutions</c>).</item>
/// <item><b>go D33's per-member columns</b> (<c>accession_direct</c>, <c>accession_inherited</c>,
/// <c>evidence_by_member</c>). They are checked by name and read, but not stored: storing them is a schema
/// decision not yet taken. <c>inherited</c> (stored) also covers a sequence variant from D34 on.</item>
/// </list>
/// <para>Header keys this reader does not use, such as D35's optional <c>#!unresolved_go_ids</c>, are kept in
/// <c>Header</c> and otherwise ignored.</para>
/// <para>Read in-house: mzLib 1.0.593 has go's writers (<c>GoAnnotationTsv</c>, <c>GoCategoryTsv</c>) but no
/// reader, and the checks above are the point of this module, not the parsing. Ported from
/// <c>sources/go.py</c> (dataRepo <c>fcdedcb</c>, the GO-D1 by-name reader); nothing in the Python ingest called
/// it (G86).</para>
/// </remarks>
public static class Go
{
    public const string AnnotationFormat = "1";
    public const string CategoryFormat = "1";

    /// <summary>As released in mzLib 1.0.593 (#1353 with go D33's three per-member columns, #1366 with D36's
    /// <c>entrapment_members</c>).</summary>
    /// <remarks>Looked up by name: order is not checked, but a missing or unknown column is refused, since an
    /// unknown column is a contract change nobody has decided to store or drop.</remarks>
    public static readonly IReadOnlyList<string> AnnotationColumns =
    [
        "protein_group", "accession_used", "accession_direct", "accession_inherited", "go_id", "go_name",
        "aspect", "evidence", "evidence_by_member", "inherited", "propagated", "n_members", "n_with",
        "entrapment_members", "annotation_status", "q_value", "go_release", "go_obo_sha256",
        "annotation_db_sha256",
    ];

    public static readonly IReadOnlyList<string> CategoryColumns = ["go_id", "category", "subcategory"];

    public static readonly IReadOnlyList<string> Statuses = ["annotated", "no_go_terms", "no_entry", "contaminant"];

    /// <summary>The aspect <c>protein_localizations</c> holds.</summary>
    public const string CellularComponent = "cellular_component";

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Python's <c>repr</c> of a tuple of strings (two or more).</summary>
    private static string TupleRepr(IEnumerable<string> items) => "(" + string.Join(", ", items.Select(i => Repr(i))) + ")";

    /// <summary>Python's <c>repr</c> of a list of strings.</summary>
    private static string ListRepr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => Repr(i))) + "]";

    /// <summary>Python's <c>int(text)</c>; a ValueError there is a <see cref="FormatException"/> here.</summary>
    private static long PyInt(string text)
    {
        var s = Strip(text);
        var body = s.Length > 0 && s[0] is '+' or '-' ? s[1..] : s;
        var ok = body.Length > 0 && char.IsAsciiDigit(body[0]) && char.IsAsciiDigit(body[^1]);
        for (var i = 0; ok && i < body.Length; i++)
            ok = char.IsAsciiDigit(body[i]) || (body[i] == '_' && body[i - 1] != '_');
        if (!ok) throw new FormatException($"invalid literal for int() with base 10: {Repr(text)}");
        var value = long.Parse(body.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture);
        return s[0] == '-' ? -value : value;
    }

    /// <summary>Python's <c>float(text)</c>; a ValueError there is a <see cref="FormatException"/> here.</summary>
    private static double PyFloat(string text) =>
        PyFormat.TryParseFloat(text, out var value) ? value
            : throw new FormatException($"could not convert string to float: {Repr(text)}");

    /// <summary><c>#!key value</c> header lines, then a tab-separated table with a header row.</summary>
    private static (Dictionary<string, string> Header, List<Dictionary<string, string>> Rows) Read(
        string path, string formatKey, string formatVersion, IReadOnlyList<string> columns)
    {
        var name = System.IO.Path.GetFileName(path);
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        // open(encoding="utf-8", newline=""): strict UTF-8, a BOM kept as a character, line endings untouched.
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        var lines = text.Split('\n');
        var bodyStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#!", StringComparison.Ordinal))
            {
                var (key, _, value) = Partition(line[2..], " ");
                if (header.ContainsKey(key))
                    throw new IngestException($"{name}: header key {Repr(key)} appears twice");
                header[key] = Strip(value);
                continue;
            }
            bodyStart = i;
            break;
        }
        if (header.Count == 0 || header.Keys.First() != formatKey)
            throw new IngestException($"{name}: the first header line must be `#!{formatKey}`");
        if (header[formatKey] != formatVersion)
            throw new IngestException($"{name}: {formatKey} {Repr(header[formatKey])}; this reader knows {formatVersion}");
        using var reader = CsvRecords(lines.Skip(bodyStart).Where(l => l != "")).GetEnumerator();
        if (!reader.MoveNext())
            throw new IngestException($"{name}: no column header row");
        var names = reader.Current;
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new IngestException($"{name}: a column name repeats in {ListRepr(names)}");
        var missing = columns.Where(c => !names.Contains(c)).ToList();
        var unknown = names.Where(c => !columns.Contains(c)).ToList();
        if (missing.Count > 0 || unknown.Count > 0)
            throw new IngestException(
                $"{name}: columns are not go's {ListRepr(columns)}: missing {ListRepr(missing)}, unknown {ListRepr(unknown)}");
        var rows = new List<Dictionary<string, string>>();
        var n = bodyStart + 2;
        while (reader.MoveNext())
        {
            var cells = reader.Current;
            if (cells.Count != names.Count)
                throw new IngestException($"{name}: line {n} has {cells.Count} cells, not {names.Count}");
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++) row[names[i]] = cells[i];
            rows.Add(row);
            n++;
        }
        return (header, rows);
    }

    private enum CsvState { StartRecord, StartField, InField, InQuotedField, QuoteInQuotedField, EatCrnl }

    /// <summary>Python's <c>csv.reader(lines, delimiter="\t")</c> over a list of strings with no <c>\n</c> in them.</summary>
    /// <remarks>
    /// <c>Modules/_csv.c</c>'s <c>parse_process_char</c> in the default <c>excel</c> dialect, driven one string at a
    /// time as <c>Reader.__next__</c> drives it. The text twin is <c>DataRepo.Study.PyCsv.Records</c>; this one is
    /// fed strings already split on <c>\n</c>, as go.py feeds it, so a quoted field that runs past one joins the
    /// next string with no newline, and a <c>\r</c> inside a line is an error rather than a line break.
    /// </remarks>
    private static IEnumerable<List<string>> CsvRecords(IEnumerable<string> lines)
    {
        const char delimiter = '\t', quote = '"';
        var state = CsvState.StartRecord;
        var fields = new List<string>();
        var field = new StringBuilder();
        var fieldStarted = false;

        void Save()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void Add(char c)
        {
            field.Append(c);
            fieldStarted = true;
        }

        void Process(char? c)
        {
            var eol = c is null;
            var newline = c is '\n' or '\r';
            switch (state)
            {
                case CsvState.StartRecord:
                    if (eol) return;
                    if (newline) { state = CsvState.EatCrnl; return; }
                    state = CsvState.StartField;
                    goto case CsvState.StartField;
                case CsvState.StartField:
                    if (newline || eol) { Save(); state = eol ? CsvState.StartRecord : CsvState.EatCrnl; }
                    else if (c == quote) state = CsvState.InQuotedField;
                    else if (c == delimiter) Save();
                    else { Add(c!.Value); state = CsvState.InField; }
                    return;
                case CsvState.InField:
                    if (newline || eol) { Save(); state = eol ? CsvState.StartRecord : CsvState.EatCrnl; }
                    else if (c == delimiter) { Save(); state = CsvState.StartField; }
                    else Add(c!.Value);
                    return;
                case CsvState.InQuotedField:
                    if (eol) return;
                    if (c == quote) state = CsvState.QuoteInQuotedField;
                    else Add(c!.Value);
                    return;
                case CsvState.QuoteInQuotedField:
                    if (c == quote) { Add(quote); state = CsvState.InQuotedField; }
                    else if (c == delimiter) { Save(); state = CsvState.StartField; }
                    else if (newline || eol) { Save(); state = eol ? CsvState.StartRecord : CsvState.EatCrnl; }
                    else { Add(c!.Value); state = CsvState.InField; }
                    return;
                case CsvState.EatCrnl:
                    if (newline) return;
                    if (eol) { state = CsvState.StartRecord; return; }
                    throw new FormatException("new-line character seen in unquoted field - do you need to open the file with newline=''?");
            }
        }

        foreach (var line in lines)
        {
            foreach (var c in line) Process(c);
            Process(null);
            if (state == CsvState.StartRecord)
            {
                yield return fields;
                fields = [];
            }
        }
        if (fieldStarted || state == CsvState.InQuotedField)
        {
            Save();
            yield return fields;
        }
    }

    private static void RequireRelease(string path, Dictionary<string, string> header, bool allowPrerelease)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!header.TryGetValue("mzlib_release", out var release))
            throw new IngestException($"{name}: no `#!mzlib_release` line, so which code wrote it is unknown");
        if (release == "none" && !allowPrerelease)
        {
            var commit = Partition(header.GetValueOrDefault("mzlib_version", ""), "+").Tail;
            if (commit.Length == 0) commit = "unknown";
            throw new IngestException(
                $"{name}: written by an unreleased mzLib (`#!mzlib_release none`, commit {commit}). "
                + "A pre-release file may be used to build and test a reader, never ingested (GO-A1).");
        }
    }

    private static List<string> SplitCell(string cell) => cell.Split(';').Where(x => x.Length > 0).ToList();

    /// <summary>go's <c>true</c>/<c>false</c>; anything else is refused, and empty is NULL (unknown, not false).</summary>
    public static bool? Flag(string cell)
    {
        if (cell == "") return null;
        if (cell is "true" or "false") return cell == "true";
        throw new IngestException($"expected `true` or `false`, found {Repr(cell)}");
    }

    /// <summary>Read and check one go annotation file.</summary>
    /// <exception cref="IngestException">Any check in the class remarks fails.</exception>
    public static GoAnnotation ReadAnnotation(string path, bool allowPrerelease = false)
    {
        var name = System.IO.Path.GetFileName(path);
        var (header, rows) = Read(path, "go_annotation_format", AnnotationFormat, AnnotationColumns);
        RequireRelease(path, header, allowPrerelease);
        foreach (var key in new[] { "go_release", "go_obo_sha256", "annotation_db_sha256", "counter_q_value_max", "n_multi_member_groups" }
                     .Concat(Statuses.Select(s => $"status_{s}")))
            if (!header.ContainsKey(key))
                throw new IngestException($"{name}: header has no `#!{key}`");
        var qMax = PyFloat(header["counter_q_value_max"]);

        var groupStatus = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupQ = new Dictionary<string, double>(StringComparer.Ordinal);
        var groupMembers = new Dictionary<string, long>(StringComparer.Ordinal);
        var groupsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var n = 0;
        foreach (var row in rows)
        {
            n++;
            var where = $"{name}: row {n} ({row["protein_group"]}, {(row["go_id"].Length > 0 ? row["go_id"] : "no term")})";
            foreach (var key in new[] { "go_release", "go_obo_sha256", "annotation_db_sha256" })
                if (row[key] != header[key])
                    throw new IngestException($"{where}: {key} {Repr(row[key])} differs from the header's {Repr(header[key])}");
            var status = row["annotation_status"];
            if (!Statuses.Contains(status))
                throw new IngestException($"{where}: annotation_status {Repr(status)} is not one of {TupleRepr(Statuses)}");
            if (row["go_id"].Length > 0 && status != "annotated")
                throw new IngestException($"{where}: carries a term but reads {Repr(status)}; a term means annotated (go D19)");
            var used = SplitCell(row["accession_used"]);
            if (row["go_id"].Length > 0 && PyInt(row["n_with"]) != used.Count)
                throw new IngestException($"{where}: n_with {row["n_with"]} but {used.Count} accession(s) used (go D22)");
            if (row["entrapment_members"].Length > 0)
                throw new IngestException(
                    $"{where}: entrapment members {Repr(row["entrapment_members"])}. An entrapment protein "
                    + "carries its target's GO (#1271), so no file from an entrapment search is ingested "
                    + "until GO-E1 is answered (dataRepo go 013).");
            var group = row["protein_group"];
            if (!groupStatus.TryAdd(group, status) && groupStatus[group] != status)
                throw new IngestException($"{where}: group has rows with two statuses");
            groupQ[group] = PyFloat(row["q_value"]);
            groupMembers[group] = PyInt(row["n_members"]);
            foreach (var accession in group.Split('|'))
            {
                if (!groupsOf.TryGetValue(accession, out var groups)) groupsOf[accession] = groups = [];
                groups.Add(group);
            }
        }

        var shared = groupsOf.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).OrderBy(a => a, CodePointOrder).ToList();
        if (shared.Count > 0)
            throw new IngestException(
                $"{name}: {shared.Count} accession(s) sit in more than one group, e.g. {ListRepr(shared.Take(3))}. A "
                + "per-accession row would carry two group q-values; refused until MetaMorpheus says whether "
                + "parsimony can produce this (dataRepo go 011).");

        var counted = groupStatus.Keys.Where(g => groupQ[g] <= qMax).ToList();
        var recount = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in Statuses) recount[$"status_{s}"] = counted.Count(g => groupStatus[g] == s);
        recount["n_multi_member_groups"] = counted.Count(g => groupMembers[g] > 1);
        var wrong = new List<(string Key, string Header, long Rows)>();
        foreach (var (k, v) in recount)
            if (PyInt(header[k]) != v) wrong.Add((k, header[k], v));
        if (wrong.Count > 0)
        {
            var detail = string.Join(", ", wrong.OrderBy(w => w.Key, CodePointOrder).Select(w => $"{w.Key} header {w.Header} rows {w.Rows}"));
            throw new IngestException(
                $"{name}: header counters disagree with the rows at q_value <= {PyFormat.FloatRepr(qMax)} (go D26, D29): {detail}");
        }

        var result = new GoAnnotation { Path = path, Header = header, Rows = rows, Sha256 = FileSha256(path) };
        foreach (var row in rows)
        {
            string? reason = null;
            if (row["go_id"].Length == 0) reason = $"no term ({row["annotation_status"]})";
            else if (row["aspect"] != CellularComponent) reason = $"aspect {row["aspect"]}";
            if (reason is not null) result.NotStored[reason] = result.NotStored.GetValueOrDefault(reason) + 1;
        }
        return result;
    }

    /// <summary>Read and check one go category file (one consumer map).</summary>
    /// <exception cref="IngestException">A bad header or table, or an unreleased writer.</exception>
    public static GoCategories ReadCategories(string path, bool allowPrerelease = false)
    {
        var name = System.IO.Path.GetFileName(path);
        var (header, rows) = Read(path, "go_category_format", CategoryFormat, CategoryColumns);
        RequireRelease(path, header, allowPrerelease);
        foreach (var key in new[] { "go_release", "go_obo_sha256", "category_map" })
            if (!header.ContainsKey(key))
                throw new IngestException($"{name}: header has no `#!{key}`");
        var parts = SplitWhitespace(header["category_map"]);
        if (parts.Count != 3)
            throw new IngestException($"{name}: `#!category_map` must be `<name> <version> <sha256>`");
        var seen = new Dictionary<(string, string, string), int>();
        foreach (var r in rows)
        {
            var key = (r["go_id"], r["category"], r["subcategory"]);
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }
        var repeated = seen.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (repeated.Count > 0)
        {
            var (a, b, c) = repeated[0];
            throw new IngestException($"{name}: {repeated.Count} (term, category, subcategory) row(s) repeat, e.g. {TupleRepr([a, b, c])}");
        }
        var n = 0;
        foreach (var row in rows)
        {
            n++;
            if (row["go_id"].Length == 0 || row["category"].Length == 0)
                throw new IngestException($"{name}: row {n} lacks a go_id or a category");
        }
        return new GoCategories
        {
            Path = path, Header = header, MapName = parts[0], MapVersion = parts[1], MapSha256 = parts[2],
            Rows = rows, Sha256 = FileSha256(path),
        };
    }

    /// <summary>Refuse a category file that is not the pair of this annotation file (go 009 section 4).</summary>
    /// <exception cref="IngestException">A different ontology release or sha256, or a category term the annotation
    /// file does not carry.</exception>
    public static void CheckCoverage(GoAnnotation annotation, GoCategories categories)
    {
        var categoriesName = System.IO.Path.GetFileName(categories.Path);
        foreach (var key in new[] { "go_release", "go_obo_sha256" })
            if (categories.Header[key] != annotation.Header[key])
                throw new IngestException(
                    $"{categoriesName}: {key} {Repr(categories.Header[key])} is not the annotation "
                    + $"file's {Repr(annotation.Header[key])}; the two files are not a pair");
        var terms = annotation.Rows.Select(r => r["go_id"]).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        var missing = categories.Rows.Select(r => r["go_id"]).Where(t => !terms.Contains(t))
            .Distinct(StringComparer.Ordinal).OrderBy(t => t, CodePointOrder).ToList();
        if (missing.Count > 0)
            throw new IngestException(
                $"{categoriesName}: {missing.Count} term(s) are not in {System.IO.Path.GetFileName(annotation.Path)}, e.g. "
                + $"{ListRepr(missing.Take(3))}. Stored together, they would be a join that silently drops categories.");
    }

    /// <summary>The <c>annotation_sources</c> row both tables cite.</summary>
    /// <remarks><c>annotation_sources</c> has no free-text column, so the version carries every pin: the ontology
    /// release, the code that wrote the file, and the three sha256s a second run must match.</remarks>
    public static Row SourceRow(GoAnnotation annotation) => new()
    {
        ["source_id"] = SourceId(annotation),
        ["owner_project"] = "go",
        ["version"] = $"{annotation.GoRelease}; mzLib {annotation.Header.GetValueOrDefault("mzlib_version", "?")}; "
            + $"file {annotation.Sha256}; go.obo {annotation.Header["go_obo_sha256"]}; "
            + $"annotation db {annotation.Header["annotation_db_sha256"]}",
        ["doi"] = null,
    };

    public static string SourceId(GoAnnotation annotation) => $"go:{annotation.GoRelease}:{annotation.Sha256[..12]}";

    /// <summary><c>protein_localizations</c>: one row per (accession, CC term), over <c>accession_used</c> only (go D22).</summary>
    /// <remarks>A group member that does not carry a term gets no row for it. Evidence codes stay <c>;</c>-joined
    /// as go wrote them.</remarks>
    public static List<Row> LocalizationRows(GoAnnotation annotation)
    {
        var src = SourceId(annotation);
        var output = new Dictionary<(string Accession, string Term), Row>();
        foreach (var row in annotation.Rows)
        {
            if (row["go_id"].Length == 0 || row["aspect"] != CellularComponent) continue;
            foreach (var accession in SplitCell(row["accession_used"]))
                output[(accession, row["go_id"])] = new Row
                {
                    ["protein_accession"] = accession,
                    ["compartment"] = row["go_id"],
                    ["go_release"] = annotation.GoRelease,
                    ["evidence"] = row["evidence"].Length > 0 ? row["evidence"] : null,
                    ["source_id"] = src,
                    ["protein_group"] = row["protein_group"],
                    ["q_value"] = PyFloat(row["q_value"]),
                    ["n_members"] = PyInt(row["n_members"]),
                    ["n_with"] = PyInt(row["n_with"]),
                    ["inherited"] = Flag(row["inherited"]),
                    ["propagated"] = Flag(row["propagated"]),
                };
        }
        return output.Keys
            .OrderBy(k => k.Accession, CodePointOrder).ThenBy(k => k.Term, CodePointOrder)
            .Select(k => output[k]).ToList();
    }

    /// <summary><c>organelle_term_categories</c>: one row per (term, category, subcategory) under one map.</summary>
    public static List<Row> CategoryRows(GoCategories categories, GoAnnotation annotation)
    {
        var src = SourceId(annotation);
        return categories.Rows
            .OrderBy(r => r["go_id"], CodePointOrder).ThenBy(r => r["category"], CodePointOrder).ThenBy(r => r["subcategory"], CodePointOrder)
            .Select(r => new Row
            {
                ["compartment"] = r["go_id"],
                ["category_map_name"] = categories.MapName,
                ["organelle_map_version"] = categories.MapVersion,
                ["go_release"] = categories.Header["go_release"],
                ["organelle_category"] = r["category"],
                ["organelle_subcategory"] = r["subcategory"].Length > 0 ? r["subcategory"] : null,
                ["source_id"] = src,
            })
            .ToList();
    }
}
