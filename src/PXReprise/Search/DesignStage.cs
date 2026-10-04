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
    /// <exception cref="SearchSetupException">A curated design MetaMorpheus would not accept. It is fixed, not searched around.</exception>
    public static JsonObject Prepare(string spectraDir, IReadOnlyList<string> searchedFiles, string? curated,
        IReadOnlyList<string>? conditionColumns)
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
        return new JsonObject
        {
            ["source"] = "none", ["written"] = false,
            ["reason"] = deposited.Count == 0 ? "no curated design and no deposited SDRF" : "no curated design; every deposited SDRF was refused",
            ["deposit_sdrfs_refused"] = refused,
        };
    }

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
    };

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
