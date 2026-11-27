using PXReprise.Batch;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Qc;

namespace PXReprise.Tests;

// G143, part 2 (the user, 2026-11-27: "the runs could begin as each file finishes its own download"): a probe file is
// searched while the next one downloads, one search at a time.
public class ProbeHeadStartTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly DepositChemistry Words = new(new("trypsin", ChemistrySource.Default), null, new("Carbamidomethyl", ChemistrySource.Default),
        [new CysMod("Carbamidomethyl", "UNIMOD:4", true)], new("label-free", ChemistrySource.Default), null);

    private static IReadOnlyDictionary<string, FragmentationCall> Hcd(string name) =>
        new Dictionary<string, FragmentationCall> { [name] = new("HCD", false, null, "Orbitrap MS2") };

    /// <summary>A head start whose QC passes every file and whose search runs <paramref name="search"/>, with its log.</summary>
    private static (ProbeHeadStart Early, List<string> Log) Start(Func<string, CancellationToken, Task<string>> search,
        Func<string, (bool, IReadOnlyDictionary<string, FragmentationCall>?)>? qc = null, CancellationToken ct = default)
    {
        var log = new List<string>();
        var early = new ProbeHeadStart(Profiles["label-free-dda@16"], Words, qc ?? (f => (true, Hcd(Path.GetFileName(f)))),
            (file, fragmentation, token) => search(file, token), m => { lock (log) log.Add(m); }, ct);
        return (early, log);
    }

    private static async Task Within(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(Patience)) != task) Assert.Fail($"{what} did not happen within {Patience.TotalSeconds} s");
        await task;
    }

    [Test]
    public async Task AFileIsSearchedBeforeTheNextOneIsReady_AndTheSearchesRunOneAtATime()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, most = 0;
        var searched = new List<string>();
        var (early, _) = Start(async (file, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            lock (searched) { searched.Add(Path.GetFileName(file)); most = Math.Max(most, now); }
            firstStarted.TrySetResult();
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref running);
            return file + ".psmtsv";
        });
        await using (early)
        {
            early.Ready("spectra/r1.raw");
            // r2 is "still downloading": it is named only once r1's search has begun.
            await Within(firstStarted.Task, "r1's search");
            early.Ready("spectra/r2.raw");
            early.Ready("spectra/r3.raw");
            var outcomes = await early.FinishAsync();
            Assert.Multiple(() =>
            {
                Assert.That(searched, Is.EqualTo(new[] { "r1.raw", "r2.raw", "r3.raw" }), "in the order they became ready");
                Assert.That(most, Is.EqualTo(1), "one search at a time");
                Assert.That(outcomes.Keys.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "r1.raw", "r2.raw", "r3.raw" }));
                Assert.That(outcomes["r2.raw"].Psms, Is.EqualTo("spectra/r2.raw.psmtsv"));
                Assert.That(outcomes["r2.raw"].Fragmentation!["r2.raw"].DissociationType, Is.EqualTo("HCD"), "searched with its own QC's fragmentation");
                Assert.That(early.RanOutOfTime, Is.False);
            });
        }
    }

    [Test]
    public async Task AFileThatDoesNotPassQcOrWhoseQcFailsIsNotSearchedEarly()
    {
        var searched = new List<string>();
        var (early, log) = Start((file, ct) => { lock (searched) searched.Add(Path.GetFileName(file)); return Task.FromResult(file); },
            qc: f => Path.GetFileName(f) switch
            {
                "r2.raw" => (false, null),
                "r3.raw" => throw new InvalidDataException("the reader saw no scans"),
                _ => (true, null),
            });
        await using (early)
        {
            foreach (string r in new[] { "r1", "r2", "r3" }) early.Ready($"spectra/{r}.raw");
            var outcomes = await early.FinishAsync();
            Assert.Multiple(() =>
            {
                Assert.That(searched, Is.EqualTo(new[] { "r1.raw" }));
                Assert.That(outcomes.Keys, Is.EqualTo(new[] { "r1.raw" }));
                Assert.That(log, Has.Some.EqualTo("r2.raw: not searched early, since it does not pass QC under label-free-dda@16; the probe decides after the download"));
                Assert.That(log, Has.Some.StartsWith("r3.raw: not searched early, since its QC failed (InvalidDataException: the reader saw no scans)"));
                Assert.That(log, Has.Some.EqualTo("r1.raw: searching while the other probe files download"));
            });
        }
    }

    [Test]
    public async Task ASearchThatRunsOutOfTimeEndsTheHeadStart_AndAFailureDoesNot()
    {
        var searched = new List<string>();
        var (early, _) = Start((file, ct) =>
        {
            string name = Path.GetFileName(file);
            lock (searched) searched.Add(name);
            return name switch
            {
                "r1.raw" => throw new InvalidOperationException("the probe search did not finish (exit 1)"),
                "r2.raw" => throw new TimeoutException("the probe search did not finish (exit -1, timed out)"),
                _ => Task.FromResult(file),
            };
        });
        await using (early)
        {
            foreach (string r in new[] { "r1", "r2", "r3" }) early.Ready($"spectra/{r}.raw");
            var outcomes = await early.FinishAsync();
            Assert.Multiple(() =>
            {
                Assert.That(searched, Is.EqualTo(new[] { "r1.raw", "r2.raw" }), "r3 would run out of time too");
                Assert.That(outcomes["r1.raw"].Failure, Is.TypeOf<InvalidOperationException>());
                Assert.That(outcomes["r2.raw"].Failure, Is.TypeOf<TimeoutException>());
                Assert.That(outcomes.ContainsKey("r3.raw"), Is.False);
                Assert.That(early.RanOutOfTime, Is.True);
            });
        }
    }

    [Test]
    public async Task DisposingStopsTheSearchUnderWay()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        var (early, _) = Start(async (file, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return file;
        });
        early.Ready("spectra/r1.raw");
        await Within(started.Task, "r1's search");
        await Within(early.DisposeAsync().AsTask(), "the head start's end");
        Assert.That(cancelled, Is.True, "the probe ended, so its early search was stopped");
    }

    [Test]
    public async Task ItServesOnlyTheProfileTimeLimitAndChemistryItSearchedUnder()
    {
        var (early, _) = Start((file, ct) => Task.FromResult(file));
        await using (early)
        {
            var p = Profiles["label-free-dda@16"];
            Assert.Multiple(() =>
            {
                Assert.That(early.Serves(p, Words), Is.True);
                Assert.That(early.Serves(p, Words with { }), Is.True, "an equal chemistry");
                Assert.That(early.Serves(Profiles["label-free-dda@17"], Words), Is.False, "routed to another profile");
                Assert.That(early.Serves(p with { ProbeSearchMinutes = p.ProbeSearchMinutes + 60 }, Words), Is.False, "another time limit");
                Assert.That(early.Serves(p, Words with { Alkylation = new("none", ChemistrySource.Default) }), Is.False, "the chemistry was read again");
                Assert.That(early.Serves(p, null), Is.False);
            });
        }
    }
}
