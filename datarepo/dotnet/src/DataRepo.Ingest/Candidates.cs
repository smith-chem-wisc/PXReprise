using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataRepo.Bundle;

namespace DataRepo.Ingest;

/// <summary>One row of the discovery census: a screened accession and its inclusion verdict (<c>dataset_candidates</c>).</summary>
public sealed record CandidateRow(string CensusVersion, string Accession, bool Included, string? ExclusionReason, string? DefinitionId);

/// <summary>The producing instance's discovery census, read from the TSV its manifest names as <c>candidates</c>.</summary>
/// <remarks>
/// <para>An instance-level fact, not a dataset's: every accession the producer SCREENED, the ones it reanalyzed and the
/// ones it did not, with the gate that excluded each. It answers "why is PXDn not here?", which no bundle can, since
/// a dataset that was never ingested has no bundle. So it is neither a bundle nor an ingest: <c>datarepo build</c>
/// reads it from the manifest it is given, as it does the non-content fields (G84, G85), loads it into
/// <c>dataset_candidates</c> and hashes the file's sha256 into the catalog id.</para>
/// <para>The format is a tab-separated file with a header row naming the schema's columns
/// (<see cref="Columns"/>). The required ones must be present and every row must fill them; the optional ones may be
/// left out of the header, and an empty cell is NULL. <c>included</c> is <c>true</c> or <c>false</c>, nothing else.
/// An accession listed twice is refused (two verdicts for one accession is the producer's contradiction to resolve,
/// never reduced to one here). Cells are read verbatim: no quoting, no trimming, so a reason reaches the catalog
/// exactly as written. A UTF-8 byte-order mark is ignored and CRLF reads as LF; the sha256 is of the bytes as
/// stored.</para>
/// </remarks>
public sealed record CandidateCensus(string Path, string Sha256, IReadOnlyList<CandidateRow> Rows)
{
    /// <summary>The catalog table the census fills.</summary>
    public const string Table = "dataset_candidates";

    /// <summary>The columns a census may carry, in schema order: the schema's own, so the file and the table cannot
    /// drift apart.</summary>
    public static IReadOnlyList<string> Columns => Tables.ByName[Table].Columns.Select(c => c.Name).ToList();

    /// <summary>The columns a census must carry and fill: the schema's <c>required</c> ones.</summary>
    public static IReadOnlyList<string> Required => Tables.ByName[Table].Columns.Where(c => !c.Nullable).Select(c => c.Name).ToList();

    /// <summary><c>DatasetCandidate.accession</c>'s pattern in schema/datarepo.yaml (a test holds them together).</summary>
    public const string AccessionPattern = "^(PXD|MSV|JPST|IPX)[0-9]+$";

    private static readonly Regex Accession = new(AccessionPattern, RegexOptions.CultureInvariant);

    /// <summary>Reads and checks a census file.</summary>
    /// <exception cref="ManifestException">The file is missing, is not UTF-8, has no header, names a column the schema
    /// does not have (or one twice), lacks a required column, or has a row that is the wrong width, leaves a required
    /// cell empty, gives an accession the schema's pattern refuses, says other than <c>true</c>/<c>false</c> for
    /// <c>included</c>, or repeats an accession.</exception>
    public static CandidateCensus Load(string path)
    {
        if (!File.Exists(path)) throw new ManifestException($"no candidates census at {path}");
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ManifestException($"{path} is not UTF-8 text");
        }
        if (text.StartsWith('﻿')) text = text[1..];

        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var header = -1;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Length > 0) { header = i; break; }
        if (header < 0)
            throw new ManifestException($"{path} is empty; a census starts with a header row naming its columns: {string.Join(", ", Columns)}");

        var names = lines[header].Split('\t');
        var known = Columns;
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 0; c < names.Length; c++)
        {
            if (!known.Contains(names[c]))
                throw new ManifestException(
                    $"{path}:{header + 1}: column {PyFormat.Repr(names[c])} is not a dataset_candidates column. The columns are {string.Join(", ", known)}.");
            if (!at.TryAdd(names[c], c))
                throw new ManifestException($"{path}:{header + 1}: column {PyFormat.Repr(names[c])} appears twice");
        }
        var missing = Required.Where(r => !at.ContainsKey(r)).ToList();
        if (missing.Count > 0)
            throw new ManifestException($"{path}:{header + 1}: the header lacks the required column(s) {string.Join(", ", missing)}");

        var rows = new List<CandidateRow>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = header + 1; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;
            var where = $"{path}:{i + 1}";
            var cells = lines[i].Split('\t');
            if (cells.Length != names.Length)
                throw new ManifestException($"{where}: {cells.Length} cell(s) where the header names {names.Length}");
            string? Cell(string column) => at.TryGetValue(column, out var c) && cells[c].Length > 0 ? cells[c] : null;
            foreach (var required in Required)
                if (Cell(required) is null)
                    throw new ManifestException($"{where}: {required} is empty, and every row must give one");
            var accession = Cell("accession")!;
            if (!Accession.IsMatch(accession))
                throw new ManifestException($"{where}: accession {PyFormat.Repr(accession)} is not a ProteomeXchange accession ({AccessionPattern})");
            var included = Cell("included")! switch
            {
                "true" => true,
                "false" => false,
                var other => throw new ManifestException($"{where}: included is {PyFormat.Repr(other)}; it must be true or false"),
            };
            if (seen.TryGetValue(accession, out var earlier))
                throw new ManifestException(
                    $"{where}: {accession} is listed twice (also on line {earlier}). One accession has one verdict.");
            seen[accession] = i + 1;
            rows.Add(new CandidateRow(Cell("census_version")!, accession, included, Cell("exclusion_reason"), Cell("definition_id")));
        }
        return new CandidateCensus(path, sha, rows);
    }
}
