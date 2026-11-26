using System.Globalization;
using PXReprise.Host;

// datarepo-host: the hosted MCP server (Streamable HTTP at /mcp). The command line and its exit codes (0 stopped,
// 1 refused, 2 usage) are HostCommandLine's; the token comes from the environment, never the command line.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
return await HostCommandLine.RunAsync(args, Environment.GetEnvironmentVariable(HostCommandLine.TokenVariable),
    Console.Out, Console.Error);
