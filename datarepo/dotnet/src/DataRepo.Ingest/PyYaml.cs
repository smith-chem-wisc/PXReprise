using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DataRepo.Ingest;

/// <summary>
/// Loads YAML into plain values with PyYAML's <c>safe_load</c> typing, because those types reach rows
/// and bundle ids.
/// </summary>
/// <remarks>
/// A manifest entry is hashed as Python's JSON of it, so <c>files: 2</c> must be the integer 2, not the
/// string "2", and <c>title: 2024-01-05</c> a date, exactly as PyYAML resolves them. Plain scalars are
/// resolved by YAML 1.1's implicit rules (PyYAML's resolver): null, bool (<c>yes</c>/<c>no</c>/<c>on</c>/
/// <c>off</c>/<c>true</c>/<c>false</c> in its three casings), int, float and date. A quoted scalar is
/// always a string. Mappings keep their document order.
/// </remarks>
public static class PyYaml
{
    private static readonly Regex Bool = new(
        "^(?:yes|Yes|YES|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$", RegexOptions.CultureInvariant);
    private static readonly Regex Null = new("^(?:~|null|Null|NULL|)$", RegexOptions.CultureInvariant);
    private static readonly Regex Int = new(
        "^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Float = new(
        @"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Date = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

    /// <summary>The first document of a YAML file as plain values.</summary>
    /// <exception cref="YamlException">The text is not valid YAML.</exception>
    public static object? LoadFile(string path)
    {
        var root = LoadRoot(path);
        return root is null ? null : Plain(root);
    }

    /// <summary>The first document of a YAML file as its node tree, or null for an empty file: for a reader that
    /// needs what <see cref="Plain"/> discards, such as a key written twice or a key's text as written.</summary>
    /// <exception cref="YamlException">The text is not valid YAML.</exception>
    public static YamlNode? LoadRoot(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];  // utf-8-sig
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
    }

    /// <summary>A YAML node as <see cref="Dictionary{TKey,TValue}"/> (document order), <see cref="List{T}"/>,
    /// string, long, double, bool, <see cref="DateOnly"/> or null.</summary>
    public static object? Plain(YamlNode node) => node switch
    {
        YamlMappingNode m => Mapping(m),
        YamlSequenceNode s => s.Children.Select(Plain).ToList(),
        YamlScalarNode s => Scalar(s),
        _ => null,
    };

    /// <summary>A mapping in document order; a repeated key keeps its first position and its last value, as
    /// PyYAML's <c>safe_load</c> does (a dict assignment).</summary>
    private static Dictionary<string, object?> Mapping(YamlMappingNode m)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in m.Children) map[KeyText(key)] = Plain(value);
        return map;
    }

    private static string KeyText(YamlNode key) => Plain(key) switch
    {
        null => "None",
        object o => YamlKey(o),
    };

    private static string YamlKey(object o) => o is string s ? s : DataRepo.Bundle.PyFormat.Str(o);

    private static object? Scalar(YamlScalarNode node)
    {
        var value = node.Value ?? "";
        if (node.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
            return value;
        if (node.Tag.IsEmpty is false && node.Tag.Value == "tag:yaml.org,2002:str") return value;
        if (Null.IsMatch(value)) return null;
        if (Bool.IsMatch(value)) return value.ToLowerInvariant() is "yes" or "true" or "on";
        if (Int.IsMatch(value)) return ParseInt(value);
        if (Float.IsMatch(value)) return ParseFloat(value);
        if (Date.IsMatch(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        return value;
    }

    private static long ParseInt(string text)
    {
        var s = text.Replace("_", "");
        var sign = 1L;
        if (s.StartsWith('-')) { sign = -1; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (s == "0") return 0;
        if (s.StartsWith("0b")) return sign * Convert.ToInt64(s[2..], 2);
        if (s.StartsWith("0x")) return sign * Convert.ToInt64(s[2..], 16);
        if (s.StartsWith('0')) return sign * Convert.ToInt64(s[1..], 8);
        if (s.Contains(':'))
        {
            long total = 0;
            foreach (var part in s.Split(':')) total = total * 60 + long.Parse(part, CultureInfo.InvariantCulture);
            return sign * total;
        }
        return sign * long.Parse(s, CultureInfo.InvariantCulture);
    }

    private static double ParseFloat(string text)
    {
        var s = text.Replace("_", "").ToLowerInvariant();
        var sign = 1.0;
        if (s.StartsWith('-')) { sign = -1; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (s == ".inf") return sign * double.PositiveInfinity;
        if (s == ".nan") return double.NaN;
        if (s.Contains(':'))
        {
            double total = 0;
            foreach (var part in s.Split(':')) total = total * 60 + double.Parse(part, CultureInfo.InvariantCulture);
            return sign * total;
        }
        return sign * double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
