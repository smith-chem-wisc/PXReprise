using System.Text.RegularExpressions;
using PXReprise.Config;

namespace PXReprise.Batch;

/// <summary>
/// D96, G134: what the batch needs to run ptmQtl's engines through <c>datarepo run</c>. The command, the store the
/// question's manifest names, and what the run printed. Pure, so the batch's two steps
/// (<see cref="BatchRunner.DeliverAsync"/>, <see cref="BatchRunner.PublishIfDueAsync"/>) stay small.
/// </summary>
public static partial class PtmQtlBatch
{
    public const string SitePairs = "ptmqtl.site_pairs";
    public const string PoolPairs = "ptmqtl.pool_pairs";

    /// <summary>How long one engine run may take. site_pairs takes seconds to minutes a deposit (its depth null fits it
    /// three more times); pool_pairs reads every member's artefact.</summary>
    public static readonly TimeSpan SitePairsTimeout = TimeSpan.FromHours(1), PoolPairsTimeout = TimeSpan.FromHours(2);

    /// <summary>Whether a profile's deliveries get ptmQtl's engines: label-free only. An isobaric bundle has no
    /// <c>ptm_stoichiometry</c> table, which every ptmQtl engine reads.</summary>
    public static bool Runs(Profile p) => p.QuantMethod == "flashlfq";

    /// <summary>
    /// <c>datarepo run &lt;engine&gt; &lt;datasets&gt; --store &lt;store&gt; --latest --input ...</c>. <c>--latest</c>
    /// takes each dataset's newest bundle, the one the catalog serves (the store's newest-written rule).
    /// </summary>
    public static List<string> RunArgv(string datarepo, string engine, IEnumerable<string> datasets, string store, PtmQtlConfig c)
    {
        var argv = new List<string> { datarepo, "run", engine };
        argv.AddRange(datasets);
        argv.AddRange(new[] { "--store", store, "--latest", "--input", $"enrichment={c.Enrichment}", "--input", $"cysteine={c.Cysteine}" });
        if (c.CaptureRuns is { } runs) argv.AddRange(new[] { "--input", $"capture_runs={runs}" });
        return argv;
    }

    /// <summary>
    /// The store the manifest names (its top-level <c>store:</c>), taken beside the manifest when relative, as datarepo
    /// resolves it (<c>Manifest.Load</c>).
    /// </summary>
    /// <exception cref="InvalidDataException">The manifest names no store.</exception>
    public static string StoreOf(string manifest)
    {
        foreach (string line in File.ReadAllText(manifest).Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("store:", StringComparison.Ordinal)) continue;
            string value = line["store:".Length..].Trim().Trim('"', '\'');
            if (value.Length == 0) break;
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, value));
        }
        throw new InvalidDataException($"{manifest} names no store");
    }

    /// <summary>The artefacts a run wrote and the ones it found already written, from its stdout
    /// (<c>written  &lt;engine&gt; artefact &lt;id&gt;  &lt;path&gt;</c>, <c>done     &lt;engine&gt; artefact &lt;id&gt; already exists</c>).</summary>
    public static (IReadOnlyList<string> Written, IReadOnlyList<string> AlreadyDone) ReadOutput(string stdout)
    {
        var written = new List<string>();
        var done = new List<string>();
        foreach (Match m in RunLine().Matches(stdout.Replace("\r\n", "\n")))
            (m.Groups["kind"].Value == "written" ? written : done).Add(m.Groups["id"].Value);
        return (written, done);
    }

    [GeneratedRegex(@"^(?<kind>written|done)\s+\S+\s+artefact\s+(?<id>[0-9a-f]+)\b", RegexOptions.Multiline)]
    private static partial Regex RunLine();
}
