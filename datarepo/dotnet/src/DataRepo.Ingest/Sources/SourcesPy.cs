using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DataRepo.Bundle;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DataRepo.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DataRepo.Catalog")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DataRepo.Mcp")]

namespace DataRepo.Ingest.Sources;

/// <summary>Python's semantics for the few operations the source readers lean on, where .NET's differ.</summary>
/// <remarks>
/// A row has to come out byte-identical to the one Python 0.32.0 wrote, so "close" is not enough: <c>Path.stem</c>
/// of <c>.hidden</c> is <c>.hidden</c> in Python and the empty string in .NET, <c>str.strip</c> removes
/// <c>\x1c</c>-<c>\x1f</c> and <c>char.IsWhiteSpace</c> does not, and Python sorts strings by code point where
/// <c>string.CompareOrdinal</c> sorts by UTF-16 unit. Each helper here is the Python operation, named after it.
/// </remarks>
internal static class SourcesPy
{
    /// <summary>The empty mapping <c>x or {}</c> falls back to. Never mutated.</summary>
    internal static readonly IReadOnlyDictionary<string, object?> EmptyDict = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Python truthiness of a plain value.</summary>
    internal static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,  // NaN is truthy in Python, and NaN != 0 is true
        BigInteger n => !n.IsZero,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary><c>(value or {})</c> where <paramref name="value"/> is expected to be a mapping.</summary>
    /// <exception cref="InvalidOperationException">A truthy value that is not a mapping, where Python's next
    /// <c>.get</c> would raise <c>AttributeError</c>.</exception>
    internal static IReadOnlyDictionary<string, object?> DictOrEmpty(object? value, string what)
    {
        if (!Truthy(value)) return EmptyDict;
        return value as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException($"{what} is a {TypeName(value)}, not a mapping");
    }

    /// <summary><c>(value or [])</c> iterated, where <paramref name="value"/> is expected to be a list.</summary>
    internal static IEnumerable<object?> ListOrEmpty(object? value, string what)
    {
        if (!Truthy(value)) return [];
        return value switch
        {
            List<object?> list => list,
            IReadOnlyDictionary<string, object?> dict => dict.Keys,  // iterating a dict yields its keys
            string s => s.Select(c => (object?)c.ToString()),
            _ => throw new InvalidOperationException($"{what} is a {TypeName(value)}, which cannot be iterated"),
        };
    }

    /// <summary><c>d.get(key)</c>, or <paramref name="fallback"/> when the key is absent (not when it is null).</summary>
    internal static object? Get(IReadOnlyDictionary<string, object?> d, string key, object? fallback = null) =>
        d.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Python's <c>str(value)</c>, including <c>None</c> and containers.</summary>
    internal static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        List<object?> or IReadOnlyDictionary<string, object?> => Repr(value),
        _ => PyFormat.Str(value),
    };

    /// <summary>Python's <c>repr(value)</c> for the plain values JSON and TOML give.</summary>
    internal static string Repr(object? value) => value switch
    {
        null => "None",
        string s => ReprString(s),
        List<object?> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        IReadOnlyDictionary<string, object?> dict => "{" + string.Join(", ", dict.Select(kv => ReprString(kv.Key) + ": " + Repr(kv.Value))) + "}",
        _ => PyFormat.Str(value),
    };

    private static string ReprString(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (var c in s)
        {
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append(quote).ToString();
    }

    internal static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        string => "str",
        long or int or BigInteger => "int",
        double => "float",
        bool => "bool",
        List<object?> => "list",
        IReadOnlyDictionary<string, object?> => "dict",
        _ => value.GetType().Name,
    };

    /// <summary><c>str.isspace</c> for one character: .NET's set plus the four information separators.</summary>
    internal static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');

    /// <summary><c>str.strip()</c>.</summary>
    internal static string Strip(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary><c>str.split()</c> with no separator: runs of whitespace, no empty parts.</summary>
    internal static List<string> SplitWhitespace(string s)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
            var start = i;
            while (i < s.Length && !IsSpace(s[i])) i++;
            if (i > start) parts.Add(s[start..i]);
        }
        return parts;
    }

    /// <summary><c>str.partition(sep)</c>.</summary>
    internal static (string Head, string Sep, string Tail) Partition(string s, string sep)
    {
        var at = s.IndexOf(sep, StringComparison.Ordinal);
        return at < 0 ? (s, "", "") : (s[..at], sep, s[(at + sep.Length)..]);
    }

    /// <summary><c>str.rpartition(sep)</c>.</summary>
    internal static (string Head, string Sep, string Tail) RPartition(string s, string sep)
    {
        var at = s.LastIndexOf(sep, StringComparison.Ordinal);
        return at < 0 ? ("", "", s) : (s[..at], sep, s[(at + sep.Length)..]);
    }

    /// <summary>Python's string ordering: by code point, which differs from UTF-16 order above U+D7FF.</summary>
    internal static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            // A surrogate (a code point above U+FFFF) sorts after every BMP character.
            var xs = char.IsSurrogate(x);
            var ys = char.IsSurrogate(y);
            if (xs != ys) return xs ? 1 : -1;
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }

    internal static readonly IComparer<string> CodePointOrder = Comparer<string>.Create(CompareCodePoints);

    /// <summary>The separators <c>pathlib.Path</c> splits on on this platform.</summary>
    private static readonly char[] Separators = OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'];

    /// <summary><c>pathlib.Path(p).name</c> on this platform.</summary>
    internal static string PathName(string path)
    {
        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            path = path[2..];  // the drive is not part of the name: Path("C:foo.raw").name == "foo.raw"
        var parts = path.Split(Separators).Where(p => p.Length > 0 && p != ".").ToList();
        return parts.Count == 0 ? "" : parts[^1];
    }

    /// <summary><c>pathlib.Path(p).stem</c>: the name less its last suffix, where a suffix needs a dot that is
    /// neither the first nor the last character of the name (Python 3.13).</summary>
    internal static string PathStem(string path)
    {
        var name = PathName(path);
        var i = name.LastIndexOf('.');
        return i > 0 && i < name.Length - 1 ? name[..i] : name;
    }

    /// <summary><c>pathlib.Path(p).is_absolute()</c> on this platform.</summary>
    internal static bool IsAbsolute(string path) =>
        OperatingSystem.IsWindows() ? Path.IsPathFullyQualified(path) : path.StartsWith('/');

    /// <summary><c>str(pathlib.Path(p))</c> on this platform: separators normalised, empty and <c>.</c> parts dropped.</summary>
    internal static string PathStr(string path)
    {
        if (path.Length == 0) return ".";
        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        var prefix = "";
        var rest = path;
        if (OperatingSystem.IsWindows() && rest.Length >= 2 && rest[1] == ':' && char.IsAsciiLetter(rest[0]))
        {
            prefix = rest[..2];
            rest = rest[2..];
        }
        var rooted = rest.Length > 0 && Separators.Contains(rest[0]);
        var parts = rest.Split(Separators).Where(p => p.Length > 0 && p != ".");
        var body = string.Join(sep, parts);
        var text = prefix + (rooted ? sep.ToString() : "") + body;
        return text.Length == 0 ? "." : text;
    }

    /// <summary><c>work_root / path</c>, as <c>str()</c>.</summary>
    internal static string JoinPath(string root, string path) =>
        IsAbsolute(path) ? PathStr(path) : PathStr(root.TrimEnd(Separators) + (OperatingSystem.IsWindows() ? "\\" : "/") + path);

    /// <summary><c>json.loads(path.read_text(encoding="utf-8-sig"))</c>, as plain values: a JSON integer is a
    /// <c>long</c> (a <c>BigInteger</c> past its range, as Python's int), any other number a <c>double</c>,
    /// arrays <c>List&lt;object?&gt;</c>, objects <c>Dictionary&lt;string, object?&gt;</c> in document order
    /// with the last of a repeated key winning, as Python's <c>dict</c>.</summary>
    /// <remarks>One known difference: Python's <c>json</c> also accepts the non-standard literals <c>NaN</c>,
    /// <c>Infinity</c> and <c>-Infinity</c>, which <c>System.Text.Json</c> refuses.</remarks>
    internal static object? LoadJson(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1000 });
        return FromJson(doc.RootElement);
    }

    internal static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ObjectFromJson(e),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => NumberFromJson(e.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> ObjectFromJson(JsonElement e)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) d[p.Name] = FromJson(p.Value);
        return d;
    }

    private static object NumberFromJson(string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) >= 0) return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
