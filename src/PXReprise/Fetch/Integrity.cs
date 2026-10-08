using System.Security.Cryptography;
using System.Text.Json;

namespace PXReprise.Fetch;

/// <summary>
/// Whether a downloaded raw file is the file PRIDE holds (2026-10-08). EBI's storage layer can write its own error
/// response INTO a file it is serving, over HTTPS and Aspera alike: 201 bytes reading
/// <c>&lt;Error&gt;&lt;Code&gt;ConnectionClosedException...</c> in place of data, the file keeping exactly PRIDE's size.
/// In one night 8 of about 25 PXD012307 transfers carried it, one of them a file the batch had fetched over HTTPS and
/// was about to search. A size check cannot see it; PRIDE's SHA-1 can, and so can the error text itself.
/// </summary>
public static class Integrity
{
    /// <summary>The start of EBI's storage error response. A Thermo .raw file has no reason to hold it.</summary>
    public static readonly byte[] EbiErrorMarker = "<Error><Code>"u8.ToArray();

    /// <summary>The file's SHA-1 (lower-case hex) and the offset of the first EBI error marker, or null if none.</summary>
    public static (string Sha1, long? ErrorAt) Check(string path)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 22];
        int keep = EbiErrorMarker.Length - 1, carried = 0;
        long consumed = 0;   // bytes of the file before buf[0]
        long? errorAt = null;
        int n;
        while ((n = s.Read(buf, carried, buf.Length - carried)) > 0)
        {
            sha1.AppendData(buf, carried, n);
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
        return (Convert.ToHexStringLower(sha1.GetHashAndReset()), errorAt);
    }

    /// <summary>Why the file is not PRIDE's, or null when it is (or when PRIDE gave no SHA-1 and no marker is present).</summary>
    public static string? Problem(string path, string? expectedSha1)
    {
        var (sha1, errorAt) = Check(path);
        if (errorAt is long at) return $"EBI error response inside the file at byte {at:N0} (sha1 {sha1})";
        if (!string.IsNullOrEmpty(expectedSha1) && !sha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            return $"sha1 {sha1}, PRIDE lists {expectedSha1.ToLowerInvariant()}";
        return null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// PRIDE's SHA-1 for each file of a project, from the v3 <c>files/all</c> list (its <c>checksum</c> field; the list
    /// mzLib reads leaves it empty, pride 007). Empty when PRIDE cannot be reached or gives none: the error-marker scan
    /// still runs then. A call for mzLib's PrideArchiveClient to own (it serves only MD5s, mzLib #1427).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> PrideSha1Async(string accession, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var r = await Http.GetAsync($"https://www.ebi.ac.uk/pride/ws/archive/v3/projects/{Uri.EscapeDataString(accession)}/files/all", ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return map;
            using var doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            foreach (var f in doc.RootElement.EnumerateArray())
                if (f.TryGetProperty("fileName", out var name) && f.TryGetProperty("checksum", out var c)
                    && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: 40 } sha)
                    map[name.GetString()!] = sha;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        return map;
    }
}
