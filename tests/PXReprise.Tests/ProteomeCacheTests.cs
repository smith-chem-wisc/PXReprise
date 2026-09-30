using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Search;

namespace PXReprise.Tests;

public class ProteomeCacheTests
{
    private static readonly OrganismDatabase Human = new(null, Array.Empty<string>(), 9606, "UP000005640");

    [Test]
    public async Task AUniProtProteomeIsDownloadedOnceThenReused()
    {
        string dbDir = TestSupport.TempDir();
        int calls = 0;
        ProteomeCache.Retrieve fake = (id, dir) =>
        {
            calls++;
            string p = Path.Combine(dir, $"{id}_reviewed.xml");
            File.WriteAllText(p, "<uniprot/>");
            return p;
        };
        var (path, first) = await ProteomeCache.ResolveAsync(Human, dbDir, CancellationToken.None, fake);
        var (again, second) = await ProteomeCache.ResolveAsync(Human, dbDir, CancellationToken.None, fake);
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(path, Is.EqualTo(Path.Combine(dbDir, "uniprot", "UP000005640_reviewed.xml")));
            Assert.That(again, Is.EqualTo(path));
            Assert.That(second!["sha256"]!.GetValue<string>(), Is.EqualTo(first!["sha256"]!.GetValue<string>()).And.Length.EqualTo(64));
            Assert.That(Directory.GetDirectories(Path.Combine(dbDir, "uniprot")), Is.Empty, "no staging folder left behind");
        });
    }

    [Test]
    public void AFailedDownloadLeavesNothingASearchWouldTrust()
    {
        string dbDir = TestSupport.TempDir();
        ProteomeCache.Retrieve down = (id, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "half.xml"), "<uni");
            throw new HttpRequestException("UniProt returned 503");
        };
        Assert.ThrowsAsync<HttpRequestException>(() => ProteomeCache.ResolveAsync(Human, dbDir, CancellationToken.None, down));
        Assert.That(Directory.EnumerateFileSystemEntries(Path.Combine(dbDir, "uniprot")), Is.Empty);
    }

    [Test, Category("ExternalService")]
    public async Task LiveUniProtProteomeDownloadsAsXml() =>
        await ExternalServiceTestHelper.RunAsync("UniProt", async () =>
        {
            // SARS-CoV-2 (UP000464024): a real proteome of a few reviewed entries, so the canary takes seconds.
            var sars = new OrganismDatabase(null, Array.Empty<string>(), 2697049, "UP000464024");
            var (path, rec) = await ProteomeCache.ResolveAsync(sars, TestSupport.TempDir(), CancellationToken.None);
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain("<uniprot").And.Contain("<entry "));
            Assert.That(rec!["bytes"]!.GetValue<long>(), Is.GreaterThan(1000));
        });

    [Test]
    public void AMissingPreparedFileIsAUsageError()
    {
        var prepared = new OrganismDatabase("absent.xml", Array.Empty<string>());
        var e = Assert.ThrowsAsync<UsageException>(() => ProteomeCache.ResolveAsync(prepared, TestSupport.TempDir(), CancellationToken.None));
        Assert.That(e!.Message, Does.Contain("absent.xml"));
    }
}
