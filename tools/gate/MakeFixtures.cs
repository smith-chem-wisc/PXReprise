#:project ../../src/PXReprise/PXReprise.csproj
#:property PublishAot=false
// Test fixtures for the question gate (v0.4.46, aging PXR-A179 part C): each deposit's gate text, read with the engine's own
// GateText readers and written WITHOUT contacts (only the fields the gate reads; reference lines reduced to their titles).
// Text, first found: the PRIDE record PXReprise saved on the evidence share; ProteomeXchange's XML in aging's screen cache;
// PRIDE's live record; ProteomeXchange live.
//   dotnet run tools/gate/MakeFixtures.cs -- <expected.tsv> <evidence share dir> <aging screen cache dir> <out dir>
// expected.tsv: accession<TAB>expected (queue | watch | not_queue, which means not_queued or watch).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Census;
using UsefulProteomicsDatabases;

string expected = args[0], evidence = args[1], cache = args[2], outDir = args[3];
Directory.CreateDirectory(outDir);
using var client = new PrideArchiveClient();
var search = new PrideProjectSearch(client);
foreach (string line in File.ReadAllLines(expected).Skip(1).Where(l => l.Trim().Length > 0))
{
    string acc = line.Split('\t')[0].Trim();
    GateText? text = null;
    string pj = Path.Combine(evidence, acc, "project.json"), px = Path.Combine(cache, acc + ".xml");
    if (File.Exists(pj) && JsonSerializer.Deserialize<PrideProject>(File.ReadAllText(pj)) is { } saved) text = GateText.From(saved);
    else if (File.Exists(px)) text = GateText.FromProteomeXchange(File.ReadAllText(px));
    else if (await search.TryGetProjectAsync(acc, CancellationToken.None) is { } live) text = GateText.From(live);
    else if (await search.TryGetProteomeXchangeXmlAsync(acc, CancellationToken.None) is { } xml) text = GateText.FromProteomeXchange(xml);
    if (text is null) { Console.Error.WriteLine($"{acc}: no text found"); continue; }
    var fields = new JsonObject();
    foreach (var (field, value) in text.Fields) fields[field] = value;
    var o = new JsonObject { ["accession"] = acc, ["source"] = text.Source, ["fields"] = fields };
    File.WriteAllText(Path.Combine(outDir, acc + ".json"), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"{acc}\t{text.Source}\t{text.Fields.Sum(f => f.Text.Length)} chars");
}
