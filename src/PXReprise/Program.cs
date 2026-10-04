using PXReprise.Census;
using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Provenance;
using UsefulProteomicsDatabases;

namespace PXReprise;

/// <summary>
/// <c>pxreprise &lt;verb&gt; [args]</c>. One JSON envelope on stdout, diagnostics on stderr, exit 0 ok / 1 handled
/// failure / 2 usage: the mzLib bridge's contract, so the engine is scriptable from any language.
/// </summary>
public static class Program
{
    /// <summary>The verbs, listed by the <c>version</c> verb. Keep in step with <see cref="Dispatch"/>.</summary>
    public static readonly string[] Verbs = { "version", "profiles", "validate", "census", "fetch", "qc", "search", "qc-payload", "batch" };

    /// <summary>Where profiles are read from when --profiles is not given: beside the executable.</summary>
    public static string DefaultProfilesDir => Path.Combine(AppContext.BaseDirectory, "profiles");

    public static async Task<int> Main(string[] argv) => await RunAsync(argv, Console.Out, CancellationToken.None);

    public static async Task<int> RunAsync(string[] argv, TextWriter stdout, CancellationToken ct,
        Func<IProjectSearch>? searchFactory = null)
    {
        try
        {
            var args = new Arguments(argv, new HashSet<string>(StringComparer.Ordinal) { "queue" });
            if (args.Words.Count == 0) throw new UsageException($"a verb is required: {string.Join(", ", Verbs)}");
            object? data = await Dispatch(args, ct, searchFactory).ConfigureAwait(false);
            return Envelope.Ok(stdout, data);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return Envelope.Fail(stdout, e);
        }
    }

    private static async Task<object?> Dispatch(Arguments args, CancellationToken ct, Func<IProjectSearch>? searchFactory)
    {
        switch (args.Words[0])
        {
            case "version":
                Positional(args, 0);
                args.AllowOnly();
                return new { tools = RunRecord.Versions(), verbs = Verbs };

            case "profiles":
                Positional(args, 0);
                args.AllowOnly("profiles");
                return ProfileLoader.LoadDirectory(ProfilesDir(args)).Values.Select(p => new
                {
                    key = p.Key, status = p.Status, p.Description, metamorpheus = p.MetaMorpheus,
                    organisms = p.Databases.Keys,
                });

            case "validate":
            {
                string file = Positional(args, 1)[0];
                args.AllowOnly("profiles");
                var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                var q = QuestionLoader.Load(file);
                var missing = q.Profiles.Where(p => !profiles.ContainsKey(p)).ToList();
                if (missing.Count > 0)
                    throw new ConfigException(file, $"profile(s) not defined: {string.Join(", ", missing)}");
                return new
                {
                    question = q.Name, q.Profiles, keywords = q.Keywords.Count, q.Organisms,
                    decisions = q.Relevance.Decisions.Count, holds = q.Holds.Count, overlays = q.Overlays.Keys,
                };
            }

            case "census":
            {
                string file = Positional(args, 1)[0];
                // --queue: also install the census's queue.json as the question's batch queue, the input of `batch run`.
                args.AllowOnly("profiles", "out", "queue");
                var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                var q = QuestionLoader.Load(file);
                string? installQueue = null;
                if (args.Has("queue"))
                {
                    installQueue = (q.Batch ?? throw new ConfigException(q.SourceFile, "--queue needs a [batch] table naming the queue file")).Queue;
                    // A batch's queue is its running order; replacing it under a batch is the operator's decision, not a census's.
                    if (File.Exists(installQueue))
                        throw new UsageException($"{installQueue} already exists; a batch may be using it. Delete or rename it first to replace it.");
                }
                string outDir = args.Option("out")
                                ?? Path.Combine(Path.GetDirectoryName(q.SourceFile)!, "census", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                var record = new RunRecord("census");
                record.Input(q.SourceFile);
                foreach (string key in q.Profiles)
                    if (profiles.TryGetValue(key, out _))
                        record.Note($"profile {key}");
                record.Note("PRIDE is a live index: this census is a dated snapshot, not reproducible by re-running.");

                IProjectSearch search;
                PrideArchiveClient? client = null;
                if (searchFactory is not null) search = searchFactory();
                else search = new PrideProjectSearch(client = new PrideArchiveClient());
                try
                {
                    var (summary, _) = await new CensusRunner(search).RunAsync(q, profiles, outDir, record, ct).ConfigureAwait(false);
                    string summaryFile = Path.Combine(outDir, "summary.json");
                    await File.WriteAllTextAsync(summaryFile,
                        System.Text.Json.JsonSerializer.Serialize(summary, Envelope.Json), ct).ConfigureAwait(false);
                    record.Output(summaryFile);
                    if (installQueue is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(installQueue)!);
                        File.Copy(Path.Combine(outDir, "queue.json"), installQueue);
                        record.Output(installQueue);
                    }
                    record.Write(outDir);
                    return summary with { QueueInstalled = installQueue };
                }
                finally
                {
                    client?.Dispose();
                }
            }

            case "fetch":
            {
                string accession = Positional(args, 1)[0];
                args.AllowOnly("out", "machine", "pick", "max-files", "max-file-mb", "parallel", "attempts");
                var machine = Machine.Load(args.Required("machine"));
                var pick = ProfileLoader.ParseEnum<Fetch.Pick>("--pick", "pick", args.Option("pick") ?? "all");
                var request = new Fetch.FetchRequest(accession, args.Required("out"), pick,
                    int.Parse(args.Option("max-files") ?? "60"), int.Parse(args.Option("max-file-mb") ?? "5000"), ".raw",
                    int.Parse(args.Option("parallel") ?? "4"), int.Parse(args.Option("attempts") ?? "8"),
                    TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), machine.WorkRoot, TimeSpan.FromMinutes(machine.FetchStallMinutes));
                using var client = new PrideArchiveClient();
                var manifest = await Fetch.FetchStage.RunAsync(request, new Fetch.PrideFiles(client), ct).ConfigureAwait(false);
                return new { accession, files = manifest["files"]!.AsArray().Count, manifest = Path.GetFullPath(Path.Combine(request.OutDir, "fetch_manifest.json")) };
            }

            case "qc":
            {
                Positional(args, 0);
                args.AllowOnly("spectra", "out", "profile", "profile-file", "profiles", "machine", "run-date");
                var profile = ResolveProfile(args);
                var machine = Machine.Load(args.Required("machine"));
                string outDir = args.Required("out");
                Directory.CreateDirectory(outDir);
                string paramsFile = Path.Combine(outDir, "params.json");
                await File.WriteAllTextAsync(paramsFile, System.Text.Json.JsonSerializer.Serialize(
                    new { profile = profile.Key, qc = profile.Qc, work_root = machine.WorkRoot }, Envelope.Json), ct).ConfigureAwait(false);
                var (report, allPass) = Qc.QcStage.Run(args.Required("spectra"), outDir, profile, machine, paramsFile, args.Option("run-date"));
                return new
                {
                    all_pass = allPass, files = report.Count,
                    failed = report.Where(kv => !kv.Value!["pass"]!.GetValue<bool>()).Select(kv => kv.Key).ToList(),
                    report = Path.GetFullPath(Path.Combine(outDir, "qc_report.json")),
                };
            }

            case "search":
            {
                Positional(args, 0);
                args.AllowOnly("spectra", "qc", "out", "organism", "profile", "profile-file", "profiles", "machine",
                    "extra-db", "exclude", "run-date", "accession");
                var profile = ResolveProfile(args);
                var request = new Search.SearchRequest(profile, args.Required("organism"), Machine.Load(args.Required("machine")),
                    args.Required("spectra"), args.Required("out"), args.Required("qc"), List(args.Option("extra-db")),
                    List(args.Option("exclude")), args.Option("run-date"), args.Option("accession"));
                return await Search.SearchStage.RunAsync(request, ct).ConfigureAwait(false);
            }

            case "qc-payload":
            {
                // pxreprise qc-payload <search_dir> <qc_dir> <out_dir> --accession PXD [--run-date D] [--machine m.toml]
                var pos = Positional(args, 3);
                args.AllowOnly("accession", "run-date", "machine", "metamorpheus");
                string accession = args.Required("accession");
                var machine = args.Option("machine") is { } mf ? Machine.Load(mf) : null;
                string outDir = pos[2];
                Directory.CreateDirectory(outDir);
                string paramsFile = Path.Combine(outDir, "params.json");
                await File.WriteAllTextAsync(paramsFile, System.Text.Json.JsonSerializer.Serialize(
                    new { stage = "qc_payload", accession, run_date = args.Option("run-date"), qc_python = machine?.QcPython }, Envelope.Json), ct).ConfigureAwait(false);
                string workRoot = machine?.WorkRoot ?? Path.GetDirectoryName(Path.GetFullPath(pos[0]))!;
                var o = Qc.QcPayloadStage.Run(pos[0], pos[1], outDir, accession, args.Option("run-date"), workRoot, paramsFile,
                    machine?.QcPython, args.Option("metamorpheus"));
                return new
                {
                    payload = o.PayloadFile, files = o.Files, protein_groups_quantified = o.ProteinGroupsQuantified,
                    bin_edges_source = o.BinSource, validate = o.Validate, render = o.Render, findings = o.FindingsFile, flags = o.Flags,
                    provenance = o.ProvenanceFile,
                };
            }

            case "batch":
            {
                // pxreprise batch run|status|stop <question.toml> [--machine m.toml] [--run-date D]
                // pxreprise batch retry <question.toml> <PXD> --reason "..."
                var rest = args.Words.Skip(1).ToList();
                if (!(rest.Count == 2 && rest[0] is ("run" or "status" or "stop")) && !(rest.Count == 3 && rest[0] == "retry"))
                    throw new UsageException("usage: pxreprise batch run|status|stop <question.toml> --machine m.toml, or batch retry <question.toml> <PXD> --reason \"...\"");
                args.AllowOnly("machine", "profiles", "run-date", "reason");
                var q = QuestionLoader.Load(rest[1]);
                var b = q.Batch ?? throw new ConfigException(q.SourceFile, "a batch needs a [batch] table");
                Directory.CreateDirectory(b.StateDir);
                string stop = Path.Combine(b.StateDir, "STOP"), stateFile = Path.Combine(b.StateDir, "state.json");
                switch (rest[0])
                {
                    case "retry":
                        return Batch.BatchRunner.Requeue(b.StateDir, b.RunRoot, rest[2], args.Required("reason"));
                    case "stop":
                        // Taken at the top of the loop: the search in flight, its cleanup and delivery finish first.
                        await File.WriteAllTextAsync(stop, $"Stop requested {DateTime.UtcNow:o}", ct).ConfigureAwait(false);
                        return new { stop_written = stop };
                    case "status":
                    {
                        var ds = File.Exists(stateFile) ? System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false))!["datasets"]!.AsObject() : new System.Text.Json.Nodes.JsonObject();
                        string pidFile = Path.Combine(b.StateDir, "driver.pid");
                        return new
                        {
                            driver_pid = File.Exists(pidFile) ? (await File.ReadAllTextAsync(pidFile, ct).ConfigureAwait(false)).Trim() : null,
                            stop_pending = File.Exists(stop),
                            datasets = ds.Count,
                            by_status = ds.GroupBy(kv => kv.Value?["status"]?.GetValue<string>() ?? "?").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                        };
                    }
                    default:
                    {
                        var machine = Machine.Load(args.Required("machine"));
                        var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                        if (File.Exists(stop)) File.Delete(stop);
                        using var client = new PrideArchiveClient();
                        var runner = new Batch.BatchRunner(q, profiles, machine, new Fetch.PrideFiles(client), new PrideProjectSearch(client),
                            args.Option("run-date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd"));
                        await runner.RunAsync(Batch.BatchRunner.LoadQueue(b.Queue), ct).ConfigureAwait(false);
                        return new { finished = true };
                    }
                }
            }

            default:
                throw new UsageException($"unknown verb '{args.Words[0]}'; verbs: {string.Join(", ", Verbs)}");
        }
    }

    private static string ProfilesDir(Arguments args) => args.Option("profiles") ?? DefaultProfilesDir;

    /// <summary>A shipped profile by key (<c>--profile label-free-dda@1</c>), or one file (<c>--profile-file</c>).</summary>
    private static Profile ResolveProfile(Arguments args)
    {
        string? key = args.Option("profile"), file = args.Option("profile-file");
        if ((key is null) == (file is null)) throw new UsageException("give exactly one of --profile or --profile-file");
        if (file is not null) return ProfileLoader.Load(file);
        var all = ProfileLoader.LoadDirectory(ProfilesDir(args));
        return all.TryGetValue(key!, out var p) ? p : throw new UsageException($"no profile {key} (known: {string.Join(", ", all.Keys)})");
    }

    private static IReadOnlyList<string> List(string? csv) =>
        csv is null ? Array.Empty<string>() : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Exactly <paramref name="n"/> positional arguments after the verb.</summary>
    private static IReadOnlyList<string> Positional(Arguments args, int n)
    {
        var rest = args.Words.Skip(1).ToList();
        if (rest.Count != n)
            throw new UsageException(n == 0
                ? $"'{args.Words[0]}' takes no positional arguments"
                : $"'{args.Words[0]}' takes {n} positional argument(s), got {rest.Count}");
        return rest;
    }
}
