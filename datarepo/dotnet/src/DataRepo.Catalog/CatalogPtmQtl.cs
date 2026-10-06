using DataRepo.Bundle;
using DataRepo.Ingest.Sources;
using DuckDB.NET.Data;

namespace DataRepo.Catalog;

/// <summary>The ptmQtl engines' artefacts in a catalog (our ptmQtl 026; <c>design/PTMQTL_ENGINE.md</c>).</summary>
/// <remarks>
/// <para><b>Which artefacts.</b></para>
/// <list type="bullet">
/// <item>One <c>ptmqtl.site_pairs</c> artefact per bundle.</item>
/// <item>One <c>ptmqtl.site_traits</c> artefact per bundle and trait.</item>
/// <item>One <c>ptmqtl.pool_pairs</c> artefact per species, and only one whose every input <c>site_pairs</c> artefact
/// is in the catalog: a pooled row must rest on the dataset rows the catalog serves.</item>
/// </list>
/// <para>Two candidates for one slot are refused, never one picked, as for go and logs. All of a catalog's ptmQtl
/// artefacts must share one enrichment list (S3), as go's must share one go.obo and map (GO-D9). A pooled result built
/// under one list beside dataset rows under another would describe two different gates.</para>
/// <para><b>Rows.</b> Dataset rows carry their bundle's <c>dataset_id</c> and <c>bundle_id</c>, which is true of them.
/// Pooled rows belong to no one bundle, so both are NULL there, and <c>scope</c> says <c>meta:&lt;species&gt;</c>. Every
/// ptmQtl row also carries <c>artefact_id</c>, a catalog column that names the artefact it came from.</para>
/// </remarks>
public static partial class CatalogBuilder
{
    /// <summary>The engine of each ptmQtl artefact kind, and the core table it fills.</summary>
    public static readonly IReadOnlyDictionary<string, string> PtmQtlTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Runner.PtmQtlSitePairs] = "ptm_pairs",
        [Runner.PtmQtlPoolPairs] = "ptm_pairs",
        [Runner.PtmQtlSiteTraits] = "trait_effects",
    };

    private static IReadOnlyDictionary<string, object?>? Summary(ArtefactRef a) => a.Record.GetValueOrDefault("engine_summary") as IReadOnlyDictionary<string, object?>;

    private static string EngineFolder(string engine) => SourcesPy.PathStr(System.IO.Path.Combine("_engine", engine));

    /// <summary>The ptmQtl artefacts for these bundles, and a coverage check.</summary>
    /// <exception cref="CatalogException">Two artefacts for one slot, or two enrichment lists.</exception>
    private static void SelectPtmQtl(string store, IReadOnlyList<BundleRef> bundles, List<ArtefactRef> chosen, List<CatalogCheck> checks)
    {
        var byBundle = bundles.ToDictionary(b => b.BundleId, StringComparer.Ordinal);
        List<ArtefactRef> Current(string engine) => Runner.DiscoverArtefacts(store, engine).Where(a => a.SchemaVersion == SchemaContract.Version).ToList();

        var pairs = new List<ArtefactRef>();
        foreach (var group in Current(Runner.PtmQtlSitePairs).Where(a => byBundle.ContainsKey(a.Inputs.GetValueOrDefault("bundle") ?? ""))
                     .GroupBy(a => a.Inputs["bundle"]).OrderBy(g => byBundle[g.Key].DatasetId, SourcesPy.CodePointOrder))
        {
            if (group.Count() > 1)
                throw new CatalogException(
                    $"{Runner.PtmQtlSitePairs}: {group.Count()} artefacts ({string.Join(", ", group.Select(a => a.ArtefactId))}) for {byBundle[group.Key].DatasetId} "
                    + $"(another enrichment list). A catalog serves one; move the others out of {EngineFolder(Runner.PtmQtlSitePairs)}.");
            pairs.Add(group.Single());
        }

        var traits = new List<ArtefactRef>();
        foreach (var group in Current(Runner.PtmQtlSiteTraits).Where(a => byBundle.ContainsKey(a.Inputs.GetValueOrDefault("bundle") ?? ""))
                     .GroupBy(a => (a.Inputs["bundle"], Summary(a)?.GetValueOrDefault("trait_id") as string)))
        {
            if (group.Count() > 1)
                throw new CatalogException(
                    $"{Runner.PtmQtlSiteTraits}: {group.Count()} artefacts for {byBundle[group.Key.Item1].DatasetId}, trait {group.Key.Item2}. "
                    + $"A catalog serves one; move the others out of {EngineFolder(Runner.PtmQtlSiteTraits)}.");
            traits.Add(group.Single());
        }

        var pairIds = pairs.Select(a => a.ArtefactId).ToHashSet(StringComparer.Ordinal);
        var pools = new List<ArtefactRef>();
        foreach (var group in Current(Runner.PtmQtlPoolPairs)
                     .Where(a => a.Inputs.Where(kv => kv.Key.StartsWith("site_pairs:", StringComparison.Ordinal)).All(kv => pairIds.Contains(kv.Value)))
                     .GroupBy(a => Summary(a)?.GetValueOrDefault("species") as string ?? ""))
        {
            if (group.Count() > 1)
                throw new CatalogException(
                    $"{Runner.PtmQtlPoolPairs}: {group.Count()} pooled artefacts for {group.Key} rest on this catalog's datasets "
                    + $"({string.Join(", ", group.Select(a => a.ArtefactId))}). A catalog serves one; move the others out of {EngineFolder(Runner.PtmQtlPoolPairs)}.");
            pools.Add(group.Single());
        }

        var all = pairs.Concat(traits).Concat(pools).ToList();
        var lists = all.Select(a => a.Inputs.GetValueOrDefault("enrichment") ?? "(none)").Distinct(StringComparer.Ordinal).ToList();
        if (lists.Count > 1)
            throw new CatalogException(
                $"ptmQtl: these artefacts were run under {lists.Count} enrichment lists ({string.Join(", ", lists.Select(l => l[..Math.Min(12, l.Length)]))}). "
                + "A catalog serves results under one list (ptmQtl S3), so a pooled row and its dataset rows share one gate. "
                + "Re-run the others with the same --input enrichment, or move them out of the store's _engine folder.");
        chosen.AddRange(all);

        var uncovered = bundles.Where(b => pairs.All(a => a.Inputs["bundle"] != b.BundleId)).Select(b => b.DatasetId).Order(SourcesPy.CodePointOrder).ToList();
        checks.Add(new CatalogCheck($"{Runner.PtmQtlSitePairs} coverage (bundles with an artefact)", "engine-coverage", true,
            bundles.Count - uncovered.Count, bundles.Count, uncovered.Count > 0 ? "no artefact for " + string.Join(", ", uncovered) : null));
    }

    /// <summary>The ptmQtl rows into <c>ptm_pairs</c> and <c>trait_effects</c>, with <c>artefact_id</c>.</summary>
    private static Dictionary<string, long> LoadPtmQtlRows(DuckDBConnection con, IReadOnlyList<BundleRef> bundles, IReadOnlyList<ArtefactRef> artefacts)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!GoActive) return counts;
        foreach (var table in PtmQtlTables.Values.Distinct())
            Exec(con, $"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS artefact_id VARCHAR");
        var byBundle = bundles.ToDictionary(b => b.BundleId, StringComparer.Ordinal);
        foreach (var a in artefacts.Where(a => PtmQtlTables.ContainsKey(a.Engine)))
        {
            var table = PtmQtlTables[a.Engine];
            var path = a.TablePath(table);
            if (path is null) continue;
            var (dataset, bundle) = a.Engine == Runner.PtmQtlPoolPairs
                ? ("NULL", "NULL")
                : (Quote(byBundle[a.Inputs["bundle"]].DatasetId), Quote(a.Inputs["bundle"]));
            Exec(con, $"INSERT INTO \"{table}\" BY NAME SELECT {dataset} AS dataset_id, {bundle} AS bundle_id, "
                + $"{Quote(a.ArtefactId)} AS artefact_id, * FROM read_parquet({Quote(AsPosix(path))})");
            counts[table] = Count(con, $"SELECT count(*) FROM \"{table}\"");
        }
        return counts;
    }

    /// <summary>Every ptmQtl artefact's rows are all there.</summary>
    private static List<CatalogCheck> CheckPtmQtl(DuckDBConnection con, IReadOnlyList<ArtefactRef> artefacts)
    {
        var checks = new List<CatalogCheck>();
        if (!GoActive) return checks;
        foreach (var a in artefacts.Where(a => PtmQtlTables.ContainsKey(a.Engine)).OrderBy(a => a.Engine, SourcesPy.CodePointOrder).ThenBy(a => a.ArtefactId, SourcesPy.CodePointOrder))
            foreach (var (table, expected) in a.RowCounts.OrderBy(kv => kv.Key, SourcesPy.CodePointOrder))
            {
                var (observed, _) = CountAndExample(con, $"SELECT count(*), NULL FROM \"{table}\" WHERE artefact_id = ?", a.ArtefactId);
                checks.Add(new CatalogCheck($"{a.Engine}/{a.ArtefactId}/{table}", "row_count", observed == expected, observed, expected,
                    $"engine artefact {a.ArtefactId}"));
            }
        return checks;
    }
}
