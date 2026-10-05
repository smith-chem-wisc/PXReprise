using System.Globalization;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest;

namespace DataRepo.Cli;

/// <summary><c>datarepo build</c>, <c>catalog</c>, <c>query</c> and <c>publish</c>: the catalog half of cli.py.</summary>
internal static class CatalogCommands
{
    private const string Prog = "datarepo";

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Cell(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        _ => PyFormat.Str(value),
    };

    /// <summary><c>--bundle PXD036557=6fea2187</c> pairs, which is how a release pins its exact bundles.</summary>
    private static Dictionary<string, string> Pins(IReadOnlyList<string> values, string flag = "--bundle", string what = "accession")
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var at = value.IndexOf('=');
            if (at < 0 || at == value.Length - 1)
                throw new CatalogException($"{flag} wants <{what}>=<bundle id>, got {PyFormat.Repr(value)}");
            pins[value[..at]] = value[(at + 1)..];
        }
        return pins;
    }

    private static Args.Spec BuildSpec(string prog) => new Args.Spec(prog, Cli.Summaries["build"])
        .Positional("manifest", "the producing instance's manifest.yaml")
        .Positional("accession", "datasets to load; default is every 'include'", "*")
        .Option("--store", Args.Kind.Value, "where bundles live; default is the manifest's store")
        .Option("--out", Args.Kind.Value, "catalog file to write; default is <store>/../catalog.duckdb")
        .Option("--bundle", Args.Kind.Append, "pin a dataset to one bundle id; repeatable", metavar: "PXD=ID")
        .Option("--latest", Args.Kind.Flag, "when a dataset has several bundles, take the newest instead of refusing")
        .Option("--study", Args.Kind.Append, "load a study layer's delivery, pinned to one bundle id; repeatable", metavar: "LAYER=ID")
        .Option("--study-latest", Args.Kind.Append, "load a study layer's newest delivery; repeatable, refused with --release", metavar: "LAYER")
        .Option("--release", Args.Kind.Value, "release version this catalog is for; requires every dataset pinned with --bundle")
        .Option("--overwrite", Args.Kind.Flag, "rebuild a catalog that is current")
        .Option("--verbose", Args.Kind.Flag, "list every check that ran", shortName: "-v");

    public static int Build(string[] argv)
    {
        var a = BuildSpec($"{Prog} build").Parse(argv);
        return Build(a.Positional("manifest"), a.Positionals("accession").ToList(), a.Value("--store"), a.Value("--out"),
            a.List("--bundle"), a.Flag("--latest"), a.List("--study"), a.List("--study-latest"), a.Value("--release"),
            a.Flag("--overwrite"), a.Flag("--verbose"));
    }

    private static int Build(string manifestPath, List<string> accessions, string? storeArg, string? outArg,
        IReadOnlyList<string> bundlePins, bool latest, IReadOnlyList<string> studyPins, IReadOnlyList<string> studyLatest,
        string? release, bool overwrite, bool verbose)
    {
        var manifest = Manifest.Load(manifestPath);
        if (accessions.Count == 0) accessions = manifest.IngestableEntries().Select(e => e.Accession).ToList();
        if (accessions.Count == 0)
        {
            Console.Error.WriteLine($"{manifest.Path}: no dataset has status 'include'");
            return 1;
        }
        var store = storeArg ?? manifest.Store;
        var output = outArg ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store))!, "catalog.duckdb");

        List<BundleRef> bundles;
        try
        {
            bundles = CatalogBuilder.SelectBundles(manifest, accessions, store, Pins(bundlePins), latest, release);
        }
        catch (DatasetExcludedException e)
        {
            Console.Error.WriteLine($"refused  {e.Message}");
            return 1;
        }
        var studyBundles = CatalogBuilder.SelectStudyBundles(store, Pins(studyPins, "--study", "layer"), studyLatest, release);
        var (artefacts, engineChecks) = CatalogBuilder.SelectArtefacts(store, bundles);

        var notes = new Dictionary<string, object?>(StringComparer.Ordinal) { ["manifest"] = BundleWriter.PathText(manifest.Path) };
        if (release is not null) notes["release"] = release;
        if (studyBundles.Count > 0) notes["study"] = studyBundles.ToDictionary(r => r.Layer, r => (object?)r.BundleId);

        var result = CatalogBuilder.BuildCatalog(bundles, output, overwrite, manifest.Instance, notes, studyBundles, artefacts, engineChecks, manifest);
        Console.WriteLine($"catalog  {result.Path}");
        Console.WriteLine($"  id       {result.CatalogId}");
        if (result.Skipped)
        {
            Console.WriteLine("  unchanged: these bundles are already the catalog's contents; --overwrite to rebuild");
            return 0;
        }
        foreach (var r in result.Bundles) Console.WriteLine($"  dataset  {r.DatasetId,-12} bundle {r.BundleId}");
        foreach (var r in result.StudyBundles) Console.WriteLine($"  study    {r.Layer,-12} bundle {r.BundleId}  ({r.LayerVersion})");
        foreach (var r in result.Artefacts) Console.WriteLine($"  engine   {r.Engine,-20} artefact {r.ArtefactId}");
        foreach (var check in engineChecks)
            if (!string.IsNullOrEmpty(check.Detail))
                Console.WriteLine($"  note     {check.Name}: {Cell(check.Observed)} of {Cell(check.Expected)}; {check.Detail}");
        foreach (var check in result.Checks.Where(c => c.Kind == "manifest" && !string.IsNullOrEmpty(c.Detail)))
            Console.WriteLine($"  note     {check.Name}: {Cell(check.Observed)} of {Cell(check.Expected)}; {check.Detail}");
        if (result.StudyBundles.Count == 0)
        {
            // Study bundles are opt-in, so a store holding one and a build not asking for it is a legitimate
            // choice -- but a silent one, and this is the only place to make it visible.
            foreach (var (layer, refs) in CatalogBuilder.AvailableStudyLayers(store))
                Console.WriteLine(
                    $"  note     study layer '{layer}' has {refs.Count} delivery on offer and none was "
                    + $"loaded; --study {layer}={refs[^1].BundleId} to include it");
        }
        Console.WriteLine($"  tables   {string.Join(", ", result.RowCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {N(kv.Value)}"))}");
        Console.WriteLine($"  indexes  {result.Indexes}");
        Console.WriteLine($"  checks   {result.Checks.Count} run, all passed");
        if (verbose)
            foreach (var check in result.Checks) Console.WriteLine($"    ok     {check.Kind,-10} {check.Name}");
        return 0;
    }

    public static int CatalogSummary(string[] argv)
    {
        var a = new Args.Spec($"{Prog} catalog", Cli.Summaries["catalog"])
            .Positional("catalog", "the catalog .duckdb file")
            .Option("--json", Args.Kind.Flag, "print the catalog's own tables verbatim")
            .Parse(argv);
        var doc = CatalogBuilder.DescribeCatalog(a.Positional("catalog"));
        if (a.Flag("--json"))
        {
            Console.WriteLine(PyFormat.JsonIndented(doc));
            return 0;
        }
        var meta = (IReadOnlyDictionary<string, object?>)doc["meta"]!;
        object? M(string key) => meta.TryGetValue(key, out var v) ? v : null;
        Console.WriteLine($"{doc["path"]}  catalog {Cell(M("catalog_id"))}");
        Console.WriteLine($"  built    {Cell(M("built_utc"))} by {Cell(M("builder"))} {Cell(M("builder_version"))} (catalog v{Cell(M("catalog_version"))})");
        Console.WriteLine($"  schema   {Cell(M("schema_version"))}  qpx {Cell(M("qpx_version"))}");
        Console.WriteLine($"  instance {Cell(M("instance"))}");
        foreach (var r in Rows(doc["bundles"]))
        {
            var mark = r["reconciliation_ok"] is true ? " " : "!";
            Console.WriteLine($"  {mark} {Cell(r["dataset_id"]),-12} bundle {Cell(r["bundle_id"])}  {Cell(r["written_utc"])}");
            foreach (var name in r["reconciliation_failed"] as IEnumerable<object?> ?? [])
                Console.WriteLine($"      bundle reconciliation mismatch carried through: {Cell(name)}");
        }
        foreach (var r in Rows(doc.GetValueOrDefault("study_bundles")))
            Console.WriteLine($"  study    {Cell(r["layer"]),-12} bundle {Cell(r["bundle_id"])}  layer {Cell(r["layer_version"])}  {Cell(r["written_utc"])}");
        foreach (var r in Rows(doc["tables"]))
        {
            var rows = Convert.ToInt64(r["rows"] ?? 0L, CultureInfo.InvariantCulture);
            if (rows == 0) continue;
            var kind = Equals(r["kind"], "bundle") ? "" : Cell(r["kind"]);
            Console.WriteLine($"    {Cell(r["table_name"]),-24} {N(rows),10} {kind}");
        }
        var checks = Rows(doc["checks"]).ToList();
        var failed = checks.Where(c => c["ok"] is not true).ToList();
        Console.WriteLine($"  checks   {checks.Count} run, {failed.Count} failed");
        foreach (var c in failed)
            Console.WriteLine($"    FAILED {Cell(c["name"])}: {Cell(c["observed"])} vs {Cell(c["expected"])}");
        return 0;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Rows(object? value) =>
        (value as System.Collections.IEnumerable)?.Cast<IReadOnlyDictionary<string, object?>>() ?? [];

    public static int Query(string[] argv)
    {
        var a = new Args.Spec($"{Prog} query", Cli.Summaries["query"])
            .Positional("catalog", "the catalog .duckdb file")
            .Positional("sql", "the statement to run")
            .Option("--format", Args.Kind.Value, "how to print the rows", defaultValue: "table", choices: ["table", "tsv", "json"])
            .Option("--limit", Args.Kind.Value, "row cap; 0 for no cap", defaultValue: "50")
            .Parse(argv);
        if (!int.TryParse(a.Value("--limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
            throw new Args.UsageException($"usage: {Prog} query ...", $"argument --limit: invalid int value: '{a.Value("--limit")}'");
        var (columns, rows) = CatalogBuilder.RunQuery(a.Positional("catalog"), a.Positional("sql"), limit == 0 ? null : limit);
        Console.WriteLine(CatalogBuilder.FormatRows(columns, rows.Select(r => (IReadOnlyList<object?>)r), a.Value("--format")!));
        return 0;
    }

    /// <summary><c>build</c> then <c>site</c>, for an operator with no script of their own (PXReprise PXR-D4).</summary>
    /// <remarks>With no accessions named, it takes every <c>include</c> dataset that HAS a bundle and names each
    /// that has none, where a bare <c>build</c> refuses on the first. It does not run an engine, pick up a study
    /// delivery nobody asked for, or push the site anywhere: publishing to a host stays the operator's step.</remarks>
    public static int Publish(string[] argv)
    {
        var a = new Args.Spec($"{Prog} publish", Cli.Summaries["publish"])
            .Positional("manifest", "the producing instance's manifest.yaml")
            .Positional("accession", "datasets to load; default is every 'include' that has a bundle, naming those without", "*")
            .Option("--site", Args.Kind.Value, "the site directory: empty, or a site written before", required: true)
            .Option("--store", Args.Kind.Value, "where bundles live; default is the manifest's store")
            .Option("--out", Args.Kind.Value, "catalog file to write; default is <store>/../catalog.duckdb")
            .Option("--study-latest", Args.Kind.Append, "load a study layer's newest delivery; repeatable. None is loaded unless named", metavar: "LAYER")
            .Option("--title", Args.Kind.Value, "the site's name; default from the catalog's instance")
            .Option("--base-url", Args.Kind.Value, "where the site will be served, for absolute links and the sitemap")
            .Option("--data-url", Args.Kind.Value, "where the bundle store is served; enables downloads and croissant.json")
            .Option("--notice", Args.Kind.Value, "a banner for the top of every page and of llms.txt")
            .Option("--about", Args.Kind.Value, "a Markdown file: the front page's overview of the project")
            .Option("--purpose", Args.Kind.Value, "the question this instance serves", metavar: "TEXT")
            .Option("--keyword", Args.Kind.Append, "a schema.org keyword for every dataset page; repeatable", metavar: "WORD")
            .Parse(argv);
        var manifest = Manifest.Load(a.Positional("manifest"));
        var store = a.Value("--store") ?? manifest.Store;
        var accessions = a.Positionals("accession").ToList();
        if (accessions.Count == 0)
        {
            var missing = new List<string>();
            foreach (var entry in manifest.IngestableEntries())
                (CatalogBuilder.DiscoverBundles(store, entry.Accession).Count > 0 ? accessions : missing).Add(entry.Accession);
            foreach (var accession in missing)
                Console.WriteLine($"skipped  {accession}: 'include' in the manifest, but no bundle under {BundleWriter.PathText(store)}");
            if (accessions.Count == 0)
            {
                Console.Error.WriteLine($"{manifest.Path}: no 'include' dataset has a bundle under {BundleWriter.PathText(store)}");
                return 1;
            }
        }
        var output = a.Value("--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store))!, "catalog.duckdb");
        // Not --overwrite: a catalog whose id is unchanged is kept, so an unchanged store republishes
        // byte-identical pages and the site's git diff is empty. Any change to its contents rebuilds it.
        var code = Build(a.Positional("manifest"), accessions, store, output, [], latest: true, [], a.List("--study-latest"),
            release: null, overwrite: false, verbose: false);
        if (code != 0) return code;
        var siteArgs = new List<string> { output, "--out", a.Value("--site")! };
        foreach (var name in new[] { "--title", "--base-url", "--data-url", "--notice", "--about", "--purpose" })
            if (a.Value(name) is { } value) siteArgs.AddRange([name, value]);
        foreach (var keyword in a.List("--keyword")) siteArgs.AddRange(["--keyword", keyword]);
        return DataRepo.Site.SiteCommand.Run(siteArgs, Console.Out, Console.Error);
    }
}
