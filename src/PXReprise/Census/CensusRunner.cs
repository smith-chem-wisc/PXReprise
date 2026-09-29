using System.Text;
using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Provenance;
using UsefulProteomicsDatabases;

namespace PXReprise.Census;

public sealed record CensusRow(
    string Accession,
    RelevanceResult Relevance,
    Acquisition Acquisition,
    Route Route,
    PrideProjectSearchResult Record,
    IReadOnlyList<string> KeywordsHit);

public sealed record CensusSummary(
    string Question,
    string SnapshotUtc,
    int Deposits,
    IReadOnlyDictionary<string, int> Relevance,
    IReadOnlyDictionary<string, int> Routes,
    IReadOnlyDictionary<string, int> SearchByProfile,
    IReadOnlyDictionary<string, int> WaitingOn,
    IReadOnlyDictionary<string, int> InScopeInstrument,
    IReadOnlyDictionary<string, int> InScopeLabelling,
    IReadOnlyList<string> FailedKeywords,
    string OutputDirectory,
    int Queued = 0,
    IReadOnlyDictionary<string, int>? NotQueuedNoDatabase = null,
    string? QueueInstalled = null);

/// <summary>
/// Discovery and routing only, with no downloads: which deposits a question would take, which profile would search
/// each, and which wait on a capability. PRIDE is a live index, so this is a dated snapshot, not reproducible by re-running.
/// </summary>
public sealed class CensusRunner(IProjectSearch search)
{
    public async Task<(CensusSummary Summary, IReadOnlyList<CensusRow> Rows)> RunAsync(
        Question q, IReadOnlyDictionary<string, Profile> profiles, string outDir, RunRecord record, CancellationToken ct)
    {
        foreach (string key in q.Profiles)
            if (!profiles.ContainsKey(key))
                throw new ConfigException(q.SourceFile, $"profile {key} is not defined (known: {string.Join(", ", profiles.Keys)})");

        var hits = new SortedDictionary<string, (PrideProjectSearchResult R, SortedSet<string> Kw)>(StringComparer.Ordinal);
        var failed = new List<string>();
        foreach (string kw in q.Keywords)
        {
            List<PrideProjectSearchResult> found;
            try
            {
                found = await search.SearchAsync(kw, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (Cli.Envelope.IsUnavailable(e) && !ct.IsCancellationRequested)
            {
                // Never silently drop a keyword: the census says which ones are missing.
                failed.Add(kw);
                record.Note($"keyword '{kw}' failed after retries: {e.Message}");
                continue;
            }
            foreach (var r in found)
            {
                if (!hits.TryGetValue(r.Accession, out var h)) hits[r.Accession] = h = (r, new SortedSet<string>(StringComparer.Ordinal));
                h.Kw.Add(kw);
            }
        }

        var rows = new List<CensusRow>();
        foreach (var (acc, (r, kws)) in hits)
        {
            var acquisition = AcquisitionClassifier.Classify(r);
            var relevance = Relevance.Evaluate(r, q.Relevance);
            if (relevance.IsIn && q.Organisms.Count > 0 && !r.Organisms.Any(o => q.Organisms.Contains(o))
                && relevance.Verdict != RelevanceVerdict.DecidedInclude)
                relevance = new RelevanceResult(RelevanceVerdict.NotRelevant, "organism: " + string.Join("; ", r.Organisms));
            var route = Router.Assign(acc, acquisition, relevance, q, profiles);
            rows.Add(new CensusRow(acc, relevance, acquisition, route, r, kws.ToList()));
        }

        Directory.CreateDirectory(outDir);
        string tsv = Path.Combine(outDir, "census.tsv");
        WriteTsv(tsv, rows);
        record.Output(tsv);
        var (queue, noDatabase) = Queue(rows, profiles);
        string queueFile = Path.Combine(outDir, "queue.json");
        File.WriteAllText(queueFile, queue.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        record.Output(queueFile);

        var inScope = rows.Where(x => x.Relevance.IsIn).ToList();
        var summary = new CensusSummary(
            q.Name, record.StartedUtc, rows.Count,
            Count(rows, x => Snake(x.Relevance.Verdict.ToString())),
            Count(rows, x => Snake(x.Route.Kind.ToString())),
            Count(rows.Where(x => x.Route.Kind == RouteKind.Search), x => x.Route.Profile!),
            Count(rows.Where(x => x.Route.Kind == RouteKind.WaitingOnCapability), x => x.Route.Profile ?? x.Route.Reason),
            Count(inScope, x => Snake(x.Acquisition.Instrument.ToString())),
            Count(inScope, x => Snake(x.Acquisition.Labelling.ToString())),
            failed, Path.GetFullPath(outDir), queue.Count, noDatabase);
        return (summary, rows);
    }

    /// <summary>
    /// The batch's queue: every deposit routed to search, in accession order, with the organism key of the profile
    /// database it will be searched against. A profile's database keys are PRIDE's common names ("Homo sapiens (human)"
    /// -> human); a deposit whose organisms have no database in its profile is not queued, and is counted by organism.
    /// </summary>
    public static (System.Text.Json.Nodes.JsonArray Queue, IReadOnlyDictionary<string, int> NoDatabase) Queue(
        IEnumerable<CensusRow> rows, IReadOnlyDictionary<string, Profile> profiles)
    {
        var queue = new System.Text.Json.Nodes.JsonArray();
        var missing = new List<string>();
        foreach (var x in rows.Where(x => x.Route.Kind == RouteKind.Search))
        {
            var keys = profiles[x.Route.Profile!].Databases.Keys;
            string? organism = x.Record.Organisms.Select(o => keys.FirstOrDefault(k => OrganismMatches(o, k))).FirstOrDefault(k => k is not null);
            if (organism is null) { missing.Add(x.Record.Organisms.FirstOrDefault() ?? "(none listed)"); continue; }
            queue.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["accession"] = x.Accession, ["title"] = x.Record.Title, ["organism"] = organism,
                ["profile"] = x.Route.Profile, ["files"] = x.Acquisition.MsFileCount,
                ["instruments"] = string.Join("; ", x.Record.Instruments),
            });
        }
        return (queue, Count(missing, o => o));
    }

    /// <summary>"Homo sapiens (human)" matches key "human" (the common name) or "homo_sapiens" / "homo sapiens".</summary>
    internal static bool OrganismMatches(string prideOrganism, string key)
    {
        string o = prideOrganism.ToLowerInvariant(), k = key.ToLowerInvariant().Replace('_', ' ');
        return o.Contains($"({k})", StringComparison.Ordinal) || o.StartsWith(k + " (", StringComparison.Ordinal) || o == k;
    }

    private static IReadOnlyDictionary<string, int> Count<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

    private static string Snake(string s) => ProfileLoader.Snake(s);

    private static void WriteTsv(string path, IEnumerable<CensusRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', "accession", "relevance", "relevance_evidence", "route", "profile", "route_reason",
            "acquisition", "labelling", "instrument_class", "enrichment", "ms_files", "file_types", "organisms",
            "organism_parts", "diseases", "instruments", "submission_type", "publication_date", "keywords_hit", "title"));
        sb.Append('\n');
        foreach (var x in rows)
        {
            var a = x.Acquisition;
            sb.Append(string.Join('\t', new[]
            {
                x.Accession, Snake(x.Relevance.Verdict.ToString()), x.Relevance.Evidence ?? "", Snake(x.Route.Kind.ToString()),
                x.Route.Profile ?? "", x.Route.Reason, Snake(a.Mode.ToString()), Snake(a.Labelling.ToString()),
                Snake(a.Instrument.ToString()), a.Enrichment, a.MsFileCount.ToString(),
                string.Join(";", a.MsFiles.Select(kv => $"{kv.Key}:{kv.Value}")), string.Join(";", x.Record.Organisms),
                string.Join(";", x.Record.OrganismParts), string.Join(";", x.Record.Diseases),
                string.Join(";", x.Record.Instruments), x.Record.SubmissionType,
                x.Record.PublicationDate == default ? "" : x.Record.PublicationDate.ToString("yyyy-MM-dd"),
                string.Join(";", x.KeywordsHit), x.Record.Title,
            }.Select(Clean)));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
