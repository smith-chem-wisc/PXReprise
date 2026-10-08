using System.Security.Cryptography;
using System.Text.Json;

namespace PXReprise.Fetch;

/// <summary>
/// Whether a downloaded raw file is the file PRIDE holds (2026-10-08). EBI's storage layer can write its own error
/// response INTO a file it is serving, over HTTPS and Aspera alike: about 200 bytes reading
/// <c>&lt;Error&gt;&lt;Code&gt;ConnectionClosedException...</c> in place of data, the file keeping exactly PRIDE's size.
/// In one night 13 of about 40 PXD012307 transfers carried it, two of them files the batch had fetched over HTTPS and
/// was about to search. A size check cannot see it; PRIDE's checksum can, and so can the error text itself.
/// </summary>
public static class Integrity
{
    /// <summary>The start of EBI's storage error response. A Thermo .raw file has no reason to hold it.</summary>
    public static readonly byte[] EbiErrorMarker = "<Error><Code>"u8.ToArray();

    /// <summary>The file's SHA-1 and MD5 (lower-case hex) and the offset of the first EBI error marker, or null. One pass.</summary>
    public static (string Sha1, string Md5, long? ErrorAt) Check(string path)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 22];
        int keep = EbiErrorMarker.Length - 1, carried = 0;
        long consumed = 0;   // bytes of the file before buf[0]
        long? errorAt = null;
        int n;
        while ((n = s.Read(buf, carried, buf.Length - carried)) > 0)
        {
            sha1.AppendData(buf, carried, n);
            md5.AppendData(buf, carried, n);
            int len = carried + n;
            if (errorAt is null)
            {
                int i = buf.AsSpan(0, len).IndexOf(EbiErrorMarker);
                if (i >= 0) errorAt = consumed + i;
            }
            // Keep the tail so a marker split across two reads is still found.
            int tail = Math.Min(keep, len);
            Array.Copy(buf, len - tail, buf, 0, tail);
            consumed += len - tail;
            carried = tail;
        }
        return (Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()), errorAt);
    }

    /// <summary>
    /// Why the file is not PRIDE's, or null when it is. <paramref name="expected"/> is PRIDE's SHA-1 (40 hex) or MD5 (32
    /// hex), or null when PRIDE gave neither; the error-marker scan runs either way.
    /// </summary>
    public static string? Problem(string path, string? expected)
    {
        var (sha1, md5, errorAt) = Check(path);
        if (errorAt is long at) return $"EBI error response inside the file at byte {at:N0} (sha1 {sha1})";
        if (string.IsNullOrEmpty(expected)) return null;
        string got = expected.Length == 32 ? md5 : sha1, kind = expected.Length == 32 ? "md5" : "sha1";
        return got.Equals(expected, StringComparison.OrdinalIgnoreCase) ? null : $"{kind} {got}, PRIDE lists {expected.ToLowerInvariant()}";
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// PRIDE's checksum for each file of a project: the SHA-1 in the v3 <c>files/all</c> list (older deposits; the list
    /// mzLib reads leaves it empty, pride 007), else the MD5 from <c>files/checksum/{accession}</c> (newer deposits; the
    /// route mzLib #1427 adds). Empty when PRIDE cannot be reached or gives neither: the error-marker scan still runs.
    /// Calls for mzLib's PrideArchiveClient to own.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> PrideChecksumsAsync(string accession, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string acc = Uri.EscapeDataString(accession);
        try
        {
            using var r = await Http.GetAsync($"https://www.ebi.ac.uk/pride/ws/archive/v3/projects/{acc}/files/all", ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                foreach (var f in doc.RootElement.EnumerateArray())
                    if (f.TryGetProperty("fileName", out var name) && f.TryGetProperty("checksum", out var c)
                        && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: 40 } sha)
                        map[name.GetString()!] = sha;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        try
        {
            using var r = await Http.GetAsync($"https://www.ebi.ac.uk/pride/ws/archive/v3/files/checksum/{acc}", ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
                foreach (string line in (await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Split('\n').Skip(1))
                    if (line.Split('\t') is { Length: >= 2 } p && p[1].Trim() is { Length: 32 } m5 && !map.ContainsKey(p[0].Trim()))
                        map[p[0].Trim()] = m5;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return map;
    }
}
