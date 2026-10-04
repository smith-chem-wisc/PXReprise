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
// build, catalog, query, publish and run arrive with their ports (design/CSHARP_PORT.md).
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
return Cli.Main(args);

namespace DataRepo.Cli
{
    internal static class Cli
    {
        private const string Prog = "datarepo";

        private static readonly string[] Commands = ["ingest", "study", "manifest", "inspect", "mcp", "site", "doctor"];
        private static readonly string[] Pending = ["build", "catalog", "query", "publish", "run"];

        public static int Main(string[] argv)
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help")
            {
                var usage = $"usage: {Prog} [-h] [--version] {{{string.Join(",", Commands.Concat(Pending))}}} ...";
                if (argv.Length == 0)
                {
                    Console.Error.WriteLine(usage);
                    Console.Error.WriteLine($"{Prog}: error: the following arguments are required: command");
                    return 2;
                }
                Console.WriteLine(usage);
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
                    "mcp" => DataRepo.Mcp.McpCommand.Run(rest, Console.Out, Console.Error),
                    "site" => DataRepo.Site.SiteCommand.Run(rest, Console.Out, Console.Error),
                    "doctor" => Doctor(),
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
            Console.Error.WriteLine($"usage: {Prog} [-h] [--version] {{{string.Join(",", Commands.Concat(Pending))}}} ...");
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

        private static void PrintResult(IngestResult result, bool verbose)
        {
            Console.WriteLine($"  bundle   {result.BundlePath}");
            if (result.Skipped)
                Console.WriteLine("  unchanged: inputs and ingester match the bundle already written; --overwrite to rebuild");
            Console.WriteLine($"  tables   {string.Join(", ", result.RowCounts.Select(kv => $"{kv.Key} {N(kv.Value)}"))}");
            foreach (var check in result.Checks)
            {
                if (check["ok"] is false)
                    Console.WriteLine($"  MISMATCH {check["name"]}: bundle {G(check["observed"])} vs producer {G(check["expected"])} ({check["source"]})");
                else if (verbose && check["observed"] is not null)
                    Console.WriteLine($"  ok       {check["name"]}: {G(check["observed"])}");
            }
            if (result.UnresolvedModifications.Count > 0)
                Console.WriteLine($"  WARN     {result.UnresolvedModifications.Count} unresolved modification(s): {string.Join(", ", result.UnresolvedModifications.Keys.Order(StringComparer.Ordinal))}");
            if (result.UnmatchedRuns.Count > 0)
                Console.WriteLine($"  WARN     run names with no deposited file: {string.Join(", ", result.UnmatchedRuns.Keys.Order(StringComparer.Ordinal))}");
            var warnings = result.Findings.Where(f => f["severity"] is "warning" or "error").ToList();
            if (warnings.Count > 0)
                Console.WriteLine($"  findings {warnings.Count} open warning(s): {string.Join(", ", warnings.Select(f => (string)f["code"]!).Distinct().Order(StringComparer.Ordinal))}");
        }

        private static int Ingest(string[] argv)
        {
            var args = new Args.Spec($"{Prog} ingest")
                .Positional("manifest", "the producing instance's manifest.yaml")
                .Positional("accession", "datasets to ingest; default is every 'include'", "*")
                .Option("--store", Args.Kind.Value, "where to write bundles; default is the manifest's store")
                .Option("--mm-settings", Args.Kind.Value, "MetaMorpheus install to read modification definitions from")
                .Option("--overwrite", Args.Kind.Flag, "rebuild a bundle that already exists")
                .Option("--verbose", Args.Kind.Flag, "show every reconciliation check", shortName: "-v")
                .Parse(argv);
            var manifest = Manifest.Load(args.Positional("manifest"));
            var accessions = args.Positionals("accession").Count > 0
                ? args.Positionals("accession").ToList()
                : manifest.IngestableEntries().Select(e => e.Accession).ToList();
            if (accessions.Count == 0)
            {
                Console.Error.WriteLine($"{manifest.Path}: no dataset has status 'include'");
                return 1;
            }
            var failures = 0;
            foreach (var accession in accessions)
            {
                Console.WriteLine(accession);
                IngestResult result;
                try
                {
                    var entry = manifest.Dataset(accession);
                    result = Ingester.IngestDataset(manifest, entry, args.Value("--store"), args.Value("--mm-settings"), args.Flag("--overwrite"));
                }
                catch (DatasetExcludedException e)
                {
                    Console.WriteLine($"  refused  {e.Message}");
                    failures++;
                    continue;
                }
                catch (DataRepoException e)
                {
                    Console.Error.WriteLine($"  failed   {e.Message}");
                    failures++;
                    continue;
                }
                PrintResult(result, args.Flag("--verbose"));
            }
            return failures > 0 ? 1 : 0;
        }

        private static int StudyCommand(string[] argv)
        {
            var args = new Args.Spec($"{Prog} study")
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
            var args = new Args.Spec($"{Prog} manifest").Positional("manifest", "").Parse(argv);
            var manifest = Manifest.Load(args.Positional("manifest"));
            Console.WriteLine($"instance   {manifest.Instance} (manifest v{manifest.ManifestVersion})");
            Console.WriteLine($"work root  {manifest.WorkRoot}");
            Console.WriteLine($"store      {manifest.Store}");
            if (manifest.Licence is not null && PyFormat.Str(manifest.Licence).Length > 0)
                Console.WriteLine($"licence    {PyFormat.Str(manifest.Licence)} - {(manifest.Credit is null ? "no credit line" : PyFormat.Str(manifest.Credit))}");
            Console.WriteLine();
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
            }
            return 0;
        }

        private static int Inspect(string[] argv)
        {
            var args = new Args.Spec($"{Prog} inspect")
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

        private static int Doctor()
        {
            Console.WriteLine($"datarepo {BundleWriter.PackageVersion}  schema {Tables.SchemaVersion}");
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
