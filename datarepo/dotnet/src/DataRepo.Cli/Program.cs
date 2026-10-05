using System.Globalization;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Cli;
using DataRepo.Ingest;
using DataRepo.Study;

// The `datarepo` command line (C# port of cli.py, D41). Exit codes are meant for the pipeline that calls
// it: 0 success, 1 a refusal or failure the operator must act on, 2 bad usage.
//
//   datarepo doctor                              is this machine able to ingest?
//   datarepo manifest <manifest.yaml>            what does the producing instance offer?
//   datarepo ingest <manifest.yaml> <PXD...>     build the bundle(s)
//   datarepo study <study.yaml>                  write a study layer's delivered rows as a bundle
//   datarepo inspect <bundle-dir>                what is in a bundle, and did it reconcile?
//   datarepo mcp --catalog <catalog.duckdb>      serve one catalog to an agent over stdio
//   datarepo site <catalog.duckdb> --out <dir>   write the public static site for one catalog
//
//   datarepo build <manifest.yaml> <PXD...>      load bundles into one DuckDB catalog
//   datarepo catalog <catalog.duckdb>            what is in a catalog, and did it check out?
//   datarepo query <catalog.duckdb> <sql>        run one read-only query against a catalog
//   datarepo publish <manifest.yaml> --site <d>  build, then site
//
//   datarepo run <engine> <PXD...> --store ...   run a released engine on stored data
//
// Every command of the Python release is here (D41).
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
return Cli.Run(args);

namespace DataRepo.Cli
{
    internal static class Cli
    {
        private const string Prog = "datarepo";

        private static readonly string[] Commands = ["ingest", "study", "manifest", "inspect", "build", "catalog", "query", "site", "publish", "run", "mcp", "doctor"];
        private static readonly string[] Pending = [];

        /// <summary>Each command's one line, as <c>datarepo -h</c> lists it and as its own <c>-h</c> prints it
        /// under the usage line. docs/cli.md is generated from that output, so this is its only source.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Summaries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ingest"] = "build a Parquet bundle for one or more datasets",
            ["study"] = "write a study layer's delivered rows as a study bundle",
            ["manifest"] = "show what the producing instance offers, and check its run_enrichment maps",
            ["inspect"] = "summarise a written bundle",
            ["build"] = "load bundles into one DuckDB catalog",
            ["catalog"] = "summarise a built catalog",
            ["query"] = "run one read-only SQL query against a catalog",
            ["site"] = DataRepo.Site.SiteCommand.Summary,
            ["publish"] = "build the catalog from every ingested dataset, then write the static site",
            ["run"] = "run a released engine on stored data (the instance operator's step)",
            ["mcp"] = DataRepo.Mcp.McpCommand.Summary,
            ["doctor"] = "report this build's version, schema and components, and any registered MCP servers",
        };

        /// <summary>The top-level usage line.</summary>
        private static string TopUsage => $"usage: {Prog} [-h] [--version] {{{string.Join(",", Commands.Concat(Pending))}}} ...";

        /// <summary>What <c>datarepo -h</c> prints, in argparse's shape.</summary>
        internal static string TopHelp()
        {
            var lines = new List<string>
            {
                TopUsage, "",
                "Reanalysed public proteomics data as one repository: ingest, build, publish and serve.", "",
                "commands:",
            };
            foreach (var command in Commands) lines.Add($"  {command,-10} {Summaries[command]}");
            lines.Add("");
            lines.Add($"`{Prog} <command> -h` describes one command.");
            lines.Add("");
            lines.Add("options:");
            lines.Add($"  {"-h, --help",-10} show this help message and exit");
            lines.Add($"  {"--version",-10} show program's version number and exit");
            return string.Join("\n", lines);
        }

        public static int Run(string[] argv)
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help")
            {
                if (argv.Length == 0)
                {
                    Console.Error.WriteLine(TopUsage);
                    Console.Error.WriteLine($"{Prog}: error: the following arguments are required: command");
                    return 2;
                }
                Console.WriteLine(TopHelp());
                return 0;
            }
            if (argv[0] == "--version")
            {
                Console.WriteLine($"{Prog} {BundleWriter.PackageVersion}");
                return 0;
            }
            var command = argv[0];
            var rest = argv[1..];
            try
            {
                return command switch
                {
                    "ingest" => Ingest(rest),
                    "study" => StudyCommand(rest),
                    "manifest" => ManifestCommand(rest),
                    "inspect" => Inspect(rest),
                    "build" => CatalogCommands.Build(rest),
                    "catalog" => CatalogCommands.CatalogSummary(rest),
                    "query" => CatalogCommands.Query(rest),
                    "publish" => CatalogCommands.Publish(rest),
                    "run" => RunCommand.Run(rest),
                    "mcp" => DataRepo.Mcp.McpCommand.Run(rest, Console.Out, Console.Error),
                    "site" => DataRepo.Site.SiteCommand.Run(rest, Console.Out, Console.Error),
                    "doctor" => Doctor(rest),
                    _ when Pending.Contains(command) => NotYet(command),
                    _ => Invalid(command),
                };
            }
            catch (Args.UsageException e)
            {
                Console.Error.WriteLine(e.Usage);
                Console.Error.WriteLine($"{Prog} {command}: error: {e.Message}");
                return 2;
            }
            catch (DataRepoException e)
            {
                Console.Error.WriteLine($"{Prog}: {e.Message}");
                return 1;
            }
        }

        private static int Invalid(string command)
        {
            Console.Error.WriteLine(TopUsage);
            Console.Error.WriteLine(
                $"{Prog}: error: argument command: invalid choice: '{command}' (choose from {string.Join(", ", Commands.Concat(Pending).Select(c => $"'{c}'"))})");
            return 2;
        }

        private static int NotYet(string command)
        {
            Console.Error.WriteLine(
                $"{Prog}: '{command}' is not in the C# port yet (design/CSHARP_PORT.md); use the Python release meanwhile");
            return 1;
        }

        private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string G(object? value) => value is null ? "None" : PyFormat.FormatG(value);

        private static void PrintResult(IngestResult result, bool verbose, TextWriter output)
        {
            output.WriteLine($"  bundle   {result.BundlePath}");
            if (result.Skipped)
                output.WriteLine("  unchanged: inputs and ingester match the bundle already written; --overwrite to rebuild");
            output.WriteLine($"  tables   {string.Join(", ", result.RowCounts.Select(kv => $"{kv.Key} {N(kv.Value)}"))}");
            foreach (var check in result.Checks)
            {
                if (check["ok"] is false)
                    output.WriteLine($"  MISMATCH {check["name"]}: bundle {G(check["observed"])} vs producer {G(check["expected"])} ({check["source"]})");
                else if (verbose && check["observed"] is not null)
                    output.WriteLine($"  ok       {check["name"]}: {G(check["observed"])}");
            }
            if (result.UnresolvedModifications.Count > 0)
                output.WriteLine($"  WARN     {result.UnresolvedModifications.Count} unresolved modification(s): {string.Join(", ", result.UnresolvedModifications.Keys.Order(StringComparer.Ordinal))}");
            if (result.UnmatchedRuns.Count > 0)
                output.WriteLine($"  WARN     run names with no deposited file: {string.Join(", ", result.UnmatchedRuns.Keys.Order(StringComparer.Ordinal))}");
            var warnings = result.Findings.Where(f => f["severity"] is "warning" or "error").ToList();
            if (warnings.Count > 0)
                output.WriteLine($"  findings {warnings.Count} open warning(s): {string.Join(", ", warnings.Select(f => (string)f["code"]!).Distinct().Order(StringComparer.Ordinal))}");
        }

        private static int Ingest(string[] argv)
        {
            var args = new Args.Spec($"{Prog} ingest", Summaries["ingest"])
                .Positional("manifest", "the producing instance's manifest.yaml")
                .Positional("accession", "datasets to ingest; default is every 'include'", "*")
                .Option("--store", Args.Kind.Value, "where to write bundles; default is the manifest's store")
                .Option("--mm-settings", Args.Kind.Value, "MetaMorpheus install to read modification definitions from")
                .Option("--overwrite", Args.Kind.Flag, "rebuild a bundle that already exists")
                .Option("--verbose", Args.Kind.Flag, "show every reconciliation check", shortName: "-v")
                .Option("--json", Args.Kind.Flag, "print one JSON envelope on stdout; the human report goes to stderr")
                .Parse(argv);
            var json = args.Flag("--json");
            // PXReprise 009: a calling pipeline reads one envelope on stdout and the exit code; everything for a
            // human goes to stderr, so the envelope is the only thing on stdout.
            var human = json ? Console.Error : Console.Out;
            var datasets = new List<Dictionary<string, object?>>();
            int Finish(int code, List<string>? reasons = null)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["datarepo"] = BundleWriter.PackageVersion,
                        ["ok"] = code == 0,
                        ["exit_code"] = code,
                        ["reasons"] = reasons ?? [],
                        ["datasets"] = datasets,
                    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return code;
            }
            Manifest manifest;
            try { manifest = Manifest.Load(args.Positional("manifest")); }
            catch (DataRepoException e) when (json)
            {
                Console.Error.WriteLine($"{Prog}: {e.Message}");
                return Finish(1, [e.Message]);
            }
            var accessions = args.Positionals("accession").Count > 0
                ? args.Positionals("accession").ToList()
                : manifest.IngestableEntries().Select(e => e.Accession).ToList();
            if (accessions.Count == 0)
            {
                Console.Error.WriteLine($"{manifest.Path}: no dataset has status 'include'");
                return Finish(1, [$"{manifest.Path}: no dataset has status 'include'"]);
            }
            var failures = 0;
            foreach (var accession in accessions)
            {
                human.WriteLine(accession);
                IngestResult result;
                try
                {
                    var entry = manifest.Dataset(accession);
                    result = Ingester.IngestDataset(manifest, entry, args.Value("--store"), args.Value("--mm-settings"), args.Flag("--overwrite"));
                }
                catch (DatasetExcludedException e)
                {
                    human.WriteLine($"  refused  {e.Message}");
                    datasets.Add(new() { ["accession"] = accession, ["status"] = "excluded", ["reasons"] = new List<string> { e.Message } });
                    failures++;
                    continue;
                }
                catch (DataRepoException e)
                {
                    Console.Error.WriteLine($"  failed   {e.Message}");
                    datasets.Add(new() { ["accession"] = accession, ["status"] = "refused", ["reasons"] = new List<string> { e.Message } });
                    failures++;
                    continue;
                }
                catch (Exception e) when (json)
                {
                    // A crash still owes the caller its envelope; the stack trace is for a human.
                    Console.Error.WriteLine($"  error    {e}");
                    datasets.Add(new() { ["accession"] = accession, ["status"] = "error", ["reasons"] = new List<string> { $"{e.GetType().Name}: {e.Message}" } });
                    failures++;
                    continue;
                }
                PrintResult(result, args.Flag("--verbose"), human);
                datasets.Add(Envelope(result));
            }
            return Finish(failures > 0 ? 1 : 0);
        }

        /// <summary>One dataset's entry in <c>ingest --json</c>'s envelope.</summary>
        private static Dictionary<string, object?> Envelope(IngestResult result) => new()
        {
            ["accession"] = result.DatasetId,
            ["status"] = result.Skipped ? "unchanged" : "ingested",
            ["bundle_id"] = result.BundleId,
            ["bundle_path"] = result.BundlePath,
            ["tables"] = result.RowCounts,
            ["checks"] = result.Checks.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c["name"], ["ok"] = c["ok"], ["observed"] = c["observed"], ["expected"] = c["expected"], ["source"] = c["source"],
            }).ToList(),
            ["mismatches"] = result.Mismatches.Count(),
            ["unresolved_modifications"] = result.UnresolvedModifications,
            ["unmatched_runs"] = result.UnmatchedRuns,
            ["findings"] = result.Findings.Where(f => f["severity"] is "warning" or "error")
                .Select(f => (string)f["code"]!).Distinct().Order(StringComparer.Ordinal).ToList(),
            ["reasons"] = new List<string>(),
        };

        private static int StudyCommand(string[] argv)
        {
            var args = new Args.Spec($"{Prog} study", Summaries["study"])
                .Positional("manifest", "the study delivery's study.yaml")
                .Option("--store", Args.Kind.Value, "where to write the bundle; default is the manifest's store")
                .Option("--overwrite", Args.Kind.Flag, "rewrite a delivery already written")
                .Parse(argv);
            var manifest = StudyWriter.LoadStudyManifest(args.Positional("manifest"));
            var result = StudyWriter.WriteStudyBundle(manifest, args.Value("--store"), args.Flag("--overwrite"));
            var delivery = manifest.Delivery is null or "" ? "unlabelled" : PyFormat.Str(manifest.Delivery);
            Console.WriteLine($"{result.Layer}  (study layer, delivery {delivery})");
            Console.WriteLine($"  bundle   {result.BundlePath}");
            if (result.Skipped)
                Console.WriteLine("  unchanged: these files are already delivered; --overwrite to rewrite");
            var counts = string.Join(", ", result.RowCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {N(kv.Value)}"));
            Console.WriteLine($"  tables   {(counts.Length > 0 ? counts : "(none)")}");
            var empty = result.RowCounts.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            if (empty.Count > 0) Console.WriteLine($"  note     delivered with no rows: {string.Join(", ", empty)}");
            Console.WriteLine($"  load it  datarepo build <manifest.yaml> --study {result.Layer}={result.BundleId}");
            return 0;
        }

        private static int ManifestCommand(string[] argv)
        {
            var args = new Args.Spec($"{Prog} manifest", Summaries["manifest"]).Positional("manifest", "the producing instance's manifest.yaml").Parse(argv);
            var manifest = Manifest.Load(args.Positional("manifest"));
            Console.WriteLine($"instance   {manifest.Instance} (manifest v{manifest.ManifestVersion})");
            Console.WriteLine($"work root  {manifest.WorkRoot}");
            Console.WriteLine($"store      {manifest.Store}");
            if (manifest.Licence is not null && PyFormat.Str(manifest.Licence).Length > 0)
                Console.WriteLine($"licence    {PyFormat.Str(manifest.Licence)} - {(manifest.Credit is null ? "no credit line" : PyFormat.Str(manifest.Credit))}");
            Console.WriteLine();
            var refused = 0;
            foreach (var entry in manifest.Datasets.Values)
            {
                var mark = entry.Ingestable ? "+" : "-";
                var axes = string.Join(" ", new[] { entry.Organism, entry.Acquisition, entry.QuantMethod }
                    .Where(v => v is not null && PyFormat.Str(v).Length > 0).Select(v => PyFormat.Str(v!)));
                Console.WriteLine($" {mark} {entry.Accession,-12} {entry.Status,-8} {axes}");
                if (entry.Run.Length > 0)
                    Console.WriteLine($"     run    {entry.Run}  ({(entry.Files is null ? "?" : PyFormat.Str(entry.Files))} files, MetaMorpheus {(entry.Metamorpheus is null ? "?" : PyFormat.Str(entry.Metamorpheus))})");
                if (entry.Flags.Count > 0) Console.WriteLine($"     flags  {string.Join(", ", entry.Flags)}");
                if (entry.Reason is not null && PyFormat.Str(entry.Reason).Length > 0)
                    Console.WriteLine($"     reason {string.Join(" ", PyFormat.Str(entry.Reason).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");
                // PXReprise 009: a run map ingest would refuse is refused here too, so a wrong map is found
                // before a re-ingest. The runs are read where the run folder is reachable.
                if (entry.RunEnrichment.Count == 0) continue;
                var (problems, runsRead) = Ingester.RunEnrichmentProblems(manifest, entry);
                foreach (var problem in problems) Console.WriteLine($"     REFUSED {problem}");
                if (problems.Count == 0)
                    Console.WriteLine(runsRead
                        ? $"     run_enrichment  ok against the runs ({entry.RunEnrichment.Count} mapped)"
                        : "     run_enrichment  values ok; runs not readable here, so coverage was not checked");
                refused += problems.Count > 0 ? 1 : 0;
            }
            if (refused > 0)
            {
                Console.Error.WriteLine($"{manifest.Path}: {refused} dataset(s) have a run_enrichment map ingest would refuse");
                return 1;
            }
            return 0;
        }

        private static int Inspect(string[] argv)
        {
            var args = new Args.Spec($"{Prog} inspect", Summaries["inspect"])
                .Positional("bundle", "bundle directory or its bundle.json")
                .Option("--json", Args.Kind.Flag, "print the manifest verbatim")
                .Parse(argv);
            var path = args.Positional("bundle");
            string manifestPath;
            if (Directory.Exists(path))
            {
                // A study bundle has a different manifest; an operator should not have to know which they hold.
                manifestPath = Path.Combine(path, BundleWriter.ManifestName);
                if (!File.Exists(manifestPath) && File.Exists(Path.Combine(path, StudyWriter.StudyBundleManifest)))
                    manifestPath = Path.Combine(path, StudyWriter.StudyBundleManifest);
            }
            else manifestPath = path;
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"no {BundleWriter.ManifestName} or {StudyWriter.StudyBundleManifest} at {path}");
                return 1;
            }
            var text = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (args.Flag("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
            if (S(root, "kind") == "study")
            {
                var ing = root.GetProperty("ingester");
                Console.WriteLine($"{S(root, "layer")}  study bundle {S(root, "bundle_id")}  (layer {S(root, "layer_version")})");
                Console.WriteLine($"  written  {S(root, "written_utc")} by {S(ing, "name")} {S(ing, "version")} (study path {S(ing, "study_ingest_path")})");
                Console.WriteLine($"  schema   {S(root, "schema_version")}");
                var instance = S(root, "instance");
                var delivery = S(root, "delivery");
                Console.WriteLine($"  from     {(instance.Length > 0 ? instance : "unknown instance")}, delivery {(delivery.Length > 0 ? delivery : "unlabelled")}");
                if (root.TryGetProperty("tables", out var st))
                    foreach (var t in st.EnumerateObject()) Console.WriteLine($"    {t.Name,-24} {N(t.Value.GetInt64()),10}");
                return 0;
            }
            var ingester = root.GetProperty("ingester");
            Console.WriteLine($"{S(root, "dataset_id")}  bundle {S(root, "bundle_id")}");
            Console.WriteLine($"  written  {S(root, "written_utc")} by {S(ingester, "name")} {S(ingester, "version")}");
            Console.WriteLine($"  schema   {S(root, "schema_version")}  qpx {S(root, "qpx_version")}");
            if (root.TryGetProperty("tables", out var tables))
                foreach (var t in tables.EnumerateObject()) Console.WriteLine($"    {t.Name,-24} {N(t.Value.GetInt64()),10}");
            var checks = root.TryGetProperty("reconciliation", out var r) ? r.EnumerateArray().ToList() : [];
            var mismatches = checks.Where(c => c.GetProperty("ok").ValueKind == JsonValueKind.False).ToList();
            Console.WriteLine($"  checks   {checks.Count} run, {mismatches.Count} mismatched");
            foreach (var c in mismatches)
                Console.WriteLine($"    MISMATCH {S(c, "name")}: {PyFormat.FormatG(c.GetProperty("observed").GetDouble())} vs {PyFormat.FormatG(c.GetProperty("expected").GetDouble())}");
            return 0;
        }

        private static int Doctor(string[] argv)
        {
            new Args.Spec($"{Prog} doctor", Summaries["doctor"]).Parse(argv);
            Console.WriteLine($"datarepo {BundleWriter.PackageVersion}  schema {SchemaContract.Version}");
            Console.WriteLine($"  runtime          .NET {Environment.Version}");
            Console.WriteLine($"  mzLib            {typeof(Readers.FileReader).Assembly.GetName().Version}");
            Console.WriteLine($"  parquet          ParquetSharp {typeof(ParquetSharp.ParquetFileReader).Assembly.GetName().Version}");
            // The MCP half. Neither line can make `doctor` fail: a machine that ingests but does not serve is a
            // normal machine (D8 -- aging hosts, we ship).
            Console.WriteLine($"  mcp SDK          ModelContextProtocol {typeof(ModelContextProtocol.Server.McpServer).Assembly.GetName().Version}");
            var entries = DataRepo.Mcp.Mcp.InstalledEntries();
            if (entries.Count > 0)
            {
                foreach (var (name, entry) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var args = (entry.GetValueOrDefault("args") as List<object?> ?? []).Select(a => a?.ToString() ?? "").ToList();
                    var at = args.IndexOf("--catalog");
                    var catalog = at >= 0 && at + 1 < args.Count ? args[at + 1] : "?";
                    Console.WriteLine($"  mcp registered   {name} -> {catalog}{(File.Exists(catalog) ? "" : "  (MISSING)")}");
                }
            }
            else
            {
                Console.WriteLine("  mcp registered   no (`datarepo mcp --catalog <path> --install`)");
                Console.WriteLine($"                   config would be {DataRepo.Mcp.Mcp.ClaudeConfigPath()}");
            }
            Console.WriteLine("ready");
            return 0;
        }
    }
}
