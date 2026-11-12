using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Storage;
using ParquetSharp;

namespace DataRepo.Tests;

/// <summary>
/// The store interface and <c>push</c>/<c>pull</c>'s copy (<see cref="StoreTransfer"/>). Local-to-local always runs.
/// The same checks against S3 run only when <c>DATAREPO_S3_TEST</c> names a scratch location
/// (<c>s3://bucket/some/scratch/prefix</c>) and credentials are available (e.g. <c>AWS_PROFILE</c>); each run writes under
/// a fresh sub-prefix and deletes it afterwards.
/// </summary>
[TestFixture]
public class StorageTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "datarepo-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Sub(string name) => Path.Combine(_dir, name);

    private string FileWith(string text)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A bundle in <paramref name="store"/>: one two-row table, one recorded source, and an unrecorded extra
    /// file, with a bundle.json recording them as ingest does.</summary>
    internal static StoreItem WriteBundle(string store, string dataset = "PXD000001", string bundleId = "0123456789abcdef",
        long recordedRows = 2, string? recordedSourceSha = null, bool hashTables = false, string? recordedTableSha = null)
    {
        var folder = Path.Combine(store, dataset, bundleId);
        Directory.CreateDirectory(Path.Combine(folder, "sources"));
        using (var writer = new ParquetFileWriter(Path.Combine(folder, "runs.parquet"), new Column[] { new Column<int>("x") }))
        {
            using var group = writer.AppendRowGroup();
            using var column = group.NextColumn().LogicalWriter<int>();
            column.WriteBatch([1, 2]);
            writer.Close();
        }
        var source = Path.Combine(folder, "sources", "results.txt");
        File.WriteAllText(source, "All target PSMs within 1% FDR: 2\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not named by bundle.json\n", new UTF8Encoding(false));
        var manifest = new Dictionary<string, object>
        {
            ["bundle_id"] = bundleId,
            ["dataset_id"] = dataset,
            ["tables"] = hashTables
                ? new Dictionary<string, object>
                {
                    ["runs"] = new Dictionary<string, object>
                    {
                        ["rows"] = recordedRows,
                        ["sha256"] = recordedTableSha ?? BundleWriter.Sha256File(Path.Combine(folder, "runs.parquet")),
                    },
                    ["samples"] = new Dictionary<string, object> { ["rows"] = 0 },
                }
                : new Dictionary<string, object> { ["runs"] = recordedRows, ["samples"] = 0L },
            ["sources"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "results", ["sha256"] = recordedSourceSha ?? BundleWriter.Sha256File(source), ["bundle_path"] = "sources/results.txt" },
            },
        };
        File.WriteAllText(Path.Combine(folder, BundleWriter.ManifestName), JsonSerializer.Serialize(manifest) + "\n", new UTF8Encoding(false));
        return StoreItem.Bundle(dataset, bundleId);
    }

    private static string[] Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();

    // ---- keys and the local store ----------------------------------------------------------------------------------

    [TestCase("../x")]
    [TestCase("a/../b")]
    [TestCase("/abs")]
    [TestCase("a\\b")]
    [TestCase("a//b")]
    [TestCase("C:/x")]
    [TestCase("a/")]
    [TestCase("")]
    public void AKeyCannotLeaveTheStore(string key)
    {
        Assert.Throws<ArgumentException>(() => StoreKey.Check(key));
    }

    [Test]
    public async Task WriteOnceWritesThenAcceptsTheSameContentAndRefusesOther()
    {
        using var store = new LocalStore(Sub("store"));
        var a = FileWith("one");
        var b = FileWith("two");
        Assert.That(await store.WriteOnceAsync("d/x.txt", a, BundleWriter.Sha256File(a)), Is.EqualTo(WriteOutcome.Written));
        Assert.That(await store.WriteOnceAsync("d/x.txt", a, BundleWriter.Sha256File(a)), Is.EqualTo(WriteOutcome.AlreadyPresent));
        var e = Assert.ThrowsAsync<StoreConflictException>(() => store.WriteOnceAsync("d/x.txt", b, BundleWriter.Sha256File(b)));
        Assert.That(e!.Message, Does.Contain("nothing in a store is replaced"));
        Assert.That(File.ReadAllText(Path.Combine(Sub("store"), "d", "x.txt")), Is.EqualTo("one"));
    }

    [Test]
    public void WriteOnceRefusesBytesThatDoNotMatchTheirHash()
    {
        using var store = new LocalStore(Sub("store"));
        var a = FileWith("one");
        Assert.ThrowsAsync<DataRepoException>(() => store.WriteOnceAsync("x.txt", a, new string('0', 64)));
        Assert.That(Directory.EnumerateFiles(Sub("store"), "*", SearchOption.AllDirectories), Is.Empty, "no partial file is left");
    }

    [Test]
    public async Task APartialFileIsNeverListed()
    {
        using var store = new LocalStore(Sub("store"));
        Directory.CreateDirectory(Path.Combine(Sub("store"), "d"));
        File.WriteAllText(Path.Combine(Sub("store"), "d", ".x.txt" + LocalStore.PartialMarker + "1234"), "half");
        var a = FileWith("one");
        await store.WriteOnceAsync("d/y.txt", a, BundleWriter.Sha256File(a));
        var keys = new List<string>();
        await foreach (var entry in store.ListAsync("d/")) keys.Add(entry.Key);
        Assert.That(keys, Is.EqualTo(new[] { "d/y.txt" }));
    }

    // ---- push and pull's copy --------------------------------------------------------------------------------------

    [Test]
    public async Task ABundleIsCopiedWholeAndASecondCopyFindsItPresent()
    {
        var bundle = WriteBundle(Sub("here"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        Assert.That(await StoreTransfer.FindAsync(from), Is.EqualTo(new[] { bundle }));
        Assert.That(await StoreTransfer.CopyAsync(from, to, bundle), Is.EqualTo(TransferOutcome.Copied));
        Assert.That(Files(Sub("there")), Is.EqualTo(Files(Sub("here"))));
        foreach (var file in Files(Sub("here")))
            Assert.That(File.ReadAllBytes(Path.Combine(Sub("there"), file)), Is.EqualTo(File.ReadAllBytes(Path.Combine(Sub("here"), file))), file);
        Assert.That(await StoreTransfer.CopyAsync(from, to, bundle), Is.EqualTo(TransferOutcome.Present));
    }

    [Test]
    public async Task AnInterruptedCopyLeavesNoBundleAndARerunFinishesIt()
    {
        var bundle = WriteBundle(Sub("here"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        using var failing = new FailAfter(to, writes: 1);
        Assert.ThrowsAsync<IOException>(() => StoreTransfer.CopyAsync(from, failing, bundle));
        Assert.That(File.Exists(Path.Combine(Sub("there"), bundle.Group, bundle.Id, "notes.txt")), Is.True, "the first file was written");
        Assert.That(await StoreTransfer.FindAsync(to), Is.Empty, "without bundle.json no reader counts the bundle");

        Assert.That(await StoreTransfer.CopyAsync(from, to, bundle), Is.EqualTo(TransferOutcome.Copied));
        Assert.That(Files(Sub("there")), Is.EqualTo(Files(Sub("here"))));
    }

    [Test]
    public async Task BundleJsonIsWrittenLast()
    {
        var bundle = WriteBundle(Sub("here"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        using var recording = new FailAfter(to, writes: int.MaxValue);
        await StoreTransfer.CopyAsync(from, recording, bundle);
        Assert.That(recording.Written.Last(), Is.EqualTo(bundle.Record));
        Assert.That(recording.Written, Has.Count.EqualTo(Files(Sub("here")).Length));
    }

    [Test]
    public void ATableWhoseRowsDoNotMatchItsRecordIsNotCopied()
    {
        var bundle = WriteBundle(Sub("here"), recordedRows: 3);
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        var e = Assert.ThrowsAsync<DataRepoException>(() => StoreTransfer.CopyAsync(from, to, bundle));
        Assert.That(e!.Message, Does.Contain("runs.parquet holds 2 rows; its bundle.json records 3"));
        Assert.That(File.Exists(Path.Combine(Sub("there"), bundle.Group, bundle.Id, BundleWriter.ManifestName)), Is.False);
    }

    [Test]
    public void ASourceWhoseHashDoesNotMatchItsRecordIsNotCopied()
    {
        var bundle = WriteBundle(Sub("here"), recordedSourceSha: new string('a', 64));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        var e = Assert.ThrowsAsync<DataRepoException>(() => StoreTransfer.CopyAsync(from, to, bundle));
        Assert.That(e!.Message, Does.Contain("sources/results.txt has sha256"));
        Assert.That(File.Exists(Path.Combine(Sub("there"), bundle.Group, bundle.Id, BundleWriter.ManifestName)), Is.False);
    }

    [Test]
    public void ABundleInTheWrongFolderIsRefused()
    {
        WriteBundle(Sub("here"), dataset: "PXD000001", bundleId: "0123456789abcdef");
        Directory.Move(Path.Combine(Sub("here"), "PXD000001"), Path.Combine(Sub("here"), "PXD000002"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        var e = Assert.ThrowsAsync<DataRepoException>(() => StoreTransfer.CopyAsync(from, to, StoreItem.Bundle("PXD000002", "0123456789abcdef")));
        Assert.That(e!.Message, Does.Contain("must be <dataset_id>/<bundle_id>"));
    }

    [Test]
    public async Task ADestinationFileWithOtherContentIsAConflict()
    {
        var bundle = WriteBundle(Sub("here"));
        Directory.CreateDirectory(Path.Combine(Sub("there"), bundle.Group, bundle.Id));
        File.WriteAllText(Path.Combine(Sub("there"), bundle.Group, bundle.Id, "notes.txt"), "something else");
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        Assert.ThrowsAsync<StoreConflictException>(() => StoreTransfer.CopyAsync(from, to, bundle));
    }

    [Test]
    public async Task FindTakesOnlyTheNamedDatasetsAndSkipsWorkFolders()
    {
        WriteBundle(Sub("here"), dataset: "PXD000001");
        var b = WriteBundle(Sub("here"), dataset: "PXD000002");
        WriteBundle(Sub("here"), dataset: "_engine");
        using var store = new LocalStore(Sub("here"));
        Assert.That(await StoreTransfer.FindAsync(store, ["PXD000002"]), Is.EqualTo(new[] { b }));
        Assert.That((await StoreTransfer.FindAsync(store)).Select(x => x.Dataset), Is.EqualTo(new[] { "PXD000001", "PXD000002" }));
    }

    [Test]
    public async Task ATableHashInTheRecordIsChecked()
    {
        var good = WriteBundle(Sub("here"), dataset: "PXD000001", hashTables: true);
        var bad = WriteBundle(Sub("here"), dataset: "PXD000002", hashTables: true, recordedTableSha: new string('b', 64));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        Assert.That(await StoreTransfer.CopyAsync(from, to, good), Is.EqualTo(TransferOutcome.Copied));
        var e = Assert.ThrowsAsync<DataRepoException>(() => StoreTransfer.CopyAsync(from, to, bad));
        Assert.That(e!.Message, Does.Contain("runs.parquet has sha256"));
        Assert.That(await to.ExistsAsync(bad.Record), Is.False);
    }

    /// <summary>An engine output and a study delivery as the runner and <c>datarepo study</c> leave them, plus an engine
    /// output still being written (its staging folder).</summary>
    private void WriteSideFolders(string store)
    {
        void Put(string relative, string text)
        {
            var path = Path.Combine(store, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        Put("_engine/go/a1b2c3/go_terms.tsv", "term\n");
        Put("_engine/go/a1b2c3/run.json", "{\"engine\": \"go\"}\n");
        Put("_engine/go/.d4e5f6.writing/run.json", "{\"engine\": \"go\"}\n");
        Put("_study/aging/0011223344556677/curated_samples.parquet", "not really parquet\n");
        Put("_study/aging/0011223344556677/study.json", "{\"layer\": \"aging\"}\n");
    }

    [Test]
    public async Task EngineOutputsAndStudyDeliveriesTravelWithTheBundles()
    {
        var bundle = WriteBundle(Sub("here"));
        WriteSideFolders(Sub("here"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        var items = await StoreTransfer.FindAsync(from);
        Assert.That(items, Is.EqualTo(new[]
        {
            bundle,
            new StoreItem(ItemKind.Engine, "go", "a1b2c3"),
            new StoreItem(ItemKind.Study, "aging", "0011223344556677"),
        }), "the engine output still being written is not found");
        Assert.That(await StoreTransfer.FindAsync(from, ["PXD000001"]), Is.EqualTo(new[] { bundle }), "named datasets: their bundles only");

        using var recording = new FailAfter(to, writes: int.MaxValue);
        foreach (var item in items) await StoreTransfer.CopyAsync(from, recording, item);
        Assert.That(recording.Written.IndexOf("_engine/go/a1b2c3/run.json"), Is.GreaterThan(recording.Written.IndexOf("_engine/go/a1b2c3/go_terms.tsv")));
        Assert.That(recording.Written.IndexOf("_study/aging/0011223344556677/study.json"),
            Is.GreaterThan(recording.Written.IndexOf("_study/aging/0011223344556677/curated_samples.parquet")));
        Assert.That(Files(Sub("there")), Is.EqualTo(Files(Sub("here")).Where(f => !f.Contains("/.d4e5f6.writing/")).ToArray()));
    }

    [Test]
    public void RecordNamesMatchTheWritersOwn()
    {
        Assert.That(StoreItem.EngineDir, Is.EqualTo(DataRepo.Catalog.Runner.EngineDir));
        Assert.That(StoreItem.EngineRecord, Is.EqualTo(DataRepo.Catalog.Runner.RunRecord));
        Assert.That(StoreItem.StudyDir, Is.EqualTo(DataRepo.Study.StudyWriter.StudyDir));
        Assert.That(StoreItem.StudyRecord, Is.EqualTo(DataRepo.Study.StudyWriter.StudyBundleManifest));
    }

    [Test]
    public async Task ADryRunWritesNothing()
    {
        var bundle = WriteBundle(Sub("here"));
        using var from = new LocalStore(Sub("here"));
        using var to = new LocalStore(Sub("there"));
        Assert.That(await StoreTransfer.CopyAsync(from, to, bundle, dryRun: true), Is.EqualTo(TransferOutcome.WouldCopy));
        Assert.That(Directory.Exists(Sub("there")), Is.False);
    }

    [Test]
    public void PushAndPullFromTheCommandLine()
    {
        WriteBundle(Sub("here"));
        Assert.That(DataRepo.Cli.Cli.Run(["push", Sub("shared"), "--store", Sub("here")]), Is.EqualTo(0));
        Assert.That(DataRepo.Cli.Cli.Run(["pull", Sub("shared"), "--store", Sub("served")]), Is.EqualTo(0));
        Assert.That(Files(Sub("served")), Is.EqualTo(Files(Sub("here"))));
        Assert.That(DataRepo.Cli.Cli.Run(["push", Sub("shared"), "PXD999999", "--store", Sub("here")]), Is.EqualTo(1), "an accession with no bundle");
    }

    /// <summary>A store that throws on the write after the first <c>writes</c>, and records the keys written.</summary>
    private sealed class FailAfter(IStore inner, int writes) : IStore
    {
        public List<string> Written { get; } = [];

        public string Location => inner.Location;
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public IAsyncEnumerable<StoreEntry> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);
        public string? LocalFile(string key) => inner.LocalFile(key);
        public Task DownloadAsync(string key, string localPath, CancellationToken ct = default) => inner.DownloadAsync(key, localPath, ct);
        public Task ReplaceAsync(string key, byte[] content, CancellationToken ct = default) => inner.ReplaceAsync(key, content, ct);

        public Task<WriteOutcome> WriteOnceAsync(string key, string localPath, string sha256, CancellationToken ct = default)
        {
            if (Written.Count >= writes) throw new IOException("the connection dropped");
            Written.Add(key);
            return inner.WriteOnceAsync(key, localPath, sha256, ct);
        }

        public void Dispose() { }
    }
}

/// <summary>The same contract against S3. Skipped unless <c>DATAREPO_S3_TEST</c> is set (see <see cref="StorageTests"/>).</summary>
[TestFixture]
[Category("S3")]
public class S3StorageTests
{
    private string _dir = null!;
    private S3Store _store = null!;

    [SetUp]
    public void SetUp()
    {
        var location = Environment.GetEnvironmentVariable("DATAREPO_S3_TEST");
        if (string.IsNullOrEmpty(location)) Assert.Ignore("DATAREPO_S3_TEST is not set");
        _store = S3Store.Open(location.TrimEnd('/') + "/test-" + Guid.NewGuid().ToString("N")[..12]);
        _dir = Path.Combine(Path.GetTempPath(), "datarepo-s3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_store is null) return;
        using var client = new Amazon.S3.AmazonS3Client();
        var keys = new List<string>();
        await foreach (var entry in _store.ListAsync("")) keys.Add(_store.Prefix + entry.Key);
        foreach (var key in keys) await client.DeleteObjectAsync(_store.Bucket, key);
        _store.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task WriteOnceOnS3()
    {
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "one");
        File.WriteAllText(b, "two");
        Assert.That(await _store.WriteOnceAsync("d/x.txt", a, BundleWriter.Sha256File(a)), Is.EqualTo(WriteOutcome.Written));
        Assert.That(await _store.WriteOnceAsync("d/x.txt", a, BundleWriter.Sha256File(a)), Is.EqualTo(WriteOutcome.AlreadyPresent));
        Assert.ThrowsAsync<StoreConflictException>(() => _store.WriteOnceAsync("d/x.txt", b, BundleWriter.Sha256File(b)));
        Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => _store.WriteOnceAsync("d/y.txt", a, BundleWriter.Sha256File(b)),
            "S3 checks the bytes it received against the sha256 sent with them");
        Assert.That(await _store.ExistsAsync("d/y.txt"), Is.False);
    }

    [Test]
    public async Task AMultipartWriteIsWriteOnceToo()
    {
        var oldThreshold = S3Store.MultipartThreshold;
        var oldPart = S3Store.PartSize;
        S3Store.MultipartThreshold = 1;
        S3Store.PartSize = 5L * 1024 * 1024;   // S3's smallest part
        try
        {
            var big = Path.Combine(_dir, "big.bin");
            var bytes = new byte[11 * 1024 * 1024];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(big, bytes);
            var sha = BundleWriter.Sha256File(big);
            Assert.That(await _store.WriteOnceAsync("big.bin", big, sha), Is.EqualTo(WriteOutcome.Written));
            Assert.That(await _store.WriteOnceAsync("big.bin", big, sha), Is.EqualTo(WriteOutcome.AlreadyPresent));
            var back = Path.Combine(_dir, "back.bin");
            await _store.DownloadAsync("big.bin", back);
            Assert.That(BundleWriter.Sha256File(back), Is.EqualTo(sha));
        }
        finally
        {
            S3Store.MultipartThreshold = oldThreshold;
            S3Store.PartSize = oldPart;
        }
    }

    [Test]
    public async Task PushThenPullThroughS3()
    {
        var here = Path.Combine(_dir, "here");
        var bundle = StorageTests.WriteBundle(here);
        using var local = new LocalStore(here);
        Assert.That(await StoreTransfer.CopyAsync(local, _store, bundle), Is.EqualTo(TransferOutcome.Copied));
        Assert.That(await StoreTransfer.CopyAsync(local, _store, bundle), Is.EqualTo(TransferOutcome.Present));
        Assert.That(await StoreTransfer.FindAsync(_store), Is.EqualTo(new[] { bundle }));

        var served = Path.Combine(_dir, "served");
        using var back = new LocalStore(served);
        Assert.That(await StoreTransfer.CopyAsync(_store, back, bundle), Is.EqualTo(TransferOutcome.Copied));
        foreach (var file in Directory.EnumerateFiles(here, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(here, file);
            Assert.That(File.ReadAllBytes(Path.Combine(served, relative)), Is.EqualTo(File.ReadAllBytes(file)), relative);
        }
    }
}
