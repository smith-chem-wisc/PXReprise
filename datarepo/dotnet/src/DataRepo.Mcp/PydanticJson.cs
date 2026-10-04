using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DataRepo.Mcp;

/// <summary>The JSON text an agent reads: <c>pydantic_core.to_json(result, fallback=str, indent=2)</c>, which is how
/// the Python MCP SDK renders a tool's return value into the text content block.</summary>
/// <remarks>
/// Reproduced byte for byte because it IS the answer as the agent sees it. Its rules differ from Python's
/// <c>json.dumps</c> in ways a reader would notice:
/// <list type="bullet">
/// <item>floats are printed shortest-round-trip in Rust's layout (<c>0.00001</c>, <c>1e16</c>, <c>2.5e-7</c>), not
/// Python's (<c>1e-05</c>, <c>1e+16</c>); NaN and infinities are the bare tokens <c>NaN</c>/<c>Infinity</c>;</item>
/// <item>text is UTF-8, never <c>\u</c>-escaped past the control characters;</item>
/// <item>a Decimal is its <c>str()</c>, a date/datetime/time ISO 8601, a timedelta an ISO 8601 duration
/// (<c>P1Y35DT0.00001S</c>, a year being 365 days), bytes their UTF-8 text, anything else its <c>str()</c>.</item>
/// </list>
/// </remarks>
public static class PydanticJson
{
    /// <summary>The text content block: two-space indent.</summary>
    public static string Serialize(object? value, int? indent = 2, bool nanAsNull = false)
    {
        var sb = new StringBuilder();
        Write(sb, value, indent, 0, nanAsNull);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int? indent, int depth, bool nanAsNull)
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
                WriteString(sb, s);
                return;
            case double d:
                sb.Append(Float(d, nanAsNull));
                return;
            case float f:
                sb.Append(Float(f, nanAsNull));
                return;
            case long or int or short or sbyte or byte or uint or ushort or ulong or BigInteger:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case PyDecimal m:
                WriteString(sb, m.ToString());
                return;
            case PyTimedelta t:
                WriteString(sb, Duration(t));
                return;
            case DateOnly d:
                WriteString(sb, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return;
            case DateTime t:
                WriteString(sb, t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(t.Ticks));
                return;
            case DateTimeOffset t:
                WriteString(sb, t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(t.Ticks)
                    + (t.Offset == TimeSpan.Zero ? "Z" : (t.Offset < TimeSpan.Zero ? "-" : "+") + t.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)));
                return;
            case TimeOnly t:
                WriteString(sb, t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(t.Ticks));
                return;
            case Guid g:
                WriteString(sb, g.ToString("D"));
                return;
            case byte[] bytes:
                WriteString(sb, new UTF8Encoding(false, true).GetString(bytes));
                return;
            case IDictionary dict:
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                var first = true;
                foreach (var key in dict.Keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    WriteString(sb, PyValues.Str(key));
                    sb.Append(indent is null ? ":" : ": ");
                    Write(sb, dict[key], indent, depth + 1, nanAsNull);
                }
                NewLine(sb, indent, depth);
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
                    NewLine(sb, indent, depth + 1);
                    Write(sb, items[i], indent, depth + 1, nanAsNull);
                }
                NewLine(sb, indent, depth);
                sb.Append(']');
                return;
            }
            default:
                WriteString(sb, PyValues.Str(value));  // fallback=str
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int? indent, int depth)
    {
        if (indent is null) return;
        sb.Append('\n').Append(' ', indent.Value * depth);
    }

    private static string Fraction(long ticks)
    {
        var micro = ticks % TimeSpan.TicksPerSecond / 10;
        return micro == 0 ? "" : "." + micro.ToString("000000", CultureInfo.InvariantCulture);
    }

    /// <summary>serde_json's string escaping: <c>"</c>, <c>\</c>, the five short escapes, other control characters
    /// as <c>\u00xx</c>; everything else, DEL and non-ASCII included, as itself.</summary>
    private static void WriteString(StringBuilder sb, string s)
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
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>A float as pydantic-core writes it: the shortest round-trip digits, laid out as the Rust
    /// <c>ryu</c> crate's <c>format64</c> does.</summary>
    public static string Float(double value, bool nanAsNull = false)
    {
        if (double.IsNaN(value)) return nanAsNull ? "null" : "NaN";
        if (double.IsPositiveInfinity(value)) return nanAsNull ? "null" : "Infinity";
        if (double.IsNegativeInfinity(value)) return nanAsNull ? "null" : "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = r.StartsWith('-');
        if (negative) r = r[1..];
        var e = r.IndexOfAny(['E', 'e']);
        var mantissa = e >= 0 ? r[..e] : r;
        var exp10 = e >= 0 ? int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        var all = intPart + fracPart;
        var lead = all.Length - all.TrimStart('0').Length;
        var digits = all.TrimStart('0').TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        // kk: where the decimal point falls relative to the first significant digit.
        var kk = intPart.Length - lead + exp10;
        var length = digits.Length;
        var k = kk - length;

        string body;
        if (k >= 0 && kk <= 16)
            body = digits + new string('0', k) + ".0";
        else if (kk > 0 && kk <= 16)
            body = digits[..kk] + "." + digits[kk..];
        else if (kk > -5 && kk <= 0)
            body = "0." + new string('0', -kk) + digits;
        else
        {
            var exp = kk - 1;
            body = (length == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + exp.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + body : body;
    }

    /// <summary>A timedelta as an ISO 8601 duration, the way pydantic-core (speedate) writes it.</summary>
    public static string Duration(PyTimedelta t)
    {
        var total = t.TotalMicroseconds;
        var negative = total < 0;
        var abs = negative ? -(BigInteger)total : total;
        var days = (long)(abs / 86_400_000_000L);
        var rest = (long)(abs % 86_400_000_000L);
        var seconds = rest / 1_000_000L;
        var micro = rest % 1_000_000L;
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        sb.Append('P');
        if (days >= 365)
        {
            sb.Append(days / 365).Append('Y');
            days %= 365;
        }
        if (days != 0) sb.Append(days).Append('D');
        if (seconds != 0 || micro != 0)
        {
            sb.Append('T');
            var h = seconds / 3600;
            var m = seconds / 60 % 60;
            var s = seconds % 60;
            if (h != 0) sb.Append(h).Append('H');
            if (m != 0) sb.Append(m).Append('M');
            if (s != 0 || micro != 0)
            {
                sb.Append(s);
                if (micro != 0) sb.Append('.').Append(micro.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0'));
                sb.Append('S');
            }
        }
        if (sb.Length == (negative ? 2 : 1)) sb.Append("T0S");
        return sb.ToString();
    }
}
