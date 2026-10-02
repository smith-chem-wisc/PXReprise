namespace PXReprise.Search;

/// <summary>
/// The .NET runtimes MetaMorpheus can load, so a search that failed because they changed under it is told apart from
/// one that failed on its data (G5, aging S66). MetaMorpheus 1.1.11 is framework-dependent (net10.0, rolling forward to
/// the newest 10.0.x), so a machine-wide update that replaces 10.0.8 with 10.0.10 mid-search removes the assemblies it
/// has not loaded yet: PXD021194 died 2 h 21 m in on System.IO.Compression.ZipFile. A machine's <c>dotnet_root</c> gives
/// MetaMorpheus a private runtime copy no updater touches; without one, the machine-wide install is watched.
/// </summary>
public static class DotnetRuntimes
{
    /// <summary>The root MetaMorpheus resolves runtimes from: the machine's <c>dotnet_root</c>, else the standard install.</summary>
    public static string Root(string? dotnetRoot) =>
        dotnetRoot
        ?? Environment.GetEnvironmentVariable("DOTNET_ROOT")
        ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
            : new[] { "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet" }.FirstOrDefault(Directory.Exists) ?? "/usr/share/dotnet");

    /// <summary>The installed Microsoft.NETCore.App versions under <paramref name="root"/>, sorted; empty when there are none.</summary>
    public static IReadOnlyList<string> Snapshot(string root)
    {
        string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        return Directory.Exists(shared)
            ? Directory.EnumerateDirectories(shared).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    /// <summary>The executable that hosts a <c>.dll</c> under a private root, as the platform names it.</summary>
    public static string Host(string root) => Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
}
