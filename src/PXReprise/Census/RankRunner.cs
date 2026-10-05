using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PXReprise.Provenance;
using Readers;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

/// <summary>The PRIDE calls ranking needs; <see cref="PrideArchiveClient"/> in production, a fake in tests.</summary>
public interface IRankSource
{
    Task<IReadOnlyList<PrideArchiveFile>> FilesAsync(string accession, CancellationToken ct);
    Task<string> DownloadAsync(PrideArchiveFile file, string directory, CancellationToken ct);
}

public sealed class PrideRankSource(PrideArchiveClient client) : IRankSource
{
    public async Task<IReadOnlyList<PrideArchiveFile>> FilesAsync(string accession, CancellationToken ct) =>
        await client.GetProjectFilesAsync(accession, cancellationToken: ct).ConfigureAwait(false);

    public Task<string> DownloadAsync(PrideArchiveFile file, string directory, CancellationToken ct) =>
        client.DownloadFileAsync(file, directory, cancellationToken: ct);
}

public sealed record RankRow(
    string Accession, int Tier, DesignGuess Design, int RawFiles, double TotalGb, double MedianMb, string? SdrfFile,
    IReadOnlyDictionary<string, string> Census, string? Error);

/// <summary>
/// <c>pxreprise rank</c>: orders a census's queueable deposits by design, without downloading spectra. Per deposit, one
/// file listing and, when the deposit has one, its SDRF (kilobytes). The design is read from the SDRF when a factor value
/// varies, else guessed from the raw-file names; both are labelled. Writes <c>ranked.tsv</c>, every signal a column.
/// </summary>
public sealed class RankRunner(IRankSource source)
{
    public async Task<IReadOnlyList<RankRow>> RunAsync(string censusDir, IReadOnlyCollection<string>? only, string outDir,
        double largeGb, int maxFiles, RunRecord record, CancellationToken ct)
    {
        string queueFile = Path.Combine(censusDir, "queue.json"), censusFile = Path.Combine(censusDir, "census.tsv");
        if (!File.Exists(queueFile) || !File.Exists(censusFile)) throw new Cli.UsageException($"{censusDir} holds no census (queue.json and census.tsv)");
        record.Input(queueFile);
        record.Input(censusFile);
        var census = ReadTsv(censusFile).ToDictionary(r => r["accession"], StringComparer.Ordinal);
        var accessions = JsonNode.Parse(File.ReadAllText(queueFile))!.AsArray().Select(x => x!["accession"]!.GetValue<string>())
            .Where(a => only is null || only.Contains(a)).ToList();
        string sdrfDir = Path.Combine(outDir, "sdrf");
        Directory.CreateDirectory(sdrfDir);

        var rows = new RankRow[accessions.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(accessions.Select(async (acc, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { rows[i] = await OneAsync(acc, census.GetValueOrDefault(acc) ?? new(), sdrfDir, largeGb, maxFiles, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var ordered = Ranking.Order(rows, r => r.Tier, r => r.RawFiles, r => r.TotalGb).ToList();
        string tsv = Path.Combine(outDir, "ranked.tsv");
        WriteTsv(tsv, ordered);
        record.Output(tsv);
        record.Note($"bigger is better (the user, 2026-10-05): tier 1 = at most {maxFiles} raw files and {largeGb} GB, more raw files first, then the smaller download; tier 2 = larger than either. The design columns are a guess, for information only.");
        foreach (var e in ordered.Where(r => r.Error is not null)) record.Note($"{e.Accession}: {e.Error}");
        return ordered;
    }

    private async Task<RankRow> OneAsync(string acc, Dictionary<string, string> census, string sdrfDir, double largeGb, int maxFiles, CancellationToken ct)
    {
        IReadOnlyList<PrideArchiveFile> files;
        try { files = await source.FilesAsync(acc, ct).ConfigureAwait(false); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException)
        {
            return new RankRow(acc, 2, DesignGuess.Unknown, 0, 0, 0, null, census, $"file listing failed: {e.Message}");
        }
        var raws = files.Where(f => f.FileName.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)).ToList();
        double totalGb = raws.Sum(f => (double)f.FileSizeBytes) / 1e9;
        var sizes = raws.Select(f => f.FileSizeBytes / 1e6).OrderBy(x => x).ToList();
        double medianMb = sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];

        DesignGuess? design = null;
        string? sdrfName = null, error = null;
        var sdrf = files.FirstOrDefault(f => f.FileName.EndsWith(".sdrf.tsv", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault(f => f.FileName.Contains("sdrf", StringComparison.OrdinalIgnoreCase) && f.FileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase));
        if (sdrf is not null)
        {
            sdrfName = sdrf.FileName;
            try
            {
                string dir = Path.Combine(sdrfDir, acc);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, sdrf.FileName);
                if (!File.Exists(path)) path = await source.DownloadAsync(sdrf, dir, ct).ConfigureAwait(false);
                design = Ranking.FromSdrf(new SdrfDocument(path));
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
            {
                error = $"SDRF {sdrf.FileName} unreadable ({e.GetType().Name}: {e.Message}); design from file names";
            }
        }
        design ??= Ranking.FromNames(raws.Select(f => f.FileName));
        return new RankRow(acc, Ranking.Tier(raws.Count, totalGb, maxFiles, largeGb), design, raws.Count, totalGb, medianMb, sdrfName, census, error);
    }

    private static IEnumerable<Dictionary<string, string>> ReadTsv(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split('\t');
        foreach (string l in lines.Skip(1).Where(l => l.Length > 0))
        {
            var cells = l.Split('\t');
            yield return header.Select((h, i) => (h, v: i < cells.Length ? cells[i] : "")).ToDictionary(x => x.h, x => x.v, StringComparer.Ordinal);
        }
    }

    private static void WriteTsv(string path, IReadOnlyList<RankRow> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', "rank", "accession", "tier", "raw_files", "total_gb", "guessed_power", "guessed_groups", "guessed_replicates_per_group", "design_source", "fractions",
            "median_mb", "sdrf_file", "reference_group", "keywords_hit", "organisms_of_record", "instruments", "title", "note")).Append('\n');
        int rank = 0;
        foreach (var r in rows)
        {
            string C(string k) => r.Census.GetValueOrDefault(k) ?? "";
            sb.Append(string.Join('\t', new[]
            {
                (++rank).ToString(inv), r.Accession, r.Tier.ToString(inv), r.RawFiles.ToString(inv), r.TotalGb.ToString("0.0", inv), r.Design.Power.ToString(inv), r.Design.Groups.ToString(inv),
                r.Design.Describe(), r.Design.Source, r.Design.Fractions.ToString(inv),
                r.MedianMb.ToString("0", inv), r.SdrfFile ?? "", C("reference_group"), C("keywords_hit"),
                C("organisms_of_record"), C("instruments"), C("title"), string.Join(" ", new[] { r.Design.Note, r.Error }.Where(x => x is not null)),
            }.Select(s => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')))).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
