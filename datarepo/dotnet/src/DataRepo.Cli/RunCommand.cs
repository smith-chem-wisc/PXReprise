using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Runner;

namespace DataRepo.Cli;

/// <summary><c>datarepo run &lt;engine&gt; &lt;PXD...&gt; --store ...</c>: run a released engine on stored data (the operator's step).</summary>
internal static class RunCommand
{
    /// <summary>One bundle per accession from the store: pinned, the only one, or the newest with --latest.</summary>
    private static List<BundleRef> PickBundles(string store, IReadOnlyList<string> accessions, IReadOnlyDictionary<string, string> pins, bool latest)
    {
        var chosen = new List<BundleRef>();
        foreach (var accession in accessions)
        {
            var candidates = CatalogBuilder.DiscoverBundles(store, accession);
            if (candidates.Count == 0) throw new CatalogException($"{accession} has no bundle under {BundleWriter.PathText(store)}");
            if (pins.TryGetValue(accession, out var pin) && pin.Length > 0)
            {
                var matches = candidates.Where(c => c.BundleId.StartsWith(pin, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) throw new CatalogException($"{accession}: --bundle {pin} matches {matches.Count} bundles");
                chosen.Add(matches[0]);
            }
            else if (candidates.Count == 1 || latest) chosen.Add(candidates[^1]);
            else
                throw new CatalogException(
                    $"{accession} has {candidates.Count} bundles; pin one with --bundle {accession}=<id>, or pass --latest");
        }
        return chosen;
    }

    public static int Run(string[] argv)
    {
        var a = new Args.Spec("datarepo run", Cli.Summaries["run"])
            .Positional("engine", $"the engine to run: {string.Join(", ", EngineRunner.Engines)}")
            .Positional("accession", "datasets to run on (logs: their searched databases; go: their protein groups)", "+")
            .Option("--store", Args.Kind.Value, "the instance's bundle store", required: true)
            .Option("--input", Args.Kind.Append, "an input file by role; repeatable", metavar: "ROLE=PATH")
            .Option("--bundle", Args.Kind.Append, "pin a dataset to one bundle id", metavar: "PXD=ID")
            .Option("--latest", Args.Kind.Flag, "take each dataset's newest bundle")
            .Parse(argv);
        var engine = a.Positional("engine");
        if (!EngineRunner.Engines.Contains(engine))
            throw new Args.UsageException("usage: datarepo run [-h] --store STORE [--input ROLE=PATH] [--bundle PXD=ID] [--latest] engine accession [accession ...]",
                $"argument engine: invalid choice: '{engine}' (choose from {string.Join(", ", EngineRunner.Engines.Select(e => $"'{e}'"))})");
        var store = a.Value("--store")!;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in a.List("--input"))
        {
            var at = item.IndexOf('=');
            if (at <= 0 || at == item.Length - 1)
            {
                Console.Error.WriteLine($"--input {PyFormat.Repr(item)}: expected ROLE=PATH");
                return 2;
            }
            var role = item[..at];
            if (inputs.ContainsKey(role))
            {
                Console.Error.WriteLine($"--input {role} given twice");
                return 2;
            }
            inputs[role] = item[(at + 1)..];
        }
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in a.List("--bundle"))
        {
            var at = value.IndexOf('=');
            if (at < 0 || at == value.Length - 1) throw new CatalogException($"--bundle wants <accession>=<bundle id>, got {PyFormat.Repr(value)}");
            pins[value[..at]] = value[(at + 1)..];
        }
        var bundles = PickBundles(store, a.Positionals("accession"), pins, a.Flag("--latest"));
        var go = engine == GoEngine.Engine;
        // go's published definition id (go D40); GoEngine.Run refuses a call without one.
        var result = go ? GoEngine.Run(store, bundles, inputs, GoEngine.DefinitionId) : LogsEngine.Run(store, bundles, inputs);
        foreach (var reference in result.Written)
        {
            var summary = reference.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;
            Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
            Console.WriteLine($"  rows     {PyFormat.Repr(reference.RowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))}");
            if (go)
            {
                Console.WriteLine($"  groups   {PyFormat.Repr(summary?.GetValueOrDefault("group_status_counts"))}");
                var unresolved = summary?.GetValueOrDefault("unresolved_go_ids") as IEnumerable<object?> ?? [];
                Console.WriteLine($"  unresolved GO ids (skipped, go D35) {unresolved.Count()}");
                continue;
            }
            Console.WriteLine($"  outcomes {PyFormat.Repr(summary?.GetValueOrDefault("outcome_counts"))}");
            foreach (var caveat in summary?.GetValueOrDefault("caveats") as IEnumerable<object?> ?? [])
                Console.WriteLine($"  caveat   {caveat}");
        }
        foreach (var reference in result.AlreadyDone)
            Console.WriteLine($"done     {reference.Engine} artefact {reference.ArtefactId} already exists; nothing re-run");
        foreach (var name in result.SkippedContaminant)
            Console.WriteLine(go
                ? $"skipped  {name}: a contaminant database is not annotation input"
                : $"skipped  {name}: a contaminant database is never resolved");
        return 0;
    }
}
