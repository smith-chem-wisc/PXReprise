using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace DataRepo.Ingest;

/// <summary>Resolve MetaMorpheus modification names to UNIMOD accessions and monoisotopic masses.</summary>
/// <remarks>
/// MetaMorpheus names modifications in its own notation (<c>Common Biological:Hydroxylation on K</c>,
/// <c>UniProt:N6-acetyllysine on K</c>). The schema wants UNIMOD accessions, because ProForma 2 and every
/// downstream question are written in those. The mapping is not ours to invent: it is already written
/// down in the modification files that ship with the MetaMorpheus build that did the search, in the
/// <c>DR   Unimod; &lt;n&gt;.</c> lines of <c>Mods/*.txt</c> and <c>Data/ptmlist.txt</c>.
/// <para>So the registry is <i>read from the search's own MetaMorpheus install</i>, which pins it to the version
/// recorded in the manifest. A modification that no file resolves is reported, never guessed.</para>
/// <para>Reading these files here rather than asking mzLib's loader is known debt (G26): this reader never
/// reads <c>Data/unimod.xml</c> and prefers <c>Mods.txt</c> where the loader prefers Unimod. Ported as it is.</para>
/// Ported from <c>modlist.py</c>.
/// </remarks>
public static class ModList
{
    /// <summary>UniProt's <c>TG</c> lines spell the residue out; MetaMorpheus's use the letter.</summary>
    public static readonly IReadOnlyDictionary<string, string> ResidueNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alanine"] = "A", ["arginine"] = "R", ["asparagine"] = "N", ["aspartate"] = "D",
        ["aspartic acid"] = "D", ["cysteine"] = "C", ["glutamate"] = "E", ["glutamic acid"] = "E",
        ["glutamine"] = "Q", ["glycine"] = "G", ["histidine"] = "H", ["isoleucine"] = "I",
        ["leucine"] = "L", ["lysine"] = "K", ["methionine"] = "M", ["phenylalanine"] = "F",
        ["proline"] = "P", ["pyrrolysine"] = "O", ["selenocysteine"] = "U", ["serine"] = "S",
        ["threonine"] = "T", ["tryptophan"] = "W", ["tyrosine"] = "Y", ["valine"] = "V",
    };

    /// <summary>Python's <c>\s</c> in a <c>str</c> pattern (exactly <c>str.isspace</c>): .NET's <c>\s</c> lacks U+001C..U+001F.</summary>
    public const string PySpace = @"[\t\n\v\f\r\x1c-\x20\x85\xa0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]";

    /// <summary><c>^DR\s+Unimod;\s*(\d+)</c> under Python's <c>re.IGNORECASE</c>.</summary>
    /// <remarks>The letters are spelled as Python's case-insensitive classes rather than with .NET's IgnoreCase,
    /// because Python also lets <c>i</c> match U+0130 and U+0131 and .NET's invariant culture does not.</remarks>
    private static readonly Regex UnimodLine = new(
        @"^[Dd][Rr]" + PySpace + @"+[Uu][Nn][Ii\u0130\u0131][Mm][Oo][Dd];" + PySpace + @"*(\d+)",
        RegexOptions.CultureInvariant);

    /// <summary><c>Category:Name on X</c> as MetaMorpheus writes it inside a full-sequence bracket (Python <c>MOD_TOKEN</c>).</summary>
    /// <remarks>Groups <c>category</c>, <c>name</c> and <c>residue</c>. <c>$</c> also matches before a final
    /// newline, in both Python and .NET, and <c>.</c> excludes newline in both.</remarks>
    public static readonly Regex ModToken = new(
        @"^(?<category>[^:]+):(?<name>.+?) on (?<residue>[A-Zc-z]|[A-Z][a-z]+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TargetSplit = new(PySpace + "+or" + PySpace + "+|,", RegexOptions.CultureInvariant);

    /// <summary>Turn a <c>TG</c> line into single-letter residues (Python <c>_targets</c>).</summary>
    /// <remarks>Handles MetaMorpheus's <c>K or N</c>, its motif forms such as <c>Nxs</c>, and UniProt's <c>Methionine.</c></remarks>
    public static FrozenSet<string> Targets(string value)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in TargetSplit.Split(PyRStrip(PyStrip(value), '.')))
        {
            var part = PyStrip(raw);
            if (part.Length == 0) continue;
            if (ResidueNames.TryGetValue(PyLower(part), out var letter) && letter.Length > 0)
                output.Add(letter);
            else if (PyIsAlpha(part))
                // `C`, or a motif such as `Nxs` whose first residue is the modified one.
                output.Add(PyUpper(FirstCodePoint(part)));
        }
        return output.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Yield <see cref="ModEntry"/> rows from a UniProt-style <c>ID/TG/MM/DR</c> flat file (Python <c>_parse_entries</c>).</summary>
    public static IEnumerable<ModEntry> ParseEntries(string text, string source)
    {
        string? ident = null, target = null;
        double? mass = null;
        long? unimod = null;
        foreach (var raw in PySplitLines(text))
        {
            var line = PyRStrip(raw);
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(ident))
                    yield return new ModEntry(ident, Targets(target ?? ""), unimod, mass, source);
                ident = target = null;
                mass = null;
                unimod = null;
                continue;
            }
            if (line.StartsWith("ID   ", StringComparison.Ordinal))
            {
                // A new ID without a closing `//` means the previous block ended (ptmlist.txt style).
                if (!string.IsNullOrEmpty(ident))
                {
                    yield return new ModEntry(ident, Targets(target ?? ""), unimod, mass, source);
                    target = null;
                    mass = null;
                    unimod = null;
                }
                ident = PyStrip(line[5..]);
            }
            else if (line.StartsWith("TG   ", StringComparison.Ordinal))
                target = PyStrip(line[5..]);
            else if (line.StartsWith("MM   ", StringComparison.Ordinal))
                mass = PyFloat(PyStrip(line[5..]));
            else if (UnimodLine.Match(line) is { Success: true } m)
                unimod = PyInt(m.Groups[1].Value);
        }
        if (!string.IsNullOrEmpty(ident))
            yield return new ModEntry(ident, Targets(target ?? ""), unimod, mass, source);
    }

    // ------------------------------------------------------------------ Python text semantics

    /// <summary>Python's <c>str.isspace</c> for one UTF-16 unit (every Python whitespace is in the BMP).</summary>
    public static bool PyIsSpace(char c) =>
        c is >= '\t' and <= '\r' or >= '\x1c' and <= ' ' or '\x85' or '\xa0' or '\u1680' or >= '\u2000' and <= '\u200A'
          or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

    /// <summary>Python's <c>str.strip()</c>.</summary>
    public static string PyStrip(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && PyIsSpace(s[start])) start++;
        while (end > start && PyIsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Python's <c>str.rstrip()</c>.</summary>
    public static string PyRStrip(string s)
    {
        var end = s.Length;
        while (end > 0 && PyIsSpace(s[end - 1])) end--;
        return s[..end];
    }

    /// <summary>Python's <c>str.rstrip(ch)</c>.</summary>
    public static string PyRStrip(string s, char ch)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == ch) end--;
        return s[..end];
    }

    /// <summary>Python's <c>str.splitlines()</c>: breaks on <c>\n \r \r\n \v \f \x1c \x1d \x1e \x85 \u2028 \u2029</c>.</summary>
    public static List<string> PySplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..i]);
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
                continue;
            }
            i++;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>The first code point of a non-empty string (Python <c>s[0]</c>).</summary>
    public static string FirstCodePoint(string s) =>
        s.Length >= 2 && char.IsSurrogatePair(s[0], s[1]) ? s[..2] : s[..1];

    // Python's case mappings and isalpha are those of ITS Unicode database (Python 3.13: Unicode 15.1), with
    // full (multi-character) mappings; .NET's Rune mappings are simple ones from a newer Unicode. The tables below
    // are every code point where the two differ, found by comparing all 1,112,064 code points against Python
    // (the test fixture pycase.json), so these four functions agree with Python 3.13 on every single code point.
    // Not reproduced: Python's context-dependent Final_Sigma rule in lower() (a capital sigma ending a word);
    // it cannot change a match against the ASCII residue names, the only place lower() decides anything here.

    /// <summary>Code point ranges unassigned in Python 3.13's Unicode 15.1 but letters in .NET's newer Unicode.</summary>
    private const string PyUnassignedTable =
        "1C89-1C8A;A7CB-A7CD;A7DA-A7DC;105C0-105F3;10D4A-10D65;10D6F-10D85;10EC2-10EC4;11380-11389;1138B-1138"
        + "B;1138E-1138E;11390-113B5;113B7-113B7;113D1-113D1;113D3-113D3;11BC0-11BE0;13460-143FA;16100-1611D;16"
        + "D40-16D6C;18CFF-18CFF;1E5D0-1E5ED;1E5F0-1E5F0";

    /// <summary><c>cp:mapping</c> where Python's <c>upper()</c> differs from .NET's <see cref="Rune.ToUpperInvariant"/>.</summary>
    private const string PyUpperTable =
        "00DF:0053 0053;0131:0049;0149:02BC 004E;01F0:004A 030C;0390:0399 0308 0301;03B0:03A5 0308 0301;0587:"
        + "0535 0552;1E96:0048 0331;1E97:0054 0308;1E98:0057 030A;1E99:0059 030A;1E9A:0041 02BE;1F50:03A5 0313;"
        + "1F52:03A5 0313 0300;1F54:03A5 0313 0301;1F56:03A5 0313 0342;1F80:1F08 0399;1F81:1F09 0399;1F82:1F0A "
        + "0399;1F83:1F0B 0399;1F84:1F0C 0399;1F85:1F0D 0399;1F86:1F0E 0399;1F87:1F0F 0399;1F88:1F08 0399;1F89:"
        + "1F09 0399;1F8A:1F0A 0399;1F8B:1F0B 0399;1F8C:1F0C 0399;1F8D:1F0D 0399;1F8E:1F0E 0399;1F8F:1F0F 0399;"
        + "1F90:1F28 0399;1F91:1F29 0399;1F92:1F2A 0399;1F93:1F2B 0399;1F94:1F2C 0399;1F95:1F2D 0399;1F96:1F2E "
        + "0399;1F97:1F2F 0399;1F98:1F28 0399;1F99:1F29 0399;1F9A:1F2A 0399;1F9B:1F2B 0399;1F9C:1F2C 0399;1F9D:"
        + "1F2D 0399;1F9E:1F2E 0399;1F9F:1F2F 0399;1FA0:1F68 0399;1FA1:1F69 0399;1FA2:1F6A 0399;1FA3:1F6B 0399;"
        + "1FA4:1F6C 0399;1FA5:1F6D 0399;1FA6:1F6E 0399;1FA7:1F6F 0399;1FA8:1F68 0399;1FA9:1F69 0399;1FAA:1F6A "
        + "0399;1FAB:1F6B 0399;1FAC:1F6C 0399;1FAD:1F6D 0399;1FAE:1F6E 0399;1FAF:1F6F 0399;1FB2:1FBA 0399;1FB3:"
        + "0391 0399;1FB4:0386 0399;1FB6:0391 0342;1FB7:0391 0342 0399;1FBC:0391 0399;1FC2:1FCA 0399;1FC3:0397 "
        + "0399;1FC4:0389 0399;1FC6:0397 0342;1FC7:0397 0342 0399;1FCC:0397 0399;1FD2:0399 0308 0300;1FD3:0399 "
        + "0308 0301;1FD6:0399 0342;1FD7:0399 0308 0342;1FE2:03A5 0308 0300;1FE3:03A5 0308 0301;1FE4:03A1 0313;"
        + "1FE6:03A5 0342;1FE7:03A5 0308 0342;1FF2:1FFA 0399;1FF3:03A9 0399;1FF4:038F 0399;1FF6:03A9 0342;1FF7:"
        + "03A9 0342 0399;1FFC:03A9 0399;FB00:0046 0046;FB01:0046 0049;FB02:0046 004C;FB03:0046 0046 0049;FB04:"
        + "0046 0046 004C;FB05:0053 0054;FB06:0053 0054;FB13:0544 0546;FB14:0544 0535;FB15:0544 053B;FB16:054E "
        + "0546;FB17:0544 053D";

    /// <summary>Where Python's <c>lower()</c> differs from .NET's <see cref="Rune.ToLowerInvariant"/>.</summary>
    private const string PyLowerTable = "0130:0069 0307";

    /// <summary>Where Python's <c>casefold()</c> differs from .NET's lower-of-upper.</summary>
    private const string PyCaseFoldTable =
        "00DF:0073 0073;0130:0069 0307;0149:02BC 006E;01F0:006A 030C;0390:03B9 0308 0301;03B0:03C5 0308 0301;"
        + "0587:0565 0582;13A0:13A0;13A1:13A1;13A2:13A2;13A3:13A3;13A4:13A4;13A5:13A5;13A6:13A6;13A7:13A7;13A8:"
        + "13A8;13A9:13A9;13AA:13AA;13AB:13AB;13AC:13AC;13AD:13AD;13AE:13AE;13AF:13AF;13B0:13B0;13B1:13B1;13B2:"
        + "13B2;13B3:13B3;13B4:13B4;13B5:13B5;13B6:13B6;13B7:13B7;13B8:13B8;13B9:13B9;13BA:13BA;13BB:13BB;13BC:"
        + "13BC;13BD:13BD;13BE:13BE;13BF:13BF;13C0:13C0;13C1:13C1;13C2:13C2;13C3:13C3;13C4:13C4;13C5:13C5;13C6:"
        + "13C6;13C7:13C7;13C8:13C8;13C9:13C9;13CA:13CA;13CB:13CB;13CC:13CC;13CD:13CD;13CE:13CE;13CF:13CF;13D0:"
        + "13D0;13D1:13D1;13D2:13D2;13D3:13D3;13D4:13D4;13D5:13D5;13D6:13D6;13D7:13D7;13D8:13D8;13D9:13D9;13DA:"
        + "13DA;13DB:13DB;13DC:13DC;13DD:13DD;13DE:13DE;13DF:13DF;13E0:13E0;13E1:13E1;13E2:13E2;13E3:13E3;13E4:"
        + "13E4;13E5:13E5;13E6:13E6;13E7:13E7;13E8:13E8;13E9:13E9;13EA:13EA;13EB:13EB;13EC:13EC;13ED:13ED;13EE:"
        + "13EE;13EF:13EF;13F0:13F0;13F1:13F1;13F2:13F2;13F3:13F3;13F4:13F4;13F5:13F5;13F8:13F0;13F9:13F1;13FA:"
        + "13F2;13FB:13F3;13FC:13F4;13FD:13F5;1E96:0068 0331;1E97:0074 0308;1E98:0077 030A;1E99:0079 030A;1E9A:"
        + "0061 02BE;1E9E:0073 0073;1F50:03C5 0313;1F52:03C5 0313 0300;1F54:03C5 0313 0301;1F56:03C5 0313 0342;"
        + "1F80:1F00 03B9;1F81:1F01 03B9;1F82:1F02 03B9;1F83:1F03 03B9;1F84:1F04 03B9;1F85:1F05 03B9;1F86:1F06 "
        + "03B9;1F87:1F07 03B9;1F88:1F00 03B9;1F89:1F01 03B9;1F8A:1F02 03B9;1F8B:1F03 03B9;1F8C:1F04 03B9;1F8D:"
        + "1F05 03B9;1F8E:1F06 03B9;1F8F:1F07 03B9;1F90:1F20 03B9;1F91:1F21 03B9;1F92:1F22 03B9;1F93:1F23 03B9;"
        + "1F94:1F24 03B9;1F95:1F25 03B9;1F96:1F26 03B9;1F97:1F27 03B9;1F98:1F20 03B9;1F99:1F21 03B9;1F9A:1F22 "
        + "03B9;1F9B:1F23 03B9;1F9C:1F24 03B9;1F9D:1F25 03B9;1F9E:1F26 03B9;1F9F:1F27 03B9;1FA0:1F60 03B9;1FA1:"
        + "1F61 03B9;1FA2:1F62 03B9;1FA3:1F63 03B9;1FA4:1F64 03B9;1FA5:1F65 03B9;1FA6:1F66 03B9;1FA7:1F67 03B9;"
        + "1FA8:1F60 03B9;1FA9:1F61 03B9;1FAA:1F62 03B9;1FAB:1F63 03B9;1FAC:1F64 03B9;1FAD:1F65 03B9;1FAE:1F66 "
        + "03B9;1FAF:1F67 03B9;1FB2:1F70 03B9;1FB3:03B1 03B9;1FB4:03AC 03B9;1FB6:03B1 0342;1FB7:03B1 0342 03B9;"
        + "1FBC:03B1 03B9;1FC2:1F74 03B9;1FC3:03B7 03B9;1FC4:03AE 03B9;1FC6:03B7 0342;1FC7:03B7 0342 03B9;1FCC:"
        + "03B7 03B9;1FD2:03B9 0308 0300;1FD3:03B9 0308 0301;1FD6:03B9 0342;1FD7:03B9 0308 0342;1FE2:03C5 0308 "
        + "0300;1FE3:03C5 0308 0301;1FE4:03C1 0313;1FE6:03C5 0342;1FE7:03C5 0308 0342;1FF2:1F7C 03B9;1FF3:03C9 "
        + "03B9;1FF4:03CE 03B9;1FF6:03C9 0342;1FF7:03C9 0342 03B9;1FFC:03C9 03B9;AB70:13A0;AB71:13A1;AB72:13A2;"
        + "AB73:13A3;AB74:13A4;AB75:13A5;AB76:13A6;AB77:13A7;AB78:13A8;AB79:13A9;AB7A:13AA;AB7B:13AB;AB7C:13AC;"
        + "AB7D:13AD;AB7E:13AE;AB7F:13AF;AB80:13B0;AB81:13B1;AB82:13B2;AB83:13B3;AB84:13B4;AB85:13B5;AB86:13B6;"
        + "AB87:13B7;AB88:13B8;AB89:13B9;AB8A:13BA;AB8B:13BB;AB8C:13BC;AB8D:13BD;AB8E:13BE;AB8F:13BF;AB90:13C0;"
        + "AB91:13C1;AB92:13C2;AB93:13C3;AB94:13C4;AB95:13C5;AB96:13C6;AB97:13C7;AB98:13C8;AB99:13C9;AB9A:13CA;"
        + "AB9B:13CB;AB9C:13CC;AB9D:13CD;AB9E:13CE;AB9F:13CF;ABA0:13D0;ABA1:13D1;ABA2:13D2;ABA3:13D3;ABA4:13D4;"
        + "ABA5:13D5;ABA6:13D6;ABA7:13D7;ABA8:13D8;ABA9:13D9;ABAA:13DA;ABAB:13DB;ABAC:13DC;ABAD:13DD;ABAE:13DE;"
        + "ABAF:13DF;ABB0:13E0;ABB1:13E1;ABB2:13E2;ABB3:13E3;ABB4:13E4;ABB5:13E5;ABB6:13E6;ABB7:13E7;ABB8:13E8;"
        + "ABB9:13E9;ABBA:13EA;ABBB:13EB;ABBC:13EC;ABBD:13ED;ABBE:13EE;ABBF:13EF;FB00:0066 0066;FB01:0066 0069;"
        + "FB02:0066 006C;FB03:0066 0066 0069;FB04:0066 0066 006C;FB05:0073 0074;FB06:0073 0074;FB13:0574 0576;"
        + "FB14:0574 0565;FB15:0574 056B;FB16:057E 0576;FB17:0574 056D";

    private static readonly (int First, int Last)[] PyUnassigned = PyUnassignedTable.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(r => r.Split('-'))
        .Select(r => (int.Parse(r[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                      int.Parse(r[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
        .ToArray();

    private static readonly FrozenDictionary<int, string> PyUpperMap = CaseTable(PyUpperTable);
    private static readonly FrozenDictionary<int, string> PyLowerMap = CaseTable(PyLowerTable);
    private static readonly FrozenDictionary<int, string> PyCaseFoldMap = CaseTable(PyCaseFoldTable);

    private static FrozenDictionary<int, string> CaseTable(string table) => table
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Split(':'))
        .ToFrozenDictionary(
            e => int.Parse(e[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            e => string.Concat(e[1].Split(' ').Select(h =>
                char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))));

    private static bool PyUnassignedCodePoint(int cp)
    {
        foreach (var (first, last) in PyUnassigned)
            if (cp >= first && cp <= last) return true;
        return false;
    }

    private static string MapCodePoints(string s, FrozenDictionary<int, string> exceptions, Func<Rune, Rune> simple)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.IsAscii) sb.Append(simple(rune).ToString());
            else if (exceptions.TryGetValue(rune.Value, out var mapped)) sb.Append(mapped);
            else if (PyUnassignedCodePoint(rune.Value)) sb.Append(rune.ToString());
            else sb.Append(simple(rune).ToString());
        }
        return sb.ToString();
    }

    /// <summary>Python's <c>str.isalpha()</c>: non-empty and every code point a letter (Lu, Ll, Lt, Lm, Lo).</summary>
    public static bool PyIsAlpha(string s)
    {
        if (s.Length == 0) return false;
        foreach (var rune in s.EnumerateRunes())
            if (!Rune.IsLetter(rune) || PyUnassignedCodePoint(rune.Value)) return false;
        return true;
    }

    /// <summary>Python's <c>str.lower()</c> (see the tables above for how exact).</summary>
    public static string PyLower(string s) => MapCodePoints(s, PyLowerMap, Rune.ToLowerInvariant);

    /// <summary>Python's <c>str.upper()</c>, with its multi-character results (sharp s to <c>SS</c>, ligatures, ...).</summary>
    public static string PyUpper(string s) => MapCodePoints(s, PyUpperMap, Rune.ToUpperInvariant);

    /// <summary>Python's <c>str.casefold()</c>, with its full foldings (sharp s to <c>ss</c>, Cherokee to capitals, ...).</summary>
    public static string PyCaseFold(string s) =>
        MapCodePoints(s, PyCaseFoldMap, r => Rune.ToLowerInvariant(Rune.ToUpperInvariant(r)));

    private static readonly Regex PyFloatGrammar = new(
        @"^[+-]?(?:(?:[0-9](?:_?[0-9])*)?\.[0-9](?:_?[0-9])*|[0-9](?:_?[0-9])*\.?)(?:[eE][+-]?[0-9](?:_?[0-9])*)?\z",
        RegexOptions.CultureInvariant);

    /// <summary>Python's <c>float(s)</c>, or null where Python raises <c>ValueError</c>.</summary>
    /// <remarks>Accepts what Python accepts: surrounding whitespace, any Unicode decimal digit, underscores between
    /// digits, a bare leading or trailing point, and <c>inf</c>/<c>infinity</c>/<c>nan</c> in any case with a sign.
    /// An exponent past the range gives infinity, as Python's does.</remarks>
    public static double? PyFloat(string s)
    {
        var text = new StringBuilder(s.Length);
        foreach (var rune in PyStrip(s).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
                text.Append((char)('0' + (int)Rune.GetNumericValue(rune)));
            else if (rune.IsAscii)
                text.Append((char)rune.Value);
            else
                return null;
        }
        var t = text.ToString();
        var body = t.TrimStart('+', '-');
        if (body.Length < t.Length - 1) return null;  // more than one sign
        var negative = t.StartsWith('-');
        switch (body.ToLowerInvariant())
        {
            case "inf" or "infinity":
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            case "nan":
                return negative ? -double.NaN : double.NaN;
        }
        if (!PyFloatGrammar.IsMatch(t)) return null;
        return double.Parse(t.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Python's <c>int(s)</c> over a run of Unicode decimal digits (what <c>\d+</c> captured).</summary>
    /// <remarks>Python's int is unbounded; a value past <see cref="long.MaxValue"/> throws <see cref="OverflowException"/>
    /// here. A UNIMOD accession is at most five digits.</remarks>
    public static long PyInt(string digits)
    {
        long value = 0;
        foreach (var rune in digits.EnumerateRunes())
            value = checked(value * 10 + (long)Rune.GetNumericValue(rune));
        return value;
    }

    /// <summary>Python's <c>format(value, "+.{digits}f")</c> (or without the forced sign): exact, round-half-even.</summary>
    public static string PyFixed(double value, int digits, bool forceSign)
    {
        if (double.IsNaN(value)) return forceSign ? "+nan" : "nan";
        var negative = double.IsNegative(value);
        var sign = negative ? "-" : forceSign ? "+" : "";
        if (double.IsInfinity(value)) return sign + "inf";

        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & ((1L << 52) - 1);
        int e;
        if (exponent == 0) e = -1074;
        else
        {
            mantissa |= 1L << 52;
            e = exponent - 1075;
        }
        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        BigInteger q;
        if (e >= 0) q = scaled << e;
        else
        {
            var den = BigInteger.One << -e;
            q = BigInteger.DivRem(scaled, den, out var r);
            var twice = r * 2;
            if (twice > den || (twice == den && !q.IsEven)) q += 1;
        }
        var s = q.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        return digits == 0 ? sign + s : sign + s[..^digits] + "." + s[^digits..];
    }
}

/// <summary>One modification as its defining file describes it.</summary>
/// <remarks>Equality is by value, the target set compared as a set, as Python's frozen dataclass compares.</remarks>
public sealed record ModEntry(string Name, IReadOnlySet<string> Targets, long? Unimod, double? MonoisotopicMass, string Source)
{
    /// <summary><c>UNIMOD:&lt;n&gt;</c>, or null when no file gave an accession.</summary>
    public string? UnimodCurie => Unimod is null ? null : "UNIMOD:" + Unimod.Value.ToString(CultureInfo.InvariantCulture);

    public bool Equals(ModEntry? other) =>
        other is not null && Name == other.Name && Targets.SetEquals(other.Targets) && Unimod == other.Unimod
        && Nullable.Equals(MonoisotopicMass, other.MonoisotopicMass) && Source == other.Source;

    public override int GetHashCode()
    {
        var targets = 0;
        foreach (var t in Targets) targets ^= StringComparer.Ordinal.GetHashCode(t);
        return HashCode.Combine(Name, targets, Unimod, MonoisotopicMass, Source);
    }
}

/// <summary>Every modification the search engine could have written, indexed by name.</summary>
public sealed class ModRegistry
{
    private readonly Dictionary<string, List<ModEntry>> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _keyOrder = [];
    private readonly List<string> _sources = [];

    public ModRegistry(IEnumerable<ModEntry>? entries = null)
    {
        foreach (var entry in entries ?? []) Add(entry);
    }

    /// <summary>The files entries came from, in first-seen order (Python <c>sources</c>).</summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>How many entries (Python <c>len(registry)</c>).</summary>
    public int Count => _byName.Values.Sum(v => v.Count);

    /// <summary>Every entry, grouped by case-folded name in first-seen order, each group in the order added.</summary>
    public IEnumerable<ModEntry> Entries => _keyOrder.SelectMany(k => _byName[k]);

    public void Add(ModEntry entry)
    {
        var key = ModList.PyCaseFold(entry.Name);
        if (!_byName.TryGetValue(key, out var list))
        {
            _byName[key] = list = [];
            _keyOrder.Add(key);
        }
        list.Add(entry);
        if (!_sources.Contains(entry.Source)) _sources.Add(entry.Source);
    }

    /// <summary>Read <c>Mods/*.txt</c> and <c>Data/ptmlist.txt</c> from a MetaMorpheus install.</summary>
    /// <remarks>A missing install is not an error here; it yields an empty registry, and every unresolved
    /// modification is reported by the caller instead. <c>Mods/*.txt</c> is matched and sorted as Python's
    /// <c>pathlib</c> does ON WINDOWS, case-insensitively, on every platform. Python's order was
    /// platform-dependent (case-sensitive on POSIX), and the order decides which file's entry a name lookup
    /// finds, so the same search ingested on Linux would have resolved modifications differently. Every
    /// stored bundle was written on Windows, so that order is the contract (found by CI, 2026-10-04; G83).
    /// Files are decoded as <c>utf-8-sig</c> with <c>errors="replace"</c> (one leading BOM dropped, bad bytes
    /// become U+FFFD).</remarks>
    public static ModRegistry FromMetaMorpheus(string installDir)
    {
        var registry = new ModRegistry();
        const bool windows = true;
        var comparison = StringComparison.OrdinalIgnoreCase;
        var paths = new List<string>();
        var mods = Path.Combine(installDir, "Mods");
        if (Directory.Exists(mods))
        {
            var names = Directory.EnumerateFiles(mods)
                .Where(p => Path.GetFileName(p).EndsWith(".txt", comparison))
                .ToList();
            // pathlib orders Windows paths by their lower-cased text, POSIX paths by their text.
            names.Sort((a, b) => windows
                ? string.CompareOrdinal(ModList.PyLower(Path.GetFileName(a)), ModList.PyLower(Path.GetFileName(b)))
                : string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            paths.AddRange(names);
        }
        paths.Add(Path.Combine(installDir, "Data", "ptmlist.txt"));
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = utf8.GetString(bytes, skip, bytes.Length - skip);
            foreach (var entry in ModList.ParseEntries(text, Path.GetFileName(path)))
                registry.Add(entry);
        }
        return registry;
    }

    /// <summary>Find the entry for a MetaMorpheus modification name.</summary>
    /// <remarks><paramref name="name"/> is the part inside the bracket after the category, e.g.
    /// <c>Hydroxylation on K</c> or <c>N6-acetyllysine on K</c>. Some files spell the target into the ID and some
    /// do not, so the full name is tried first and then the name with <c> on &lt;residue&gt;</c> stripped.
    /// An empty <paramref name="residue"/> counts as none, as Python's falsy test does.</remarks>
    public ModEntry? Lookup(string name, string? residue = null)
    {
        _byName.TryGetValue(ModList.PyCaseFold(name), out var candidates);
        if (candidates is null && name.Contains(" on ", StringComparison.Ordinal))
        {
            var cut = name.LastIndexOf(" on ", StringComparison.Ordinal);
            var stem = name[..cut];
            var tail = ModList.PyStrip(name[(cut + 4)..]);
            if (string.IsNullOrEmpty(residue))
                residue = tail.Length == 0 ? "" : ModList.PyUpper(ModList.FirstCodePoint(tail));
            _byName.TryGetValue(ModList.PyCaseFold(stem), out candidates);
        }
        if (candidates is null) return null;
        if (!string.IsNullOrEmpty(residue))
        {
            var wanted = ModList.PyUpper(residue);
            foreach (var entry in candidates)
                if (entry.Targets.Contains(wanted))
                    return entry;
        }
        // No target match: prefer an entry that at least carries a UNIMOD accession.
        foreach (var entry in candidates)
            if (entry.Unimod is not null)
                return entry;
        return candidates[0];
    }
}
