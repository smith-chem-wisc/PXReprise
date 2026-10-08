#:package DuckDB.NET.Data.Full@1.5.5
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// PTM-annotated proteomes (aging D59): each organism's reviewed UniProt proteome XML, plus the biological PTM sites
// GPTMD found across a question's catalog that UniProt does not annotate. A C# port of aging's
// results/ptm_db_2026-09-27/build_ptm_db.py (agingPTM-v1), same selection, same XML bytes (PXReprise D12: no Python).
//
// Selection (aging, 2026-09-27):
//   - catalog ptm_sites: target, best_q_value <= 0.01, best_ambiguity_level = '1';
//   - biological classes only (Bio below); deamidation, citrullination, Gla, sulfo-Y, ADP-ribosyl excluded;
//   - n_psms >= --min-psms (5), summed over the organism's datasets;
//   - dropped when the accession is not in the proteome (isoforms), the residue or position disagrees, or UniProt
//     already has a 'modified residue' of the same chemistry there. mzLib loads only 'modified residue' and 'lipid
//     moiety-binding region' features, so GG and O-GlcNAc sites UniProt lists as cross-link/glycosylation are kept.
// Sites are written as mzLib's ProteinDbWriter writes GPTMD sites: <feature type="modified residue"
// description="<IdWithMotif>">, inserted before the entry's top-level <sequence length=...> line.
//
//   dotnet run BuildPtmDb.cs -- --catalog C.duckdb --out DIR --tag agingPTM-v2 --xml mouse=P.xml [--xml human=P.xml ...]
//                               [--min-psms 5] [--sites-out DIR] [--exclude-enriched data/ptm-db/enriched_ptm_exclusions_<date>.tsv]
//   dotnet run BuildPtmDb.cs -- --from-sites DIR ...   use DIR/sites_<organism>.tsv instead of the catalog (to prove the
//                               port: v1's site lists must rebuild v1's XMLs byte for byte)
//
// --mode isoforms (aging PXR-A16): the port of aging's isoform_sites.py, for the D43 isoform XMLs
// (aging_isoforms_tier1-2_<organism>.xml). A biological site on an isoform accession (P02751-8) goes on the isoform only
// where MetaMorpheus's parsimony put it in a 1%-FDR target protein group that lists the isoform and NOT its canonical
// (aging's rule, 2026-09-27). A group listing both is ambiguous. Every site the rule assigns is written ("loose"); the
// "strict" column records the guard that the canonical is not also identified in that dataset. PSMs are summed over the
// qualifying datasets, >= --min-psms. A residue that disagrees with the isoform sequence stops the build.
//   dotnet run BuildPtmDb.cs -- --mode isoforms --catalog C.duckdb --out DIR --tag T --xml human=ISO.xml ...
//       writes isoform_site_verdicts.tsv and isoform_sites_selected.tsv to --sites-out, <xml>_<T>.xml, isoform_provenance_<T>.json
//   dotnet run BuildPtmDb.cs -- --mode isoforms --from-sites DIR ...   DIR/isoform_sites_selected.tsv instead of the catalog
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DuckDB.NET.Data;

var opt = Opts(args);
string outDir = Need("out"), tag = Need("tag");
int minPsms = int.Parse(opt.GetValueOrDefault("min-psms")?[0] ?? "5");
string? fromSites = opt.GetValueOrDefault("from-sites")?[0], catalog = opt.GetValueOrDefault("catalog")?[0];
if ((fromSites is null) == (catalog is null)) return Usage("give exactly one of --catalog or --from-sites");
string sitesOut = opt.GetValueOrDefault("sites-out")?[0] ?? outDir;
var xmls = (opt.GetValueOrDefault("xml") ?? new()).Select(s => s.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
if (xmls.Count == 0 || xmls.Keys.Any(k => !Orgs.Values.Contains(k))) return Usage("--xml organism=path, organism one of human, mouse, rat");
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(sitesOut);
string mode = opt.GetValueOrDefault("mode")?[0] ?? "proteome";
// --exclude-enriched FILE (the user, 2026-10-07): a site of a PTM class is not counted from a deposit that ENRICHES that
// class (phospho from a phospho enrichment, GG from a ubiquitin pulldown); applied to regular searches, enriched-for
// sites would flood them with false positives. FILE (declared): dataset<TAB>excluded_classes (comma-separated Bio
// classes)[<TAB>source]. To it the catalog adds MEASURED enrichment: any run whose 1% target PSMs carry phospho at
// >= --enriched-phospho-pct (5; whole proteomes run 0.1-0.3%) or GG at >= --enriched-gg-pct (2) excludes that class for
// the deposit. The list used is written to --sites-out as enriched_exclusions_used.tsv.
string? exclFile = opt.GetValueOrDefault("exclude-enriched")?[0];
double phPct = double.Parse(opt.GetValueOrDefault("enriched-phospho-pct")?[0] ?? "5", System.Globalization.CultureInfo.InvariantCulture);
double ggPct = double.Parse(opt.GetValueOrDefault("enriched-gg-pct")?[0] ?? "2", System.Globalization.CultureInfo.InvariantCulture);
var exclRows = exclFile is null ? new List<Exclusion>() : Exclusions(exclFile, catalog, phPct, ggPct);
var excluded = ModSet(exclRows);
JsonNode? exclProv = exclFile is null ? null : WriteExclusions(exclFile, catalog is not null, phPct, ggPct, sitesOut, exclRows);
if (mode == "isoforms") return RunIsoforms(catalog, fromSites, outDir, sitesOut, tag, minPsms, xmls, excluded, exclProv);
if (mode != "proteome") return Usage("--mode is proteome (the default) or isoforms");

var all = Stopwatch.StartNew();
var (meta, rows) = fromSites is null ? Select(catalog!, minPsms, excluded) : (null, FromSites(fromSites, xmls.Keys));
var prov = new JsonObject
{
    ["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'"),
    ["script"] = "PXReprise tools/ptm-db/BuildPtmDb.cs (C# port of aging results/ptm_db_2026-09-27/build_ptm_db.py)",
    ["tag"] = tag,
    ["catalog"] = meta,
    ["from_sites"] = fromSites,
    ["selection"] = new JsonObject
    {
        ["q_max"] = 0.01, ["ambiguity_level"] = "1", ["target_only"] = true, ["min_psms_summed"] = minPsms,
        ["modifications"] = new JsonArray(Bio.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode)k).ToArray()),
        ["excluded_if_uniprot_modified_residue_same_class"] = true,
        ["uniprot_overlap_ignored_for"] = new JsonArray(NotLoadedByMzLib.Order(StringComparer.Ordinal).Select(k => (JsonNode)k).ToArray()),
        ["enriched_for_excluded"] = exclProv,
    },
    ["note"] = "The sites come from searches of the corpus these databases then search; the PSM floor limits that feedback.",
    ["dotnet"] = Environment.Version.ToString(),
    ["organisms"] = new JsonObject(),
};
foreach (var (org, src) in xmls.OrderBy(kv => kv.Key, StringComparer.Ordinal))
{
    var t0 = Stopwatch.StartNew();
    var (ann, seq) = LoadUniprot(src);
    var keep = new Dictionary<string, List<Site>>(StringComparer.Ordinal);
    var drop = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var kept = new List<Site>();
    foreach (var s in rows.Where(r => r.Organism == org))
    {
        var (cls, res) = Bio[s.Modification];
        string? why = null;
        if (!seq.TryGetValue(s.Accession, out string? sq)) why = "not_in_canonical_xml";
        else if (s.Position < 1 || s.Position > sq.Length) why = "position_out_of_range";
        else if (res is null ? s.Position > 2 : sq[s.Position - 1] != res) why = res is null ? "nterm_acetyl_not_at_1_or_2" : "residue_mismatch";
        else if (!NotLoadedByMzLib.Contains(cls) && ann.TryGetValue((s.Accession, s.Position), out var have) && have.Contains(cls)) why = "already_in_uniprot";
        if (why is not null) { drop[why] = drop.GetValueOrDefault(why) + 1; continue; }
        var k = s with { Residue = sq![s.Position - 1] };
        kept.Add(k);
        (keep.TryGetValue(k.Accession, out var l) ? l : keep[k.Accession] = new()).Add(k);
    }
    // A database that matches almost none of the sites is a reading failure, not a selection (agingPTM-v3's first build
    // read 0 entries from an https-namespace file and wrote 0 sites without complaint).
    int offered = rows.Count(r => r.Organism == org), absent = drop.GetValueOrDefault("not_in_canonical_xml");
    if (seq.Count == 0 || (offered > 0 && absent * 2 > offered))
    {
        Console.Error.WriteLine($"{org}: STOP. {src} gave {seq.Count} entries, and {absent} of {offered} sites' accessions are not in it.");
        return 1;
    }
    string dst = Path.Combine(outDir, Path.GetFileName(src).Replace(".xml", $"_{tag}.xml"));
    int written = Annotate(src, dst, keep);
    string tsv = Path.Combine(sitesOut, $"sites_{org}.tsv");
    var sb = new StringBuilder("accession\tposition\tresidue\tmodification\tsite_type\tn_datasets\tn_psms\tdatasets\r\n");
    foreach (var s in kept.OrderBy(s => s.Accession, StringComparer.Ordinal).ThenBy(s => s.Position).ThenBy(s => s.Modification, StringComparer.Ordinal))
        sb.Append($"{s.Accession}\t{s.Position}\t{s.Residue}\t{s.Modification}\t{s.SiteType}\t{s.NDatasets}\t{s.NPsms}\t{s.Datasets}\r\n");
    File.WriteAllText(tsv, sb.ToString());
    var perMod = kept.GroupBy(s => s.Modification).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
    prov["organisms"]![org] = new JsonObject
    {
        ["source_xml"] = src, ["source_sha256"] = Sha(src), ["output_xml"] = dst, ["output_sha256"] = Sha(dst),
        ["sites_written"] = written, ["proteins_touched"] = kept.Select(s => s.Accession).Distinct().Count(),
        ["sites_by_modification"] = new JsonObject(perMod.Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
        ["dropped"] = new JsonObject(drop.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["sites_tsv"] = tsv, ["wall_s"] = Math.Round(t0.Elapsed.TotalSeconds, 1),
    };
    Console.WriteLine($"{org}: {written} sites written to {dst}; dropped {JsonSerializer.Serialize(drop)}");
}
prov["wall_s"] = Math.Round(all.Elapsed.TotalSeconds, 1);
string provFile = Path.Combine(outDir, $"provenance_{tag}.json");
File.WriteAllText(provFile, prov.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"provenance: {provFile}");
return 0;

// ---------------------------------------------------------------------------------------------------------------

static (JsonObject Meta, List<Site> Rows) Select(string catalog, int minPsms, HashSet<string> excluded)
{
    using var con = new DuckDBConnection($"Data Source={catalog};ACCESS_MODE=READ_ONLY");
    con.Open();
    var meta = new JsonObject();
    using (var c = con.CreateCommand())
    {
        c.CommandText = "select catalog_id, builder_version, built_utc from catalog_meta";
        using var r = c.ExecuteReader();
        r.Read();
        meta["id"] = r.GetValue(0)?.ToString(); meta["datarepo"] = r.GetValue(1)?.ToString(); meta["built_utc"] = r.GetValue(2)?.ToString();
        meta["path"] = catalog;
    }
    string names = string.Join(",", Bio.Keys.Select(n => "'" + n.Replace("'", "''") + "'"));
    var rows = new List<Site>();
    using (var c = con.CreateCommand())
    {
        c.CommandText = $@"
          WITH org AS (SELECT dataset_id, organism_name FROM (
              SELECT dataset_id, organism_name, count(*) n FROM proteins WHERE organism IS NOT NULL GROUP BY 1,2)
            QUALIFY row_number() OVER (PARTITION BY dataset_id ORDER BY n DESC)=1)
          SELECT o.organism_name, s.protein_accession, s.position, s.modification_name, any_value(s.site_type),
                 count(DISTINCT s.dataset_id)::BIGINT, sum(s.n_psms)::BIGINT, string_agg(DISTINCT s.dataset_id, ',' ORDER BY s.dataset_id)
          FROM ptm_sites s JOIN org o USING (dataset_id)
          WHERE s.target_decoy='target' AND s.best_q_value<=0.01 AND s.best_ambiguity_level='1'
            AND s.modification_name IN ({names}){NotExcluded(excluded)}
          GROUP BY 1,2,3,4 HAVING sum(s.n_psms) >= {minPsms}";
        using var r = c.ExecuteReader();
        while (r.Read())
            if (Orgs.TryGetValue(r.GetString(0), out string? o))
                rows.Add(new Site(o, r.GetString(1), Convert.ToInt32(r.GetValue(2)), r.GetString(3), r.GetValue(4)?.ToString() ?? "",
                    Convert.ToInt32(r.GetValue(5)), Convert.ToInt64(r.GetValue(6)), r.GetString(7)));
    }
    return (meta, rows);
}

// ---------------------------------------------------------------------------------------------- --mode isoforms

static int RunIsoforms(string? catalog, string? fromSites, string outDir, string sitesOut, string tag, int minPsms, Dictionary<string, string> xmls,
    HashSet<string> excluded, JsonNode? exclProv)
{
    var all = Stopwatch.StartNew();
    JsonObject? meta = null;
    // (organism, accession, position, residue, modification) -> (PSMs, datasets, strict)
    var selected = new SortedDictionary<(string Org, string Acc, int Pos, string Res, string Mod), (long Psms, SortedSet<string> Datasets, bool Strict)>(
        Comparer<(string, string, int, string, string)>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1, b.Item1); if (c != 0) return c;
            c = string.CompareOrdinal(a.Item2, b.Item2); if (c != 0) return c;
            c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
            c = string.CompareOrdinal(a.Item4, b.Item4); if (c != 0) return c;
            return string.CompareOrdinal(a.Item5, b.Item5);
        }));
    if (catalog is not null)
    {
        var verdicts = IsoformVerdicts(catalog, out meta).Where(v => !excluded.Contains(v.Dataset + "|" + v.Mod)).ToList();
        var tsv = new StringBuilder("organism\tdataset_id\taccession\tcanonical\tposition\tresidue\tmodification\tn_psms\tn_groups\tgroup_has_canonical\tisoform_only_group\tcanonical_in_dataset\tverdict\r\n");
        foreach (var v in verdicts)
            tsv.Append($"{v.Org}\t{v.Dataset}\t{v.Acc}\t{v.Canonical}\t{v.Pos}\t{v.Res}\t{v.Mod}\t{v.Psms}\t{v.Groups}\t{Py(v.HasCanonical)}\t{Py(v.IsoformOnly)}\t{Py(v.CanonicalInDataset)}\t{v.Verdict}\r\n");
        File.WriteAllText(Path.Combine(sitesOut, "isoform_site_verdicts.tsv"), tsv.ToString());
        foreach (var g in verdicts.GroupBy(v => (v.Org, v.Verdict)).OrderBy(g => g.Key.Org, StringComparer.Ordinal).ThenBy(g => g.Key.Verdict, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key.Org} {g.Key.Verdict}: {g.Count()}");
        var loose = Rollup(verdicts, "isoform_by_parsimony", "isoform_group_but_canonical_also_identified");
        var strict = Rollup(verdicts, "isoform_by_parsimony");
        foreach (var (k, (psms, ds)) in loose.Where(kv => kv.Value.Psms >= minPsms))
            selected[k] = (psms, ds, strict.TryGetValue(k, out var s) && s.Psms >= minPsms);
        var sel = new StringBuilder("organism\taccession\tposition\tresidue\tmodification\tn_psms\tdatasets\tstrict\r\n");
        foreach (var (k, v) in selected)
            sel.Append($"{k.Org}\t{k.Acc}\t{k.Pos}\t{k.Res}\t{k.Mod}\t{v.Psms}\t{string.Join(",", v.Datasets)}\t{Py(v.Strict)}\r\n");
        File.WriteAllText(Path.Combine(sitesOut, "isoform_sites_selected.tsv"), sel.ToString());
        Console.WriteLine($"sites >= {minPsms} PSMs: rule only (loose) {selected.Count}; with the canonical-absent guard (strict) {selected.Count(kv => kv.Value.Strict)}");
    }
    else
        foreach (var p in File.ReadLines(Path.Combine(fromSites!, "isoform_sites_selected.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')))
            selected[(p[0], p[1], int.Parse(p[2]), p[3], p[4])] = (long.Parse(p[5]), new SortedSet<string>(p[6].Split(','), StringComparer.Ordinal), p[7] == "True");

    var prov = new JsonObject
    {
        ["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'"),
        ["script"] = "PXReprise tools/ptm-db/BuildPtmDb.cs --mode isoforms (C# port of aging results/ptm_db_2026-09-27/isoform_sites.py)",
        ["tag"] = tag, ["catalog"] = meta, ["from_sites"] = fromSites, ["enriched_for_excluded"] = exclProv?.DeepClone(),
        ["rule"] = "site on an isoform accession, reported in a 1%-FDR target protein group that lists the isoform and not its canonical " +
                   $"(MetaMorpheus parsimony); >= {minPsms} PSMs summed over those datasets",
        ["dotnet"] = Environment.Version.ToString(),
        ["organisms"] = new JsonObject(),
    };
    foreach (var (org, src) in xmls.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        var (_, seq) = LoadUniprot(src);
        var keep = new Dictionary<string, List<Site>>(StringComparer.Ordinal);
        var elsewhere = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (k, v) in selected.Where(kv => kv.Key.Org == org))
        {
            // The searches also carry other isoform databases (tier3_compartment since v1), so a site's accession may belong
            // to another XML: that one's build writes it. A residue that disagrees with this XML's own sequence is fatal.
            if (!seq.TryGetValue(k.Acc, out string? sq)) { elsewhere.Add(k.Acc); continue; }
            if (k.Pos < 1 || k.Pos > sq.Length || sq[k.Pos - 1].ToString() != k.Res)
                throw new InvalidDataException($"{k.Acc} {k.Pos}: residue {k.Res} does not match the isoform sequence in {src}");
            (keep.TryGetValue(k.Acc, out var l) ? l : keep[k.Acc] = new()).Add(new Site(org, k.Acc, k.Pos, k.Mod, "", v.Datasets.Count, v.Psms, string.Join(",", v.Datasets)));
        }
        string dst = Path.Combine(outDir, Path.GetFileName(src).Replace(".xml", $"_{tag}.xml"));
        int written = Annotate(src, dst, keep);
        prov["organisms"]![org] = new JsonObject
        {
            ["source_xml"] = src, ["source_sha256"] = Sha(src), ["output_xml"] = dst, ["output_sha256"] = Sha(dst), ["sites_written"] = written,
            ["sites_by_accession"] = new JsonObject(keep.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.Count))),
            ["selected_accessions_not_in_this_xml"] = new JsonArray(elsewhere.Select(a => (JsonNode?)a).ToArray()),
        };
        Console.WriteLine($"{org}: {written} isoform sites written to {dst}" + (elsewhere.Count > 0 ? $"; {elsewhere.Count} selected accession(s) not in this XML: {string.Join(", ", elsewhere)}" : ""));
    }
    prov["wall_s"] = Math.Round(all.Elapsed.TotalSeconds, 1);
    string provFile = Path.Combine(outDir, $"isoform_provenance_{tag}.json");
    File.WriteAllText(provFile, prov.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"provenance: {provFile}");
    return 0;
}

/// <summary>Declared exclusions (the file) plus measured ones (the catalog's runs), one row per (dataset, class).</summary>
static List<Exclusion> Exclusions(string file, string? catalog, double phPct, double ggPct)
{
    var rows = new SortedDictionary<(string, string), string>();
    foreach (var p in File.ReadLines(file).Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')))
        foreach (string cls in p[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Bio.Values.Any(v => v.Class == cls)) throw new InvalidDataException($"{file}: {p[0]}: '{cls}' is not a class this builder selects");
            rows[(p[0], cls)] = p.Length > 2 && p[2].Length > 0 ? p[2] : "declared";
        }
    if (catalog is not null)
    {
        using var con = new DuckDBConnection($"Data Source={catalog};ACCESS_MODE=READ_ONLY");
        con.Open();
        using var c = con.CreateCommand();
        // The peptidoform spells a mod as the ingesting mzLib serialised it: phospho is UNIMOD:21 throughout; GG was
        // [Info:GG (Ubiquitination Site) on K] before mzLib #1328 and UNIMOD:121 after.
        c.CommandText = @"
          SELECT dataset_id, count(*)::BIGINT,
                 count(*) FILTER (WHERE 100.0 * ph / n >= $ph)::BIGINT, max(100.0 * ph / n)::DOUBLE,
                 count(*) FILTER (WHERE 100.0 * gg / n >= $gg)::BIGINT, max(100.0 * gg / n)::DOUBLE
          FROM (SELECT dataset_id, run_id, count(*) n,
                       count(*) FILTER (WHERE peptidoform LIKE '%UNIMOD:21]%') ph,
                       count(*) FILTER (WHERE peptidoform LIKE '%UNIMOD:121]%' OR peptidoform LIKE '%GG (Ubiquitination Site)%') gg
                FROM psms_1pct WHERE target_decoy = 'target' GROUP BY 1, 2)
          GROUP BY 1";
        c.Parameters.Add(new DuckDBParameter("ph", phPct));
        c.Parameters.Add(new DuckDBParameter("gg", ggPct));
        using var r = c.ExecuteReader();
        while (r.Read())
            foreach (var (cls, hit, max, thr) in new[] { ("phospho", r.GetInt64(2), r.GetDouble(3), phPct), ("GG", r.GetInt64(4), r.GetDouble(5), ggPct) })
                if (hit > 0)
                {
                    string m = $"measured: {hit}/{r.GetInt64(1)} runs with {cls} >= {thr:0.##}% of 1% target PSMs, max {max:0.0}%";
                    var key = (r.GetString(0), cls);
                    rows[key] = rows.TryGetValue(key, out string? d) ? d + "; " + m : m;
                }
    }
    return rows.Select(kv => new Exclusion(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
}

/// <summary>"dataset|modification name" for every Bio modification of each excluded (dataset, class).</summary>
static HashSet<string> ModSet(List<Exclusion> rows) =>
    rows.SelectMany(e => Bio.Where(kv => kv.Value.Class == e.Class).Select(kv => e.Dataset + "|" + kv.Key)).ToHashSet(StringComparer.Ordinal);

static string NotExcluded(HashSet<string> excluded) => excluded.Count == 0 ? "" :
    $"\n            AND (s.dataset_id || '|' || s.modification_name) NOT IN ({string.Join(",", excluded.Order(StringComparer.Ordinal).Select(e => "'" + e.Replace("'", "''") + "'"))})";

static JsonNode WriteExclusions(string file, bool measured, double phPct, double ggPct, string sitesOut, List<Exclusion> rows)
{
    string used = Path.Combine(sitesOut, "enriched_exclusions_used.tsv");
    File.WriteAllText(used, "dataset\texcluded_class\tsource\r\n" + string.Concat(rows.Select(e => $"{e.Dataset}\t{e.Class}\t{e.Source}\r\n")));
    return new JsonObject
    {
        ["rule"] = "a site of a PTM class is not counted from a deposit that enriches that class (user, 2026-10-07)",
        ["declared_file"] = file, ["declared_sha256"] = Sha(file),
        ["measured"] = measured ? new JsonObject { ["phospho_pct_min"] = phPct, ["gg_pct_min"] = ggPct, ["over"] = "1% target PSMs per run (psms_1pct)" } : null,
        ["used_file"] = used, ["used_sha256"] = Sha(used), ["rows"] = rows.Count,
    };
}

/// <summary>One row per (dataset, isoform site): the groups it was reported in, and the verdict of aging's rule.</summary>
static List<IsoVerdict> IsoformVerdicts(string catalog, out JsonObject meta)
{
    using var con = new DuckDBConnection($"Data Source={catalog};ACCESS_MODE=READ_ONLY");
    con.Open();
    meta = new JsonObject();
    using (var c = con.CreateCommand())
    {
        c.CommandText = "select catalog_id, builder_version, built_utc from catalog_meta";
        using var r = c.ExecuteReader();
        r.Read();
        meta["id"] = r.GetValue(0)?.ToString(); meta["datarepo"] = r.GetValue(1)?.ToString(); meta["built_utc"] = r.GetValue(2)?.ToString();
        meta["path"] = catalog;
    }
    string names = string.Join(",", Bio.Keys.Select(n => "'" + n.Replace("'", "''") + "'"));
    var rows = new List<IsoVerdict>();
    using (var c = con.CreateCommand())
    {
        c.CommandText = $@"
          WITH org AS (SELECT dataset_id, organism_name FROM (
              SELECT dataset_id, organism_name, count(*) n FROM proteins WHERE organism IS NOT NULL GROUP BY 1,2)
            QUALIFY row_number() OVER (PARTITION BY dataset_id ORDER BY n DESC)=1),
          site AS (
            SELECT s.*, split_part(s.protein_accession, '-', 1) AS canonical
            FROM ptm_sites s
            WHERE s.target_decoy='target' AND s.best_q_value<=0.01 AND s.best_ambiguity_level='1'
              AND s.modification_name IN ({names}) AND s.protein_accession LIKE '%-%'),
          grp AS (
            SELECT DISTINCT st.ptm_site_id, g.protein_group_id, g.protein_accessions
            FROM ptm_stoichiometry st JOIN protein_groups_1pct g USING (protein_group_id)
            WHERE g.target_decoy='target'),
          canon_elsewhere AS (
            SELECT DISTINCT g.dataset_id, a AS acc
            FROM protein_groups_1pct g, unnest(g.protein_accessions) t(a)
            WHERE g.target_decoy='target')
          SELECT o.organism_name, s.dataset_id, s.protein_accession, s.canonical, s.position, s.residue,
                 s.modification_name, s.n_psms::BIGINT,
                 count(gr.protein_group_id)::BIGINT AS n_groups,
                 bool_or(list_contains(gr.protein_accessions, s.canonical)) AS group_has_canonical,
                 bool_or(NOT list_contains(gr.protein_accessions, s.canonical)) AS isoform_only_group,
                 any_value(ce.acc) IS NOT NULL AS canonical_in_dataset
          FROM site s JOIN org o USING (dataset_id)
          LEFT JOIN grp gr ON gr.ptm_site_id = s.ptm_site_id
          LEFT JOIN canon_elsewhere ce ON ce.dataset_id = s.dataset_id AND ce.acc = s.canonical
          GROUP BY ALL";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            // isoform_sites.py stopped on an organism outside the three; the isoform XMLs exist only for them.
            if (!Orgs.TryGetValue(r.GetString(0), out string? org)) continue;
            bool? B(int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
            long groups = Convert.ToInt64(r.GetValue(8));
            bool? hasCanon = B(9), isoOnly = B(10);
            bool canonIn = r.GetBoolean(11);
            string verdict = groups == 0 ? "no_1pct_group"
                : hasCanon == true ? "ambiguous_group"
                : canonIn ? "isoform_group_but_canonical_also_identified"
                : "isoform_by_parsimony";
            rows.Add(new IsoVerdict(org, r.GetString(1), r.GetString(2), r.GetString(3), Convert.ToInt32(r.GetValue(4)), r.GetValue(5)?.ToString() ?? "",
                r.GetString(6), Convert.ToInt64(r.GetValue(7)), groups, hasCanon, isoOnly, canonIn, verdict));
        }
    }
    // isoform_sites.py wrote DuckDB's row order, which no query fixes; this order is stable.
    return rows.OrderBy(v => v.Org, StringComparer.Ordinal).ThenBy(v => v.Dataset, StringComparer.Ordinal).ThenBy(v => v.Acc, StringComparer.Ordinal)
        .ThenBy(v => v.Pos).ThenBy(v => v.Mod, StringComparer.Ordinal).ToList();
}

static Dictionary<(string Org, string Acc, int Pos, string Res, string Mod), (long Psms, SortedSet<string> Datasets)> Rollup(List<IsoVerdict> rows, params string[] ok)
{
    var agg = new Dictionary<(string, string, int, string, string), (long Psms, SortedSet<string> Datasets)>();
    foreach (var r in rows.Where(r => ok.Contains(r.Verdict)))
    {
        var k = (r.Org, r.Acc, r.Pos, r.Res, r.Mod);
        var (p, d) = agg.TryGetValue(k, out var v) ? v : (0L, new SortedSet<string>(StringComparer.Ordinal));
        d.Add(r.Dataset);
        agg[k] = (p + r.Psms, d);
    }
    return agg;
}

/// <summary>Python's spelling of a nullable bool in a csv cell, so the tables read as v1's did.</summary>
static string Py(bool? b) => b switch { true => "True", false => "False", null => "" };

static List<Site> FromSites(string dir, IEnumerable<string> orgs) =>
    orgs.SelectMany(o => File.ReadLines(Path.Combine(dir, $"sites_{o}.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t'))
        .Select(p => new Site(o, p[0], int.Parse(p[1]), p[3], p[4], int.Parse(p[5]), long.Parse(p[6]), p[7]))).ToList();

/// <summary>(accession, position) -> chemistry classes of UniProt 'modified residue' features; accession -> sequence.</summary>
static (Dictionary<(string, int), HashSet<string>> Ann, Dictionary<string, string> Seq) LoadUniprot(string xml)
{
    var ann = new Dictionary<(string, int), HashSet<string>>();
    var seq = new Dictionary<string, string>(StringComparer.Ordinal);
    using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
    reader.MoveToContent();
    // The document's own namespace: UniProt's REST files declare http://uniprot.org/uniprot, its FTP reference proteomes
    // https://uniprot.org/uniprot (2026_03). Hard-coding one read every FTP entry as absent (agingPTM-v3, 2026-10-08).
    XNamespace ns = reader.NamespaceURI;
    if (ns.NamespaceName is not ("http://uniprot.org/uniprot" or "https://uniprot.org/uniprot"))
        throw new InvalidDataException($"{xml}: root namespace '{ns.NamespaceName}' is not UniProt's");
    while (!reader.EOF)
    {
        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry" || reader.NamespaceURI != ns.NamespaceName) { reader.Read(); continue; }
        var e = (XElement)XNode.ReadFrom(reader);
        string acc = e.Element(ns + "accession")!.Value;
        seq[acc] = string.Concat(e.Element(ns + "sequence")!.Value.Where(c => !char.IsWhiteSpace(c)));
        foreach (var f in e.Elements(ns + "feature").Where(f => (string?)f.Attribute("type") == "modified residue"))
        {
            string d = ((string?)f.Attribute("description") ?? "").ToLowerInvariant();
            string cls = Rules.FirstOrDefault(r => d.Contains(r.Key)).Value ?? "other";
            if (f.Element(ns + "location")?.Element(ns + "position")?.Attribute("position")?.Value is { Length: > 0 } p)
            {
                var key = (acc, int.Parse(p));
                (ann.TryGetValue(key, out var set) ? set : ann[key] = new()).Add(cls);
            }
        }
    }
    return (ann, seq);
}

/// <summary>Streams the XML line by line (LF out, as v1 wrote), inserting each accession's sites before the entry's
/// top-level &lt;sequence length=...&gt;. Every accession must reach an insertion point, or nothing is written.</summary>
static int Annotate(string src, string dst, Dictionary<string, List<Site>> keep)
{
    var left = keep.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    string tmp = dst + ".partial";
    int written = 0;
    string? acc = null;
    bool inEntry = false;
    using (var fi = new StreamReader(src, new UTF8Encoding(false)))
    using (var fo = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
    {
        bool endsWithNewline = EndsWithNewline(src);
        string? line = fi.ReadLine();
        while (line is not null)
        {
            string next = fi.ReadLine()!;
            string st = line.TrimStart();
            if (st.StartsWith("<entry ", StringComparison.Ordinal)) { inEntry = true; acc = null; }
            else if (inEntry && acc is null && st.StartsWith("<accession>", StringComparison.Ordinal))
                acc = st["<accession>".Length..st.IndexOf("</accession>", StringComparison.Ordinal)];
            else if (inEntry && st.StartsWith("<sequence length=", StringComparison.Ordinal) && acc is not null && left.TryGetValue(acc, out var sites))
            {
                string ind = line[..(line.Length - st.Length)];
                foreach (var s in sites.OrderBy(s => s.Position).ThenBy(s => s.Modification, StringComparer.Ordinal))
                {
                    fo.Write($"{ind}<feature type=\"modified residue\" description=\"{s.Modification}\">\n{ind}  <location>\n" +
                             $"{ind}    <position position=\"{s.Position}\"/>\n{ind}  </location>\n{ind}</feature>\n");
                    written++;
                }
                left.Remove(acc);
            }
            else if (st.StartsWith("</entry>", StringComparison.Ordinal)) inEntry = false;
            fo.Write(line);
            if (next is not null || endsWithNewline) fo.Write('\n');
            line = next;
        }
    }
    if (left.Count > 0)
    {
        File.Delete(tmp);
        throw new InvalidDataException($"{src}: {left.Count} accessions never reached an insertion point");
    }
    File.Move(tmp, dst, overwrite: true);
    return written;
}

static bool EndsWithNewline(string path)
{
    using var fs = File.OpenRead(path);
    if (fs.Length == 0) return false;
    fs.Seek(-1, SeekOrigin.End);
    return fs.ReadByte() == '\n';
}

static string Sha(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(s));
}

Dictionary<string, List<string>> Opts(string[] a)
{
    var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (int i = 0; i + 1 < a.Length; i += 2)
        if (a[i].StartsWith("--", StringComparison.Ordinal)) (d.TryGetValue(a[i][2..], out var l) ? l : d[a[i][2..]] = new()).Add(a[i + 1]);
    return d;
}

string Need(string k) => opt.GetValueOrDefault(k)?[0] ?? throw new ArgumentException($"--{k} is required");

static int Usage(string why) { Console.Error.WriteLine($"usage error: {why} (see the header of BuildPtmDb.cs)"); return 2; }

record Site(string Organism, string Accession, int Position, string Modification, string SiteType, int NDatasets, long NPsms, string Datasets)
{
    public char Residue { get; init; }
}

record Exclusion(string Dataset, string Class, string Source);

record IsoVerdict(string Org, string Dataset, string Acc, string Canonical, int Pos, string Res, string Mod, long Psms, long Groups,
    bool? HasCanonical, bool? IsoformOnly, bool CanonicalInDataset, string Verdict);

static partial class Program
{
    static readonly Dictionary<string, string> Orgs = new() { ["Homo sapiens"] = "human", ["Mus musculus"] = "mouse", ["Rattus norvegicus"] = "rat" };
    static readonly HashSet<string> NotLoadedByMzLib = new(StringComparer.Ordinal) { "GG", "oglcnac" };

    /// <summary>GPTMD IdWithMotif -> (chemistry class, residue; null = protein N-terminus).</summary>
    static readonly Dictionary<string, (string Class, char? Residue)> Bio = new(StringComparer.Ordinal)
    {
        ["Phosphorylation on S"] = ("phospho", 'S'), ["Phosphorylation on T"] = ("phospho", 'T'), ["Phosphorylation on Y"] = ("phospho", 'Y'),
        ["Acetylation on K"] = ("acetylK", 'K'), ["Acetylation on X"] = ("acetylNterm", null),
        ["Methylation on K"] = ("me1K", 'K'), ["Dimethylation on K"] = ("me2K", 'K'), ["Trimethylation on K"] = ("me3K", 'K'),
        ["Methylation on R"] = ("me1R", 'R'), ["Dimethylation on R"] = ("me2R", 'R'),
        ["Hydroxylation on P"] = ("hydroxyP", 'P'), ["Hydroxylation on K"] = ("hydroxyK", 'K'),
        ["Succinylation on K"] = ("succinyl", 'K'), ["Glutarylation on K"] = ("glutaryl", 'K'), ["Malonylation on K"] = ("malonyl", 'K'),
        ["Crotonylation on K"] = ("crotonyl", 'K'), ["Butyrylation on K"] = ("butyryl", 'K'), ["Hydroxybutyrylation on K"] = ("hydroxybutyryl", 'K'),
        ["HexNAc on S"] = ("oglcnac", 'S'), ["HexNAc on T"] = ("oglcnac", 'T'),
        ["GG (Ubiquitination Site) on K"] = ("GG", 'K'),
    };

    /// <summary>UniProt 'modified residue' description (lower case) -> chemistry class; the first match wins, in this order.</summary>
    static readonly KeyValuePair<string, string>[] Rules =
    {
        new("phospho", "phospho"), new("n6-acetyllysine", "acetylK"), new("n-acetyl", "acetylNterm"),
        new("n6,n6,n6-trimethyl", "me3K"), new("n6,n6-dimethyl", "me2K"), new("n6-methyllysine", "me1K"),
        new("dimethylarginine", "me2R"), new("omega-n-methylarginine", "me1R"),
        new("hydroxyproline", "hydroxyP"), new("hydroxylysine", "hydroxyK"),
        new("succinyl", "succinyl"), new("glutaryl", "glutaryl"), new("malonyl", "malonyl"), new("crotonyl", "crotonyl"),
        new("hydroxyisobutyryl", "hydroxybutyryl"), new("hydroxybutyryl", "hydroxybutyryl"), new("butyryl", "butyryl"),
    };
}
