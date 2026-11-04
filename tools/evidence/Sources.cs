// The publication inputs beyond the paper's methods and the supplement tables (the user, 2026-11-04: "don't forget about
// supplements. they are often key"; "include information in figures too"; sdrf G38 for the figure part):
//   - the deposit's OWN supplementary files in PRIDE (sample sheets, design templates, README, an SDRF), which the pilot
//     never fetched: 10 of its 15 deposits carry some, and 9 of them are exactly the deposits whose paper is not open;
//   - the paper's abstract, figure legends and supplementary-material captions (the pilot read methods sections only);
//   - the paper's figure images and the full text of TDM-licensed author manuscripts, from the PMC Article Datasets on AWS
//     (s3://pmc-oa-opendata; sdrf G38 found europepmc.org/.../bin/ answers 403 to scripts);
//   - the prose and embedded images of supplementary .docx files, and supplementary PDFs as documents (Claude sees each
//     page's text and its figures), cut to their figure and table pages when long.
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using UsefulProteomicsDatabases;

/// <summary>An image or a PDF the model is shown, with the label its claims cite (<c>[fig:F2]</c>, <c>[supp:mmc1.pdf]</c>).</summary>
internal sealed record Attachment(string Label, string Kind, string MediaType, byte[] Bytes, string Describe, int Pages = 0);

/// <summary>One line of DIR\&lt;acc&gt;\inputs.tsv: what was found and what was done with it.</summary>
internal sealed record InputRow(string Kind, string Name, long Bytes, string UsedAs);

/// <summary>A figure of a JATS article: its id, label ("Figure 2"), caption text and graphic file name.</summary>
internal sealed record JatsFigure(string Id, string Label, string Caption, string Href);

internal static class Jats
{
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>The abstract(s), figure legends and supplementary-material captions, as headed plain text.</summary>
    internal static string AbstractLegendsAndSupplements(string xml, out IReadOnlyList<JatsFigure> figures, out IReadOnlyList<string> supplementHrefs)
    {
        var doc = XDocument.Parse(xml);
        var sb = new StringBuilder();
        foreach (var a in doc.Descendants().Where(e => e.Name.LocalName == "abstract"))
        {
            string type = (string?)a.Attribute("abstract-type") ?? "";
            sb.Append(type.Length > 0 ? $"## Abstract ({type})\n" : "## Abstract\n").Append(Clean(a.Value)).Append("\n\n");
        }
        var figs = new List<JatsFigure>();
        foreach (var f in doc.Descendants().Where(e => e.Name.LocalName == "fig"))
        {
            string id = (string?)f.Attribute("id") ?? $"F{figs.Count + 1}";
            string label = Clean(f.Elements().FirstOrDefault(e => e.Name.LocalName == "label")?.Value ?? id);
            string caption = Clean(f.Elements().FirstOrDefault(e => e.Name.LocalName == "caption")?.Value ?? "");
            string href = f.Descendants().Where(e => e.Name.LocalName == "graphic").Select(g => (string?)g.Attribute(XLink + "href")).FirstOrDefault(h => h != null) ?? "";
            figs.Add(new JatsFigure(id, label, caption, href));
        }
        if (figs.Count > 0)
        {
            sb.Append("## Figure legends\n");
            foreach (var f in figs) sb.Append($"[fig:{f.Id}] {f.Label} {f.Caption}\n");
            sb.Append('\n');
        }
        var supp = new List<string>();
        var captions = new StringBuilder();
        foreach (var s in doc.Descendants().Where(e => e.Name.LocalName == "supplementary-material"))
        {
            string label = Clean(s.Elements().FirstOrDefault(e => e.Name.LocalName == "label")?.Value ?? "");
            string caption = Clean(s.Descendants().FirstOrDefault(e => e.Name.LocalName == "caption")?.Value ?? "");
            var hrefs = s.DescendantsAndSelf().Select(e => (string?)e.Attribute(XLink + "href")).Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).ToList();
            supp.AddRange(hrefs);
            if (label.Length + caption.Length > 0)
                captions.Append($"{(hrefs.Count > 0 ? $"[{string.Join(", ", hrefs)}] " : "")}{label} {caption}".Trim()).Append('\n');
        }
        if (captions.Length > 0) sb.Append("## Supplementary material captions\n").Append(captions).Append('\n');
        figures = figs;
        supplementHrefs = supp.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return sb.ToString();
    }

    internal static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}

/// <summary>
/// The PMC Article Datasets on AWS (public, no account): each article's latest version under <c>PMCnnn.v/</c>, with its
/// JATS XML, plain text, metadata JSON (license_code, is_manuscript) and, for open-access articles, its figure images
/// and supplements. Author manuscripts are there with license_code TDM (text and data mining), XML and text only.
/// </summary>
internal sealed class PmcOpenData : IDisposable
{
    private const string Bucket = "https://pmc-oa-opendata.s3.amazonaws.com";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    internal PmcOpenData() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("pxr-evidence/1.0 (PXReprise; research)");

    public void Dispose() => _http.Dispose();

    /// <summary>The latest version's prefix and object names, or null when the article is not in the datasets.</summary>
    internal async Task<(string Prefix, IReadOnlyList<string> Names)?> TryListAsync(string pmcid, CancellationToken ct = default)
    {
        string xml = await _http.GetStringAsync($"{Bucket}/?list-type=2&prefix={Uri.EscapeDataString(pmcid)}.", ct).ConfigureAwait(false);
        var keys = Regex.Matches(xml, "<Key>([^<]+)</Key>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        if (keys.Count == 0) return null;
        // PMC4802414.1/..., PMC4802414.2/...: the highest version is the current one.
        string prefix = keys.Select(k => k.Split('/')[0]).Distinct()
            .OrderBy(p => int.TryParse(p[(p.LastIndexOf('.') + 1)..], out int v) ? v : 0).Last();
        return (prefix, keys.Where(k => k.StartsWith(prefix + "/", StringComparison.Ordinal)).Select(k => k[(prefix.Length + 1)..]).ToList());
    }

    /// <summary>Downloads one object of a version to <paramref name="dest"/> unless it is already there.</summary>
    internal async Task DownloadAsync(string prefix, string name, string dest, CancellationToken ct = default)
    {
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        byte[] data = await _http.GetByteArrayAsync($"{Bucket}/{prefix}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(dest + ".partial", data, ct).ConfigureAwait(false);
        File.Move(dest + ".partial", dest, overwrite: true);
    }

    /// <summary>The version's metadata JSON (license_code, is_pmc_openaccess, is_manuscript), or null.</summary>
    internal async Task<JObject?> TryMetadataAsync(string prefix, CancellationToken ct = default)
    {
        try { return JObject.Parse(await _http.GetStringAsync($"{Bucket}/{prefix}/{prefix}.json", ct).ConfigureAwait(false)); }
        catch (HttpRequestException) { return null; }
    }
}

internal static class SupplementContent
{
    /// <summary>Extensions the table reader reads, and so the ones worth fetching from a deposit.</summary>
    internal static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xlsm", ".docx", ".csv", ".tsv", ".txt", ".pdf", ".md" };

    private static readonly HashSet<string> SkippedCategories = new(StringComparer.OrdinalIgnoreCase) { "RAW", "PEAK", "FASTA", "SPECTRUM_LIBRARY" };

    /// <summary>Unread formats worth a line in inputs.tsv: they may hold a sample sheet (an archive, an old Office file).</summary>
    private static readonly HashSet<string> NoteworthyUnread = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".tgz", ".xls", ".doc", ".ppt", ".pptx", ".odt", ".ods", ".rtf" };

    /// <summary>
    /// The deposit's own files worth reading: a readable extension, at most <paramref name="maxBytes"/>, not spectra.
    /// A deposit's sample sheet, design templates, README and SDRF live here (PXD002467: six MaxQuant design templates and a
    /// community SDRF; the Jarome deposits: a Proteome Discoverer export whose headers name each file's group and sample).
    /// </summary>
    internal static List<PrideArchiveFile> DepositFiles(IEnumerable<PrideArchiveFile> files, long maxBytes, List<InputRow> inputs)
    {
        var keep = new List<PrideArchiveFile>();
        foreach (var f in files)
        {
            string cat = f.FileCategory?.Value ?? "";
            if (SkippedCategories.Contains(cat)) continue;
            string ext = f.FileName.EndsWith(".sdrf.tsv", StringComparison.OrdinalIgnoreCase) ? ".tsv" : Path.GetExtension(f.FileName);
            if (!Readable.Contains(ext))
            {
                if (NoteworthyUnread.Contains(ext)) inputs.Add(new InputRow("deposit file", f.FileName, f.FileSizeBytes, $"not read: {cat} {ext} has no reader"));
                continue;
            }
            if (f.FileSizeBytes > maxBytes) { inputs.Add(new InputRow("deposit file", f.FileName, f.FileSizeBytes, $"not read: larger than {maxBytes / 1_000_000} MB")); continue; }
            keep.Add(f);
        }
        return keep;
    }

    /// <summary>A .docx's paragraphs outside its tables (the tables are read as tables), and its embedded images.</summary>
    internal static (string Prose, List<(string Name, byte[] Bytes)> Images) Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var entry = zip.GetEntry("word/document.xml");
        if (entry != null)
        {
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p" && !e.Ancestors().Any(a => a.Name.LocalName == "tbl")))
            {
                string line = string.Concat(p.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)).Trim();
                if (line.Length > 0) sb.Append(line).Append('\n');
            }
        }
        var images = new List<(string, byte[])>();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase) && IsImage(e.Name)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            images.Add((e.Name, ms.ToArray()));
        }
        return (sb.ToString(), images);
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".tif", ".tiff", ".bmp", ".emf", ".wmf" };

    /// <summary>An image file, sendable as is or after conversion (<see cref="Sendable"/>).</summary>
    internal static bool IsImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// An image the API takes (JPEG, PNG, GIF, WebP; at most <paramref name="maxBytes"/>): as is when it already is one,
    /// else converted on Windows with System.Drawing (TIFF and BMP figures, EMF/WMF drawings in Word files) and shrunk to
    /// <paramref name="maxEdge"/> pixels on the long edge when too large. Null, with the reason, when it cannot be made one.
    /// </summary>
    internal static (byte[] Bytes, string MediaType, string Note)? Sendable(byte[] data, string name, long maxBytes, out string why, int maxEdge = 2576)
    {
        why = "";
        if (ImageMediaType(name) is { } media && data.LongLength <= maxBytes) return (data, media, "");
        if (!OperatingSystem.IsWindows()) { why = $"{Path.GetExtension(name)} needs converting, which this tool does only on Windows"; return null; }
        try { return Convert(data, maxBytes, maxEdge); }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException)
        { why = $"could not convert ({e.GetType().Name}: {e.Message})"; return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (byte[], string, string)? Convert(byte[] data, long maxBytes, int maxEdge)
    {
        using var input = new MemoryStream(data);
        using var image = System.Drawing.Image.FromStream(input);
        int frames = 1;
        try { frames = image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Page); } catch (ExternalException) { }
        // A metafile has no pixels: draw it at a readable size. A bitmap keeps its size unless it is too large.
        double scale = Math.Min(1.0, (double)maxEdge / Math.Max(image.Width, image.Height));
        if (image is System.Drawing.Imaging.Metafile) scale = Math.Min((double)maxEdge, 2000.0) / Math.Max(image.Width, image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, w, h);
        }
        string note = $"converted to {w}x{h}{(frames > 1 ? $", page 1 of {frames}" : "")}";
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        if (png.Length <= maxBytes) return (png.ToArray(), "image/png", note);
        using var jpg = new MemoryStream();
        bmp.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg);
        return jpg.Length <= maxBytes ? (jpg.ToArray(), "image/jpeg", note + " (JPEG)") : null;
    }

    /// <summary>A PDF's words, page by page ("[p.3] ..."), the way the table reader takes them.</summary>
    internal static string PdfText(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.Append($"[p.{page.Number}] ").Append(string.Join(" ", page.GetWords().Select(w => w.Text))).Append('\n');
        return sb.ToString();
    }

    private static readonly Regex FigureOrTablePage = new(@"\b(fig(ure)?|table)\s*\.?\s*S?\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The PDF to show the model: whole when it has at most <paramref name="maxPages"/> pages; otherwise its pages with an
    /// image or a figure/table caption, the first <paramref name="maxPages"/> of them (copied into a new PDF). Null when
    /// nothing is worth showing or the PDF cannot be copied.
    /// </summary>
    internal static (byte[] Bytes, int Sent, int Total, string Pages)? SelectPages(string path, int maxPages)
    {
        if (maxPages <= 0) return null;
        using var doc = PdfDocument.Open(path);
        int total = doc.NumberOfPages;
        if (total <= maxPages) return (File.ReadAllBytes(path), total, total, $"1-{total}");
        var keep = doc.GetPages().Where(p => p.GetImages().Any() || FigureOrTablePage.IsMatch(p.Text)).Select(p => p.Number).Take(maxPages).ToList();
        if (keep.Count == 0) return null;
        var builder = new PdfDocumentBuilder();
        foreach (int k in keep) builder.AddPage(doc, k);
        byte[] built = builder.Build();
        // A copy that does not read back whole would fail the deposit's whole request: give its text instead.
        using (var check = PdfDocument.Open(built))
            if (check.NumberOfPages != keep.Count) return null;
        return (built, keep.Count, total, string.Join(",", keep));
    }

    internal static string? ImageMediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };
}
