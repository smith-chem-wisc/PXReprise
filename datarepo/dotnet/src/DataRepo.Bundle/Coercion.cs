using System.Collections;
using System.Globalization;

namespace DataRepo.Bundle;

/// <summary>Bends one row value into its column's declared type, or leaves it null.</summary>
/// <remarks>
/// The rules are the Python writer's (<c>bundle._coerce</c>), because a row is the same row whichever
/// writer stored it. Empty or blank strings become null on purpose: a missing measurement is NA,
/// never 0 and never "". NaN becomes null for the same reason.
/// <para>One deliberate difference: an integer that is already an integer is kept exactly. Python's
/// <c>int(float(value))</c> rounds above 2^53; no stored column comes near that.</para>
/// </remarks>
public static class Coercion
{
    private static readonly HashSet<string> TrueWords = new(StringComparer.Ordinal) { "true", "yes", "1", "y", "t" };

    /// <summary>The value as the column stores it: <see cref="string"/>, <see cref="long"/>,
    /// <see cref="double"/>, <see cref="bool"/>, <see cref="DateOnly"/>, <see cref="DateTimeOffset"/>
    /// (UTC), a <see cref="List{T}"/> of those for a list column, or null.</summary>
    public static object? Coerce(object? value, ColumnSpec column)
    {
        if (value is null) return null;
        if (column.IsList)
        {
            IEnumerable items = value switch
            {
                string s => s.Length > 0 ? new[] { s } : Array.Empty<string>(),
                IEnumerable e => e,
                _ => new[] { value },
            };
            var list = new List<object?>();
            foreach (var item in items)
                list.Add(Scalar(item, column.Type));
            return list.Count > 0 ? list : null;
        }
        return Scalar(value, column.Type);
    }

    private static object? Scalar(object? value, ColumnType type)
    {
        if (value is null) return null;
        if (value is string text && string.IsNullOrWhiteSpace(text)) return null;
        return type switch
        {
            ColumnType.Date => ToDate(value),
            ColumnType.TimestampUtc => ToTimestamp(value),
            ColumnType.Boolean => ToBool(value),
            ColumnType.Int64 => ToLong(value),
            ColumnType.Float64 => ToDouble(value) is var d && double.IsNaN(d) ? null : d,
            _ => PyFormat.Str(value),
        };
    }

    private static DateOnly ToDate(object value) => value switch
    {
        DateOnly d => d,
        DateTime t => DateOnly.FromDateTime(t),
        DateTimeOffset o => DateOnly.FromDateTime(o.DateTime),
        _ => DateOnly.ParseExact(Truncate(PyFormat.Str(value), 10), "yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] : s;

    private static DateTimeOffset ToTimestamp(object value)
    {
        DateTimeOffset stamp = value switch
        {
            DateTimeOffset o => o,
            DateTime t when t.Kind == DateTimeKind.Unspecified => new DateTimeOffset(t, TimeSpan.Zero),
            DateTime t => new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero),
            _ => ParseIso(PyFormat.Str(value)),
        };
        return stamp.ToUniversalTime();
    }

    /// <summary>Python's <c>datetime.fromisoformat</c> for the forms producers write; a value without
    /// an offset is UTC, as the Python writer assumes.</summary>
    private static DateTimeOffset ParseIso(string s)
    {
        var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal;
        if (DateTimeOffset.TryParse(s.Replace(' ', 'T'), CultureInfo.InvariantCulture, styles, out var o))
            return o;
        throw new FormatException($"not an ISO-8601 date-time: {s}");
    }

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => TrueWords.Contains(s.Trim().ToLowerInvariant()),
        double d => d != 0,
        float f => f != 0,
        IConvertible c => Convert.ToDecimal(c, CultureInfo.InvariantCulture) != 0,
        _ => true,
    };

    private static long ToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        uint u => u,
        bool b => b ? 1 : 0,
        double d => (long)Math.Truncate(d),
        float f => (long)Math.Truncate(f),
        decimal m => (long)Math.Truncate(m),
        _ => (long)Math.Truncate(double.Parse(PyFormat.Str(value).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture)),
    };

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        bool b => b ? 1 : 0,
        IConvertible c and not string => Convert.ToDouble(c, CultureInfo.InvariantCulture),
        _ => ParsePythonFloat(PyFormat.Str(value).Trim()),
    };

    /// <summary>Python's <c>float(str)</c>, including its spellings of NaN and infinity.</summary>
    private static double ParsePythonFloat(string s) => s.ToLowerInvariant() switch
    {
        "nan" or "+nan" or "-nan" => double.NaN,
        "inf" or "+inf" or "infinity" or "+infinity" => double.PositiveInfinity,
        "-inf" or "-infinity" => double.NegativeInfinity,
        _ => double.Parse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture),
    };
}
