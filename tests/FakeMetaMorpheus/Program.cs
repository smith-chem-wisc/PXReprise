// A stand-in for MetaMorpheus's CMD, for OFFLINE tests of PXReprise's search stage (the C# twin of aging's
// fake_metamorpheus.py). It implements only the calls the engine makes, with the output shapes MetaMorpheus 1.1.11
// writes. It tests orchestration (arguments, success checks, provenance, the library registry), never science.
//   CMD -g -o <dir>      -> the banner "Welcome to MetaMorpheus / <release>" and the three default task TOMLs (CRLF)
//   CMD --help           -> help text carrying "CMD 1.0.0+<40-hex commit>"
//   CMD -t ... -o <dir>  -> Task1CalibrationTask/, Task2GptmdTask/, Task3SearchTask/ with small result tables
// Environment: FAKE_MM_EXIT=<n> exits with n after writing outputs; FAKE_MM_NO_PROTEIN_GROUPS=1 omits the protein-group
// table (FlashLFQ failing silently); FAKE_MM_SKIP_QUANT=1 prints the design warning; FAKE_MM_HANG=1 sleeps forever, using
// no CPU (a hung search); FAKE_MM_BUSY_SECONDS=<n> works silently for n seconds first (MetaMorpheus's long PEP step);
// FAKE_MM_WARN=<text> prints that line during the first task (MetaMorpheus's warnings, e.g. "Unrecognized mod ...").
using System.Text;

string Release = Environment.GetEnvironmentVariable("FAKE_MM_RELEASE") ?? "1.1.11";
string? Arg(string flag) { int i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
List<string> Values(string flag)
{
    var list = new List<string>();
    int i = Array.IndexOf(args, flag);
    if (i < 0) return list;
    for (int j = i + 1; j < args.Length && !args[j].StartsWith('-'); j++) list.Add(args[j]);
    return list;
}
void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(false)); }

if (args.Contains("-g"))
{
    string o = Arg("-o")!;
    Write(Path.Combine(o, "CalibrationTask.toml"), "TaskType = \"Calibrate\"\n\n[CommonParameters]\nMaxThreadsToUsePerFile = 63\nListOfModsFixed = \"Common Fixed\\tCarbamidomethyl on C\\t\\tCommon Fixed\\tCarbamidomethyl on U\"\nListOfModsVariable = \"Common Variable\\tOxidation on M\"\nProductMassTolerance = \"±20.0000 PPM\"\n\n[CommonParameters.DigestionParams]\nSpecificProtease = \"trypsin\"\nProtease = \"trypsin\"\n");
    Write(Path.Combine(o, "GptmdTask.toml"), "TaskType = \"Gptmd\"\n\n[GptmdParameters]\nListOfModsGptmd = \"Common Biological\\tAcetylation on K\\t\\t\"\n\n[CommonParameters]\nMaxThreadsToUsePerFile = 63\nListOfModsFixed = \"Common Fixed\\tCarbamidomethyl on C\\t\\tCommon Fixed\\tCarbamidomethyl on U\"\nListOfModsVariable = \"Common Variable\\tOxidation on M\"\n\n[CommonParameters.DigestionParams]\nSpecificProtease = \"trypsin\"\nProtease = \"trypsin\"\n");
    Write(Path.Combine(o, "SearchTask.toml"), "TaskType = \"Search\"\n\n[SearchParameters]\nMatchBetweenRuns = false\nSearchType = \"Classic\"\nWriteSpectralLibrary = false\nUpdateSpectralLibrary = false\n\n[CommonParameters]\nMaxThreadsToUsePerFile = 63\nListOfModsFixed = \"Common Fixed\\tCarbamidomethyl on C\\t\\tCommon Fixed\\tCarbamidomethyl on U\"\nListOfModsVariable = \"Common Variable\\tOxidation on M\"\n\n[CommonParameters.DigestionParams]\nSpecificProtease = \"trypsin\"\nProtease = \"trypsin\"\n");
    Console.WriteLine($"Welcome to MetaMorpheus\n{Release}\n");
    return 0;
}
if (args.Contains("--help"))
{
    Console.WriteLine("CMD 1.0.0+" + string.Concat(Enumerable.Repeat("0123456789abcdef", 2)) + "01234567\nUsage: CMD [options]");
    return 0;
}

string outDir = Arg("-o")!;
Directory.CreateDirectory(outDir);
if (Environment.GetEnvironmentVariable("FAKE_MM_HANG") == "1") Thread.Sleep(Timeout.Infinite);
if (double.TryParse(Environment.GetEnvironmentVariable("FAKE_MM_BUSY_SECONDS"), out double busy))
{
    var until = DateTime.UtcNow.AddSeconds(busy);
    double x = 0;
    while (DateTime.UtcNow < until) x += Math.Sqrt(x + 1);
}
var spectra = Values("-s").Select(Path.GetFileNameWithoutExtension).ToList();
foreach (var (task, i) in new[] { "CalibrationTask", "GptmdTask", "SearchTask" }.Select((t, i) => (t, i + 1)))
{
    Console.WriteLine($"Starting task: Task{i}{task}");
    if (i == 1 && Environment.GetEnvironmentVariable("FAKE_MM_WARN") is { Length: > 0 } warn) Console.WriteLine(warn);
    Directory.CreateDirectory(Path.Combine(outDir, $"Task{i}{task}"));
    if (task == "SearchTask" && Environment.GetEnvironmentVariable("FAKE_MM_SKIP_QUANT") == "1")
        Console.WriteLine("Error reading experimental design file: Condition \"x\" biorep 2 is missing. Skipping quantification");
    Console.WriteLine($"Finished task: Task{i}{task}");
}
File.WriteAllText(Path.Combine(outDir, "Task2GptmdTask", "db-GPTMD.xml"), "<uniprot/>");
string sd = Path.Combine(outDir, "Task3SearchTask");
// 8 target, 1 contaminant, 1 decoy PSM at q <= 0.01, plus one above the cut-off.
File.WriteAllText(Path.Combine(sd, "AllPSMs.psmtsv"), "QValue\tDecoy/Contaminant/Target\n" + string.Concat(Enumerable.Repeat("0.001\tT\n", 8)) + "0.002\tC\n0.003\tD\n0.5\tT\n");
File.WriteAllText(Path.Combine(sd, "AllPeptides.psmtsv"), "QValue\n0.001\n");
File.WriteAllText(Path.Combine(sd, "AllQuantifiedPeptides.tsv"), "Sequence\nPEPTIDE\n");
string cols = string.Join("\t", spectra.Select(s => $"Intensity_{s}"));
string Row(string acc, string name, string org, string td, string q, string v) => $"{acc}\t{name}\t{org}\t{td}\t{q}\t" + string.Join("\t", spectra.Select(_ => v)) + "\n";
if (Environment.GetEnvironmentVariable("FAKE_MM_NO_PROTEIN_GROUPS") != "1")
    File.WriteAllText(Path.Combine(sd, "AllQuantifiedProteinGroups.tsv"),
        $"Protein Accession\tProtein Full Name\tOrganism\tProtein Decoy/Contaminant/Target\tProtein QValue\t{cols}\n"
        + Row("Q8WZ42", "Titin", "Homo sapiens", "T", "0.001", "90") + Row("P02769", "Serum albumin", "Bos taurus", "C", "0.001", "10")
        + Row("P20929", "Nebulin", "Homo sapiens", "T", "0.5", "900"));
File.WriteAllText(Path.Combine(sd, "AllQuantifiedPeaks.tsv"),
    "Peak Detection Type\tRandom RT\tPIP Q-Value\tDecoy Peptide\nMSMS\t\t\tFalse\nMSMS\t\t\tFalse\nMSMS\t\t\tFalse\nMBR\tFalse\t0.001\tFalse\nMBR\tTrue\t0.001\tFalse\nMBR\tFalse\t0.2\tFalse\n");
File.WriteAllText(Path.Combine(sd, "results.txt"), "All target PSMs with q-value <= 0.01: 8\n\nPSMs within 1% FDR: 9\n");
// The spectral library, under MetaMorpheus's own timestamped names, in the SEARCH task's folder. An update MERGES:
// every spectrum of the loaded library plus one for this run, so a test can prove the chain grows.
string? searchToml = Values("-t").FirstOrDefault(t => t.EndsWith("SearchTask.toml", StringComparison.Ordinal));
if (searchToml is not null)
{
    string cfg = File.ReadAllText(searchToml);
    var libs = Values("-d").Where(d => d.EndsWith(".msp", StringComparison.OrdinalIgnoreCase)).ToList();
    if (cfg.Contains("UpdateSpectralLibrary = true"))
    {
        int carried = libs.Sum(l => File.ReadLines(l).Count(x => x.StartsWith("Name:")));
        File.WriteAllText(Path.Combine(sd, "updateSpectralLibrary_2026-09-28-12-00-00.msp"),
            string.Concat(Enumerable.Range(0, carried).Select(i => $"Name: CARRIED{i}/2\n")) + "Name: NEW/2\n");
    }
    else if (cfg.Contains("WriteSpectralLibrary = true"))
        File.WriteAllText(Path.Combine(sd, "SpectralLibrary_2026-09-28-12-00-00.msp"), "Name: PEPTIDEK/2\nName: PEPTIDER/2\n");
}
File.WriteAllText(Path.Combine(outDir, "allResults.txt"), "fake run\n");
return int.TryParse(Environment.GetEnvironmentVariable("FAKE_MM_EXIT"), out int rc) ? rc : 0;
