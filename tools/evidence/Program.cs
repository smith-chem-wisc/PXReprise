// pxr-evidence: what a deposit's paper says about its samples, and the SDRF drafted from it (the user, 2026-11-04:
// "use those [API credits] to boost the quality of all the sdrfs"; design: sdrf's SAMPLE-EVIDENCE.md, G36/G38, D42/D43).
//
//   pxr-evidence read  --out DIR [--model claude-opus-5-5 | --no-model] [--refresh] [--list FILE] PXD...
//       Per deposit, into DIR\<acc>\: the PRIDE record and file list; the paper (Europe PMC JATS) and its supplements;
//       the claims the rules find in the supplement tables (evidence.rules.tsv); the claims Claude finds in the paper,
//       tables and record that the rules left open, each backed by a quote (evidence.model.tsv, model.json: tokens,
//       cost, the stated design, the rejected claims); and two drafts, draft.noevidence.sdrf.tsv (the record and file
//       names only, as today) and draft.sdrf.tsv (with the evidence). A model reading is paid once: it is reused on a
//       rerun unless --refresh. One line per deposit is appended to DIR\summary.tsv. The key is ANTHROPIC_API_KEY.
//   pxr-evidence grade --out DIR --curated DIR [--list FILE] [PXD...]
//       Each draft against the curated SDRF <curated>\<acc>.sdrf.tsv, row by row on (data file, label): organism, label,
//       fraction, technical replicate and other characteristics by value; samples (source name) and every curated factor
//       by partition (do the same files go together?), as a Rand index. Writes DIR\grade.tsv.
// Exit codes: 0 done (per-deposit problems are in the summary), 1 a run-wide failure, 2 usage.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Anthropic;
using MzLibUtil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Readers;
using SampleEvidenceModel;
using UsefulProteomicsDatabases;

var inv = CultureInfo.InvariantCulture;
var opt = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
var accs = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] is "--no-model" or "--refresh") flags.Add(args[i]);
    else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) opt[args[i][2..]] = args[++i];
    else accs.Add(args[i]);
}
if (opt.TryGetValue("list", out string? listFile))
    accs.AddRange(File.ReadAllLines(listFile).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0));
accs = accs.Select(a => a.ToUpperInvariant()).Distinct().ToList();
if (args.Length == 0 || !opt.TryGetValue("out", out string? outRoot) || accs.Any(a => !System.Text.RegularExpressions.Regex.IsMatch(a, "^PXD\\d{6}$")))
{
    Console.Error.WriteLine("usage: pxr-evidence read --out DIR [--model ID | --no-model] [--refresh] [--list FILE] PXD...\n" +
                            "       pxr-evidence grade --out DIR --curated DIR [--list FILE] [PXD...]");
    return 2;
}
Directory.CreateDirectory(outRoot);

if (args[0] == "grade")
{
    if (!opt.TryGetValue("curated", out string? curatedDir)) { Console.Error.WriteLine("grade needs --curated DIR"); return 2; }
    if (accs.Count == 0)
        accs = Directory.GetFiles(curatedDir, "PXD*.sdrf.tsv").Select(f => Path.GetFileName(f)[..9]).Order(StringComparer.Ordinal).ToList();
    return Grading.Run(outRoot, curatedDir, accs);
}
if (args[0] != "read") { Console.Error.WriteLine($"unknown command '{args[0]}'"); return 2; }
if (accs.Count == 0) { Console.Error.WriteLine("read needs at least one PXD accession"); return 2; }

string? model = flags.Contains("--no-model") ? null : opt.GetValueOrDefault("model", "claude-opus-5-5");
// Per million tokens, for the summary's estimate only (the Console bills the truth). Claude Opus 5.5: $4 in, $20 out, $0.20
// cache read; a cache write (the reader's cached system prompt) is not in the usage the reader returns.
double priceIn = double.Parse(opt.GetValueOrDefault("price-in", "4"), inv), priceOut = double.Parse(opt.GetValueOrDefault("price-out", "20"), inv),
    priceCache = double.Parse(opt.GetValueOrDefault("price-cache-read", "0.2"), inv);
AnthropicClient? claude = null;
if (model is not null)
{
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    {
        Console.Error.WriteLine("ANTHROPIC_API_KEY is not set for this user: set it, or run with --no-model");
        return 1;
    }
    claude = new AnthropicClient();
}

string summary = Path.Combine(outRoot, "summary.tsv");
if (!File.Exists(summary))
    File.WriteAllText(summary, string.Join('\t', "utc", "accession", "raw_files", "pubmed", "pmcid", "full_text", "supplement_files", "tables",
        "rule_claims", "model", "model_claims", "rejected", "stop", "input_tokens", "output_tokens", "cache_read_tokens", "est_cost_usd",
        "predicted_runs", "draft_rows", "validator_errors", "validator_warnings", "notes") + "\n");
using var pride = new PrideArchiveClient();
using var pmc = new EuropePmcClient();
double total = 0;
foreach (string acc in accs)
{
    var notes = new List<string>();
    try
    {
        var row = await Deposit.ReadAsync(acc, Path.Combine(outRoot, acc), model, flags.Contains("--refresh"), pride, pmc, claude,
            (priceIn, priceOut, priceCache), notes);
        total += double.TryParse(row[16], NumberStyles.Float, inv, out double c) ? c : 0;
        File.AppendAllText(summary, string.Join('\t', row.Select(Clean)) + "\n");
        Console.WriteLine($"{acc}: {string.Join(" | ", row.Skip(2).Take(20))}");
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        // One deposit's failure never stops the list; it is recorded with its type and message.
        notes.Add($"FAILED {e.GetType().Name}: {e.Message}");
        File.AppendAllText(summary, string.Join('\t', new[] { DateTime.UtcNow.ToString("o", inv), acc }.Concat(Enumerable.Repeat("", 19)).Append(string.Join("; ", notes)).Select(Clean)) + "\n");
        Console.WriteLine($"{acc}: FAILED {e.GetType().Name}: {e.Message}");
    }
}
Console.WriteLine($"estimated model cost this run: ${total:0.00} ({accs.Count} deposits)");
return 0;

static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

internal static class Deposit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static async Task<string[]> ReadAsync(string acc, string dir, string? model, bool refresh, PrideArchiveClient pride,
        EuropePmcClient pmc, AnthropicClient? claude, (double In, double Out, double Cache) price, List<string> notes)
    {
        Directory.CreateDirectory(dir);
        // 1. The PRIDE record and file list, kept so a rerun reads the same inputs.
        string projectFile = Path.Combine(dir, "project.json"), filesFile = Path.Combine(dir, "files.json");
        PrideProject project;
        if (File.Exists(projectFile) && !refresh) project = JsonConvert.DeserializeObject<PrideProject>(File.ReadAllText(projectFile))!;
        else
        {
            var (found, p) = await pride.TryGetProjectAsync(acc);
            if (!found) throw new InvalidDataException("PRIDE has no such project");
            project = p;
            File.WriteAllText(projectFile, JsonConvert.SerializeObject(project, Formatting.Indented));
        }
        List<PrideArchiveFile> files;
        if (File.Exists(filesFile) && !refresh) files = JsonConvert.DeserializeObject<List<PrideArchiveFile>>(File.ReadAllText(filesFile))!;
        else
        {
            files = await pride.GetProjectFilesAsync(acc);
            File.WriteAllText(filesFile, JsonConvert.SerializeObject(files, Formatting.Indented));
        }
        var raw = files.Where(f => string.Equals(f.FileCategory?.Value, "RAW", StringComparison.OrdinalIgnoreCase)).Select(f => f.FileName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        string record = string.Join(Environment.NewLine, project.Title, project.ProjectDescription, project.SampleProcessingProtocol, project.DataProcessingProtocol);

        // 2. The paper the record cites, and its supplements, from Europe PMC (open access only).
        string paperDir = Path.Combine(dir, "paper"), suppDir = Path.Combine(dir, "supp");
        EuropePmcArticle? article = null;
        string? paperXml = null;
        int suppFiles = 0;
        try
        {
            foreach (var r in project.References ?? new List<PrideReference>())
            {
                if (r.PubmedId <= 0 && string.IsNullOrWhiteSpace(r.Doi)) continue;
                var (found, a) = await pmc.TryFindArticleAsync(r);
                if (found) { article = a; break; }
            }
            if (article is null) notes.Add(project.References is { Count: > 0 } ? "the cited paper is not in Europe PMC" : "the record cites no paper");
            else
            {
                Directory.CreateDirectory(paperDir);
                File.WriteAllText(Path.Combine(paperDir, "article.json"), JsonConvert.SerializeObject(article, Formatting.Indented));
                if (!article.HasFullText) notes.Add($"{article.PmcId}{(article.PmcId.Length == 0 ? "no PMC id" : "")}: no open full text");
                else
                {
                    var (ft, path) = await pmc.TryDownloadFullTextXmlAsync(article, paperDir);
                    if (ft) paperXml = path;
                    var (sf, zip) = await pmc.TryDownloadSupplementaryFilesAsync(article, paperDir);
                    if (sf && zip is not null)
                    {
                        string marker = Path.Combine(suppDir, "_extracted");
                        if (!File.Exists(marker) || refresh)
                        {
                            Directory.CreateDirectory(suppDir);
                            ZipFile.ExtractToDirectory(zip, suppDir, overwriteFiles: true);
                            File.WriteAllText(marker, Path.GetFileName(zip));
                        }
                    }
                    else notes.Add("no supplements in Europe PMC");
                }
            }
        }
        catch (HttpRequestException e) { notes.Add($"Europe PMC unavailable ({e.Message}): read without the paper"); }
        catch (MzLibException e) { notes.Add($"Europe PMC refused ({e.Message}): read without the paper"); }

        // 3. Supplement tables, and the claims the rules find in them.
        var tables = new List<SupplementTable>();
        if (Directory.Exists(suppDir))
            foreach (string f in Directory.GetFiles(suppDir, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('_')))
            {
                suppFiles++;
                try { tables.AddRange(SupplementTableReader.Read(f)); }
                catch (Exception e) when (e is not OperationCanceledException) { notes.Add($"{Path.GetFileName(f)} unreadable ({e.GetType().Name})"); }
            }
        var rules = SampleEvidenceExtractor.Extract(tables, raw);
        SdrfEvidenceFile.Write(Path.Combine(dir, "evidence.rules.tsv"), rules);

        // 4. The model reading (opt-in): what the rules left open, each claim quoted. Paid once, reused on a rerun.
        string modelTsv = Path.Combine(dir, "evidence.model.tsv"), modelJson = Path.Combine(dir, "model.json");
        IReadOnlyList<SdrfEvidence> modelEvidence = Array.Empty<SdrfEvidence>();
        string stop = "", rejected = "", inTok = "", outTok = "", cacheTok = "", cost = "", predicted = "";
        if (claude is not null && model is not null)
        {
            if (File.Exists(modelTsv) && File.Exists(modelJson) && !refresh)
            {
                modelEvidence = SdrfEvidenceFile.Read(modelTsv);
                var j = JObject.Parse(File.ReadAllText(modelJson));
                stop = $"{j["stop"]} (reused)";
                rejected = j["rejected"] is JArray rj ? rj.Count.ToString(Inv) : "";
                predicted = (string?)j["design"]?["predicted_runs"] ?? "";
                cost = "0";
            }
            else
            {
                string paperText = paperXml is not null ? PublicationText.MethodsAndTables(File.ReadAllText(paperXml)) : "";
                var input = new PublicationInput(acc, record, paperText, tables, raw, rules);
                var reader = new ModelEvidenceReader(claude, model);
                var r = await reader.ReadAsync(input);
                modelEvidence = r.Evidence;
                SdrfEvidenceFile.Write(modelTsv, r.Evidence);
                double usd = r.InputTokens / 1e6 * price.In + r.OutputTokens / 1e6 * price.Out + r.CacheReadTokens / 1e6 * price.Cache;
                var design = r.Design is { } d ? new JObject
                {
                    ["groups"] = new JArray(d.Groups.Select(g => new JObject { ["name"] = g.Name, ["samples"] = g.Samples })),
                    ["plexes"] = d.Plexes, ["technical_replicates"] = d.TechnicalReplicates, ["fractions_per_sample"] = d.FractionsPerSample,
                    ["runs_stated"] = d.RunsStated, ["predicted_runs"] = d.PredictedRuns, ["raw_files"] = raw.Count,
                    ["quotes"] = new JArray(d.Quotes),
                } : null;
                File.WriteAllText(modelJson, new JObject
                {
                    ["utc"] = DateTime.UtcNow.ToString("o", Inv), ["model"] = model, ["stop"] = r.StopReason,
                    ["input_tokens"] = r.InputTokens, ["output_tokens"] = r.OutputTokens, ["cache_read_tokens"] = r.CacheReadTokens,
                    ["est_cost_usd"] = Math.Round(usd, 4), ["claims"] = r.Evidence.Count, ["rejected"] = new JArray(r.Rejected), ["design"] = design,
                    ["paper"] = paperXml is not null ? Path.GetFileName(paperXml) : null, ["tables"] = tables.Count,
                }.ToString(Formatting.Indented));
                stop = r.StopReason;
                rejected = r.Rejected.Count.ToString(Inv);
                (inTok, outTok, cacheTok) = (r.InputTokens.ToString(Inv), r.OutputTokens.ToString(Inv), r.CacheReadTokens.ToString(Inv));
                cost = usd.ToString("0.0000", Inv);
                predicted = r.Design?.PredictedRuns.ToString(Inv) ?? "";
            }
        }

        // 5. Two drafts: as today (the record and file names), and with the evidence.
        SdrfDocument Draft(IEnumerable<SdrfEvidence>? evidence, string file, out int errors, out int warnings)
        {
            var draft = SdrfDrafter.Draft(project, raw, evidence);
            var doc = SdrfDrafter.ToDocument(draft, acc);
            doc.WriteResults(Path.Combine(dir, file));
            if (evidence is not null && draft.EvidenceNotes.Count > 0)
                File.WriteAllLines(Path.Combine(dir, "evidence.notes.tsv"), new[] { "data_file\tcolumn\tdraft_value\tevidence_value\twhy" }
                    .Concat(draft.EvidenceNotes.Select(n => string.Join('\t', n.DataFile, n.Column, n.DraftValue, n.EvidenceValue, n.Why))));
            var v = SdrfValidator.Validate(doc);
            errors = v.Errors.Count();
            warnings = v.Warnings.Count();
            return doc;
        }
        Draft(null, "draft.noevidence.sdrf.tsv", out _, out _);
        var withEvidence = Draft(rules.Concat(modelEvidence).ToList(), "draft.sdrf.tsv", out int err, out int warn);

        return new[]
        {
            DateTime.UtcNow.ToString("o", Inv), acc, raw.Count.ToString(Inv), article?.PubMedId ?? "", article?.PmcId ?? "",
            paperXml is not null ? "yes" : "no", suppFiles.ToString(Inv), tables.Count.ToString(Inv), rules.Count.ToString(Inv),
            model ?? "none", modelEvidence.Count.ToString(Inv), rejected, stop, inTok, outTok, cacheTok, cost, predicted,
            withEvidence.Results.Count.ToString(Inv), err.ToString(Inv), warn.ToString(Inv), string.Join("; ", notes),
        };
    }
}
