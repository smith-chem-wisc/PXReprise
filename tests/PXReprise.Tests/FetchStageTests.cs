using System.Net;
using MzLibUtil;
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

    [Test]
    public void MzLib594sEmptyFtpRootThrowIsReadAsAnEmptyListing()   // pride 005, PXR-S1: the S52 / PXD058248 case
    {
        // The exact shape mzLib 1.0.594 throws: no status code, no inner exception.
        var empty = new HttpRequestException("PRIDE FTP listing of project PXD058248's root directory came back empty although the project exists.");
        Assert.That(PrideFiles.IsEmptyFtpRoot(empty), Is.True);
        Assert.That(PrideFiles.IsEmptyFtpRoot(new HttpRequestException("came back empty", null, System.Net.HttpStatusCode.NotFound)), Is.False);
        Assert.That(PrideFiles.IsEmptyFtpRoot(new HttpRequestException("came back empty", new IOException("reset"))), Is.False);
        Assert.That(FetchStage.IsTransient(empty), Is.False, "the reason the adapter must catch it: our retry would not");
    }

    [Test]
    public async Task AnSdrfDownloadIsRetriedAndAnFtpOnlySdrfIsFlagged()   // pride 007, PXR-S8
    {
        string dir = TestSupport.TempDir();
        var pride = new FlakyPride { FailuresBeforeSuccess = 1, Files = { F("x.sdrf.tsv", 5) }, Ftp = { "x.sdrf.tsv", "y_sdrf.tsv" } };
        var r = new FetchRequest("PXD000001", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 2, 3, TimeSpan.Zero, TimeSpan.Zero, dir);
        var manifest = await FetchStage.RunAsync(r, pride, CancellationToken.None);
        Assert.That(manifest["sdrf"]!.AsArray(), Has.Count.EqualTo(1));
        string prov = File.ReadAllText(Path.Combine(dir, "02_fetch", "provenance.json"));
        Assert.That(prov, Does.Contain("sdrf_ftp_only: y_sdrf.tsv").And.Contain("PXR-S6"));
    }

    // D28: a question's choice of runs is applied first, recorded, and is what "the whole deposit" means afterwards.
    [Test]
    public async Task AQuestionsFileSelectionIsFetchedAndRecorded()
    {
        string dir = TestSupport.TempDir();
        var pride = new FlakyPride { Files = { F("rat_1.raw", 10), F("rat_2.raw", 10), F("macaque_1.raw", 10) }, Ftp = { "rat_1.raw", "rat_2.raw", "macaque_1.raw" } };
        var r = new FetchRequest("PXD027971", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 2, 3, TimeSpan.Zero, TimeSpan.Zero, dir,
            Select: new FileSelection(n => n.StartsWith("rat", StringComparison.Ordinal), "rat files only (aging 044)"));
        var manifest = await FetchStage.RunAsync(r, pride, CancellationToken.None);
        Assert.That((manifest["rest_raw_count"]!.GetValue<int>(), manifest["deposit_raw_count"]!.GetValue<int>()), Is.EqualTo((2, 3)));
        Assert.That(manifest["raw_missing_from_rest_manifest"]!.AsArray(), Is.Empty, "an unselected FTP file is not 'missing'");
        string prov = File.ReadAllText(Path.Combine(dir, "02_fetch", "provenance.json"));
        Assert.That(prov, Does.Contain("subset_by_question: 2 of 3 raw files").And.Contain("rat files only (aging 044)"));
    }

    // 2026-10-08: EBI wrote "<Error><Code>ConnectionClosedException..." into 8 of ~25 PXD012307 files, at PRIDE's size.
    [Test]
    public void TheIntegrityCheckFindsEbisErrorTextAnywhereAndHashesTheWholeFile()
    {
        string dir = TestSupport.TempDir();
        var data = new byte[(1 << 22) + 100];
        string clean = Path.Combine(dir, "clean.raw");
        File.WriteAllBytes(clean, data);
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data));
        string md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data));
        Assert.That(Integrity.Check(clean), Is.EqualTo((expected, md5, (long?)null)));
        Assert.That(Integrity.Problem(clean, md5), Is.Null, "newer deposits: PRIDE's MD5 (32 hex) is checked as MD5");
        Assert.That(Integrity.Problem(clean, new string('0', 32)), Does.StartWith("md5 "));
        Assert.That(Integrity.Problem(clean, expected), Is.Null);
        Assert.That(Integrity.Problem(clean, expected.ToUpperInvariant()), Is.Null, "PRIDE's case does not matter");
        Assert.That(Integrity.Problem(clean, new string('0', 40)), Does.StartWith("sha1 "));

        // Split across the 4 MiB read boundary: the scan must still see it.
        int at = (1 << 22) - 5;
        Integrity.EbiErrorMarker.CopyTo(data.AsSpan(at));
        string split = Path.Combine(dir, "split.raw");
        File.WriteAllBytes(split, data);
        Assert.That(Integrity.Check(split).ErrorAt, Is.EqualTo(at));
        Assert.That(Integrity.Problem(split, null), Does.StartWith($"EBI error response inside the file at byte {at:N0}"),
            "with no SHA-1 from PRIDE the error text alone condemns the file");
    }

    [Test]
    public async Task AFileWithEbisErrorTextInsideIsDownloadedAgain()
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>();
        var pride = new FlakyPride { CorruptFirst = 1, Files = { F("a.raw", 100) }, Ftp = { "a.raw" } };
        var r = new FetchRequest("PXD000001", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 1, 3, TimeSpan.Zero, TimeSpan.Zero, dir);
        var manifest = await FetchStage.RunAsync(r, pride, CancellationToken.None, lines.Add);
        string path = manifest["files"]![0]!["file"]!.GetValue<string>();
        Assert.That(Integrity.Check(path).ErrorAt, Is.Null, "the file kept is the clean second download");
        Assert.That(lines, Has.Some.Contains("is not PRIDE's file (EBI error response inside the file at byte 1"));
        Assert.That(lines, Has.Some.Contains("attempt 2"));
    }

    [Test]
    public async Task AFileWhoseSha1IsNotPridesIsRefusedAfterEveryAttempt()
    {
        string dir = TestSupport.TempDir();
        var pride = new FlakyPride { Files = { F("a.raw", 100) }, Ftp = { "a.raw" } };
        string zeros = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(new byte[100]));
        var good = new FetchRequest("PXD000001", Path.Combine(dir, "ok", "02_fetch"), Pick.All, 60, 5000, ".raw", 1, 2, TimeSpan.Zero, TimeSpan.Zero, dir,
            Sha1Source: (a, ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["a.raw"] = zeros }));
        Assert.That((await FetchStage.RunAsync(good, pride, CancellationToken.None))["files"]!.AsArray(), Has.Count.EqualTo(1));

        var wrong = good with { OutDir = Path.Combine(dir, "bad", "02_fetch"),
            Sha1Source = (a, ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["a.raw"] = new string('f', 40) }) };
        var ex = Assert.ThrowsAsync<HttpRequestException>(() => FetchStage.RunAsync(wrong, pride, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("2 attempts all failed").And.Contain("PRIDE lists ffff"));
    }

    [Test]
    public async Task ACorruptedFileAlreadyOnDiskIsReplaced()
    {
        string dir = TestSupport.TempDir();
        string spectra = Path.Combine(dir, "02_fetch", "spectra");
        Directory.CreateDirectory(spectra);
        var bad = new byte[100];
        Integrity.EbiErrorMarker.CopyTo(bad.AsSpan(50));
        File.WriteAllBytes(Path.Combine(spectra, "a.raw"), bad);   // the right size, from an earlier fetch or by hand
        var pride = new FlakyPride { Files = { F("a.raw", 100) }, Ftp = { "a.raw" } };
        var r = new FetchRequest("PXD000001", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 1, 3, TimeSpan.Zero, TimeSpan.Zero, dir);
        await FetchStage.RunAsync(r, pride, CancellationToken.None);
        Assert.That(Integrity.Check(Path.Combine(spectra, "a.raw")).ErrorAt, Is.Null);
    }

    // 2026-10-08: Aspera first (about 5x HTTPS here), HTTPS after, so a broken Aspera setup never costs a deposit.
    [Test]
    public void AsperasSourceIsReadFromPridesLocationWithTheLeadingSlashEbiUses()
    {
        var f = F("x.raw", 10);
        Assert.That(Aspera.Source(f), Is.Null, "no Aspera location listed");
        f.PublicFileLocations.Add(new CvParam { Accession = "PRIDE:0000469", Name = "FTP Protocol", Value = "ftp://ftp.pride.ebi.ac.uk/pride/data/archive/2019/01/PXD012307/x.raw" });
        f.PublicFileLocations.Add(new CvParam { Accession = "PRIDE:0000468", Name = "Aspera Protocol", Value = "prd_ascp@fasp.ebi.ac.uk:pride/data/archive/2019/01/PXD012307/x.raw" });
        Assert.That(Aspera.Source(f), Is.EqualTo("prd_ascp@fasp.ebi.ac.uk:/pride/data/archive/2019/01/PXD012307/x.raw"));
    }

    [Test]
    public async Task AnAsperaClientThatCannotRunFallsBackToHttpsAfterItsAttempts()
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>();
        var f = F("a.raw", 100);
        f.PublicFileLocations.Add(new CvParam { Accession = "PRIDE:0000468", Name = "Aspera Protocol", Value = "prd_ascp@fasp.ebi.ac.uk:pride/data/a.raw" });
        var pride = new FlakyPride { Files = { f }, Ftp = { "a.raw" } };
        var r = new FetchRequest("PXD000001", Path.Combine(dir, "02_fetch"), Pick.All, 60, 5000, ".raw", 1, 4, TimeSpan.Zero, TimeSpan.Zero, dir,
            Aspera: new AsperaClient(Path.Combine(dir, "no_such_ascp.exe"), Path.Combine(dir, "key"), Attempts: 2));
        var manifest = await FetchStage.RunAsync(r, pride, CancellationToken.None, lines.Add);
        Assert.That(File.Exists(manifest["files"]![0]!["file"]!.GetValue<string>()), "HTTPS delivered it");
        Assert.That(lines.Count(l => l.Contains("ascp could not start")), Is.EqualTo(2), "two Aspera attempts, then HTTPS");
        Assert.That(lines, Has.Some.Contains("attempt 3"));
        Assert.That(Directory.Exists(Path.Combine(dir, "02_fetch", "spectra", ".aspera")) &&
                    Directory.EnumerateFileSystemEntries(Path.Combine(dir, "02_fetch", "spectra", ".aspera")).Any(), Is.False,
            "no Aspera staging folder is left behind");
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

        /// <summary>The first location of each file asked for: which host the download was sent to.</summary>
        public List<string?> Locations { get; } = new();

        public Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct)
        {
            lock (Locations) Locations.Add(f.PublicFileLocations?.FirstOrDefault()?.Value);
            if (Hard is not null) throw Hard;
            // The real shape of an EBI drop: HttpIOException (an IOException), not an HttpRequestException.
            if (Interlocked.Increment(ref _calls) <= FailuresBeforeSuccess)
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely, with at least 334864664 additional bytes expected. (ResponseEnded)");
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, f.FileName);
            if (File.Exists(p)) return Task.FromResult(p);   // mzLib's overwrite: false skips on existence alone
            var bytes = new byte[f.FileSizeBytes - Short];
            // EBI's storage error written into the data, the file keeping its size (2026-10-08, PXD012307).
            if (Interlocked.Increment(ref _writes) <= CorruptFirst && bytes.Length > 40)
                "<Error><Code>ConnectionClosedException"u8.CopyTo(bytes.AsSpan(1));
            File.WriteAllBytes(p, bytes);
            return Task.FromResult(p);
        }

        /// <summary>Bytes each download comes up short by.</summary>
        public int Short;

        /// <summary>How many downloads arrive with EBI's error response inside them.</summary>
        public int CorruptFirst;
        private int _writes;
    }

    [Test]
    public async Task AFileCutShortOnDiskIsDownloadedAgain()   // first-run 2026-10-03: raws downloaded by hand into spectra/
    {
        string dir = TestSupport.TempDir();
        File.WriteAllBytes(Path.Combine(dir, "a.raw"), new byte[40]);
        var lines = new List<string>();
        var (path, _, attempts) = await FetchStage.DownloadWithRetry(new FlakyPride(), F("a.raw", 100), dir, 3, TimeSpan.Zero, CancellationToken.None, log: lines.Add);
        Assert.That((new FileInfo(path).Length, attempts), Is.EqualTo((100L, 1)));
        Assert.That(lines[0], Does.Contain("on disk is 40 bytes, PRIDE lists 100"));
        Assert.That(lines.Skip(1), Is.EqualTo(new[] { "a.raw arrived over ftp.pride HTTPS (attempt 1): 0 MB in 0 s" }));
    }

    // pride 014 (PXR-W4): on the same files in the same minutes, ftp.pride's HTTPS copies came back damaged 2 of 5 times
    // and the Globus host's 0 of 5. The Globus route is the same file with the Globus URL as its only location.
    private static PrideArchiveFile Located(string name, long size)
    {
        var f = F(name, size);
        f.PublicFileLocations.Add(new CvParam { Accession = "PRIDE:0000469", Name = "FTP Protocol", Value = $"ftp://ftp.pride.ebi.ac.uk/pride/data/archive/2025/01/PXD058855/{name}" });
        f.PublicFileLocations.Add(new CvParam { Accession = "PRIDE:0000468", Name = "Aspera Protocol", Value = $"prd_ascp@fasp.ebi.ac.uk:pride/data/archive/2025/01/PXD058855/{name}" });
        return f;
    }

    private const string GlobusUrl = "https://g-a8b222.dd271.03c0.data.globus.org/pride/data/archive/2025/01/PXD058855/";

    [Test]
    public void TheGlobusRouteIsTheSamePathOnTheGlobusHost()
    {
        var g = PrideHosts.ViaGlobus(Located("K80_E1.raw", 100))!;
        Assert.Multiple(() =>
        {
            Assert.That(g.PublicFileLocations.Select(l => l.Value), Is.EqualTo(new[] { GlobusUrl + "K80_E1.raw" }));
            Assert.That((g.FileName, g.FileSizeBytes), Is.EqualTo(("K80_E1.raw", 100L)));
            Assert.That(PrideHosts.ViaGlobus(F("x.raw", 1)), Is.Null, "no FTP path to map: mzLib chooses");
            Assert.That((PrideHosts.Parse(null), PrideHosts.Parse("FTP")), Is.EqualTo((PrideHttps.Globus, PrideHttps.Ftp)));
            Assert.Throws<ArgumentException>(() => PrideHosts.Parse("aspera"));
        });
    }

    [Test]
    public async Task ADamagedAsperaCopySendsTheNextAttemptToGlobusAtOnce()
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>();
        var pride = new FlakyPride();
        int asperaCalls = 0;
        // EBI's error text written into an Aspera copy of the right size (2026-10-12: DDPC4E1_3 was damaged 3 times running).
        Task<string> DamagedAspera(AsperaClient a, PrideArchiveFile f, string d, CancellationToken ct)
        {
            asperaCalls++;
            var bytes = new byte[f.FileSizeBytes];
            "<Error><Code>ConnectionClosedException"u8.CopyTo(bytes.AsSpan(1));
            string p = Path.Combine(d, f.FileName);
            File.WriteAllBytes(p, bytes);
            return Task.FromResult(p);
        }
        var (path, _, attempts) = await FetchStage.DownloadWithRetry(pride, Located("DDPC4E1_3.raw", 100), dir, 8, TimeSpan.Zero, CancellationToken.None,
            log: lines.Add, verify: true, aspera: new AsperaClient("ascp.exe", "key", Attempts: 3), https: PrideHttps.Globus, asperaDownload: DamagedAspera);
        Assert.Multiple(() =>
        {
            Assert.That((asperaCalls, attempts), Is.EqualTo((1, 2)), "one damaged Aspera copy, not three");
            Assert.That(pride.Locations, Is.EqualTo(new[] { GlobusUrl + "DDPC4E1_3.raw" }));
            Assert.That(lines, Has.Some.StartsWith("DDPC4E1_3.raw arrived over Aspera (attempt 1)"));
            Assert.That(lines, Has.Some.Contains("the next attempt goes over Globus HTTPS"));
            Assert.That(lines, Has.Some.StartsWith("DDPC4E1_3.raw arrived over Globus HTTPS (attempt 2)"));
            Assert.That(Integrity.Check(path).ErrorAt, Is.Null);
        });
    }

    [Test]
    public async Task AGlobusAttemptThatFailsToDeliverIsFollowedByOneFromFtpPride()
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>();
        var pride = new FlakyPride { FailuresBeforeSuccess = 1 };   // the Globus host drops the first download
        var (_, _, attempts) = await FetchStage.DownloadWithRetry(pride, Located("a.raw", 100), dir, 8, TimeSpan.Zero, CancellationToken.None,
            log: lines.Add, verify: true, https: PrideHttps.Globus);
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(pride.Locations, Is.EqualTo(new[] { GlobusUrl + "a.raw", "ftp://ftp.pride.ebi.ac.uk/pride/data/archive/2025/01/PXD058855/a.raw" }));
        Assert.That(lines, Has.Some.StartsWith("a.raw arrived over ftp.pride HTTPS (attempt 2)"));

        var ftpOnly = new FlakyPride();
        await FetchStage.DownloadWithRetry(ftpOnly, Located("b.raw", 100), dir, 8, TimeSpan.Zero, CancellationToken.None, verify: true, https: PrideHttps.Ftp);
        Assert.That(ftpOnly.Locations, Is.EqualTo(new[] { "ftp://ftp.pride.ebi.ac.uk/pride/data/archive/2025/01/PXD058855/b.raw" }), "pride_https = \"ftp\" never uses Globus");
    }

    [Test]
    public void ADownloadThatKeepsComingUpShortIsUnavailabilityNotSuccess()
    {
        string dir = TestSupport.TempDir();
        var lines = new List<string>();
        var e = Assert.ThrowsAsync<HttpRequestException>(() =>
            FetchStage.DownloadWithRetry(new FlakyPride { Short = 1 }, F("a.raw", 100), dir, 2, TimeSpan.Zero, CancellationToken.None, log: lines.Add));
        Assert.That(FetchStage.IsTransient(e!), Is.True);
        Assert.That(File.Exists(Path.Combine(dir, "a.raw")), Is.False);
        Assert.That(lines.Where(l => l.StartsWith("retry ", StringComparison.Ordinal)),
            Has.Exactly(2).Items.And.All.Contains("HttpIOException: a.raw: received 99 bytes, PRIDE lists 100"));
        Assert.That(lines.Count(l => l.Contains(" arrived over ftp.pride HTTPS ")), Is.EqualTo(2), "each short copy is a transfer, logged as one");
    }

    [Test]
    public async Task AStalledDownloadIsAbandonedAndRetried()   // first-run 2026-10-03: 6 h on two frozen .partial files
    {
        string dir = TestSupport.TempDir();
        var pride = new StallingPride { Stalls = 1 };
        var lines = new List<string>();
        var (path, _, attempts) = await FetchStage.DownloadWithRetry(pride, F("a.raw", 10), dir, 3, TimeSpan.Zero, CancellationToken.None,
            stallAfter: TimeSpan.FromMilliseconds(300), log: lines.Add, poll: TimeSpan.FromMilliseconds(50), abandonGrace: TimeSpan.FromMilliseconds(200));
        Assert.That((File.Exists(path), attempts), Is.EqualTo((true, 2)));
        Assert.That(lines.Single(l => l.StartsWith("retry ", StringComparison.Ordinal)), Does.Contain("TimeoutException: a.raw: no data for"));
    }

    [Test]
    public void AStallThatIgnoresCancellationCannotHangTheFetch()
    {
        var pride = new StallingPride { Stalls = 99, IgnoreCancellation = true };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.ThrowsAsync<HttpRequestException>(() => FetchStage.DownloadWithRetry(pride, F("a.raw", 10), TestSupport.TempDir(), 2,
            TimeSpan.Zero, CancellationToken.None, stallAfter: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromMilliseconds(50),
            abandonGrace: TimeSpan.FromMilliseconds(100)));
        Assert.That(e!.InnerException, Is.TypeOf<TimeoutException>());
        Assert.That(FetchStage.IsTransient(e), Is.True);
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Writes a few bytes to the .partial, then waits forever: the shape of the 2026-10-03 hang.</summary>
    private sealed class StallingPride : IPrideFiles
    {
        public int Stalls;
        public bool IgnoreCancellation;
        private int _calls;

        public Task<List<PrideArchiveFile>> ListFilesAsync(string a, CancellationToken ct) => Task.FromResult(new List<PrideArchiveFile>());
        public Task<List<string>> ListFtpNamesAsync(string a, CancellationToken ct) => Task.FromResult(new List<string>());

        public async Task<string> DownloadAsync(PrideArchiveFile f, string dir, CancellationToken ct)
        {
            string p = Path.Combine(dir, f.FileName);
            if (Interlocked.Increment(ref _calls) > Stalls)
            {
                File.WriteAllBytes(p, new byte[f.FileSizeBytes]);
                return p;
            }
            File.WriteAllBytes(p + ".partial", new byte[3]);
            await Task.Delay(Timeout.Infinite, IgnoreCancellation ? CancellationToken.None : ct);
            return p;
        }
    }
}
