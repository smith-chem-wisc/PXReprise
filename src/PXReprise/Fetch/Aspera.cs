using System.Diagnostics;
using UsefulProteomicsDatabases;

namespace PXReprise.Fetch;

/// <summary>
/// The machine's IBM Aspera client, used by the fetch before HTTPS (2026-10-08, the user: "Yes try aspera"). EBI serves
/// public PRIDE files over Aspera at about five times the HTTPS rate measured here (4 sessions about 10 MB/s, HTTPS
/// about 2). EBI (KB0011565) requires ascp 4.4.4 or newer, encryption on (no -T) and its public RSA key.
/// </summary>
/// <param name="Ascp">Full path of ascp.exe (IBM Aspera for Desktop's is at transferd\bin; Aspera Connect's 4.4.3 is too old).</param>
/// <param name="Key">EBI's public Aspera key (KB0011565; the same key pridepy ships as aspera_tokenauth_id_rsa).</param>
/// <param name="Attempts">How many of a file's attempts go to Aspera before HTTPS takes over.</param>
/// <param name="StartGap">Time between two sessions' starts: EBI is not asked for four sessions in one instant.</param>
/// <param name="Stall">A session whose staging folder gains no bytes for this long is killed (zero: never). 2026-10-29: one
/// ascp sat 9.3 h on PXD045861 with 0 bytes and never exited, and the draining driver waited on it the whole time.</param>
public sealed record AsperaClient(string Ascp, string Key, int Attempts = 3, string MaxRate = "300m", TimeSpan StartGap = default,
    TimeSpan Stall = default);

public static class Aspera
{
    /// <summary>PRIDE's CV accession of a file's Aspera location (mzLib PrideArchiveExtensions.AsperaLocationAccession).</summary>
    public const string LocationAccession = "PRIDE:0000468";

    /// <summary>
    /// The file's Aspera source as ascp takes it, <c>user@host:/path</c>, or null when PRIDE lists none. PRIDE writes
    /// <c>prd_ascp@fasp.ebi.ac.uk:pride/data/...</c>, without the leading slash EBI's examples use.
    /// </summary>
    public static string? Source(PrideArchiveFile f)
    {
        var loc = f.PublicFileLocations?.FirstOrDefault(l => l?.Accession == LocationAccession || l?.Name == "Aspera Protocol")?.Value;
        if (string.IsNullOrWhiteSpace(loc) || loc.Split(':', 2) is not [var userHost, var path] || !userHost.Contains('@')) return null;
        return userHost.Trim() + ":/" + path.Trim().TrimStart('/');
    }

    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static DateTime _lastStart = DateTime.MinValue;

    /// <summary>
    /// Downloads <paramref name="f"/> into <paramref name="dir"/> over Aspera: into a private folder first, moved into
    /// place only when ascp reports success, so a broken session never leaves a file under the name a search trusts.
    /// Never resumes: a resume after a killed session gave corrupted files (2026-10-08). The caller verifies the file.
    /// </summary>
    /// <exception cref="HttpIOException">ascp failed or did not run; treated as unavailability, like a dropped transfer.</exception>
    /// <exception cref="TimeoutException">no data arrived for <see cref="AsperaClient.Stall"/>; ascp was killed. Also unavailability.</exception>
    public static async Task<string> DownloadAsync(AsperaClient a, PrideArchiveFile f, string dir, CancellationToken ct)
    {
        string source = Source(f) ?? throw new HttpIOException(HttpRequestError.Unknown, $"{f.FileName}: PRIDE lists no Aspera location");
        string staging = Path.Combine(dir, ".aspera", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await StartGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _lastStart + a.StartGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                _lastStart = DateTime.UtcNow;
            }
            finally { StartGate.Release(); }

            var psi = new ProcessStartInfo(a.Ascp)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = staging,
            };
            foreach (string arg in new[] { "-Q", "-l", a.MaxRate, "-P", "33001", "-i", a.Key, source, staging }) psi.ArgumentList.Add(arg);
            Process p;
            try { p = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new HttpIOException(HttpRequestError.Unknown, $"{f.FileName}: ascp could not start ({a.Ascp}): {e.Message}");
            }
            using (p)
            {
                var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
                bool exited;
                try { exited = await WatchAsync(p.WaitForExitAsync(ct), () => StagedBytes(staging), a.Stall, Poll, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
                if (!exited)
                {
                    long had = StagedBytes(staging);
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    throw new TimeoutException($"{f.FileName}: ascp received no data for {a.Stall.TotalMinutes:0.#} min ({had:N0} bytes); killed");
                }
                string got = Path.Combine(staging, f.FileName);
                if (p.ExitCode != 0 || !File.Exists(got))
                {
                    string said = ((await stderr.ConfigureAwait(false)) + (await stdout.ConfigureAwait(false)))
                        .Split('\n', '\r').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                    throw new HttpIOException(HttpRequestError.ResponseEnded, $"{f.FileName}: ascp exit {p.ExitCode}: {said}");
                }
                string target = Path.Combine(dir, f.FileName);
                File.Move(got, target, overwrite: true);
                return target;
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits for <paramref name="exited"/>: true when it completes, false once <paramref name="progress"/> has not changed
    /// for <paramref name="stall"/> (the caller kills the session). A zero stall waits for the exit alone, as before.
    /// </summary>
    internal static async Task<bool> WatchAsync(Task exited, Func<long> progress, TimeSpan stall, TimeSpan poll, CancellationToken ct)
    {
        if (stall <= TimeSpan.Zero) { await exited.ConfigureAwait(false); return true; }
        long seen = progress();
        var quiet = Stopwatch.StartNew();
        while (true)
        {
            if (await Task.WhenAny(exited, Task.Delay(poll, ct)).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                return true;
            }
            ct.ThrowIfCancellationRequested();
            long now = progress();
            if (now != seen) { seen = now; quiet.Restart(); continue; }
            if (quiet.Elapsed >= stall) return false;
        }
    }

    /// <summary>Bytes under the session's staging folder (ascp names its partial file itself).</summary>
    private static long StagedBytes(string staging)
    {
        try { return Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length); }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }
}
