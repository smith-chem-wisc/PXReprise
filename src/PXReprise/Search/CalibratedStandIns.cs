using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PXReprise.Search;

/// <summary>
/// v0.4.45 (the user, 2026-11-11: "once a file is calibrated, we don't need to calibrate it again. and we don't need all
/// files calibrated in the same session. they can split. no problem"). Windows Update restarted MOZZLE at 08:45 UTC during
/// PXD028799's search, after MetaMorpheus had calibrated 34 of its 48 raws and D59 had deleted those 34; v0.4.44 then had
/// to fetch all 34 again and calibrate every file afresh.
/// <para>
/// A calibrated stand-in is one raw file's finished calibration, <c>&lt;stem&gt;-calib.mzML</c> and <c>&lt;stem&gt;-calib.toml</c>
/// as MetaMorpheus's Calibration task writes them, kept in <c>02_fetch/spectra/calibrated/</c> with a record of the settings it
/// was made under (<see cref="Identity"/>). The fetch does not download a raw that has one, full QC keeps that raw's earlier
/// verdict, and the search calibrates only the raws on disk, then searches them together with the stand-ins (SearchStage's
/// two runs). A stand-in made under other settings is refused by the search, and its raw is fetched again.
/// </para>
/// MetaMorpheus calibrates each file from that file's data alone (CalibrationTask's per-file loop; read in 1.1.14 and in
/// design/PLAN_calibrate-as-you-go.md), so a file calibrated in an earlier run is the file this run would have written.
/// Stand-ins come only from searches the batch set aside as interrupted (<c>04_search.interrupted-*</c>: a reboot, a crash, a
/// runtime change), never from a failed search a person retried, since a retry usually follows a change of settings or files.
/// </summary>
public static class CalibratedStandIns
{
    /// <summary>The folder under the spectra folder that holds the stand-ins.</summary>
    public const string Folder = "calibrated";

    /// <summary>Each stand-in's record, beside its two files: <c>&lt;stem&gt;.calibration.json</c>.</summary>
    public const string RecordSuffix = ".calibration.json";

    /// <summary>MetaMorpheus's CalibrationTask.CalibSuffix.</summary>
    public const string CalibSuffix = "-calib";

    /// <summary>Where a search set aside by an interruption comes from (BatchRunner's <c>Aside</c>).</summary>
    public const string InterruptedPattern = ".interrupted-*";

    /// <param name="RawName">The raw file it stands in for, as the fetch named it.</param>
    /// <param name="From">The set-aside search it was taken from.</param>
    /// <param name="FetchEntry">The raw's entry in the fetch manifest of that search (name, SHA-256, PRIDE checksum), which a
    /// later fetch writes again in place of downloading the raw; null when there was none, and then the raw is fetched.</param>
    public sealed record StandIn(string RawName, string Mzml, string Toml, JsonObject Identity, string From, JsonObject? FetchEntry = null);

    /// <summary>
    /// The settings a calibration depends on, from a search's <c>params.json</c> and its edited Calibration task file: the
    /// profile and organism, the MetaMorpheus release and its mzLib, the protein databases, and the task file itself
    /// (modifications, protease, tolerances, fragmentation, threads), compared by its sha256 with line ends normalised.
    /// Two calibrations with equal identities were made by the same program on the same settings.
    /// </summary>
    public static JsonObject Identity(JsonNode searchParams, string calibrationTaskText) => new()
    {
        ["profile"] = searchParams["profile"]?.DeepClone(),
        ["organism"] = searchParams["organism"]?.DeepClone(),
        ["metamorpheus_version"] = searchParams["search"]?["metamorpheus_version"]?.DeepClone(),
        ["metamorpheus_mzlib_version"] = searchParams["search"]?["metamorpheus_mzlib_version"]?.DeepClone(),
        ["database_prepared"] = searchParams["database"]?["prepared"]?.DeepClone(),
        ["database_extra"] = searchParams["database"]?["extra_prepared"]?.DeepClone(),
        ["calibration_task_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(calibrationTaskText.ReplaceLineEndings("\n").Trim()))),
    };

    /// <summary>Whether two identities are the same, key by key.</summary>
    public static bool Same(JsonObject a, JsonObject b) => JsonNode.DeepEquals(a, b);

    /// <summary>
    /// The identity keys on which <paramref name="a"/> and <paramref name="b"/> differ, for the refusal's message.
    /// </summary>
    public static IReadOnlyList<string> Differences(JsonObject a, JsonObject b) =>
        a.Select(kv => kv.Key).Union(b.Select(kv => kv.Key)).Where(k => !JsonNode.DeepEquals(a[k], b[k])).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether MetaMorpheus finished writing <paramref name="mzml"/>: it exists, is not empty, and its last bytes close the
    /// document (<c>&lt;/indexedmzML&gt;</c> or <c>&lt;/mzML&gt;</c>). The <c>-calib.toml</c> is written after the mzML, but a
    /// power cut can leave a file whose length is right and whose tail never reached the disk.
    /// </summary>
    public static bool Complete(string mzml)
    {
        var info = new FileInfo(mzml);
        if (!info.Exists || info.Length == 0) return false;
        using var s = File.OpenRead(mzml);
        int n = (int)Math.Min(512, s.Length);
        s.Seek(-n, SeekOrigin.End);
        var buffer = new byte[n];
        s.ReadExactly(buffer);
        string tail = Encoding.ASCII.GetString(buffer);
        return tail.Contains("</indexedmzML>", StringComparison.Ordinal) || tail.Contains("</mzML>", StringComparison.Ordinal);
    }

    /// <summary>
    /// Moves every finished calibration out of <paramref name="runDir"/>'s interrupted searches into the spectra folder's
    /// <see cref="Folder"/>, newest search first, and writes each one's record. A raw that already has a stand-in keeps it.
    /// The record names the raw as <c>02_fetch/fetch_manifest.json</c> does (its extension's case included), else
    /// <c>&lt;stem&gt;.raw</c>, and copies that manifest's entry for it: a probe run before the whole fetch rewrites the
    /// manifest with its own few files. Call it before anything fetches. Returns what it moved.
    /// </summary>
    public static IReadOnlyList<StandIn> Harvest(string runDir, string searchFolder = "04_search")
    {
        var got = new List<StandIn>();
        if (!Directory.Exists(runDir)) return got;
        string into = Path.Combine(runDir, "02_fetch", "spectra", Folder);
        var entries = ManifestEntriesByStem(Path.Combine(runDir, "02_fetch", "fetch_manifest.json"));
        // The suffix is a UTC stamp (yyyyMMddTHHmmssZ), so the newest sorts last.
        foreach (string aside in Directory.EnumerateDirectories(runDir, searchFolder + InterruptedPattern).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            string paramsFile = Path.Combine(aside, "params.json"), calTask = Path.Combine(aside, "tasks", "1_" + TaskFiles.FileName(TaskKind.Calibration)), mm = Path.Combine(aside, "mm");
            if (!File.Exists(paramsFile) || !File.Exists(calTask) || !Directory.Exists(mm)) continue;
            JsonObject identity;
            try { identity = Identity(JsonNode.Parse(File.ReadAllText(paramsFile)) ?? new JsonObject(), File.ReadAllText(calTask)); }
            catch (JsonException) { continue; }
            foreach (string calDir in Directory.EnumerateDirectories(mm, "Task*CalibrationTask").Order(StringComparer.Ordinal))
                foreach (string toml in Directory.EnumerateFiles(calDir, "*" + CalibSuffix + ".toml").Order(StringComparer.Ordinal))
                {
                    string stem = Path.GetFileName(toml)[..^(CalibSuffix.Length + ".toml".Length)];
                    string mzml = Path.Combine(calDir, stem + CalibSuffix + ".mzML");
                    if (File.Exists(Path.Combine(into, stem + RecordSuffix)) || !Complete(mzml)) continue;
                    Directory.CreateDirectory(into);
                    string mzmlTo = Path.Combine(into, Path.GetFileName(mzml)), tomlTo = Path.Combine(into, Path.GetFileName(toml));
                    File.Move(mzml, mzmlTo, overwrite: true);
                    File.Move(toml, tomlTo, overwrite: true);
                    var entry = entries.GetValueOrDefault(stem);
                    string raw = entry?["name"]?.GetValue<string>() ?? stem + ".raw";
                    var record = new JsonObject
                    {
                        ["raw"] = raw, ["mzml"] = Path.GetFileName(mzmlTo), ["toml"] = Path.GetFileName(tomlTo),
                        ["identity"] = identity.DeepClone(), ["from"] = Path.GetFileName(aside), ["harvested_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                        ["fetch_entry"] = entry?.DeepClone(),
                        ["note"] = "calibrated by MetaMorpheus in a search the batch set aside as interrupted; it stands in for the raw, which is not fetched or calibrated again (v0.4.45)",
                    };
                    File.WriteAllText(Path.Combine(into, stem + RecordSuffix), record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    got.Add(new StandIn(raw, mzmlTo, tomlTo, (JsonObject)identity.DeepClone(), Path.GetFileName(aside), (JsonObject?)entry?.DeepClone()));
                }
        }
        return got;
    }

    /// <summary>The stand-ins in <paramref name="spectraDir"/>, by raw name; a record whose files are gone is skipped.</summary>
    public static IReadOnlyList<StandIn> In(string spectraDir)
    {
        string dir = Path.Combine(spectraDir, Folder);
        if (!Directory.Exists(dir)) return Array.Empty<StandIn>();
        var o = new List<StandIn>();
        foreach (string rec in Directory.EnumerateFiles(dir, "*" + RecordSuffix).Order(StringComparer.Ordinal))
        {
            JsonNode? j;
            try { j = JsonNode.Parse(File.ReadAllText(rec)); }
            catch (JsonException) { continue; }
            if (j?["raw"]?.GetValue<string>() is not { } raw || j["identity"] is not JsonObject identity
                || j["mzml"]?.GetValue<string>() is not { } mzml || j["toml"]?.GetValue<string>() is not { } toml) continue;
            string m = Path.Combine(dir, mzml), t = Path.Combine(dir, toml);
            if (File.Exists(m) && File.Exists(t))
                o.Add(new StandIn(raw, m, t, (JsonObject)identity.DeepClone(), j["from"]?.GetValue<string>() ?? "", j["fetch_entry"]?.DeepClone() as JsonObject));
        }
        return o;
    }

    /// <summary>Deletes the stand-ins of <paramref name="rawNames"/> (their raws are fetched again instead).</summary>
    public static void Discard(string spectraDir, IEnumerable<string> rawNames)
    {
        var drop = rawNames.ToHashSet(StringComparer.Ordinal);
        foreach (var s in In(spectraDir).Where(s => drop.Contains(s.RawName)))
        {
            File.Delete(s.Mzml);
            File.Delete(s.Toml);
            File.Delete(Path.Combine(spectraDir, Folder, Path.GetFileNameWithoutExtension(s.RawName) + RecordSuffix));
        }
    }

    /// <summary>A fetch manifest's raw entries that carry a SHA-256, by the raw's stem; empty when there is none.</summary>
    private static Dictionary<string, JsonObject> ManifestEntriesByStem(string fetchManifest)
    {
        var o = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!File.Exists(fetchManifest)) return o;
        try
        {
            foreach (var f in JsonNode.Parse(File.ReadAllText(fetchManifest))?["files"]?.AsArray() ?? new JsonArray())
                if (f is JsonObject e && e["name"]?.GetValue<string>() is { } name && e["sha256"]?.GetValue<string>() is { Length: > 0 })
                    o[Path.GetFileNameWithoutExtension(name)] = e;
        }
        catch (JsonException) { }
        return o;
    }
}

/// <summary>
/// v0.4.45: calibrated stand-ins (<see cref="CalibratedStandIns"/>) that this search cannot use, for raws that are not on
/// disk. Nothing ran; the batch discards them and fetches their raws on its next pass.
/// </summary>
public sealed class CalibrationNotCarriedException(IReadOnlyList<string> rawNames, string reason)
    : SearchSetupException($"{rawNames.Count} calibrated stand-in(s) cannot be used and their raws are not on disk ({reason}): {string.Join(", ", rawNames.Take(5))}{(rawNames.Count > 5 ? ", ..." : "")}")
{
    public IReadOnlyList<string> RawNames { get; } = rawNames;
}
