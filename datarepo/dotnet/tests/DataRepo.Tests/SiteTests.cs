using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Site;

namespace DataRepo.Tests;

/// <summary>The C# site generator writes the bytes <c>site.py</c> wrote from the same catalog.</summary>
/// <remarks>
/// <para>Expected output is Python 0.32.0's own (<c>Fixtures/site/python-plain</c>, <c>python-full</c>), written
/// from <c>Fixtures/site/catalog.duckdb.gz</c>, a catalog Python built from the <c>python-0.32.0</c> bundle; see
/// <c>Fixtures/site/PROVENANCE.md</c>. The only normalisation is the generator's own version (marker
/// <c>generator</c> and every page's footer), which <see cref="SiteParity"/> names.</para>
/// <para>The catalog records its bundle's path relative to <c>Fixtures/</c> (Python ran there), so these tests
/// run with that as the working directory, as Python's did, and are not run in parallel.</para>
/// </remarks>
[NonParallelizable]
public class SiteTests
{
    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");
    private static string SiteFixtures => Path.Combine(Fixtures, "site");

    private string _scratch = "";
    private string _catalog = "";
    private string _cwd = "";

    [OneTimeSetUp]
    public void Catalog()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "datarepo-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _catalog = Path.Combine(_scratch, "catalog.duckdb");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(SiteFixtures, "catalog.duckdb.gz")), CompressionMode.Decompress);
        using var file = File.Create(_catalog);
        gz.CopyTo(file);
    }

    [OneTimeTearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
    }

    [SetUp]
    public void IntoFixtures()
    {
        _cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Fixtures;
    }

    [TearDown]
    public void BackOut() => Environment.CurrentDirectory = _cwd;

    private string Out(string name) => Path.Combine(_scratch, name + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = SiteCommand.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The arguments <c>python-full</c> was written with (PROVENANCE.md).</summary>
    private string[] FullArgs(string out_) =>
    [
        _catalog, "--out", out_,
        "--title", "Test <repo> & 'co'", "--about", "site/about.md",
        "--base-url", "https://example.org/repo/", "--data-url", "https://example.org/store/",
        "--purpose", "  how organelle proteomes change with age.  ", "--keyword", "aging", "--keyword", " organelle ",
        "--keyword", "  ",
        "--notice", "Preview: regenerated & <checked> \"soon\".",
    ];

    private static void AssertSame(string expected, string actual)
    {
        var differences = SiteParity.Compare(expected, actual, out var files, out _);
        Assert.That(differences, Is.Empty, string.Join("\n", differences));
        Assert.That(files, Is.EqualTo(SiteParity.Files(expected).Count));
    }

    [Test]
    public void WithNoOptionsTheSiteIsPythons()
    {
        var out_ = Out("plain");
        var (code, stdout, _) = Run(_catalog, "--out", out_);
        Assert.That(code, Is.EqualTo(0));
        AssertSame(Path.Combine(SiteFixtures, "python-plain"), out_);
        Assert.That(stdout, Does.Contain("  wrote    36 files, 1 dataset page\n").Or.Contain("  wrote    36 files, 1 dataset page\r\n"));
        Assert.That(stdout, Does.Contain("skipped  croissant.json: no --data-url"));
    }

    [Test]
    public void WithEveryOptionTheSiteIsPythons()
    {
        var out_ = Out("full");
        var (code, _, stderr) = Run(FullArgs(out_));
        Assert.That(code, Is.EqualTo(0), stderr);
        AssertSame(Path.Combine(SiteFixtures, "python-full"), out_);
    }

    [Test]
    public void OnlyTheGeneratorsVersionIsNormalised()
    {
        var out_ = Out("version");
        SiteGenerator.BuildSite(_catalog, out_);
        var marker = File.ReadAllText(Path.Combine(out_, SiteGenerator.SiteMarker));
        Assert.That(marker, Does.Contain($"\"generator\": \"datarepo {SiteGenerator.Version}\""));
        var index = File.ReadAllText(Path.Combine(out_, "index.html"));
        Assert.That(index, Does.Contain($"\">datarepo</a> {SiteGenerator.Version} from catalog\n"));
        // A changed byte anywhere else is still a difference.
        var copy = Out("copy");
        Directory.CreateDirectory(copy);
        foreach (var f in SiteParity.Files(out_))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, f))!);
            File.Copy(Path.Combine(out_, f), Path.Combine(copy, f));
        }
        File.WriteAllText(Path.Combine(copy, "index.html"), index.Replace("from catalog\n<code>", "from catalog\n<code>x"));
        Assert.That(SiteParity.Compare(out_, copy, out _, out _), Has.Count.EqualTo(1));
    }

    [Test]
    public void ARegenerationDeletesOnlyWhatTheLastOneWrote()
    {
        var out_ = Out("regen");
        Assert.That(Run(FullArgs(out_)).Code, Is.EqualTo(0));
        // What the operator's gh-pages clone holds beside the generated files.
        Directory.CreateDirectory(Path.Combine(out_, ".git"));
        File.WriteAllText(Path.Combine(out_, ".git", "HEAD"), "ref: refs/heads/gh-pages\n");
        File.WriteAllText(Path.Combine(out_, ".nojekyll"), "");
        File.WriteAllText(Path.Combine(out_, "datasets", "operator-note.txt"), "mine");

        Assert.That(Run(_catalog, "--out", out_).Code, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(out_, ".git", "HEAD")));
        Assert.That(File.Exists(Path.Combine(out_, ".nojekyll")));
        Assert.That(File.Exists(Path.Combine(out_, "datasets", "operator-note.txt")));
        foreach (var gone in new[] { "croissant.json", "sitemap.xml", "robots.txt" })
            Assert.That(File.Exists(Path.Combine(out_, gone)), Is.False, gone);

        File.Delete(Path.Combine(out_, ".nojekyll"));
        File.Delete(Path.Combine(out_, "datasets", "operator-note.txt"));
        AssertSame(Path.Combine(SiteFixtures, "python-plain"), out_);
    }

    [Test]
    public void ADirectoryThatIsNotASiteIsRefused()
    {
        var out_ = Out("foreign");
        Directory.CreateDirectory(out_);
        File.WriteAllText(Path.Combine(out_, "precious.txt"), "not ours");
        var ex = Assert.Throws<CatalogException>(() => SiteGenerator.BuildSite(_catalog, out_));
        Assert.That(ex!.Message, Does.Contain("is not empty and was not written by `datarepo site`"));
        Assert.That(File.ReadAllText(Path.Combine(out_, "precious.txt")), Is.EqualTo("not ours"));

        var (code, _, stderr) = Run(_catalog, "--out", out_);
        Assert.That(code, Is.EqualTo(1));
        Assert.That(stderr, Does.StartWith("datarepo: "));

        var file = Path.Combine(out_, "precious.txt");
        Assert.That(() => SiteGenerator.BuildSite(_catalog, file),
            Throws.InstanceOf<CatalogException>().With.Message.EqualTo($"{file} exists and is not a directory"));
    }

    [Test]
    public void AnUnreadableMarkerIsRefused()
    {
        var out_ = Out("marker");
        Directory.CreateDirectory(out_);
        File.WriteAllText(Path.Combine(out_, SiteGenerator.SiteMarker), "{not json");
        Assert.That(() => SiteGenerator.BuildSite(_catalog, out_),
            Throws.InstanceOf<CatalogException>().With.Message.StartWith($"{Path.Combine(out_, SiteGenerator.SiteMarker)} is unreadable: "));
    }

    [Test]
    public void NoCatalogIsRefused()
    {
        var missing = Path.Combine(_scratch, "nothing.duckdb");
        Assert.That(() => SiteGenerator.ReadSiteFacts(missing),
            Throws.InstanceOf<CatalogException>().With.Message.EqualTo($"no catalog at {missing}"));
    }

    [Test]
    public void UsageErrorsExitTwo()
    {
        Assert.That(Run(_catalog).Code, Is.EqualTo(2));
        Assert.That(Run("--out", Out("x")).Code, Is.EqualTo(2));
        Assert.That(Run(_catalog, "--out", Out("x"), "--nonsense", "1").Code, Is.EqualTo(2));
        Assert.That(Run(_catalog, "--out").Code, Is.EqualTo(2));
        // argparse accepts --name=value and a unique prefix.
        var out_ = Out("prefix");
        Assert.That(Run(_catalog, $"--out={out_}", "--base", "https://example.org/").Code, Is.EqualTo(0));
        Assert.That(File.Exists(Path.Combine(out_, "sitemap.xml")));
    }

    [Test]
    public void TheSpectraTileNamesTheIngestersMs2Definition() =>
        Assert.That(DataRepo.Ingest.Definitions.Ms2Count.DefinitionId, Is.EqualTo(SiteGenerator.Ms2CountDefinitionId));

    private static JsonElement Formats()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SiteFixtures, "formats.json")));
        return doc.RootElement.Clone();
    }

    [Test]
    public void TileNumbersAreCompactedAsPythonDoes()
    {
        foreach (var pair in Formats().GetProperty("compact").EnumerateArray())
        {
            var input = pair[0];
            object? value = input.ValueKind switch
            {
                JsonValueKind.Null => null,
                _ when input.GetRawText().Contains('.') => input.GetDouble(),
                _ => input.GetInt64(),
            };
            Assert.That(SiteGenerator.Compact(value), Is.EqualTo(pair[1].GetString()), input.GetRawText());
        }
    }

    [Test]
    public void PercentagesRoundAsPythonDoes()
    {
        foreach (var pair in Formats().GetProperty("percent").EnumerateArray())
            Assert.That(PyText.FormatFixed(pair[0].GetDouble() * 100, 1) + "%", Is.EqualTo(pair[1].GetString()), pair[0].GetRawText());
    }

    [Test]
    public void TheSharedProteinThresholdIsPythons()
    {
        foreach (var pair in Formats().GetProperty("shared_threshold").EnumerateArray())
        {
            var byN = pair[0].EnumerateArray().ToDictionary(kv => kv[0].GetInt64(), kv => kv[1].GetInt64());
            var (n, count) = SiteGenerator.SharedThreshold(byN);
            Assert.That(n, Is.EqualTo(pair[1][0].ValueKind == JsonValueKind.Null ? null : pair[1][0].GetInt64()));
            Assert.That(count, Is.EqualTo(pair[1][1].GetInt64()));
        }
    }

    [Test]
    public void TheAboutMarkdownSubsetIsPythons()
    {
        foreach (var pair in Formats().GetProperty("about_html").EnumerateArray())
            Assert.That(SiteGenerator.AboutHtml(pair[0].GetString()!), Is.EqualTo(pair[1].GetString()), pair[0].GetString());
    }

    // --- real data ---------------------------------------------------------------------------

    private const string RealCatalog = "F:/aging_data/repo/catalog.duckdb";

    /// <summary>The real corpus, read only: no protein shard over the cap, and every shard is in the index
    /// (a fixed two-character shard passed every fixture test and was 3.6 MB on aging's data).</summary>
    [Test, Category("RealData")]
    public void OnTheRealCorpusEveryShardIsUnderTheCapAndIndexed()
    {
        if (!File.Exists(RealCatalog)) Assert.Ignore($"{RealCatalog} is absent");
        var out_ = Out("real");
        SiteGenerator.BuildSite(RealCatalog, out_);
        var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(out_, "proteins", "index.json"))).RootElement;
        var listed = index.GetProperty("protein_shards").EnumerateObject().Select(p => p.Value.GetProperty("file").GetString()!).ToHashSet();
        var written = Directory.GetFiles(Path.Combine(out_, "proteins"), "*.json")
            .Select(f => "proteins/" + Path.GetFileName(f)).Where(f => f != "proteins/index.json").ToHashSet();
        Assert.That(written, Is.EquivalentTo(listed));
        foreach (var f in written)
            Assert.That(new FileInfo(Path.Combine(out_, f)).Length, Is.LessThanOrEqualTo(SiteGenerator.MaxShardBytes), f);
        Directory.Delete(out_, true);
    }

    /// <summary>Byte parity on aging's serving catalog, against a Python 0.32.0 site written from the SAME
    /// catalog. Python is not run here: point <c>DATAREPO_SITE_PARITY_DIR</c> at a folder holding
    /// <c>real-py/</c> (Python's output) and <c>real_about.md</c>, made as PROVENANCE.md says.</summary>
    [Test, Category("RealData")]
    public void OnTheRealCatalogTheSiteIsPythons()
    {
        var dir = Environment.GetEnvironmentVariable("DATAREPO_SITE_PARITY_DIR");
        if (!File.Exists(RealCatalog) || dir is null || !Directory.Exists(Path.Combine(dir, "real-py")))
            Assert.Ignore($"needs {RealCatalog} and DATAREPO_SITE_PARITY_DIR/real-py");
        var expected = Path.Combine(dir!, "real-py");
        var pyCatalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(expected, SiteGenerator.SiteMarker))).RootElement
            .GetProperty("catalog_id").GetString();
        var out_ = Out("real-parity");
        var (code, stdout, stderr) = Run(
            RealCatalog, "--out", out_,
            "--title", "Aging proteomics repository", "--about", Path.Combine(dir!, "real_about.md"),
            "--base-url", "https://trishorts.github.io/aging-pipeline/",
            "--data-url", "https://example.org/store",
            "--purpose", "how organelle proteomes change with age", "--keyword", "aging",
            "--notice", "Preview: this data will be regenerated & <checked>.");
        Assert.That(code, Is.EqualTo(0), stderr);
        if (!stdout.Contains($"  catalog  {pyCatalog}"))
            Assert.Inconclusive($"the catalog has been rebuilt since {expected} was written from {pyCatalog}");
        AssertSame(expected, out_);
        Directory.Delete(out_, true);
    }
}
