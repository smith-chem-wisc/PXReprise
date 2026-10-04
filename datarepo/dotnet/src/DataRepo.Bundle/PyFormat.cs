using System.Collections;
using System.Globalization;
using System.Text;

namespace DataRepo.Bundle;

/// <summary>
/// Python's text forms of values, reproduced exactly where a hash or a stored string depends on them.
/// </summary>
/// <remarks>
/// A bundle id hashes each declaration as Python's <c>json.dumps(payload, sort_keys=True,
/// default=str, ensure_ascii=False)</c>, and a non-string value written to a string column is stored
/// as Python's <c>str(value)</c>. Both reach rows or ids, so "close" is not good enough: a float
/// written <c>1E-05</c> instead of <c>1e-05</c> is a different bundle id for the same inputs.
/// </remarks>
public static class PyFormat
{
    /// <summary>Python's <c>repr(float)</c>: the shortest digits that round-trip, in Python's layout.</summary>
    /// <remarks>Fixed notation when the decimal exponent is in [-4, 16), otherwise <c>d.ddde+XX</c> with at
    /// least two exponent digits; a whole number in fixed notation keeps <c>.0</c>.</remarks>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        // "E16" is not shortest; "R" is, so take its digits and exponent and re-lay them out.
        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = r.StartsWith('-');
        if (negative) r = r[1..];
        int exp10;
        string mantissa;
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            mantissa = r[..e];
            exp10 = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
        }
        else
        {
            mantissa = r;
            exp10 = 0;
        }
        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        var digits = (intPart + fracPart).TrimStart('0');
        // Position of the decimal point relative to the first significant digit.
        var leadingZeros = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        var decpt = intPart.Length - leadingZeros + exp10;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        string body;
        if (decpt > -4 && decpt <= 16)
        {
            if (decpt <= 0)
                body = "0." + new string('0', -decpt) + digits;
            else if (decpt >= digits.Length)
                body = digits + new string('0', decpt - digits.Length) + ".0";
            else
                body = digits[..decpt] + "." + digits[decpt..];
        }
        else
        {
            var x = decpt - 1;
            var m = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            body = m + "e" + (x < 0 ? "-" : "+") + Math.Abs(x).ToString("00", CultureInfo.InvariantCulture);
        }
        return negative ? "-" + body : body;
    }

    /// <summary>Python's <c>format(value, "g")</c>: six significant digits, trailing zeros dropped,
    /// exponent form when the decimal exponent is below -4 or at least 6.</summary>
    public static string FormatG(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";
        // "E5" rounds to six significant digits correctly; its exponent decides the layout.
        var e = value.ToString("E5", CultureInfo.InvariantCulture);
        var exp = int.Parse(e[(e.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        if (exp < -4 || exp >= 6)
        {
            var mantissa = e[..e.IndexOf('E')];
            if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
            return mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        var fixedText = value.ToString("F" + Math.Max(0, 5 - exp), CultureInfo.InvariantCulture);
        if (fixedText.Contains('.')) fixedText = fixedText.TrimEnd('0').TrimEnd('.');
        return fixedText;
    }

    /// <summary><see cref="FormatG(double)"/> for any number a row can carry.</summary>
    public static string FormatG(object value) => value switch
    {
        double d => FormatG(d),
        float f => FormatG(f),
        IConvertible c => FormatG(c.ToDouble(CultureInfo.InvariantCulture)),
        _ => throw new FormatException($"cannot format {value} with 'g'"),
    };

    /// <summary>Python's <c>float(text)</c>: surrounding whitespace allowed, <c>nan</c>/<c>inf</c>/<c>infinity</c>
    /// in any case with an optional sign, underscores between digits; false where Python raises ValueError.</summary>
    public static bool TryParseFloat(string? text, out double value)
    {
        value = 0;
        if (text is null) return false;
        var s = text.Trim();
        if (s.Length == 0) return false;
        var lower = s.ToLowerInvariant();
        var unsigned = lower.TrimStart('+', '-');
        var negative = lower.StartsWith('-');
        if (lower.Length - unsigned.Length > 1) return false;
        if (unsigned is "nan") { value = double.NaN; return true; }
        if (unsigned is "inf" or "infinity") { value = negative ? double.NegativeInfinity : double.PositiveInfinity; return true; }
        if (s.Contains('_'))
        {
            // Python allows one underscore between two digits only.
            for (var i = 0; i < s.Length; i++)
                if (s[i] == '_' && (i == 0 || i == s.Length - 1 || !char.IsAsciiDigit(s[i - 1]) || !char.IsAsciiDigit(s[i + 1])))
                    return false;
            s = s.Replace("_", "");
        }
        foreach (var c in s)
            if (!(char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-')) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Python's <c>str(value)</c> for the scalar types a row can carry.</summary>
    public static string Str(object value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        double d => FloatRepr(d),
        float f => FloatRepr(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Python's <c>json.dumps(value, sort_keys=sortKeys, ensure_ascii=ensureAscii)</c> with the default
    /// separators (<c>", "</c> and <c>": "</c>) and <c>default=str</c>.
    /// </summary>
    public static string Json(object? value, bool sortKeys = true, bool ensureAscii = false)
    {
        var sb = new StringBuilder();
        WriteJson(sb, value, sortKeys, ensureAscii);
        return sb.ToString();
    }

    private static void WriteJson(StringBuilder sb, object? value, bool sortKeys, bool ensureAscii)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(sb, s, ensureAscii);
                return;
            case double d:
                sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity"
                    : double.IsNegativeInfinity(d) ? "-Infinity" : FloatRepr(d));
                return;
            case float f:
                WriteJson(sb, (double)f, sortKeys, ensureAscii);
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case IDictionary dict:
            {
                var keys = dict.Keys.Cast<object>().Select(k => (Key: k, Text: Str(k))).ToList();
                if (sortKeys) keys.Sort((a, b) => string.CompareOrdinal(a.Text, b.Text));
                sb.Append('{');
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteString(sb, keys[i].Text, ensureAscii);
                    sb.Append(": ");
                    WriteJson(sb, dict[keys[i].Key], sortKeys, ensureAscii);
                }
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    WriteJson(sb, item, sortKeys, ensureAscii);
                }
                sb.Append(']');
                return;
            }
            default:
                WriteString(sb, Str(value), ensureAscii);  // json.dumps(..., default=str)
                return;
        }
    }

    private static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7f))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
