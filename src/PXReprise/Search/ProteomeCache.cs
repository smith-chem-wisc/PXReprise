using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PXReprise.Cli;
using PXReprise.Config;
using UsefulProteomicsDatabases;

namespace PXReprise.Search;

/// <summary>
/// An organism's search database as a file on this machine. A profile names either a prepared file under the machine's
/// database_dir, or a UniProt proteome ID. An ID is downloaded ONCE, through mzLib's
/// <c>ProteinDbRetriever.RetrieveProteome</c>
/// (reviewed entries, UniProt xml so MetaMorpheus sees UniProt's annotated modifications), to
/// <c>database_dir/uniprot/&lt;ID&gt;_reviewed.xml</c>, and reused from then on: UniProt changes weekly, so the cached copy
/// is what makes one machine's searches comparable. Its retrieval date and SHA-256 are kept beside it
/// (<c>.retrieval.json</c>) and copied into each search's provenance.
/// </summary>
public static class ProteomeCache
{
    /// <summary>mzLib's call, behind a delegate so tests run offline: (proteome ID, directory) -> path written.</summary>
    public delegate string Retrieve(string proteomeId, string directory);

    public static readonly Retrieve FromUniProt = (id, dir) =>
        ProteinDbRetriever.RetrieveProteome(id, dir, ProteinDbRetriever.ProteomeFormat.xml, ProteinDbRetriever.Reviewed.yes,
            ProteinDbRetriever.Compress.no, ProteinDbRetriever.IncludeIsoforms.no);

    public static string UniProtDir(string databaseDir) => Path.Combine(databaseDir, "uniprot");

    private static string CachedPath(string databaseDir, string id) => Path.Combine(UniProtDir(databaseDir), $"{id}_reviewed.xml");

    /// <summary>True when <paramref name="db"/> needs no download: a prepared file, or a UniProt proteome already cached.</summary>
    public static bool IsCached(OrganismDatabase db, string databaseDir) =>
        db.UniProt is not { } id || (File.Exists(CachedPath(databaseDir, id)) && File.Exists(CachedPath(databaseDir, id) + ".retrieval.json"));

    /// <summary>
    /// The database file for <paramref name="db"/>, downloading a UniProt proteome if it is not cached yet, with its
    /// retrieval record (null for a prepared file). A prepared file that is missing is a usage error: the operator names
    /// it, so only the operator can supply it. UniProt being unreachable surfaces as <see cref="HttpRequestException"/>
    /// (the batch retries it on its next pass); UniProt answering wrongly, as mzLib's <c>MzLibException</c>.
    /// </summary>
    public static async Task<(string Path, JsonObject? Retrieval)> ResolveAsync(OrganismDatabase db, string databaseDir,
        CancellationToken ct, Retrieve? retrieve = null)
    {
        if (db.Proteome is { } prepared)
        {
            string path = Path.Combine(databaseDir, prepared);
            return File.Exists(path) ? (path, null) : throw new UsageException($"prepared database {path} missing");
        }

        string id = db.UniProt ?? throw new ArgumentException("an organism database names neither a file nor a UniProt proteome", nameof(db));
        string dir = UniProtDir(databaseDir);
        string target = CachedPath(databaseDir, id), record = target + ".retrieval.json";
        if (File.Exists(target) && File.Exists(record))
            return (target, JsonNode.Parse(await File.ReadAllTextAsync(record, ct).ConfigureAwait(false))!.AsObject());

        // Download into a private folder and move into place, so a failed or concurrent download never leaves a partial
        // file under the name a search would trust.
        string staging = Path.Combine(dir, $".download-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            // mzLib's retrieval is synchronous and takes minutes for a large proteome; it cannot be cancelled mid-transfer.
            string written = await Task.Run(() => (retrieve ?? FromUniProt)(id, staging), ct).ConfigureAwait(false);
            var retrieval = new JsonObject
            {
                ["uniprot_proteome"] = id, ["reviewed"] = "yes", ["format"] = "xml",
                ["retrieved_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["via"] = "mzLib ProteinDbRetriever.RetrieveProteome (uniprotkb/stream)",
                ["bytes"] = new FileInfo(written).Length, ["sha256"] = Sha(written),
            };
            File.Move(written, target, overwrite: true);
            await File.WriteAllTextAsync(record, retrieval.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            return (target, retrieval);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
