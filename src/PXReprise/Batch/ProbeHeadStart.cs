using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Qc;

namespace PXReprise.Batch;

/// <summary>
/// The probe's searches, started while the other probe files still download (G143, part 2). The user, 2026-11-27: "the
/// runs could begin as each file finishes its own download. we don't need the three together necesseraily".
/// <para>
/// Each file the fetch names as ready (<see cref="Fetch.FetchRequest.OnFileReady"/>) is checked by its own QC, then
/// searched alone. This uses the profile and the chemistry that the probe had before the download. The files are
/// searched one at a time, since each search uses every core. Downloads and searches overlap; searches do not.
/// </para>
/// <para>
/// This is a head start only. Every decision is still taken after the download, on all the probe files, as before. A
/// result is used only when the profile, the chemistry and the file's fragmentation are those the probe then searches
/// under (<see cref="Serves"/>). Otherwise it is set aside, and the file is searched again.
/// </para>
/// <para>
/// A file whose QC does not pass under the profile is not searched early: the probe's own routing decides what becomes
/// of it. A search that runs out of time ends the head start, as it ends the probe's own searching.
/// </para>
/// </summary>
internal sealed class ProbeHeadStart : IAsyncDisposable
{
    /// <summary>One file's early search: the fragmentation it was searched with, and its PSM file or its failure.</summary>
    internal sealed record Outcome(IReadOnlyDictionary<string, FragmentationCall>? Fragmentation, string? Psms, Exception? Failure, double Minutes);

    private readonly Func<string, (bool Pass, IReadOnlyDictionary<string, FragmentationCall>? Fragmentation)> _qc;
    private readonly Func<string, IReadOnlyDictionary<string, FragmentationCall>?, CancellationToken, Task<string>> _search;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Outcome> _outcomes = new(StringComparer.Ordinal);
    private readonly Task _worker;

    /// <summary>The profile the early searches use, with the probe's time limit.</summary>
    public Profile Profile { get; }

    /// <summary>The chemistry the early searches use.</summary>
    public DepositChemistry? Chemistry { get; }

    /// <summary>True once an early search used its whole time limit; no file is searched early after it.</summary>
    public bool RanOutOfTime { get; private set; }

    /// <param name="qc">Checks one file. It says whether the file passes under <paramref name="profile"/>, and gives its
    /// fragmentation for the search.</param>
    /// <param name="search">Searches one file with that fragmentation, and returns its <c>AllPSMs.psmtsv</c>.</param>
    public ProbeHeadStart(Profile profile, DepositChemistry? chemistry,
        Func<string, (bool Pass, IReadOnlyDictionary<string, FragmentationCall>? Fragmentation)> qc,
        Func<string, IReadOnlyDictionary<string, FragmentationCall>?, CancellationToken, Task<string>> search,
        Action<string> log, CancellationToken ct)
    {
        (Profile, Chemistry, _qc, _search, _log) = (profile, chemistry, qc, search, log);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>A probe file is on disk and verified. It is checked and searched when the search before it is done.</summary>
    public void Ready(string file) => _ready.Writer.TryWrite(file);

    /// <summary>True when the probe searches under the same profile, time limit and chemistry as the early searches.</summary>
    public bool Serves(Profile profile, DepositChemistry? chemistry) =>
        profile.Key == Profile.Key && profile.ProbeSearchMinutes == Profile.ProbeSearchMinutes
        && (ReferenceEquals(chemistry, Chemistry) || Equals(chemistry, Chemistry));

    /// <summary>
    /// Every file named ready is checked and, where it passes, searched. Then it gives each searched file's outcome, by
    /// file name. Call it once the fetch has ended, so that no file is named after it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Outcome>> FinishAsync()
    {
        _ready.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        return new Dictionary<string, Outcome>(_outcomes, StringComparer.Ordinal);
    }

    /// <summary>Stops the search under way and searches nothing more; for a probe that ends or routes elsewhere.</summary>
    public async ValueTask DisposeAsync()
    {
        _ready.Writer.TryComplete();
        if (!_worker.IsCompleted) _cts.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (string file in _ready.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                string name = Path.GetFileName(file);
                if (RanOutOfTime) continue;
                (bool Pass, IReadOnlyDictionary<string, FragmentationCall>? Fragmentation) checkedFile;
                try { checkedFile = await Task.Run(() => _qc(file), _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    _log($"{name}: not searched early, since its QC failed ({ex.GetType().Name}: {ex.Message}); the probe checks it again after the download");
                    continue;
                }
                if (!checkedFile.Pass)
                {
                    _log($"{name}: not searched early, since it does not pass QC under {Profile.Key}; the probe decides after the download");
                    continue;
                }
                _log($"{name}: searching while the other probe files download");
                var clock = Stopwatch.StartNew();
                try
                {
                    string psms = await _search(file, checkedFile.Fragmentation, _cts.Token).ConfigureAwait(false);
                    _outcomes[name] = new(checkedFile.Fragmentation, psms, null, clock.Elapsed.TotalMinutes);
                }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    _outcomes[name] = new(checkedFile.Fragmentation, null, ex, clock.Elapsed.TotalMinutes);
                    RanOutOfTime = ex is TimeoutException;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }
}
