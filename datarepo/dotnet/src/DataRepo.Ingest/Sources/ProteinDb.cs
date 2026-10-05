using System.Text;
using System.Xml;
using DataRepo.Bundle;
using static DataRepo.Ingest.Sources.SourcesPy;

namespace DataRepo.Ingest.Sources;

/// <summary>Accession -> every distinct sequence the searched databases carry under it.</summary>
/// <remarks>A list rather than one string because an accession can appear in two searched databases -- a
/// human keratin is in both the proteome and the contaminant panel -- and the two need not agree.
/// A site is placed wherever the peptide occurs in any of them.</remarks>
public sealed class ProteinSequences
{
    public Dictionary<string, List<string>> ByAccession { get; } = new(StringComparer.Ordinal);

    /// <summary>What was read: path, sha256 and entry count per database, for bundle.json.</summary>
    public List<Dictionary<string, object?>> Files { get; } = [];

    /// <summary>Databases the provenance names that are not on disk, so their proteins cannot be aligned.</summary>
    public List<string> Missing { get; } = [];

    /// <summary>Accession -> which kinds of database it was read from: {true} contaminant only, {false}
    /// target only, {true, false} both (human keratins, albumin). See <see cref="DatabaseStatus"/>.</summary>
    public Dictionary<string, HashSet<bool>> ContaminantFrom { get; } = new(StringComparer.Ordinal);

    public void Add(string accession, string sequence)
    {
        if (!ByAccession.TryGetValue(accession, out var known)) ByAccession[accession] = known = [];
        if (!known.Contains(sequence)) known.Add(sequence);
    }

    private static readonly IReadOnlyList<string> None = [];

    public IReadOnlyList<string> Get(string accession) => ByAccession.TryGetValue(accession, out var known) ? known : None;

    /// <summary><c>contaminant</c>, <c>target</c> or <c>both</c>, from the databases the accession was read from.</summary>
    /// <returns>Null when no database on disk carried it (a missing database, or a name the engine made).</returns>
    public string? DatabaseStatus(string accession)
    {
        if (!ContaminantFrom.TryGetValue(accession, out var kinds) || kinds.Count == 0) return null;
        if (kinds.Count == 2) return "both";
        return kinds.Contains(true) ? "contaminant" : "target";
    }

    /// <summary>How many accessions carry a sequence (Python's <c>len()</c>).</summary>
    public int Count => ByAccession.Count;
}

/// <summary>The search's protein database -> one sequence per accession, for placing sites (DATAREPO-32).</summary>
/// <remarks>
/// <c>ptm_sites</c> needs each PTM's position in every protein a peptide maps to. The psmtsv cannot supply
/// it: MetaMorpheus writes <c>Start and End Residues In Full Sequence</c> <b>de-duplicated</b>, one span per
/// distinct position rather than one per accession, and one per occurrence when a peptide repeats
/// within a protein:
/// <code>
///     Accession                        Start and End Residues In Full Sequence
///     P60709|P63261|Q6S8J3             [216 to 238]|[916 to 938]
/// </code>
/// Beta- and gamma-actin share a span, so it is written once. Across aging's ten datasets 140,818
/// target PSMs at 1% FDR have a span count different from their accession count, and 9,093 have MORE
/// spans than accessions (aging 043). No pairing of the two cells is right in general, including the
/// case where the counts happen to agree. So the position comes from the sequence: find the peptide
/// in each member protein, which is what MetaMorpheus's own occupancy code does.
///
/// Which databases were searched is read from the search provenance's <c>inputs</c>, the same list
/// <see cref="SearchParams.SearchedDatabase"/> reads, and each file's sha256 is checked against the one the
/// provenance recorded. A database edited after the search would place sites against sequences the
/// engine never saw, so a mismatch stops the ingest rather than being worked around.
///
/// <b>Why this is not mzLib's <c>ProteinDbLoader</c>.</b> The project rule is to read producer formats with
/// mzLib, and the port measured it (2026-10-04, mzLib 1.0.593, on every database aging has searched): it
/// does not give the same <c>(accession, sequence)</c> pairs as the Python reader. With its default
/// <c>maxHeterozygousVariants = 4</c> it adds variant proteins (264 contaminant entries become 579); with 0 it
/// matches on 14 of 18 files but rewrites <c>B</c> and <c>Z</c> to <c>X</c>
/// (<c>SanitizeAminoAcidSequence</c>) in 6 mouse and 1 rat proteome proteins. So the Python's reading is ported
/// as it was: the accession and the sequence, nothing interpreted. Ported from <c>sources/protein_db.py</c>.
/// </remarks>
public static class ProteinDb
{
    private const string UniprotNs = "http://uniprot.org/uniprot";

    /// <summary>Every 1-based start of <paramref name="baseSequence"/> in <paramref name="proteinSequence"/>,
    /// overlapping ones included.</summary>
    public static List<int> Occurrences(string baseSequence, string proteinSequence)
    {
        var starts = new List<int>();
        var at = proteinSequence.IndexOf(baseSequence, StringComparison.Ordinal);
        while (at >= 0)
        {
            starts.Add(at + 1);
            // Python's str.find from at + 1; an empty needle also matches at len(haystack).
            if (at + 1 > proteinSequence.Length) break;
            at = proteinSequence.IndexOf(baseSequence, at + 1, StringComparison.Ordinal);
        }
        return starts;
    }

    /// <summary><c>(first accession, sequence)</c> per <c>&lt;entry&gt;</c>, streamed.</summary>
    /// <remarks>The first <c>&lt;accession&gt;</c> is the one mzLib's loader keys a protein on. Only the entry's
    /// direct <c>&lt;sequence&gt;</c> child is read; isoform sequences named inside comments are not entries.
    /// Text is an element's leading text only, as lxml's <c>.text</c>: it stops at the first child element,
    /// comment or processing instruction.</remarks>
    internal static IEnumerable<(string Accession, string Sequence)> IterUniprotXml(string path, IngestRules rules = IngestRules.Current)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry" || reader.NamespaceURI != UniprotNs)
                continue;
            if (reader.IsEmptyElement) continue;
            var depth = reader.Depth;
            string? accession = null, sequence = null;
            bool sawAccession = false, sawSequence = false;
            while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1 || reader.NamespaceURI != UniprotNs)
                    continue;
                if (reader.LocalName == "accession" && !sawAccession)
                {
                    sawAccession = true;
                    accession = LeadingText(reader) ?? "";
                }
                else if (reader.LocalName == "sequence" && !sawSequence)
                {
                    sawSequence = true;
                    sequence = LeadingText(reader);
                }
            }
            if (!string.IsNullOrEmpty(accession) && !string.IsNullOrEmpty(sequence))
                yield return (Strip(accession), AsSearched(string.Concat(SplitWhitespace(sequence)), rules));
        }
    }

    /// <summary>A database sequence as the search saw it.</summary>
    /// <remarks>Under <see cref="IngestRules.Current"/>, mzLib's own <c>ProteinDbLoader.SanitizeAminoAcidSequence</c>
    /// (<c>'X'</c>), which both of its loaders apply to every sequence before MetaMorpheus digests it: <c>B</c>,
    /// <c>Z</c>, <c>J</c>, non-ASCII and any other letter that is not a residue become <c>X</c>. So a peptide
    /// the engine matched across a <c>B</c> reads <c>X</c> there, and it is found in the sequence it was searched
    /// against. Python 0.32.0 kept the letters (upper-cased), and such peptides could not be placed (G83 item 10:
    /// 6 mouse and 1 rat proteome proteins).</remarks>
    private static string AsSearched(string sequence, IngestRules rules) =>
        rules == IngestRules.Current
            ? UsefulProteomicsDatabases.ProteinDbLoader.SanitizeAminoAcidSequence(sequence, 'X')
            : sequence.ToUpperInvariant();

    /// <summary>lxml's <c>.text</c> of the element the reader is on: null for an empty element or one whose
    /// first node is not text. Leaves the reader inside the element (or on it, if empty).</summary>
    private static string? LeadingText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        var depth = reader.Depth;
        StringBuilder? text = null;
        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                (text ??= new StringBuilder()).Append(reader.Value);
                continue;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return text?.ToString();
            // A child element, comment or processing instruction ends lxml's .text. Skip to this element's end
            // so the caller's walk of direct children stays in step.
            var result = text?.ToString();
            while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) && reader.Read()) { }
            return result;
        }
        return text?.ToString();
    }

    /// <summary>UniProt <c>sp|P12345|NAME_HUMAN ...</c> -> <c>P12345</c>; anything else -> its first token.</summary>
    private static string FastaAccession(string header)
    {
        var tokens = SplitWhitespace(header[1..]);
        var token = tokens.Count > 0 ? tokens[0] : "";
        var parts = token.Split('|');
        return parts.Length >= 3 && (parts[0] == "sp" || parts[0] == "tr") ? parts[1] : token;
    }

    /// <remarks>Read as Python's text mode reads it: UTF-8 with undecodable bytes replaced and universal newlines.
    /// Under <see cref="IngestRules.Current"/> a leading byte-order mark is dropped (<c>utf-8-sig</c>); Python
    /// 0.32.0 read <c>utf-8</c> and kept it as a character, so the first header did not start with <c>&gt;</c> and
    /// the file's first protein was lost (G83 item 17).</remarks>
    internal static IEnumerable<(string Accession, string Sequence)> IterFasta(string path, IngestRules rules = IngestRules.Current)
    {
        string? accession = null;
        var chunks = new List<string>();
        using var handle = new StreamReader(path, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false);
        string? raw;
        var first = true;
        while ((raw = handle.ReadLine()) is not null)
        {
            if (first && rules == IngestRules.Current && raw.StartsWith('﻿')) raw = raw[1..];
            first = false;
            var line = Strip(raw);
            if (line.StartsWith('>'))
            {
                if (!string.IsNullOrEmpty(accession) && chunks.Count > 0)
                    yield return (accession, AsSearched(string.Concat(chunks), rules));
                accession = FastaAccession(line);
                chunks = [];
            }
            else if (line.Length > 0)
                chunks.Add(line);
        }
        if (!string.IsNullOrEmpty(accession) && chunks.Count > 0)
            yield return (accession, AsSearched(string.Concat(chunks), rules));
    }

    /// <summary>MetaMorpheus's own rule for which database is a contaminant panel.</summary>
    /// <remarks>The command line marks a database file as contaminant when its path contains "contaminant" or
    /// "CRAP", case-insensitively (MetaMorpheus <c>CMD/Program.cs:261, 368-374</c> at <c>6e152da70</c>; the GUI
    /// uses the same rule). It is a property of the FILE, never of an accession.</remarks>
    public static bool IsContaminantDatabase(string path)
    {
        var text = PathStr(path).ToLowerInvariant();
        return text.Contains("contaminant") || text.Contains("crap");
    }

    /// <summary>Add every entry of one UniProt XML or FASTA file.</summary>
    /// <returns>How many entries were read.</returns>
    /// <exception cref="IngestException">Neither <c>.xml</c> nor <c>.fasta</c>/<c>.fa</c>.</exception>
    public static int ReadDatabase(string path, ProteinSequences into, IngestRules rules = IngestRules.Current)
    {
        var name = PathName(path).ToLowerInvariant();
        IEnumerable<(string Accession, string Sequence)> entries;
        if (name.EndsWith(".xml", StringComparison.Ordinal))
            entries = IterUniprotXml(path, rules);
        else if (name.EndsWith(".fasta", StringComparison.Ordinal) || name.EndsWith(".fa", StringComparison.Ordinal))
            entries = IterFasta(path, rules);
        else
            // A gzipped database is legal input to MetaMorpheus but none has been searched yet; refuse
            // rather than silently place nothing.
            throw new IngestException($"cannot read protein database {PathStr(path)}: only .xml and .fasta are supported");
        var contaminant = IsContaminantDatabase(path);
        var count = 0;
        foreach (var (accession, sequence) in entries)
        {
            into.Add(accession, sequence);
            if (!into.ContaminantFrom.TryGetValue(accession, out var kinds)) into.ContaminantFrom[accession] = kinds = [];
            kinds.Add(contaminant);
            count++;
        }
        return count;
    }

    /// <summary>Every protein database the search provenance lists as an input, with its recorded sha256.</summary>
    /// <returns>Paths as Python's <c>str(Path)</c> writes them on this platform, relative ones taken against
    /// <paramref name="workRoot"/>.</returns>
    public static List<(string Path, object? Sha256)> SearchedDatabases(IReadOnlyDictionary<string, object?> provenance, string workRoot)
    {
        var output = new List<(string, object?)>();
        foreach (var item in ListOrEmpty(Get(provenance, "inputs"), "inputs"))
        {
            var entry = item as IReadOnlyDictionary<string, object?> ?? EmptyDict;
            var raw = Str(Get(entry, "path", ""));
            if (SearchParams.EndsWithDatabaseSuffix(raw))
                output.Add((JoinPath(workRoot, raw), Get(entry, "sha256")));
        }
        return output;
    }

    /// <summary>Read every searched database that is on disk, after checking it is the file that was searched.</summary>
    /// <exception cref="IngestException">A database on disk does not match the sha256 the search recorded for it.</exception>
    public static ProteinSequences Load(IReadOnlyDictionary<string, object?> provenance, string workRoot, IngestRules rules = IngestRules.Current)
    {
        var sequences = new ProteinSequences();
        foreach (var (path, recorded) in SearchedDatabases(provenance, workRoot))
        {
            if (!File.Exists(path))
            {
                sequences.Missing.Add(path);
                continue;
            }
            var actual = BundleWriter.Sha256File(path);
            if (Truthy(recorded) && !Equals(recorded, actual))
                throw new IngestException(
                    $"{path} is not the database that was searched: the search provenance recorded " +
                    $"sha256 {Str(recorded)} and the file on disk is {actual}. Sites would be placed against " +
                    "sequences the engine never saw.");
            var count = ReadDatabase(path, sequences, rules);
            sequences.Files.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["sha256"] = actual,
                ["entries"] = (long)count,
            });
        }
        return sequences;
    }
}
