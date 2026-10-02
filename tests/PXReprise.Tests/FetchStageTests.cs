using System.Net;
using PXReprise.Fetch;
using UsefulProteomicsDatabases;

namespace PXReprise.Tests;

public class FetchStageTests
{
    private static PrideArchiveFile F(string name, long size) => new() { FileName = name, FileSizeBytes = size };

    [Test]
    public void ProbeSpreadTakesTheMedianAndBothEndsOfTheNameOrderButNeverTheSmallest()
    {
        var bySize = new[] { F("blank.raw", 1), F("QEHF_b.raw", 5), F("Fusion_a.raw", 6), F("QEHF_c.raw", 7), F("QEHF_z.raw", 9) };
        var picked = FetchStage.ProbeSpread(bySize).Select(f => f.FileName);
        Assert.That(picked, Is.EqualTo(new[] { "Fusion_a.raw", "QEHF_z.raw" }));   // median (Fusion_a) is also first by name
        Assert.That(FetchStage.ProbeSpread(new[] { F("only.raw", 1) }).Single().FileName, Is.EqualTo("only.raw"));
    }

    [Test]
    public void AllTakesEveryFileInNameOrder() =>
        Assert.That(FetchStage.Choose(new[] { F("b.raw", 1), F("a.raw", 2) }, Pick.All, 1).Select(f => f.FileName),
            Is.EqualTo(new[] { "a.raw", "b.raw" }));

    [TestCase("x failed with status 403 Forbidden for 'u'", true)]    // EBI returned 403 transiently (S52)
    [TestCase("x failed with status 503 Service Unavailable", true)]
    [TestCase("x failed with status 404 Not Found", false)]
    [TestCase("x failed with status 400 Bad Request", false)]
    public void OnlyTransientStatusesAreRetried(string message, bool transient) =>
        Assert.That(FetchStage.IsTransient(new HttpRequestException(message)), Is.EqualTo(transient));

    [Test]
    public void ATruncatedResponseIsTransient() =>   // 11 deposits lost overnight 2026-09-28/29 when it was not
        Assert.That(FetchStage.IsTransient(new HttpIOException(HttpRequestError.ResponseEnded)), Is.True);

    [Test]
    public async Task ATlsDisconnectIsTransientAndADiskErrorIsNot()   // PXD032240 lost a 78-minute fetch on 2026-10-02
    {
        // The real exception: SslStream over a transport that returns 0 bytes throws the IOException EBI's drop produced.
        using var ssl = new System.Net.Security.SslStream(new MemoryStream());
        var eof = Assert.CatchAsync<IOException>(() => ssl.AuthenticateAsClientAsync("example.org"))!;
        Assert.That(eof.GetType(), Is.EqualTo(typeof(IOException)));
        Assert.That(eof.Message, Does.Contain("unexpected EOF or 0 bytes from the transport stream"));
        Assert.That(FetchStage.IsTransient(eof), Is.True);

        // A local disk error is an IOException too, thrown from CoreLib: it must still fail at once.
        string dir = TestSupport.TempDir();
        var disk = Assert.Catch<IOException>(() => File.OpenRead(Path.Combine(dir, "missing.raw")))!;
        Assert.That(FetchStage.IsTransient(disk), Is.False);
    }

    [Test]
    public async Task ADroppedTransferIsRetriedAndAHardFailureIsNot()
    {
        var pride = new FlakyPride { FailuresBeforeSuccess = 2 };
        var (_, _, attempts) = await FetchStage.DownloadWithRetry(pride, F("a.raw", 1), TestSupport.TempDir(), 3, TimeSpan.Zero, CancellationToken.None);
        Assert.That(attempts, Is.EqualTo(3));
        var hard = new FlakyPride { Hard = new MzLibUtil.MzLibException("no such file") };
        Assert.ThrowsAsync<MzLibUtil.MzLibException>(() =>
            FetchStage.DownloadWithRetry(hard, F("a.raw", 1), TestSupport.TempDir(), 3, TimeSpan.Zero, CancellationToken.None));
    }

    [Test]
    public async Task AnEmptyListingIsRetriedBeforeItIsBelieved()
    {
        int calls = 0;
        var (result, attempts) = await FetchStage.ListWithRetry(
            () => Task.FromResult(++calls < 3 ? new List<string>() : new List<string> { "a.raw" }), 4, TimeSpan.Zero, CancellationToken.None);
        Assert.That((result.Count, attempts), Is.EqualTo((1, 3)));
    }

    [Test]
    public async Task AFetchWritesTheManifestAndFlagsASubset()
    {
        string dir = TestSupport.TempDir();
        var pride = new FlakyPride
        {
            Files = { F("s1.raw", 10), F("s2.raw", 20), F("s3.raw", 30), F("big.raw", 9_000_000_000), F("x.sdrf.tsv", 1) },
            Ftp = { "s1.raw", "s2.raw", "s3.raw", "big.raw", "hidden.raw" },
        };
        var r = new FetchRequest("PXD000001", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 2, 3, TimeSpan.Zero, TimeSpan.Zero, dir);
        var manifest = await FetchStage.RunAsync(r, pride, CancellationToken.None);
        Assert.That(manifest["files"]!.AsArray(), Has.Count.EqualTo(3));
        Assert.That(manifest["raw_missing_from_rest_manifest"]![0]!.GetValue<string>(), Is.EqualTo("hidden.raw"));
        string prov = File.ReadAllText(Path.Combine(dir, "02_fetch", "provenance.json"));
        Assert.That(prov, Does.Contain("subset_of_deposit: 3 of 4 raw files").And.Contain("aging-provenance/3"));
    }

    private sealed class FlakyPride : IPrideFiles
    {
        public int FailuresBeforeSuccess;
        public Exception? Hard;
        public List<PrideArchiveFile> Files { get; } = new();
        public List<string> Ftp { get; } = new();
        private int _calls;

        public Task<List<PrideArchiveFile>> ListFilesAsync(string a, CancellationToken ct) => Task.FromResult(Files);
        public Task<List<string>> ListFtpNamesAsync(string a, CancellationToken ct) => Task.FromResult(Ftp);

        public Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct)
        {
            if (Hard is not null) throw Hard;
            // The real shape of an EBI drop: HttpIOException (an IOException), not an HttpRequestException.
            if (Interlocked.Increment(ref _calls) <= FailuresBeforeSuccess)
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely, with at least 334864664 additional bytes expected. (ResponseEnded)");
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, f.FileName);
            File.WriteAllText(p, f.FileName);
            return Task.FromResult(p);
        }
    }
}
