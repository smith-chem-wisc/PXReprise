#:package DuckDB.NET.Data.Full@1.2.0
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
//                               [--min-psms 5] [--sites-out DIR]
//   dotnet run BuildPtmDb.cs -- --from-sites DIR ...   use DIR/sites_<organism>.tsv instead of the catalog (to prove the
//                               port: v1's site lists must rebuild v1's XMLs byte for byte)
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

var all = Stopwatch.StartNew();
var (meta, rows) = fromSites is null ? Select(catalog!, minPsms) : (null, FromSites(fromSites, xmls.Keys));
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

static (JsonObject Meta, List<Site> Rows) Select(string catalog, int minPsms)
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
            AND s.modification_name IN ({names})
          GROUP BY 1,2,3,4 HAVING sum(s.n_psms) >= {minPsms}";
        using var r = c.ExecuteReader();
        while (r.Read())
            if (Orgs.TryGetValue(r.GetString(0), out string? o))
                rows.Add(new Site(o, r.GetString(1), Convert.ToInt32(r.GetValue(2)), r.GetString(3), r.GetValue(4)?.ToString() ?? "",
                    Convert.ToInt32(r.GetValue(5)), Convert.ToInt64(r.GetValue(6)), r.GetString(7)));
    }
    return (meta, rows);
}

static List<Site> FromSites(string dir, IEnumerable<string> orgs) =>
    orgs.SelectMany(o => File.ReadLines(Path.Combine(dir, $"sites_{o}.tsv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t'))
        .Select(p => new Site(o, p[0], int.Parse(p[1]), p[3], p[4], int.Parse(p[5]), long.Parse(p[6]), p[7]))).ToList();

/// <summary>(accession, position) -> chemistry classes of UniProt 'modified residue' features; accession -> sequence.</summary>
static (Dictionary<(string, int), HashSet<string>> Ann, Dictionary<string, string> Seq) LoadUniprot(string xml)
{
    XNamespace ns = "http://uniprot.org/uniprot";
    var ann = new Dictionary<(string, int), HashSet<string>>();
    var seq = new Dictionary<string, string>(StringComparer.Ordinal);
    using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
    reader.MoveToContent();
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
