// pxr-evidence: what a deposit's paper, figures and supplements say about its samples, and the SDRF drafted from it (the
// user, 2026-11-04: "use those [API credits] to boost the quality of all the sdrfs"; "deposit wide facts are important";
// "don't forget about supplements. they are often key"; "include information in figures too". Design: sdrf's
// SAMPLE-EVIDENCE.md, G36/G38, D42/D43).
//
//   pxr-evidence read  --out DIR [--model claude-opus-5-5 | --no-model] [--refresh] [--independent] [--list FILE] PXD...
//       [--max-images 20] [--max-pdf-pages 40] [--max-deposit-file-mb 25] [--max-usd N] [--curator DIR]
//       --independent (D65 (8), the user 2026-11-07: "we create an independant sdrf and compare it to the community sdrf"): no
//       deposited or community SDRF is fetched, read or shown to the model (one named so, or a table with SDRF's source name
//       and comment[data file] columns); inputs.tsv says what was left out. Its outputs carry ".independent" in their names
//       (draft.independent.sdrf.tsv, model.independent.json, summary.independent.tsv, ...), so a reading paid for with the
//       SDRF in view is never reused or overwritten; the downloads in each deposit folder are shared.
//       --max-usd: start no new deposit once the estimated spend reaches N. --curator: DIR\<acc>.evidence.tsv holds what a
//       curator confirmed (rows copied from review.tsv with confidence certain), applied like any certain claim.
//       Per deposit, into DIR\<acc>\:
//         project.json, files.json   the PRIDE record and file list;
//         paper\                     the paper's JATS (Europe PMC, else a TDM author manuscript from the PMC Article
//                                    Datasets), and Europe PMC's supplement zip;
//         supp\, figs\, deposit\     the paper's supplements, its figure images, the deposit's OWN files in PRIDE;
//         inputs.tsv                 every file found and what was done with it (tables, text, image, PDF pages, why not);
//         prompt.txt                 the text the model is shown (written with --no-model too, to check before paying);
//         evidence.rules.tsv         the claims the rules find in the tables;
//         evidence.model.tsv, model.json, transcripts.tsv   the model's claims (each quoted), its reading, its
//                                    transcripts of figures and PDF pages (sdrf G38);
//         facts.tsv                  the deposit-wide facts (strain, sex, age, treatment, ...), each with its source;
//         review.tsv                 the guesses (never applied), in the evidence file's format, for a curator;
//         draft.noevidence.sdrf.tsv, draft.sdrf.tsv, evidence.notes.tsv   the two drafts.
//       A model reading is paid once: it is reused on a rerun unless --refresh. One line per deposit goes to
//       DIR\summary.tsv, and the deposit-wide facts to DIR\facts.tsv. The key is ANTHROPIC_API_KEY.
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

if (args is ["selftest"]) return SelfTest.Run();
// A PDF's text as the quote check sees it (margin line numbers left out), for a reviewer checking a rejected quote.
if (args is ["pdftext", var pdfPath]) { Console.Write(SupplementContent.PdfText(pdfPath)); return 0; }
var inv = CultureInfo.InvariantCulture;
var opt = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
var accs = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] is "--no-model" or "--refresh" or "--independent") flags.Add(args[i]);
    else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) opt[args[i][2..]] = args[++i];
    else accs.Add(args[i]);
}
if (opt.TryGetValue("list", out string? listFile))
    accs.AddRange(File.ReadAllLines(listFile).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0));
accs = accs.Select(a => a.ToUpperInvariant()).Distinct().ToList();
if (args.Length == 0 || !opt.TryGetValue("out", out string? outRoot) || accs.Any(a => !System.Text.RegularExpressions.Regex.IsMatch(a, "^PXD\\d{6}$")))
{
    Console.Error.WriteLine("usage: pxr-evidence read --out DIR [--model ID | --no-model] [--refresh] [--independent] [--list FILE] PXD...\n" +
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
// What one deposit may show the model beyond text. The API takes at most 20 images at full size in one request, 5 MB each.
var budget = new Budget(
    MaxImages: Math.Min(20, int.Parse(opt.GetValueOrDefault("max-images", "20"), inv)),
    MaxPdfPages: int.Parse(opt.GetValueOrDefault("max-pdf-pages", "40"), inv),
    MaxDepositFileBytes: long.Parse(opt.GetValueOrDefault("max-deposit-file-mb", "25"), inv) * 1_000_000,
    MaxImageBytes: 5_000_000,
    MaxAttachmentBytes: 24_000_000);
AnthropicClient? claude = null;
if (model is not null)
{
    string? key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    // A key stored for the Windows user after the calling shell started (a long-lived session) is not in the inherited
    // environment: take it from the user's own store, for this process only. It is never printed.
    if (string.IsNullOrEmpty(key) && OperatingSystem.IsWindows())
    {
        key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(key)) Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);
    }
    if (string.IsNullOrEmpty(key))
    {
        Console.Error.WriteLine("ANTHROPIC_API_KEY is not set for this user: set it, or run with --no-model");
        return 1;
    }
    // 2026-11-04: a key pasted with a line break in it failed every request at the HTTP header ("New-line or NUL
    // characters are not allowed in header values"), after the fetching had run. A key holds no whitespace: say so first.
    if (key.Any(char.IsWhiteSpace))
    {
        Console.Error.WriteLine($"ANTHROPIC_API_KEY contains {key.Count(char.IsWhiteSpace)} whitespace character(s), such as a line break from a paste: "
                                + "store it again without them (the run file's first line does this)");
        return 1;
    }
    claude = new AnthropicClient();
}

string sfx = flags.Contains("--independent") ? ".independent" : "";
string summary = Path.Combine(outRoot, Deposit.Name("summary.tsv", sfx));
string summaryHeader = string.Join('\t', Deposit.Columns);
// A summary written by an older version has fewer columns: appending to it shifts every later column under the wrong
// name (the aging run of 2026-11-06 appended 34-field rows under a 32-field header). Set it aside and start afresh.
if (File.Exists(summary) && File.ReadLines(summary).FirstOrDefault()?.TrimEnd('\r') != summaryHeader)
{
    string aside = Path.Combine(outRoot, $"summary{sfx}.before-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.tsv");
    File.Move(summary, aside);
    Console.Error.WriteLine($"summary.tsv has another version's columns: moved to {Path.GetFileName(aside)}");
}
if (!File.Exists(summary)) File.WriteAllText(summary, summaryHeader + "\n");
string? curatorDir = opt.GetValueOrDefault("curator");
using var pride = new PrideArchiveClient();
using var pmc = new EuropePmcClient();
using var aws = new PmcOpenData();
double total = 0;
// A spending cap for a large run: no new deposit is started once the estimated spend reaches it (0: no cap). A deposit
// costs at most about $2 under the budget above, so the run ends within about $2 of the cap.
double maxUsd = double.Parse(opt.GetValueOrDefault("max-usd", "0"), inv);
foreach (string acc in accs)
{
    if (model is not null && maxUsd > 0 && total >= maxUsd)
    {
        Console.WriteLine($"stopped before {acc}: the estimated spend ${total:0.00} reached --max-usd {maxUsd:0.##}; a rerun with the same list reuses every reading already paid for");
        break;
    }
    var notes = new List<string>();
    try
    {
        var row = await Deposit.ReadAsync(acc, Path.Combine(outRoot, acc), model, flags.Contains("--refresh"), pride, pmc, aws, claude,
            (priceIn, priceOut, priceCache), budget, curatorDir, notes, Console.WriteLine, sfx);
        total += double.TryParse(row[Array.IndexOf(Deposit.Columns, "est_cost_usd")], NumberStyles.Float, inv, out double c) ? c : 0;
        File.AppendAllText(summary, string.Join('\t', row.Select(Clean)) + "\n");
        Console.WriteLine($"{acc}: " + string.Join(" | ", Deposit.Columns.Zip(row).Skip(2).Where(x => x.First != "notes" && x.Second.Length > 0).Select(x => $"{x.First} {x.Second}")));
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        // One deposit's failure never stops the list; it is recorded with its type and message.
        notes.Add($"FAILED {e.GetType().Name}: {e.Message}");
        var failed = new string[Deposit.Columns.Length];
        Array.Fill(failed, "");
        (failed[0], failed[1], failed[^1]) = (DateTime.UtcNow.ToString("o", inv), acc, string.Join("; ", notes));
        File.AppendAllText(summary, string.Join('\t', failed.Select(Clean)) + "\n");
        Console.WriteLine($"{acc}: FAILED {e.GetType().Name}: {e.Message}");
    }
}
Console.WriteLine($"estimated model cost this run: ${total:0.00} ({accs.Count} deposits)");
// The run's facts and guesses, rebuilt from every deposit folder (a rerun replaces them, never appends).
foreach (string name in new[] { Deposit.Name("facts.tsv", sfx), Deposit.Name("review.tsv", sfx) })
{
    var lines = new List<string>();
    foreach (string d in Directory.GetDirectories(outRoot, "PXD*").Order(StringComparer.Ordinal))
    {
        string f = Path.Combine(d, name);
        if (!File.Exists(f)) continue;
        var rows = File.ReadAllLines(f);
        if (lines.Count == 0 && rows.Length > 0) lines.Add("accession\t" + rows[0]);
        lines.AddRange(rows.Skip(1).Where(l => l.Length > 0).Select(l => Path.GetFileName(d) + "\t" + l));
    }
    File.WriteAllLines(Path.Combine(outRoot, name), lines);
}
return 0;

static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

internal sealed record Budget(int MaxImages, int MaxPdfPages, long MaxDepositFileBytes, long MaxImageBytes, long MaxAttachmentBytes);

internal static class Deposit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static readonly string[] Columns =
    {
        "utc", "accession", "raw_files", "pubmed", "pmcid", "paper_source", "figures", "supplement_files", "deposit_files", "sdrf_left_out", "tables",
        "prose_chars", "images_sent", "pdf_pages_sent", "est_input_tokens", "rule_claims", "model", "model_claims", "figure_claims",
        "transcripts", "rejected", "stop", "attempts", "input_tokens", "output_tokens", "cache_read_tokens", "est_cost_usd",
        "predicted_runs", "deposit_facts", "guesses", "curator_claims", "draft_rows", "validator_errors", "validator_warnings", "notes",
    };

    internal const string FactsHeader = "column\tvalue\tsource\tmethod\tconfidence\tlocator";

    /// <summary>
    /// An output's name under a mode suffix (<c>.independent</c>, or none): <c>draft.sdrf.tsv</c> becomes
    /// <c>draft.independent.sdrf.tsv</c>, <c>model.json</c> becomes <c>model.independent.json</c>.
    /// </summary>
    internal static string Name(string file, string sfx)
    {
        if (sfx.Length == 0) return file;
        if (file.EndsWith(".sdrf.tsv", StringComparison.Ordinal)) return file[..^".sdrf.tsv".Length] + sfx + ".sdrf.tsv";
        string ext = Path.GetExtension(file);
        return file[..^ext.Length] + sfx + ext;
    }

    internal static async Task<string[]> ReadAsync(string acc, string dir, string? model, bool refresh,
        PrideArchiveClient pride, EuropePmcClient pmc, PmcOpenData aws, AnthropicClient? claude, (double In, double Out, double Cache) price,
        Budget budget, string? curatorDir, List<string> notes, Action<string> log, string sfx = "")
    {
        bool independent = sfx.Length > 0;
        string N(string file) => Path.Combine(dir, Name(file, sfx));
        Directory.CreateDirectory(dir);
        var inputs = new List<InputRow>();
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

        // 2. The paper the record cites: Europe PMC first (open access), then the PMC Article Datasets on AWS for the figures,
        //    for supplements Europe PMC has not mirrored, and for the text of a TDM-licensed author manuscript.
        string paperDir = Path.Combine(dir, "paper"), suppDir = Path.Combine(dir, "supp"), figDir = Path.Combine(dir, "figs"), depDir = Path.Combine(dir, "deposit");
        EuropePmcArticle? article = null;
        string? paperXml = null;
        string paperSource = "";
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
                if (article.HasFullText)
                {
                    var (ft, path) = await pmc.TryDownloadFullTextXmlAsync(article, paperDir);
                    if (ft) { paperXml = path; paperSource = "Europe PMC"; }
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
                }
            }
        }
        catch (HttpRequestException e) { notes.Add($"Europe PMC unavailable ({e.Message}): read without its copy of the paper"); }
        catch (MzLibException e) { notes.Add($"Europe PMC refused ({e.Message}): read without its copy of the paper"); }

        (string Prefix, IReadOnlyList<string> Names)? oa = null;
        if (article is { PmcId.Length: > 0 })
        {
            try
            {
                oa = await aws.TryListAsync(article.PmcId);
                if (oa is { } o)
                {
                    var meta = await aws.TryMetadataAsync(o.Prefix);
                    if (meta != null) File.WriteAllText(Path.Combine(paperDir, $"{o.Prefix}.meta.json"), meta.ToString(Formatting.Indented));
                    string license = (string?)meta?["license_code"] ?? "?";
                    bool manuscript = meta?["is_manuscript"]?.Type == JTokenType.Boolean && meta["is_manuscript"]!.Value<bool>();
                    if (paperXml is null && o.Names.Contains($"{o.Prefix}.xml"))
                    {
                        string dest = Path.Combine(paperDir, $"{o.Prefix}.xml");
                        await aws.DownloadAsync(o.Prefix, $"{o.Prefix}.xml", dest);
                        paperXml = dest;
                        paperSource = $"PMC Article Datasets ({license}{(manuscript ? ", author manuscript" : "")})";
                    }
                    else if (paperSource.Length > 0) paperSource += $"; figures from the PMC Article Datasets ({license})";
                }
                else notes.Add($"{article.PmcId} is not in the PMC Article Datasets");
            }
            catch (HttpRequestException e) { notes.Add($"PMC Article Datasets unavailable ({e.Message})"); }
        }
        if (article is not null && paperXml is null) notes.Add($"{(article.PmcId.Length > 0 ? article.PmcId : "no PMC id")}: no open full text");

        string methods = "", extras = "";
        IReadOnlyList<JatsFigure> figures = Array.Empty<JatsFigure>();
        IReadOnlyList<string> suppHrefs = Array.Empty<string>();
        if (paperXml is not null)
        {
            string xml = File.ReadAllText(paperXml);
            methods = PublicationText.MethodsAndTables(xml);
            extras = Jats.AbstractLegendsAndSupplements(xml, out figures, out suppHrefs);
        }
        // The figure images, and the supplements Europe PMC did not give, by the names the JATS uses.
        var figureFiles = new List<(JatsFigure Figure, string Path)>();
        if (oa is { } od)
        {
            string? Find(string href) => href.Length == 0 ? null
                : od.Names.FirstOrDefault(n => n.Equals(href, StringComparison.OrdinalIgnoreCase))
                  ?? od.Names.Where(n => Path.GetFileNameWithoutExtension(n).Equals(href, StringComparison.OrdinalIgnoreCase) && SupplementContent.ImageMediaType(n) != null)
                       .OrderBy(n => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
            try
            {
                foreach (var f in figures)
                {
                    if (Find(f.Href) is not { } name) { inputs.Add(new InputRow("figure", f.Href, 0, $"{f.Label}: no image in the PMC Article Datasets")); continue; }
                    string dest = Path.Combine(figDir, name);
                    await aws.DownloadAsync(od.Prefix, name, dest);
                    figureFiles.Add((f, dest));
                }
                // Every supplement the article lists, plus any other file of the version that is not the article itself or a
                // figure (PMC6001795's two supplementary-figure TIFFs): fetched when Europe PMC's zip lacks it or gave it empty
                // (PMC8428053: Europe PMC gave 1 of its 17 supplements, and that one with 0 bytes).
                var figureNames = figureFiles.Select(x => Path.GetFileName(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var own = new[] { ".xml", ".json", ".txt", ".pdf" }.Select(e => od.Prefix + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var wanted = suppHrefs.Select(Find).OfType<string>()
                    .Concat(od.Names.Where(n => !own.Contains(n) && !figureNames.Contains(n) && SupplementContent.ImageMediaType(n) == null))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int fromAws = 0;
                foreach (string name in wanted)
                {
                    string dest = Path.Combine(suppDir, name);
                    if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                    if (File.Exists(dest)) File.Delete(dest);
                    await aws.DownloadAsync(od.Prefix, name, dest);
                    fromAws++;
                }
                if (fromAws > 0) notes.Add($"{fromAws} supplement file(s) from the PMC Article Datasets");
            }
            catch (HttpRequestException e) { notes.Add($"PMC Article Datasets download failed ({e.Message})"); }
        }

        // 2b. The deposit's own files in PRIDE (sample sheets, design templates, README, SDRF).
        foreach (var f in SupplementContent.DepositFiles(files, budget.MaxDepositFileBytes, inputs, leaveOutSdrf: independent))
        {
            try { await pride.DownloadFileAsync(f, depDir, overwrite: false); }
            catch (Exception e) when (e is HttpRequestException or NotSupportedException or IOException)
            { inputs.Add(new InputRow("deposit file", f.FileName, f.FileSizeBytes, $"not read: download failed ({e.GetType().Name}: {e.Message})")); }
        }

        // 3. Every supplement and deposit file: its tables (for the rules and the model), its text, its images, its PDF pages.
        var tables = new List<SupplementTable>();
        var prose = new StringBuilder();
        var pdfText = new StringBuilder();   // the text of PDFs shown as documents: in the quote check, not repeated in the prompt
        var images = new List<Attachment>();
        var pdfs = new List<(string Kind, string Path)>();
        int suppFiles = 0, depFiles = 0;
        foreach (var (folder, kind) in new[] { (suppDir, "supplement"), (depDir, "deposit file") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith('_') && !f.EndsWith(".partial", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(f), ext = Path.GetExtension(f).ToLowerInvariant();
                // --independent: an SDRF already in the folder (a supplement, or a deposit file fetched by an earlier run) is
                // left out too, by its name here and by its columns below. Said once per file in inputs.tsv.
                if (independent && SupplementContent.NamedLikeSdrf(name))
                {
                    if (!inputs.Any(i => i.Name == name && i.UsedAs == SupplementContent.LeftOut))
                        inputs.Add(new InputRow(kind, name, new FileInfo(f).Length, SupplementContent.LeftOut));
                    continue;
                }
                if (kind == "supplement") suppFiles++; else depFiles++;
                var used = new List<string>();
                int nTables = 0;
                if (SupplementContent.IsImage(name))
                {
                    if (SupplementContent.Sendable(File.ReadAllBytes(f), name, budget.MaxImageBytes, out string why) is { } img)
                        images.Add(new Attachment($"[{(kind == "supplement" ? "supp" : "deposit")}:{name}]", "image", img.MediaType, img.Bytes,
                            $"is the image file {name} among the {kind}s{(img.Note.Length > 0 ? $" ({img.Note})" : "")}."));
                    else inputs.Add(new InputRow(kind, name, new FileInfo(f).Length, $"image not shown: {why}"));
                    continue;
                }
                IReadOnlyList<SupplementTable> t;
                try { t = SupplementTableReader.Read(f); }
                catch (Exception e) when (e is not OperationCanceledException) { t = Array.Empty<SupplementTable>(); used.Add($"no tables ({e.GetType().Name}: {e.Message})"); }
                if (independent && t.Any(SupplementContent.ShapedLikeSdrf))
                {
                    if (kind == "supplement") suppFiles--; else depFiles--;
                    inputs.Add(new InputRow(kind, name, new FileInfo(f).Length, SupplementContent.LeftOut + "; known by its source name and comment[data file] columns"));
                    continue;
                }
                tables.AddRange(t);
                nTables = t.Count;
                if (nTables > 0) used.Add($"{nTables} table(s)");
                try
                {
                    if (ext == ".docx")
                    {
                        var (p, imgs) = SupplementContent.Docx(f);
                        if (p.Length > 0) { prose.Append($"### {name}\n{p}\n"); used.Add($"{p.Length} characters of text"); }
                        int shown = 0;
                        foreach (var im in imgs)
                        {
                            if (SupplementContent.Sendable(im.Bytes, im.Name, budget.MaxImageBytes, out string why) is { } img)
                            {
                                images.Add(new Attachment($"[supp:{name}#{im.Name}]", "image", img.MediaType, img.Bytes, $"is an image inside {name}{(img.Note.Length > 0 ? $" ({img.Note})" : "")}."));
                                shown++;
                            }
                            else used.Add($"image {im.Name} not shown: {why}");
                        }
                        if (shown > 0) used.Add($"{shown} image(s)");
                    }
                    else if (ext is ".txt" or ".md" && (nTables == 0 || name.Contains("readme", StringComparison.OrdinalIgnoreCase)))
                    {
                        string p = File.ReadAllText(f);
                        if (p.Length > 20_000) p = p[..20_000] + "\n[cut at 20,000 characters]";
                        prose.Append($"### {name}\n{p}\n");
                        used.Add($"{p.Length} characters of text");
                    }
                    else if (ext == ".pdf") pdfs.Add((kind, f));
                }
                catch (Exception e) when (e is not OperationCanceledException) { used.Add($"text unreadable ({e.GetType().Name}: {e.Message})"); }
                inputs.Add(new InputRow(kind, name, new FileInfo(f).Length, used.Count > 0 ? string.Join("; ", used) : "nothing read"));
            }
        }
        var rules = SampleEvidenceExtractor.Extract(tables, raw);
        SdrfEvidenceFile.Write(N("evidence.rules.tsv"), rules);

        // 3b. What the model is shown beyond text, within the budget: the paper's figures first, then supplement images, then
        //     PDF pages. A PDF that does not fit is given as text instead.
        var attachments = new List<Attachment>();
        long bytes = 0;
        bool Fits(byte[] b, int imageCount) => b.LongLength <= budget.MaxImageBytes && imageCount < budget.MaxImages && bytes + b.LongLength <= budget.MaxAttachmentBytes;
        foreach (var (fig, path) in figureFiles)
        {
            string label = $"[fig:{fig.Id}]";
            if (SupplementContent.Sendable(File.ReadAllBytes(path), path, budget.MaxImageBytes, out string why) is not { } img)
            { inputs.Add(new InputRow("figure", Path.GetFileName(path), new FileInfo(path).Length, $"not shown: {why}")); continue; }
            if (!Fits(img.Bytes, attachments.Count(a => a.Kind == "image")))
            { inputs.Add(new InputRow("figure", Path.GetFileName(path), img.Bytes.LongLength, "not shown: over the image budget")); continue; }
            attachments.Add(new Attachment(label, "image", img.MediaType, img.Bytes, $"is {fig.Label} of the paper (its legend is under Figure legends){(img.Note.Length > 0 ? $"; {img.Note}" : "")}."));
            bytes += img.Bytes.LongLength;
            inputs.Add(new InputRow("figure", Path.GetFileName(path), img.Bytes.LongLength, $"image {label}{(img.Note.Length > 0 ? $" ({img.Note})" : "")}"));
        }
        foreach (var im in images)
        {
            if (!Fits(im.Bytes, attachments.Count(a => a.Kind == "image")))
            { inputs.Add(new InputRow("supplement image", im.Label, im.Bytes.LongLength, "not shown: over the image budget or 5 MB")); continue; }
            attachments.Add(im);
            bytes += im.Bytes.LongLength;
            inputs.Add(new InputRow("supplement image", im.Label, im.Bytes.LongLength, $"image {im.Label}"));
        }
        int pagesLeft = budget.MaxPdfPages, pagesSent = 0;
        foreach (var (kind, path) in pdfs)
        {
            string name = Path.GetFileName(path), label = $"[{(kind == "supplement" ? "supp" : "deposit")}:{name}]";
            string text = "";
            try { text = SupplementContent.PdfText(path); } catch (Exception e) when (e is not OperationCanceledException) { notes.Add($"{name}: no PDF text ({e.GetType().Name})"); }
            (byte[] Bytes, int Sent, int Total, string Pages)? sel = null;
            try { sel = SupplementContent.SelectPages(path, pagesLeft); }
            catch (Exception e) when (e is not OperationCanceledException) { notes.Add($"{name}: pages not copied ({e.GetType().Name}: {e.Message})"); }
            if (sel is { } s && bytes + s.Bytes.LongLength <= budget.MaxAttachmentBytes)
            {
                attachments.Add(new Attachment(label, "pdf", "application/pdf", s.Bytes, $"is {kind} {name}, pages {s.Pages} of {s.Total}.", s.Sent));
                bytes += s.Bytes.LongLength;
                pagesLeft -= s.Sent;
                pagesSent += s.Sent;
                pdfText.Append($"### {name}\n{text}\n");
                inputs.Add(new InputRow(kind + " PDF", name, new FileInfo(path).Length, $"document {label}: pages {s.Pages} of {s.Total}"));
            }
            else
            {
                if (text.Length > 0) prose.Append($"### {name} (PDF text)\n{text}\n");
                inputs.Add(new InputRow(kind + " PDF", name, new FileInfo(path).Length, $"text only ({text.Length} characters): over the page or size budget"));
            }
        }

        // A converted image is kept as shown, so a reviewer sees what the model saw (the originals are in supp\ and figs\).
        foreach (var a in attachments.Where(a => a.Kind == "image" && a.Describe.Contains("converted to", StringComparison.Ordinal)))
        {
            Directory.CreateDirectory(Path.Combine(dir, "shown"));
            string safe = string.Concat(a.Label.Trim('[', ']').Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '#' ? '_' : ch));
            File.WriteAllBytes(Path.Combine(dir, "shown", safe + (a.MediaType == "image/png" ? ".png" : ".jpg")), a.Bytes);
        }

        // 4. The model reading (opt-in): everything above, each claim quoted. Paid once, reused on a rerun.
        string paperText = methods + extras;
        var input = new PublicationInput(acc, record, paperText + "\n" + prose + "\n" + pdfText, tables, raw, rules);
        string prompt = RichReader.Prompt(input, paperText, prose.ToString(), attachments);
        File.WriteAllText(N("prompt.txt"), prompt);
        File.WriteAllLines(N("inputs.tsv"), new[] { "kind\tname\tbytes\tused_as" }
            .Concat(inputs.Select(i => string.Join('\t', i.Kind, i.Name, i.Bytes.ToString(Inv), i.UsedAs))));
        int imagesSent = attachments.Count(a => a.Kind == "image");
        // Rough: ~4 characters a token for text; up to ~4,800 a full-size image; ~3,000 a PDF page (its text and image).
        long estTokens = prompt.Length / 4 + imagesSent * 4_800L + pagesSent * 3_000L;

        string modelTsv = N("evidence.model.tsv"), modelJson = N("model.json");
        IReadOnlyList<SdrfEvidence> modelEvidence = Array.Empty<SdrfEvidence>();
        string stop = "", rejected = "", inTok = "", outTok = "", cacheTok = "", cost = "", predicted = "", figureClaims = "", transcripts = "", attempts = "";
        string modelUsed = model ?? "none";
        {
            // A reading already paid for is reused, with or without --no-model (which only forbids new calls).
            if (File.Exists(modelTsv) && File.Exists(modelJson) && !refresh)
            {
                modelEvidence = SdrfEvidenceFile.Read(modelTsv);
                var j = JObject.Parse(File.ReadAllText(modelJson));
                modelUsed = (string?)j["model"] ?? modelUsed;
                stop = $"{j["stop"]} (reused)";
                rejected = j["rejected"] is JArray rj ? rj.Count.ToString(Inv) : "";
                // "design": null is a JValue, not a C# null: indexing it throws (a reading with no stated design).
                predicted = j["design"] is JObject jd ? (string?)jd["predicted_runs"] ?? "" : "";
                figureClaims = (string?)j["figure_claims"] ?? "";
                transcripts = (string?)j["transcripts"] ?? "";
                cost = "0";
            }
            else if (claude is not null && model is not null)
            {
                var rr = await RichReader.ReadAsync(claude, model, input, prompt, attachments, log);
                var r = rr.Result;
                modelEvidence = r.Evidence;
                SdrfEvidenceFile.Write(modelTsv, r.Evidence);
                File.WriteAllLines(N("transcripts.tsv"), new[] { "label\ttext" }.Concat(rr.Transcripts.Select(t => $"{t.Label}\t{Clean(t.Text)}")));
                // The model's own answer, so a rejected claim's quote can be read (the second run's rejections could not).
                File.WriteAllText(N("model.raw.json"), rr.Raw);
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
                    ["utc"] = DateTime.UtcNow.ToString("o", Inv), ["model"] = model, ["independent"] = independent, ["stop"] = r.StopReason, ["attempts"] = rr.Attempts,
                    ["input_tokens"] = r.InputTokens, ["output_tokens"] = r.OutputTokens, ["cache_read_tokens"] = r.CacheReadTokens,
                    ["est_cost_usd"] = Math.Round(usd, 4), ["claims"] = r.Evidence.Count, ["figure_claims"] = rr.FigureClaims,
                    ["transcripts"] = rr.Transcripts.Count, ["repaired_quotes"] = rr.Repaired, ["rejected"] = new JArray(r.Rejected), ["design"] = design,
                    ["paper"] = paperXml is not null ? Path.GetFileName(paperXml) : null, ["paper_source"] = paperSource, ["tables"] = tables.Count,
                    ["images"] = imagesSent, ["pdf_pages"] = pagesSent,
                    ["attachments"] = new JArray(attachments.Select(a => $"{a.Label} {a.Kind} {a.Bytes.LongLength} bytes{(a.Pages > 0 ? $", {a.Pages} pages" : "")}")),
                }.ToString(Formatting.Indented));
                stop = r.StopReason;
                rejected = r.Rejected.Count.ToString(Inv);
                (inTok, outTok, cacheTok) = (r.InputTokens.ToString(Inv), r.OutputTokens.ToString(Inv), r.CacheReadTokens.ToString(Inv));
                cost = usd.ToString("0.0000", Inv);
                predicted = r.Design?.PredictedRuns.ToString(Inv) ?? "";
                figureClaims = rr.FigureClaims.ToString(Inv);
                transcripts = rr.Transcripts.Count.ToString(Inv);
                attempts = rr.Attempts.ToString(Inv);
            }
        }

        // 5. Two drafts: as today (the record and file names), and with the evidence.
        SdrfDocument Draft(IEnumerable<SdrfEvidence>? evidence, string file, out int errors, out int warnings)
        {
            var draft = SdrfDrafter.Draft(project, raw, evidence);
            var doc = SdrfDrafter.ToDocument(draft, acc);
            doc.WriteResults(Path.Combine(dir, file));
            if (evidence is not null && draft.EvidenceNotes.Count > 0)
                File.WriteAllLines(N("evidence.notes.tsv"), new[] { "data_file\tcolumn\tdraft_value\tevidence_value\twhy" }
                    .Concat(draft.EvidenceNotes.Select(n => string.Join('\t', n.DataFile, n.Column, n.DraftValue, n.EvidenceValue, n.Why))));
            var v = SdrfValidator.Validate(doc);
            errors = v.Errors.Count();
            warnings = v.Warnings.Count();
            return doc;
        }
        Draft(null, "draft.noevidence.sdrf.tsv", out _, out _);
        // What a curator confirmed (the question's own data, e.g. aging's evidence\<acc>.evidence.tsv): rows copied from
        // review.tsv with confidence certain, which the drafter applies like any certain claim.
        IReadOnlyList<SdrfEvidence> curated = Array.Empty<SdrfEvidence>();
        if (curatorDir is not null && File.Exists(Path.Combine(curatorDir, $"{acc}.evidence.tsv")))
            curated = SdrfEvidenceFile.Read(Path.Combine(curatorDir, $"{acc}.evidence.tsv"));
        var all = rules.Concat(modelEvidence).Concat(curated).ToList();
        var withEvidence = Draft(all, Name("draft.sdrf.tsv", sfx), out int err, out int warn);

        // 5b. The guesses, for a curator to confirm or not (the drafter never applies one): the evidence file's own format.
        var guesses = rules.Concat(modelEvidence).Where(e => e.Confidence == SdrfEvidenceConfidence.Guess).ToList();
        SdrfEvidenceFile.Write(N("review.tsv"), guesses);

        // 6. The deposit-wide facts (the user: "deposit wide facts are important"): every claim about the whole deposit.
        var facts = all.Where(e => e.DataFile.Length == 0 && e.DataFilePattern.Length == 0 && e.Label.Length == 0)
            .OrderBy(e => e.Column, StringComparer.Ordinal).ThenBy(e => e.Confidence)
            .Select(e => string.Join('\t', new[] { e.Column, e.Value, e.Source, e.Method, e.Confidence.ToString(), e.Locator }.Select(Clean))).ToList();
        File.WriteAllLines(N("facts.tsv"), new[] { FactsHeader }.Concat(facts));

        var row = new Dictionary<string, string>
        {
            ["utc"] = DateTime.UtcNow.ToString("o", Inv), ["accession"] = acc, ["raw_files"] = raw.Count.ToString(Inv),
            ["pubmed"] = article?.PubMedId ?? "", ["pmcid"] = article?.PmcId ?? "", ["paper_source"] = paperSource,
            ["figures"] = $"{figureFiles.Count}/{figures.Count}", ["supplement_files"] = suppFiles.ToString(Inv), ["deposit_files"] = depFiles.ToString(Inv),
            ["sdrf_left_out"] = inputs.Where(i => i.UsedAs.StartsWith(SupplementContent.LeftOut, StringComparison.Ordinal)).Select(i => i.Name).Distinct(StringComparer.Ordinal).Count().ToString(Inv),
            ["tables"] = tables.Count.ToString(Inv), ["prose_chars"] = prose.Length.ToString(Inv), ["images_sent"] = imagesSent.ToString(Inv),
            ["pdf_pages_sent"] = pagesSent.ToString(Inv), ["est_input_tokens"] = estTokens.ToString(Inv), ["rule_claims"] = rules.Count.ToString(Inv),
            ["model"] = modelUsed, ["model_claims"] = modelEvidence.Count.ToString(Inv), ["guesses"] = guesses.Count.ToString(Inv),
            ["curator_claims"] = curated.Count.ToString(Inv), ["figure_claims"] = figureClaims, ["transcripts"] = transcripts,
            ["rejected"] = rejected, ["stop"] = stop, ["attempts"] = attempts, ["input_tokens"] = inTok, ["output_tokens"] = outTok,
            ["cache_read_tokens"] = cacheTok, ["est_cost_usd"] = cost, ["predicted_runs"] = predicted, ["deposit_facts"] = facts.Count.ToString(Inv),
            ["draft_rows"] = withEvidence.Results.Count.ToString(Inv), ["validator_errors"] = err.ToString(Inv), ["validator_warnings"] = warn.ToString(Inv),
            ["notes"] = string.Join("; ", notes),
        };
        return Columns.Select(c => row[c]).ToArray();
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
