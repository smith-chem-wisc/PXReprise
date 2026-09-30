using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;

namespace PXReprise.Search;

/// <summary>What the search stage needs to know before it runs: write a first library, or update the current one.</summary>
public sealed record LibraryPlan(string Organism, string Mode, string? LibraryIn, int? ParentVersion, string Root)
{
    /// <summary>MetaMorpheus's own output names (MetaMorpheusTask.cs:1125 and :1139). The trailing '_' keeps
    /// SpectralLibrary_* from also matching updateSpectralLibrary_* on a case-insensitive filesystem.</summary>
    public string OutputPattern => Mode == "write" ? "SpectralLibrary_*.msp" : "updateSpectralLibrary_*.msp";
}

/// <summary>
/// Spectral libraries, one chain per organism, built up across searches (aging D33). The first search of an organism
/// writes one; every later search updates the current one and consumes it; every version is kept so the chain can be
/// rolled back. Search task only. The registry format is aging's (<c>aging-spectral-library-registry/1</c>), so the
/// engine continues the chains the aging batch started.
///
/// MetaMorpheus's side, from the 1.1.11 source: the two TOML booleans act independently (both true writes two
/// libraries, so never both); an existing library is passed as another -d and is recognised by extension; an update
/// MERGES, keeping the better-evidenced spectrum per (sequence, charge), so a library only grows in coverage; and the
/// output name is timestamped, which is why the produced file must be discovered and registered.
/// </summary>
public static class SpectralLibrary
{
    public const string Schema = "aging-spectral-library-registry/1";

    public static string RegistryPath(string root) => Path.Combine(root, "registry.json");

    public static JsonObject ReadRegistry(string root)
    {
        string p = RegistryPath(root);
        if (!File.Exists(p)) return new JsonObject { ["schema"] = Schema, ["organisms"] = new JsonObject() };
        var reg = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        string? schema = reg["schema"]?.GetValue<string>();
        if (schema != Schema) throw new SearchSetupException($"{p}: registry schema is '{schema}', this code writes '{Schema}'");
        return reg;
    }

    /// <summary>Atomic replace, so an interrupted run cannot leave a half-written registry behind.</summary>
    private static void WriteRegistry(string root, JsonObject reg)
    {
        string p = RegistryPath(root);
        Directory.CreateDirectory(root);
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, reg.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n");
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>
    /// Write or update for this run. The organism key is stated, never inferred from a PRIDE facet string. A registry
    /// whose current library is missing on disk is refused, never silently restarted: that would start a second chain
    /// and discard every spectrum the first had accumulated.
    /// </summary>
    public static LibraryPlan Plan(string root, string organism)
    {
        string key = organism.Trim().ToLowerInvariant().Replace(' ', '_');
        if (key.Length == 0) throw new UsageException("a spectral library needs an organism");
        var entry = ReadRegistry(root)["organisms"]![key]?.AsObject();
        string? current = entry?["current"]?.GetValue<string>();
        if (current is null) return new LibraryPlan(key, "write", null, null, root);
        string lib = Path.Combine(root, current);
        if (!File.Exists(lib))
            throw new SearchSetupException($"the registry says {key}'s current library is {current}, but {lib} does not exist. Restore it, or roll back to a version on disk (`pxreprise library list`).");
        return new LibraryPlan(key, "update", lib, VersionOf(entry!, current), root);
    }

    /// <summary>
    /// Copies the library MetaMorpheus produced into the registry as the next version and advances <c>current</c>.
    /// Refuses (without failing the search) when the file is absent, or when another search registered for this
    /// organism in between: registering then would silently discard that search's contribution.
    /// </summary>
    public static JsonObject Register(LibraryPlan plan, string searchTaskDir, string runLabel, string accession, string metamorpheus)
    {
        var produced = Directory.EnumerateFiles(searchTaskDir, plan.OutputPattern).Select(Path.GetFileName)
            .Where(n => plan.Mode != "write" || !n!.StartsWith("update", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (produced.Count == 0)
            throw new SearchSetupException($"spectral_library: {plan.Mode} mode was configured but no {plan.OutputPattern} was written to {searchTaskDir}. The library was NOT advanced.");
        string src = Path.Combine(searchTaskDir, produced[^1]!);

        var reg = ReadRegistry(plan.Root);
        var organisms = reg["organisms"]!.AsObject();
        if (organisms[plan.Organism] is not JsonObject entry)
            organisms[plan.Organism] = entry = new JsonObject { ["current"] = null, ["versions"] = new JsonArray() };
        string? current = entry["current"]?.GetValue<string>();
        if (current is not null || plan.ParentVersion is not null)
        {
            int? live = current is null ? null : VersionOf(entry, current);
            if (live != plan.ParentVersion)
                throw new SearchSetupException($"spectral_library: {plan.Organism}'s current library moved from version {plan.ParentVersion} to {live} while this search was running. This run's library is at {src} and has NOT been registered, so nothing was lost; merge it deliberately.");
        }

        var versions = entry["versions"]!.AsArray();
        int version = versions.Select(v => v!["version"]!.GetValue<int>()).DefaultIfEmpty(0).Max() + 1;
        string rel = $"{plan.Organism}/{plan.Organism}.v{version:000}.msp";
        string dest = Path.Combine(plan.Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // COPY, not move: the run folder stays a faithful record of what MetaMorpheus wrote.
        File.Copy(src, dest, overwrite: false);
        var rec = new JsonObject
        {
            ["version"] = version, ["path"] = rel, ["mode"] = plan.Mode, ["parent_version"] = plan.ParentVersion,
            ["sha256"] = Sha(dest), ["n_spectra"] = CountSpectra(dest), ["bytes"] = new FileInfo(dest).Length,
            ["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'"),
            ["produced_by"] = new JsonObject
            {
                ["run"] = runLabel, ["accession"] = accession, ["metamorpheus"] = metamorpheus,
                ["metamorpheus_path"] = src, ["metamorpheus_filename"] = Path.GetFileName(src),
            },
        };
        versions.Add(rec);
        entry["current"] = rel;
        WriteRegistry(plan.Root, reg);
        return rec;
    }

    /// <summary>
    /// Points <c>current</c> at an earlier version. Versions are never removed, so searching again appends a NEW version
    /// whose parent is the one rolled back to: the chain records the decision.
    /// </summary>
    public static JsonObject Rollback(string root, string organism, int version)
    {
        var reg = ReadRegistry(root);
        var entry = reg["organisms"]![organism]?.AsObject() ?? throw new UsageException($"no libraries registered for '{organism}'");
        var rec = entry["versions"]!.AsArray().FirstOrDefault(v => v!["version"]!.GetValue<int>() == version)?.AsObject()
                  ?? throw new UsageException($"{organism} has no version {version}");
        string path = rec["path"]!.GetValue<string>();
        if (!File.Exists(Path.Combine(root, path))) throw new UsageException($"version {version} is registered as {path} but that file is missing");
        entry["current"] = path;
        WriteRegistry(root, reg);
        return rec;
    }

    /// <summary>"Name:" lines, one per library spectrum: makes a library that shrank visible in the registry.</summary>
    public static int CountSpectra(string msp) => File.ReadLines(msp).Count(l => l.StartsWith("Name:", StringComparison.Ordinal));

    private static int? VersionOf(JsonObject entry, string path) =>
        entry["versions"]!.AsArray().FirstOrDefault(v => v!["path"]!.GetValue<string>() == path)?["version"]?.GetValue<int>();

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
