using System.Globalization;
using PXReprise.Bundle;
using PXReprise.Host;

// datarepo-host: the hosted MCP server (Streamable HTTP at /mcp). Exit codes as the CLI's: 0 stopped, 1 refused, 2 usage.
//
//   datarepo-host --catalog <folder or file> [--urls http://127.0.0.1:8080] [--rate 120] [--allowed-origins A,B] [--name N]
//
// The token is read from the environment (DATAREPO_HOST_TOKEN), never from the command line, so it is not in the
// process list or the shell history.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const string TokenVariable = "DATAREPO_HOST_TOKEN";
const string Usage =
    "usage: datarepo-host --catalog CATALOG [--urls URLS] [--rate N] [--allowed-origins A,B] [--name NAME]";

if (args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine(Usage + "\n\n"
        + "Serve one catalog to AI agents over MCP (Streamable HTTP, at /mcp).\n\n"
        + "  --catalog CATALOG        the catalog folder (or one catalog file) to serve\n"
        + "  --urls URLS              where to listen (default http://127.0.0.1:8080)\n"
        + "  --rate N                 requests per minute per client address (default 120)\n"
        + "  --allowed-origins A,B    web origins a browser may call from (an agent sends none); others get 403\n"
        + "  --name NAME              the server name a client sees (default datarepo)\n\n"
        + $"  {TokenVariable}      environment variable: the bearer token every /mcp request must carry\n"
        + "                           (Authorization: Bearer <token>). Required unless --urls is loopback only.\n"
        + "  GET /healthz             names the catalog served; needs no token");
    return 0;
}
string? Value(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
var catalog = Value("--catalog");
if (catalog is null)
{
    Console.Error.WriteLine(Usage);
    Console.Error.WriteLine("datarepo-host: error: --catalog is required");
    return 2;
}
var rateText = Value("--rate");
var rate = 120;
if (rateText is not null && (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out rate) || rate < 1))
{
    Console.Error.WriteLine(Usage);
    Console.Error.WriteLine($"datarepo-host: error: --rate must be a positive whole number, not {rateText}");
    return 2;
}
try
{
    var settings = new HostSettings(
        catalog,
        Token: Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } token ? token : null,
        PermitPerMinute: rate,
        Urls: Value("--urls") ?? "http://127.0.0.1:8080",
        Name: Value("--name") ?? "datarepo",
        AllowedOrigins: Value("--allowed-origins")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    await using var app = HostedServer.Build(settings);
    await app.StartAsync();
    Console.Error.WriteLine($"datarepo-host: serving {catalog} at {HostedServer.Address(app)}/mcp"
        + (settings.Token is null ? " (no token: loopback only)" : " (bearer token required)"));
    await app.WaitForShutdownAsync();
    return 0;
}
catch (DataRepoException e)
{
    Console.Error.WriteLine($"datarepo-host: {e.Message}");
    return 1;
}
