using DataRepo.Bundle;

namespace DataRepo.Site;

/// <summary><c>datarepo site &lt;catalog.duckdb&gt; --out &lt;dir&gt; [options]</c>: the command line of
/// <c>cli.py</c>'s <c>site</c> subcommand (<c>cmd_site</c>), its options and its printed report.</summary>
/// <remarks>Parsed the way argparse parses it: <c>--name value</c> or <c>--name=value</c>, a unique prefix of a
/// long option, <c>--keyword</c> repeatable, and a usage error exits 2. Until <c>DataRepo.Cli</c> exists
/// (phase 5) this is reached through the parity tool's <c>site</c> verb.</remarks>
public static class SiteCommand
{
    public const string Usage =
        "usage: datarepo site [-h] --out OUT [--title TITLE] [--base-url BASE_URL] [--data-url DATA_URL]\n" +
        "                     [--notice NOTICE] [--about ABOUT] [--purpose TEXT] [--keyword WORD]\n" +
        "                     catalog";

    /// <summary>Every option, with argparse's help text.</summary>
    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>
    {
        ["--out"] = "an empty directory, or a site written before",
        ["--title"] = "the site's name; default from the catalog's instance",
        ["--base-url"] = "where the site will be served, for absolute links and the sitemap",
        ["--data-url"] = "where the bundle store is served; enables downloads and croissant.json",
        ["--notice"] = "a banner for the top of every page and of llms.txt",
        ["--about"] = "a Markdown file: the front page's overview of the project",
        ["--purpose"] = "the question this instance serves, ending \"for questions about ...\"; " +
                        "e.g. \"how organelle proteomes change with age\". Without it no page claims one",
        ["--keyword"] = "a schema.org keyword for every dataset page, e.g. aging; repeatable",
    };

    /// <summary>Run the command; returns the process exit code (0 written, 1 refused, 2 usage).</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        string? catalog = null;
        var values = new Dictionary<string, string>();
        var keywords = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                stdout.WriteLine(Usage);
                return 0;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var eq = arg.IndexOf('=');
                var given = eq >= 0 ? arg[..eq] : arg;
                var matches = Options.Keys.Where(o => o == given).ToList();
                if (matches.Count == 0) matches = Options.Keys.Where(o => o.StartsWith(given, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1)
                {
                    return Error(stderr, matches.Count == 0
                        ? $"unrecognized arguments: {arg}"
                        : $"ambiguous option: {given} could match {string.Join(", ", matches)}");
                }
                string value;
                if (eq >= 0) value = arg[(eq + 1)..];
                else if (i + 1 < args.Count && !(args[i + 1].StartsWith('-') && args[i + 1].Length > 1)) value = args[++i];
                else return Error(stderr, $"argument {matches[0]}: expected one argument");
                if (matches[0] == "--keyword") keywords.Add(value);
                else values[matches[0]] = value;
            }
            else if (catalog is null)
            {
                catalog = arg;
            }
            else
            {
                return Error(stderr, $"unrecognized arguments: {arg}");
            }
        }
        var missing = new List<string>();
        if (!values.ContainsKey("--out")) missing.Add("--out");
        if (catalog is null) missing.Add("catalog");
        if (missing.Count > 0) return Error(stderr, $"the following arguments are required: {string.Join(", ", missing)}");

        try
        {
            var result = SiteGenerator.BuildSite(catalog!, values["--out"], new SiteOptions(
                Title: values.GetValueOrDefault("--title"),
                BaseUrl: values.GetValueOrDefault("--base-url"),
                DataUrl: values.GetValueOrDefault("--data-url"),
                Notice: values.GetValueOrDefault("--notice"),
                About: values.TryGetValue("--about", out var about) ? ReadText(about) : null,
                Purpose: values.GetValueOrDefault("--purpose"),
                Keywords: keywords));
            stdout.WriteLine($"site     {result.Out}");
            stdout.WriteLine($"  catalog  {result.CatalogId}");
            var pages = result.Files.Count(n => n.StartsWith("datasets/", StringComparison.Ordinal) && n.EndsWith(".html", StringComparison.Ordinal));
            stdout.WriteLine($"  wrote    {result.Files.Count} files, {pages} dataset page{(pages == 1 ? "" : "s")}");
            foreach (var (name, reason) in result.Skipped.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                stdout.WriteLine($"  skipped  {name}: {reason}");
            foreach (var warning in result.Warnings)
                stdout.WriteLine($"  WARN     {warning}");
            return 0;
        }
        catch (DataRepoException ex)
        {
            stderr.WriteLine($"datarepo: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Python's <c>Path.read_text(encoding="utf-8")</c>: universal newlines, so a CRLF file reads as
    /// LF and the page is the same whichever OS saved the Markdown.</summary>
    public static string ReadText(string path)
    {
        var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static int Error(TextWriter stderr, string message)
    {
        stderr.WriteLine(Usage);
        stderr.WriteLine($"datarepo site: error: {message}");
        return 2;
    }
}
