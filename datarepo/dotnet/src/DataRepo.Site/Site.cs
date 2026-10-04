using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Types;
using DataRepo.Bundle;
using Dict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace DataRepo.Site;

/// <summary>Where the site is served from, and the instance owner's words for it: the options of
/// <c>datarepo site</c>.</summary>
/// <param name="Title">The site's name. Defaults to one built from the catalog's instance.</param>
/// <param name="BaseUrl">Where the site will be served. Needed for absolute URLs in the structured data,
/// <c>sitemap.xml</c> and <c>robots.txt</c>; without it links are relative and those are skipped.</param>
/// <param name="DataUrl">Where the bundle store is served, as <c>&lt;data_url&gt;/&lt;dataset&gt;/&lt;bundle&gt;/&lt;table&gt;.parquet</c>.
/// Without it no download links are written and <c>croissant.json</c> is skipped.</param>
/// <param name="Notice">A banner shown at the top of every page and of <c>llms.txt</c>, e.g. that the site is a
/// preview whose data will be regenerated. It goes where no reader, human or agent, can miss it, because a
/// caveat on a page nobody opens is not a caveat.</param>
/// <param name="About">Markdown for the front page's overview: what the project behind this instance is and
/// why it exists. It is the instance owner's text, not dataRepo's, so it comes in from outside rather than
/// being written here.</param>
/// <param name="Purpose">The question the instance serves, as the end of "for questions about ...", e.g. "how
/// organelle proteomes change with age". The operator's words; without it no page claims a purpose. Shown in
/// the tagline, the overview, <c>llms.txt</c> and the structured data.</param>
/// <param name="Keywords">Extra schema.org keywords for every dataset page, e.g. <c>aging</c>.</param>
public sealed record SiteOptions(
    string? Title = null,
    string? BaseUrl = null,
    string? DataUrl = null,
    string? Notice = null,
    string? About = null,
    string? Purpose = null,
    IReadOnlyList<string>? Keywords = null);

/// <summary>What <see cref="SiteGenerator.BuildSite"/> wrote, skipped and warned about.</summary>
public sealed class SiteResult
{
    public required string Out { get; init; }
    public required string CatalogId { get; init; }
    public List<string> Files { get; set; } = [];
    public Dictionary<string, string> Skipped { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>Generate the public static site from a built catalog (FRAMEWORK step 4a, D16, D17). Ported from
/// <c>site.py</c>.</summary>
/// <remarks>
/// <para>The site is a derived artefact like the catalog itself: it is written from one catalog and nothing
/// else, regenerating it moves no bundle id and no catalog id, and deleting it loses nothing. dataRepo ships the
/// generator; aging publishes the output (D8, D16). It needs no server: every page is plain HTML that works
/// with no API behind it, which FRAMEWORK section 5 requires.</para>
/// <para>What it writes: <c>index.html</c> (every dataset, with the catalog's id and a schema.org
/// <c>DataCatalog</c> block); <c>datasets/&lt;id&gt;.html</c> (one page per dataset, with a Bioschemas
/// <c>Dataset</c> block, the dataset's open findings, and a summary sentence); <c>llms.txt</c> (the entry point
/// for an agent, llmstxt.org, which is who this repository is for); <c>datasets.json</c> (the same facts as the
/// pages, for a program rather than a reader); <c>croissant.json</c> only with a data URL, because a Croissant
/// file is a description of files someone can download, and without one it would describe nothing;
/// <c>sitemap.xml</c> and <c>robots.txt</c> only with a base URL, because both need absolute URLs.</para>
/// <para><b>The summary is grounded by construction (D17).</b> It is a template filled from catalog fields, not
/// prose from a model, so it may be clumsy but it cannot say anything about biology the catalog does not hold.
/// It is still labelled "generated", because D17 makes the label part of the contract and a model-written
/// summary can later replace the template without changing what the label promises.</para>
/// <para><b>Nothing here is invented to fill a gap.</b> An absent licence is left out of the metadata and
/// reported, not defaulted to D3's value; an organism with no name in the catalog is shown as its taxon id; a
/// dataset with no title is shown under its accession. A field the catalog cannot fill is the same failure as a
/// <c>required: true</c> column with no true value.</para>
/// </remarks>
public static partial class SiteGenerator
{
    /// <summary>Written into the output directory; the only thing that lets a regeneration delete what the
    /// last one wrote. A directory without it is not ours, and the generator refuses to write into it.</summary>
    public const string SiteMarker = ".datarepo-site.json";

    public const string BioschemasDataset = "https://bioschemas.org/profiles/Dataset/1.0-RELEASE";
    public const string CroissantConformsTo = "http://mlcommons.org/croissant/1.0";
    public const string Parquet = "application/x-parquet";
    public const string Repository = "https://github.com/smith-chem-wisc/dataRepo";

    /// <summary><c>definitions.MS2_COUNT.definition_id</c>, the definition the "Spectra searched" tile names.</summary>
    public const string Ms2CountDefinitionId = "aging:DEF-MS2";

    public static readonly IReadOnlyDictionary<string, string> LicenceUrls =
        new Dictionary<string, string> { ["CC-BY-4.0"] = "https://creativecommons.org/licenses/by/4.0/" };

    /// <summary>The bundle tables a dataset page links to, in the order a reader wants them. Everything else
    /// a bundle holds is reached through the Croissant file and the schema documentation.</summary>
    public static readonly IReadOnlyList<string> DownloadTables =
    [
        "psms", "peptidoforms", "protein_groups", "proteins", "ptm_sites", "quant_values",
        "runs", "samples", "assays", "findings", "metrics", "definitions",
    ];

    /// <summary>The "in N or more datasets" figure aims here: N is chosen per build so the count stays near
    /// it, because a fixed N counts more proteins with every dataset added (3 gave 14,442 at 62 datasets).</summary>
    public const long SharedProteinsTarget = 10_000;

    /// <summary>No protein shard is written larger than this. A fixed two-character prefix put 3.6 MB in
    /// <c>Q9.json</c> on aging's 34 datasets, which is the truncation 55a was about in a new file; so a shard
    /// over the cap is split by lengthening its prefix, and the index names every shard.</summary>
    public const int MaxShardBytes = 150_000;

    /// <summary>This generator's name and version, as the marker's <c>generator</c> field and every page's
    /// footer state it. Python wrote <c>datarepo &lt;package version&gt;</c>; this is the C# assembly's.</summary>
    public static string Version { get; } = typeof(SiteGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // --- words ---------------------------------------------------------------------------------

    private static string N(object? value) => value is null ? "unknown" : PyText.Thousands(value);

    private static string Plural(object n, string word) =>
        n is long l && l == 1 || n is int i && i == 1 ? $"{PyText.Thousands(n)} {word}" : $"{PyText.Thousands(n)} {word}s";

    private static string JoinWords(IReadOnlyList<string> items)
    {
        if (items.Count <= 1) return string.Concat(items);
        return string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    private static List<string> Strings(object? list) =>
        (list as List<object?> ?? []).Select(x => (string)x!).ToList();

    private static string EnrichmentWords(object? values, bool mixed = false)
    {
        var list = Strings(values);
        if (mixed)
        {
            // A mixed deposit's declaration is true of some runs only (G63): saying "enrichment:
            // chemical_probe" alone would present its whole-proteome runs as captures.
            var declared = JoinWords(list) is { Length: > 0 } j ? j : "an unrecorded enrichment";
            return $"runs that differ in enrichment ({declared} on some runs; see each run's own)";
        }
        if (list.Count == 0) return "no recorded enrichment";
        if (list is ["none"]) return "no enrichment step (whole proteome)";
        if (list is ["other"]) return "an enrichment step outside the controlled list (recorded as `other`)";
        return "enrichment: " + JoinWords(list);
    }

    /// <summary>The declared enrichment, saying so when the runs differ (G63).</summary>
    private static string EnrichmentCell(Dict ds)
    {
        var text = string.Join(", ", Strings(ds.GetValueOrDefault("enrichment")));
        return PyText.Truthy(ds.GetValueOrDefault("enrichment_mixed")) ? $"{text} (on some runs only; runs differ)" : text;
    }

    private static string? S(Dict d, string key) => d.GetValueOrDefault(key) is { } v ? PyText.Str(v) : null;

    /// <summary>Python's <c>d.get(key) or fallback</c>, as text.</summary>
    private static string Or(Dict d, string key, string fallback) =>
        PyText.Truthy(d.GetValueOrDefault(key)) ? PyText.Str(d[key]) : fallback;

    private static string Id(Dict ds) => (string)ds["dataset_id"]!;

    private static string Title(Dict ds) => Or(ds, "title", Id(ds));

    /// <summary>The dataset's summary sentence, filled only from catalog fields (D17).</summary>
    /// <remarks>Every clause is a field or a count the page also shows, so a reader can check the sentence
    /// against the table below it. Nothing about what the study found biologically can appear here, because
    /// the catalog holds no such field.</remarks>
    public static string Summary(Dict ds)
    {
        var quant = Or(ds, "quant_method", "").Replace("_", "-");
        var kind = string.Join(" ", new[] { S(ds, "acquisition"), quant }.Where(x => !string.IsNullOrEmpty(x)));
        // The organism is the dataset's, as the producer's manifest states it -- not the organism a title
        // mentions. PXD050351's title says "in mice"; its PRIDE record and its samples are a human cell line,
        // which is what the proteomics measured.
        var organisms = JoinWords(Strings(ds["organism_names"])) is { Length: > 0 } o ? o : "unrecorded organism";
        var parts = new List<string>
        {
            $"{Id(ds)} is a {kind} dataset ({organisms}) " +
            $"with {Plural(ds["n_runs"]!, "run")}, {Plural(ds["n_samples"]!, "sample")} and " +
            $"{EnrichmentWords(ds.GetValueOrDefault("enrichment"), PyText.Truthy(ds.GetValueOrDefault("enrichment_mixed")))}.",
        };
        var engine = string.Join(" ", new[] { S(ds, "search_engine"), S(ds, "search_engine_version") }.Where(x => !string.IsNullOrEmpty(x)));
        if (engine.Length > 0) parts.Add($"It was reanalysed with {engine}.");
        parts.Add(
            $"At 1% FDR the search reports {N(ds["n_psms_1pct"])} PSMs, " +
            $"{N(ds["n_peptidoforms_1pct"])} peptidoforms and {N(ds["n_protein_groups_1pct"])} " +
            $"protein groups, and the repository holds {N(ds["n_ptm_sites"])} PTM sites for it.");
        var warnings = (long)Findings(ds).Count(f => f["severity"] is "warning" or "error");
        if (warnings != 0) parts.Add($"{Plural(warnings, "open warning")} about it are recorded.");
        return string.Join(" ", parts);
    }

    // --- URLs ----------------------------------------------------------------------------------

    /// <summary><c>, for questions about &lt;purpose&gt;</c> when the instance stated one, else nothing.</summary>
    /// <remarks>The question an instance serves is the operator's (<c>--purpose</c>), not dataRepo's: the
    /// generator used to say "how organelle proteomes change with age" on every site it wrote (PXR-D2).</remarks>
    private static string ForQuestions(SiteFacts facts) =>
        facts.Purpose is { Length: > 0 } p ? $", for questions about {p}" : "";

    private static string? PrideUrl(string datasetId) =>
        datasetId.StartsWith("PXD", StringComparison.Ordinal)
            ? $"https://www.ebi.ac.uk/pride/archive/projects/{PyText.Quote(datasetId)}" : null;

    private static string PagePath(string datasetId) => $"datasets/{PyText.Quote(datasetId)}.html";

    private static string? Absolute(string? baseUrl, string path) =>
        string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/{path}";

    private static Dict Bundle(Dict ds) => (Dict)ds["bundle"]!;

    private static string? DownloadUrl(string? dataUrl, Dict ds, string table)
    {
        var bundleId = Bundle(ds)["bundle_id"];
        if (string.IsNullOrEmpty(dataUrl) || !PyText.Truthy(bundleId)) return null;
        return $"{dataUrl.TrimEnd('/')}/{PyText.Quote(Id(ds))}/{PyText.Str(bundleId)}/{table}.parquet";
    }

    /// <summary><c>LICENCE_URLS.get(licence or "", licence)</c>.</summary>
    private static object? LicenceUrl(object? licence)
    {
        var key = PyText.Truthy(licence) ? PyText.Str(licence) : "";
        return LicenceUrls.TryGetValue(key, out var url) ? url : licence;
    }

    // --- structured data -----------------------------------------------------------------------

    private static Dict WithoutNulls(Dict doc)
    {
        var out_ = new Dict();
        foreach (var (k, v) in doc)
            if (v is not null) out_[k] = v;
        return out_;
    }

    /// <summary>A Bioschemas Dataset (1.0-RELEASE) block for one dataset page.</summary>
    public static Dict DatasetJsonLd(Dict ds, Dict meta, string? baseUrl, string? dataUrl, IReadOnlyList<string>? keywords = null)
    {
        var bundle = Bundle(ds);
        var keywordList = new List<object?> { "proteomics", "mass spectrometry", "reanalysis" };
        keywordList.AddRange(keywords ?? []);
        keywordList.AddRange(ds["organism_names"] as List<object?> ?? []);
        var doc = new Dict
        {
            ["@context"] = "https://schema.org/",
            ["@type"] = "Dataset",
            ["dct:conformsTo"] = new Dict { ["@id"] = BioschemasDataset, ["@type"] = "CreativeWork" },
            ["@id"] = Absolute(baseUrl, PagePath(Id(ds))),
            ["url"] = Absolute(baseUrl, PagePath(Id(ds))),
            ["name"] = $"{Id(ds)} reanalysis: {Title(ds)}",
            ["identifier"] = new List<object?> { Id(ds), $"datarepo:bundle:{PyText.Str(bundle["bundle_id"])}" },
            ["description"] = Summary(ds),
            ["version"] = bundle["bundle_id"],
            ["dateModified"] = bundle["written_utc"],
            ["keywords"] = keywordList,
            ["measurementTechnique"] = "mass spectrometry",
            ["variableMeasured"] = new List<object?>
            {
                "peptide-spectrum match", "peptidoform", "protein group", "PTM site", "protein abundance",
            },
            ["isBasedOn"] = PrideUrl(Id(ds)),
            ["license"] = LicenceUrl(ds.GetValueOrDefault("licence")),
            ["creator"] = PyText.Truthy(ds.GetValueOrDefault("credit"))
                ? new Dict { ["@type"] = "Organization", ["name"] = ds["credit"] } : null,
            ["includedInDataCatalog"] = new Dict
            {
                ["@type"] = "DataCatalog",
                ["name"] = $"datarepo catalog {PyText.Str(meta["catalog_id"])}",
                ["url"] = Absolute(baseUrl, "index.html"),
            },
        };
        var downloads = DownloadTables
            .Select(t => (t, url: DownloadUrl(dataUrl, ds, t)))
            .Where(x => x.url is not null)
            .Select(x => (object?)new Dict
            {
                ["@type"] = "DataDownload", ["name"] = $"{x.t}.parquet", ["encodingFormat"] = Parquet, ["contentUrl"] = x.url,
            })
            .ToList();
        if (downloads.Count > 0) doc["distribution"] = downloads;
        return WithoutNulls(doc);
    }

    public static Dict CatalogJsonLd(SiteFacts facts, string title, string? baseUrl)
    {
        var doc = new Dict
        {
            ["@context"] = "https://schema.org/",
            ["@type"] = "DataCatalog",
            ["name"] = title,
            ["url"] = Absolute(baseUrl, "index.html"),
            ["identifier"] = $"datarepo:catalog:{PyText.Str(facts.Meta["catalog_id"])}",
            ["description"] =
                $"Search and quantification results for {facts.Datasets.Count} public proteomics " +
                $"datasets reanalysed with one pipeline{ForQuestions(facts)}.",
            ["dataset"] = facts.Datasets.Select(ds => (object?)WithoutNulls(new Dict
            {
                ["@type"] = "Dataset", ["name"] = Id(ds), ["identifier"] = Id(ds), ["url"] = Absolute(baseUrl, PagePath(Id(ds))),
            })).ToList(),
        };
        return WithoutNulls(doc);
    }

    /// <summary>(Croissant dataType, repeated) for an Arrow type.</summary>
    private static (string DataType, bool Repeated) CroissantType(IArrowType type)
    {
        if (type is ListType list) return (CroissantType(list.ValueDataType).DataType, true);
        if (type is LargeListType large) return (CroissantType(large.ValueDataType).DataType, true);
        return (type.TypeId switch
        {
            ArrowTypeId.String or ArrowTypeId.LargeString => "sc:Text",
            ArrowTypeId.Int64 or ArrowTypeId.Int32 => "sc:Integer",
            ArrowTypeId.Double or ArrowTypeId.Float => "sc:Float",
            ArrowTypeId.Boolean => "sc:Boolean",
            _ => "sc:Text",
        }, false);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A Croissant 1.0 description of the bundles' Parquet files, and the files it could not read.</summary>
    /// <remarks>
    /// <para><b>One FileObject and one RecordSet per (dataset, table), not one FileSet per table.</b> A FileSet
    /// has to sit inside a container a loader can list -- a git repository, an archive or a local folder -- and
    /// a directory served over HTTP is none of those. The FileSet form passed <c>mlcroissant validate</c> and
    /// then loaded ZERO records from a served store, with no error. The per-file form loads; that was measured,
    /// not assumed (2026-09-23).</para>
    /// <para>Every file is hashed from the store, and its fields are read from the file's own Parquet schema
    /// rather than this package's, because a catalog may hold bundles written against an earlier schema. A file
    /// that cannot be read is left out and returned, never described from a guess at its contents.</para>
    /// </remarks>
    public static (Dict Doc, List<string> Unreadable) Croissant(SiteFacts facts, string title, string? baseUrl, string dataUrl)
    {
        var meta = facts.Meta;
        var datasets = facts.Datasets.Where(ds => PyText.Truthy(Bundle(ds)["bundle_id"])).ToList();
        var licences = datasets.Where(ds => PyText.Truthy(ds.GetValueOrDefault("licence")))
            .Select(ds => PyText.Str(ds["licence"])).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var credits = datasets.Where(ds => PyText.Truthy(ds.GetValueOrDefault("credit")))
            .Select(ds => PyText.Str(ds["credit"])).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var files = new List<object?>();
        var recordSets = new List<object?>();
        var unreadable = new List<string>();
        foreach (var ds in datasets)
        {
            var folder = Bundle(ds)["path"] as string ?? "";
            foreach (var table in DownloadTables)
            {
                var path = Path.Combine(folder, $"{table}.parquet");
                Schema schema;
                string sha256;
                // Python catches OSError only: a missing or unopenable file is left out, while a file that
                // opens but is not Parquet raises. The existence check stands in for pyarrow's OSError.
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(path);
                    using (var reader = new ParquetSharp.Arrow.FileReader(path)) schema = reader.Schema;
                    sha256 = Sha256(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add($"{Id(ds)}/{table}.parquet");
                    continue;
                }
                var key = $"{Id(ds)}/{table}";
                files.Add(new Dict
                {
                    ["@type"] = "cr:FileObject",
                    ["@id"] = $"{key}.parquet",
                    ["name"] = $"{key}.parquet",
                    ["contentUrl"] = DownloadUrl(dataUrl, ds, table),
                    ["encodingFormat"] = Parquet,
                    ["contentSize"] = $"{new FileInfo(path).Length} B",
                    ["sha256"] = sha256,
                });
                var fields = new List<object?>();
                foreach (var column in schema.FieldsList)
                {
                    var (dataType, repeated) = CroissantType(column.DataType);
                    var spec = new Dict
                    {
                        ["@type"] = "cr:Field",
                        ["@id"] = $"{key}/{column.Name}",
                        ["name"] = column.Name,
                        ["dataType"] = dataType,
                        ["source"] = new Dict
                        {
                            ["fileObject"] = new Dict { ["@id"] = $"{key}.parquet" },
                            ["extract"] = new Dict { ["column"] = column.Name },
                        },
                    };
                    if (repeated) spec["repeated"] = true;
                    fields.Add(spec);
                }
                recordSets.Add(new Dict { ["@type"] = "cr:RecordSet", ["@id"] = key, ["name"] = key, ["field"] = fields });
            }
        }
        var context = new Dict
        {
            ["@language"] = "en", ["@vocab"] = "https://schema.org/", ["sc"] = "https://schema.org/",
            ["cr"] = "http://mlcommons.org/croissant/", ["rai"] = "http://mlcommons.org/croissant/RAI/",
            ["dct"] = "http://purl.org/dc/terms/",
            ["equivalentProperty"] = "cr:equivalentProperty",
            ["examples"] = new Dict { ["@id"] = "cr:examples", ["@type"] = "@json" },
            ["samplingRate"] = "cr:samplingRate",
            ["citeAs"] = "cr:citeAs", ["column"] = "cr:column", ["conformsTo"] = "dct:conformsTo",
            ["data"] = new Dict { ["@id"] = "cr:data", ["@type"] = "@json" },
            ["dataType"] = new Dict { ["@id"] = "cr:dataType", ["@type"] = "@vocab" },
            ["extract"] = "cr:extract", ["field"] = "cr:field", ["fileProperty"] = "cr:fileProperty",
            ["fileObject"] = "cr:fileObject", ["fileSet"] = "cr:fileSet", ["format"] = "cr:format",
            ["includes"] = "cr:includes", ["isLiveDataset"] = "cr:isLiveDataset",
            ["jsonPath"] = "cr:jsonPath", ["key"] = "cr:key", ["md5"] = "cr:md5",
            ["parentField"] = "cr:parentField", ["path"] = "cr:path", ["recordSet"] = "cr:recordSet",
            ["references"] = "cr:references", ["regex"] = "cr:regex", ["repeated"] = "cr:repeated",
            ["replace"] = "cr:replace", ["separator"] = "cr:separator", ["source"] = "cr:source",
            ["subField"] = "cr:subField", ["transform"] = "cr:transform",
        };
        var builtUtc = PyText.Str(meta["built_utc"]);
        var doc = new Dict
        {
            ["@context"] = context,
            ["@type"] = "sc:Dataset",
            ["conformsTo"] = CroissantConformsTo,
            ["name"] = title,
            ["description"] = CatalogJsonLd(facts, title, baseUrl)["description"],
            ["url"] = Absolute(baseUrl, "index.html") ?? (dataUrl.Length > 0 ? dataUrl : null),
            // Not `version`: Croissant wants semver there, and a catalog id is a content hash. The id is what
            // identifies the data, so it goes where identifiers go.
            ["identifier"] = $"datarepo:catalog:{PyText.Str(meta["catalog_id"])}",
            ["datePublished"] = builtUtc.Length > 10 ? builtUtc[..10] : builtUtc,
            ["license"] = licences.Count > 0 ? licences.Select(x => LicenceUrl(x)).ToList() : null,
            ["creator"] = credits.Count > 0
                ? credits.Select(c => (object?)new Dict { ["@type"] = "Organization", ["name"] = c }).ToList() : null,
            ["distribution"] = files,
            ["recordSet"] = recordSets,
        };
        return (WithoutNulls(doc), unreadable);
    }

    // --- HTML ----------------------------------------------------------------------------------

    /// <summary><c>html.escape("" if value is None else str(value))</c>.</summary>
    private static string E(object? value) => PyText.HtmlEscape(value is null ? "" : PyText.Str(value));

    private static string JsonLdScript(Dict doc)
    {
        // `</` cannot appear inside a script element; escaping `<` keeps a title from closing it.
        var text = PyText.Json(doc).Replace("<", "\\u003c");
        return $"<script type=\"application/ld+json\">\n{text}\n</script>";
    }

    private static string Page(string title, string body, string root, Dict jsonld, Dict meta) =>
        Lf.Text($"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{E(title)}</title>
        <link rel="stylesheet" href="{root}style.css">
        <link rel="alternate" type="text/plain" href="{root}llms.txt" title="llms.txt">
        {JsonLdScript(jsonld)}
        </head>
        <body>
        {body}
        <footer>
        Generated by <a href="{Repository}">datarepo</a> {E(Version)} from catalog
        <code>{E(meta["catalog_id"])}</code>, built {E(meta["built_utc"])} by datarepo
        {E(meta["builder_version"])} (schema {E(meta["schema_version"])}).
        Every number on this site is read from that catalog; cite its id with any number you reuse.
        </footer>
        </body>
        </html>

        """);

    /// <summary>1,284 / 12.9K / 4.45M: a tile's headline. The exact value is in its title attribute.</summary>
    public static string Compact(object? value)
    {
        if (value is null) return "—";
        var x = value switch
        {
            long l => (double)l,
            System.Numerics.BigInteger b => (double)b,
            double d => d,
            _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        };
        // (threshold, divisor, suffix): thousands are compacted only from 10,000, so 9,236 stays exact.
        foreach (var (threshold, size, suffix) in new (double, double, string)[]
                 { (1_000_000_000, 1_000_000_000, "B"), (1_000_000, 1_000_000, "M"), (10_000, 1_000, "K") })
        {
            if (x >= threshold) return $"{PyText.FormatGeneral(x / size, 3)}{suffix}";
        }
        return PyText.Thousands(value);
    }

    private static readonly (Regex Pattern, string Replacement)[] MdInline =
    [
        (new Regex(@"\*\*(.+?)\*\*"), "<strong>$1</strong>"),
        (new Regex(@"(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])"), "<em>$1</em>"),
        (new Regex("`([^`]+)`"), "<code>$1</code>"),
        (new Regex(@"\[([^\]]+)\]\((https?://[^)\s]+|[\w./#-]+)\)"), "<a href=\"$2\">$1</a>"),
    ];

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    private static readonly Regex LineBreaks = new(@"\r\n|[\n\r\v\f\x1c\x1d\x1e\x85\u2028\u2029]");

    /// <summary>A small, safe subset of Markdown: <c>## </c> headings, <c>- </c> lists, paragraphs, and
    /// inline bold, italic, code and links.</summary>
    /// <remarks>The text is escaped BEFORE any markup is added, so the file cannot inject HTML, and a link
    /// may only be http(s) or a relative path. Deliberately not a Markdown library: the about text is a few
    /// paragraphs written by the instance owner.</remarks>
    public static string AboutHtml(string markdown)
    {
        static string Inline(string text)
        {
            text = PyText.HtmlEscape(text);
            foreach (var (pattern, repl) in MdInline) text = pattern.Replace(text, repl);
            return text;
        }

        var out_ = new List<string>();
        foreach (var block in Regex.Split(markdown.Trim(), @"\n\s*\n"))
        {
            var parts = LineBreaks.Split(block).ToList();
            if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
            var lines = parts.Where(l => l.Trim().Length > 0).Select(l => l.TrimEnd()).ToList();
            if (lines.Count > 0 && lines[0].StartsWith("## ", StringComparison.Ordinal))
            {
                out_.Add($"<h2>{Inline(lines[0][3..].Trim())}</h2>");
                lines = lines[1..];
            }
            if (lines.Count == 0) continue;
            if (lines.All(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)))
            {
                var items = string.Concat(lines.Select(l => $"<li>{Inline(l.TrimStart()[2..])}</li>"));
                out_.Add($"<ul>{items}</ul>");
            }
            else
            {
                out_.Add($"<p>{Inline(string.Join(" ", lines.Select(l => l.Trim())))}</p>");
            }
        }
        return string.Join("\n", out_);
    }

    private static Dict Counts(Dict ds) => (Dict)ds["findings_by_severity"]!;

    private static string SeverityWords(Dict ds)
    {
        var counts = Counts(ds);
        return $"Recorded against this dataset: {Plural(counts["error"]!, "error")}, " +
               $"{Plural(counts["warning"]!, "warning")} and {PyText.Thousands(counts["info"]!)} for information";
    }

    private static string FindingsCell(Dict ds)
    {
        var counts = Counts(ds);
        var head = $"{PyText.Str(counts["error"])} E / {PyText.Str(counts["warning"])} W / {PyText.Str(counts["info"])} I";
        return $"{head}<br><span class=\"note\">{E(string.Join(", ", Strings(ds["finding_codes"])))}</span>";
    }

    /// <summary>Datasets by organism and enrichment (aging 070 57d), so "which datasets are whole-proteome
    /// mouse" is one glance rather than 34 page opens.</summary>
    private static string CorpusTable(IReadOnlyList<Dict> datasets)
    {
        var grid = new OrderedDictionary<string, OrderedDictionary<string, long>>();
        foreach (var ds in datasets)
        {
            var organism = JoinWords(Strings(ds["organism_names"])) is { Length: > 0 } o ? o : "not recorded";
            var enrichment = EnrichmentCell(ds) is { Length: > 0 } e ? e : "not recorded";
            if (!grid.TryGetValue(organism, out var row)) grid[organism] = row = new OrderedDictionary<string, long>();
            row[enrichment] = row.GetValueOrDefault(enrichment) + 1;
        }
        var columns = grid.Values.SelectMany(r => r.Keys).Distinct()
            .OrderBy(e => e != "none").ThenBy(e => e, StringComparer.Ordinal).ToList();
        var head = string.Concat(columns.Select(c => $"<th class=\"num\">{E(c)}</th>"));
        var body = string.Join("\n", grid.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            $"<tr><td>{E(kv.Key)}</td>"
            + string.Concat(columns.Select(c => $"<td class=\"num\">{(kv.Value.TryGetValue(c, out var n) ? PyText.Str(n) : "")}</td>"))
            + $"<td class=\"num\">{PyText.Str(kv.Value.Values.Sum())}</td></tr>"));
        return "<div class=\"scroll\"><table>\n<thead><tr><th>Organism searched</th>" +
               $"{head}<th class=\"num\">All</th></tr></thead>\n<tbody>\n{body}\n</tbody></table></div>\n" +
               "<p class=\"note\">Datasets by the organism of the searched database and the declared " +
               "enrichment. \"none\" is a whole-proteome dataset; any other value is a capture, and a " +
               "protein's absence from one says nothing about the proteome.</p>";
    }

    private static string IndexHtml(SiteFacts facts, string title, string? baseUrl, string? about)
    {
        var meta = facts.Meta;
        var rows = new List<string>();
        foreach (var ds in facts.Datasets)
        {
            rows.Add(
                "<tr>" +
                $"<td><a href=\"{PagePath(Id(ds))}\">{E(Id(ds))}</a></td>" +
                $"<td>{E(Or(ds, "title", ""))}</td>" +
                $"<td>{E(string.Join(", ", Strings(ds["organism_names"])))}</td>" +
                $"<td>{E(EnrichmentCell(ds))}</td>" +
                $"<td class=\"num\">{N(ds["n_runs"])}</td>" +
                $"<td class=\"num\">{N(ds["n_psms_1pct"])}</td>" +
                $"<td class=\"num\">{N(ds["n_protein_groups_1pct"])}</td>" +
                $"<td class=\"num\">{N(ds["n_ptm_sites"])}</td>" +
                $"<td>{FindingsCell(ds)}</td>" +
                "</tr>");
        }
        // The tile labels and notes are written in FiguresOfMerit; "Unique peptides at 1% FDR" is G76's tile.
        var tiles = string.Join("\n", facts.Merit.Select(m =>
            $"<div class=\"tile\"><div class=\"tile-label\">{E(m["label"])}</div>" +
            $"<div class=\"tile-value\" title=\"{N(m["value"])}\">{Compact(m["value"])}</div>" +
            $"<div class=\"tile-note\">{E(m["note"])}</div></div>"));
        var aboutBlock = !string.IsNullOrEmpty(about) ? $"<section class=\"about\">\n{AboutHtml(about)}\n</section>" : "";
        var purposeSentence = facts.Purpose is { Length: > 0 } p ? $" The question they serve is {E(p)}." : "";
        var body = Lf.Text($"""
            <header>
            <h1>{E(title)}</h1>
            <p class="tagline">Public proteomics data, reanalysed with one pipeline{E(ForQuestions(facts))}.</p>
            </header>
            <main>
            {aboutBlock}
            <div class="tiles">
            {tiles}
            </div>
            <p class="note">Figures of merit from catalog <code>{E(meta["catalog_id"])}</code>. Hover a number for its exact value.</p>
            <h2>This repository</h2>
            <p class="lede">Search and quantification results for {Plural((long)facts.Datasets.Count, "public proteomics dataset")},
            reanalysed with one pipeline and stored with the same schema, so they can be compared.{purposeSentence}</p>
            <p><strong>For AI agents:</strong> start at <a href="llms.txt"><code>llms.txt</code></a>. The same facts
            as this page are in <a href="datasets.json"><code>datasets.json</code></a>.</p>
            <h2>The corpus at a glance</h2>
            {CorpusTable(facts.Datasets)}
            <h2>Datasets</h2>
            <p class="note">Counts are at 1% FDR, as the search engine reports them. "Findings" are what is
            recorded against a dataset, by type; most are about what the deposit lacks (no SDRF, no design).
            Read them before using it. Organism is that of the protein database searched.</p>
            <div class="scroll"><table>
            <thead><tr><th>Dataset</th><th>Title</th><th>Organism</th><th>Enrichment</th>
            <th class="num">Runs</th><th class="num">PSMs</th><th class="num">Protein groups</th>
            <th class="num">PTM sites</th><th>Findings</th></tr></thead>
            <tbody>
            {string.Join("\n", rows)}
            </tbody>
            </table></div>
            <h2>This catalog</h2>
            <dl>
            <dt>Catalog id</dt><dd><code>{E(meta["catalog_id"])}</code></dd>
            <dt>Built</dt><dd>{E(meta["built_utc"])}</dd>
            <dt>Built by</dt><dd>datarepo {E(meta["builder_version"])}, catalog format {E(meta["catalog_version"])}</dd>
            <dt>Schema</dt><dd>{E(meta["schema_version"])}</dd>
            <dt>Instance</dt><dd>{E(Or(meta, "instance", "not recorded"))}</dd>
            </dl>
            </main>
            """);
        return Page(title, body, "", CatalogJsonLd(facts, title, baseUrl), meta);
    }

    private static string DatasetHtml(Dict ds, Dict meta, string title, string? baseUrl, string? dataUrl, IReadOnlyList<string> keywords)
    {
        var datasetId = Id(ds);
        var pride = PrideUrl(datasetId);
        var counts = new (string Label, object? Value)[]
        {
            ("PSMs at 1% FDR", ds["n_psms_1pct"]),
            ("Peptidoforms at 1% FDR", ds["n_peptidoforms_1pct"]),
            ("Protein groups at 1% FDR", ds["n_protein_groups_1pct"]),
            ("PTM sites", ds["n_ptm_sites"]),
            ("Quantitative values", ds["n_quant_values"]),
            ("PSMs, all (including decoys and sub-threshold)", ds["n_psms_all"]),
        };
        var countRows = string.Join("\n", counts.Select(c => $"<tr><td>{E(c.Label)}</td><td class=\"num\">{N(c.Value)}</td></tr>"));
        var metrics = (Dict)ds["metrics"]!;
        foreach (var (name, label) in new[] { ("id_rate", "MS2 identification rate"), ("contamination_psm_share", "Contaminant share of PSMs") })
        {
            if (metrics.GetValueOrDefault(name) is Dict metric && PyText.Truthy(metric) && metric["value"] is double value)
            {
                countRows +=
                    $"\n<tr><td>{label} <span class=\"note\">({E(metric["definition_id"])})</span></td>" +
                    $"<td class=\"num\">{PyText.FormatFixed(value * 100, 1)}%</td></tr>";
            }
        }

        var findingList = Findings(ds).ToList();
        var findings = findingList.Count > 0
            ? string.Join("\n", findingList.Select(f =>
                $"<div class=\"finding {E(f["severity"])}\"><span class=\"code\">{E(f["code"])}</span> " +
                $"<span class=\"note\">{E(f["severity"])}</span><br>{E(f["message"])}</div>"))
            : "<p>None recorded.</p>";

        var modList = ((List<object?>)ds["modifications"]!).Cast<Dict>().ToList();
        string mods;
        if (modList.Count > 0)
        {
            var modRows = string.Join("\n", modList.Select(m =>
                $"<tr><td>{E(m["modification_name"])}</td><td>{E(PyText.Truthy(m["modification"]) ? m["modification"] : "—")}</td>" +
                $"<td class=\"num\">{N(m["n_sites"])}</td></tr>"));
            mods = Lf.Text($"""
                <div class="scroll"><table>
                <thead><tr><th>Search engine's name</th><th>UNIMOD</th><th class="num">Sites</th></tr></thead>
                <tbody>
                {modRows}
                </tbody></table></div>
                <p class="note">Every modification with a site on a target protein, most frequent first. A dash in the UNIMOD
                column means the modification has no UNIMOD term in the registry that searched it, not that it is
                unknown. One chemistry can appear under two names (UniProt's and MetaMorpheus's); the catalog's
                <code>ptm_sites_by_chemistry</code> view merges them.</p>
                """);
        }
        else
        {
            mods = "<p>No PTM sites are recorded for this dataset.</p>";
        }

        var bundle = Bundle(ds);
        string reconciliation;
        if (bundle["reconciliation_ok"] is false)
            reconciliation = "failed: " + E(string.Join(", ", Strings(bundle["reconciliation_failed"])))
                + " <span class=\"note\">(the bundle disagrees with the producer's own count here)</span>";
        else if (PyText.Truthy(bundle["reconciliation_ok"]))
            reconciliation = "every check agreed with the producer's own counts";
        else
            reconciliation = "not recorded";

        // The public pair when the producer records one (aging 069 55e): the private repo is where the commit
        // lives, and a reader cannot open it.
        var commitValue = PyText.Truthy(ds.GetValueOrDefault("pipeline_public_commit"))
            ? ds["pipeline_public_commit"] : ds.GetValueOrDefault("pipeline_commit");
        var repoValue = PyText.Truthy(ds.GetValueOrDefault("pipeline_public_repo"))
            ? ds["pipeline_public_repo"] : ds.GetValueOrDefault("pipeline_repo");
        var repo = PyText.Truthy(repoValue) ? PyText.Str(repoValue) : "";
        if (repo.EndsWith(".git", StringComparison.Ordinal)) repo = repo[..^4];
        string pipeline;
        if (PyText.Truthy(commitValue) && repo.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            var commit = PyText.Str(commitValue);
            pipeline = $"<a href=\"{E(repo)}/tree/{E(commit)}\">{E(commit.Length > 12 ? commit[..12] : commit)}</a>";
        }
        else
        {
            pipeline = E(PyText.Truthy(commitValue) ? commitValue : "not recorded");
        }

        var links = pride is not null ? new List<string> { $"<a href=\"{E(pride)}\">PRIDE record</a>" } : [];
        var downloads = DownloadTables
            .Select(t => (t, url: DownloadUrl(dataUrl, ds, t)))
            .Where(x => x.url is not null)
            .Select(x => $"<a href=\"{E(x.url)}\"><code>{x.t}.parquet</code></a>")
            .ToList();
        var downloadHtml = downloads.Count > 0
            ? "<p>" + string.Join(" · ", downloads) + "</p>"
            : "<p>The Parquet files for this dataset are not published yet.</p>";
        var licence = ds.GetValueOrDefault("licence");
        var licenceHtml = licence is string l && LicenceUrls.ContainsKey(l)
            ? $"<a href=\"{E(LicenceUrl(licence))}\">{E(licence)}</a>"
            : E(PyText.Truthy(licence) ? licence : "not recorded in the bundle");
        var organismSearched = JoinWords(Strings(ds["organism_names"])) is { Length: > 0 } o ? o : "not recorded";

        var body = Lf.Text($"""
            <header>
            <p class="crumb"><a href="../index.html">{E(title)}</a> / {E(datasetId)}</p>
            <h1>{E(Title(ds))}</h1>
            <p>{E(datasetId)}{(links.Count > 0 ? " · " + string.Join(" · ", links) : "")}</p>
            </header>
            <main>
            <div class="generated"><span class="label">Generated summary</span>
            {E(Summary(ds))}
            <br><span class="note">Written from catalog fields only (D17), with no model involved. It says nothing the tables below do not.</span></div>

            <h2>Open findings</h2>
            <p><strong>{SeverityWords(ds)}</strong></p>
            {findings}

            <h2>What was found</h2>
            <div class="scroll"><table><tbody>
            {countRows}
            </tbody></table></div>

            <h2>How it was measured</h2>
            <dl>
            <dt>Organism searched</dt><dd>{E(organismSearched)} <span class="note">(the protein database's organism, not a statement about the samples)</span></dd>
            <dt>Acquisition</dt><dd>{E(ds.GetValueOrDefault("acquisition"))}</dd>
            <dt>Quantification</dt><dd>{E(ds.GetValueOrDefault("quant_method"))}</dd>
            <dt>Labelling</dt><dd>{E(ds.GetValueOrDefault("labelling"))}</dd>
            <dt>Enrichment</dt><dd>{E(EnrichmentCell(ds))}</dd>
            <dt>Instrument vendor</dt><dd>{E(Or(ds, "instrument_vendor", "not recorded"))}</dd>
            <dt>Runs / samples</dt><dd>{N(ds["n_runs"])} / {N(ds["n_samples"])}</dd>
            <dt>Sample metadata</dt><dd>{E(Or(ds, "sdrf_status", "not recorded"))}</dd>
            <dt>Search engine</dt><dd>{E(ds.GetValueOrDefault("search_engine"))} {E(ds.GetValueOrDefault("search_engine_version"))}</dd>
            <dt>Protein database</dt><dd>{E(Or(ds, "search_database", "not recorded"))}</dd>
            <dt>Pipeline commit</dt><dd>{pipeline}</dd>
            </dl>

            <h2>Modifications</h2>
            {mods}

            <h2>Data</h2>
            {downloadHtml}
            <dl>
            <dt>Bundle</dt><dd><code>{E(bundle["bundle_id"])}</code>, written {E(bundle["written_utc"])} by ingester {E(bundle["ingester_version"])}</dd>
            <dt>Reconciliation</dt><dd>{reconciliation}</dd>
            <dt>Licence</dt><dd>{licenceHtml}</dd>
            <dt>Credit</dt><dd>{E(Or(ds, "credit", "not recorded in the bundle"))}</dd>
            </dl>
            </main>
            """);
        return Page($"{datasetId}: {Title(ds)}", body, "../",
            DatasetJsonLd(ds, meta, baseUrl, dataUrl, keywords), meta);
    }
}
