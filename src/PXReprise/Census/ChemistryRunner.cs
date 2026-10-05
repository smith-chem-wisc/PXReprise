using System.Text;
using PXReprise.Discovery;
using PXReprise.Provenance;
using Readers;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

public sealed record ChemistryRow(string Accession, DepositChemistry? Chemistry, string? SdrfFile, bool CuratedSdrf, string? Error)
{
    /// <summary>
    /// How this differs from what <c>label-free-dda@1</c> searched with (trypsin, fixed carbamidomethyl, label-free), or
    /// an empty list. The report D26 asks for before <c>label-free-dda@2</c> runs.
    /// </summary>
    public IReadOnlyList<string> DiffersFromV1()
    {
        if (Chemistry is not { } c) return Array.Empty<string>();
        var d = new List<string>();
        if (c.Protease.Value != "trypsin") d.Add($"protease {c.Protease.Value}");
        if (c.CysMods.Count != 1 || c.CysMods[0].Name != "Carbamidomethyl" || !c.CysMods[0].Fixed)
            d.Add("cysteine " + (c.CysMods.Count == 0 ? "unmodified" : string.Join(" + ", c.CysMods.Select(m => $"{m.Name} ({(m.Fixed ? "fixed" : "variable")})"))));
        if (c.Label.Value != "label_free") d.Add($"label {c.Label.Value}");
        return d;
    }
}

/// <summary>
/// <c>pxreprise chemistry</c>: each deposit's protease, alkylation and label (G19, D22 to D24), read from PRIDE's
/// project record, the deposit's SDRF and the question's curated SDRF; no spectra. Writes <c>chemistry.tsv</c>.
/// </summary>
public sealed class ChemistryRunner(IProjectSearch projects, IRankSource files)
{
    public async Task<IReadOnlyList<ChemistryRow>> RunAsync(IReadOnlyList<string> accessions, string? designsDir, string outDir, RunRecord record, CancellationToken ct)
    {
        string sdrfDir = Path.Combine(outDir, "sdrf");
        Directory.CreateDirectory(sdrfDir);
        var rows = new ChemistryRow[accessions.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(accessions.Select(async (acc, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { rows[i] = await OneAsync(acc, designsDir, sdrfDir, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        string tsv = Path.Combine(outDir, "chemistry.tsv");
        WriteTsv(tsv, rows);
        record.Output(tsv);
        record.Note("Sources in D22's order: curated SDRF, deposited SDRF, PRIDE identifiedPTMStrings / quantificationMethods, protocol text, default. differs_from_v1 compares with label-free-dda@1: trypsin, fixed Carbamidomethyl on C, label-free.");
        foreach (var e in rows.Where(r => r.Error is not null)) record.Note($"{e.Accession}: {e.Error}");
        return rows;
    }

    private async Task<ChemistryRow> OneAsync(string acc, string? designsDir, string sdrfDir, CancellationToken ct)
    {
        try
        {
            var project = await projects.TryGetProjectAsync(acc, ct).ConfigureAwait(false);
            var listing = await files.FilesAsync(acc, ct).ConfigureAwait(false);
            var raws = listing.Where(f => f.FileName.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)).Select(f => f.FileName).ToList();
            SdrfDocument? curated = null, deposited = null;
            string? sdrfName = null;
            if (designsDir is not null && File.Exists(Path.Combine(designsDir, $"{acc}.sdrf.tsv")))
                curated = new SdrfDocument(Path.Combine(designsDir, $"{acc}.sdrf.tsv"));
            var sdrf = listing.FirstOrDefault(f => f.FileName.EndsWith(".sdrf.tsv", StringComparison.OrdinalIgnoreCase))
                       ?? listing.FirstOrDefault(f => f.FileName.Contains("sdrf", StringComparison.OrdinalIgnoreCase) && f.FileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase));
            if (sdrf is not null)
            {
                sdrfName = sdrf.FileName;
                string dir = Path.Combine(sdrfDir, acc);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, sdrf.FileName);
                if (!File.Exists(path)) path = await files.DownloadAsync(sdrf, dir, ct).ConfigureAwait(false);
                deposited = new SdrfDocument(path);
            }
            return new ChemistryRow(acc, ChemistryDetector.Detect(project, curated, deposited, raws), sdrfName, curated is not null,
                project is null ? "PRIDE has no project record; defaults and SDRF only" : null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
        {
            return new ChemistryRow(acc, null, null, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static void WriteTsv(string path, IEnumerable<ChemistryRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', "accession", "differs_from_v1", "protease", "protease_source", "protease_evidence", "protease_per_file",
            "alkylation", "cys_mods", "alkylation_source", "alkylation_evidence", "label", "label_source", "label_evidence",
            "park", "any_guessed", "sdrf_file", "curated_sdrf", "error")).Append('\n');
        foreach (var r in rows)
        {
            var c = r.Chemistry;
            sb.Append(string.Join('\t', new[]
            {
                r.Accession, string.Join("; ", r.DiffersFromV1()),
                c?.Protease.Value ?? "", c?.Protease.Source ?? "", c?.Protease.Evidence ?? "",
                c?.ProteasePerFile is { } m ? string.Join("; ", m.Select(kv => $"{kv.Key}={kv.Value}")) : "",
                c?.Alkylation.Value ?? "", c is null ? "" : string.Join(" + ", c.CysMods.Select(x => $"{x.Name}/{x.Unimod}/{(x.Fixed ? "fixed" : "variable")}")),
                c?.Alkylation.Source ?? "", c?.Alkylation.Evidence ?? "", c?.Label.Value ?? "", c?.Label.Source ?? "", c?.Label.Evidence ?? "",
                c?.Park ?? "", c is null ? "" : c.AnyGuessed ? "yes" : "no", r.SdrfFile ?? "", r.CuratedSdrf ? "yes" : "no", r.Error ?? "",
            }.Select(s => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')))).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
