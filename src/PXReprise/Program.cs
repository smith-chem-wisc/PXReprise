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
    public static readonly string[] Verbs = { "version", "profiles", "validate", "census", "qc", "search" };

    /// <summary>Where profiles are read from when --profiles is not given: beside the executable.</summary>
    public static string DefaultProfilesDir => Path.Combine(AppContext.BaseDirectory, "profiles");

    public static async Task<int> Main(string[] argv) => await RunAsync(argv, Console.Out, CancellationToken.None);

    public static async Task<int> RunAsync(string[] argv, TextWriter stdout, CancellationToken ct,
        Func<IProjectSearch>? searchFactory = null)
    {
        try
        {
            var args = new Arguments(argv);
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
                args.AllowOnly("profiles", "out");
                var profiles = ProfileLoader.LoadDirectory(ProfilesDir(args));
                var q = QuestionLoader.Load(file);
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
                    record.Write(outDir);
                    return summary;
                }
                finally
                {
                    client?.Dispose();
                }
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
                    "extra-db", "exclude", "run-date");
                var profile = ResolveProfile(args);
                var request = new Search.SearchRequest(profile, args.Required("organism"), Machine.Load(args.Required("machine")),
                    args.Required("spectra"), args.Required("out"), args.Required("qc"), List(args.Option("extra-db")),
                    List(args.Option("exclude")), args.Option("run-date"));
                return await Search.SearchStage.RunAsync(request, ct).ConfigureAwait(false);
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
