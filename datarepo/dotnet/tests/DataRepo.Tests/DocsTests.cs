using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;

namespace DataRepo.Tests;

/// <summary>The user-facing pages say what the program does: the tutorial's output is the program's, and every
/// relative link in README.md, CONTRIBUTING.md and docs/ lands on a file and a heading that exist.</summary>
/// <remarks>
/// <para>Ported from the Python release's <c>tests/test_getting_started.py</c> and <c>tests/test_docs_links.py</c>,
/// which now run only at the frozen v0.32.0 tag. docs/cli.md has its own check, <c>tools/build_cli_docs.py
/// --check</c>, run by CI against the CLI it builds.</para>
/// <para>The tutorial promises that a reader on its stated version gets the ids it prints. A catalog id hashes the
/// program's version, so every release silently falsified the page until a test held it (0.30.0's tutorial quoted
/// a 0.29.0 catalog id, and a stranger found it first). Tests are built as <c>0.0.0-dev</c>, so the catalog id this
/// build prints is not the page's; the test recomputes, from the same bundles, the id a release build of the
/// page's stated version prints (as <c>CatalogTests.TheCatalogIdHashesWhatPythonHashesWithThisBuildsVersion</c>
/// substitutes a version), after checking that the recomputation reproduces this build's own id.</para>
/// </remarks>
[NonParallelizable]
public class DocsTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>Runs one <c>datarepo</c> command line in-process, as the tutorial types it.</summary>
    private static (int Code, string Stdout, string Stderr) Datarepo(params string[] argv)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var (oldOut, oldErr, oldCulture) = (Console.Out, Console.Error, CultureInfo.CurrentCulture);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // as Program.cs sets it
        try
        {
            var code = DataRepo.Cli.Cli.Run(argv);
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    /// <summary>Every line of <paramref name="output"/>, trailing spaces dropped, appears on the page in order.</summary>
    private static void AssertShown(string page, string output, string what)
    {
        var at = 0;
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
        {
            var found = page.IndexOf(line + "\n", at, StringComparison.Ordinal);
            Assert.That(found, Is.GreaterThanOrEqualTo(0), $"getting-started.md does not show {what}'s line '{line}' (in order)");
            at = found + line.Length;
        }
    }

    [Test]
    public void TheTutorialPrintsWhatTheProgramPrints()
    {
        var root = RepoRoot();
        var page = Read(Path.Combine(root, "docs", "getting-started.md"));
        var stated = Regex.Match(page, @"was run on this example with datarepo (\S+?)\.\s");
        Assert.That(stated.Success, "getting-started.md no longer says which version it was run with");
        var version = stated.Groups[1].Value;
        Assert.That(version, Does.Not.Contain("-dev"), "the tutorial must be run with a release build");
        if (!BundleWriter.PackageVersion.Contains("-dev", StringComparison.Ordinal))
            Assert.That(version, Is.EqualTo(BundleWriter.PackageVersion),
                $"getting-started.md says datarepo {version}, and this is {BundleWriter.PackageVersion}: rerun the tutorial and update its output");
        Assert.That(page, Does.Contain($"datarepo {version}  schema {SchemaContract.Version}\n"),
            "the page's `doctor` output names another version or schema");
        Assert.That(page, Does.Contain($"built    … by datarepo {version}\n"), "the page's `mcp --check` output names another version");
        Assert.That(page, Does.Contain($"--branch v{version} "), "the page clones another tag than its version");
        Assert.That(page, Does.Contain($"/releases/download/v{version}/datarepo-{version}-"), "the page downloads another version");

        var scratch = Path.Combine(Path.GetTempPath(), "datarepo-docs-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(scratch, "example-instance");
        CopyTree(Path.Combine(root, "tests", "data"), instance);
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(instance);
        try
        {
            var manifest = Datarepo("manifest", "manifest.yaml");
            Assert.That(manifest.Code, Is.EqualTo(0), manifest.Stderr);

            var ingest = Datarepo("ingest", "manifest.yaml", "PXD999999");
            Assert.That(ingest.Code, Is.EqualTo(0), ingest.Stderr);
            var bundle = Regex.Match(ingest.Stdout, @"PXD999999[\\/]([0-9a-f]{16})").Groups[1].Value;
            Assert.That(ingest.Stdout, Does.Contain("findings 5 open warning(s): low_id_rate, no_design_file, occupancy_not_stored, sdrf_skeleton, unresolved_modifications"));

            var refused = Datarepo("ingest", "manifest.yaml", "PXD000000");
            Assert.That(refused.Code, Is.EqualTo(1), "the page says the excluded dataset exits 1");
            Assert.That(refused.Stdout, Does.Contain("refused  PXD000000 has status 'exclude' in manifest.yaml and will not be loaded."));

            var publish = Datarepo("publish", "manifest.yaml", "--site", "site", "--title", "Example repository",
                "--purpose", "how a tutorial fixture behaves");
            Assert.That(publish.Code, Is.EqualTo(0), publish.Stderr);
            var printed = Regex.Match(publish.Stdout, @"id\s+([0-9a-f]{16})").Groups[1].Value;
            Assert.That(publish.Stdout, Does.Contain("checks   58 run, all passed"));
            Assert.That(publish.Stdout, Does.Contain("wrote    36 files, 1 dataset page"));

            // The id a release build of the page's version prints for these bundles. First prove the recomputation
            // has the build's inputs, by reproducing the id this build printed.
            var store = Path.Combine(instance, "store");
            var bundles = CatalogBuilder.SelectBundles(Manifest.Load("manifest.yaml"), ["PXD999999"], store);
            var (artefacts, _) = CatalogBuilder.SelectArtefacts(store, bundles);
            Assert.That(CatalogBuilder.CatalogId(bundles, [], artefacts), Is.EqualTo(printed),
                "the recomputed catalog id does not reproduce the one this build printed, so its inputs differ from publish's");
            var catalog = CatalogBuilder.CatalogId(bundles, [], artefacts, packageVersion: version);

            var quotedBundles = Regex.Matches(page, @"bundle\s+(?:…/store/PXD999999/)?([0-9a-f]{16})").Select(m => m.Groups[1].Value).ToHashSet();
            var quotedCatalogs = Regex.Matches(page, @"(?:id|catalog)\s+([0-9a-f]{16})").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.That(quotedBundles, Is.EquivalentTo(new[] { bundle }), $"page quotes bundle(s) {string.Join(", ", quotedBundles)}, the program wrote {bundle}");
            Assert.That(quotedCatalogs, Is.EquivalentTo(new[] { catalog }),
                $"page quotes catalog(s) {string.Join(", ", quotedCatalogs)}; datarepo {version} builds {catalog}");
            Assert.That(page, Does.Contain($"The bundle's name, `{bundle}`"));

            var again = Datarepo("publish", "manifest.yaml", "--site", "site", "--title", "Example repository",
                "--purpose", "how a tutorial fixture behaves");
            Assert.That(again.Stdout, Does.Contain("unchanged"), "the page says a second publish reports the catalog unchanged");

            var overview = Datarepo("query", "catalog.duckdb",
                "SELECT dataset_id, n_psms_1pct, n_protein_groups_1pct, n_open_findings FROM dataset_overview");
            Assert.That(overview.Code, Is.EqualTo(0), overview.Stderr);
            AssertShown(page, overview.Stdout, "the dataset_overview query");
            var findings = Datarepo("query", "catalog.duckdb",
                "SELECT code, severity FROM findings ORDER BY CASE severity WHEN 'error' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, code");
            Assert.That(findings.Code, Is.EqualTo(0), findings.Stderr);
            AssertShown(page, findings.Stdout, "the findings query");

            var check = Datarepo("mcp", "--catalog", "catalog.duckdb", "--check");
            Assert.That(check.Code, Is.EqualTo(0), check.Stderr);
            foreach (var line in check.Stdout.Split('\n').Where(l => l.Contains("PSMs at 1%") || l.Contains("table(s) present") || l.Contains("tools ")))
                Assert.That(page, Does.Contain(line.TrimEnd() + "\n"), $"the page's `mcp --check` output lacks '{line.TrimEnd()}'");
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a DuckDB file still mapped on Windows; the temp folder is the OS's to clear */ }
        }
    }

    // --- links ------------------------------------------------------------------------------------------------

    private static readonly Regex Link = new(@"(?<!!)\[[^\]]*\]\(([^)\s]+)\)");
    private static readonly Regex Fence = new(@"^(```|~~~)");

    /// <summary>GitHub's heading id: lower-case, markup dropped, punctuation other than <c>-</c> and <c>_</c>
    /// dropped, each space a hyphen.</summary>
    private static string Slug(string heading)
    {
        var text = Regex.Replace(heading, @"[`*]|<[^>]+>", "").Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"[^\w\- ]", "");
        return text.Replace(' ', '-');
    }

    private static HashSet<string> Anchors(string page)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var fenced = false;
        foreach (var line in Read(page).Split('\n'))
        {
            if (Fence.IsMatch(line.Trim())) { fenced = !fenced; continue; }
            if (fenced || !line.StartsWith('#')) continue;
            var slug = Slug(line.TrimStart('#'));
            var n = seen.GetValueOrDefault(slug);
            anchors.Add(n == 0 ? slug : $"{slug}-{n}");
            seen[slug] = n + 1;
        }
        return anchors;
    }

    private static IEnumerable<string> Links(string page)
    {
        var fenced = false;
        foreach (var line in Read(page).Split('\n'))
        {
            if (Fence.IsMatch(line.Trim())) { fenced = !fenced; continue; }
            if (fenced) continue;
            foreach (Match m in Link.Matches(line)) yield return m.Groups[1].Value;
        }
    }

    private static IEnumerable<string> Pages()
    {
        var root = RepoRoot();
        yield return "README.md";
        yield return "CONTRIBUTING.md";
        foreach (var page in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            yield return Path.GetRelativePath(root, page).Replace('\\', '/');
    }

    [TestCaseSource(nameof(Pages))]
    public void EveryRelativeLinkResolves(string relative)
    {
        var page = Path.Combine(RepoRoot(), relative);
        var broken = new List<string>();
        foreach (var target in Links(page))
        {
            if (Regex.IsMatch(target, "^[a-z]+:") || target.StartsWith("//", StringComparison.Ordinal))
                continue; // http(s), mailto: checked by nobody here, on purpose
            var hash = target.IndexOf('#');
            var (path, anchor) = hash < 0 ? (target, "") : (target[..hash], target[(hash + 1)..]);
            var dest = path.Length > 0 ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(path))) : page;
            if (!File.Exists(dest) && !Directory.Exists(dest))
                broken.Add($"{target} (no such file)");
            else if (anchor.Length > 0 && dest.EndsWith(".md", StringComparison.Ordinal) && !Anchors(dest).Contains(anchor))
                broken.Add($"{target} (no such heading)");
        }
        Assert.That(broken, Is.Empty, $"{relative}: {string.Join("; ", broken)}");
    }
}
