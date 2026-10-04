using System.Text;

namespace DataRepo.Mcp;

/// <summary>A statement's kind from its text, for the one case DuckDB cannot tell us: a statement that parses but
/// cannot be prepared, so <c>duckdb_prepared_statement_type</c> has nothing to report.</summary>
/// <remarks>
/// Python's <c>duckdb.extract_statements</c> names a statement's kind after parsing alone, so <c>INSERT INTO nope
/// VALUES (1)</c> is an INSERT even though the table does not exist. DuckDB's C API offers the kind only on a
/// prepared statement, and preparing binds. These are the statements whose kind is decided here instead: a write to
/// a missing object, a COPY or EXPORT the locked filesystem refuses at bind time, a SELECT with a bad column. The
/// kind is read off the leading keyword the way DuckDB's transformer assigns it (checked against Python's
/// <c>extract_statements</c> on DuckDB 1.5.5). It decides only whether the statement is refused and how the
/// refusal names it; an allowed one is still executed and fails with DuckDB's own message.
/// </remarks>
public static class StatementText
{
    /// <summary>The query split at top-level semicolons, skipping statements that are only whitespace and comments.
    /// Quotes, dollar quotes, line and (nested) block comments are honoured.</summary>
    public static List<string> Split(string sql)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var meaningful = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == ';')
            {
                if (meaningful) parts.Add(current.ToString());
                current.Clear();
                meaningful = false;
                i++;
                continue;
            }
            var end = SkipToken(sql, i, out var isComment);
            current.Append(sql, i, end - i);
            if (!isComment && !char.IsWhiteSpace(c)) meaningful = true;
            i = end;
        }
        if (meaningful) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The end of the token starting at <paramref name="i"/>: a quoted string or identifier, a dollar-quoted
    /// string, a comment, or one character.</summary>
    private static int SkipToken(string sql, int i, out bool isComment)
    {
        isComment = false;
        var c = sql[i];
        if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
        {
            isComment = true;
            var nl = sql.IndexOf('\n', i);
            return nl < 0 ? sql.Length : nl + 1;
        }
        if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
        {
            isComment = true;
            var depth = 0;
            var j = i;
            while (j < sql.Length)
            {
                if (sql[j] == '/' && j + 1 < sql.Length && sql[j + 1] == '*') { depth++; j += 2; continue; }
                if (sql[j] == '*' && j + 1 < sql.Length && sql[j + 1] == '/') { depth--; j += 2; if (depth == 0) return j; continue; }
                j++;
            }
            return sql.Length;
        }
        if (c is '\'' or '"')
        {
            var escapes = c == '\'' && i > 0 && sql[i - 1] is 'E' or 'e';
            var j = i + 1;
            while (j < sql.Length)
            {
                if (escapes && sql[j] == '\\') { j += 2; continue; }
                if (sql[j] == c)
                {
                    if (j + 1 < sql.Length && sql[j + 1] == c) { j += 2; continue; }
                    return j + 1;
                }
                j++;
            }
            return sql.Length;
        }
        if (c == '$')
        {
            var j = i + 1;
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
            if (j < sql.Length && sql[j] == '$' && !(j > i + 1 && char.IsDigit(sql[i + 1])))
            {
                var tag = sql[i..(j + 1)];
                var close = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
                return close < 0 ? sql.Length : close + tag.Length;
            }
        }
        return i + 1;
    }

    /// <summary>The words of a statement outside quotes and comments, upper-cased, with their bracket depth.</summary>
    private static List<(string Word, int Depth)> Words(string sql)
    {
        var words = new List<(string, int)>();
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                words.Add((sql[i..j].ToUpperInvariant(), depth));
                i = j;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '=') words.Add(("=", depth));
            i = SkipToken(sql, i, out _);
        }
        return words;
    }

    /// <summary>A statement's first keyword, upper-cased, or the empty string.</summary>
    public static string FirstWord(string statement)
    {
        var words = Words(statement);
        return words.Count == 0 ? "" : words[0].Word;
    }

    /// <summary>The kind DuckDB's transformer gives a statement, named as Python's <c>duckdb.StatementType</c>.</summary>
    public static string Kind(string statement)
    {
        var words = Words(statement);
        if (words.Count == 0) return "SELECT";
        var first = words[0].Word;
        var second = words.Count > 1 ? words[1].Word : "";
        switch (first)
        {
            case "WITH":
                foreach (var (word, depth) in words.Skip(1))
                {
                    if (depth != 0) continue;
                    switch (word)
                    {
                        case "INSERT": return "INSERT";
                        case "UPDATE": return "UPDATE";
                        case "DELETE": return "DELETE";
                        case "MERGE": return "MERGE_INTO";
                        case "SELECT" or "FROM" or "VALUES" or "TABLE": return "SELECT";
                    }
                }
                return "SELECT";
            case "SELECT" or "FROM" or "VALUES" or "TABLE" or "SHOW" or "DESCRIBE" or "DESC" or "SUMMARIZE"
                or "UNPIVOT" or "PIVOT_LONGER":
                return "SELECT";
            case "PIVOT" or "PIVOT_WIDER":
                return "CREATE";
            case "EXPLAIN":
                return "EXPLAIN";
            case "INSERT" or "REPLACE":
                return "INSERT";
            case "UPDATE":
                return second == "EXTENSIONS" ? "???" : "UPDATE";
            case "DELETE" or "TRUNCATE":
                return "DELETE";
            case "CREATE":
                return "CREATE";
            case "DROP" or "DEALLOCATE":
                return "DROP";
            case "ALTER" or "COMMENT":
                return "ALTER";
            case "ATTACH":
                return "ATTACH";
            case "DETACH":
                return "DETACH";
            case "COPY":
                return second == "FROM" && words.Count > 2 && words[2].Word == "DATABASE" ? "COPY_DATABASE" : "COPY";
            case "EXPORT":
                return "EXPORT";
            case "INSTALL" or "LOAD":
                return "LOAD";
            case "FORCE":
                return second == "CHECKPOINT" ? "CALL" : "LOAD";
            case "BEGIN" or "START" or "COMMIT" or "ROLLBACK" or "ABORT" or "END":
                return "TRANSACTION";
            case "CHECKPOINT" or "CALL":
                return "CALL";
            case "VACUUM" or "ANALYZE":
                return "VACUUM";
            case "PREPARE":
                return "PREPARE";
            case "EXECUTE":
                return "EXECUTE";
            case "SET" or "RESET" or "USE":
                return "SET";
            case "PRAGMA":
                return words.Any(w => w.Word == "=" && w.Depth == 0) ? "SET" : "PRAGMA";
            case "MERGE":
                return "MERGE_INTO";
            default:
                return first;
        }
    }
}
