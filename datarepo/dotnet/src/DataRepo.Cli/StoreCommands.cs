using DataRepo.Bundle;
using DataRepo.Storage;

namespace DataRepo.Cli;

/// <summary><c>push</c> and <c>pull</c>: whole bundles, engine outputs and study deliveries between this machine's store and a shared one.</summary>
/// <remarks>
/// Ingest and build keep working on a local folder. These two commands are how bundles travel: each person who searches
/// pushes what they ingested to the shared store, and a machine that builds or serves pulls what it needs. Both write
/// once (an existing file is never replaced) and write each folder's record (<c>bundle.json</c>, <c>run.json</c>, <c>study.json</c>) last.
/// </remarks>
internal static class StoreCommands
{
    private const string Prog = "datarepo";

    public static int Push(string[] argv)
    {
        var a = new Args.Spec($"{Prog} push", Cli.Summaries["push"])
            .Positional("destination", "the shared store: a folder, or s3://bucket/prefix (credentials from AWS_PROFILE)")
            .Positional("accession", "datasets whose bundles to push; default is everything in --store: every bundle, engine output and study delivery", "*")
            .Option("--store", Args.Kind.Value, "this machine's store, where ingest wrote the bundles", required: true)
            .Option("--dry-run", Args.Kind.Flag, "list what would be copied, and check each bundle has its tables, without writing")
            .Option("--verbose", Args.Kind.Flag, "name every file written")
            .Parse(argv);
        using var from = new LocalStore(a.Value("--store")!);
        using var to = Stores.Open(a.Positional("destination"));
        return Copy(from, to, a.Positionals("accession"), a.Flag("--dry-run"), a.Flag("--verbose"));
    }

    public static int Pull(string[] argv)
    {
        var a = new Args.Spec($"{Prog} pull", Cli.Summaries["pull"])
            .Positional("source", "the shared store: a folder, or s3://bucket/prefix (credentials from AWS_PROFILE)")
            .Positional("accession", "datasets whose bundles to pull; default is everything in the source: every bundle, engine output and study delivery", "*")
            .Option("--store", Args.Kind.Value, "this machine's store, where the bundles are written", required: true)
            .Option("--dry-run", Args.Kind.Flag, "list what would be copied, and check each bundle has its tables, without writing")
            .Option("--verbose", Args.Kind.Flag, "name every file written")
            .Parse(argv);
        using var from = Stores.Open(a.Positional("source"));
        using var to = new LocalStore(a.Value("--store")!);
        return Copy(from, to, a.Positionals("accession"), a.Flag("--dry-run"), a.Flag("--verbose"));
    }

    private static int Copy(IStore from, IStore to, IReadOnlyList<string> accessions, bool dryRun, bool verbose)
    {
        var items = StoreTransfer.FindAsync(from, accessions).GetAwaiter().GetResult();
        var missing = accessions.Where(x => items.All(b => b.Dataset != x)).ToList();
        foreach (var accession in missing) Console.Error.WriteLine($"{from.Location}: no bundle for {accession}");
        if (items.Count == 0)
        {
            Console.Error.WriteLine($"{from.Location}: nothing to copy");
            return 1;
        }
        int copied = 0, present = 0;
        foreach (var item in items)
        {
            var outcome = StoreTransfer.CopyAsync(from, to, item, dryRun, verbose ? Console.WriteLine : null).GetAwaiter().GetResult();
            Console.WriteLine(outcome switch
            {
                TransferOutcome.Copied => $"copied   {item}",
                TransferOutcome.Present => $"present  {item}",
                _ => $"would copy {item}",
            });
            if (outcome == TransferOutcome.Present) present++; else copied++;
        }
        Console.WriteLine($"{(dryRun ? "would copy" : "copied")} {copied}, already present {present}: {from.Location} -> {to.Location}");
        return missing.Count == 0 ? 0 : 1;
    }
}
