#:package DuckDB.NET.Data.Full@1.5.5
// The evidence a cumulative PTM database is built from (PXReprise D91; the maintainer, 2026-11-24: "yes build it the
// cumulative way. and note that this should be the policy moving forward").
//
// A question's catalog serves only the deposits the question includes today. A deposit it excludes later (out of scope)
// leaves the catalog, and before D91 its sites left the next agingPTM-vN with it: v9 lost 3,250 of v8's sites, 3,236 of
// them that way. Its bundle stays in the store, so its searches still vouch for their sites.
//
// This writes a new DuckDB file holding:
//   - every view of the live catalog. A view over the store's bundles (the catalog's own form:
//     SELECT '<dataset>' AS dataset_id, '<bundle>' AS bundle_id, ... FROM read_parquet('<store>/<dataset>/<bundle>/<t>.parquet'))
//     also gets, in the same form, the newest bundle of every deposit in the store that the catalog does not serve;
//   - psms_1pct over the same bundles, at the catalog's 1% rule (q-value and, where set, notch q-value at most 0.01), so
//     BuildPtmDb measures enrichment on the added deposits too;
//   - catalog_meta, the live row with catalog_id '<live id>+kept<n>', and evidence_bundles, the bundles added.
// Not added: a deposit split into served parts (PXD027526 is served as PXD027526-human and -mouse), and a deposit on
// --drop (dataset<TAB>reason<TAB>source), which leaves for a stated quality problem. BuildPtmDb then reads this file
// as its --catalog, unchanged.
//
//   dotnet run EvidenceCatalog.cs -- --catalog F:/aging_data/repo/catalogs/catalog-<id>.duckdb --out E.duckdb
//                                    --drop data/ptm-db/evidence_dropped.tsv [--manifest F:/aging_data/batch/manifest.yaml]
//                                    [--store DIR (default: the store beside the catalogs folder)]
// Writes E.duckdb (never over an existing file) and evidence_bundles.tsv beside it.
using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

var opt = new Dictionary<string, string>();
for (int i = 0; i + 1 < args.Length; i += 2)
    opt[args[i].TrimStart('-')] = args[i + 1];
if (!opt.TryGetValue("catalog", out var catalog) || !opt.TryGetValue("out", out var outDb) || !opt.TryGetValue("drop", out var dropFile))
{
    Console.Error.WriteLine("usage: EvidenceCatalog.cs --catalog C.duckdb --out E.duckdb --drop FILE [--manifest M] [--store DIR]");
    return 2;
}
if (File.Exists(outDb)) { Console.Error.WriteLine($"{outDb} exists; not overwritten"); return 1; }
string store = (opt.GetValueOrDefault("store") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalog))!, "..", "store"));
store = Path.GetFullPath(store).Replace('\\', '/');
static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

// The drop list: dataset, reason, source (a header line, then one row per deposit).
var dropped = File.ReadAllLines(dropFile).Select(l => l.TrimEnd('\r')).Skip(1).Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t')).ToDictionary(c => c[0], c => c.Length > 1 ? c[1] : "", StringComparer.Ordinal);

// The manifest's status per deposit, for the record only.
var status = new Dictionary<string, string>(StringComparer.Ordinal);
if (opt.TryGetValue("manifest", out var manifest))
{
    string? cur = null;
    foreach (var raw in File.ReadLines(manifest))
    {
        var l = raw.TrimEnd('\r');
        var a = Regex.Match(l, @"^  - accession: (\S+)");
        if (a.Success) { cur = a.Groups[1].Value; continue; }
        var s = Regex.Match(l, @"^    status: (\S+)");
        if (s.Success && cur is not null && !status.ContainsKey(cur)) status[cur] = s.Groups[1].Value;
    }
}

// The live catalog: its meta row, its views, and the bundles it serves.
var views = new List<(string Name, string Body)>();
var served = new HashSet<string>(StringComparer.Ordinal);
var metaCols = new List<string>(); var metaVals = new List<object?>();
using (var live = new DuckDBConnection($"Data Source={catalog};ACCESS_MODE=READ_ONLY"))
{
    live.Open();
    using (var c = live.CreateCommand())
    {
        c.CommandText = "select view_name, sql from duckdb_views() where not internal and schema_name = 'main' order by view_oid";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            string name = r.GetString(0), sql = r.GetString(1).Trim().TrimEnd(';');
            var m = Regex.Match(sql, @"^CREATE VIEW \S+ AS (.*)$", RegexOptions.Singleline);
            if (!m.Success) throw new InvalidDataException($"view {name}: unexpected SQL {sql[..Math.Min(80, sql.Length)]}");
            views.Add((name, m.Groups[1].Value));
        }
    }
    using (var c = live.CreateCommand())
    {
        c.CommandText = "select distinct dataset_id from runs";
        using var r = c.ExecuteReader();
        while (r.Read()) served.Add(r.GetString(0));
    }
    using (var c = live.CreateCommand())
    {
        c.CommandText = "select * from catalog_meta";
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException($"{catalog}: catalog_meta is empty");
        for (int i = 0; i < r.FieldCount; i++) { metaCols.Add(r.GetName(i)); metaVals.Add(r.IsDBNull(i) ? null : r.GetValue(i)); }
    }
}

// Every deposit in the store the catalog does not serve: its newest bundle (bundle.json written_utc, as datarepo serves).
var added = new List<(string Dataset, string Bundle, string Written, string Status)>();
var skipped = new List<(string Dataset, string Why)>();
foreach (var dir in Directory.GetDirectories(store).OrderBy(d => d, StringComparer.Ordinal))
{
    string ds = Path.GetFileName(dir);
    if (served.Contains(ds)) continue;
    if (served.Any(s => s.StartsWith(ds + "-", StringComparison.Ordinal))) { skipped.Add((ds, "split into served parts")); continue; }
    if (dropped.TryGetValue(ds, out var why)) { skipped.Add((ds, "dropped: " + why)); continue; }
    string? best = null, bestT = "";
    foreach (var b in Directory.GetDirectories(dir))
    {
        var bj = Path.Combine(b, "bundle.json");
        if (!File.Exists(bj)) continue;
        var t = Regex.Match(File.ReadAllText(bj), "\"written_utc\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        if (string.CompareOrdinal(t, bestT) > 0) { bestT = t; best = Path.GetFileName(b); }
    }
    if (best is null) { skipped.Add((ds, "no bundle")); continue; }
    added.Add((ds, best, bestT, status.GetValueOrDefault(ds, "not in manifest")));
}
foreach (var d in dropped.Keys.Where(d => served.Contains(d)))
    Console.WriteLine($"note: {d} is on --drop but the catalog serves it; the catalog's own evidence is kept");

var part = new Regex(@"SELECT '[^']+' AS dataset_id, '[^']+' AS bundle_id, (\* EXCLUDE \(dataset_id\)|\*) FROM read_parquet\('([^']+)/[^/']+/[^/']+/([^/']+\.parquet)'\)");
using var con = new DuckDBConnection($"Data Source={outDb}");
con.Open();
void Exec(string sql) { using var c = con.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
bool Has(string file, string column)
{
    using var c = con.CreateCommand();
    c.CommandText = $"select count(*) from (describe select * from read_parquet({Lit(file)})) where column_name = {Lit(column)}";
    return Convert.ToInt64(c.ExecuteScalar()) > 0;
}

// Base views first (over the store), in the live catalog's order; derived views after, retried until their inputs exist.
var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
var pending = new List<(string Name, string Body)>();
foreach (var (name, body) in views)
{
    var m = part.Match(body);
    if (!m.Success) { pending.Add((name, body)); continue; }
    string select = m.Groups[1].Value, file = m.Groups[3].Value;
    var parts = new List<string>();
    foreach (var (ds, b, _, _) in added)
    {
        string f = $"{store}/{ds}/{b}/{file}";
        if (!File.Exists(f)) { missing[file] = missing.GetValueOrDefault(file) + 1; continue; }
        string sel = select.StartsWith("* EXCLUDE") && !Has(f, "dataset_id") ? "*" : select;
        parts.Add($"(SELECT {Lit(ds)} AS dataset_id, {Lit(b)} AS bundle_id, {sel} FROM read_parquet({Lit(f)}))");
    }
    Exec($"CREATE VIEW {name} AS ({body})" + string.Concat(parts.Select(p => " UNION ALL BY NAME " + p)));
}
for (int round = 0; pending.Count > 0; round++)
{
    var next = new List<(string, string)>();
    foreach (var (name, body) in pending)
        try { Exec($"CREATE VIEW {name} AS {body}"); }
        catch (Exception) when (round < 10) { next.Add((name, body)); }
    if (next.Count == pending.Count && round >= 10) throw new InvalidDataException("derived views that never bind: " + string.Join(", ", next.Select(n => n.Item1)));
    pending = next;
}

// psms_1pct over every bundle these views serve, at the catalog's 1% rule (as BuildPtmDb's PsmsOnePercent reads them).
var psmParts = new List<string>();
using (var c = con.CreateCommand())
{
    c.CommandText = "select distinct dataset_id, bundle_id from runs order by 1";
    using var r = c.ExecuteReader();
    while (r.Read())
    {
        string f = $"{store}/{r.GetString(0)}/{r.GetString(1)}/psms.parquet";
        if (!File.Exists(f)) throw new FileNotFoundException($"{r.GetString(0)} bundle {r.GetString(1)}: no psms.parquet at {f}");
        psmParts.Add(f);
    }
}
var psmSql = psmParts.Select(f =>
{
    string ds = f.Split('/')[^3];
    string notch = Has(f, "q_value_notch") ? " AND (q_value_notch IS NULL OR q_value_notch <= 0.01)" : "";
    return $"SELECT {Lit(ds)} AS dataset_id, run_id, peptidoform, target_decoy FROM read_parquet({Lit(f)}) WHERE q_value <= 0.01{notch}";
});
Exec("CREATE VIEW psms_1pct AS " + string.Join(" UNION ALL ", psmSql));

// catalog_meta: the live row, its id marked; and the record of what was added.
string liveId = metaVals[metaCols.IndexOf("catalog_id")]?.ToString() ?? "";
var vals = metaCols.Select((col, i) => col == "catalog_id" ? Lit($"{liveId}+kept{added.Count}")
    : metaVals[i] is null ? "NULL" : $"CAST({Lit(Convert.ToString(metaVals[i], System.Globalization.CultureInfo.InvariantCulture)!)} AS VARCHAR)");
Exec($"CREATE TABLE catalog_meta AS SELECT {string.Join(", ", vals.Zip(metaCols, (v, col) => $"{v} AS \"{col}\""))}");
Exec("CREATE TABLE evidence_bundles (dataset_id VARCHAR, bundle_id VARCHAR, written_utc VARCHAR, manifest_status VARCHAR)");
foreach (var (ds, b, w, s) in added)
    Exec($"INSERT INTO evidence_bundles VALUES ({Lit(ds)}, {Lit(b)}, {Lit(w)}, {Lit(s)})");

var tsv = new StringBuilder("dataset_id\tbundle_id\twritten_utc\tmanifest_status\r\n");
foreach (var (ds, b, w, s) in added) tsv.Append($"{ds}\t{b}\t{w}\t{s}\r\n");
foreach (var (ds, why) in skipped) tsv.Append($"# not added: {ds}\t{why}\r\n");
string listFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outDb))!, "evidence_bundles.tsv");
File.WriteAllText(listFile, tsv.ToString());

Console.WriteLine($"live catalog {liveId}: {served.Count} deposits served, {views.Count} views");
Console.WriteLine($"added the newest kept bundle of {added.Count} deposits the catalog does not serve: " +
    string.Join(", ", added.GroupBy(a => a.Status).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
foreach (var (ds, why) in skipped) Console.WriteLine($"  not added: {ds} ({why})");
foreach (var (f, n) in missing) Console.WriteLine($"  {n} added bundles have no {f}");
Console.WriteLine($"psms_1pct over {psmParts.Count} bundles; catalog_id {liveId}+kept{added.Count}; wrote {outDb} and {listFile}");
return 0;
