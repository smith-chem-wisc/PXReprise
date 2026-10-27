#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Removes whole GPTMD-named site classes from built databases, leaving every other byte as it is (agingPTM-v5, the user
// 2026-10-26/27: "clean the databases and keep them clean until other PRs produce clean databases"). A site this
// builder added is written as mzLib's writer writes a GPTMD site:
//     <feature type="modified residue" description="<IdWithMotif>">
//       <location>
//         <position position="N"/>
//       </location>
//     </feature>
// UniProt's own features carry UniProt names ("4-hydroxyproline"), so naming a class by its GPTMD IdWithMotif removes
// only the added sites. Line endings are kept as found.
//
//   dotnet run RemoveClasses.cs -- --tag agingPTM-v5 --provenance data/ptm-db/agingPTM-v5
//       --remove "Hydroxylation on P" --remove "Hydroxylation on K"  SRC.xml=DST.xml ...
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var remove = new List<string>();
var pairs = new List<(string Src, string Dst)>();
string? tag = null, provDir = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--remove": remove.Add(args[++i]); break;
        case "--tag": tag = args[++i]; break;
        case "--provenance": provDir = args[++i]; break;
        default:
            var p = args[i].Split('=', 2);
            if (p.Length != 2) { Console.Error.WriteLine($"not SRC=DST: {args[i]}"); return 2; }
            pairs.Add((p[0], p[1]));
            break;
    }
}
if (tag is null || provDir is null || remove.Count == 0 || pairs.Count == 0)
{
    Console.Error.WriteLine("usage: --tag T --provenance DIR --remove NAME [--remove NAME ...] SRC=DST ...");
    return 2;
}
var openers = remove.ToDictionary(r => $"<feature type=\"modified residue\" description=\"{r}\">", r => r, StringComparer.Ordinal);

Directory.CreateDirectory(provDir);
var files = new JsonArray();
var sums = new StringBuilder();
foreach (var (src, dst) in pairs)
{
    if (File.Exists(dst)) { Console.Error.WriteLine($"refusing to overwrite {dst}"); return 1; }
    var removed = remove.ToDictionary(r => r, _ => 0L, StringComparer.Ordinal);
    long kept = 0;
    string tmp = dst + ".partial";
    using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
    using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
    {
        string? skipping = null;
        foreach (var raw in Lines(input))
        {
            string line = raw.TrimEnd('\r', '\n').Trim();
            if (skipping != null)
            {
                if (line == "</feature>") { removed[skipping]++; skipping = null; }
                else if (line.StartsWith("<feature", StringComparison.Ordinal) || line.StartsWith("</entry", StringComparison.Ordinal))
                    throw new InvalidDataException($"{src}: unterminated '{skipping}' feature before: {line}");
                continue;
            }
            if (openers.TryGetValue(line, out var name)) { skipping = name; continue; }
            if (line.StartsWith("<feature type=\"modified residue\"", StringComparison.Ordinal)) kept++;
            output.Write(raw);
        }
        if (skipping != null) throw new InvalidDataException($"{src}: file ends inside a '{skipping}' feature");
    }
    File.Move(tmp, dst);
    string srcSha = Sha(src), dstSha = Sha(dst);
    sums.Append($"{dstSha} *{Path.GetFileName(dst)}\n");
    var o = new JsonObject
    {
        ["source"] = src.Replace('\\', '/'), ["source_sha256"] = srcSha,
        ["output"] = dst.Replace('\\', '/'), ["output_sha256"] = dstSha,
        ["modified_residue_features_kept"] = kept,
    };
    var rm = new JsonObject();
    foreach (var (k, v) in removed) rm[k] = v;
    o["removed"] = rm;
    files.Add(o);
    Console.WriteLine($"{Path.GetFileName(dst)}: removed {string.Join(", ", removed.Select(kv => $"{kv.Key} {kv.Value}"))}; kept {kept} modified-residue features");
}
var prov = new JsonObject
{
    ["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    ["script"] = "PXReprise tools/ptm-db/RemoveClasses.cs",
    ["tag"] = tag,
    ["rule"] = "every site of the named GPTMD classes removed (the user, 2026-10-26/27: clean the databases and keep them clean until other PRs produce clean databases); every other byte as in the source",
    ["removed_classes"] = new JsonArray(remove.Select(r => (JsonNode)r).ToArray()),
    ["files"] = files,
};
File.WriteAllText(Path.Combine(provDir, $"provenance_{tag}.json"), prov.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
File.WriteAllText(Path.Combine(provDir, $"{tag}.sha256"), sums.ToString());
return 0;

// Each line with its own terminator, so the output keeps the source's line endings byte for byte.
static IEnumerable<byte[]> LinesBytes(Stream s)
{
    var buf = new MemoryStream();
    int b;
    while ((b = s.ReadByte()) != -1)
    {
        buf.WriteByte((byte)b);
        if (b == '\n') { yield return buf.ToArray(); buf.SetLength(0); }
    }
    if (buf.Length > 0) yield return buf.ToArray();
}

static IEnumerable<string> Lines(Stream s)
{
    foreach (var l in LinesBytes(s)) yield return Encoding.UTF8.GetString(l);
}

static string Sha(string path)
{
    using var f = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
}

static class StreamExt
{
    public static void Write(this Stream s, string text) { var b = Encoding.UTF8.GetBytes(text); s.Write(b, 0, b.Length); }
}
