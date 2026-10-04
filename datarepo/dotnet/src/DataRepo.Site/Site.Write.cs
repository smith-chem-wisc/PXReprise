using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using Dict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace DataRepo.Site;

public static partial class SiteGenerator
{
    // --- llms.txt ------------------------------------------------------------------------------

    private static string LlmsTxt(SiteFacts facts, string title, string? dataUrl)
    {
        var meta = facts.Meta;
        var lines = new List<string>
        {
            $"# {title}",
            "",
            "> Search and quantification results for " +
            $"{Plural((long)facts.Datasets.Count, "public proteomics dataset")}, reanalysed with one " +
            $"pipeline into one schema{ForQuestions(facts)}. " +
            $"Every number here comes from catalog `{PyText.Str(meta["catalog_id"])}` (datarepo " +
            $"{PyText.Str(meta["builder_version"])}, schema {PyText.Str(meta["schema_version"])}, built {PyText.Str(meta["built_utc"])}); " +
            "cite that id with any number you reuse.",
            "",
            "Before you answer from this data:",
            "",
            "- Counts at 1% FDR come from the `*_1pct` views (`psms_1pct`, `peptidoforms_1pct`, " +
            "`protein_groups_1pct`). The base tables also hold decoys and sub-threshold matches.",
            "- Each dataset page lists its open findings. A `low_id_rate` or `sdrf_skeleton` finding " +
            "means the absence of a protein, or of sample metadata, is weak evidence.",
            "- An empty table means nothing was delivered. It does not mean the answer is zero or " +
            "none. The MCP `describe` tool says which tables are empty.",
            "- Contaminant proteins (e.g. bovine albumin, porcine trypsin) are flagged and carry " +
            "their own species. They are reagents, not evidence about the sample's organism. The " +
            "flag is PER DATASET: human albumin is a target in a human search and a contaminant in " +
            "a rodent one.",
            "- A dataset's organism is the organism of the protein database searched, not a " +
            "statement about the samples.",
            "- `pep` values are not comparable across datasets: the model is retrained on every " +
            "search. Compare counts at a threshold instead.",
            "- A protein group's accession list is kept in the search engine's order, and " +
            "MetaMorpheus writes it alphabetically. The first accession is not a leading or razor " +
            "protein, and nothing in this repository names one.",
            "",
            "## Datasets",
            "",
        };
        foreach (var ds in facts.Datasets)
            lines.Add($"- [{Id(ds)}: {Title(ds)}]({PagePath(Id(ds))}): {Summary(ds)}");
        lines.AddRange(
        [
            "",
            "## Access",
            "",
            "- [datasets.json](datasets.json): a small index, one entry per dataset, each naming " +
            "its full JSON (`datasets/<id>.json`: every fact on its page, all findings and " +
            "modifications).",
            "- [proteins/index.json](proteins/index.json): which datasets have accepted evidence for " +
            "a protein. It lists every protein shard by accession prefix: fetch the shard whose " +
            "prefix is the longest one the accession starts with. A gene symbol is in " +
            "`genes/<first letter>.json`, which gives its accessions.",
            $"- [MCP server]({Repository}/blob/master/docs/mcp.md): `datarepo mcp --catalog " +
            "<catalog.duckdb>` serves a catalog to an agent over stdio (describe, search, " +
            "read-only SQL).",
            $"- [Schema]({Repository}/tree/master/docs/schema): every table and column, with its " +
            "meaning.",
        ]);
        lines.Add(!string.IsNullOrEmpty(dataUrl)
            ? "- [croissant.json](croissant.json): the Parquet files, described for ML loaders."
            : "- Bulk Parquet download: not published yet.");
        lines.AddRange(
        [
            "",
            "## Optional",
            "",
            $"- [Source code]({Repository}): the ingester and catalog builder that made this data.",
            $"- [Limitations]({Repository}/blob/master/docs/limitations.md): what the repository " +
            "does not yet hold.",
            "",
        ]);
        return string.Join("\n", lines);
    }

    // --- JSON for programs ---------------------------------------------------------------------

    /// <summary>Said wherever a dataset's organism is shown, because it reads as a fact about the sample and
    /// it is not one: PXD050351 is a human cell line, and an agent judged it "maybe a mouse sample" from the
    /// title while <c>organisms</c> said only which proteome was searched (aging 070 57g).</summary>
    public const string OrganismsAre =
        "`organisms` is the organism of the protein DATABASE the search used, not a statement about " +
        "the samples. Sample annotation, where any exists, is in the catalog's `samples` and " +
        "`sample_characteristics` tables.";

    public const string DatasetsAre =
        "One entry per dataset. Each names its page and its full JSON (`json`), which holds every " +
        "fact on the page including all findings and modifications. `findings_by_severity` and " +
        "`finding_codes` say what is recorded against a dataset; whether that makes it usable for a " +
        "question is the question's call, not this file's. " + OrganismsAre;

    private static string Json(object? doc) => PyText.Json(doc) + "\n";

    private static string JsonCompact(object? doc) => PyText.Json(doc, indent: null) + "\n";

    private static string JsonPath(string datasetId) => $"datasets/{PyText.Quote(datasetId)}.json";

    /// <summary>Relative paths always; absolute URLs as well when the site's address is known (55b).</summary>
    private static Dict Links(string datasetId, string? baseUrl)
    {
        var page = PagePath(datasetId);
        var doc = JsonPath(datasetId);
        return new Dict { ["page"] = page, ["json"] = doc, ["page_url"] = Absolute(baseUrl, page), ["json_url"] = Absolute(baseUrl, doc) };
    }

    private static readonly string[] IndexKeys =
    [
        "dataset_id", "title", "organisms", "organism_names", "acquisition", "quant_method",
        "labelling", "enrichment", "enrichment_mixed", "n_runs", "n_samples", "n_psms_1pct",
        "n_peptidoforms_1pct", "n_protein_groups_1pct", "n_ptm_sites", "sdrf_status",
        "findings_by_severity", "finding_codes",
    ];

    private static Dict IndexEntry(Dict ds, string? baseUrl)
    {
        var entry = new Dict();
        foreach (var k in IndexKeys) entry[k] = ds.GetValueOrDefault(k);
        entry["summary"] = Summary(ds);
        foreach (var (k, v) in Links(Id(ds), baseUrl)) entry[k] = v;
        return entry;
    }

    /// <summary><c>{prefix: entries}</c> with every shard under <see cref="MaxShardBytes"/>, prefixes as short
    /// as allows.</summary>
    private static OrderedDictionary<string, Dict> Shards(Dict entries, int length = 2)
    {
        var groups = new OrderedDictionary<string, Dict>();
        foreach (var (accession, entry) in entries)
        {
            var prefix = accession.Length > length ? accession[..length] : accession;
            if (!groups.TryGetValue(prefix, out var group)) groups[prefix] = group = new Dict();
            group[accession] = entry;
        }
        var out_ = new OrderedDictionary<string, Dict>();
        foreach (var (prefix, group) in groups)
        {
            var tooBig = Encoding.UTF8.GetByteCount(JsonCompact(group)) > MaxShardBytes;
            if (tooBig && group.Keys.Any(a => a.Length > length))
                foreach (var (k, v) in Shards(group, length + 1)) out_[k] = v;
            else
                out_[prefix] = group;
        }
        return out_;
    }

    /// <summary>Python's <c>str.isalnum()</c> for one character.</summary>
    private static bool IsAlnum(char c) => char.IsLetterOrDigit(c) || char.IsNumber(c);

    /// <summary><c>proteins/&lt;shard&gt;.json</c>, <c>genes/&lt;letter&gt;.json</c> and <c>proteins/index.json</c>
    /// (DATAREPO-56).</summary>
    private static OrderedDictionary<string, string> ProteinFiles(Dict proteins, Dict catalog)
    {
        var shards = Shards(proteins);
        var genes = new OrderedDictionary<string, OrderedDictionary<string, List<object?>>>();
        foreach (var (accession, value) in proteins)
        {
            var gene = ((Dict)value!).GetValueOrDefault("gene") as string;
            if (string.IsNullOrEmpty(gene)) continue;
            var letter = IsAlnum(gene[0]) ? gene[0].ToString().ToUpperInvariant() : "_";
            if (!genes.TryGetValue(letter, out var bySymbol)) genes[letter] = bySymbol = [];
            var symbol = gene.ToUpperInvariant();
            if (!bySymbol.TryGetValue(symbol, out var accessions)) bySymbol[symbol] = accessions = [];
            accessions.Add(accession);
        }
        const string about =
            "Accessions with evidence that passed acceptance (an accepted protein group OR an " +
            "accepted peptidoform) in at least one dataset; decoys excluded, contaminants kept and " +
            "labelled PER DATASET, because the label is: human albumin is a target in a human search " +
            "and a contaminant in a rodent one. `best_protein_group_q_value` is NULL where only a " +
            "peptidoform passed, so a listed dataset is not 'identified at 1% protein FDR'. An " +
            "accession absent from its shard had no accepted evidence anywhere in this catalog.";
        var paths = shards.Keys.ToDictionary(k => k, k => $"proteins/{PyText.Quote(k, safe: "")}.json");
        var files = new OrderedDictionary<string, string>();
        foreach (var (key, entries) in shards.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            files[paths[key]] = JsonCompact(new Dict { ["catalog"] = catalog, ["proteins_are"] = about, ["proteins"] = entries });
        foreach (var (key, entries) in genes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var sorted = new Dict();
            foreach (var (symbol, accessions) in entries.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[symbol] = accessions;
            files[$"genes/{key}.json"] = JsonCompact(new Dict
            {
                ["catalog"] = catalog,
                ["genes_are"] = "Gene symbol (upper-cased) -> accessions, from the producer's display " +
                                "symbol. Look each accession up in its proteins/ shard.",
                ["genes"] = sorted,
            });
        }
        var proteinShards = new Dict();
        foreach (var (k, v) in shards.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            proteinShards[k] = new Dict { ["file"] = paths[k], ["n"] = (long)v.Count };
        var geneShards = new Dict();
        foreach (var (k, v) in genes.OrderBy(kv => kv.Key, StringComparer.Ordinal)) geneShards[k] = (long)v.Count;
        files["proteins/index.json"] = Json(new Dict
        {
            ["catalog"] = catalog,
            ["how_to_look_up"] =
                "Find the LONGEST key of `protein_shards` that the accession starts with, and fetch " +
                "that shard's `file` (P02768 -> the key 'P02' or 'P0', whichever is listed). By gene " +
                "symbol, fetch genes/<first letter, upper-cased>.json first. " + about,
            ["protein_shards"] = proteinShards,
            ["gene_shards"] = geneShards,
        });
        return files;
    }

    // --- writing -------------------------------------------------------------------------------

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Delete what the last generation wrote, and refuse a directory that is not a site of ours.</summary>
    private static void ClearPrevious(string out_)
    {
        if (File.Exists(out_)) throw new CatalogException($"{out_} exists and is not a directory");
        if (!Directory.Exists(out_)) return;
        var marker = Path.Combine(out_, SiteMarker);
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(out_).Any())
            {
                throw new CatalogException(
                    $"{out_} is not empty and was not written by `datarepo site`; choose an empty " +
                    "directory. The generator deletes what it wrote last time, so it will not adopt " +
                    "a directory it cannot account for.");
            }
            return;
        }
        object? doc;
        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllBytes(marker));
            doc = FromJson(parsed.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CatalogException($"{marker} is unreadable: {ex.Message}");
        }
        var previous = doc is Dict d ? d.GetValueOrDefault("files") as List<object?> ?? [] : [];
        var root = Path.GetFullPath(out_).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in previous.OfType<string>())
        {
            var path = Path.GetFullPath(Path.Combine(out_, name));
            if (path.StartsWith(root, PathComparison) && path.Length > root.Length && File.Exists(path))
                File.Delete(path);
        }
        foreach (var folder in new[] { "datasets", "proteins", "genes" })
        {
            var path = Path.Combine(out_, folder);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
    }

    /// <summary>Write the static site for one catalog into <paramref name="out_"/>.</summary>
    /// <param name="catalog">A catalog written by <c>datarepo build</c>.</param>
    /// <param name="out_">An empty directory, or one a previous <see cref="BuildSite"/> wrote.</param>
    /// <param name="options">The site's address, the data's address and the instance owner's words.</param>
    /// <exception cref="CatalogException">No catalog at <paramref name="catalog"/>, or <paramref name="out_"/>
    /// holds files this generator did not write.</exception>
    public static SiteResult BuildSite(string catalog, string out_, SiteOptions? options = null)
    {
        options ??= new SiteOptions();
        var facts = ReadSiteFacts(catalog);
        var purpose = (options.Purpose ?? "").Trim().TrimEnd('.');
        facts.Purpose = purpose.Length > 0 ? purpose : null;
        var keywords = (options.Keywords ?? []).Where(k => !string.IsNullOrEmpty(k) && k.Trim().Length > 0)
            .Select(k => k.Trim()).ToList();
        var meta = facts.Meta;
        var title = !string.IsNullOrEmpty(options.Title) ? options.Title
            : PyText.Truthy(meta.GetValueOrDefault("instance")) ? $"{PyText.Str(meta["instance"])} proteomics repository"
            : "Reanalysed proteomics repository";
        var baseUrl = options.BaseUrl;
        var dataUrl = options.DataUrl;
        ClearPrevious(out_);
        Directory.CreateDirectory(Path.Combine(out_, "datasets"));

        var catalogBlock = new Dict();
        foreach (var k in new[] { "catalog_id", "catalog_version", "schema_version", "builder_version", "built_utc", "instance" })
            catalogBlock[k] = meta[k];
        catalogBlock["base_url"] = baseUrl;
        var files = new OrderedDictionary<string, string>
        {
            ["style.css"] = Style,
            ["index.html"] = IndexHtml(facts, title, baseUrl, options.About),
            ["llms.txt"] = LlmsTxt(facts, title, dataUrl),
            // An INDEX, one small entry per dataset. The whole thing in one file was cut off by agent fetch
            // tools after about 19 of 26 entries, and the agent could not tell (aging 069 55a).
            ["datasets.json"] = Json(new Dict
            {
                ["catalog"] = catalogBlock,
                ["datasets_are"] = DatasetsAre,
                ["datasets"] = facts.Datasets.Select(ds => (object?)IndexEntry(ds, baseUrl)).ToList(),
            }),
        };
        foreach (var ds in facts.Datasets)
        {
            files[PagePath(Id(ds))] = DatasetHtml(ds, meta, title, baseUrl, dataUrl, keywords);
            var dataset = new Dict();
            foreach (var (k, v) in ds)
                if (k != "bundle") dataset[k] = v;
            var bundle = new Dict();
            foreach (var (k, v) in Bundle(ds))
                if (k != "path") bundle[k] = v;
            dataset["bundle"] = bundle;
            dataset["summary"] = Summary(ds);
            foreach (var (k, v) in Links(Id(ds), baseUrl)) dataset[k] = v;
            files[JsonPath(Id(ds))] = Json(new Dict
            {
                ["catalog"] = catalogBlock,
                ["dataset"] = dataset,
                ["organisms_are"] = OrganismsAre,
            });
        }
        foreach (var (k, v) in ProteinFiles(facts.Proteins, catalogBlock)) files[k] = v;

        var result = new SiteResult { Out = out_, CatalogId = PyText.Str(meta["catalog_id"]) };
        if (!string.IsNullOrEmpty(dataUrl))
        {
            var (doc, unreadable) = Croissant(facts, title, baseUrl, dataUrl);
            files["croissant.json"] = PyText.Json(doc) + "\n";
            if (unreadable.Count > 0)
            {
                result.Warnings.Add(
                    $"croissant.json leaves out {unreadable.Count} file(s) it could not read from the " +
                    $"store, e.g. {unreadable[0]}; a file is described from its own bytes or not at all");
            }
        }
        else
        {
            result.Skipped["croissant.json"] =
                "no --data-url: a Croissant file describes downloadable files, and none are published";
        }
        if (!string.IsNullOrEmpty(baseUrl))
        {
            var urls = new List<string> { "index.html", "llms.txt" };
            urls.AddRange(facts.Datasets.Select(ds => PagePath(Id(ds))));
            files["sitemap.xml"] =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
                string.Concat(urls.Select(u => $"<url><loc>{E(Absolute(baseUrl, u))}</loc></url>\n")) +
                "</urlset>\n";
            files["robots.txt"] = $"User-agent: *\nAllow: /\nSitemap: {Absolute(baseUrl, "sitemap.xml")}\n";
        }
        else
        {
            foreach (var name in new[] { "sitemap.xml", "robots.txt" })
                result.Skipped[name] = "no --base-url: both need the site's absolute address";
        }

        var missing = facts.Datasets.Where(ds => !PyText.Truthy(ds.GetValueOrDefault("licence")))
            .Select(Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            result.Warnings.Add(
                $"no licence readable from the bundle for {string.Join(", ", missing)}; those pages state " +
                "none rather than assume one (is the store at the path the catalog recorded?)");
        }

        if (!string.IsNullOrEmpty(options.Notice))
        {
            var banner = $"<div class=\"notice\" role=\"note\">{E(options.Notice)}</div>";
            foreach (var name in files.Keys.ToList())
            {
                if (!name.EndsWith(".html", StringComparison.Ordinal)) continue;
                var text = files[name];
                var at = text.IndexOf("<body>\n", StringComparison.Ordinal);
                if (at >= 0) files[name] = text[..at] + $"<body>\n{banner}\n" + text[(at + "<body>\n".Length)..];
            }
            var llms = files["llms.txt"];
            const string sep = "\n\nBefore you answer";
            var cut = llms.IndexOf(sep, StringComparison.Ordinal);
            var (head, rest) = cut >= 0 ? (llms[..cut], llms[cut..]) : (llms, "");
            files["llms.txt"] = $"{head}\n\n**Notice:** {options.Notice}{rest}";
        }

        var utf8 = new UTF8Encoding(false);
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(out_, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // "\n" only: the output is published from any OS and must be byte-identical across them.
            File.WriteAllText(path, text, utf8);
        }
        result.Files = files.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        File.WriteAllText(Path.Combine(out_, SiteMarker), PyText.Json(new Dict
        {
            ["catalog_id"] = meta["catalog_id"],
            ["generator"] = $"datarepo {Version}",
            ["files"] = result.Files,
        }, ensureAscii: true) + "\n", utf8);
        return result;
    }
}
