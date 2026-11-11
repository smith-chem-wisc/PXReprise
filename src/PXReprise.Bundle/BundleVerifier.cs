using System.Text.Json;

namespace PXReprise.Bundle;

/// <summary>One file of a bundle that is not what its <c>bundle.json</c> recorded.</summary>
/// <param name="Kind"><c>changed</c> (its sha256 is not the recorded one), <c>missing</c> (recorded, not on disk) or
/// <c>extra</c> (on disk, recorded nowhere).</param>
/// <param name="File">The file, relative to the bundle folder.</param>
/// <param name="Detail">The recorded and the current sha256 for a changed file; empty otherwise.</param>
public sealed record VerifyProblem(string Kind, string File, string Detail);

/// <summary>What <c>datarepo verify</c> found in one bundle.</summary>
/// <param name="TablesRecorded">Whether <c>bundle.json</c> records the tables' sha256 (from datarepo 1.5.0, D61). A bundle
/// written before records none, so its tables are not verified: that is "not checked", never "passed".</param>
public sealed record BundleVerification(string Path, string? DatasetId, string? BundleId, bool TablesRecorded,
    int TablesChecked, int SourcesChecked, IReadOnlyList<VerifyProblem> Problems)
{
    /// <summary>No file differs from the record. Says nothing about tables when <see cref="TablesRecorded"/> is false.</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>Each bundle's record of its own files, and the check against it (D61, 1.5.0 build list item 6).</summary>
/// <remarks>
/// <para>From datarepo 1.5.0 a bundle records the sha256 of every table file it wrote, in <c>bundle.json</c>'s
/// <c>table_sha256</c>, beside <c>tables</c> (the row counts, whose shape readers rely on). Every copied source already
/// carried its sha256 and its <c>bundle_path</c>. <see cref="Verify"/> re-hashes both on demand; <c>build</c> does not, by
/// the user's choice (D61), so it costs a build nothing.</para>
/// <para>The sha256 is of the file's bytes. A table rewritten with identical rows by another program reads as
/// <c>changed</c>, which is what a check for rewrites has to say; it is not a claim that a row is wrong. The bundle id
/// hashes inputs, never these files, so it cannot vouch for them. <c>bundle.json</c> itself is not covered: it cannot
/// hold its own hash.</para>
/// </remarks>
public static class BundleVerifier
{
    /// <summary>The <c>bundle.json</c> key that holds each table file's sha256.</summary>
    public const string TableHashesKey = "table_sha256";

    /// <summary>Checks one bundle folder's tables and copied sources against its <c>bundle.json</c>.</summary>
    /// <exception cref="IngestException">The folder has no <c>bundle.json</c>, or it is not a search bundle's.</exception>
    public static BundleVerification Verify(string bundleDir)
    {
        var manifestPath = System.IO.Path.Combine(bundleDir, BundleWriter.ManifestName);
        if (!File.Exists(manifestPath)) throw new IngestException($"no {BundleWriter.ManifestName} in {bundleDir}");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("tables", out _) || !root.TryGetProperty("sources", out var sources))
            throw new IngestException($"{manifestPath} is not a search bundle's (no tables or sources)");
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var problems = new List<VerifyProblem>();

        void Check(string relative, string recorded)
        {
            var path = System.IO.Path.Combine(bundleDir, relative);
            if (!File.Exists(path))
            {
                problems.Add(new VerifyProblem("missing", relative, ""));
                return;
            }
            var now = BundleWriter.Sha256File(path);
            if (!string.Equals(now, recorded, StringComparison.OrdinalIgnoreCase))
                problems.Add(new VerifyProblem("changed", relative, $"recorded {recorded}, now {now}"));
        }

        var tablesRecorded = root.TryGetProperty(TableHashesKey, out var hashes) && hashes.ValueKind == JsonValueKind.Object;
        var tablesChecked = 0;
        if (tablesRecorded)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in hashes.EnumerateObject())
            {
                var file = $"{table.Name}.parquet";
                named.Add(file);
                Check(file, table.Value.GetString() ?? "");
                tablesChecked++;
            }
            foreach (var file in Directory.EnumerateFiles(bundleDir, "*.parquet").Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal))
                if (!named.Contains(file!)) problems.Add(new VerifyProblem("extra", file!, ""));
        }

        var sourcesChecked = 0;
        var copies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.EnumerateArray())
        {
            if (!source.TryGetProperty("bundle_path", out var at) || at.GetString() is not { Length: > 0 } relative) continue;
            copies.Add(relative.Replace('\\', '/'));
            Check(relative, source.TryGetProperty("sha256", out var sha) ? sha.GetString() ?? "" : "");
            sourcesChecked++;
        }
        var sourcesDir = System.IO.Path.Combine(bundleDir, BundleWriter.SourcesDir);
        if (Directory.Exists(sourcesDir))
            foreach (var file in Directory.EnumerateFiles(sourcesDir, "*", SearchOption.AllDirectories)
                         .Select(f => System.IO.Path.GetRelativePath(bundleDir, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
                if (!copies.Contains(file)) problems.Add(new VerifyProblem("extra", file, ""));

        return new BundleVerification(bundleDir, Text("dataset_id"), Text("bundle_id"), tablesRecorded, tablesChecked, sourcesChecked, problems);
    }

    /// <summary>Every bundle folder in a store, <c>&lt;store&gt;/&lt;dataset&gt;/&lt;bundle id&gt;</c>, in path order. Folders
    /// whose name starts with <c>_</c> or <c>.</c> (engine artefacts, companions, staging) are not bundles.</summary>
    public static IReadOnlyList<string> BundlesIn(string store)
    {
        if (!Directory.Exists(store)) throw new IngestException($"no store at {store}");
        return Directory.EnumerateDirectories(store)
            .Where(d => System.IO.Path.GetFileName(d) is { Length: > 0 } n && n[0] is not ('_' or '.'))
            .SelectMany(Directory.EnumerateDirectories)
            .Where(d => File.Exists(System.IO.Path.Combine(d, BundleWriter.ManifestName)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
