using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PXReprise.Cli;

namespace PXReprise.Provenance;

public sealed record FileEntry(string Path, long SizeBytes, string Sha256);

/// <summary>
/// The provenance record every PXReprise output gets (aging D9's rule, carried over): what ran, on what inputs (with
/// sha256), with which versions, when, and the notes a reader needs. Written beside the outputs as provenance.json.
/// </summary>
public sealed class RunRecord
{
    public string Schema { get; } = "pxreprise-provenance/1";
    public string Command { get; }
    public string StartedUtc { get; } = Now();
    public string? FinishedUtc { get; private set; }
    public string Host { get; } = Environment.MachineName;
    public Dictionary<string, string> Tools { get; } = Versions();
    public List<FileEntry> Inputs { get; } = new();
    public List<FileEntry> Outputs { get; } = new();
    public List<string> Notes { get; } = new();

    public RunRecord(string command) => Command = command;

    public void Input(string path) => Inputs.Add(Entry(path));
    public void Output(string path) => Outputs.Add(Entry(path));
    public void Note(string note) => Notes.Add(note);

    public string Write(string dir)
    {
        FinishedUtc = Now();
        string file = System.IO.Path.Combine(dir, "provenance.json");
        File.WriteAllText(file, JsonSerializer.Serialize(this, Envelope.Json));
        return file;
    }

    public static FileEntry Entry(string path)
    {
        using var s = File.OpenRead(path);
        return new FileEntry(System.IO.Path.GetFullPath(path), s.Length, Convert.ToHexStringLower(SHA256.HashData(s)));
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static Dictionary<string, string> Versions() => new()
    {
        ["pxreprise"] = Informational(typeof(RunRecord).Assembly),
        ["mzlib"] = Informational(typeof(UsefulProteomicsDatabases.PrideArchiveClient).Assembly),
        ["dotnet"] = Environment.Version.ToString(),
    };

    private static string Informational(Assembly a) =>
        a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? a.GetName().Version?.ToString() ?? "unknown";
}
