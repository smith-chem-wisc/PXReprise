using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace PXReprise.Ingest;

/// <summary>Resolve MetaMorpheus modification names to UNIMOD accessions and monoisotopic masses.</summary>
/// <remarks>
/// MetaMorpheus names modifications in its own notation (<c>Common Biological:Hydroxylation on K</c>,
/// <c>UniProt:N6-acetyllysine on K</c>). The schema wants UNIMOD accessions, because ProForma 2 and every
/// downstream question are written in those. The mapping is not ours to invent: it is already written
/// down in the modification files that ship with the MetaMorpheus build that did the search, in the
/// <c>DR   Unimod; &lt;n&gt;.</c> lines of <c>Mods/*.txt</c> and <c>Data/ptmlist.txt</c>.
/// <para>So the registry is <i>read from the search's own MetaMorpheus install</i>, which pins it to the version
/// recorded in the manifest. A modification that no file resolves is reported, never guessed.</para>
/// <para>This file reader is <see cref="IngestRules.Python0320"/>'s. Under <see cref="IngestRules.Current"/> the
/// registry comes from mzLib's own loaders over the same install, as MetaMorpheus builds it
/// (<see cref="ModRegistry.FromMzLib"/>, G26), because this reader never reads <c>Data/unimod.xml</c>, reads no
/// mass from a chemical formula, and takes UniProt's <c>MM</c> line without mzLib's formal-charge correction.</para>
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
    /// <remarks>Under <see cref="IngestRules.Current"/> an entry starts clean at its <c>ID</c> line: a <c>TG</c>,
    /// <c>MM</c> or <c>DR</c> line between a <c>//</c> and the next <c>ID</c> belongs to no entry. Python 0.32.0
    /// (<see cref="IngestRules.Python0320"/>) carried such lines into the next entry, giving it a target, mass or
    /// UNIMOD accession its own block never stated (G83 item 2).</remarks>
    public static IEnumerable<ModEntry> ParseEntries(string text, string source, IngestRules rules = IngestRules.Current)
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
                    yield return new ModEntry(ident, Targets(target ?? ""), unimod, mass, source);
                if (!string.IsNullOrEmpty(ident) || rules == IngestRules.Current)
                {
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
    // full (multi-character) mappings; .NET's Rune mappings are simple ones from a newer Unicode, which on Linux is
    // the machine's ICU and moves with it (Unicode 17 reached CI's runner on 2026-11-01). The case tables below are
    // every code point where the two differ, found by comparing all 1,112,064 code points against Python (the test
    // fixture pycase.json); the unassigned table is all of 15.1's, so a later Unicode cannot leak in. These four
    // functions agree with Python 3.13 on every single code point.
    // Not reproduced: Python's context-dependent Final_Sigma rule in lower() (a capital sigma ending a word);
    // it cannot change a match against the ASCII residue names, the only place lower() decides anything here.

    /// <summary>Every code point range unassigned (<c>Cn</c>) in Python 3.13's Unicode 15.1, from <c>unicodedata</c>. Python
    /// leaves these unchanged and not alphabetic whatever Unicode the machine's .NET or ICU knows.</summary>
    private const string PyUnassignedTable =
        "378-379;380-383;38B-38B;38D-38D;3A2-3A2;530-530;557-558;58B-58C;590-590;5C8-5CF;5EB-5EE;5F5-5FF;70E-70"
        + "E;74B-74C;7B2-7BF;7FB-7FC;82E-82F;83F-83F;85C-85D;85F-85F;86B-86F;88F-88F;892-897;984-984;98D-98E;991-"
        + "992;9A9-9A9;9B1-9B1;9B3-9B5;9BA-9BB;9C5-9C6;9C9-9CA;9CF-9D6;9D8-9DB;9DE-9DE;9E4-9E5;9FF-A00;A04-A04;A0"
        + "B-A0E;A11-A12;A29-A29;A31-A31;A34-A34;A37-A37;A3A-A3B;A3D-A3D;A43-A46;A49-A4A;A4E-A50;A52-A58;A5D-A5D;"
        + "A5F-A65;A77-A80;A84-A84;A8E-A8E;A92-A92;AA9-AA9;AB1-AB1;AB4-AB4;ABA-ABB;AC6-AC6;ACA-ACA;ACE-ACF;AD1-AD"
        + "F;AE4-AE5;AF2-AF8;B00-B00;B04-B04;B0D-B0E;B11-B12;B29-B29;B31-B31;B34-B34;B3A-B3B;B45-B46;B49-B4A;B4E-"
        + "B54;B58-B5B;B5E-B5E;B64-B65;B78-B81;B84-B84;B8B-B8D;B91-B91;B96-B98;B9B-B9B;B9D-B9D;BA0-BA2;BA5-BA7;BA"
        + "B-BAD;BBA-BBD;BC3-BC5;BC9-BC9;BCE-BCF;BD1-BD6;BD8-BE5;BFB-BFF;C0D-C0D;C11-C11;C29-C29;C3A-C3B;C45-C45;"
        + "C49-C49;C4E-C54;C57-C57;C5B-C5C;C5E-C5F;C64-C65;C70-C76;C8D-C8D;C91-C91;CA9-CA9;CB4-CB4;CBA-CBB;CC5-CC"
        + "5;CC9-CC9;CCE-CD4;CD7-CDC;CDF-CDF;CE4-CE5;CF0-CF0;CF4-CFF;D0D-D0D;D11-D11;D45-D45;D49-D49;D50-D53;D64-"
        + "D65;D80-D80;D84-D84;D97-D99;DB2-DB2;DBC-DBC;DBE-DBF;DC7-DC9;DCB-DCE;DD5-DD5;DD7-DD7;DE0-DE5;DF0-DF1;DF"
        + "5-E00;E3B-E3E;E5C-E80;E83-E83;E85-E85;E8B-E8B;EA4-EA4;EA6-EA6;EBE-EBF;EC5-EC5;EC7-EC7;ECF-ECF;EDA-EDB;"
        + "EE0-EFF;F48-F48;F6D-F70;F98-F98;FBD-FBD;FCD-FCD;FDB-FFF;10C6-10C6;10C8-10CC;10CE-10CF;1249-1249;124E-1"
        + "24F;1257-1257;1259-1259;125E-125F;1289-1289;128E-128F;12B1-12B1;12B6-12B7;12BF-12BF;12C1-12C1;12C6-12C"
        + "7;12D7-12D7;1311-1311;1316-1317;135B-135C;137D-137F;139A-139F;13F6-13F7;13FE-13FF;169D-169F;16F9-16FF;"
        + "1716-171E;1737-173F;1754-175F;176D-176D;1771-1771;1774-177F;17DE-17DF;17EA-17EF;17FA-17FF;181A-181F;18"
        + "79-187F;18AB-18AF;18F6-18FF;191F-191F;192C-192F;193C-193F;1941-1943;196E-196F;1975-197F;19AC-19AF;19CA"
        + "-19CF;19DB-19DD;1A1C-1A1D;1A5F-1A5F;1A7D-1A7E;1A8A-1A8F;1A9A-1A9F;1AAE-1AAF;1ACF-1AFF;1B4D-1B4F;1B7F-1"
        + "B7F;1BF4-1BFB;1C38-1C3A;1C4A-1C4C;1C89-1C8F;1CBB-1CBC;1CC8-1CCF;1CFB-1CFF;1F16-1F17;1F1E-1F1F;1F46-1F4"
        + "7;1F4E-1F4F;1F58-1F58;1F5A-1F5A;1F5C-1F5C;1F5E-1F5E;1F7E-1F7F;1FB5-1FB5;1FC5-1FC5;1FD4-1FD5;1FDC-1FDC;"
        + "1FF0-1FF1;1FF5-1FF5;1FFF-1FFF;2065-2065;2072-2073;208F-208F;209D-209F;20C1-20CF;20F1-20FF;218C-218F;24"
        + "27-243F;244B-245F;2B74-2B75;2B96-2B96;2CF4-2CF8;2D26-2D26;2D28-2D2C;2D2E-2D2F;2D68-2D6E;2D71-2D7E;2D97"
        + "-2D9F;2DA7-2DA7;2DAF-2DAF;2DB7-2DB7;2DBF-2DBF;2DC7-2DC7;2DCF-2DCF;2DD7-2DD7;2DDF-2DDF;2E5E-2E7F;2E9A-2"
        + "E9A;2EF4-2EFF;2FD6-2FEF;3040-3040;3097-3098;3100-3104;3130-3130;318F-318F;31E4-31EE;321F-321F;A48D-A48"
        + "F;A4C7-A4CF;A62C-A63F;A6F8-A6FF;A7CB-A7CF;A7D2-A7D2;A7D4-A7D4;A7DA-A7F1;A82D-A82F;A83A-A83F;A878-A87F;"
        + "A8C6-A8CD;A8DA-A8DF;A954-A95E;A97D-A97F;A9CE-A9CE;A9DA-A9DD;A9FF-A9FF;AA37-AA3F;AA4E-AA4F;AA5A-AA5B;AA"
        + "C3-AADA;AAF7-AB00;AB07-AB08;AB0F-AB10;AB17-AB1F;AB27-AB27;AB2F-AB2F;AB6C-AB6F;ABEE-ABEF;ABFA-ABFF;D7A4"
        + "-D7AF;D7C7-D7CA;D7FC-D7FF;FA6E-FA6F;FADA-FAFF;FB07-FB12;FB18-FB1C;FB37-FB37;FB3D-FB3D;FB3F-FB3F;FB42-F"
        + "B42;FB45-FB45;FBC3-FBD2;FD90-FD91;FDC8-FDCE;FDD0-FDEF;FE1A-FE1F;FE53-FE53;FE67-FE67;FE6C-FE6F;FE75-FE7"
        + "5;FEFD-FEFE;FF00-FF00;FFBF-FFC1;FFC8-FFC9;FFD0-FFD1;FFD8-FFD9;FFDD-FFDF;FFE7-FFE7;FFEF-FFF8;FFFE-FFFF;"
        + "1000C-1000C;10027-10027;1003B-1003B;1003E-1003E;1004E-1004F;1005E-1007F;100FB-100FF;10103-10106;10134-"
        + "10136;1018F-1018F;1019D-1019F;101A1-101CF;101FE-1027F;1029D-1029F;102D1-102DF;102FC-102FF;10324-1032C;"
        + "1034B-1034F;1037B-1037F;1039E-1039E;103C4-103C7;103D6-103FF;1049E-1049F;104AA-104AF;104D4-104D7;104FC-"
        + "104FF;10528-1052F;10564-1056E;1057B-1057B;1058B-1058B;10593-10593;10596-10596;105A2-105A2;105B2-105B2;"
        + "105BA-105BA;105BD-105FF;10737-1073F;10756-1075F;10768-1077F;10786-10786;107B1-107B1;107BB-107FF;10806-"
        + "10807;10809-10809;10836-10836;10839-1083B;1083D-1083E;10856-10856;1089F-108A6;108B0-108DF;108F3-108F3;"
        + "108F6-108FA;1091C-1091E;1093A-1093E;10940-1097F;109B8-109BB;109D0-109D1;10A04-10A04;10A07-10A0B;10A14-"
        + "10A14;10A18-10A18;10A36-10A37;10A3B-10A3E;10A49-10A4F;10A59-10A5F;10AA0-10ABF;10AE7-10AEA;10AF7-10AFF;"
        + "10B36-10B38;10B56-10B57;10B73-10B77;10B92-10B98;10B9D-10BA8;10BB0-10BFF;10C49-10C7F;10CB3-10CBF;10CF3-"
        + "10CF9;10D28-10D2F;10D3A-10E5F;10E7F-10E7F;10EAA-10EAA;10EAE-10EAF;10EB2-10EFC;10F28-10F2F;10F5A-10F6F;"
        + "10F8A-10FAF;10FCC-10FDF;10FF7-10FFF;1104E-11051;11076-1107E;110C3-110CC;110CE-110CF;110E9-110EF;110FA-"
        + "110FF;11135-11135;11148-1114F;11177-1117F;111E0-111E0;111F5-111FF;11212-11212;11242-1127F;11287-11287;"
        + "11289-11289;1128E-1128E;1129E-1129E;112AA-112AF;112EB-112EF;112FA-112FF;11304-11304;1130D-1130E;11311-"
        + "11312;11329-11329;11331-11331;11334-11334;1133A-1133A;11345-11346;11349-1134A;1134E-1134F;11351-11356;"
        + "11358-1135C;11364-11365;1136D-1136F;11375-113FF;1145C-1145C;11462-1147F;114C8-114CF;114DA-1157F;115B6-"
        + "115B7;115DE-115FF;11645-1164F;1165A-1165F;1166D-1167F;116BA-116BF;116CA-116FF;1171B-1171C;1172C-1172F;"
        + "11747-117FF;1183C-1189F;118F3-118FE;11907-11908;1190A-1190B;11914-11914;11917-11917;11936-11936;11939-"
        + "1193A;11947-1194F;1195A-1199F;119A8-119A9;119D8-119D9;119E5-119FF;11A48-11A4F;11AA3-11AAF;11AF9-11AFF;"
        + "11B0A-11BFF;11C09-11C09;11C37-11C37;11C46-11C4F;11C6D-11C6F;11C90-11C91;11CA8-11CA8;11CB7-11CFF;11D07-"
        + "11D07;11D0A-11D0A;11D37-11D39;11D3B-11D3B;11D3E-11D3E;11D48-11D4F;11D5A-11D5F;11D66-11D66;11D69-11D69;"
        + "11D8F-11D8F;11D92-11D92;11D99-11D9F;11DAA-11EDF;11EF9-11EFF;11F11-11F11;11F3B-11F3D;11F5A-11FAF;11FB1-"
        + "11FBF;11FF2-11FFE;1239A-123FF;1246F-1246F;12475-1247F;12544-12F8F;12FF3-12FFF;13456-143FF;14647-167FF;"
        + "16A39-16A3F;16A5F-16A5F;16A6A-16A6D;16ABF-16ABF;16ACA-16ACF;16AEE-16AEF;16AF6-16AFF;16B46-16B4F;16B5A-"
        + "16B5A;16B62-16B62;16B78-16B7C;16B90-16E3F;16E9B-16EFF;16F4B-16F4E;16F88-16F8E;16FA0-16FDF;16FE5-16FEF;"
        + "16FF2-16FFF;187F8-187FF;18CD6-18CFF;18D09-1AFEF;1AFF4-1AFF4;1AFFC-1AFFC;1AFFF-1AFFF;1B123-1B131;1B133-"
        + "1B14F;1B153-1B154;1B156-1B163;1B168-1B16F;1B2FC-1BBFF;1BC6B-1BC6F;1BC7D-1BC7F;1BC89-1BC8F;1BC9A-1BC9B;"
        + "1BCA4-1CEFF;1CF2E-1CF2F;1CF47-1CF4F;1CFC4-1CFFF;1D0F6-1D0FF;1D127-1D128;1D1EB-1D1FF;1D246-1D2BF;1D2D4-"
        + "1D2DF;1D2F4-1D2FF;1D357-1D35F;1D379-1D3FF;1D455-1D455;1D49D-1D49D;1D4A0-1D4A1;1D4A3-1D4A4;1D4A7-1D4A8;"
        + "1D4AD-1D4AD;1D4BA-1D4BA;1D4BC-1D4BC;1D4C4-1D4C4;1D506-1D506;1D50B-1D50C;1D515-1D515;1D51D-1D51D;1D53A-"
        + "1D53A;1D53F-1D53F;1D545-1D545;1D547-1D549;1D551-1D551;1D6A6-1D6A7;1D7CC-1D7CD;1DA8C-1DA9A;1DAA0-1DAA0;"
        + "1DAB0-1DEFF;1DF1F-1DF24;1DF2B-1DFFF;1E007-1E007;1E019-1E01A;1E022-1E022;1E025-1E025;1E02B-1E02F;1E06E-"
        + "1E08E;1E090-1E0FF;1E12D-1E12F;1E13E-1E13F;1E14A-1E14D;1E150-1E28F;1E2AF-1E2BF;1E2FA-1E2FE;1E300-1E4CF;"
        + "1E4FA-1E7DF;1E7E7-1E7E7;1E7EC-1E7EC;1E7EF-1E7EF;1E7FF-1E7FF;1E8C5-1E8C6;1E8D7-1E8FF;1E94C-1E94F;1E95A-"
        + "1E95D;1E960-1EC70;1ECB5-1ED00;1ED3E-1EDFF;1EE04-1EE04;1EE20-1EE20;1EE23-1EE23;1EE25-1EE26;1EE28-1EE28;"
        + "1EE33-1EE33;1EE38-1EE38;1EE3A-1EE3A;1EE3C-1EE41;1EE43-1EE46;1EE48-1EE48;1EE4A-1EE4A;1EE4C-1EE4C;1EE50-"
        + "1EE50;1EE53-1EE53;1EE55-1EE56;1EE58-1EE58;1EE5A-1EE5A;1EE5C-1EE5C;1EE5E-1EE5E;1EE60-1EE60;1EE63-1EE63;"
        + "1EE65-1EE66;1EE6B-1EE6B;1EE73-1EE73;1EE78-1EE78;1EE7D-1EE7D;1EE7F-1EE7F;1EE8A-1EE8A;1EE9C-1EEA0;1EEA4-"
        + "1EEA4;1EEAA-1EEAA;1EEBC-1EEEF;1EEF2-1EFFF;1F02C-1F02F;1F094-1F09F;1F0AF-1F0B0;1F0C0-1F0C0;1F0D0-1F0D0;"
        + "1F0F6-1F0FF;1F1AE-1F1E5;1F203-1F20F;1F23C-1F23F;1F249-1F24F;1F252-1F25F;1F266-1F2FF;1F6D8-1F6DB;1F6ED-"
        + "1F6EF;1F6FD-1F6FF;1F777-1F77A;1F7DA-1F7DF;1F7EC-1F7EF;1F7F1-1F7FF;1F80C-1F80F;1F848-1F84F;1F85A-1F85F;"
        + "1F888-1F88F;1F8AE-1F8AF;1F8B2-1F8FF;1FA54-1FA5F;1FA6E-1FA6F;1FA7D-1FA7F;1FA89-1FA8F;1FABE-1FABE;1FAC6-"
        + "1FACD;1FADC-1FADF;1FAE9-1FAEF;1FAF9-1FAFF;1FB93-1FB93;1FBCB-1FBEF;1FBFA-1FFFF;2A6E0-2A6FF;2B73A-2B73F;"
        + "2B81E-2B81F;2CEA2-2CEAF;2EBE1-2EBEF;2EE5E-2F7FF;2FA1E-2FFFF;3134B-3134F;323B0-E0000;E0002-E001F;E0080-"
        + "E00FF;E01F0-EFFFF;FFFFE-FFFFF;10FFFE-10FFFF";

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
        int lo = 0, hi = PyUnassigned.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cp < PyUnassigned[mid].First) hi = mid - 1;
            else if (cp > PyUnassigned[mid].Last) lo = mid + 1;
            else return true;
        }
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
            else
            {
                // A newer Unicode can give an old letter a case partner Python's does not have (U+019B -> U+A7DC).
                var simpleMapped = simple(rune);
                sb.Append((PyUnassignedCodePoint(simpleMapped.Value) ? rune : simpleMapped).ToString());
            }
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
    /// <summary>mzLib's <c>ModificationType</c> (MetaMorpheus's category, e.g. <c>Common Fixed</c>), for an entry
    /// <see cref="ModRegistry.FromMzLib"/> made; null for one read from a file by this module.</summary>
    public string? Category { get; init; }

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

    /// <summary><c>Category:IdWithMotif</c> -> entry, for a registry <see cref="FromMzLib"/> built.</summary>
    private readonly Dictionary<string, ModEntry> _byToken = new(StringComparer.Ordinal);

    /// <summary>True when the registry came from mzLib's loaders, and so answers by the exact token (G26).</summary>
    public bool ByToken { get; private set; }

    /// <summary>What built the registry, for <c>bundle.json</c>: the mzLib that loaded it, or null for the file reader.</summary>
    public string? Loader { get; private set; }

    /// <summary>Tokens whose mass <see cref="FromMzLib"/> left out, each with why: this build's mzLib would give a mass
    /// the search did not use.</summary>
    public SortedDictionary<string, string> MassesWithheld { get; } = new(StringComparer.Ordinal);

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
    public static ModRegistry FromMetaMorpheus(string installDir, IngestRules rules = IngestRules.Current)
    {
        var registry = new ModRegistry();
        var paths = ModFiles(installDir);
        paths.Add(Path.Combine(installDir, "Data", "ptmlist.txt"));
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = utf8.GetString(bytes, skip, bytes.Length - skip);
            foreach (var entry in ModList.ParseEntries(text, Path.GetFileName(path), rules))
                registry.Add(entry);
        }
        return registry;
    }

    /// <summary>An install's <c>Mods/*.txt</c>, in the order every stored bundle read them (see
    /// <see cref="FromMetaMorpheus"/>).</summary>
    private static List<string> ModFiles(string installDir)
    {
        var mods = Path.Combine(installDir, "Mods");
        if (!Directory.Exists(mods)) return [];
        var names = Directory.EnumerateFiles(mods)
            .Where(p => Path.GetFileName(p).EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // pathlib orders Windows paths by their lower-cased text, and every stored bundle was written on Windows.
        names.Sort((a, b) => string.CompareOrdinal(ModList.PyLower(Path.GetFileName(a)), ModList.PyLower(Path.GetFileName(b))));
        return names;
    }

    /// <summary>The modifications an install's MetaMorpheus knew, loaded by mzLib as MetaMorpheus loads them (G26).</summary>
    /// <remarks>
    /// <para>MetaMorpheus <c>GlobalVariables.LoadModifications</c> (at <c>6e152da70</c>), with this build's mzLib:
    /// every <c>Mods/*.txt</c> but <c>glyco.txt</c> and the RNA files (<c>ModificationLoader.ReadModsFromFile</c>), then
    /// <c>Data/ptmlist.txt</c> (<c>Loaders.LoadUniprot</c>, with the formal charges of <c>Data/PSI-MOD.obo.xml</c>),
    /// then <c>Data/unimod.xml</c> (<c>Loaders.LoadUnimod</c>). As <c>AddMods</c> keeps it, one modification per
    /// <c>IdWithMotif</c>: the first loaded. MetaMorpheus writes a modification into a full sequence as
    /// <c>[ModificationType:IdWithMotif]</c> of the one it kept, so <see cref="Lookup"/> answers that exact token and
    /// nothing else.</para>
    /// <para>The accession is the entry's <c>Unimod</c> database reference (a <c>DR   Unimod;</c> line, or the Unimod
    /// record itself), and the mass is mzLib's (<c>ModificationLoader.ReadMod</c>): an <c>MM</c> line if there is one,
    /// else the <c>CF</c> formula's mass, and for a <c>ptmlist.txt</c> entry with an <c>MM</c> line, less one proton per
    /// unit of its PSI-MOD formal charge. The file reader took UniProt's <c>MM</c> as written, so a fixed charge counted
    /// one proton (N6,N6,N6-trimethyllysine 43.0542 against the 42.0470 searched, which is Unimod:37's C3H6). The
    /// <c>Mods</c> files are read with no charges, as MetaMorpheus reads them.</para>
    /// <para><b>A mass the search did not use is withheld.</b> mzLib read a NEGATIVE PSI-MOD formal charge as positive
    /// until 1.0.592 (#1299, 2026-09-17), so an install whose own mzLib is older (MetaMorpheus 1.1.11 ships 1.0.591)
    /// searched such a modification two protons per charge away from the mass this build computes. Its entry keeps any
    /// accession and has no mass, so a token without an accession stays an <c>[Info:...]</c> tag rather than a mass
    /// tag that would describe some other search (<see cref="MassesWithheld"/>). An install whose mzLib version cannot
    /// be read is treated as older. Measured 2026-10-19: no entry of aging's 1.1.11 or 1.1.12 installs carries a
    /// negative charge, and every charged one that reaches the store is +1, so no stored token is affected.</para>
    /// <para>Two differences from MetaMorpheus, both deliberate: the <c>Mods</c> files are read in the order every
    /// stored bundle used (<see cref="ModFiles"/>), not the file system's; and <c>glyco.txt</c> and the RNA files are
    /// recognised by file name, where MetaMorpheus tests the whole path. A file the install lacks is skipped, so a
    /// missing install gives an empty registry, as <see cref="FromMetaMorpheus"/> does.</para>
    /// </remarks>
    public static ModRegistry FromMzLib(string installDir)
    {
        var registry = new ModRegistry { ByToken = true, Loader = $"mzLib {MzLibVersion}" };
        var kept = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(IEnumerable<Omics.Modifications.Modification> mods, string source, Func<Omics.Modifications.Modification, string?>? withhold = null)
        {
            foreach (var mod in mods)
            {
                if (string.IsNullOrEmpty(mod.ModificationType) || string.IsNullOrEmpty(mod.IdWithMotif)) continue;
                if (!kept.Add(mod.IdWithMotif)) continue;  // every duplicate branch of MetaMorpheus's AddMods skips it
                long? unimod = mod.DatabaseReference is not null && mod.DatabaseReference.TryGetValue("Unimod", out var ids)
                    && ids.Count > 0 && long.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
                var token = $"{mod.ModificationType}:{mod.IdWithMotif}";
                var why = withhold?.Invoke(mod);
                if (why is not null) registry.MassesWithheld[token] = why;
                var entry = new ModEntry(mod.IdWithMotif, ModList.Targets(mod.Target?.ToString() ?? ""), unimod,
                    why is null ? mod.MonoisotopicMass : null, source)
                {
                    Category = mod.ModificationType,
                };
                registry.Add(entry);
                registry._byToken[token] = entry;
            }
        }
        foreach (var path in ModFiles(installDir))
        {
            var name = Path.GetFileName(path);
            if (name.Contains("glyco.txt", StringComparison.Ordinal) || name.Contains("Rna", StringComparison.Ordinal)) continue;
            AddAll(Omics.Modifications.IO.ModificationLoader.ReadModsFromFile(path, out _), name);
        }
        var data = Path.Combine(installDir, "Data");
        var ptmlist = Path.Combine(data, "ptmlist.txt");
        var psimod = Path.Combine(data, "PSI-MOD.obo.xml");
        if (File.Exists(ptmlist))
        {
            // MetaMorpheus always ships PSI-MOD; an install without it cannot say which UniProt masses carry a formal
            // charge, so any entry citing a PSI-MOD term keeps its accession and loses its mass.
            var hasPsiMod = File.Exists(psimod);
            var charges = hasPsiMod
                ? UsefulProteomicsDatabases.Loaders.GetFormalChargesDictionary(UsefulProteomicsDatabases.Loaders.LoadPsiMod(psimod))
                : new Dictionary<string, int>();
            var installMzLib = InstallMzLib(installDir);
            var signRead = installMzLib is not null && installMzLib >= FormalChargeSignFixed;
            string? Withhold(Omics.Modifications.Modification mod)
            {
                if (mod.DatabaseReference is null) return null;
                if (!hasPsiMod)
                    return mod.DatabaseReference.TryGetValue("PSI-MOD", out var terms) && terms.Count > 0
                        ? $"PSI-MOD; {terms[0]} may carry a formal charge, and the install has no Data/PSI-MOD.obo.xml to read it from"
                        : null;
                if (signRead) return null;
                var negative = mod.DatabaseReference.SelectMany(b => b.Value.Select(c => b.Key + "; " + c))
                    .FirstOrDefault(key => charges.TryGetValue(key, out var charge) && charge < 0);
                return negative is null ? null
                    : $"{negative} has formal charge {charges[negative]}, which the install's mzLib "
                      + $"{installMzLib?.ToString() ?? "(version unreadable)"} read as positive (mzLib #1299, fixed in {FormalChargeSignFixed})";
            }
            AddAll(UsefulProteomicsDatabases.Loaders.LoadUniprot(ptmlist, charges).ToList(), "ptmlist.txt", Withhold);
        }
        var unimodXml = Path.Combine(data, "unimod.xml");
        if (File.Exists(unimodXml))
            AddAll(UsefulProteomicsDatabases.Loaders.LoadUnimod(unimodXml).ToList(), "unimod.xml");
        return registry;
    }

    /// <summary>The first mzLib that keeps the sign of a PSI-MOD formal charge (#1299).</summary>
    private static readonly Version FormalChargeSignFixed = new(1, 0, 592);

    /// <summary>The mzLib an install's MetaMorpheus was built with, from its own <c>UsefulProteomicsDatabases.dll</c>,
    /// or null when that cannot be read.</summary>
    private static Version? InstallMzLib(string installDir)
    {
        var dll = Path.Combine(installDir, "UsefulProteomicsDatabases.dll");
        if (!File.Exists(dll)) return null;
        var text = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>The mzLib this build loads modifications with.</summary>
    private static string MzLibVersion =>
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(UsefulProteomicsDatabases.Loaders).Assembly)?.InformationalVersion ?? "unknown").Split('+')[0];

    /// <summary>Find the entry for a MetaMorpheus modification name.</summary>
    /// <remarks><paramref name="name"/> is the part inside the bracket after the category, e.g.
    /// <c>Hydroxylation on K</c> or <c>N6-acetyllysine on K</c>. Some files spell the target into the ID and some
    /// do not, so the full name is tried first and then the name with <c> on &lt;residue&gt;</c> stripped.
    /// An empty <paramref name="residue"/> counts as none, as Python's falsy test does.
    /// <para><b>Under <see cref="IngestRules.Current"/> nothing is guessed</b> (G83 item 1). The residue is read
    /// the way a <c>TG</c> line is (<see cref="ModList.Targets"/>: <c>Nxs</c> is <c>N</c>, <c>Methionine</c> is
    /// <c>M</c>), and an entry answers only if it targets that residue, or targets <c>X</c> (any residue, as an
    /// mzLib motif reads it). With no residue at all, the candidates must agree on accession and mass. Anything
    /// else is null, so the caller reports the name unresolved. Python 0.32.0 fell back to the first candidate
    /// carrying a UNIMOD accession, so <c>N6-acetyllysine on S</c> resolved to <c>UNIMOD:1</c>.</para></remarks>
    /// <param name="category">The token's category (<c>Common Fixed</c>). A registry <see cref="FromMzLib"/> built answers
    /// <c>category:name</c> exactly, and nothing without a category; a file-read one ignores it.</param>
    public ModEntry? Lookup(string name, string? residue = null, IngestRules rules = IngestRules.Current, string? category = null)
    {
        if (ByToken && rules == IngestRules.Current)
            return string.IsNullOrEmpty(category) ? null : _byToken.GetValueOrDefault($"{category}:{name}");
        if (rules == IngestRules.Current) return StrictLookup(name, residue);
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

    /// <summary><see cref="Lookup"/> under <see cref="IngestRules.Current"/>: the candidates as 0.32.0 found them,
    /// and an answer only where the residue (or, with none, the candidates' agreement) decides it.</summary>
    private ModEntry? StrictLookup(string name, string? residue)
    {
        _byName.TryGetValue(ModList.PyCaseFold(name), out var candidates);
        var cut = name.LastIndexOf(" on ", StringComparison.Ordinal);
        if (candidates is null && cut >= 0)
            _byName.TryGetValue(ModList.PyCaseFold(name[..cut]), out candidates);
        if (candidates is null) return null;
        if (string.IsNullOrEmpty(residue) && cut >= 0) residue = name[(cut + 4)..];
        IReadOnlySet<string> wanted = string.IsNullOrEmpty(residue) ? new HashSet<string>() : ModList.Targets(residue);
        if (wanted.Count > 0)
            return candidates.FirstOrDefault(e => e.Targets.Overlaps(wanted))
                ?? candidates.FirstOrDefault(e => e.Targets.Contains("X"));
        // No residue to check: an answer only when every candidate gives the same one.
        return candidates.Select(e => (e.UnimodCurie, e.MonoisotopicMass)).Distinct().Count() == 1 ? candidates[0] : null;
    }
}
