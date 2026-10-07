using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Readers;

namespace PXReprise.Search;

/// <summary>
/// The label-free experimental design a search runs with (G15): <c>ExperimentalDesign.tsv</c> beside the spectra, from an
/// SDRF. The question's curated SDRF comes first (<c>[designs] dir</c>, one <c>&lt;PXD&gt;.sdrf.tsv</c> per deposit), then
/// the deposit's own. mzLib's <see cref="SdrfLabelFreeDesign"/> checks each the way MetaMorpheus reads it and refuses
/// rather than repairs, because MetaMorpheus skips quantification on a bad design with only a warning. Deposited SDRFs
/// rarely say more than the tissue: on the aging corpus 22 of 23 were refused (2026-10-04), so the curated one counts.
/// With neither usable, PXReprise writes its own default SDRF and the design comes from that (D31): no search is
/// design-less.
/// </summary>
public static class DesignStage
{
    public const string FileName = "ExperimentalDesign.tsv";

    /// <summary>
    /// Writes the design, or removes any stale one, and returns its provenance record (<c>written</c> says which).
    /// </summary>
    /// <param name="searchedFiles">The spectra files the search reads; rows for any other file are dropped.</param>
    /// <param name="curated">The question's SDRF for this deposit; ignored when the file does not exist.</param>
    /// <param name="conditionColumns">The factor columns a curated design's condition is built from; null for its only one.</param>
    /// <param name="organism">The profile's organism key, written into a default SDRF.</param>
    /// <param name="accession">The deposit's accession, naming a default SDRF.</param>
    /// <exception cref="SearchSetupException">A curated design MetaMorpheus would not accept. It is fixed, not searched around.</exception>
    public static JsonObject Prepare(string spectraDir, IReadOnlyList<string> searchedFiles, string? curated,
        IReadOnlyList<string>? conditionColumns, string? organism = null, string? accession = null)
    {
        string target = Path.Combine(spectraDir, FileName);
        File.Delete(target);   // never one left by an earlier attempt or by hand
        if (curated is not null && File.Exists(curated))
        {
            var d = Read(curated, searchedFiles, conditionColumns, out string? error);
            if (d is null || !d.IsValid)
                throw new SearchSetupException($"the question's design {curated} is refused: {error ?? string.Join(" | ", d!.Refusals.Take(5))}");
            d.WriteExperimentalDesignTsv(target);
            return Record("question", curated, d);
        }
        var refused = new JsonArray();
        string metadata = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "metadata");
        var deposited = Directory.Exists(metadata)
            ? Directory.EnumerateFiles(metadata, "*.tsv").Where(f => Path.GetFileName(f).Contains("sdrf", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToList()
            : new List<string>();
        foreach (string s in deposited)
        {
            var d = Read(s, searchedFiles, null, out string? error);
            if (d is { IsValid: true })
            {
                d.WriteExperimentalDesignTsv(target);
                var rec = Record("deposit", s, d);
                if (refused.Count > 0) rec["deposit_sdrfs_refused"] = refused;
                return rec;
            }
            refused.Add(new JsonObject
            {
                ["sdrf"] = Path.GetFileName(s),
                ["refusals"] = new JsonArray((error is null ? d!.Refusals.Take(5) : new[] { error }).Select(x => (JsonNode?)x).ToArray()),
            });
        }
        // D31 (the user, 2026-10-07): every searched set of files gets an SDRF and a design. With neither SDRF usable, ours
        // says only what design-less quantification assumes, on the record: one condition, each file its own sample.
        string why = deposited.Count == 0 ? "no curated design and no deposited SDRF" : "no curated design; every deposited SDRF was refused";
        string fallback = WriteDefaultSdrf(spectraDir, searchedFiles, organism, accession);
        var made = Read(fallback, searchedFiles, null, out string? fallbackError);
        if (made is null || !made.IsValid)
            throw new SearchSetupException($"PXReprise's default SDRF {fallback} is refused: {fallbackError ?? string.Join(" | ", made!.Refusals.Take(5))}");
        made.WriteExperimentalDesignTsv(target);
        var record = Record(DefaultSource, fallback, made);
        record["reason"] = why;
        if (refused.Count > 0) record["deposit_sdrfs_refused"] = refused;
        return record;
    }

    /// <summary>The provenance <c>source</c> of a design from <see cref="WriteDefaultSdrf"/>.</summary>
    public const string DefaultSource = "pxreprise_default";

    /// <summary>The condition every file of a default design is in.</summary>
    public const string DefaultCondition = "all";

    /// <summary>
    /// Writes PXReprise's own SDRF for the searched files (D31): one row per file, all in <see cref="DefaultCondition"/>,
    /// each file its own biological replicate, fraction 1, technical replicate 1. It sits in <c>pxreprise_sdrf</c> beside
    /// the fetch's <c>metadata</c>, never in it, so a later attempt does not read it back as the deposit's. A fractionated
    /// deposit is wrong under it (each fraction a sample) until a curated SDRF or sdrf's drafter replaces it.
    /// </summary>
    private static string WriteDefaultSdrf(string spectraDir, IReadOnlyList<string> searchedFiles, string? organism, string? accession)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spectraDir))!, "pxreprise_sdrf");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{(string.IsNullOrEmpty(accession) ? "deposit" : accession)}.{DefaultSource}.sdrf.tsv");
        var lines = new List<string>
        {
            "source name\tcharacteristics[organism]\tcharacteristics[biological replicate]\tassay name\tcomment[label]\t"
            + "comment[data file]\tcomment[technical replicate]\tcomment[fraction identifier]\tfactor value[group]",
        };
        int i = 0;
        foreach (string f in searchedFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            i++;
            lines.Add($"sample {i}\t{OrganismName(organism)}\t{i}\trun {i}\tlabel free sample\t{f}\t1\t1\t{DefaultCondition}");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The SDRF spelling of a profile's organism key; anything else is written as given, or "not available".</summary>
    private static string OrganismName(string? organism) => organism?.ToLowerInvariant() switch
    {
        null or "" => "not available",
        "human" => "homo sapiens",
        "mouse" => "mus musculus",
        "rat" => "rattus norvegicus",
        var o => o,
    };

    private static SdrfLabelFreeDesign? Read(string sdrf, IReadOnlyList<string> searchedFiles, IReadOnlyList<string>? conditionColumns, out string? error)
    {
        error = null;
        try
        {
            return SdrfLabelFreeDesign.Read(sdrf, new SdrfLabelFreeDesignOptions { SearchedFiles = searchedFiles.ToList(), ConditionColumns = conditionColumns });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A file that is not an SDRF at all: as much a refusal as a bad one, and recorded the same way.
            error = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static JsonObject Record(string source, string sdrf, SdrfLabelFreeDesign d) => new()
    {
        ["source"] = source, ["written"] = true, ["sdrf"] = sdrf, ["sdrf_sha256"] = Sha(sdrf),
        ["file_key_column"] = d.FileKeyColumn,
        ["condition_columns"] = new JsonArray(d.ConditionColumns.Select(c => (JsonNode?)c).ToArray()),
        ["conditions"] = new JsonArray(d.Files.Select(f => f.Condition).Distinct().Order(StringComparer.Ordinal).Select(c => (JsonNode?)c).ToArray()),
        ["files"] = d.Files.Count,
        ["notes"] = new JsonArray(d.Notes.Select(n => (JsonNode?)n).ToArray()),
        // QuantProject 012 (PXR-U1): mzLib's whole account, kept per deposit; replicate numbers change when #1426 ships.
        ["report"] = d.Report(),
    };

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
