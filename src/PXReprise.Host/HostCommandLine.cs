using System.Globalization;
using PXReprise.Bundle;

namespace PXReprise.Host;

/// <summary><c>datarepo-host</c>'s command line, which <c>Program.cs</c> runs. It lives here so the offline tests reach
/// every branch of it without a process. Exit codes as the CLI's: 0 stopped, 1 refused, 2 usage.</summary>
/// <remarks><c>datarepo-host --catalog &lt;folder or file&gt; [--urls http://127.0.0.1:8080] [--rate 120]
/// [--allowed-origins A,B] [--name N]</c>. The token is read from the environment (<see cref="TokenVariable"/>), never
/// from the command line, so it is not in the process list or the shell history.</remarks>
public static class HostCommandLine
{
    /// <summary>The environment variable that holds the bearer token.</summary>
    public const string TokenVariable = "DATAREPO_HOST_TOKEN";

    /// <summary>The one-line usage, printed before every usage error.</summary>
    public const string Usage =
        "usage: datarepo-host --catalog CATALOG [--urls URLS] [--rate N] [--allowed-origins A,B] [--name NAME]";

    /// <summary>What <c>--help</c> prints.</summary>
    public static string Help => Usage + "\n\n"
        + "Serve one catalog to AI agents over MCP (Streamable HTTP, at /mcp).\n\n"
        + "  --catalog CATALOG        the catalog folder (or one catalog file) to serve\n"
        + "  --urls URLS              where to listen (default http://127.0.0.1:8080)\n"
        + "  --rate N                 requests per minute per client address (default 120)\n"
        + "  --allowed-origins A,B    web origins a browser may call from (an agent sends none); others get 403\n"
        + "  --name NAME              the server name a client sees (default datarepo)\n\n"
        + $"  {TokenVariable}      environment variable: the bearer token every /mcp request must carry\n"
        + "                           (Authorization: Bearer <token>). Required unless --urls is loopback only.\n"
        + "  GET /healthz             names the catalog served; needs no token";

    /// <summary>The settings a command line and the token variable's value give, or, with none, the exit code and the
    /// text to print: <see cref="Help"/> (exit 0, for stdout) or a usage error (exit 2, for stderr).</summary>
    /// <param name="args">The command line.</param>
    /// <param name="token">The value of <see cref="TokenVariable"/>; null or empty is no token.</param>
    public static (HostSettings? Settings, int ExitCode, string? Message) Parse(IReadOnlyList<string> args, string? token)
    {
        if (args.Contains("-h") || args.Contains("--help")) return (null, 0, Help);
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
        static (HostSettings?, int, string?) UsageError(string message) => (null, 2, $"{Usage}\ndatarepo-host: error: {message}");

        var catalog = Value("--catalog");
        if (catalog is null) return UsageError("--catalog is required");
        var rateText = Value("--rate");
        var rate = 120;
        if (rateText is not null && (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out rate) || rate < 1))
            return UsageError($"--rate must be a positive whole number, not {rateText}");
        return (new HostSettings(
            catalog,
            Token: token is { Length: > 0 } ? token : null,
            PermitPerMinute: rate,
            Urls: Value("--urls") ?? "http://127.0.0.1:8080",
            Name: Value("--name") ?? "datarepo",
            AllowedOrigins: Value("--allowed-origins")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), 0, null);
    }

    /// <summary>Runs <c>datarepo-host</c>: reads the command line, then builds and starts the server, says where it
    /// serves, and serves until it is stopped (Ctrl+C or SIGTERM, or <paramref name="cancellationToken"/>).</summary>
    /// <returns>0 when it stopped (or after <c>--help</c>), 1 when the server refused to start (a catalog it cannot
    /// open, or an address other than loopback with no token), 2 for a usage error.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string? token, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        var (settings, exitCode, message) = Parse(args, token);
        if (settings is null)
        {
            await (exitCode == 0 ? stdout : stderr).WriteLineAsync(message).ConfigureAwait(false);
            return exitCode;
        }
        try
        {
            await using var app = HostedServer.Build(settings);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            await stderr.WriteLineAsync($"datarepo-host: serving {settings.Catalog} at {HostedServer.Address(app)}/mcp"
                + (settings.Token is null ? " (no token: loopback only)" : " (bearer token required)")).ConfigureAwait(false);
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (DataRepoException e)
        {
            await stderr.WriteLineAsync($"datarepo-host: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
