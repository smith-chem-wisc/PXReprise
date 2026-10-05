using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DataRepo.Bundle;
using DataRepo.Catalog;

namespace DataRepo.Runner;

/// <summary><c>datarepo run</c>: an instance operator runs a RELEASED engine on stored data (G64, D27, D28).</summary>
/// <remarks>
/// <para>Engines that work on stored results never learn which consumer they serve, so none runs itself. dataRepo
/// ships this runner and operates nothing; the operator (today aging) runs it. Its output is an engine artefact,
/// content-addressed and written beside the bundles, never inside one (U12). The four rules it keeps (charter
/// section 2, aging 055 section 1, design/RUNNER.md):</para>
/// <list type="number">
/// <item><b>Released inputs only.</b> A development build of datarepo is refused (aging 063): an artefact stamped
/// by unreleased code is reproducible by nobody.</item>
/// <item><b>Idempotent.</b> The artefact id is a sha256 over the engine, its release, every input's role and
/// sha256, the definition id, <see cref="RunnerVersion"/> and the schema version (U13).</item>
/// <item><b>Beside the bundle, never inside it.</b> A run reads only a bundle's <c>bundle.json</c>.</item>
/// <item><b>The record is the output's provenance.</b> <c>run.json</c> holds everything hashed plus what was not.</item>
/// </list>
/// <para>What reaches a written row is hashed, and nothing else: the datarepo install is recorded but NOT hashed,
/// so a runner release that writes the same rows does not re-identify an artefact. Ported from <c>runner.py</c>.</para>
/// </remarks>
public static class EngineRunner
{
    /// <summary>The version of the RUN PATH, and the only datarepo version in an artefact's id. Bump it in the same
    /// commit as any change to what a run reads, checks, derives or writes.</summary>
    public const string RunnerVersion = "1";

    /// <summary>Engines the runner knows.</summary>
    public static readonly IReadOnlyList<string> Engines = [LogsEngine.Engine];

    /// <summary>Which datarepo build is running, or a refusal (aging 063).</summary>
    /// <remarks>A release build carries its version and the commit it was built from (CI stamps both: Version and
    /// SourceRevisionId). A development build (<c>-dev</c>, or no commit) is refused: the code can change under
    /// a running operator, which is how a 0.18.0 working tree was on aging's PATH mid-release.</remarks>
    /// <exception cref="RunnerException">Not a release build.</exception>
    public static Dictionary<string, object?> InstallIdentity()
    {
        var version = BundleWriter.PackageVersion;
        var commit = typeof(EngineRunner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceRevisionId")?.Value;
        if (version.Contains("-dev", StringComparison.Ordinal) || string.IsNullOrEmpty(commit))
            throw new RunnerException(
                $"datarepo {version} is a development build{(string.IsNullOrEmpty(commit) ? " with no recorded commit" : "")}. "
                + "An artefact stamped by unreleased code is reproducible by nobody, so the runner refuses it. "
                + "Run a released datarepo (a release download, or a build of a release tag).");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["distribution"] = "datarepo",
            ["version"] = version,
            ["source"] = "release-build",
            ["commit"] = commit,
            ["platform"] = RuntimeInformation.RuntimeIdentifier,
        };
    }

    /// <summary>The artefact's content hash, and the "already done" test (U13).</summary>
    /// <param name="engine">e.g. <c>logs.resolve_genes</c>.</param>
    /// <param name="release">The engine's released versions, e.g. <c>{"mzlib": "1.0.593"}</c>.</param>
    /// <param name="inputs"><c>{role: sha256}</c> for every input that reaches a row.</param>
    /// <param name="definitionId">The method the rows are written under.</param>
    public static string ArtefactId(string engine, IReadOnlyDictionary<string, string> release,
        IReadOnlyDictionary<string, string> inputs, string definitionId)
    {
        var text = new StringBuilder($"runner/{RunnerVersion}\nschema/{SchemaContract.Version}\nengine/{engine}\n");
        foreach (var (key, value) in release.OrderBy(kv => kv.Key, StringComparer.Ordinal)) text.Append($"release/{key}\t{value}\n");
        foreach (var (role, sha) in inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal)) text.Append($"input/{role}\t{sha}\n");
        text.Append($"definition/{definitionId}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    public static string ArtefactDir(string store, string engine, string artefactId) =>
        BundleWriter.PathText(Path.Combine(store, DataRepo.Catalog.Runner.EngineDir, engine, artefactId));

    /// <summary>Writes one artefact atomically: into a staging directory, then renamed into place.</summary>
    /// <remarks>A failure leaves nothing that discovery would find, so a half-written run can never be mistaken
    /// for "already done".</remarks>
    /// <exception cref="RunnerException">The artefact already exists.</exception>
    public static ArtefactRef WriteArtefact(string store, string engine, string artefactId,
        IReadOnlyDictionary<string, object?> record, IReadOnlyDictionary<string, List<IReadOnlyDictionary<string, object?>>> tables)
    {
        var final = ArtefactDir(store, engine, artefactId);
        if (Directory.Exists(final)) throw new RunnerException($"{final} already exists");
        var staging = Path.Combine(Path.GetDirectoryName(final)!, $".{artefactId}.writing");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            var counts = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (table, rows) in tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                ArrowTables.WriteParquet(ArrowTables.FromRows(table, rows), Path.Combine(staging, $"{table}.parquet"));
                counts[table] = (long)rows.Count;
            }
            var full = new Dictionary<string, object?>(record, StringComparer.Ordinal)
            {
                ["engine"] = engine,
                ["artefact_id"] = artefactId,
                ["runner_version"] = RunnerVersion,
                ["schema_version"] = SchemaContract.Version,
                ["datarepo_version"] = BundleWriter.PackageVersion,
                ["tables"] = counts,
                ["written_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            // Python: json.dumps(full, indent=1, sort_keys=True) + "\n".
            File.WriteAllText(Path.Combine(staging, DataRepo.Catalog.Runner.RunRecord),
                PyFormat.JsonIndented(SortKeys(full), 1) + "\n", new UTF8Encoding(false));
            Directory.Move(staging, final);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
        return ArtefactRef.Load(final);
    }

    /// <summary>A value with every mapping's keys sorted, as <c>sort_keys=True</c> writes it.</summary>
    private static object? SortKeys(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> d => new SortedDictionary<string, object?>(
            d.ToDictionary(kv => kv.Key, kv => SortKeys(kv.Value)), StringComparer.Ordinal),
        IDictionary<string, object?> d => new SortedDictionary<string, object?>(
            d.ToDictionary(kv => kv.Key, kv => SortKeys(kv.Value)), StringComparer.Ordinal),
        string s => s,
        System.Collections.IEnumerable e => e.Cast<object?>().Select(SortKeys).ToList(),
        _ => value,
    };
}
