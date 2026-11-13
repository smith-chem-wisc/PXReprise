using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using DataRepo.Bundle;

namespace DataRepo.Site;

/// <summary>
/// The Python text forms the site generator depends on, reproduced exactly: every published file must be
/// byte-identical to what <c>site.py</c> wrote from the same catalog.
/// </summary>
/// <remarks>
/// Float formatting rounds the EXACT binary value half-to-even, as CPython's <c>format()</c> does; .NET's own
/// fixed-precision formats round a midpoint away from zero, so <c>112.5</c> at three digits would read
/// <c>113</c> here and <c>112</c> in Python.
/// </remarks>
public static class PyText
{
    /// <summary>Python's <c>html.escape(text, quote=True)</c>.</summary>
    public static string HtmlEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#x27;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Python's <c>str(value)</c>, with <c>None</c> for null.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        BigInteger b => b.ToString(CultureInfo.InvariantCulture),
        _ => PyFormat.Str(value),
    };

    /// <summary>Python's <c>urllib.parse.quote(text, safe=safe)</c>: UTF-8, upper-case hex, letters, digits
    /// and <c>_.-~</c> always kept.</summary>
    public static string Quote(string text, string safe = "/")
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || safe.Contains(c)))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Python's truth value of a plain value.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        BigInteger b => !b.IsZero,
        ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>Python's <c>f"{value:,}"</c> for an int or a float.</summary>
    public static string Thousands(object value) => value switch
    {
        long l => l.ToString("N0", CultureInfo.InvariantCulture),
        int i => i.ToString("N0", CultureInfo.InvariantCulture),
        BigInteger b => b.ToString("N0", CultureInfo.InvariantCulture),
        double d => ThousandsFloat(d),
        _ => throw new FormatException($"unsupported format string passed to {value.GetType().Name}.__format__"),
    };

    private static string ThousandsFloat(double d)
    {
        var repr = PyFormat.FloatRepr(d);
        if (repr.Contains('e') || repr.Contains('n') || repr.Contains('i')) return repr;
        var negative = repr.StartsWith('-');
        if (negative) repr = repr[1..];
        var dot = repr.IndexOf('.');
        var whole = BigInteger.Parse(repr[..dot], CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);
        return (negative ? "-" : "") + whole + repr[dot..];
    }

    /// <summary>Python's <c>f"{value:.{precision}g}"</c>.</summary>
    public static string FormatGeneral(double value, int precision)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        if (precision == 0) precision = 1;
        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0) return sign + "0";
        var (digits, decpt) = Exact(Math.Abs(value));
        (digits, decpt) = Round(digits, decpt, precision);
        var exp = decpt - 1;
        string body;
        if (exp >= -4 && exp < precision)
        {
            if (decpt <= 0) body = "0." + new string('0', -decpt) + digits;
            else if (decpt >= digits.Length) body = digits + new string('0', decpt - digits.Length);
            else body = digits[..decpt] + "." + digits[decpt..];
            if (body.Contains('.')) body = body.TrimEnd('0').TrimEnd('.');
        }
        else
        {
            var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
            body = mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        return sign + body;
    }

    /// <summary>Python's <c>f"{value:.{decimals}f}"</c>.</summary>
    public static string FormatFixed(double value, int decimals)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsInfinity(value)) return value > 0 ? "inf" : "-inf";
        var sign = double.IsNegative(value) ? "-" : "";
        string digits;
        int decpt;
        if (value == 0)
        {
            digits = "0";
            decpt = 1;
        }
        else
        {
            (digits, decpt) = Exact(Math.Abs(value));
        }
        // Make sure at least one digit sits at or before the last kept place, so the rounding sees it.
        var keep = decpt + decimals;
        if (keep < 1)
        {
            digits = new string('0', 1 - keep) + digits;
            decpt += 1 - keep;
            keep = 1;
        }
        (digits, decpt) = Round(digits, decpt, keep);
        var whole = decpt <= 0 ? "0" : digits[..Math.Min(decpt, digits.Length)].PadRight(decpt, '0');
        var frac = decpt >= digits.Length ? "" : digits[Math.Max(decpt, 0)..];
        if (decpt < 0) frac = new string('0', -decpt) + frac;
        frac = frac.PadRight(decimals, '0')[..decimals];
        whole = whole.TrimStart('0');
        if (whole.Length == 0) whole = "0";
        return sign + whole + (decimals > 0 ? "." + frac : "");
    }

    /// <summary>The exact decimal digits of a positive finite double, and where the point goes
    /// (<c>value = 0.digits * 10^decpt</c>).</summary>
    private static (string Digits, int Decpt) Exact(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & ((1L << 52) - 1);
        int e;
        if (exponent == 0) e = -1074;
        else
        {
            mantissa |= 1L << 52;
            e = exponent - 1075;
        }
        BigInteger n;
        int exp10;
        if (e >= 0)
        {
            n = new BigInteger(mantissa) << e;
            exp10 = 0;
        }
        else
        {
            n = new BigInteger(mantissa) * BigInteger.Pow(5, -e);
            exp10 = e;
        }
        var digits = n.ToString(CultureInfo.InvariantCulture);
        var trimmed = digits.TrimEnd('0');
        exp10 += digits.Length - trimmed.Length;
        return (trimmed, trimmed.Length + exp10);
    }

    /// <summary>Round to <paramref name="keep"/> significant digits, half to even, on the exact digits.</summary>
    private static (string Digits, int Decpt) Round(string digits, int decpt, int keep)
    {
        if (digits.Length <= keep) return (digits.PadRight(keep, '0'), decpt);
        var head = digits[..keep];
        var rest = digits[keep..];
        bool up;
        if (rest[0] > '5') up = true;
        else if (rest[0] < '5') up = false;
        else if (rest.AsSpan(1).IndexOfAnyExcept('0') >= 0) up = true;
        else up = (head[^1] - '0') % 2 == 1;
        if (!up) return (head, decpt);
        var bumped = (BigInteger.Parse(head, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        if (bumped.Length > keep) return (bumped[..keep], decpt + 1);
        return (bumped.PadLeft(keep, '0'), decpt);
    }

    /// <summary>Python's <c>json.dumps</c> with <c>indent=1</c> (or with <c>separators=(",", ":")</c> when
    /// <paramref name="indent"/> is null), <c>default=str</c>, insertion order kept.</summary>
    public static string Json(object? value, int? indent = 1, bool ensureAscii = false)
    {
        var sb = new StringBuilder();
        WriteJson(sb, value, indent, 0, ensureAscii);
        return sb.ToString();
    }

    private static void WriteJson(StringBuilder sb, object? value, int? indent, int level, bool ensureAscii)
    {
        switch (value)
        {
            case null: sb.Append("null"); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case string s: WriteString(sb, s, ensureAscii); return;
            case double d:
                sb.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity"
                    : double.IsNegativeInfinity(d) ? "-Infinity" : PyFormat.FloatRepr(d));
                return;
            case float f: WriteJson(sb, (double)f, indent, level, ensureAscii); return;
            case sbyte or byte or short or ushort or int or uint or long or ulong or BigInteger:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case IDictionary dict:
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(indent is null ? "," : ",");
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Str(kv.Key), ensureAscii);
                    sb.Append(indent is null ? ":" : ": ");
                    WriteJson(sb, kv.Value, indent, level + 1, ensureAscii);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent, level + 1);
                    WriteJson(sb, items[i], indent, level + 1, ensureAscii);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            default:
                WriteString(sb, Str(value), ensureAscii);  // default=str
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent is null) return;
        sb.Append('\n').Append(' ', indent.Value * level);
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
                    if (c < 0x20 || (ensureAscii && c > 0x7e))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
