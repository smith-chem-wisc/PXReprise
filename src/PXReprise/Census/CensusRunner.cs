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
    IReadOnlyList<string> KeywordsHit,
    IReadOnlyList<string> Organisms,
    string OrganismSource,
    string? Watch = null,
    string? ReferenceGroupEvidence = null)
{
    /// <summary>Queued by <c>census --queue</c>: routed to search, and not on the watch list.</summary>
    public bool Queueable => Route.Kind == RouteKind.Search && Watch is null;
}

/// <summary>Why a deposit is on the census's watch list (<c>watch.tsv</c>) and never queued.</summary>
public static class WatchReason
{
    /// <summary>Found only by the question's <c>watch_keywords</c>.</summary>
    public const string WatchList = "watch_list";
    /// <summary>Found only by <c>disease_keywords</c>, and its record names no reference group (PXR-A27).</summary>
    public const string NoReferenceGroup = "no_reference_group_found";
}

/// <summary>Where a row's <see cref="CensusRow.Organisms"/> came from: the census acts on these, so it says which.</summary>
public static class OrganismSource
{
    /// <summary>PRIDE's project record: the source of record (pride G31).</summary>
    public const string Project = "project";
    /// <summary>The search index, for a deposit the census does not act on (out of scope): discovery only.</summary>
    public const string Search = "search";
    /// <summary>The search index, because the project record could not be had; SDRF column headers removed.</summary>
    public const string SearchFallback = "search_fallback";
}

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
    string? QueueInstalled = null,
    IReadOnlyDictionary<string, int>? OrganismSources = null,
    IReadOnlyDictionary<string, int>? QueuedByKeyword = null,
    IReadOnlyDictionary<string, int>? Watched = null);

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
        foreach (string kw in q.Keywords.Concat(q.More.Disease).Concat(q.More.Watch))
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
            // The search index is for discovery only: its metadata fields can carry SDRF column headers as terms
            // (PXD043476, pride 001). A deposit the census acts on takes its organisms from the project record,
            // fetched only for those, so a census stays one search per keyword plus one record per in-scope deposit.
            var (organisms, source) = relevance.IsIn
                ? await OrganismsOfRecordAsync(acc, r, record, ct).ConfigureAwait(false)
                : (r.Organisms, OrganismSource.Search);
            if (relevance.IsIn && q.Organisms.Count > 0 && !organisms.Any(o => q.Organisms.Contains(o))
                && relevance.Verdict != RelevanceVerdict.DecidedInclude)
                relevance = new RelevanceResult(RelevanceVerdict.NotRelevant, "organism: " + string.Join("; ", organisms));
            var route = Router.Assign(acc, acquisition, relevance, q, profiles);
            var (watch, reference) = Watch(q, kws, route, r);
            rows.Add(new CensusRow(acc, relevance, acquisition, route, r, kws.ToList(), organisms, source, watch, reference));
        }

        Directory.CreateDirectory(outDir);
        string tsv = Path.Combine(outDir, "census.tsv");
        WriteTsv(tsv, rows);
        record.Output(tsv);
        string review = Path.Combine(outDir, "review.md");
        CensusReview.Write(review, q, rows, record.StartedUtc);
        record.Output(review);
        var (queue, noDatabase) = Queue(rows, profiles);
        string queueFile = Path.Combine(outDir, "queue.json");
        File.WriteAllText(queueFile, queue.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        record.Output(queueFile);
        var watched = rows.Where(x => x.Watch is not null).ToList();
        if (q.More.Disease.Count > 0 || q.More.Watch.Count > 0)
        {
            // PXR-A26: the watch list is its own table: discovered and screened, never queued.
            string watchFile = Path.Combine(outDir, "watch.tsv");
            WriteTsv(watchFile, watched);
            record.Output(watchFile);
        }
        var queued = new HashSet<string>(queue.Select(x => x!["accession"]!.GetValue<string>()), StringComparer.Ordinal);

        var inScope = rows.Where(x => x.Relevance.IsIn).ToList();
        var summary = new CensusSummary(
            q.Name, record.StartedUtc, rows.Count,
            Count(rows, x => Snake(x.Relevance.Verdict.ToString())),
            Count(rows, x => Snake(x.Route.Kind.ToString())),
            Count(rows.Where(x => x.Route.Kind == RouteKind.Search), x => x.Route.Profile!),
            Count(rows.Where(x => x.Route.Kind == RouteKind.WaitingOnCapability), x => x.Route.Profile ?? x.Route.Reason),
            Count(inScope, x => Snake(x.Acquisition.Instrument.ToString())),
            Count(inScope, x => Snake(x.Acquisition.Labelling.ToString())),
            failed, Path.GetFullPath(outDir), queue.Count, noDatabase,
            OrganismSources: Count(rows, x => x.OrganismSource),
            // Per keyword, the deposits the queue would hold: after every screen and the reference-group rule (PXR-A25).
            QueuedByKeyword: q.Keywords.Concat(q.More.Disease).ToDictionary(k => k,
                k => rows.Count(x => queued.Contains(x.Accession) && x.KeywordsHit.Contains(k))),
            Watched: Count(watched, x => x.Watch!));
        return (summary, rows);
    }

    /// <summary>
    /// The watch reason, and the reference-group evidence for a disease-only deposit. A deposit found by any of the
    /// question's <c>keywords</c> keeps today's screen: its comparison may be the question's own (age, for aging).
    /// </summary>
    internal static (string? Watch, string? ReferenceGroup) Watch(Question q, IReadOnlySet<string> keywordsHit, Route route, PrideProjectSearchResult r)
    {
        if (q.Keywords.Any(keywordsHit.Contains)) return (null, null);
        if (q.More.Disease.Any(keywordsHit.Contains))
        {
            string? evidence = ReferenceGroup.Find(r);
            return (route.Kind == RouteKind.Search && evidence is null ? WatchReason.NoReferenceGroup : null, evidence);
        }
        return (q.More.Watch.Any(keywordsHit.Contains) ? WatchReason.WatchList : null, null);
    }

    /// <summary>
    /// The organisms PRIDE's project record gives. When the record cannot be had (PRIDE unavailable after retries, or
    /// no such project), the deposit is NOT dropped: that would silently shrink the census. It falls back to the search
    /// index's organisms with SDRF column headers removed, is marked <see cref="OrganismSource.SearchFallback"/> in
    /// census.tsv, and the reason is noted in the run record. A broken answer (<c>MzLibException</c>) fails the census,
    /// as it does for a keyword: it is a contract break, not an outage.
    /// </summary>
    private async Task<(IReadOnlyList<string> Organisms, string Source)> OrganismsOfRecordAsync(
        string accession, PrideProjectSearchResult hit, RunRecord record, CancellationToken ct)
    {
        string why;
        try
        {
            var project = await search.TryGetProjectAsync(accession, ct).ConfigureAwait(false);
            var names = project?.Organisms.Select(o => o.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names is { Count: > 0 }) return (names, OrganismSource.Project);
            why = project is null ? "PRIDE has no project record" : "the project record lists no organism";
        }
        catch (Exception e) when (Cli.Envelope.IsUnavailable(e) && !ct.IsCancellationRequested)
        {
            why = $"project record unavailable: {e.Message}";
        }
        record.Note($"{accession} organisms from the search index ({why})");
        return (hit.Organisms.Where(o => !IsSdrfHeader(o)).ToList(), OrganismSource.SearchFallback);
    }

    /// <summary>An SDRF column header, e.g. "Characteristics[organism]" or "Comment[technical replicate]".</summary>
    internal static bool IsSdrfHeader(string term) =>
        System.Text.RegularExpressions.Regex.IsMatch(term, @"^\s*(characteristics|comment|factor ?value)\s*\[.*\]\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        foreach (var x in rows.Where(x => x.Queueable))
        {
            var keys = profiles[x.Route.Profile!].Databases.Keys;
            string? organism = x.Organisms.Select(o => keys.FirstOrDefault(k => OrganismMatches(o, k))).FirstOrDefault(k => k is not null);
            if (organism is null) { missing.Add(x.Organisms.FirstOrDefault() ?? "(none listed)"); continue; }
            queue.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["accession"] = x.Accession, ["title"] = x.Record.Title, ["organism"] = organism,
                ["profile"] = x.Route.Profile, ["files"] = x.Acquisition.MsFileCount,
                ["instruments"] = string.Join("; ", x.Record.Instruments), ["organism_source"] = x.OrganismSource,
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
            "organism_parts", "diseases", "instruments", "submission_type", "publication_date", "keywords_hit", "title",
            "organisms_of_record", "organism_source", "watch", "reference_group"));
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
                string.Join(";", x.KeywordsHit), x.Record.Title, string.Join(";", x.Organisms), x.OrganismSource,
                x.Watch ?? "", x.ReferenceGroupEvidence ?? "",
            }.Select(Clean)));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
