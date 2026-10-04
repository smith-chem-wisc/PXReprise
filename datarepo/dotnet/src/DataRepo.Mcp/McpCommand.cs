using System.Globalization;
using DataRepo.Bundle;

namespace DataRepo.Mcp;

/// <summary><c>datarepo mcp --catalog &lt;catalog.duckdb&gt; [options]</c>: the command line of <c>cli.py</c>'s
/// <c>mcp</c> subcommand (<c>cmd_mcp</c>), its options and its printed report.</summary>
/// <remarks>
/// <para>Parsed the way argparse parses it: <c>--name value</c> or <c>--name=value</c>, a unique prefix of a long
/// option, and a usage error exits 2.</para>
/// <para>For Claude Code (<c>~/.claude.json</c>, or any MCP client config), the server is started as:</para>
/// <code>{"command": "C:\\path\\to\\datarepo.exe", "args": ["mcp", "--catalog", "F:\\aging_data\\repo\\catalog.duckdb"], "env": {}}</code>
/// <para>which <c>datarepo mcp --catalog &lt;path&gt; --install</c> writes itself. Under the shared .NET host the
/// command is <c>dotnet</c> and the first argument the path of <c>datarepo.dll</c>.</para>
/// </remarks>
public static class McpCommand
{
    public const string Usage =
        "usage: datarepo mcp [-h] [--catalog CATALOG] [--install] [--list] [--check]\n" +
        "                    [--name NAME] [--config CONFIG] [--force]";

    public const string Help =
        Usage + "\n\n" +
        "options:\n" +
        "  -h, --help         show this help message and exit\n" +
        "  --catalog CATALOG  the catalog .duckdb file to serve; never auto-discovered\n" +
        "  --install          register it with Claude Code and exit\n" +
        "  --list             show the datarepo servers already registered\n" +
        "  --check            open the catalog and report, without serving\n" +
        "  --name NAME        server name, for registering several catalogs\n" +
        "  --config CONFIG    MCP config to write; default is the Claude Code user\n" +
        "                     config\n" +
        "  --force            repoint an entry of that name at this catalog";

    /// <summary>Every option; true when it takes a value.</summary>
    private static readonly IReadOnlyDictionary<string, bool> Options = new Dictionary<string, bool>
    {
        ["--catalog"] = true,
        ["--install"] = false,
        ["--list"] = false,
        ["--check"] = false,
        ["--name"] = true,
        ["--config"] = true,
        ["--force"] = false,
    };

    /// <summary>Run the command; returns the process exit code (0 done, 1 refused, 2 usage). Serving blocks until the
    /// client disconnects.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                stdout.WriteLine(Help);
                return 0;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length <= 2)
                return Error(stderr, $"unrecognized arguments: {arg}");
            var eq = arg.IndexOf('=');
            var given = eq >= 0 ? arg[..eq] : arg;
            var matches = Options.Keys.Where(o => o == given).ToList();
            if (matches.Count == 0) matches = Options.Keys.Where(o => o.StartsWith(given, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
                return Error(stderr, matches.Count == 0
                    ? $"unrecognized arguments: {arg}"
                    : $"ambiguous option: {given} could match {string.Join(", ", matches)}");
            var option = matches[0];
            if (!Options[option])
            {
                if (eq >= 0) return Error(stderr, $"argument {option}: ignored explicit argument '{arg[(eq + 1)..]}'");
                flags.Add(option);
                continue;
            }
            string value;
            if (eq >= 0) value = arg[(eq + 1)..];
            else if (i + 1 < args.Count && !(args[i + 1].StartsWith('-') && args[i + 1].Length > 1)) value = args[++i];
            else return Error(stderr, $"argument {option}: expected one argument");
            values[option] = value;
        }

        try
        {
            return Execute(values, flags, stdout, stderr);
        }
        catch (DataRepoException ex)
        {
            stderr.WriteLine($"datarepo: {ex.Message}");
            return 1;
        }
    }

    private static int Execute(Dictionary<string, string> values, HashSet<string> flags, TextWriter stdout, TextWriter stderr)
    {
        var config = values.GetValueOrDefault("--config");
        var name = values.GetValueOrDefault("--name") ?? "datarepo";
        if (flags.Contains("--list"))
        {
            var entries = Mcp.InstalledEntries(config);
            if (entries.Count == 0)
            {
                stdout.WriteLine("no datarepo MCP server is registered");
                return 0;
            }
            foreach (var (entryName, entry) in entries.OrderBy(kv => kv.Key, PyValues.CodePointOrder))
            {
                var entryArgs = entry.GetValueOrDefault("args") as List<object?> ?? [];
                stdout.WriteLine($"{entryName}  {PyValues.Str(entry.GetValueOrDefault("command"))} {string.Join(" ", entryArgs.Select(PyValues.Str))}");
            }
            return 0;
        }

        if (!values.TryGetValue("--catalog", out var catalog) || catalog.Length == 0)
        {
            stderr.WriteLine("datarepo mcp: --catalog is required (D13: one catalog, by explicit path)");
            return 2;
        }

        var toolNames = string.Join(", ", Mcp.ToolSpecs.Select(spec => spec.Name));
        if (flags.Contains("--install"))
        {
            var result = Mcp.Install(catalog, name, config, flags.Contains("--force"));
            var entry = (Dictionary<string, object?>)result["entry"]!;
            stdout.WriteLine($"{result["action"]}  {result["name"]} in {result["config"]}");
            stdout.WriteLine($"  command  {entry["command"]} {string.Join(" ", ((List<object?>)entry["args"]!).Select(PyValues.Str))}");
            stdout.WriteLine($"  tools    {toolNames}");
            if ((string)result["action"]! != "unchanged") stdout.WriteLine("  restart Claude Code to pick it up");
            return 0;
        }

        if (flags.Contains("--check"))
        {
            // Open the catalog and answer one question through the tools, without the SDK. This is what tells an
            // operator the failure is the SDK or the config rather than the catalog.
            using var server = new CatalogServer(catalog);
            var overview = server.Describe();
            stdout.WriteLine($"catalog  {server.Box.Path}");
            stdout.WriteLine($"  id       {PyValues.Str(server.Identity.CatalogId)}");
            stdout.WriteLine($"  built    {PyValues.Str(server.Identity.BuiltUtc)} by {PyValues.Str(server.Identity.Builder)} {PyValues.Str(server.Identity.BuilderVersion)}");
            foreach (var row in (List<Dictionary<string, object?>>)overview["datasets"]!)
                stdout.WriteLine($"  dataset  {PyValues.Str(row["dataset_id"]),-12} {PyValues.Thousands(row["n_psms_1pct"]),9} PSMs at 1%");
            stdout.WriteLine($"  tools    {toolNames}");
            stdout.WriteLine($"  empty    {((List<string>)overview["tables_empty"]!).Count.ToString(CultureInfo.InvariantCulture)} table(s) present with no rows");
            return 0;
        }

        Mcp.ServeAsync(catalog, name, stderr).GetAwaiter().GetResult();
        return 0;
    }

    private static int Error(TextWriter stderr, string message)
    {
        stderr.WriteLine(Usage);
        stderr.WriteLine($"datarepo mcp: error: {message}");
        return 2;
    }
}
