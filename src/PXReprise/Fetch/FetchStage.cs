using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PXReprise.Cli;
using PXReprise.Provenance;
using UsefulProteomicsDatabases;

namespace PXReprise.Fetch;

/// <summary>The three PRIDE calls a fetch makes, behind an interface so tests run offline.</summary>
public interface IPrideFiles
{
    Task<List<PrideArchiveFile>> ListFilesAsync(string accession, CancellationToken ct);
    Task<List<string>> ListFtpNamesAsync(string accession, CancellationToken ct);
    Task<string> DownloadAsync(PrideArchiveFile file, string dir, CancellationToken ct);
}

public sealed class PrideFiles(PrideArchiveClient client) : IPrideFiles
{
    public Task<List<PrideArchiveFile>> ListFilesAsync(string accession, CancellationToken ct) =>
        client.GetProjectFilesAsync(accession, cancellationToken: ct);

    public async Task<List<string>> ListFtpNamesAsync(string accession, CancellationToken ct) =>
        (await client.GetProjectFilesFromFtpAsync(accession, ct).ConfigureAwait(false)).Select(f => Path.GetFileName(f.RelativePath)).ToList();

    /// <summary>overwrite=false: a complete file is skipped, so re-running a fetch is cheap.</summary>
    public Task<string> DownloadAsync(PrideArchiveFile file, string dir, CancellationToken ct) =>
        client.DownloadFileAsync(file, dir, overwrite: false, cancellationToken: ct);
}

public enum Pick { All, ProbeSpread, MedianSize, FirstByName }

/// <param name="StallAfter">A download whose <c>.partial</c> file has not grown for this long is abandoned and retried
/// (zero turns the watch off). mzLib has its own 2-minute body stall guard; this one also covers a read that never
/// answers its cancellation (a first-run on 2026-10-03 sat 6 h on two frozen <c>.partial</c> files).</param>
public sealed record FetchRequest(string Accession, string OutDir, Pick Pick, int MaxFiles, int MaxFileMb, string Extension,
    int ParallelDownloads, int MaxAttempts, TimeSpan Backoff, TimeSpan ListingBackoff, string WorkRoot,
    TimeSpan StallAfter = default);

/// <summary>
/// One accession's raw files (and SDRF, if deposited), with the engine's retry policy. mzLib's client never retries
/// by design; the caller owns the policy (aging fetch.py, S33/S52): transport failures and the HTTP statuses EBI has
/// returned transiently (403, 408, 429, 5xx) are retried; anything else fails at once. A retry is not a resume: a
/// transfer that dies at 90% pays for the whole file again until PRIDE's client resumes (pride REQ-PRIDE-1).
/// </summary>
public static class FetchStage
{
    private static readonly Regex TransientStatus = new(@"failed with status (403|408|429|5\d\d)\b");

    public static bool IsTransient(Exception e) =>
        Envelope.IsUnavailable(e) || (e is HttpRequestException && TransientStatus.IsMatch(e.Message));

    /// <summary>
    /// Up to three probe files: the median by size, and the first and last by name. One median file cannot see a
    /// deposit that mixes acquisition methods (PXD022196: 11 of 43 files ion-trap CID, all sorted to one end of the
    /// name order). The smallest file is left out while any other remains: it is often a blank. Input sorted by size.
    /// </summary>
    public static List<PrideArchiveFile> ProbeSpread(IReadOnlyList<PrideArchiveFile> bySize)
    {
        var pool = bySize.Count > 1 ? bySize.Skip(1).ToList() : bySize.ToList();
        var byName = pool.OrderBy(f => f.FileName, StringComparer.Ordinal).ToList();
        var chosen = new List<PrideArchiveFile>();
        foreach (var f in new[] { bySize[bySize.Count / 2], byName[0], byName[^1] })
            if (chosen.All(c => c.FileName != f.FileName)) chosen.Add(f);
        return chosen;
    }

    public static List<PrideArchiveFile> Choose(IReadOnlyList<PrideArchiveFile> raws, Pick pick, int maxFiles)
    {
        var bySize = raws.OrderBy(f => f.FileSizeBytes).ToList();   // stable: ties keep listing order, as Python's sort
        switch (pick)
        {
            case Pick.All: return raws.OrderBy(f => f.FileName, StringComparer.Ordinal).ToList();
            case Pick.FirstByName: return raws.OrderBy(f => f.FileName, StringComparer.Ordinal).Take(maxFiles).ToList();
            case Pick.MedianSize when bySize.Count > 0:
                int start = Math.Max(0, bySize.Count / 2 - maxFiles / 2);
                return bySize.Skip(start).Take(maxFiles).ToList();
            case Pick.ProbeSpread when bySize.Count > 0: return ProbeSpread(bySize);
            default: return bySize.Take(maxFiles).ToList();
        }
    }

    /// <param name="log">Progress lines (each file done, each retry with its exception type); null for none.</param>
    public static async Task<JsonObject> RunAsync(FetchRequest r, IPrideFiles pride, CancellationToken ct, Action<string>? log = null)
    {
        string outDir = Path.GetFullPath(r.OutDir);
        Directory.CreateDirectory(outDir);
        string paramsFile = Path.Combine(outDir, "params.json");
        File.WriteAllText(paramsFile, JsonSerializer.Serialize(r, Envelope.Json));
        var prov = new AgingProvenance("fetch", r.WorkRoot, paramsFile,
            JsonSerializer.SerializeToNode(new { pick = Snake(r.Pick), max_files = r.MaxFiles, max_file_mb = r.MaxFileMb,
                extension = r.Extension, parallel_downloads = r.ParallelDownloads, max_attempts = r.MaxAttempts })!, null);
        prov.Set("accession", (JsonNode)r.Accession);
        string discover = Path.Combine(Path.GetDirectoryName(outDir)!, "..", "01_discover", "provenance.json");
        if (File.Exists(discover)) prov.Upstream(discover);
        prov.Command(new[] { "mzLib.PrideArchiveClient.GetProjectFilesAsync", r.Accession });
        prov.Command(new[] { "mzLib.PrideArchiveClient.GetProjectFilesFromFtpAsync", r.Accession });
        var flags = new List<string>();

        int listingAttempts = Math.Min(r.MaxAttempts, 4);
        var (rest, nRest) = await ListWithRetry(() => pride.ListFilesAsync(r.Accession, ct), listingAttempts, r.ListingBackoff, ct).ConfigureAwait(false);
        var (ftp, nFtp) = await ListWithRetry(() => pride.ListFtpNamesAsync(r.Accession, ct), listingAttempts, r.ListingBackoff, ct).ConfigureAwait(false);
        prov.Set("listing_attempts", new JsonObject { ["list_files"] = nRest, ["list_ftp_files"] = nFtp });
        if (nRest > 1 || nFtp > 1) flags.Add($"listing_retried: list_files x{nRest}, list_ftp_files x{nFtp} (S52)");

        string ext = r.Extension.ToLowerInvariant();
        var raws = rest.Where(f => f.FileName.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)).ToList();
        var ftpRaw = ftp.Where(n => n.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var missingFromRest = ftpRaw.Except(raws.Select(f => f.FileName)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        int nListed = raws.Count;
        raws = raws.Where(f => f.FileSizeBytes <= (long)r.MaxFileMb * 1_000_000).ToList();
        int nOversize = nListed - raws.Count;
        var chosen = Choose(raws, r.Pick, r.MaxFiles);
        var sdrfs = rest.Where(f => f.FileName.Contains("sdrf", StringComparison.OrdinalIgnoreCase)).ToList();
        prov.Set("raw_files_listed", (JsonNode)nListed);
        prov.Set("raw_files_chosen", (JsonNode)chosen.Count);
        // A subset of a deposit is a design decision; it is allowed (a probe needs few files) but never silent (S43).
        if (chosen.Count < nListed)
            flags.Add($"subset_of_deposit: {chosen.Count} of {nListed} raw files (pick={Snake(r.Pick)}"
                      + (nOversize > 0 ? $", {nOversize} above max_file_mb={r.MaxFileMb}" : "") + "); results describe this subset, not the experiment");

        string spectraDir = Path.Combine(outDir, "spectra"), metaDir = Path.Combine(outDir, "metadata");
        Directory.CreateDirectory(spectraDir);
        prov.Command(new[] { "mzLib.PrideArchiveClient.DownloadFileAsync" }.Concat(chosen.Concat(sdrfs).Select(f => f.FileName)).Append("overwrite=False"));
        var results = new (string Path, double Seconds, int Attempts)[chosen.Count];
        int done = 0;
        using (var gate = new SemaphoreSlim(Math.Max(1, r.ParallelDownloads)))
        {
            await Task.WhenAll(chosen.Select(async (f, i) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[i] = await DownloadWithRetry(pride, f, spectraDir, r.MaxAttempts, r.Backoff, ct, r.StallAfter, log).ConfigureAwait(false);
                    var (_, s, a) = results[i];
                    log?.Invoke($"{Interlocked.Increment(ref done)} of {chosen.Count} done: {f.FileName} {f.FileSizeBytes / 1e6:0} MB in {s:0} s"
                                + (a > 1 ? $", attempt {a}" : ""));
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        var seconds = new JsonObject();
        var attempts = new JsonObject();
        for (int i = 0; i < chosen.Count; i++) { seconds[chosen[i].FileName] = results[i].Seconds; attempts[chosen[i].FileName] = results[i].Attempts; }
        prov.Set("download_seconds", seconds);
        prov.Set("download_attempts", attempts);
        prov.Set("max_attempts", (JsonNode)r.MaxAttempts);
        prov.Set("parallel_downloads", (JsonNode)r.ParallelDownloads);
        var retried = chosen.Select((f, i) => (f.FileName, results[i].Attempts)).Where(x => x.Attempts > 1).OrderBy(x => x.FileName, StringComparer.Ordinal).ToList();
        if (retried.Count > 0)
            flags.Add($"download_retried: {retried.Count} of {chosen.Count} files needed more than one attempt ({string.Join(", ", retried.Select(x => $"{x.FileName} x{x.Attempts}"))})");

        var gotSdrf = new List<string>();
        if (sdrfs.Count > 0)
        {
            Directory.CreateDirectory(metaDir);
            foreach (var s in sdrfs) gotSdrf.Add(await pride.DownloadAsync(s, metaDir, ct).ConfigureAwait(false));
        }

        var files = new JsonArray();
        for (int i = 0; i < chosen.Count; i++)
        {
            var f = chosen[i];
            files.Add(new JsonObject
            {
                ["file"] = results[i].Path, ["name"] = f.FileName, ["pride_size_bytes"] = f.FileSizeBytes,
                ["local_size_bytes"] = new FileInfo(results[i].Path).Length,
                ["pride_checksum"] = string.IsNullOrEmpty(f.Checksum) ? null : f.Checksum,
                ["sha256"] = Sha(results[i].Path), ["category"] = f.FileCategory?.Value ?? f.FileCategory?.Name,
            });
        }
        var manifest = new JsonObject
        {
            ["accession"] = r.Accession, ["rest_raw_count"] = nListed, ["ftp_raw_count"] = ftpRaw.Count,
            ["raw_missing_from_rest_manifest"] = new JsonArray(missingFromRest.Select(n => (JsonNode?)n).ToArray()),
            ["files"] = files, ["sdrf"] = new JsonArray(gotSdrf.Select(n => (JsonNode?)n).ToArray()),
        };
        string manifestFile = Path.Combine(outDir, "fetch_manifest.json");
        File.WriteAllText(manifestFile, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!files.Any(f => f!["pride_checksum"] is not null))
            prov.Note("PRIDE supplied no checksum; integrity rests on the local SHA-256 only (REQ-PRIDE-2).");
        foreach (var (p, _, _) in results) prov.Output(p);
        foreach (string s in gotSdrf) prov.Output(s);
        prov.Output(manifestFile);
        if (flags.Count > 0) prov.Set("flags", new JsonArray(flags.Select(x => (JsonNode?)x).ToArray()));
        prov.Write(outDir);
        return manifest;
    }

    /// <summary>
    /// A PRIDE listing, retrying a transport failure and an EMPTY listing (S52: PXD058248's FTP listing was empty at
    /// 11:57 UTC and held 46 files under three hours later). Returns the result and the attempts used.
    /// </summary>
    public static async Task<(List<T> Result, int Attempts)> ListWithRetry<T>(Func<Task<List<T>>> list, int attempts, TimeSpan backoff, CancellationToken ct)
    {
        for (int a = 1; ; a++)
        {
            try
            {
                var result = await list().ConfigureAwait(false);
                if (result.Count > 0 || a >= attempts) return (result, a);
            }
            catch (Exception e) when (IsTransient(e) && a < attempts) { }
            await Task.Delay(backoff * a, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One file, retried on transport failures. A file is complete only at PRIDE's listed size: mzLib's
    /// <c>overwrite: false</c> skips on existence alone, so a file cut short (a crash, a hand download) would otherwise
    /// be searched. Over 2,222 aging downloads PRIDE's size and the local size never differed.
    /// </summary>
    public static async Task<(string Path, double Seconds, int Attempts)> DownloadWithRetry(IPrideFiles pride, PrideArchiveFile f,
        string dir, int attempts, TimeSpan backoff, CancellationToken ct, TimeSpan stallAfter = default, Action<string>? log = null,
        TimeSpan? poll = null, TimeSpan? abandonGrace = null)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        string target = Path.Combine(dir, f.FileName);
        for (int a = 1; a <= attempts; a++)
        {
            if (f.FileSizeBytes > 0 && File.Exists(target) && new FileInfo(target).Length is long had && had != f.FileSizeBytes)
            {
                log?.Invoke($"{f.FileName} on disk is {had:N0} bytes, PRIDE lists {f.FileSizeBytes:N0}: deleted, downloading again");
                File.Delete(target);
            }
            try
            {
                string path = await Watched(pride, f, dir, stallAfter, poll ?? TimeSpan.FromSeconds(15), abandonGrace ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                long got = new FileInfo(path).Length;
                if (f.FileSizeBytes <= 0 || got == f.FileSizeBytes) return (path, Math.Round(sw.Elapsed.TotalSeconds, 1), a);
                File.Delete(path);
                throw new HttpIOException(HttpRequestError.ResponseEnded, $"{f.FileName}: received {got:N0} bytes, PRIDE lists {f.FileSizeBytes:N0}");
            }
            catch (Exception e) when (IsTransient(e))
            {
                last = e;
                log?.Invoke($"retry {f.FileName} attempt {a} of {attempts}: {Describe(e)}");
                if (a < attempts) await Task.Delay(backoff * a, ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException($"{f.FileName}: {attempts} attempts all failed; last error: {(last is null ? "none" : Describe(last))}", last);
    }

    /// <summary>The exception's type and message: a bare message cannot tell a TLS drop from a disk error.</summary>
    public static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";

    /// <summary>
    /// The download, abandoned when its <c>.partial</c> stops growing for <paramref name="stall"/>. The attempt is
    /// cancelled first; if it does not end within <paramref name="grace"/> it is left behind, so a read that ignores
    /// cancellation can no longer hang the batch. Either way it is a <see cref="TimeoutException"/>: unavailability.
    /// </summary>
    private static async Task<string> Watched(IPrideFiles pride, PrideArchiveFile f, string dir, TimeSpan stall, TimeSpan poll,
        TimeSpan grace, CancellationToken ct)
    {
        if (stall <= TimeSpan.Zero) return await pride.DownloadAsync(f, dir, ct).ConfigureAwait(false);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = pride.DownloadAsync(f, dir, attempt.Token);
        string partial = Path.Combine(dir, f.FileName + ".partial");
        long seen = -1;
        var quiet = Stopwatch.StartNew();
        while (true)
        {
            if (await Task.WhenAny(download, Task.Delay(poll, ct)).ConfigureAwait(false) == download)
                return await download.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            long len = PartialLength(partial);
            if (len != seen) { seen = len; quiet.Restart(); continue; }
            if (quiet.Elapsed < stall) continue;
            attempt.Cancel();
            if (await Task.WhenAny(download, Task.Delay(grace, ct)).ConfigureAwait(false) != download)
                _ = download.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // left behind; observe its fault
            else if (download.IsCompletedSuccessfully)
                return download.Result;   // it finished as we cancelled
            throw new TimeoutException($"{f.FileName}: no data for {stall.TotalMinutes:0.#} min ({Math.Max(seen, 0):N0} bytes received)");
        }
    }

    private static long PartialLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : -1; }
        catch (IOException) { return -1; }
    }

    private static string Snake(Pick p) => Config.ProfileLoader.Snake(p.ToString());

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
