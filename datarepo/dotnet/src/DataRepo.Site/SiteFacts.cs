using System.Collections;
using System.Numerics;
using System.Text.Json;
using DataRepo.Bundle;
using DuckDB.NET.Data;
using Dict = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace DataRepo.Site;

/// <summary>Everything the site says, read from one catalog in one pass (<c>read_site_facts</c>).</summary>
/// <remarks>Values are Python's plain types as <c>PORTING.md</c> lays them down: a dataset, the catalog's meta
/// row, a figure of merit and a protein entry are each an insertion-ordered dictionary, because the JSON files
/// are those dictionaries written out in that order.</remarks>
public sealed class SiteFacts
{
    /// <summary>The catalog's <c>catalog_meta</c> row.</summary>
    public required Dict Meta { get; init; }

    /// <summary>One dictionary per dataset, ordered by dataset id.</summary>
    public required List<Dict> Datasets { get; init; }

    /// <summary>The front page's figures of merit: <c>label</c>, <c>value</c>, <c>note</c>.</summary>
    public required List<Dict> Merit { get; init; }

    /// <summary>Accession -> <c>{gene, organism, datasets}</c>, in accession order.</summary>
    public required Dict Proteins { get; init; }

    /// <summary>The question the instance serves (<c>--purpose</c>), already trimmed; null for none.</summary>
    public string? Purpose { get; set; }
}

public static partial class SiteGenerator
{
    // --- reading the catalog -------------------------------------------------------------------

    private static List<Dict> Rows(DuckDBConnection con, string sql, params object?[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.Add(new DuckDBParameter(p ?? DBNull.Value));
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dict>();
        while (reader.Read())
        {
            var row = new Dict();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = Plain(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    private static object?[] One(DuckDBConnection con, string sql) => Rows(con, sql)[0].Values.ToArray();

    /// <summary>A DuckDB value as the Python client returns it: a HUGEINT sum is a Python int, a LIST a list.</summary>
    private static object? Plain(object? value) => value switch
    {
        null or DBNull => null,
        string => value,
        BigInteger b => b >= long.MinValue && b <= long.MaxValue ? (long)b : b,
        int i => (long)i,
        short s => (long)s,
        sbyte s => (long)s,
        byte b => (long)b,
        ushort u => (long)u,
        uint u => (long)u,
        float f => (double)f,
        IDictionary => value,
        IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        _ => value,
    };

    /// <summary>A JSON document as Python's <c>json.loads</c> returns it.</summary>
    internal static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Aggregate(new Dict(), (d, p) => { d[p.Name] = FromJson(p.Value); return d; }),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l
            : IsIntegerText(e.GetRawText()) ? BigInteger.Parse(e.GetRawText()) : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static bool IsIntegerText(string raw) => raw.All(c => char.IsAsciiDigit(c) || c == '-');

    private static object? ReadJsonFile(string path)
    {
        // Python's json.loads raises ValueError on a bad document; the callers treat it like an unreadable file.
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return FromJson(doc.RootElement);
    }

    private static object? Get(object? dict, string key) =>
        dict is Dict d && d.TryGetValue(key, out var v) ? v : null;

    /// <summary>The licence and credit line a bundle was written under, or (null, null) if unreadable.</summary>
    /// <remarks>The catalog does not carry them, the bundle does. Read from the bundle rather than restated
    /// from D3, because the bundle is what a user downloads and the licence it states is the one that binds;
    /// a site that restated a decision could disagree with the files it links to.</remarks>
    internal static (object? Licence, object? Credit) BundleLicence(string? path)
    {
        if (string.IsNullOrEmpty(path)) return (null, null);
        object? doc;
        try
        {
            doc = ReadJsonFile(Path.Combine(path, "bundle.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
        return (Get(doc, "licence"), Get(doc, "credit"));
    }

    /// <summary>The PUBLIC repo and commit for a private pipeline commit, if the producer records one.</summary>
    /// <remarks>aging 069 55e: their pipeline repo is private and their public copy is a subtree split with
    /// different hashes, so neither pair can be derived from the other. They record the public pair in the
    /// provenance's <c>pipeline</c> block (<c>public_repo</c>, <c>public_commit</c>), and the bundle keeps a
    /// verbatim copy of every provenance file. The block is found by its <c>commit</c>, not by the file's name,
    /// because the stage names are the producer's.</remarks>
    internal static (object? Repo, object? Commit) BundlePublicPipeline(string? path, object? commit)
    {
        if (string.IsNullOrEmpty(path) || !PyText.Truthy(commit)) return (null, null);
        object? doc;
        try
        {
            doc = ReadJsonFile(Path.Combine(path, "bundle.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
        var sources = Get(doc, "sources");
        if (!PyText.Truthy(sources) || sources is not IList list) return (null, null);
        foreach (var source in list)
        {
            var role = Get(source, "role");
            var bundlePath = Get(source, "bundle_path");
            if (!PyText.Str(role ?? "").StartsWith("provenance:", StringComparison.Ordinal) || !PyText.Truthy(bundlePath)) continue;
            object? provenance;
            try
            {
                provenance = ReadJsonFile(Path.Combine(path, PyText.Str(bundlePath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            var pipeline = Get(provenance, "pipeline");
            if (Equals(Get(pipeline, "commit"), commit) && PyText.Truthy(Get(pipeline, "public_commit")))
                return (Get(pipeline, "public_repo"), Get(pipeline, "public_commit"));
        }
        return (null, null);
    }

    /// <summary>Everything the site says, read from one catalog in one pass.</summary>
    /// <exception cref="CatalogException">There is no catalog at <paramref name="catalog"/>.</exception>
    public static SiteFacts ReadSiteFacts(string catalog)
    {
        if (!File.Exists(catalog)) throw new CatalogException($"no catalog at {catalog}");
        Dict meta;
        List<Dict> overview, findings, names, modifications, metrics, merit;
        Dictionary<string, Dict> extra, bundles;
        Dict proteins;
        using (var con = new DuckDBConnection($"Data Source={catalog};ACCESS_MODE=READ_ONLY"))
        {
            con.Open();
            try
            {
                meta = Rows(con, "SELECT * FROM catalog_meta")[0];
            }
            catch (Exception ex) when (ex is DuckDBException or ArgumentOutOfRangeException)
            {
                throw new CatalogException($"{catalog} is not a datarepo catalog: {ex.Message}");
            }
            if (!Columns(con, "protein_datasets").Contains("is_contaminant"))
            {
                // Catalog format 7 made the contaminant label per dataset. Counting proteins from an older
                // catalog would fall back on the corpus-wide flag that dropped albumin (57f).
                throw new CatalogException(
                    $"{catalog} is catalog format {PyText.Str(Get(meta, "catalog_version"))}, built before the " +
                    "contaminant label was per dataset (format 7); rebuild it with this datarepo");
            }
            overview = Rows(con, "SELECT * FROM dataset_overview ORDER BY dataset_id");
            extra = ByDataset(Rows(con,
                "SELECT dataset_id, instruments, search_database, sdrf_status, pipeline_repo, " +
                "pipeline_commit FROM datasets"));
            bundles = ByDataset(Rows(con, "SELECT * FROM catalog_bundles"));
            findings = Rows(con,
                "SELECT dataset_id, code, severity, message FROM findings WHERE status = 'open' " +
                "ORDER BY dataset_id, CASE severity WHEN 'error' THEN 0 WHEN 'warning' THEN 1 " +
                "ELSE 2 END, code");
            // The searched proteome's own species name, taken from the entries that carry its taxon.
            // A contaminant entry has no taxon (schema 0.0.7), so it cannot lend its species here.
            names = Rows(con,
                "SELECT dataset_id, organism, mode(organism_name) AS organism_name FROM proteins " +
                "WHERE organism IS NOT NULL AND organism_name IS NOT NULL GROUP BY ALL");
            modifications = Rows(con,
                "SELECT dataset_id, modification_name, any_value(modification) AS modification, " +
                "count(*) AS n_sites FROM ptm_sites WHERE target_decoy = 'target' " +
                "GROUP BY dataset_id, modification_name " +
                "ORDER BY dataset_id, n_sites DESC, modification_name");
            metrics = Rows(con,
                "SELECT dataset_id, name, value, definition_id FROM metrics WHERE scope = 'dataset' " +
                "AND name IN ('id_rate', 'contamination_psm_share')");
            merit = FiguresOfMerit(con);
            proteins = ProteinEvidence(con);
        }

        var organismNames = new Dictionary<(object?, object?), object?>();
        foreach (var r in names) organismNames[(r["dataset_id"], r["organism"])] = r["organism_name"];
        var datasets = new List<Dict>();
        foreach (var row in overview)
        {
            var ds = (string)row["dataset_id"]!;
            var bundle = bundles.GetValueOrDefault(ds) ?? new Dict();
            var bundlePath = Get(bundle, "path") as string;
            var (licence, credit) = BundleLicence(bundlePath);
            var (publicRepo, publicCommit) = BundlePublicPipeline(bundlePath, Get(extra.GetValueOrDefault(ds), "pipeline_commit"));
            var d = new Dict(row);
            if (extra.TryGetValue(ds, out var more))
                foreach (var (k, v) in more)
                    if (k != "dataset_id") d[k] = v;
            var organisms = row["organisms"] as List<object?> ?? [];
            d["organism_names"] = organisms
                .Select(taxon => organismNames.GetValueOrDefault((ds, taxon)) is { } n && PyText.Truthy(n) ? n : taxon)
                .ToList();
            d["bundle"] = new Dict
            {
                ["bundle_id"] = Get(bundle, "bundle_id"),
                ["written_utc"] = Get(bundle, "written_utc"),
                ["ingester_version"] = Get(bundle, "ingester_version"),
                ["reconciliation_ok"] = Get(bundle, "reconciliation_ok"),
                ["reconciliation_failed"] = (Get(bundle, "reconciliation_failed") as List<object?> ?? []).ToList(),
                ["path"] = Get(bundle, "path"),
            };
            d["licence"] = licence;
            d["credit"] = credit;
            d["pipeline_public_repo"] = publicRepo;
            d["pipeline_public_commit"] = publicCommit;
            d["findings"] = findings.Where(f => Equals(f["dataset_id"], ds))
                .Select(f => (object?)new Dict { ["code"] = f["code"], ["severity"] = f["severity"], ["message"] = f["message"] })
                .ToList();
            // All of them: a list cut at ten read "zero" for the eleventh (PXD026608's deamidation,
            // aging 069 55c).
            d["modifications"] = modifications.Where(m => Equals(m["dataset_id"], ds))
                .Select(m => (object?)new Dict
                {
                    ["modification_name"] = m["modification_name"], ["modification"] = m["modification"], ["n_sites"] = m["n_sites"],
                })
                .ToList();
            var dsMetrics = new Dict();
            foreach (var m in metrics.Where(m => Equals(m["dataset_id"], ds)))
                dsMetrics[PyText.Str(m["name"])] = new Dict { ["value"] = m["value"], ["definition_id"] = m["definition_id"] };
            d["metrics"] = dsMetrics;
            datasets.Add(d);
        }
        foreach (var ds in datasets)
        {
            var severities = Findings(ds).Select(f => f["severity"]).ToList();
            ds["findings_by_severity"] = new Dict
            {
                ["error"] = (long)severities.Count(s => Equals(s, "error")),
                ["warning"] = (long)severities.Count(s => Equals(s, "warning")),
                ["info"] = (long)severities.Count(s => Equals(s, "info")),
            };
            ds["finding_codes"] = Findings(ds).Select(f => PyText.Str(f["code"])).Distinct()
                .OrderBy(c => c, StringComparer.Ordinal).Cast<object?>().ToList();
        }
        return new SiteFacts { Meta = meta, Datasets = datasets, Merit = merit, Proteins = proteins };
    }

    private static Dictionary<string, Dict> ByDataset(List<Dict> rows)
    {
        var out_ = new Dictionary<string, Dict>();
        foreach (var r in rows) out_[(string)r["dataset_id"]!] = r;
        return out_;
    }

    internal static IEnumerable<Dict> Findings(Dict ds) => ((List<object?>)ds["findings"]!).Cast<Dict>();

    private static HashSet<string> Columns(DuckDBConnection con, string table) =>
        Rows(con, "SELECT column_name FROM information_schema.columns WHERE table_name = ?", table)
            .Select(r => (string)r["column_name"]!).ToHashSet();

    /// <summary>Every accession with accepted evidence somewhere, and where (aging 070, DATAREPO-56).</summary>
    /// <remarks>The site could answer no protein-level question at all -- is albumin identified, which
    /// datasets have tau -- because nothing below the dataset reached it. This is the smallest thing that
    /// does: per accession, the datasets where a protein group or peptidoform passed, with the contaminant
    /// label AS THAT DATASET HAS IT. Decoys are left out; a contaminant is kept and labelled.</remarks>
    private static Dict ProteinEvidence(DuckDBConnection con)
    {
        var rows = Rows(con,
            "SELECT d.protein_accession, i.gene, i.organism, d.dataset_id, d.n_protein_groups, " +
            "d.n_peptidoforms, d.best_q_value, d.is_contaminant " +
            "FROM protein_datasets d JOIN protein_index i USING (protein_accession) " +
            "WHERE (d.n_protein_groups > 0 OR d.n_peptidoforms > 0) " +
            "AND d.protein_accession NOT LIKE 'DECOY%' ORDER BY 1, 4");
        var out_ = new Dict();
        foreach (var r in rows)
        {
            var accession = (string)r["protein_accession"]!;
            if (!out_.TryGetValue(accession, out var entry))
            {
                entry = new Dict { ["gene"] = r["gene"], ["organism"] = r["organism"], ["datasets"] = new List<object?>() };
                out_[accession] = entry;
            }
            ((List<object?>)((Dict)entry!)["datasets"]!).Add(new Dict
            {
                ["dataset_id"] = r["dataset_id"],
                ["n_protein_groups"] = r["n_protein_groups"],
                ["n_peptidoforms"] = r["n_peptidoforms"],
                ["best_protein_group_q_value"] = r["best_q_value"],
                ["is_contaminant"] = r["is_contaminant"],
            });
        }
        return out_;
    }

    /// <summary>The front page's headline numbers, each with the rule that produced it.</summary>
    /// <remarks>Every figure is a sum or a count over catalog tables the dataset pages also show, and each
    /// carries a note saying what it counts, because a bare "9,236 proteins" invites the reader to supply
    /// their own definition -- decoys or not, contaminants or not, 1% FDR or anything matched. A figure some
    /// datasets cannot supply says how many did, rather than presenting a partial sum as a total.</remarks>
    private static List<Dict> FiguresOfMerit(DuckDBConnection con)
    {
        var o = One(con, "SELECT count(*), sum(n_runs), sum(n_psms_1pct), sum(n_ptm_sites) FROM dataset_overview");
        var (nDatasets, runs, psms, sites) = (o[0], o[1], o[2], o[3]);
        o = One(con,
            "SELECT sum(value), count(DISTINCT dataset_id) FROM metrics " +
            "WHERE scope = 'dataset' AND name = 'ms2'");
        var (ms2, ms2Datasets) = (o[0], o[1]);
        // Per dataset, because the contaminant label is: human albumin is a target in a human search and a
        // contaminant in a rodent one, and a corpus-wide flag dropped it from every count (aging 070 57f). An
        // accession counts in a dataset where it was accepted and was not a contaminant.
        var spread = Rows(con,
            "SELECT n, count(*) FROM (" +
            "  SELECT protein_accession, count(DISTINCT dataset_id) AS n FROM protein_datasets " +
            "  WHERE (n_protein_groups > 0 OR n_peptidoforms > 0) " +
            "    AND NOT coalesce(is_contaminant, false) AND protein_accession NOT LIKE 'DECOY%' " +
            "  GROUP BY 1) GROUP BY 1")
            .Select(r => r.Values.ToArray()).Select(a => ((long)a[0]!, (long)a[1]!)).ToList();
        var proteins = spread.Sum(s => s.Item2);
        var byN = new Dictionary<long, long>();
        foreach (var (n, c) in spread) byN[n] = c;
        var (sharedIn, shared) = SharedThreshold(byN);
        // A peptide sequence at 1% FDR counts as unique when parsimony gave it one protein in EVERY dataset
        // where it passed; `target` excludes decoys and contaminants. Parsimony-unique, not sequence-unique:
        // `is_unique` comes from the producer's parsimony list (G76).
        o = One(con,
            "SELECT count(*), count(*) FILTER (WHERE u) FROM (" +
            "  SELECT base_sequence, bool_and(coalesce(is_unique, false)) AS u " +
            "  FROM peptidoforms_1pct GROUP BY 1)");
        var (peptides, uniquePeptides) = (o[0], o[1]);
        // Enriched datasets (pull-downs, probes, IPs) say nothing about a proteome, so the count a reader
        // asking "how much of the proteome is here" needs is the one over whole-proteome datasets only (aging
        // 070 57d). A mixed deposit is left out: its declaration is true of some runs only.
        var whole = One(con,
            "SELECT count(DISTINCT p.protein_accession) FROM protein_datasets p " +
            "JOIN datasets d USING (dataset_id) " +
            "WHERE d.enrichment = ['none'] AND NOT coalesce(d.enrichment_mixed, false) " +
            "AND (p.n_protein_groups > 0 OR p.n_peptidoforms > 0) " +
            "AND NOT coalesce(p.is_contaminant, false) AND p.protein_accession NOT LIKE 'DECOY%'")[0];
        var nWhole = One(con,
            "SELECT count(*) FROM datasets WHERE enrichment = ['none'] AND NOT coalesce(enrichment_mixed, false)")[0];
        var kinds = One(con,
            "SELECT count(DISTINCT modification_name) FROM ptm_sites WHERE target_decoy = 'target'")[0];
        // By UNIMOD accession, not name: "Phosphoserine on S" (UniProt) and "Phosphorylation on S" (the
        // search) are one chemistry, and a name match would also catch glycerylphosphoryl-ethanolamine. The
        // chemistry view counts a residue reached under both names once.
        o = One(con,
            "SELECT count(*) FILTER (WHERE modification = 'UNIMOD:21'), " +
            "count(*) FILTER (WHERE modification = 'UNIMOD:1') " +
            "FROM ptm_sites_by_chemistry WHERE target_decoy = 'target'");
        var (phospho, acetyl) = (o[0], o[1]);
        var ms2Note = $"MS2 scans in the raw files, summed ({Ms2CountDefinitionId})";
        if (!Equals(ms2Datasets, nDatasets))
            ms2Note += $"; {PyText.Str(ms2Datasets)} of {PyText.Str(nDatasets)} datasets report it";
        // `ms2 and int(ms2)`: NULL stays None, 0.0 stays the float 0.0, anything else is truncated to an int.
        object? spectra = ms2 is double m && m != 0 ? (object)new BigInteger(Math.Truncate(m)) : ms2;
        if (spectra is BigInteger sb && sb >= long.MinValue && sb <= long.MaxValue) spectra = (long)sb;
        return
        [
            Merit("Datasets", nDatasets, "public PRIDE datasets, each reanalysed from its raw files"),
            Merit("Raw files searched", runs, "LC-MS/MS runs"),
            Merit("Spectra searched", spectra, ms2Note),
            Merit("PSMs at 1% FDR", psms, "target peptide-spectrum matches, as the search engine counts them"),
            // G76 (not yet built in C#): the definition of this tile changes with the C# release.
            Merit("Unique peptides at 1% FDR", uniquePeptides,
                "peptide sequences assigned to a single protein by parsimony in every dataset " +
                $"that found them, decoys and contaminants excluded; out of {PyText.Thousands(Or0(peptides))} " +
                "sequences in all"),
            Merit("Proteins identified", proteins,
                "accessions with 1%-FDR evidence, decoys and contaminants excluded"
                + (sharedIn is not null ? $"; {PyText.Thousands(shared)} of them in {sharedIn} or more datasets" : "")),
            Merit("Proteins, whole-proteome datasets", whole,
                $"the same count over the {PyText.Thousands(nWhole!)} datasets with no enrichment step; an " +
                "enriched dataset (pull-down, probe, IP) says nothing about the proteome"),
            Merit("PTM sites", sites,
                $"modified residues on proteins, {PyText.Thousands(Or0(kinds))} kinds of modification; on " +
                $"target proteins, {PyText.Thousands(Or0(phospho))} phosphorylation (UNIMOD:21) and " +
                $"{PyText.Thousands(Or0(acetyl))} acetylation (UNIMOD:1) sites"),
        ];
    }

    /// <summary>Python's <c>x or 0</c>.</summary>
    private static object Or0(object? value) => PyText.Truthy(value) ? value! : 0L;

    private static Dict Merit(string label, object? value, string note) =>
        new() { ["label"] = label, ["value"] = value, ["note"] = note };

    /// <summary>(N, count) for the N &gt;= 2 whose "in N or more datasets" count is nearest the target.</summary>
    /// <remarks>A tie goes to the larger N, the stricter statement. (null, 0) when no protein is in two
    /// datasets, since "in 1 or more" is every protein and says nothing.</remarks>
    public static (long? N, long Count) SharedThreshold(IReadOnlyDictionary<long, long> byN)
    {
        (long? N, long Count) best = (null, 0);
        long atLeast = 0;
        foreach (var n in byN.Keys.OrderByDescending(n => n))
        {
            atLeast += byN[n];
            if (n < 2) break;
            if (best.N is null || Math.Abs(atLeast - SharedProteinsTarget) < Math.Abs(best.Count - SharedProteinsTarget))
                best = (n, atLeast);
        }
        return best;
    }
}
