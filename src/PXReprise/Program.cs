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
    public static readonly string[] Verbs = { "version", "profiles", "validate", "census" };

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

            default:
                throw new UsageException($"unknown verb '{args.Words[0]}'; verbs: {string.Join(", ", Verbs)}");
        }
    }

    private static string ProfilesDir(Arguments args) => args.Option("profiles") ?? DefaultProfilesDir;

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
