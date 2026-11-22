using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Runner;

namespace PXReprise.DataCli;

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
            .Positional("accession", "datasets to run on (logs, go.annotate_databases, kegg.reactome_membership: their searched databases; go.resolve_members, ptmqtl: their searches; none for logs.register_orthology, go.categorize_terms or kegg.reactome_release)", "*")
            .Option("--store", Args.Kind.Value, "the instance's bundle store", required: true)
            .Option("--input", Args.Kind.Append, "an input file by role; repeatable", metavar: "ROLE=PATH")
            .Option("--bundle", Args.Kind.Append, "pin a dataset to one bundle id", metavar: "PXD=ID")
            .Option("--latest", Args.Kind.Flag, "take each dataset's newest bundle")
            .Parse(argv);
        var engine = a.Positional("engine");
        // Retired in 1.5.0: say what replaced it, rather than only that it is not a choice.
        if (engine == PXReprise.Catalog.Runner.GoEngine) throw new RunnerException(GoReferenceEngines.Retired);
        if (!EngineRunner.Engines.Contains(engine))
            throw new Args.UsageException("usage: datarepo run [-h] --store STORE [--input ROLE=PATH] [--bundle PXD=ID] [--latest] engine [accession ...]",
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
        var accessions = a.Positionals("accession");
        if (engine == OrthologyEngine.Engine)
        {
            // A snapshot belongs to no dataset: it is registered once per store and cited by every catalog built there.
            if (accessions.Count > 0)
                throw new RunnerException($"{engine} registers one snapshot for the whole store and takes no accession (given {string.Join(", ", accessions)}).");
            var registered = OrthologyEngine.Run(store, inputs);
            foreach (var reference in registered.Written)
            {
                var snapshot = reference.Record.GetValueOrDefault("snapshot") as IReadOnlyDictionary<string, object?>;
                Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
                Console.WriteLine($"  snapshot {snapshot?.GetValueOrDefault("tag")}  id {snapshot?.GetValueOrDefault("snapshot_id")}");
                Console.WriteLine($"  read it  {System.IO.Path.Combine(reference.Path, OrthologyEngine.SnapshotDir)} through its views.sql");
            }
            foreach (var reference in registered.AlreadyDone)
                Console.WriteLine($"done     {reference.Engine} artefact {reference.ArtefactId} already exists; nothing re-run");
            return 0;
        }
        if (engine == GoReferenceEngines.CategorizeTerms)
        {
            // An ontology and a map belong to no dataset: one artefact per (go.obo, map) serves every catalog built here.
            if (accessions.Count > 0)
                throw new RunnerException($"{engine} categorizes one go.obo under one map for the whole store and takes no accession (given {string.Join(", ", accessions)}).");
            var categorized = GoReferenceEngines.CategorizeTermsRun(store, inputs, GoReferenceEngines.CategoryDefinitionId);
            foreach (var reference in categorized.Written)
            {
                var summary = reference.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;
                Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
                Console.WriteLine($"  rows     {PyFormat.Repr(reference.RowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))}");
                Console.WriteLine($"  release  {summary?.GetValueOrDefault("go_release")}  map {PyFormat.Repr(summary?.GetValueOrDefault("category_map"))}");
            }
            foreach (var reference in categorized.AlreadyDone)
                Console.WriteLine($"done     {reference.Engine} artefact {reference.ArtefactId} already exists; nothing re-run");
            return 0;
        }
        if (engine == ReactomeEngines.ReleaseEngine)
        {
            // A release belongs to no dataset: one artefact per release serves every catalog built here.
            if (accessions.Count > 0)
                throw new RunnerException($"{engine} reads one Reactome release for the whole store and takes no accession (given {string.Join(", ", accessions)}).");
            var released = ReactomeEngines.ReleaseRun(store, inputs, ReactomeEngines.DefinitionId);
            foreach (var reference in released.Written)
            {
                var summary = reference.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;
                Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
                Console.WriteLine($"  rows     {PyFormat.Repr(reference.RowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))}");
                Console.WriteLine($"  release  Reactome {summary?.GetValueOrDefault("reactome_release")}  roots {summary?.GetValueOrDefault("roots")}");
            }
            foreach (var reference in released.AlreadyDone)
                Console.WriteLine($"done     {reference.Engine} artefact {reference.ArtefactId} already exists; nothing re-run");
            return 0;
        }
        if (accessions.Count == 0)
            throw new Args.UsageException("usage: datarepo run [-h] --store STORE [--input ROLE=PATH] [--bundle PXD=ID] [--latest] engine [accession ...]",
                $"{engine} needs at least one accession");
        var bundles = PickBundles(store, accessions, pins, a.Flag("--latest"));
        if (engine.StartsWith("ptmqtl.", StringComparison.Ordinal))
        {
            var ran = engine switch
            {
                PtmQtlEngine.SitePairsEngine => PtmQtlEngine.SitePairs(store, bundles, inputs),
                PtmQtlEngine.PoolPairsEngine => PtmQtlEngine.PoolPairs(store, bundles, inputs),
                _ => PtmQtlEngine.SiteTraits(store, bundles, inputs),
            };
            foreach (var reference in ran.Written)
            {
                var summary = reference.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;
                Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
                Console.WriteLine($"  rows     {PyFormat.Repr(reference.RowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))}");
                if (summary?.GetValueOrDefault("search_gate") is IReadOnlyDictionary<string, object?> gate && gate.GetValueOrDefault("lacks_met_removed_protein_n_term") is true)
                    Console.WriteLine($"  gate     searched by {gate["search_engine"]} {gate["search_engine_version"]}: lacks protein N-termini after Met removal (ptmQtl S6); nothing filled in");
            }
            foreach (var reference in ran.AlreadyDone)
                Console.WriteLine($"done     {reference.Engine} artefact {reference.ArtefactId} already exists; nothing re-run");
            return 0;
        }
        var go = engine is GoReferenceEngines.AnnotateDatabases or GoReferenceEngines.ResolveMembers or ReactomeEngines.Membership;
        // go's and kegg's published definition ids; each run refuses a call without one.
        var result = engine switch
        {
            GoReferenceEngines.AnnotateDatabases => GoReferenceEngines.AnnotateDatabasesRun(store, bundles, inputs, GoReferenceEngines.AccessionDefinitionId),
            GoReferenceEngines.ResolveMembers => GoReferenceEngines.ResolveMembersRun(store, bundles, inputs, GoReferenceEngines.MemberDefinitionId),
            ReactomeEngines.Membership => ReactomeEngines.MembershipRun(store, bundles, inputs, ReactomeEngines.DefinitionId),
            _ => LogsEngine.Run(store, bundles, inputs),
        };
        foreach (var reference in result.Written)
        {
            var summary = reference.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;
            Console.WriteLine($"written  {reference.Engine} artefact {reference.ArtefactId}  {reference.Path}");
            Console.WriteLine($"  rows     {PyFormat.Repr(reference.RowCounts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))}");
            if (engine == GoReferenceEngines.AnnotateDatabases)
            {
                var unresolved = summary?.GetValueOrDefault("unresolved_go_ids") as IEnumerable<object?> ?? [];
                Console.WriteLine($"  database {summary?.GetValueOrDefault("database")}  entries {summary?.GetValueOrDefault("entries")}, "
                    + $"{summary?.GetValueOrDefault("entries_without_go")} without GO; unresolved GO ids (skipped, go D35) {unresolved.Count()}");
                continue;
            }
            if (engine == GoReferenceEngines.ResolveMembers)
            {
                Console.WriteLine($"  members  {PyFormat.Repr(summary?.GetValueOrDefault("resolution_counts"))}");
                continue;
            }
            if (engine == ReactomeEngines.Membership)
            {
                Console.WriteLine($"  database {summary?.GetValueOrDefault("database")}  entries {PyFormat.Repr(summary?.GetValueOrDefault("entries_by_status"))}");
                Console.WriteLine($"  species  {PyFormat.Repr(summary?.GetValueOrDefault("species_codes"))}; unvalidated {PyFormat.Repr(summary?.GetValueOrDefault("unvalidated_organisms"))}; "
                    + $"foreign references dropped {summary?.GetValueOrDefault("dropped_foreign_references")}");
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
