using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;
using DataRepo.Mcp;
using DataRepo.Site;

namespace DataRepo.Tests;

/// <summary>PXR-R32 (1.4.5, catalog format 16): a deposit searched in parts, each part a dataset keyed
/// <c>&lt;accession&gt;-&lt;group&gt;</c> (G95), is served as ONE deposit. The catalog names the deposit, counts it once,
/// matches the census by it and refuses it served both whole and in parts; the site links it to PRIDE by its accession and
/// gives it a page.</summary>
/// <remarks>The fixture's one search is ingested three times: whole, and as two stand-in parts (<c>-a</c>, <c>-b</c>) of the
/// same folder, as our PXReprise 093 trial did on PXD027754. So every part holds the same proteins, which is the case a
/// count by key gets most wrong: the parts' indexes must read exactly what the whole deposit's do. A real species split
/// holds different proteins; that difference is not tested here and needs none of this code.</remarks>
[NonParallelizable]
public class SplitDepositTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private const string Bare = "PXD999999";
    private const string PartA = "PXD999999-a";
    private const string PartB = "PXD999999-b";
    private const string Entry = "  - accession: PXD999999\n";
    private const string NextEntry = "  - accession: PXD000000\n";

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string Data => Path.Combine(_root, "data");
    private string ManifestPath => Path.Combine(Data, "manifest.yaml");
    private Dictionary<string, BundleRef> _bundles = [];

    [OneTimeSetUp]
    public void IngestWholeAndParts()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-split-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(RepoRoot(), "tests", "data");
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(Data, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(Data, Path.GetRelativePath(from, file)));
        File.WriteAllText(ManifestPath, WithParts(File.ReadAllText(ManifestPath), "    split_reason: species\n"));
        foreach (var key in new[] { Bare, PartA, PartB })
            _bundles[key] = BundleRef.Load(Ingester.Ingest(ManifestPath, key, Store, rules: IngestRules.Current).BundlePath);
    }

    [OneTimeTearDown]
    public void RemoveScratch()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>The manifest with the fixture's entry copied as parts <c>-a</c> and <c>-b</c> of the same search, each
    /// carrying <paramref name="partLines"/>. The whole entry stays <c>include</c>, so a test can serve it.</summary>
    private static string WithParts(string text, string partLines)
    {
        text = text.Replace("\r\n", "\n");
        var start = text.IndexOf(Entry, StringComparison.Ordinal);
        var end = text.IndexOf(NextEntry, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "the fixture manifest changed under this test");
        Assert.That(end, Is.GreaterThan(start), "the fixture manifest changed under this test");
        var block = text[start..end];
        var parts = string.Concat(new[] { PartA, PartB }.Select(k => block.Replace(Entry, $"  - accession: {k}\n{partLines}")));
        return text[..end] + parts + text[end..];
    }

    private CatalogResult Build(string name, Manifest? manifest = null, params string[] keys)
    {
        manifest ??= Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, keys, Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        // Unique per call, keeping a file's extension: a name not ending .duckdb is a catalog folder.
        var tag = "-" + Guid.NewGuid().ToString("N")[..8];
        var unique = name.EndsWith(".duckdb", StringComparison.Ordinal) ? name[..^".duckdb".Length] + tag + ".duckdb" : name + tag;
        return CatalogBuilder.BuildCatalog(bundles, Path.Combine(_root, unique),
            instance: manifest.Instance, artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    private static List<object?[]> Rows(CatalogResult built, string sql) => CatalogBuilder.RunQuery(built.Path, sql).Rows.ToList();

    // --- the manifest field ------------------------------------------------------------------------------

    [Test]
    public void TheIngestDidNotMoveTheWholeDepositsIdAndThePartsAreTheirOwn()
    {
        Assert.That(_bundles.Values.Select(b => b.BundleId).Distinct().Count(), Is.EqualTo(3), "each key is its own bundle");
        var plain = Manifest.Load(Path.Combine(RepoRoot(), "tests", "data", "manifest.yaml"));
        var parts = Manifest.Load(ManifestPath);
        Assert.That(parts.Datasets[PartA].SplitReason, Is.EqualTo("species"));
        Assert.That(parts.Datasets[Bare].SplitReason, Is.Null);
        Assert.That(parts.Datasets[Bare].ContentDeclaration(), Is.EqualTo(plain.Datasets[Bare].ContentDeclaration()));
        // split_reason is not content: dropping it from a part leaves its content declaration as it was.
        Assert.That(DatasetEntry.NonContentFields, Does.ContainKey("split_reason"));
        Assert.That(DatasetEntry.ContentFields, Does.Not.Contain("split_reason"));
    }

    [TestCase(PartA, "    split_reason: species-ish\n", "split_reason 'species-ish' is not one of chemistry, enrichment, label, species")]
    [TestCase(PartA, "    split_reason: [species, label]\n", "is not one of chemistry, enrichment, label, species")]
    [TestCase(Bare, "    split_reason: species\n", "split_reason 'species' is written on a key that names no group")]
    public void ASplitReasonOffTheListOrOnAWholeDepositIsRefused(string key, string line, string message)
    {
        var text = File.ReadAllText(ManifestPath).Replace("\r\n", "\n").Replace("    split_reason: species\n", "");
        var at = text.IndexOf($"  - accession: {key}\n", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0));
        var after = at + $"  - accession: {key}\n".Length;
        var path = Path.Combine(Data, "refused.yaml");
        File.WriteAllText(path, text[..after] + line + text[after..]);
        var e = Assert.Throws<ManifestException>(() => Manifest.Load(path));
        Assert.That(e!.Message, Does.Contain(message));
    }

    /// <summary><c>datarepo manifest</c> names each part's deposit, and a whole entry still <c>include</c> beside its parts
    /// (exit 1), so the mistake the build refuses is found before a build.</summary>
    [Test]
    public void TheManifestCommandNamesThePartsAndAWholeEntryBesideThem()
    {
        var both = Datarepo("manifest", ManifestPath);
        Assert.That(both.Code, Is.EqualTo(1));
        Assert.That(both.Stdout, Does.Contain($" + {PartA,-12} include").And.Contain("     split  part of PXD999999, split by species\n"));
        Assert.That(both.Stdout, Does.Contain("WHOLE AND PARTS PXD999999 is include beside PXD999999-a, PXD999999-b; retire it (status: superseded)"));
        Assert.That(both.Stderr, Does.Contain("1 deposit(s) are include both whole and in parts; build would refuse them served together"));

        var text = File.ReadAllText(ManifestPath).Replace("\r\n", "\n");
        var retired = Path.Combine(Data, "retired.yaml");
        var at = text.IndexOf(Entry, StringComparison.Ordinal);
        var status = text.IndexOf("    status: include\n", at, StringComparison.Ordinal);
        File.WriteAllText(retired, text[..status] + "    status: superseded\n    reason: split into PXD999999-a and PXD999999-b\n"
            + text[(status + "    status: include\n".Length)..]);
        var parts = Datarepo("manifest", retired);
        Assert.That(parts.Code, Is.EqualTo(0), parts.Stderr);
        Assert.That(parts.Stdout, Does.Not.Contain("WHOLE AND PARTS"));
        Assert.That(parts.Stdout, Does.Contain($" - {Bare,-12} superseded"));
    }

    private static (int Code, string Stdout, string Stderr) Datarepo(params string[] argv)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (oldOut, oldErr, oldCulture) = (Console.Out, Console.Error, System.Globalization.CultureInfo.CurrentCulture);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var code = DataRepo.Cli.Cli.Run(argv);
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            System.Globalization.CultureInfo.CurrentCulture = oldCulture;
        }
    }

    // --- the catalog ---------------------------------------------------------------------------------------

    [Test]
    public void TheCatalogNamesTheDepositAndCountsItOnce()
    {
        var whole = Build("whole.duckdb", keys: [Bare]);
        var split = Build("split.duckdb", keys: [PartA, PartB]);
        Assert.Multiple(() =>
        {
            Assert.That(split.FailedChecks, Is.Empty);
            Assert.That(Rows(split, "SELECT catalog_version FROM catalog_meta").Single()[0], Is.EqualTo("16"));
            Assert.That(Rows(split, "SELECT dataset_id, accession, split_group, split_reason FROM dataset_overview ORDER BY dataset_id"),
                Is.EqualTo(new[]
                {
                    new object?[] { PartA, Bare, "a", "species" },
                    new object?[] { PartB, Bare, "b", "species" },
                }));
            Assert.That(Rows(whole, "SELECT dataset_id, accession, split_group, split_reason FROM dataset_overview"),
                Is.EqualTo(new[] { new object?[] { Bare, Bare, null, null } }), "a deposit searched whole is its own accession, with no group");
            Assert.That(Rows(split, "SELECT split_reason FROM dataset_annotations ORDER BY dataset_id"),
                Is.EqualTo(new[] { new object?[] { "species" }, new object?[] { "species" } }));

            var runs = (long)Rows(whole, "SELECT n_runs FROM dataset_overview").Single()[0]!;
            Assert.That(Rows(split, "SELECT accession, n_datasets, dataset_ids, split_groups, split_reasons, n_runs FROM deposits"),
                Is.EqualTo(new[] { new object?[] { Bare, 2L, new List<string> { PartA, PartB }, new List<string> { "a", "b" }, new List<string> { "species" }, 2 * runs } }));
            Assert.That(Rows(whole, "SELECT accession, n_datasets, dataset_ids, split_groups, split_reasons, n_runs FROM deposits"),
                Is.EqualTo(new[] { new object?[] { Bare, 1L, new List<string> { Bare }, new List<string>(), new List<string>(), runs } }));

            // Both parts hold every protein of the whole, and the index still counts ONE deposit for each of them.
            const string proteins = "SELECT protein_accession, n_datasets, n_datasets_1pct, n_datasets_contaminant FROM protein_index ORDER BY 1";
            Assert.That(Rows(split, proteins), Is.EqualTo(Rows(whole, proteins)));
            Assert.That(Rows(split, "SELECT count(*) FROM protein_index WHERE n_datasets_1pct > 0").Single()[0], Is.GreaterThan(0L), "the fixture has accepted proteins");
            Assert.That(Rows(split, "SELECT DISTINCT dataset_ids FROM protein_index WHERE n_datasets = 1 AND len(dataset_ids) = 2").Single()[0],
                Is.EqualTo(new List<string> { PartA, PartB }), "dataset_ids keeps each search");
            const string peptides = "SELECT base_sequence, n_datasets FROM peptide_index ORDER BY 1";
            Assert.That(Rows(split, peptides), Is.EqualTo(Rows(whole, peptides)));

            var check = split.Checks.Single(c => c.Kind == "split");
            Assert.That((check.Ok, check.Observed, check.Expected, check.Detail), Is.EqualTo((true, (long?)1, (long?)1, (string?)null)));
            check = whole.Checks.Single(c => c.Kind == "split");
            Assert.That((check.Ok, check.Observed, check.Expected), Is.EqualTo((true, (long?)0, (long?)0)));
        });
    }

    [Test]
    public void AFolderCatalogCountsTheDepositOnceToo()
    {
        var file = Build("split.duckdb", keys: [PartA, PartB]);
        var folder = Build("folder", keys: [PartA, PartB]);
        Assert.That(folder.FailedChecks, Is.Empty);
        foreach (var sql in new[]
                 {
                     "SELECT * FROM protein_index ORDER BY protein_accession",
                     "SELECT * FROM peptide_index ORDER BY base_sequence",
                     "SELECT * FROM deposits",
                     "SELECT dataset_id, accession, split_group, split_reason FROM dataset_overview ORDER BY dataset_id",
                 })
            Assert.That(Rows(folder, sql), Is.EqualTo(Rows(file, sql)), sql);
    }

    [Test]
    public void ADepositServedWholeBesideItsPartsIsRefusedBeforeAnythingIsWritten()
    {
        var name = "both.duckdb";
        var e = Assert.Throws<CatalogException>(() => Build(name, keys: [Bare, PartA, PartB]));
        Assert.That(e!.Message, Does.Contain("split deposits (none served both whole and in parts): 0 vs 1"));
        Assert.That(e.Message, Does.Contain($"{Bare} beside {PartA}, {PartB}"));
        Assert.That(e.Message, Does.Contain("status: superseded"));
        Assert.That(Directory.EnumerateFiles(_root, "both-*"), Is.Empty, "refused before anything was staged");

        // One part beside the whole is the same mistake.
        e = Assert.Throws<CatalogException>(() => Build(name, keys: [Bare, PartB]));
        Assert.That(e!.Message, Does.Contain($"{Bare} beside {PartB}"));
    }

    [Test]
    public void TheCensusIsMatchedByAccession()
    {
        var census = Path.Combine(Data, "candidates.tsv");
        File.WriteAllBytes(census, new UTF8Encoding(false).GetBytes(
            "census_version\taccession\tincluded\texclusion_reason\tdefinition_id\n"
            + "v1\tPXD999999\ttrue\t\tDEF-AGING-SCREEN\n"
            + "v1\tPXD000001\ttrue\t\tDEF-AGING-SCREEN\n"));
        var text = File.ReadAllText(ManifestPath).Replace("\r\n", "\n");
        var withCensus = Path.Combine(Data, "manifest_census.yaml");
        File.WriteAllText(withCensus, text.Replace("credit: Test Working Group\n", "credit: Test Working Group\ncandidates: candidates.tsv\n"));
        var built = Build("census.duckdb", Manifest.Load(withCensus), PartA, PartB);
        Assert.That(built.FailedChecks, Is.Empty);
        Assert.That(Rows(built, "SELECT dataset_id, bundle_id, accession, included FROM dataset_candidates ORDER BY accession, dataset_id"),
            Is.EqualTo(new[]
            {
                new object?[] { null, null, "PXD000001", true },
                new object?[] { PartA, _bundles[PartA].BundleId, Bare, true },
                new object?[] { PartB, _bundles[PartB].BundleId, Bare, true },
            }), "one row per served part, each naming its dataset");
        var checks = built.Checks.Where(c => c.Kind == "census").Select(c => (c.Name, c.Observed, c.Expected, c.Detail)).ToList();
        Assert.That(checks, Is.EqualTo(new[]
        {
            ("dataset_candidates (included accessions the catalog holds)", (long?)1, (long?)2,
                (string?)"the census includes, and the catalog holds no dataset for: PXD000001 (the census may run ahead of ingest)"),
            ("dataset_candidates (catalog datasets the census includes)", (long?)2, (long?)2, (string?)null),
        }), "the deposit is held, and both parts take its verdict");
        Assert.That(Rows(built, "SELECT rows FROM catalog_tables WHERE table_name = 'dataset_candidates'").Single()[0], Is.EqualTo(3L));
    }

    // --- the site and the MCP server ------------------------------------------------------------------------

    [Test]
    public void TheSiteLinksTheDepositToPrideAndGivesItAPage()
    {
        var split = Build("site.duckdb", keys: [PartA, PartB]);
        var out_ = Path.Combine(_root, "site-" + Guid.NewGuid().ToString("N")[..8]);
        var result = SiteGenerator.BuildSite(split.Path, out_, new SiteOptions(BaseUrl: "https://example.org/repo/"));
        string Read(string name) => File.ReadAllText(Path.Combine(out_, name));

        Assert.That(result.DepositPages, Is.EqualTo(new[] { "datasets/PXD999999.html" }));
        Assert.That(result.Warnings.Where(w => w.Contains("whole and in parts")), Is.Empty);
        var part = Read("datasets/PXD999999-a.html");
        Assert.That(part, Does.Contain("https://www.ebi.ac.uk/pride/archive/projects/PXD999999\""), "PRIDE knows the deposit");
        Assert.That(part, Does.Not.Contain("projects/PXD999999-a"), "PRIDE has no project named by a part's key");
        Assert.That(part, Does.Contain("One part of deposit <a href=\"PXD999999.html\">PXD999999</a>, split by species"));
        Assert.That(part, Does.Contain("Its other part: <a href=\"PXD999999-b.html\">PXD999999-b</a>."));
        Assert.That(part, Does.Contain("\"isPartOf\""));
        Assert.That(Read("datasets/PXD999999-a.json"),
            Does.Contain("It is one part of deposit PXD999999, which was searched in parts (split by species). The deposit's other part is PXD999999-b."),
            "the generated summary says it, from catalog fields only");

        var deposit = Read("datasets/PXD999999.html");
        Assert.That(deposit, Does.Contain("This deposit was searched in 2 parts (split by species)."));
        Assert.That(deposit, Does.Contain("<a href=\"PXD999999-a.html\">PXD999999-a</a>").And.Contain("<a href=\"PXD999999-b.html\">PXD999999-b</a>"));
        Assert.That(deposit, Does.Contain("\"hasPart\"").And.Contain("https://www.ebi.ac.uk/pride/archive/projects/PXD999999\""));

        var index = Read("index.html");
        Assert.That(index, Does.Contain("Search and quantification results for 1 public proteomics dataset,"));
        Assert.That(index, Does.Contain("1 of them was searched in parts"));
        Assert.That(index, Does.Contain("part of <a href=\"datasets/PXD999999.html\">PXD999999</a>"));
        Assert.That(Read("llms.txt"), Does.Contain("> Search and quantification results for 1 public proteomics dataset,")
            .And.Contain("keyed `<accession>-<group>` (PXD999999 here)"));
        var datasets = Read("datasets.json");
        Assert.That(datasets, Does.Contain("\"split_deposits\"").And.Contain("\"siblings\""));
        Assert.That(Read("sitemap.xml"), Does.Contain("https://example.org/repo/datasets/PXD999999.html"));
        Assert.That(index, Does.Contain("<div class=\"tile-label\">Datasets</div><div class=\"tile-value\" title=\"1\">1</div>"),
            "the figure of merit counts the deposit once");
    }

    [Test]
    public void ACatalogWithNoSplitSaysNothingAboutSplits()
    {
        var whole = Build("plain.duckdb", keys: [Bare]);
        var out_ = Path.Combine(_root, "plain-site-" + Guid.NewGuid().ToString("N")[..8]);
        var result = SiteGenerator.BuildSite(whole.Path, out_);
        Assert.That(result.DepositPages, Is.Empty);
        foreach (var name in new[] { "index.html", "llms.txt", "datasets.json", "datasets/PXD999999.html" })
        {
            var text = File.ReadAllText(Path.Combine(out_, name));
            Assert.That(text, Does.Not.Contain("in parts").And.Not.Contain("split_deposits").And.Not.Contain("siblings"), name);
        }
        using var server = new CatalogServer(whole.Path);
        var described = PydanticJson.Serialize(Mcp.Mcp.Call(server, "describe"));
        Assert.That(described, Does.Not.Contain("\"split_group\""), "an unsplit catalog's datasets answer as they did");
    }

    [Test]
    public void TheMcpServerNamesEachPartsDeposit()
    {
        var split = Build("mcp.duckdb", keys: [PartA, PartB]);
        using var server = new CatalogServer(split.Path);
        var described = PydanticJson.Serialize(Mcp.Mcp.Call(server, "describe"));
        Assert.That(described, Does.Contain("\"split_group\"").And.Contain("\"split_reason\"").And.Contain("\"accession\""));
        Assert.That(described, Does.Contain("\"species\""));
        var deposits = PydanticJson.Serialize(Mcp.Mcp.Call(server, "describe", target: "deposits"));
        Assert.That(deposits, Does.Contain("One row per DEPOSIT"));
    }
}
