using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using DuckDB.NET.Native;

namespace DataRepo.Mcp;

/// <summary>Python's <c>decimal.Decimal</c> as DuckDB's Python client builds it: the unscaled integer and the
/// column's scale, so <c>str()</c> keeps the trailing zeros (<c>123.450</c>) that a .NET <c>decimal</c> can lose.</summary>
public sealed record PyDecimal(BigInteger Unscaled, int Scale)
{
    /// <summary>A .NET decimal at a known scale, or at its own when the column's is unknown (a nested value).</summary>
    public static PyDecimal From(decimal value, int? scale)
    {
        var s = scale ?? value.Scale;
        var unscaled = new BigInteger(decimal.Round(value * Pow10m(s), 0));
        return new PyDecimal(unscaled, s);
    }

    private static decimal Pow10m(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }

    /// <summary>Python's <c>str(Decimal)</c>: plain notation when the exponent is not positive and the adjusted
    /// exponent is at least -6, scientific otherwise (<c>1E-7</c>).</summary>
    public override string ToString()
    {
        var negative = Unscaled.Sign < 0;
        var digits = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture);
        var exp = -Scale;
        var adjusted = exp + digits.Length - 1;
        string body;
        if (exp <= 0 && adjusted >= -6)
        {
            if (exp == 0) body = digits;
            else
            {
                var point = digits.Length + exp;
                body = point > 0 ? digits[..point] + "." + digits[point..] : "0." + new string('0', -point) + digits;
            }
        }
        else
        {
            body = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            body += "E" + (adjusted >= 0 ? "+" : "-") + Math.Abs(adjusted).ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + body : body;
    }
}

/// <summary>Python's <c>datetime.timedelta</c>, normalised as Python normalises it: any sign on <c>Days</c>,
/// <c>0 &lt;= Seconds &lt; 86400</c>, <c>0 &lt;= Microseconds &lt; 1,000,000</c>.</summary>
public sealed record PyTimedelta(long Days, long Seconds, long Microseconds)
{
    /// <summary>A DuckDB interval as DuckDB's Python client converts it: a month is thirty days.</summary>
    public static PyTimedelta FromInterval(long months, long days, long micros) =>
        FromMicroseconds(checked(((months * 30) + days) * 86_400_000_000L + micros));

    public static PyTimedelta FromMicroseconds(long total)
    {
        var days = Math.DivRem(total, 86_400_000_000L, out var rest);
        if (rest < 0) { days -= 1; rest += 86_400_000_000L; }
        return new PyTimedelta(days, rest / 1_000_000L, rest % 1_000_000L);
    }

    public long TotalMicroseconds => (Days * 86_400L + Seconds) * 1_000_000L + Microseconds;

    /// <summary>Python's <c>str(timedelta)</c>: <c>3 days, 0:00:00</c>, <c>-1 day, 23:59:59</c>, <c>0:00:00.500000</c>.</summary>
    public override string ToString()
    {
        var h = Seconds / 3600;
        var m = Seconds / 60 % 60;
        var s = Seconds % 60;
        var clock = $"{h}:{m:00}:{s:00}" + (Microseconds != 0 ? "." + Microseconds.ToString("000000", CultureInfo.InvariantCulture) : "");
        if (Days == 0) return clock;
        return $"{Days} day{(Math.Abs(Days) != 1 ? "s" : "")}, {clock}";
    }

    /// <summary>Python's <c>repr(timedelta)</c>.</summary>
    public string Repr()
    {
        var parts = new List<string>();
        if (Days != 0) parts.Add($"days={Days}");
        if (Seconds != 0) parts.Add($"seconds={Seconds}");
        if (Microseconds != 0) parts.Add($"microseconds={Microseconds}");
        return "datetime.timedelta(" + (parts.Count == 0 ? "0" : string.Join(", ", parts)) + ")";
    }
}

/// <summary>Values as DuckDB's Python client hands them over, and Python's text forms of them.</summary>
/// <remarks>The MCP tools return whatever a caller's SQL selects, so the C# server has to turn every DuckDB value
/// into the value the Python server would have held: an integer of any width is a <c>long</c> (a
/// <c>BigInteger</c> past it, as Python's <c>int</c>), a DECIMAL a <see cref="PyDecimal"/>, an INTERVAL a
/// <see cref="PyTimedelta"/>, a TIMESTAMP WITH TIME ZONE a <see cref="DateTimeOffset"/> in the machine's zone (as
/// DuckDB's <c>TimeZone</c> setting, which defaults to it), a list a <c>List&lt;object?&gt;</c>, a struct or map an
/// insertion-ordered dictionary.</remarks>
public static class PyValues
{
    /// <summary>One cell of the current row, as a plain value. A TIMESTAMP WITH TIME ZONE is rendered in
    /// <paramref name="zone"/> (the connection's <c>TimeZone</c>), or the machine's when none is given.</summary>
    public static object? Read(DbDataReader reader, int ordinal, int? decimalScale = null, TimeZoneInfo? zone = null)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var typeName = reader.GetDataTypeName(ordinal);
        object raw;
        try
        {
            raw = reader.GetValue(ordinal);
        }
        catch (ArgumentOutOfRangeException) when (typeName == "Interval")
        {
            // DuckDB.NET refuses to express a month as a TimeSpan; the raw interval is still readable.
            var interval = reader.GetFieldValue<DuckDBInterval>(ordinal);
            return PyTimedelta.FromInterval(interval.Months, interval.Days, (long)interval.Micros);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidCastException)
        {
            // A list of a value type holding a NULL: read it as a list of the nullable type instead.
            raw = ReadNullableList(reader, ordinal) ?? throw new InvalidOperationException($"cannot read column {reader.GetName(ordinal)} ({typeName})");
        }
        if (typeName == "TimestampTz" && raw is DateTime utc)
            return InZone(utc, zone ?? TimeZoneInfo.Local);
        if (raw is decimal m) return PyDecimal.From(m, decimalScale);
        return Plain(raw);
    }

    private static object? ReadNullableList(DbDataReader reader, int ordinal)
    {
        var type = reader.GetFieldType(ordinal);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) return null;
        var element = type.GetGenericArguments()[0];
        if (!element.IsValueType || Nullable.GetUnderlyingType(element) is not null) return null;
        var target = typeof(List<>).MakeGenericType(typeof(Nullable<>).MakeGenericType(element));
        var method = typeof(DbDataReader).GetMethod(nameof(DbDataReader.GetFieldValue), BindingFlags.Public | BindingFlags.Instance)!.MakeGenericMethod(target);
        return method.Invoke(reader, [ordinal]);
    }

    /// <summary>A UTC instant in a zone, as Python's client renders a TIMESTAMPTZ.</summary>
    private static DateTimeOffset InZone(DateTime utc, TimeZoneInfo zone)
    {
        var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return TimeZoneInfo.ConvertTime(instant, zone);
    }

    /// <summary>A value DuckDB.NET returned, as the plain value Python's DuckDB returns.</summary>
    public static object? Plain(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or long or double or BigInteger or DateOnly or TimeOnly or Guid or PyDecimal or PyTimedelta or DateTimeOffset => value,
        DateTime t => DateTime.SpecifyKind(t, DateTimeKind.Unspecified),
        int or short or sbyte or byte or uint or ushort => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? (long)u : new BigInteger(u),
        float f => (double)f,
        decimal m => PyDecimal.From(m, null),
        TimeSpan span => PyTimedelta.FromMicroseconds(span.Ticks / 10),
        DuckDBInterval interval => PyTimedelta.FromInterval(interval.Months, interval.Days, (long)interval.Micros),
        byte[] bytes => bytes,
        Stream stream => ReadAll(stream),
        IDictionary d => ToDict(d),
        IEnumerable e => e.Cast<object?>().Select(Plain).ToList(),
        _ => value,
    };

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Dictionary<string, object?> ToDict(IDictionary d)
    {
        // Python's dict keeps the struct's field order; a map's keys reach JSON as their str().
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Keys, then the indexer: DuckDB.NET's struct type does not enumerate as DictionaryEntry.
        foreach (var key in d.Keys) result[Str(Plain(key))] = Plain(d[key]);
        return result;
    }

    // --- Python's text forms -------------------------------------------------------------------------

    /// <summary>Python's <c>str(value)</c>.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => Bundle.PyFormat.FloatRepr(d),
        long or BigInteger or int => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        PyDecimal m => m.ToString(),
        PyTimedelta t => t.ToString(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "HH:mm:ss" : "HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTime t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString(t.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + Offset(t.Offset),
        Guid g => g.ToString("D"),
        byte[] bytes => BytesRepr(bytes),
        IDictionary or IList => Repr(value),
        _ => Bundle.PyFormat.Str(value),
    };

    private static string Offset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>repr(value)</c>, as it appears inside <c>str(list)</c>.</summary>
    public static string Repr(object? value) => value switch
    {
        string s => ReprString(s),
        PyDecimal m => $"Decimal('{m}')",
        PyTimedelta t => t.Repr(),
        DateOnly d => $"datetime.date({d.Year}, {d.Month}, {d.Day})",
        DateTime t => $"datetime.datetime({DateTimeArgs(t)})",
        TimeOnly t => $"datetime.time({t.Hour}, {t.Minute}" + (t.Second != 0 || t.Ticks % TimeSpan.TicksPerSecond != 0 ? $", {t.Second}" : "")
            + (t.Ticks % TimeSpan.TicksPerSecond != 0 ? $", {t.Ticks % TimeSpan.TicksPerSecond / 10}" : "") + ")",
        Guid g => $"UUID('{g:D}')",
        IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => $"{Repr(k)}: {Repr(d[k])}")) + "}",
        IList list => "[" + string.Join(", ", list.Cast<object?>().Select(Repr)) + "]",
        _ => Str(value),
    };

    private static string DateTimeArgs(DateTime t)
    {
        var parts = new List<string> { t.Year.ToString(CultureInfo.InvariantCulture), t.Month.ToString(CultureInfo.InvariantCulture), t.Day.ToString(CultureInfo.InvariantCulture), t.Hour.ToString(CultureInfo.InvariantCulture), t.Minute.ToString(CultureInfo.InvariantCulture) };
        var micro = t.Ticks % TimeSpan.TicksPerSecond / 10;
        if (t.Second != 0 || micro != 0) parts.Add(t.Second.ToString(CultureInfo.InvariantCulture));
        if (micro != 0) parts.Add(micro.ToString(CultureInfo.InvariantCulture));
        return string.Join(", ", parts);
    }

    /// <summary>Python's <c>repr(str)</c>, escaping what <c>str.isprintable()</c> rejects.</summary>
    public static string ReprString(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (var rune in s.EnumerateRunes())
        {
            var c = rune.Value;
            if (c == '\\') sb.Append("\\\\");
            else if (c == quote) sb.Append('\\').Append((char)c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (IsPrintable(rune)) sb.Append(rune.ToString());
            else if (c < 0x100) sb.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture));
            else if (c < 0x10000) sb.Append("\\u").Append(c.ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append("\\U").Append(c.ToString("x8", CultureInfo.InvariantCulture));
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Python's <c>str.isprintable()</c> for one character: not a control, format, surrogate, private-use
    /// or unassigned character, and no separator other than the ASCII space.</summary>
    private static bool IsPrintable(Rune rune)
    {
        if (rune.Value == ' ') return true;
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator => false,
            _ => true,
        };
    }

    private static string BytesRepr(byte[] bytes)
    {
        var quote = bytes.Contains((byte)'\'') && !bytes.Contains((byte)'"') ? '"' : '\'';
        var sb = new StringBuilder("b").Append(quote);
        foreach (var b in bytes)
        {
            if (b == '\\') sb.Append("\\\\");
            else if (b == quote) sb.Append('\\').Append((char)b);
            else if (b == '\n') sb.Append("\\n");
            else if (b == '\r') sb.Append("\\r");
            else if (b == '\t') sb.Append("\\t");
            else if (b < 0x20 || b >= 0x7f) sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            else sb.Append((char)b);
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Python's <c>len(str)</c>: code points, not UTF-16 units.</summary>
    public static int Len(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    // --- Python's string methods -----------------------------------------------------------------------

    /// <summary>The characters Python's <c>str.strip()</c> removes (<c>str.isspace()</c>): .NET's whitespace plus
    /// the four ASCII separators U+001C..U+001F.</summary>
    private static bool IsPySpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Python's <c>str.strip()</c>.</summary>
    public static string Strip(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsPySpace(s[start])) start++;
        while (end > start && IsPySpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Python's <c>str.upper()</c>, including the unconditional one-to-many mappings (<c>ß</c> is <c>SS</c>).</summary>
    public static string Upper(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var special = c switch
            {
                'ß' => "SS",
                'ŉ' => "ʼN",
                'ǰ' => "J̌",
                'ΐ' => "Ϊ́",
                'ΰ' => "Ϋ́",
                'և' => "ԵՒ",
                'ẖ' => "H̱",
                'ẗ' => "T̈",
                'ẘ' => "W̊",
                'ẙ' => "Y̊",
                'ẚ' => "Aʾ",
                'ﬀ' => "FF",
                'ﬁ' => "FI",
                'ﬂ' => "FL",
                'ﬃ' => "FFI",
                'ﬄ' => "FFL",
                'ﬅ' or 'ﬆ' => "ST",
                _ => null,
            };
            if (special is not null) sb.Append(special);
            else sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Python's <c>str.lower()</c>, including <c>İ</c> to <c>i̇</c>.</summary>
    public static string Lower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == 'İ') sb.Append("i̇");
            else sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary><c>f"{n:,}"</c>.</summary>
    public static string Thousands(object? n) => n switch
    {
        long l => l.ToString("N0", CultureInfo.InvariantCulture),
        int i => i.ToString("N0", CultureInfo.InvariantCulture),
        BigInteger b => b.ToString("N0", CultureInfo.InvariantCulture),
        _ => Str(n),
    };

    /// <summary>Python's ordering of <c>str</c>: by code point, which UTF-16 ordinal order is not past U+FFFF.</summary>
    public static readonly IComparer<string> CodePointOrder = Comparer<string>.Create(CompareCodePoints);

    private static int CompareCodePoints(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var ea = a.EnumerateRunes();
        var eb = b.EnumerateRunes();
        while (true)
        {
            var ha = ea.MoveNext();
            var hb = eb.MoveNext();
            if (!ha || !hb) return ha == hb ? 0 : ha ? 1 : -1;
            var c = ea.Current.Value.CompareTo(eb.Current.Value);
            if (c != 0) return c;
        }
    }

    /// <summary>Python's truthiness of a plain value.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        BigInteger b => !b.IsZero,
        PyDecimal m => !m.Unscaled.IsZero,
        PyTimedelta t => t.TotalMicroseconds != 0,
        ICollection c => c.Count > 0,
        _ => true,
    };
}
