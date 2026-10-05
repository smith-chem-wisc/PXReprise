using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.Catalog;
using DataRepo.Ingest.Sources;

namespace DataRepo.Mcp;

/// <summary>The tool was called with something it cannot act on. The message says what to send instead.</summary>
/// <remarks>The C# counterpart of the Python <c>ToolError</c>.</remarks>
public class ToolException(string message) : DataRepoException(message);

/// <summary>What a catalog says about itself. Read once, when the server opens it.</summary>
public sealed record CatalogIdentity(
    object? CatalogId,
    object? CatalogVersion,
    object? SchemaVersion,
    object? Builder,
    object? BuilderVersion,
    object? BuiltUtc,
    object? Instance,
    object? QpxVersion,
    object? Release,
    List<Dictionary<string, object?>> Bundles,
    List<Dictionary<string, object?>> StudyBundles)
{
    public Dictionary<string, object?> Base() => new(StringComparer.Ordinal)
    {
        ["catalog_id"] = CatalogId,
        ["catalog_version"] = CatalogVersion,
        ["schema_version"] = SchemaVersion,
        ["built_by"] = PyValues.Truthy(Builder) ? $"{PyValues.Str(Builder)} {PyValues.Str(BuilderVersion)}" : null,
        ["built_utc"] = BuiltUtc,
        ["instance"] = Instance,
        ["release"] = Release,
        ["served_by"] = $"datarepo {CatalogBuilder.PackageVersion}",
    };

    internal static CatalogIdentity Read(Sandbox box)
    {
        var metaRows = box.Dicts("SELECT * FROM catalog_meta");
        if (metaRows.Count == 0) throw new CatalogException($"{box.Path} has no catalog_meta; it is not a datarepo catalog");
        var meta = metaRows[0];
        object? release = null;
        var notes = meta.GetValueOrDefault("notes");
        var text = PyValues.Truthy(notes) ? notes : "{}";
        if (text is string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var parsed = SourcesPy.FromJson(doc.RootElement);
                if (parsed is Dictionary<string, object?> d) release = d.GetValueOrDefault("release");
                else if (PyValues.Truthy(parsed))
                    // `(json.loads(...) or {}).get(...)` on a list or a number is an AttributeError in the Python.
                    throw new InvalidOperationException($"'{SourcesPy.TypeName(parsed)}' object has no attribute 'get'");
            }
            catch (JsonException)
            {
                release = null;
            }
        }
        var bundles = box.Dicts(
            "SELECT dataset_id, bundle_id, ingester_version, written_utc, reconciliation_ok "
            + "FROM catalog_bundles ORDER BY dataset_id");
        var study = box.HasTable("catalog_study_bundles")
            ? box.Dicts("SELECT * FROM catalog_study_bundles ORDER BY layer")
            : [];
        return new CatalogIdentity(
            meta.GetValueOrDefault("catalog_id"),
            meta.GetValueOrDefault("catalog_version"),
            meta.GetValueOrDefault("schema_version"),
            meta.GetValueOrDefault("builder"),
            meta.GetValueOrDefault("builder_version"),
            meta.GetValueOrDefault("built_utc"),
            meta.GetValueOrDefault("instance"),
            meta.GetValueOrDefault("qpx_version"),
            release,
            bundles,
            study);
    }
}

/// <summary>The three tools, over one open catalog (C# port of <c>mcp.CatalogServer</c>). No SDK involved --
/// <see cref="Mcp.Serve"/> wires this to one.</summary>
/// <remarks>
/// <para>One catalog, named by explicit path, never auto-discovered (D13). A server that searched for a catalog
/// could answer today from a working build and tomorrow from a release, with nothing in either answer saying
/// which.</para>
/// <para><b>Every result carries its provenance</b> (D13): the <c>catalog_id</c>, the catalog's versions, and the
/// count of bundles this catalog holds. <b>Provenance is a fact about the server and is inferred from nothing.</b>
/// It does not narrow to what an answer touched: it used to, by reading the result's own <c>bundle_id</c> and
/// <c>dataset_id</c> columns, and a query can put anything in a column with those names. <c>catalog_id</c> is a hash
/// of the exact (dataset, bundle) set, so naming it already states precisely which frozen copy of every dataset
/// was available. That is the whole citation (D20: anything that certifies an answer comes from the engine, never
/// from the query).</para>
/// <para><b>The bar is zero silently-wrong answers, not a percentage</b> (D15). <c>search</c> reports what it
/// searched with each source's row count; <c>describe</c> reads its column meanings from the generated schema
/// prose and says when the catalog was built against another schema; <c>sql</c> names the empty tables a
/// statement read in its envelope, because a tool has to be chosen and an envelope field cannot be skipped
/// (D19).</para>
/// </remarks>
public sealed partial class CatalogServer : IDisposable
{
    /// <summary>Hits per search kind before it says there are more, and the most a caller may ask for.</summary>
    public const int SearchLimit = 25;

    public const int SearchLimitMax = 200;

    /// <summary>What the no-target <c>describe()</c> says before anything else.</summary>
    public static readonly IReadOnlyList<string> ReadFirst =
    [
        "`target_decoy` has THREE values: target, decoy and contaminant. Count targets explicitly; "
        + "decoys are FDR machinery and contaminants are reagents.",
        "The contaminant label is per dataset: human albumin is a target in a human search and a "
        + "contaminant in a rodent one. Never use one corpus-wide flag.",
        "`datasets.organisms` is the organism of the searched protein database, not a statement "
        + "about the samples.",
        "A protein group's accessions are sorted alphabetically, in `protein_accessions` and in "
        + "`protein_group_id`: the first is not a leading or razor protein.",
        "`pep` is run-relative: never compare its values across datasets.",
        "An empty table means nothing was delivered, not that the answer is none.",
    ];

    /// <summary>Columns whose VALUE means something only inside the search that wrote it (pep 002, G70).</summary>
    /// <remarks>MetaMorpheus retrains its PEP model from scratch on every search, so two datasets' <c>pep</c> come
    /// from two different models even on one release. Put in the <c>sql</c> envelope, not only in the column
    /// descriptions, for D19's reason: <c>describe</c> can be skipped and an envelope field cannot.</remarks>
    public static readonly IReadOnlyDictionary<string, string> RunRelativeColumns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pep"] = "run-relative: a per-search model's score, not comparable across datasets or releases",
        ["best_pep"] = "run-relative: the lowest `pep` of the dataset's PSMs, so it inherits `pep`'s scale",
        ["pep_q_value"] =
            "ordered by `pep`, so it moves whenever `pep` does; a count at a threshold compares within "
            + "one MetaMorpheus release. Across releases use `q_value`, which does not depend on PEP",
    };

    /// <summary>Core tables whose rows ARE samples (aging 078).</summary>
    public static readonly IReadOnlySet<string> SampleTables = new HashSet<string>(StringComparer.Ordinal) { "samples", "sample_characteristics" };

    /// <summary>The search kinds, in the order an omitted <c>kind</c> searches them.</summary>
    public static readonly IReadOnlyList<string> SearchKinds =
        ["dataset", "protein", "peptide", "modification", "sample", "run", "definition", "localization"];

    public Sandbox Box { get; }

    public CatalogIdentity Identity { get; }

    private Dictionary<string, Dictionary<string, object?>>? _tables;
    private readonly (long, long)? _openedStat;

    public CatalogServer(string catalog, double timeoutSeconds = Sandbox.TimeoutSecondsDefault, int rowCap = Sandbox.RowCap, int charCap = Sandbox.CharCap)
    {
        Box = new Sandbox(catalog, timeoutSeconds, rowCap, charCap);
        try
        {
            Identity = CatalogIdentity.Read(Box);
        }
        catch
        {
            Box.Dispose();
            throw;
        }
        _catalogFile = catalog;
        _openedStat = FileStat();
    }

    private readonly string _catalogFile;

    private (long, long)? FileStat()
    {
        try
        {
            var info = new FileInfo(_catalogFile);
            if (!info.Exists) return null;
            return (info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Set when the file at the catalog's path is no longer the one this server opened.</summary>
    /// <remarks>aging's server answered from <c>62d419643e71320c</c> for hours after they published
    /// <c>1e2f13be6f121fd7</c> over the same path (070, 57k). The server keeps what it opened, on purpose: an answer
    /// must not change catalogs halfway through a conversation. What it must not do is keep quiet about it.</remarks>
    public string? CatalogFileChanged
    {
        get
        {
            if (FileStat() == _openedStat) return null;
            return $"The file at {Box.Path} has changed since this server opened it. Every answer "
                + $"still comes from the catalog it opened ({PyValues.Str(Identity.CatalogId)}), which may no "
                + "longer be the one published there. Restart the MCP server to serve the current file.";
        }
    }

    /// <summary>Set when the prose this server carries describes a different schema than the catalog holds.</summary>
    /// <remarks>Serving a 0.0.5 catalog from 0.0.7 code, <c>describe('proteins')</c> narrated a fix in the past tense
    /// and directed the reader to a column that catalog does not have. <b>An agent that did the diligent thing and
    /// called <c>describe</c> first came away more confident and more wrong.</b> The fix is to say, in every result
    /// carrying a description, which schema the description is of.</remarks>
    public string? SchemaDrift
    {
        get
        {
            var served = Identity.SchemaVersion;
            if (!PyValues.Truthy(served) || Equals(served, SchemaContract.Version)) return null;
            return $"This catalog was built against schema {PyValues.Str(served)}; the descriptions here are generated "
                + $"from schema {SchemaContract.Version}, which this server was built against. Where they "
                + "disagree THE CATALOG IS RIGHT and the description is of a later version: a column "
                + "the prose mentions may not exist here, and a rule it states may not yet hold. Trust "
                + "the `type`, the `populated` count and the rows over the prose. Rebuilding the "
                + "catalog with this version of datarepo makes them agree.";
        }
    }

    public void Dispose() => Box.Dispose();

    // --- shared ---------------------------------------------------------------------------------------

    /// <summary>Every table and view actually in this catalog, with row count and kind.</summary>
    /// <remarks>Read from <c>catalog_tables</c> where it exists and from <c>information_schema</c> where it does not,
    /// so a catalog built by an older <c>datarepo</c> still describes itself. A table <c>catalog_tables</c> does not
    /// count is counted here rather than left None: an unknown count renders the same as zero everywhere it is
    /// read, and "empty" is the one word this server must not say when it does not know.</remarks>
    public Dictionary<string, Dictionary<string, object?>> TablesInCatalog()
    {
        if (_tables is null)
        {
            var rows = Box.HasTable("catalog_tables") ? Box.Dicts("SELECT table_name, rows, kind FROM catalog_tables") : [];
            var found = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var r in rows) found[PyValues.Str(r["table_name"])] = new Dictionary<string, object?>(r, StringComparer.Ordinal);
            foreach (var row in Box.Dicts("SELECT table_name, table_type FROM information_schema.tables"))
            {
                var name = PyValues.Str(row["table_name"]);
                if (found.ContainsKey(name)) continue;
                found[name] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table_name"] = name,
                    ["rows"] = Box.OneValue($"SELECT count(*) FROM \"{name}\""),
                    ["kind"] = Equals(row["table_type"], "VIEW") ? "view" : "table",
                };
            }
            _tables = found.OrderBy(kv => kv.Key, PyValues.CodePointOrder).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
        return _tables;
    }

    private static object? Rows(Dictionary<string, object?>? info) => info?.GetValueOrDefault("rows");

    /// <summary><c>(t.get("rows") or 0)</c> as a number.</summary>
    private static long RowsOrZero(Dictionary<string, object?>? info) => Rows(info) switch
    {
        long l => l,
        System.Numerics.BigInteger b => (long)b,
        _ => 0,
    };

    /// <summary>What frozen data this server holds. <b>Identical on every answer, and inferred from nothing.</b></summary>
    /// <remarks><b>Compact by default; the bundle list is in <c>describe()</c> only</b> (aging 070, DATAREPO-58).
    /// Nothing is lost by carrying it once: <c>catalog_id</c> is the hash of exactly that list. It used to narrow per
    /// answer -- 'the bundles named in the rows returned' -- and labelling that guess as provenance made it
    /// forgeable: <c>SELECT max(dataset_id) AS dataset_id, count(*) FROM ptm_sites</c> returned the catalog-wide
    /// count stamped with one dataset's bundle.</remarks>
    private Dictionary<string, object?> Provenance(bool full = false)
    {
        var output = Identity.Base();
        output["n_bundles"] = (long)Identity.Bundles.Count;
        if (full)
        {
            output["bundles"] = Identity.Bundles.Select(b => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dataset_id"] = b["dataset_id"],
                ["bundle_id"] = b["bundle_id"],
            }).ToList();
            output["bundles_are"] =
                "every bundle this catalog holds, which is what `catalog_id` is a hash of. This is "
                + "a fact about the server, not a claim about any answer: no question can change it, "
                + "and it is NOT narrowed to what a result touched, because that cannot be determined "
                + "from a result without being wrong sometimes and silent about which times.";
            if (Identity.StudyBundles.Count > 0)
                output["study_bundles"] = Identity.StudyBundles.Select(b => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["layer"] = b.GetValueOrDefault("layer"),
                    ["bundle_id"] = b.GetValueOrDefault("bundle_id"),
                }).ToList();
        }
        else
        {
            if (Identity.StudyBundles.Count > 0) output["n_study_bundles"] = (long)Identity.StudyBundles.Count;
            output["bundles_are"] =
                "the whole citation is catalog_id: it is a hash of the exact set of bundles this "
                + "catalog holds, listed once by describe() with no target. It describes the server, "
                + "not a slice this answer touched.";
        }
        // A release is archived at a fixed path and never changes; a working catalog is rebuilt in place and its id
        // moves when the bundles under it do. Both ids are exact -- only one is durable.
        output["catalog_kind"] = PyValues.Truthy(Identity.Release) ? "release" : "working build";
        if (!PyValues.Truthy(Identity.Release))
            output["catalog_kind_means"] =
                "a working catalog is rebuilt in place, so this catalog_id identifies the data "
                + "exactly today but the file at this path may be replaced. Cite a release.";
        var changed = CatalogFileChanged;
        if (changed is not null) output["catalog_file_changed"] = changed;
        return output;
    }

    // --- describe -------------------------------------------------------------------------------------

    /// <summary>What this catalog is, what is in it, and what a table's columns mean.</summary>
    /// <param name="target">Nothing for the catalog itself; a table or view name for its columns; an enum name for
    /// its permissible values; a definition id for the published text behind a stored number; <c>"tables"</c> for
    /// the full list.</param>
    /// <param name="detail"><c>"concise"</c> or <c>"detailed"</c>. Detailed renders each column as an object.</param>
    public Dictionary<string, object?> Describe(string? target = null, string detail = "concise")
    {
        if (detail is not ("concise" or "detailed"))
            throw new ToolException($"detail is 'concise' or 'detailed', not {PyFormat.Repr(detail)}");
        target = PyValues.Strip(target ?? "");
        var lower = PyValues.Lower(target);
        if (target.Length == 0 || lower is "catalog" or "overview" or ".") return DescribeCatalog();
        if (lower is "tables" or "schema") return DescribeTables();
        if (TablesInCatalog().ContainsKey(target)) return DescribeTable(target, detail);
        if (SchemaDocs.Enums.ContainsKey(target) || SchemaDocs.StudyEnums.Values.Any(e => e.ContainsKey(target)))
            return DescribeEnum(target);
        var definition = DescribeDefinition(target);
        if (definition is not null) return definition;
        foreach (var layer in SchemaDocs.StudyTables.Keys)
            if (lower == PyValues.Lower(layer)) return DescribeStudyLayer(layer);
        throw new ToolException(UnknownTarget(target));
    }

    private string UnknownTarget(string target)
    {
        var pool = TablesInCatalog().Keys.Concat(SchemaDocs.Enums.Keys).Concat(["catalog", "tables"]);
        var close = Difflib.GetCloseMatches(target, pool, n: 4, cutoff: 0.6);
        var hint = close.Count > 0 ? $" Did you mean {string.Join(", ", close)}?" : "";
        return $"{PyFormat.Repr(target)} is not a table, view, enum or definition id in this catalog.{hint} "
            + "Call describe with no target for the catalog, or 'tables' for the full list.";
    }

    private Dictionary<string, object?> DescribeCatalog()
    {
        var tables = TablesInCatalog();
        var populated = tables.Where(kv => RowsOrZero(kv.Value) > 0).ToList();
        var datasets = Box.HasTable("dataset_overview")
            ? Box.Dicts(
                "SELECT dataset_id, title, organisms, acquisition, quant_method, labelling, "
                + "enrichment, enrichment_mixed, instrument_vendor, n_runs, n_samples, n_psms_1pct, "
                + "n_peptidoforms_1pct, n_protein_groups_1pct, n_ptm_sites, n_open_findings "
                + "FROM dataset_overview ORDER BY dataset_id")
            : [];
        var findings = Box.HasTable("findings")
            ? Box.Dicts(
                "SELECT code, severity, count(*) AS n, min(message) AS example FROM findings "
                + "WHERE severity IN ('warning', 'error') GROUP BY 1, 2 ORDER BY n DESC")
            : [];
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["catalog"] = Box.Path,
            ["what_it_is"] = SchemaDocs.SchemaDescription,
        };
        var drift = SchemaDrift;
        if (drift is not null) output["schema_drift"] = drift;
        output["instance"] = Identity.Instance;
        output["qpx_version"] = Identity.QpxVersion;
        output["datasets"] = datasets;
        // Every layer the BUILD knows about, each saying whether a delivery was loaded: "the tables are here and
        // nobody has delivered rows yet" is a different fact from "there is no study layer".
        output["study_layers"] = StudyLayers();
        output["tables_with_rows"] = populated
            .OrderByDescending(kv => RowsOrZero(kv.Value))
            .Select(kv => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["table"] = kv.Key,
                ["rows"] = Rows(kv.Value),
                ["kind"] = kv.Value.GetValueOrDefault("kind"),
            }).ToList();
        var populatedNames = populated.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        output["tables_empty"] = tables.Keys.Where(n => !populatedNames.Contains(n)).Order(PyValues.CodePointOrder).ToList();
        output["empty_means"] =
            "the table exists and holds no rows in THIS catalog. It is not evidence that the "
            + "thing does not exist -- a producer may simply not have delivered it yet. Say so "
            + "rather than answering from the empty table.";
        output["open_findings"] = findings;
        // The rules an agent in aging's evaluation reached only by querying, or not at all (aging 070, pep 002).
        output["read_first"] = ReadFirst.ToList();
        output["next"] = new List<string>
        {
            "describe('tables') for every table with its row count",
            "describe('<table>') for one table's columns and what they mean",
            "search('<gene, accession, peptide, tissue or modification>') to find ids",
            "sql('SELECT ...') for anything else",
        };
        output["provenance"] = Provenance(full: true);
        return output;
    }

    /// <summary>Every study layer this build knows, loaded or not. aging's benchmark distinguishes NO_TABLE from
    /// EMPTY_TABLE, and so must this.</summary>
    private List<Dictionary<string, object?>> StudyLayers()
    {
        var loaded = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var loadedOrder = new List<object?>();
        foreach (var b in Identity.StudyBundles)
        {
            var key = PyValues.Str(b.GetValueOrDefault("layer"));
            if (!loaded.ContainsKey(key)) loadedOrder.Add(b.GetValueOrDefault("layer"));
            loaded[key] = b;
        }
        var known = TablesInCatalog();
        var output = new List<Dictionary<string, object?>>();
        foreach (var (layer, tables) in SchemaDocs.StudyTables)
        {
            var present = tables.Keys.Where(known.ContainsKey).ToList();
            if (present.Count == 0 && !loaded.ContainsKey(layer)) continue;
            var delivery = loaded.GetValueOrDefault(layer) ?? new Dictionary<string, object?>();
            output.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["layer"] = layer,
                ["tables_present"] = (long)present.Count,
                ["delivery_loaded"] = loaded.ContainsKey(layer),
                ["bundle_id"] = delivery.GetValueOrDefault("bundle_id"),
                ["layer_version"] = delivery.GetValueOrDefault("layer_version"),
                ["rows"] = present.Sum(name => RowsOrZero(known[name])),
            });
        }
        foreach (var layer in loadedOrder)
        {
            var delivery = loaded[PyValues.Str(layer)];
            if (layer is string name && SchemaDocs.StudyTables.ContainsKey(name)) continue;
            output.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["layer"] = layer,
                ["tables_present"] = null,
                ["delivery_loaded"] = true,
                ["bundle_id"] = delivery.GetValueOrDefault("bundle_id"),
                ["layer_version"] = delivery.GetValueOrDefault("layer_version"),
            });
        }
        return output;
    }

    private Dictionary<string, object?> DescribeTables()
    {
        var docs = AllDocs();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var (name, table) in TablesInCatalog())
        {
            var doc = docs.GetValueOrDefault(name);
            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["table"] = name,
                ["rows"] = Rows(table),
                ["kind"] = table.GetValueOrDefault("kind"),
                ["holds"] = doc?.Description,
                ["layer"] = doc?.Layer,
            });
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tables"] = rows,
            ["kinds"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bundle"] = "written by the ingester from a producer's results; the evidence",
                ["view"] = "an acceptance rule or a coarser grain applied to a bundle table",
                ["derived"] = "built by `datarepo build` across datasets; the reason a catalog exists",
                ["study"] = "delivered by a study layer, keyed on core identifiers (U5)",
                ["table"] = "a catalog's own bookkeeping (catalog_meta, catalog_bundles, ...)",
            },
            ["provenance"] = Provenance(),
        };
    }

    /// <summary>One column's prose, as <c>describe</c> reads it.</summary>
    private sealed record ColumnProse(string? Description, bool Identifier = false, string? Enum = null, string? Unit = null);

    /// <summary>One table's prose: what a row is, its layer, its columns.</summary>
    private sealed record TableProse(string? Description, string? Layer, Dictionary<string, ColumnProse> Columns);

    /// <summary>Prose for every table an agent can reach, keyed by table name.</summary>
    /// <remarks>Three sources, and the third is the one that was missing: the core schema, each study layer's schema,
    /// and <see cref="CatalogBuilder.DerivedDocs"/> for the tables and views <c>build</c> invents. The derived ones
    /// are in no LinkML file -- and they are exactly the tables <c>search</c> answers from.</remarks>
    private static Dictionary<string, TableProse> AllDocs()
    {
        static Dictionary<string, ColumnProse> Columns(TableDoc doc) =>
            doc.Columns.ToDictionary(c => c.Name, c => new ColumnProse(c.Description, c.Identifier, c.Enum, c.Unit), StringComparer.Ordinal);

        var docs = new Dictionary<string, TableProse>(StringComparer.Ordinal);
        foreach (var (name, doc) in SchemaDocs.Tables) docs[name] = new TableProse(doc.Description, null, Columns(doc));
        foreach (var (layer, tables) in SchemaDocs.StudyTables)
            foreach (var (name, doc) in tables) docs[name] = new TableProse(doc.Description, layer, Columns(doc));
        foreach (var (table, columns) in CatalogBuilder.DerivedColumnDocs)
        {
            if (!docs.TryGetValue(table, out var existing)) continue;
            var merged = new Dictionary<string, ColumnProse>(existing.Columns, StringComparer.Ordinal);
            foreach (var (column, text) in columns) merged[column] = new ColumnProse(text);
            docs[table] = existing with { Columns = merged };
        }
        foreach (var (name, doc) in CatalogBuilder.DerivedDocs)
            docs[name] = new TableProse(doc.Description, null,
                doc.Columns.ToDictionary(kv => kv.Key, kv => new ColumnProse(kv.Value), StringComparer.Ordinal));
        return docs;
    }

    /// <summary>Non-null count per column, in one pass.</summary>
    /// <remarks>The other half of "an empty table is named": a table with rows and a 100%-NULL column is invisible
    /// to a row count. aging's <c>samples</c> held 57 rows with <c>organism_part</c>, <c>cell_type</c>,
    /// <c>disease</c>, <c>condition</c> and <c>cell_line</c> all entirely NULL, and a search for "plasma" came back
    /// <c>rows: 57, hits: 0</c>, which reads as "we looked and it is not there".</remarks>
    private Dictionary<string, long> Populated(string table, IReadOnlyList<string>? columns = null)
    {
        var names = columns?.ToList() ?? ColumnTypes(table).Keys.ToList();
        if (names.Count == 0) return [];
        var counts = string.Join(", ", names.Select(name => $"count(\"{name}\") AS \"{name}\""));
        var rows = Box.Dicts($"SELECT {counts} FROM \"{table}\"");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (rows.Count > 0)
            foreach (var (k, v) in rows[0]) result[k] = v is long l ? l : 0;
        return result;
    }

    /// <summary>DuckDB's own types for the table as built, not the Arrow schema's: the catalog carries columns the
    /// schema does not (<c>dataset_id</c>, <c>bundle_id</c>), and <c>VARCHAR[]</c> is the form a
    /// <c>list_contains(...)</c> has to be written for anyway.</summary>
    private Dictionary<string, object?> ColumnTypes(string table)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in Box.Dicts(
                     "SELECT column_name, data_type FROM information_schema.columns "
                     + "WHERE table_name = ? ORDER BY ordinal_position", [table]))
            result[PyValues.Str(row["column_name"])] = row["data_type"];
        return result;
    }

    private Dictionary<string, object?> DescribeTable(string table, string detail)
    {
        var doc = AllDocs().GetValueOrDefault(table);
        var types = ColumnTypes(table);
        var columnDocs = doc?.Columns ?? [];
        var info = TablesInCatalog().GetValueOrDefault(table);
        var total = RowsOrZero(info);
        // In both detail modes, because a 100%-NULL column is the thing most likely to produce a confident false
        // negative, and an agent asking for `concise` has not asked to be misled.
        var populated = total != 0 ? Populated(table) : [];
        var columns = new List<Dictionary<string, object?>>();
        foreach (var (name, type) in types)
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["column"] = name, ["type"] = type };
            if (populated.TryGetValue(name, out var count))
            {
                entry["populated"] = count;
                if (count == 0)
                    entry["all_null"] =
                        $"NULL on all {PyValues.Thousands(total)} rows. Nothing has been delivered in this column, "
                        + "so a query filtering on it returns nothing for THAT reason -- not "
                        + "because the answer is negative.";
            }
            if (columnDocs.TryGetValue(name, out var cdoc))
            {
                entry["means"] = cdoc.Description;
                if (cdoc.Identifier) entry["identifier"] = true;
                if (!string.IsNullOrEmpty(cdoc.Enum))
                {
                    entry["enum"] = cdoc.Enum;
                    entry["values"] = EnumValues(cdoc.Enum);
                }
                if (!string.IsNullOrEmpty(cdoc.Unit)) entry["unit"] = cdoc.Unit;
            }
            else if (name is "dataset_id" or "bundle_id" or "study_layer" or "study_bundle_id")
            {
                entry["means"] =
                    "provenance added by `datarepo build`: which dataset and which bundle this row "
                    + "came from. Every answer can be traced back through it.";
            }
            columns.Add(entry);
        }
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["table"] = table,
            ["kind"] = info?.GetValueOrDefault("kind"),
            ["rows"] = Rows(info),
            ["one_row_is"] = doc?.Description,
        };
        var drift = SchemaDrift;
        if (drift is not null) output["schema_drift"] = drift;
        output["layer"] = doc?.Layer;
        // `concise` renders each column as one line instead of a JSON object: the SAME facts, a third of the tokens.
        output["columns"] = detail == "detailed" ? columns : columns.Select(c => (object?)OneLineColumn(c)).ToList();
        output["provenance"] = Provenance();
        if (total == 0)
            output["empty_means"] =
                $"{table} exists in this catalog and holds no rows. Do not answer a question about "
                + "it from another table; say the data has not been delivered.";
        if (table is "psms" or "peptidoforms" or "protein_groups")
            output["see_also"] =
                $"{table}_1pct applies the producing search engine's acceptance rule once, so no "
                + "caller has to restate it. Use it unless you specifically want the rejected rows.";
        if (table == "ptm_sites")
            output["see_also"] =
                "ptm_sites is keyed on the search engine's own name for a modification, which is "
                + "the grain it was measured at; ptm_sites_by_chemistry groups the handful of sites "
                + "that reach one dataset under two names.";
        return output;
    }

    /// <summary><c>concise</c> form of one column: name, type, meaning, and an enum's values if it has them.</summary>
    private static string OneLineColumn(Dictionary<string, object?> entry)
    {
        var line = PyValues.Strip($"{PyValues.Str(entry["column"])} {(PyValues.Truthy(entry.GetValueOrDefault("type")) ? PyValues.Str(entry["type"]) : "")}");
        if (PyValues.Truthy(entry.GetValueOrDefault("identifier"))) line += " (identifier)";
        if (PyValues.Truthy(entry.GetValueOrDefault("means"))) line += $" -- {PyValues.Str(entry["means"])}";
        if (PyValues.Truthy(entry.GetValueOrDefault("values")))
            line += $" One of: {string.Join(", ", ((IEnumerable<string>)entry["values"]!))}.";
        if (PyValues.Truthy(entry.GetValueOrDefault("unit"))) line += $" Unit: {PyValues.Str(entry["unit"])}.";
        if (PyValues.Truthy(entry.GetValueOrDefault("all_null"))) line += $" **{PyValues.Str(entry["all_null"])}**";
        else if (entry.GetValueOrDefault("populated") is { } populated) line += $" [{PyValues.Thousands(populated)} non-null]";
        return line;
    }

    private static List<string> EnumValues(string name)
    {
        if (SchemaDocs.Enums.TryGetValue(name, out var e)) return e.Values.ToList();
        foreach (var enums in SchemaDocs.StudyEnums.Values)
            if (enums.TryGetValue(name, out var s)) return s.Values.ToList();
        return [];
    }

    private Dictionary<string, object?> DescribeEnum(string name)
    {
        string? layer = null;
        if (!SchemaDocs.Enums.TryGetValue(name, out var entry))
        {
            foreach (var (layerName, enums) in SchemaDocs.StudyEnums)
                if (enums.TryGetValue(name, out entry))
                {
                    layer = layerName;
                    break;
                }
        }
        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enum"] = name,
            ["layer"] = layer,
            ["means"] = entry!.Description,
        };
        var drift = SchemaDrift;
        if (drift is not null) output["schema_drift"] = drift;
        output["values"] = entry.Values.ToList();
        output["note"] = "a column of this enum holds exactly one of these strings, or NULL";
        output["provenance"] = Provenance();
        return output;
    }

    /// <summary>The published text behind a stored number, if <paramref name="target"/> names one. Definition ids are
    /// namespaced <c>&lt;owner&gt;:&lt;ID&gt;</c> (U7), and a number with no published definition carries
    /// <c>PROVISIONAL:&lt;NAME&gt;</c>.</summary>
    private Dictionary<string, object?>? DescribeDefinition(string target)
    {
        if (!Box.HasTable("definitions")) return null;
        var rows = Box.Dicts(
            "SELECT DISTINCT definition_id, version, owner_project, text, url FROM definitions "
            + "WHERE upper(definition_id) = upper(?) ORDER BY version DESC", [target]);
        if (rows.Count == 0) return null;
        var usedBy = Box.HasTable("metrics")
            ? Box.Dicts("SELECT DISTINCT name FROM metrics WHERE upper(definition_id) = upper(?) ORDER BY 1", [target])
            : [];
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["definition_id"] = rows[0]["definition_id"],
            ["owner_project"] = rows[0]["owner_project"],
            ["versions"] = rows.Select(r => r["version"]).ToList(),
            ["text"] = rows[0]["text"],
            ["url"] = rows[0]["url"],
            ["metrics_using_it"] = usedBy.Select(r => r["name"]).ToList(),
            ["provisional"] = PyValues.Upper(PyValues.Str(rows[0]["definition_id"])).StartsWith("PROVISIONAL:", StringComparison.Ordinal),
            ["provenance"] = Provenance(),
        };
    }

    private Dictionary<string, object?> DescribeStudyLayer(string layer)
    {
        var loaded = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var b in Identity.StudyBundles) loaded[PyValues.Str(b.GetValueOrDefault("layer"))] = b;
        var tables = new List<Dictionary<string, object?>>();
        foreach (var (name, doc) in SchemaDocs.StudyTables[layer])
        {
            var info = TablesInCatalog().GetValueOrDefault(name);
            tables.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["table"] = name,
                ["present"] = info is not null,
                ["rows"] = Rows(info),
                ["one_row_is"] = doc.Description,
            });
        }
        var delivery = loaded.GetValueOrDefault(layer);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["study_layer"] = layer,
            ["loaded_into_this_catalog"] = loaded.ContainsKey(layer),
            ["delivery"] = delivery?.GetValueOrDefault("bundle_id"),
            ["layer_version"] = delivery?.GetValueOrDefault("layer_version"),
            ["tables"] = tables,
            ["note"] =
                "A study layer ADDS tables keyed on core identifiers and never alters a core table "
                + "(U5). A table present with 0 rows has been delivered empty, which is a different "
                + "fact from the table not existing -- both mean 'no data', neither means 'no effect'.",
            ["provenance"] = Provenance(),
        };
    }
}
