using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Ingest.Sources;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DataRepo.Tests")]

namespace DataRepo.Mcp;

/// <summary>One query's answer, with everything needed to say how complete it is.</summary>
public sealed class BoundedResult
{
    public required List<string> Columns { get; init; }
    public required List<object?[]> Rows { get; init; }
    public bool Truncated { get; init; }
    /// <summary>Why it was cut short: <c>"rows"</c>, <c>"characters"</c>, or null when it is whole.</summary>
    public string? TruncatedBy { get; init; }
    public double ElapsedSeconds { get; init; }
    /// <summary>The caps in force, so a caller can report them without reading this class's constants.</summary>
    public required Dictionary<string, object?> Caps { get; init; }
    public int RowCount => Rows.Count;
}

/// <summary>A bounded, read-only DuckDB connection over one catalog (C# port of <c>sandbox.py</c>).</summary>
/// <remarks>
/// <para>This is stage one of D14: what an agent gets when it runs SQL through <c>datarepo mcp</c>. It is not the
/// sqlglot AST allow-list of FRAMEWORK section 4 -- that is stage two, for when D2's public no-login endpoint exists
/// and the thing being bounded is an attacker. Locally the agent already has the filesystem through Claude Code, so
/// the job here is <b>blast radius and provenance</b>, not security: a query should not be able to run for an hour,
/// return a million rows into a context window, or -- the one that matters most -- quietly answer from data that is
/// not this catalog.</para>
/// <para>Everything below was measured on DuckDB 1.5.5 against a real catalog, because the obvious assumptions are
/// wrong:</para>
/// <list type="bullet">
/// <item><b>Read-only alone is not a sandbox.</b> A read-only connection ran <c>read_csv_auto</c> on a file outside
/// the store and returned its 2 rows.</item>
/// <item><b><c>enable_external_access=false</c> closes that</b> and cannot be undone from inside the session:
/// <c>SET</c>, <c>PRAGMA</c> and <c>RESET</c> all fail with "Cannot enable external access while database is
/// running", and <c>INSTALL</c>/<c>LOAD</c>/<c>COPY ... TO</c>/<c>glob()</c> are refused with it.</item>
/// <item><b>It does not close <c>ATTACH</c>.</b> With external access off, <c>ATTACH 'other.duckdb' (READ_ONLY)</c>
/// still succeeded -- so an agent could read any other DuckDB file on the machine and return rows under a result
/// labelled with THIS catalog's <c>catalog_id</c>. That is a D13 violation before it is a security one.
/// <c>SET disabled_filesystems='LocalFileSystem'</c>, issued after the connection is open, refuses the ATTACH and is
/// itself one-way. The already-open catalog keeps serving.</item>
/// <item><b>The <c>range(3e9)</c> probe no longer demonstrates a timeout.</b> DuckDB 1.5 answers
/// <c>SELECT count(*) FROM range(3000000000)</c> from metadata in half a second. A real cross join is needed to
/// show the watchdog working, and it does.</item>
/// </list>
/// <para>The filesystem lock belongs to the DuckDB <b>database instance</b>, not to the connection: in one process,
/// a second connection to the same file shares it, and DuckDB refuses a second connection with a different config.
/// The server is its own process, so this costs nothing there.</para>
/// <para>The row and character caps are applied by <b>reading fewer rows</b>, never by rewriting the query. A
/// wrapped <c>SELECT * FROM (&lt;their sql&gt;) LIMIT n</c> would silently change the meaning of a statement that is
/// not a plain SELECT, and a tool that edits a question before answering it is the shape of a silently-wrong
/// answer.</para>
/// <para>The statement kind comes from DuckDB's own parser: <c>duckdb_extract_statements</c> counts the statements
/// and reports a parse error in the words Python's <c>duckdb.extract_statements</c> uses, and each statement's kind
/// is DuckDB's <c>duckdb_prepared_statement_type</c>. A statement that parses but cannot be prepared (a write to a
/// table that does not exist, a COPY the locked filesystem refuses) has no prepared type; its kind is then read off
/// its leading keyword, which is how DuckDB's transformer assigns it.</para>
/// </remarks>
public sealed class Sandbox : IDisposable
{
    /// <summary>Set immediately after connecting: it cannot be passed at connect time and cannot be reset afterwards.
    /// This is what closes the ATTACH hole that <c>enable_external_access</c> leaves open.</summary>
    public const string DisabledFilesystems = "LocalFileSystem";

    /// <summary>Caps, from D14. A result at either cap is still returned, with <c>truncated</c> set and a reason.</summary>
    public const int RowCap = 1_000;

    public const int CharCap = 50_000;

    /// <summary>Wall-clock seconds before the watchdog interrupts the connection. DuckDB 1.5 has no
    /// <c>statement_timeout</c> setting, so the deadline is enforced from a timer of ours.</summary>
    public const double TimeoutSecondsDefault = 30.0;

    /// <summary>Statement kinds this sandbox will execute. Everything else is refused <b>by name, before it runs</b>.</summary>
    public static readonly IReadOnlyList<string> AllowedStatements = ["SELECT", "EXPLAIN", "PRAGMA", "SHOW"];

    /// <summary>Table functions that can read a table the parse cannot see: they take the table name as a STRING, so
    /// meeting one means the answer is "cannot tell", never "touches nothing".</summary>
    public static readonly IReadOnlySet<string> OpaqueTableFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "query", "query_table", "read_parquet", "read_csv", "read_csv_auto", "read_json", "read_json_auto",
    };

    private readonly DuckDBConnection _con;

    /// <summary><c>str(Path(path))</c>: the catalog as the caller named it.</summary>
    public string Path { get; }

    public double TimeoutSeconds { get; }

    public int RowCapValue { get; }

    public int CharCapValue { get; }

    /// <exception cref="CatalogException">There is no catalog at <paramref name="path"/>, or it cannot be opened
    /// read-only.</exception>
    public Sandbox(string path, double timeoutSeconds = TimeoutSecondsDefault, int rowCap = RowCap, int charCap = CharCap)
    {
        Path = SourcesPy.PathStr(path);
        if (!File.Exists(path)) throw new CatalogException($"no catalog at {Path}");
        TimeoutSeconds = timeoutSeconds;
        RowCapValue = rowCap;
        CharCapValue = charCap;
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        builder["ACCESS_MODE"] = "READ_ONLY";
        builder["enable_external_access"] = "false";
        _con = new DuckDBConnection(builder.ConnectionString);
        try
        {
            _con.Open();
        }
        catch (DuckDBException exc)
        {
            _con.Dispose();
            throw new CatalogException($"{Path} could not be opened read-only: {exc.Message}");
        }
        // Order matters and is not optional: this cannot be set at connect time, and once set it cannot be unset.
        // Do it before any caller-supplied SQL reaches the connection.
        using var cmd = _con.CreateCommand();
        cmd.CommandText = $"SET disabled_filesystems='{DisabledFilesystems}'";
        cmd.ExecuteNonQuery();
        TimeZone = TimeZoneOverride ?? SessionTimeZone();
    }

    /// <summary>The zone a TIMESTAMP WITH TIME ZONE is shown in: DuckDB's <c>TimeZone</c> setting, which is what
    /// Python's client converts to (it defaults to the machine's zone).</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>For tests whose expected answers were written in another zone. Null in the product.</summary>
    internal static TimeZoneInfo? TimeZoneOverride { get; set; }

    private TimeZoneInfo SessionTimeZone()
    {
        try
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = "SELECT current_setting('TimeZone')";
            return cmd.ExecuteScalar() is string name && name.Length > 0 ? TimeZoneInfo.FindSystemTimeZoneById(name) : TimeZoneInfo.Local;
        }
        catch (Exception e) when (e is DuckDBException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public void Dispose()
    {
        try
        {
            _con.Dispose();
        }
        catch (Exception)
        {
            // closing a broken connection is not an error worth raising
        }
    }

    public Dictionary<string, object?> Caps => new(StringComparer.Ordinal)
    {
        ["max_rows"] = (long)RowCapValue,
        ["max_characters"] = (long)CharCapValue,
        ["timeout_seconds"] = TimeoutSeconds,
    };

    // --- statement kinds ------------------------------------------------------------------------------

    [DllImport("duckdb", EntryPoint = "duckdb_prepared_statement_type")]
    private static extern int PreparedStatementType(IntPtr prepared);

    /// <summary>The C API's <c>duckdb_statement_type</c> values, named as Python's <c>duckdb.StatementType</c> names
    /// them. Python's enum has no member for UPDATE EXTENSIONS and prints it <c>???</c>; so does this.</summary>
    private static readonly IReadOnlyDictionary<int, string> StatementTypeNames = new Dictionary<int, string>
    {
        [1] = "SELECT", [2] = "INSERT", [3] = "UPDATE", [4] = "EXPLAIN", [5] = "DELETE", [6] = "PREPARE",
        [7] = "CREATE", [8] = "EXECUTE", [9] = "ALTER", [10] = "TRANSACTION", [11] = "COPY", [12] = "ANALYZE",
        [13] = "VARIABLE_SET", [14] = "CREATE_FUNC", [15] = "DROP", [16] = "EXPORT", [17] = "PRAGMA",
        [18] = "VACUUM", [19] = "CALL", [20] = "SET", [21] = "LOAD", [22] = "RELATION", [23] = "EXTENSION",
        [24] = "LOGICAL_PLAN", [25] = "ATTACH", [26] = "DETACH", [27] = "MULTI", [28] = "COPY_DATABASE",
        [29] = "???", [30] = "MERGE_INTO",
    };

    [DllImport("duckdb", EntryPoint = "duckdb_parameter_name")]
    private static extern IntPtr ParameterName(IntPtr prepared, long index);

    [DllImport("duckdb", EntryPoint = "duckdb_free")]
    private static extern void Free(IntPtr pointer);

    /// <summary>The parameters (<c>?</c>, <c>$1</c>, <c>$name</c>) the last checked statement declares. Python's
    /// <c>execute(sql)</c> passes no values, so DuckDB refuses such a statement; DuckDB.NET refuses it before DuckDB
    /// sees it, in other words, so the refusal is raised here in DuckDB's.</summary>
    private List<string> _parameters = [];

    /// <summary>Parse without executing. Raises <see cref="QueryRefusedException"/> when DuckDB cannot parse it.</summary>
    private List<string> StatementKinds(string sql)
    {
        _parameters = [];
        var native = _con.NativeConnection;
        var count = NativeMethods.ExtractStatements.DuckDBExtractStatements(native, sql, out var extracted);
        try
        {
            if (count == 0)
            {
                var error = NativeMethods.ExtractStatements.DuckDBExtractStatementsError(extracted);
                if (string.IsNullOrEmpty(error)) return [];
                throw new QueryRefusedException($"not valid SQL: {error}");
            }
            var kinds = new List<string>();
            List<string>? texts = null;
            for (var i = 0; i < count; i++)
            {
                string? kind = null;
                var state = NativeMethods.ExtractStatements.DuckDBPrepareExtractedStatement(native, extracted, i, out var prepared);
                using (prepared)
                {
                    if (state == DuckDBState.Success)
                    {
                        var handle = prepared.DangerousGetHandle();
                        kind = StatementTypeNames.GetValueOrDefault(PreparedStatementType(handle));
                        if (count == 1) _parameters = ParameterNames(prepared, handle);
                    }
                }
                if (kind is null)
                {
                    texts ??= StatementText.Split(sql);
                    var text = texts.Count == count ? texts[i] : texts.Count > 0 ? texts[Math.Min(i, texts.Count - 1)] : sql;
                    kind = StatementText.Kind(text);
                    // A PIVOT is expanded by the parser into CREATE TYPE statements and then its SELECT; only the
                    // first can be prepared before the others run.
                    if (texts.Count != count && kind == "CREATE" && i == count - 1
                        && StatementText.FirstWord(text) is "PIVOT" or "PIVOT_WIDER")
                        kind = "SELECT";
                }
                kinds.Add(kind);
            }
            return kinds;
        }
        finally
        {
            extracted.Dispose();
        }
    }

    private static List<string> ParameterNames(DuckDBPreparedStatement prepared, IntPtr handle)
    {
        var names = new List<string>();
        var n = NativeMethods.PreparedStatements.DuckDBParams(prepared);
        for (long index = 1; index <= n; index++)
        {
            var pointer = ParameterName(handle, index);
            if (pointer == IntPtr.Zero) continue;
            try
            {
                names.Add(Marshal.PtrToStringUTF8(pointer) ?? "");
            }
            finally
            {
                Free(pointer);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Refuse a query before it runs, and say why in a sentence the agent can act on.</summary>
    /// <returns>The statement kind, e.g. <c>"SELECT"</c>.</returns>
    /// <exception cref="QueryRefusedException">Empty, more than one statement, or a kind not in
    /// <see cref="AllowedStatements"/>.</exception>
    public string CheckStatement(string sql)
    {
        var kinds = StatementKinds(sql);
        if (kinds.Count == 0) throw new QueryRefusedException("no statement to run");
        if (kinds.Count > 1)
            throw new QueryRefusedException(
                $"{kinds.Count} statements in one query ({string.Join(", ", kinds)}); send one at a time, so "
                + "that the result you get back is the answer to a question you asked");
        var kind = kinds[0];
        if (!AllowedStatements.Contains(kind))
            throw new QueryRefusedException(
                $"{kind} is not one of the statement kinds this catalog answers "
                + $"({string.Join(", ", AllowedStatements)}). The catalog is derived and read-only: it is "
                + "rebuilt from its bundles by `datarepo build`, never written to through a query.");
        return kind;
    }

    // --- querying -------------------------------------------------------------------------------------

    /// <summary>Run one statement under every bound this class documents.</summary>
    /// <param name="sql">One SQL statement. Several are refused rather than run.</param>
    /// <param name="rowCap">A tighter cap than <see cref="RowCapValue"/> for this call. It can only tighten.</param>
    /// <exception cref="QueryRefusedException">The statement is not one this sandbox runs.</exception>
    /// <exception cref="QueryTimeoutException">It was still running at the deadline and was interrupted.</exception>
    /// <exception cref="CatalogException">DuckDB rejected it -- a bad column name, a missing table.</exception>
    public BoundedResult Query(string sql, long? rowCap = null)
    {
        CheckStatement(sql);
        var cap = rowCap is null ? RowCapValue : Math.Min(RowCapValue, rowCap.Value);
        if (_parameters.Count > 0)
            throw new CatalogException(
                $"Invalid Input Error: Values were not provided for the following prepared statement parameters: {string.Join(", ", _parameters)}");
        var columns = new List<string>();
        var fetched = new List<object?[]>();
        var watch = Stopwatch.StartNew();
        var native = _con.NativeConnection;
        using (var watchdog = new Timer(_ => native.Interrupt(), null, TimeSpan.FromSeconds(TimeoutSeconds), Timeout.InfiniteTimeSpan))
        {
            try
            {
                using var cmd = _con.CreateCommand();
                cmd.CommandText = sql;
                cmd.UseStreamingMode = true;  // as Python's execute(): rows are produced as they are fetched
                using var reader = cmd.ExecuteReader();
                for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
                var scales = DecimalScales(reader);
                // Read one more than the cap so truncation is *observed*, not inferred from a full page.
                if (columns.Count > 0)
                {
                    // Python's cursor.fetchmany refuses a negative size with a TypeError, after the statement has
                    // run: not a DataRepoError, so the SDK reports it as a tool error.
                    if (cap + 1 < 0)
                        throw new ArgumentException("fetchmany(): incompatible function arguments. The following argument types are supported:\n    1. (self: _duckdb.DuckDBPyConnection, size: typing.SupportsInt | typing.SupportsIndex = 1) -> list");
                    while (fetched.Count < cap + 1 && reader.Read())
                    {
                        var row = new object?[reader.FieldCount];
                        for (var i = 0; i < row.Length; i++) row[i] = PyValues.Read(reader, i, scales[i], TimeZone);
                        fetched.Add(row);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw TimedOut();
            }
            catch (DuckDBException exc) when (exc.ErrorType == DuckDBErrorType.Interrupt)
            {
                throw TimedOut();
            }
            catch (DuckDBException exc)
            {
                throw new CatalogException(exc.Message);
            }
            finally
            {
                watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        var elapsed = watch.Elapsed.TotalSeconds;

        var rows = fetched;
        string? truncatedBy = null;
        if (rows.Count > cap)
        {
            rows = PySlice(rows, cap);
            truncatedBy = "rows";
        }
        (rows, truncatedBy) = ApplyCharCap(columns, rows, truncatedBy);
        var caps = Caps;
        caps["max_rows"] = cap;
        return new BoundedResult
        {
            Columns = columns,
            Rows = rows,
            Truncated = truncatedBy is not null,
            TruncatedBy = truncatedBy,
            ElapsedSeconds = elapsed,
            Caps = caps,
        };
    }

    private QueryTimeoutException TimedOut() => new(
        $"query ran longer than {PyFormat.FormatG(TimeoutSeconds)} s and was stopped. Narrow it -- "
        + "add a WHERE on dataset_id, or aggregate instead of returning rows.");

    /// <summary><c>rows[:cap]</c>, Python's slice: a negative bound counts from the end.</summary>
    private static List<object?[]> PySlice(List<object?[]> rows, long cap)
    {
        var end = cap >= 0 ? Math.Min(cap, rows.Count) : Math.Max(0, rows.Count + cap);
        return rows.Take((int)end).ToList();
    }

    /// <summary>Each column's DECIMAL scale (null for any other type), so a decimal keeps its trailing zeros.</summary>
    private static int?[] DecimalScales(System.Data.Common.DbDataReader reader)
    {
        var scales = new int?[reader.FieldCount];
        if (!Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetFieldType(i) == typeof(decimal))) return scales;
        var schema = reader.GetSchemaTable();
        if (schema is null) return scales;
        foreach (System.Data.DataRow row in schema.Rows)
        {
            var ordinal = Convert.ToInt32(row["ColumnOrdinal"], System.Globalization.CultureInfo.InvariantCulture);
            if (row["NumericScale"] is not DBNull and not null)
                scales[ordinal] = Convert.ToInt32(row["NumericScale"], System.Globalization.CultureInfo.InvariantCulture);
        }
        return scales;
    }

    /// <summary>Drop whole rows until the rendered result fits <see cref="CharCapValue"/>.</summary>
    /// <remarks>Whole rows, never a truncated cell: half a peptidoform or half a UniProt accession is a value an
    /// agent can read and be wrong about, where a missing row is covered by the <c>truncated</c> flag it is handed
    /// alongside. Lengths are Python's: <c>len(str(value))</c>, in code points.</remarks>
    private (List<object?[]>, string?) ApplyCharCap(List<string> columns, List<object?[]> rows, string? truncatedBy)
    {
        long budget = CharCapValue - columns.Sum(c => (long)PyValues.Len(c) + 1);
        long used = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            used += rows[i].Sum(v => (long)PyValues.Len(PyValues.Str(v)) + 1);
            if (used > budget) return (rows.Take(i).ToList(), "characters");
        }
        return (rows, truncatedBy);
    }

    /// <summary>A single scalar, for the server's own bookkeeping queries. Not for caller SQL.</summary>
    public object? OneValue(string sql, params object?[] parameters)
    {
        var rows = Dicts(sql, parameters);
        return rows.Count == 0 ? null : rows[0].Values.FirstOrDefault();
    }

    /// <summary>Rows as dictionaries, for the server's own queries. Not for caller SQL -- no watchdog, no caps.</summary>
    /// <remarks>Everything this runs is written in this repository, so the bound that matters is the one the query
    /// itself carries. Callers that build a <c>LIMIT</c> from user input pass <paramref name="limit"/> instead.</remarks>
    public List<Dictionary<string, object?>> Dicts(string sql, IReadOnlyList<object?>? parameters = null, int? limit = null)
    {
        try
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = sql;
            foreach (var p in parameters ?? []) cmd.Parameters.Add(new DuckDBParameter(p ?? DBNull.Value));
            using var reader = cmd.ExecuteReader();
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var scales = DecimalScales(reader);
            var rows = new List<Dictionary<string, object?>>();
            while ((limit is null || rows.Count < limit) && reader.Read())
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < names.Count; i++) d[names[i]] = PyValues.Read(reader, i, scales[i], TimeZone);
                rows.Add(d);
            }
            return rows;
        }
        catch (DuckDBException exc)
        {
            throw new CatalogException(exc.Message);
        }
    }

    // --- what a statement reads ------------------------------------------------------------------------

    /// <summary>The catalog tables a statement reads, or <b>null when that cannot be determined</b>.</summary>
    /// <remarks>
    /// Parsed out of DuckDB's own <c>json_serialize_sql</c>, so it sees through aliases, subqueries and set
    /// operations, and a table name inside a string literal is correctly not a reference.
    /// <para><b>CTE names are subtracted, and that is the whole point.</b> A <c>WITH</c> clause may define a temporary
    /// table named after a real one, and it serializes as a <c>BASE_TABLE</c> node indistinguishable from the real
    /// thing. Before this, <c>WITH protein_groups_1pct AS (SELECT 'PXD036557' AS dataset_id, 99999 AS n) SELECT * FROM
    /// protein_groups_1pct</c> came back certified as having read the view's 8,055 rows, with a real bundle id
    /// attached.</para>
    /// <para>Null means "unknown" and must never be rendered as "no tables".</para>
    /// </remarks>
    public List<string>? ReferencedTables(string sql)
    {
        var tree = ParseTree(sql);
        if (tree is null) return null;
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var ctes = new HashSet<string>(StringComparer.Ordinal);
        var opaque = false;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (Get(node, "type") is { ValueKind: JsonValueKind.String } type)
                {
                    if (type.GetString() == "BASE_TABLE" && Truthy(Get(node, "table_name")))
                        tables.Add(JsonStr(Get(node, "table_name")!.Value));
                    if (type.GetString() == "TABLE_FUNCTION")
                    {
                        var function = Get(node, "function");
                        var name = function is { ValueKind: JsonValueKind.Object } f && Truthy(Get(f, "function_name"))
                            ? JsonStr(Get(f, "function_name")!.Value) : "";
                        if (OpaqueTableFunctions.Contains(PyValues.Lower(name))) opaque = true;
                    }
                }
                if (Get(node, "cte_map") is { ValueKind: JsonValueKind.Object } cteMap
                    && Get(cteMap, "map") is { ValueKind: JsonValueKind.Array } map)
                {
                    foreach (var entry in map.EnumerateArray())
                        if (entry.ValueKind == JsonValueKind.Object && Truthy(Get(entry, "key")))
                            ctes.Add(JsonStr(Get(entry, "key")!.Value));
                }
                foreach (var property in node.EnumerateObject()) Walk(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in node.EnumerateArray()) Walk(value);
            }
        }

        Walk(tree.Value);
        if (opaque) return null;
        tables.ExceptWith(ctes);
        return tables.Order(PyValues.CodePointOrder).ToList();
    }

    /// <summary>The column names a statement names, and whether it expands a <c>*</c> or <c>COLUMNS(...)</c>.</summary>
    /// <remarks>From the same parse tree as <see cref="ReferencedTables"/>, so an alias (<c>pep AS x</c>) still names
    /// <c>pep</c>. Names are lower-cased and unqualified. <b>A hint</b>: a star is reported rather than resolved.</remarks>
    /// <returns><c>(names, expandsStar)</c>, or null when the statement could not be parsed.</returns>
    public (HashSet<string> Names, bool Star)? ReferencedColumns(string sql)
    {
        var tree = ParseTree(sql);
        if (tree is null) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var star = false;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var cls = Get(node, "class");
                if (cls is { ValueKind: JsonValueKind.String } && cls.Value.GetString() == "COLUMN_REF"
                    && Get(node, "column_names") is { ValueKind: JsonValueKind.Array } columnNames && columnNames.GetArrayLength() > 0)
                    names.Add(PyValues.Lower(JsonStr(columnNames[columnNames.GetArrayLength() - 1])));
                if (cls is { ValueKind: JsonValueKind.String } && cls.Value.GetString() == "STAR") star = true;
                foreach (var property in node.EnumerateObject()) Walk(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in node.EnumerateArray()) Walk(value);
            }
        }

        Walk(tree.Value);
        return (names, star);
    }

    /// <summary>DuckDB's own parse of <paramref name="sql"/> (<c>json_serialize_sql</c>), or null when it cannot be
    /// parsed.</summary>
    private JsonElement? ParseTree(string sql)
    {
        object? document;
        try
        {
            using var cmd = _con.CreateCommand();
            // DuckDB.NET binds the parameter untyped, and json_serialize_sql insists on a VARCHAR.
            cmd.CommandText = "SELECT json_serialize_sql(?::VARCHAR)";
            cmd.Parameters.Add(new DuckDBParameter(sql));
            document = cmd.ExecuteScalar();
        }
        catch (DuckDBException)
        {
            return null;
        }
        if (document is not string text || text.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 10_000 });
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Get(JsonElement obj, string name)
    {
        // Python's dict keeps the LAST of a repeated key; JsonElement.TryGetProperty returns the first.
        JsonElement? found = null;
        foreach (var property in obj.EnumerateObject())
            if (property.NameEquals(name)) found = property.Value;
        return found;
    }

    private static bool Truthy(JsonElement? e) => e is { } v && v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => true,
    };

    /// <summary><c>str()</c> of a parsed JSON value.</summary>
    private static string JsonStr(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? e.GetString()!
        : PyValues.Str(SourcesPy.FromJson(e));

    /// <summary>Is this table or view present? A catalog built before a table existed still is one.</summary>
    public bool HasTable(string name) =>
        PyValues.Truthy(OneValue("SELECT count(*) FROM information_schema.tables WHERE table_name = ?", name));

    public List<string> ColumnsOf(string name) =>
        Dicts("SELECT column_name FROM information_schema.columns WHERE table_name = ? ORDER BY ordinal_position", [name])
            .Select(r => (string)r["column_name"]!).ToList();
}
