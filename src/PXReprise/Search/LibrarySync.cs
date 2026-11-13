using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Storage;

namespace PXReprise.Search;

/// <summary>
/// A spectral library chain merged entry by entry: the newest version plus one search's own changes.
/// </summary>
/// <remarks>
/// Two machines that both start from version N cannot both register N+1 (Alex and the user, 2026-11-11: libraries are
/// synced through the cloud; Alex chose "merge at save"). The one that registers second keeps the newest version and adds
/// what its search changed: every entry (by its <c>Name:</c> line, charge included) that the search's output holds and
/// its parent did not, or holds with different bytes. An entry the newest version ALSO changed since that parent keeps the
/// newest version's spectrum (first registered wins); <see cref="Counts.KeptNewer"/> counts them. MetaMorpheus's update
/// only adds or replaces entries, so nothing a search left out is ever removed.
/// </remarks>
public static class LibraryMerge
{
    public const string Rule = "pxreprise-library-merge/1: the newest version, plus every entry the search's output added or changed relative to the version the search read; an entry both changed keeps the newest version's spectrum";

    /// <summary>What a merge did.</summary>
    /// <param name="Added">Entries the search added that the newest version did not hold.</param>
    /// <param name="Replaced">Entries the search changed and the newest version had not, now the search's spectrum.</param>
    /// <param name="KeptNewer">Entries both changed: the newest version's spectrum kept.</param>
    public sealed record Counts(int Added, int Replaced, int KeptNewer);

    /// <summary>Writes <paramref name="dest"/>: <paramref name="newest"/> plus what <paramref name="output"/> changed
    /// relative to <paramref name="parent"/> (null when the search wrote a first library).</summary>
    public static Counts Onto(string? parent, string output, string newest, string dest)
    {
        var inParent = new Dictionary<string, UInt128>(StringComparer.Ordinal);
        if (parent is not null)
            foreach (var block in SpectralLibrary.Entries(parent))
                inParent[SpectralLibrary.NameOf(block[0])] = Key(block);

        // The search's own changes, in its order. On a large chain these are the new spectra of one deposit, not the chain.
        var changed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var block in SpectralLibrary.Entries(output))
        {
            string name = SpectralLibrary.NameOf(block[0]);
            if (inParent.TryGetValue(name, out var k) && k == Key(block)) continue;
            if (changed.TryAdd(name, block)) order.Add(name);
        }

        int replaced = 0, keptNewer = 0;
        using (var w = new StreamWriter(new FileStream(dest, FileMode.CreateNew), new UTF8Encoding(false)) { NewLine = SpectralLibrary.NewLineOf(newest) })
        {
            foreach (var block in SpectralLibrary.Entries(newest, keepPreamble: true))
            {
                if (!block[0].StartsWith("Name:", StringComparison.Ordinal)) { Write(w, block); continue; }
                string name = SpectralLibrary.NameOf(block[0]);
                if (!changed.Remove(name, out var ours)) { Write(w, block); continue; }
                bool newestChangedIt = !inParent.TryGetValue(name, out var p) || p != Key(block);
                if (newestChangedIt) { keptNewer++; Write(w, block); }
                else { replaced++; Write(w, ours); }
            }
            int added = 0;
            foreach (string name in order)
                if (changed.TryGetValue(name, out var block)) { added++; Write(w, block); }
            return new Counts(added, replaced, keptNewer);
        }
    }

    private static void Write(StreamWriter w, List<string> block)
    {
        foreach (string l in block) w.WriteLine(l);
    }

    /// <summary>The first 16 bytes of the entry's SHA-256: a million entries' keys fit in a few tens of MB.</summary>
    private static UInt128 Key(List<string> block)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', block)), h);
        return new UInt128(BitConverter.ToUInt64(h[..8]), BitConverter.ToUInt64(h[8..16]));
    }
}

/// <summary>
/// The cloud copy of the spectral library chains: add-only, one record per version (Alex and the user, 2026-11-11).
/// </summary>
/// <remarks>
/// <para>Layout below the remote's root, per chain: <c>&lt;chain&gt;/&lt;sha256&gt;.msp</c> (a library, named by content)
/// and <c>&lt;chain&gt;/v&lt;NNN&gt;.json</c> (that version's record, as <c>registry.json</c> holds it, plus <c>file</c>
/// and <c>machine</c>). Both are written once. A version exists once its record does, and the highest is current. No
/// object is ever replaced, so there is no shared registry to race on: claiming version N is writing <c>vN.json</c>, and S3
/// refuses a second writer (<c>If-None-Match: *</c>).</para>
/// <para>A record whose <c>file</c> is null holds no library in the cloud (a version from before the chain was seeded);
/// it keeps the version numbers and the history the same on every machine.</para>
/// </remarks>
public sealed class LibraryCloud(IStore remote, string machine) : IDisposable
{
    public IStore Remote => remote;

    public string Machine => machine;

    /// <summary>The machine file's <c>spectral_library_remote</c>, opened; null when it names none (no sync).</summary>
    public static LibraryCloud? Open(string? location) =>
        string.IsNullOrWhiteSpace(location) ? null : new LibraryCloud(Stores.Open(location), Environment.MachineName);

    public static string RecordKey(string chain, int version) => $"{StoreKey.Check(chain)}/v{version:000}.json";

    /// <summary>Every version record of <paramref name="chain"/> in the cloud, by version.</summary>
    public SortedDictionary<int, JsonObject> Records(string chain)
    {
        var found = new SortedDictionary<int, JsonObject>();
        var prefix = StoreKey.Check(chain) + "/v";
        var keys = new List<string>();
        Run(async () => { await foreach (var e in remote.ListAsync(prefix).ConfigureAwait(false)) keys.Add(e.Key); });
        string scratch = Scratch();
        try
        {
            foreach (string key in keys)
            {
                string name = key[(chain.Length + 2)..];
                if (!name.EndsWith(".json", StringComparison.Ordinal) || !int.TryParse(name[..^5], out int v)) continue;
                string local = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                Run(() => remote.DownloadAsync(key, local));
                found[v] = JsonNode.Parse(File.ReadAllText(local))!.AsObject();
            }
        }
        finally { Directory.Delete(scratch, true); }
        return found;
    }

    /// <summary>Downloads version <paramref name="record"/>'s library to <paramref name="dest"/> and checks its sha256.</summary>
    public void Download(string chain, JsonObject record, string dest)
    {
        string file = record["file"]?.GetValue<string>()
                      ?? throw new SearchSetupException($"spectral_library: {chain} v{(int)record["version"]!:000} has no library in {remote.Location}");
        string sha = record["sha256"]!.GetValue<string>();
        string partial = dest + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Run(() => remote.DownloadAsync($"{chain}/{file}", partial));
            string got = Stores.Sha256File(partial);
            if (got != sha)
                throw new SearchSetupException($"spectral_library: {remote.Location}{chain}/{file} has sha256 {got}; its record says {sha}. Not used.");
            File.Move(partial, dest, overwrite: false);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    /// <summary>
    /// Claims version <c>record["version"]</c> of <paramref name="chain"/>: uploads the library (unless
    /// <paramref name="localFile"/> is null), then writes the record. False when another machine claimed that number first;
    /// the caller then fetches, merges again and retries. Nothing it wrote is a version until the record exists.
    /// </summary>
    public bool Claim(string chain, JsonObject record, string? localFile)
    {
        int version = (int)record["version"]!;
        var cloudRecord = (JsonObject)record.DeepClone();
        cloudRecord["file"] = null;
        if (localFile is not null)
        {
            string sha = record["sha256"]!.GetValue<string>();
            Run(() => remote.WriteOnceAsync($"{chain}/{sha}.msp", localFile, sha));
            cloudRecord["file"] = $"{sha}.msp";
        }
        cloudRecord["machine"] = machine;
        string scratch = Scratch();
        try
        {
            string json = Path.Combine(scratch, "record.json");
            File.WriteAllText(json, cloudRecord.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Envelope.Json.Encoder }) + "\n", new UTF8Encoding(false));
            try
            {
                Run(() => remote.WriteOnceAsync(RecordKey(chain, version), json, Stores.Sha256File(json)));
                return true;
            }
            catch (StoreConflictException) { return false; }
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static string Scratch()
    {
        string d = Path.Combine(Path.GetTempPath(), "pxreprise-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // Registration runs synchronously under the registry lock (SpectralLibrary.Register), so the store's calls are waited here.
    private static void Run(Func<Task> work) => work().ConfigureAwait(false).GetAwaiter().GetResult();

    public void Dispose() => remote.Dispose();
}
