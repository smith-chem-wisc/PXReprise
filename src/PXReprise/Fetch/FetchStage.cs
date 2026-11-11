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

    /// <summary>
    /// The download, checked against PRIDE's MD5 and size as the bytes arrive (mzLib #1427, #1449; pride PXR-W6): a copy
    /// that fails never reaches the file's name, and after mzLib's own retries it throws <see cref="MzLibUtil.MzLibException"/>.
    /// This default (for the offline fakes) downloads, then checks the whole file the same way.
    /// </summary>
    async Task<string> DownloadCheckedAsync(PrideArchiveFile file, string dir, string md5, long sizeBytes, CancellationToken ct)
    {
        string path = await DownloadAsync(file, dir, ct).ConfigureAwait(false);
        string? bad = new FileInfo(path).Length != sizeBytes ? $"{new FileInfo(path).Length:N0} bytes, PRIDE lists {sizeBytes:N0}" : Integrity.Problem(path, md5);
        if (bad is null) return path;
        File.Delete(path);
        throw new MzLibUtil.MzLibException($"{file.FileName}: {bad}");
    }
}

public sealed class PrideFiles(PrideArchiveClient client) : IPrideFiles
{
    public Task<List<PrideArchiveFile>> ListFilesAsync(string accession, CancellationToken ct) =>
        client.GetProjectFilesAsync(accession, cancellationToken: ct);

    public async Task<List<string>> ListFtpNamesAsync(string accession, CancellationToken ct)
    {
        try
        {
            return (await client.GetProjectFilesFromFtpAsync(accession, ct).ConfigureAwait(false)).Select(f => Path.GetFileName(f.RelativePath)).ToList();
        }
        catch (HttpRequestException e) when (IsEmptyFtpRoot(e))
        {
            return new List<string>();   // the caller's empty-listing retry (S52) decides, as it did on mzLib 1.0.593
        }
    }

    /// <summary>
    /// mzLib 1.0.594 (#1412) throws, after its own retries, where 1.0.593 returned an empty list: "PRIDE FTP listing of
    /// project X's root directory came back empty although the project exists". It carries no status code and no inner
    /// exception, so it is neither unavailability nor a transient status; pride 005 (PXR-S1) names the two marks.
    /// </summary>
    public static bool IsEmptyFtpRoot(HttpRequestException e) =>
        e.StatusCode is null && e.InnerException is null && e.Message.Contains("came back empty", StringComparison.Ordinal);

    /// <summary>overwrite=false: a complete file is skipped, so re-running a fetch is cheap.</summary>
    public Task<string> DownloadAsync(PrideArchiveFile file, string dir, CancellationToken ct) =>
        client.DownloadFileAsync(file, dir, overwrite: false, cancellationToken: ct);

    /// <summary>overwrite=false keeps a file already present only if it passes the size and MD5 check (mzLib reads it).</summary>
    public Task<string> DownloadCheckedAsync(PrideArchiveFile file, string dir, string md5, long sizeBytes, CancellationToken ct) =>
        client.DownloadFileAsync(file, dir, new PrideFileChecksum(file.FileName, md5, sizeBytes), overwrite: false, verifyMd5: true, cancellationToken: ct);
}

public enum Pick { All, ProbeSpread, MedianSize, FirstByName }

/// <param name="StallAfter">A download whose <c>.partial</c> file has not grown for this long is abandoned and retried
/// (zero turns the watch off). mzLib has its own 2-minute body stall guard; this one also covers a read that never
/// answers its cancellation (a first-run on 2026-10-03 sat 6 h on two frozen <c>.partial</c> files).</param>
/// <param name="Select">The question's own choice of raw files (an <c>[exceptions]</c> entry, D28), with its reason; null
/// takes every raw file. It is applied before any other rule and is always recorded.</param>
public sealed record FetchRequest(string Accession, string OutDir, Pick Pick, int MaxFiles, int MaxFileMb, string Extension,
    int ParallelDownloads, int MaxAttempts, TimeSpan Backoff, TimeSpan ListingBackoff, string WorkRoot,
    TimeSpan StallAfter = default, FileSelection? Select = null,
    [property: System.Text.Json.Serialization.JsonIgnore] Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? Sha1Source = null,
    AsperaClient? Aspera = null, PrideHttps Https = PrideHttps.Globus,
    [property: System.Text.Json.Serialization.JsonIgnore] DiskFloor? Floor = null,
    IReadOnlyDictionary<string, PXReprise.Config.FileReplacement>? Replace = null,
    [property: System.Text.Json.Serialization.JsonIgnore] Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? PrideSha1Source = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyDictionary<string, JsonObject>? Calibrated = null);

/// <summary>Which raw files of a deposit a question searches, and why (<c>[exceptions]</c>, PXReprise D28).</summary>
public sealed record FileSelection([property: System.Text.Json.Serialization.JsonIgnore] Func<string, bool> Keeps, string Reason);

/// <summary>A fetch would take the disk below the machine's floor (<c>min_free_gb</c>). Nothing is wrong with the deposit.</summary>
public sealed class DiskFloorException(string message) : Exception(message);

/// <summary>
/// v0.4.18: the machine's free-space floor, checked by the fetch itself. Before v0.4.18 the batch checked it only before
/// a deposit started: on 2026-10-15 PXD039172's 98 files (95 GB) began downloading with 125 GB free while PXD008990's
/// search and aging's publish were writing, and F: reached 0 bytes (the search and the publish both failed on it).
/// </summary>
public sealed record DiskFloor(Func<double> FreeGb, double MinFreeGb)
{
    /// <summary>Throws <see cref="DiskFloorException"/> when writing <paramref name="bytes"/> more would leave less than the floor.</summary>
    public void Check(long bytes, string what)
    {
        double free = FreeGb(), after = free - bytes / 1e9;
        if (after < MinFreeGb)
            throw new DiskFloorException($"{what} need {bytes / 1e9:0.0} GB more; {free:0} GB free would leave {after:0} GB, below the {MinFreeGb:0} GB floor");
    }
}

/// <summary>
/// One accession's raw files (and SDRF, if deposited), with the engine's retry policy. mzLib's client never retries
/// by design; the caller owns the policy (aging fetch.py, S33/S52): transport failures and the HTTP statuses EBI has
/// returned transiently (403, 408, 429, 5xx) are retried; anything else fails at once. Since mzLib 1.0.594 (#1412) the
/// client itself retries each call 3 times and RESUMES a broken download from its <c>.partial</c> (plus
/// <c>.partial.validator</c>, both left in the spectra folder on purpose), so our attempts sit on top of its own. Keep
/// <c>fetch_stall_minutes</c> at 5 or more: a cancellation deletes the <c>.partial</c>, and mzLib's own recovery can be
/// silent for up to 3 minutes (pride 005, PXR-S2).
/// </summary>
public static class FetchStage
{
    private static readonly Regex TransientStatus = new(@"failed with status (403|408|429|5\d\d)\b");

    public static bool IsTransient(Exception e) =>
        Envelope.IsUnavailable(e) || (e is HttpRequestException && TransientStatus.IsMatch(e.Message));

    private static bool IsSdrf(string name) => name.Contains("sdrf", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>What a file still adds to the disk: all of it, less a copy already there (that one is checked, not fetched again).</summary>
    /// <summary>
    /// v0.4.39 (aging 197, PXR-A158): puts a question's replacement in place of a raw file, instead of downloading it.
    /// PRIDE must still list the SHA-1 the replacement was made for: if it lists another (PRIDE replaced the file) or none,
    /// the repair is not known to stand for what PRIDE serves, and the fetch stops (InvalidDataException settles the
    /// deposit; a person decides). The copy must have the question's SHA-256. A file already in place with that SHA-256 is
    /// kept, so a later pass never copies it again or, worse, downloads PRIDE's damaged file over it.
    /// </summary>
    internal static (string Path, double Seconds, int Attempts) PlaceReplacement(PrideArchiveFile f, PXReprise.Config.FileReplacement rep,
        string spectraDir, string? prideSha1, Action<string>? log)
    {
        var sw = Stopwatch.StartNew();
        if (prideSha1 is null)
            throw new InvalidDataException($"{f.FileName}: PRIDE lists no SHA-1 for it, so the question's replacement (made for SHA-1 {rep.PrideSha1}) cannot be matched to the file PRIDE serves");
        if (!string.Equals(prideSha1, rep.PrideSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{f.FileName}: PRIDE now lists SHA-1 {prideSha1}, and the question's replacement was made for {rep.PrideSha1}: PRIDE's file has changed, so check the replacement again");
        string dst = Path.Combine(spectraDir, f.FileName);
        if (File.Exists(dst) && Sha(dst) == rep.Sha256)
        {
            log?.Invoke($"{f.FileName}: the question's replacement is already in place (SHA-256 {rep.Sha256[..12]})");
            return (dst, Math.Round(sw.Elapsed.TotalSeconds, 1), 1);
        }
        if (!File.Exists(rep.Path))
            throw new InvalidDataException($"{f.FileName}: the question's replacement {rep.Path} does not exist");
        string tmp = dst + ".replacing";
        File.Copy(rep.Path, tmp, overwrite: true);
        string got = Sha(tmp);
        if (got != rep.Sha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"{f.FileName}: the question's replacement {rep.Path} has SHA-256 {got}, and the question names {rep.Sha256}");
        }
        File.Move(tmp, dst, overwrite: true);
        log?.Invoke($"{f.FileName} replaced by the question's copy {rep.Path} (SHA-256 {rep.Sha256[..12]}): {rep.Note}");
        return (dst, Math.Round(sw.Elapsed.TotalSeconds, 1), 1);
    }

    internal static long StillToFetch(PrideArchiveFile f, string spectraDir)
    {
        string p = Path.Combine(spectraDir, f.FileName);
        return Math.Max(0, f.FileSizeBytes - (File.Exists(p) ? new FileInfo(p).Length : 0));
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
                extension = r.Extension, parallel_downloads = r.ParallelDownloads, max_attempts = r.MaxAttempts,
                aspera_attempts = r.Aspera?.Attempts ?? 0, pride_https = r.Https.ToString().ToLowerInvariant() })!, null);
        prov.Set("accession", (JsonNode)r.Accession);
        // v0.4.43: a split search's key (PXD027754-mouse) names the search; PRIDE is asked for the deposit.
        string deposit = Config.SearchKey.Accession(r.Accession);
        if (deposit != r.Accession) prov.Set("pride_accession", (JsonNode)deposit);
        string discover = Path.Combine(Path.GetDirectoryName(outDir)!, "..", "01_discover", "provenance.json");
        if (File.Exists(discover)) prov.Upstream(discover);
        prov.Command(new[] { "mzLib.PrideArchiveClient.GetProjectFilesAsync", deposit });
        prov.Command(new[] { "mzLib.PrideArchiveClient.GetProjectFilesFromFtpAsync", deposit });
        var flags = new List<string>();

        int listingAttempts = Math.Min(r.MaxAttempts, 4);
        var (rest, nRest) = await ListWithRetry(() => pride.ListFilesAsync(deposit, ct), listingAttempts, r.ListingBackoff, ct).ConfigureAwait(false);
        var (ftp, nFtp) = await ListWithRetry(() => pride.ListFtpNamesAsync(deposit, ct), listingAttempts, r.ListingBackoff, ct).ConfigureAwait(false);
        prov.Set("listing_attempts", new JsonObject { ["list_files"] = nRest, ["list_ftp_files"] = nFtp });
        if (nRest > 1 || nFtp > 1) flags.Add($"listing_retried: list_files x{nRest}, list_ftp_files x{nFtp} (S52)");

        string ext = r.Extension.ToLowerInvariant();
        var raws = rest.Where(f => f.FileName.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)).ToList();
        var ftpRaw = ftp.Where(n => n.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        int nDeposit = raws.Count;
        if (r.Select is { } sel)
        {
            // The question's choice of runs (D28): a design decision it states, never one made here by size (S43).
            raws = raws.Where(f => sel.Keeps(f.FileName)).ToList();
            ftpRaw = ftpRaw.Where(sel.Keeps).ToHashSet(StringComparer.Ordinal);
            prov.Set("question_file_selection", new JsonObject { ["reason"] = sel.Reason, ["selected"] = raws.Count, ["of"] = nDeposit });
            flags.Add($"subset_by_question: {raws.Count} of {nDeposit} raw files, chosen by the question ({sel.Reason})");
        }
        var missingFromRest = ftpRaw.Except(raws.Select(f => f.FileName)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        int nListed = raws.Count;
        raws = raws.Where(f => f.FileSizeBytes <= (long)r.MaxFileMb * 1_000_000).ToList();
        int nOversize = nListed - raws.Count;
        var chosen = Choose(raws, r.Pick, r.MaxFiles);
        var sdrfs = rest.Where(f => IsSdrf(f.FileName)).ToList();
        // PRIDE's REST list can miss submitted files that the FTP tree holds (pride 007, PXR-S8). An SDRF there is
        // flagged, as a missing raw is; fetching it waits for pride's SDRF verb, which reads both lists.
        var sdrfFtpOnly = ftp.Where(IsSdrf).Except(sdrfs.Select(f => f.FileName), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (sdrfFtpOnly.Count > 0) flags.Add($"sdrf_ftp_only: {string.Join(", ", sdrfFtpOnly)} (in the FTP tree, not in PRIDE's file list; not fetched)");
        prov.Set("raw_files_listed", (JsonNode)nListed);
        prov.Set("raw_files_chosen", (JsonNode)chosen.Count);
        // A subset of a deposit is a design decision; it is allowed (a probe needs few files) but never silent (S43).
        if (chosen.Count < nListed)
            flags.Add($"subset_of_deposit: {chosen.Count} of {nListed} raw files (pick={Snake(r.Pick)}"
                      + (nOversize > 0 ? $", {nOversize} above max_file_mb={r.MaxFileMb}" : "") + "); results describe this subset, not the experiment");

        string spectraDir = Path.Combine(outDir, "spectra"), metaDir = Path.Combine(outDir, "metadata");
        Directory.CreateDirectory(spectraDir);
        string manifestFile = Path.Combine(outDir, "fetch_manifest.json");
        // v0.4.45 (the user, 2026-11-11): a chosen raw that is not on disk and has a calibrated stand-in (Search.CalibratedStandIns)
        // is not fetched again. The stand-in carries the raw's entry from the fetch manifest of the search it came from (name,
        // SHA-256, PRIDE checksum: what dataRepo builds a run from), and that entry is written again here.
        var earlier = r.Calibrated ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var standIn = chosen.Where(f => earlier.ContainsKey(f.FileName) && !File.Exists(Path.Combine(spectraDir, f.FileName)))
            .Select(f => f.FileName).ToHashSet(StringComparer.Ordinal);
        // The whole selection must fit above the floor before anything is downloaded; each file is checked again as it
        // starts, since searches and a publish write to the same disk meanwhile (v0.4.18).
        r.Floor?.Check(chosen.Where(f => !standIn.Contains(f.FileName)).Sum(f => StillToFetch(f, spectraDir)), $"{r.Accession}'s {chosen.Count} raw file(s)");
        prov.Command(new[] { "mzLib.PrideArchiveClient.DownloadFileAsync" }.Concat(chosen.Where(f => !standIn.Contains(f.FileName)).Concat(sdrfs).Select(f => f.FileName)).Append("overwrite=False"));
        var results = new (string Path, double Seconds, int Attempts)[chosen.Count];
        int done = 0;
        // 2026-10-08: every raw file is checked against PRIDE's SHA-1 where PRIDE gives one, and always for EBI's own error
        // response written into the data (Integrity); a file that fails is deleted and fetched again.
        var sha1 = r.Sha1Source is null ? new Dictionary<string, string>() : await r.Sha1Source(deposit, ct).ConfigureAwait(false);
        prov.Set("pride_sha1_known", (JsonNode)chosen.Count(f => sha1.ContainsKey(f.FileName)));
        // v0.4.39 (aging PXR-A158): a file the question replaces is not downloaded. PRIDE's own SHA-1 for it is read first
        // (an outage throws and the deposit waits for a later pass), so the replacement is known to stand for PRIDE's file.
        var replace = chosen.Where(f => r.Replace?.ContainsKey(f.FileName) == true).ToDictionary(f => f.FileName, f => r.Replace![f.FileName], StringComparer.Ordinal);
        var prideSha1 = replace.Count == 0 ? new Dictionary<string, string>()
            : await (r.PrideSha1Source ?? Integrity.PrideSha1sAsync)(deposit, ct).ConfigureAwait(false);
        using (var gate = new SemaphoreSlim(Math.Max(1, r.ParallelDownloads)))
        {
            await Task.WhenAll(chosen.Select(async (f, i) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (standIn.Contains(f.FileName))
                    {
                        results[i] = (Path.Combine(spectraDir, f.FileName), 0, 0);
                        log?.Invoke($"{Interlocked.Increment(ref done)} of {chosen.Count} done: {f.FileName} has a calibrated stand-in (not fetched again)");
                        return;
                    }
                    r.Floor?.Check(StillToFetch(f, spectraDir), f.FileName);
                    if (replace.TryGetValue(f.FileName, out var rep))
                    {
                        results[i] = PlaceReplacement(f, rep, spectraDir, prideSha1.GetValueOrDefault(f.FileName), log);
                        log?.Invoke($"{Interlocked.Increment(ref done)} of {chosen.Count} done: {f.FileName} is the question's replacement (not downloaded)");
                        return;
                    }
                    results[i] = await DownloadWithRetry(pride, f, spectraDir, r.MaxAttempts, r.Backoff, ct, r.StallAfter, log,
                        expectedChecksum: sha1.GetValueOrDefault(f.FileName), verify: true, aspera: r.Aspera, https: r.Https).ConfigureAwait(false);
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
            foreach (var s in sdrfs)
                gotSdrf.Add((await DownloadWithRetry(pride, s, metaDir, r.MaxAttempts, r.Backoff, ct, r.StallAfter, log).ConfigureAwait(false)).Path);
        }

        var files = new JsonArray();
        for (int i = 0; i < chosen.Count; i++)
        {
            var f = chosen[i];
            if (standIn.Contains(f.FileName))
            {
                var kept = (JsonObject)earlier[f.FileName].DeepClone();
                kept["calibrated_stand_in"] = true;
                files.Add(kept);
                continue;
            }
            files.Add(new JsonObject
            {
                ["file"] = results[i].Path, ["name"] = f.FileName, ["pride_size_bytes"] = f.FileSizeBytes,
                ["local_size_bytes"] = new FileInfo(results[i].Path).Length,
                ["pride_checksum"] = string.IsNullOrEmpty(f.Checksum) ? null : f.Checksum,
                ["sha256"] = Sha(results[i].Path), ["category"] = f.FileCategory?.Value ?? f.FileCategory?.Name,
                ["replaced"] = replace.TryGetValue(f.FileName, out var rp) ? new JsonObject
                {
                    ["source"] = rp.Path, ["pride_sha1"] = rp.PrideSha1, ["sha256"] = rp.Sha256, ["note"] = rp.Note,
                } : null,
            });
        }
        if (replace.Count > 0)
        {
            prov.Set("replaced_files", new JsonArray(replace.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (JsonNode?)new JsonObject
            {
                ["name"] = kv.Key, ["source"] = kv.Value.Path, ["pride_sha1"] = kv.Value.PrideSha1, ["sha256"] = kv.Value.Sha256, ["note"] = kv.Value.Note,
            }).ToArray()));
            flags.Add($"replaced_by_question: {replace.Count} of {chosen.Count} raw files are the question's replacements, not PRIDE's bytes ("
                      + string.Join(", ", replace.Keys.Order(StringComparer.Ordinal)) + ")");
        }
        var manifest = new JsonObject
        {
            // rest_raw_count counts the raw files this fetch is responsible for: after the question's selection, if any.
            ["accession"] = r.Accession, ["rest_raw_count"] = nListed, ["deposit_raw_count"] = nDeposit, ["ftp_raw_count"] = ftpRaw.Count,
            ["raw_missing_from_rest_manifest"] = new JsonArray(missingFromRest.Select(n => (JsonNode?)n).ToArray()),
            ["files"] = files, ["sdrf"] = new JsonArray(gotSdrf.Select(n => (JsonNode?)n).ToArray()),
        };
        File.WriteAllText(manifestFile, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (standIn.Count > 0)
        {
            prov.Set("calibrated_stand_ins", new JsonArray(standIn.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n).ToArray()));
            prov.Note($"{standIn.Count} of {chosen.Count} raw file(s) were not fetched again: each was deleted once MetaMorpheus had calibrated it, and that calibration, "
                      + "kept from an interrupted search, stands in for it (v0.4.45). Their manifest entries, SHA-256 included, are the earlier fetch's.");
        }
        if (!files.Any(f => f!["pride_checksum"] is not null))
            prov.Note("pride_checksum is null because PRIDE's REST file list leaves it empty (pride 007, PXR-S6), not because PRIDE "
                      + "has none: its MD5s are served only by /files/checksum (mzLib #1427, released in 1.0.595, not yet used here). Integrity rests on the "
                      + "local SHA-256 and PRIDE's listed size.");
        foreach (var (p, _, _) in results.Where(x => !standIn.Contains(Path.GetFileName(x.Path)))) prov.Output(p);
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
    /// <para>
    /// Routes: Aspera first, for up to <c>aspera.Attempts</c> attempts at a file not yet on disk, so a broken Aspera setup
    /// costs a few attempts, never the deposit. A DAMAGED Aspera copy sends the file's remaining attempts to HTTPS at
    /// once (pride 014: Aspera damaged 24% of transfers 2026-10-08 to 10-12; the Globus host none of 15). HTTPS comes from
    /// <paramref name="https"/>'s host; a Globus attempt that fails to deliver (not a damaged copy) is followed by one
    /// from <c>ftp.pride.ebi.ac.uk</c>, then Globus again. Every transfer logs its route, size and time (pride PXR-G1).
    /// </para>
    /// </summary>
    public static async Task<(string Path, double Seconds, int Attempts)> DownloadWithRetry(IPrideFiles pride, PrideArchiveFile f,
        string dir, int attempts, TimeSpan backoff, CancellationToken ct, TimeSpan stallAfter = default, Action<string>? log = null,
        TimeSpan? poll = null, TimeSpan? abandonGrace = null, string? expectedChecksum = null, bool verify = false,
        AsperaClient? aspera = null, PrideHttps https = PrideHttps.Ftp,
        Func<AsperaClient, PrideArchiveFile, string, CancellationToken, Task<string>>? asperaDownload = null)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        string target = Path.Combine(dir, f.FileName);
        bool asperaDamaged = false, skipGlobus = false;
        for (int a = 1; a <= attempts; a++)
        {
            if (f.FileSizeBytes > 0 && File.Exists(target) && new FileInfo(target).Length is long had && had != f.FileSizeBytes)
            {
                log?.Invoke($"{f.FileName} on disk is {had:N0} bytes, PRIDE lists {f.FileSizeBytes:N0}: deleted, downloading again");
                File.Delete(target);
            }
            // A file already present is checked, not refetched.
            bool onDisk = File.Exists(target);
            bool overAspera = aspera is not null && a <= aspera.Attempts && !asperaDamaged && !onDisk && Aspera.Source(f) is not null;
            var viaGlobus = !overAspera && https == PrideHttps.Globus && !skipGlobus ? PrideHosts.ViaGlobus(f) : null;
            string route = overAspera ? "Aspera" : viaGlobus is not null ? "Globus HTTPS" : "ftp.pride HTTPS";
            skipGlobus = false;
            bool damaged = false;
            // v0.4.34 (pride PXR-W6): over HTTPS, a file PRIDE lists an MD5 for is checked by mzLib as it arrives (and a
            // copy already on disk is checked before it is kept), so it is not read again here. Aspera copies and files
            // with only a SHA-1 keep this engine's own check (Integrity).
            bool mzLibChecks = !overAspera && expectedChecksum is { Length: 32 } && f.FileSizeBytes > 0;
            var https1 = viaGlobus ?? f;
            Func<CancellationToken, Task<string>> httpsDownload = mzLibChecks
                ? async t =>
                {
                    try { return await pride.DownloadCheckedAsync(https1, dir, expectedChecksum!, f.FileSizeBytes, t).ConfigureAwait(false); }
                    catch (MzLibUtil.MzLibException e)
                    {
                        damaged = true;
                        log?.Invoke($"{f.FileName} failed mzLib's size/MD5 check on every one of its tries ({e.Message})");
                        throw new HttpIOException(HttpRequestError.InvalidResponse, $"{f.FileName}: {e.Message}");
                    }
                }
                : t => pride.DownloadAsync(https1, dir, t);
            var transfer = Stopwatch.StartNew();
            try
            {
                string path = overAspera
                    ? await (asperaDownload ?? Aspera.DownloadAsync)(aspera!, f, dir, ct).ConfigureAwait(false)
                    : await Watched(httpsDownload, https1.FileName, dir, stallAfter, poll ?? TimeSpan.FromSeconds(15), abandonGrace ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                long got = new FileInfo(path).Length;
                if (!onDisk)
                    log?.Invoke($"{f.FileName} arrived over {route} (attempt {a}): {got / 1048576.0:F0} MB in {transfer.Elapsed.TotalSeconds:F0} s{(mzLibChecks ? ", MD5 checked by mzLib" : "")}");
                if (f.FileSizeBytes > 0 && got != f.FileSizeBytes)
                {
                    File.Delete(path);
                    throw new HttpIOException(HttpRequestError.ResponseEnded, $"{f.FileName}: received {got:N0} bytes, PRIDE lists {f.FileSizeBytes:N0}");
                }
                // The right size is not the right file: EBI can write its own error response into the data (Integrity).
                // A file already on disk from an earlier fetch is checked the same way.
                if (verify && !mzLibChecks && Integrity.Problem(path, expectedChecksum) is { } bad)
                {
                    damaged = true;
                    log?.Invoke($"{f.FileName} is not PRIDE's file ({bad}): deleted, downloading again");
                    File.Delete(path);
                    throw new HttpIOException(HttpRequestError.InvalidResponse, $"{f.FileName}: {bad}");
                }
                return (path, Math.Round(sw.Elapsed.TotalSeconds, 1), a);
            }
            catch (Exception e) when (IsTransient(e))
            {
                last = e;
                if (overAspera && damaged) asperaDamaged = true;
                if (viaGlobus is not null && !damaged) skipGlobus = true;
                log?.Invoke($"retry {f.FileName} attempt {a} of {attempts}: {Describe(e)}"
                    + (overAspera && damaged && a < attempts ? $"; the next attempt goes over {(https == PrideHttps.Globus ? "Globus HTTPS" : "ftp.pride HTTPS")}" : ""));
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
    private static async Task<string> Watched(Func<CancellationToken, Task<string>> start, string fileName, string dir, TimeSpan stall, TimeSpan poll,
        TimeSpan grace, CancellationToken ct)
    {
        if (stall <= TimeSpan.Zero) return await start(ct).ConfigureAwait(false);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = start(attempt.Token);
        string partial = Path.Combine(dir, fileName + ".partial");
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
            throw new TimeoutException($"{fileName}: no data for {stall.TotalMinutes:0.#} min ({Math.Max(seen, 0):N0} bytes received)");
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
