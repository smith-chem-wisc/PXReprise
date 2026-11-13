using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;
using DuckDB.NET.Data;
using UsefulProteomicsDatabases;

namespace PXReprise.Catalog;

/// <summary>One PRIDE record kept beside the bundles (1.5.0 item 12; U20, U21; ptmQtl's DATAREPO-P35, our 061, their 062).</summary>
/// <param name="Accession">The bare ProteomeXchange accession: a split deposit's searches share one record.</param>
/// <param name="RecordId">The directory it is kept in, named by when it was fetched.</param>
/// <param name="Path">That directory.</param>
/// <param name="Sha256">sha256 of its <c>record.json</c>, as <c>fetch.json</c> recorded it and the file still hashes.</param>
/// <param name="FetchedUtc">When it was fetched, ISO 8601 in UTC.</param>
/// <param name="MzLibVersion">The mzLib whose <c>PrideArchiveClient</c> read it.</param>
/// <param name="Record">The parsed <c>record.json</c>.</param>
public sealed record PrideRecordRef(string Accession, string RecordId, string Path, string Sha256, string FetchedUtc, string MzLibVersion,
    IReadOnlyDictionary<string, object?> Record);

/// <summary>What one accession's fetch did.</summary>
/// <param name="Outcome"><c>written</c>, <c>unchanged</c> (the newest record kept is already this one) or <c>failed</c>.</param>
/// <param name="Detail">The record id written or kept, or why the fetch failed.</param>
public sealed record PrideFetchResult(string Accession, string Outcome, bool? Found, string Detail);

/// <summary>Each deposit's PRIDE record, fetched by the operator into the store and read by <c>build</c> into
/// <c>deposit_records</c>, <c>deposit_contacts</c> and <c>deposit_references</c> (ptmQtl's lab rule; our 061, their 062).</summary>
/// <remarks>
/// <para><b>Beside the bundle, never in it.</b> PRIDE edits a record after deposit (a paper is added when it is published),
/// and in a bundle that would move the bundle id while no search row changed. So a record lives at
/// <c>&lt;store&gt;/_companion/pride/&lt;accession&gt;/&lt;record id&gt;/</c>: <c>record.json</c>, the fields as mzLib's
/// <c>PrideArchiveClient.TryGetProjectAsync</c> reads them, and <c>fetch.json</c>, when and with what. Add-only: a fetch
/// that finds the record unchanged writes nothing, one that finds it changed writes a new directory, and <c>build</c> serves
/// the newest. Its sha256 is part of the catalog id, never a bundle id.</para>
/// <para><b>Everything is kept as mzLib read it</b>, e-mail included, and nothing is redacted: PRIDE publishes it, and the
/// user's group writes to depositors and lab heads (D65, reversing the exclusion of our ptmQtl 061). PRIDE's empty strings
/// are kept too; the catalog makes an empty string NULL.</para>
/// <para><b>Only this command reaches the network.</b> <c>build</c> reads what was fetched and fetches nothing, and the
/// runner fetches nothing (U14).</para>
/// </remarks>
public static class PrideRecords
{
    /// <summary>Where companions live in a store, beside <c>_engine</c> and <c>_study</c>.</summary>
    public const string CompanionDir = "_companion";

    /// <summary>This companion's kind: its directory under <see cref="CompanionDir"/>.</summary>
    public const string Kind = "pride";

    public const string RecordFile = "record.json";
    public const string FetchFile = "fetch.json";

    /// <summary>The archive every record here comes from.</summary>
    public const string Archive = "PRIDE";

    /// <summary>The directory one accession's records are kept in.</summary>
    public static string AccessionDir(string store, string accession) =>
        System.IO.Path.Combine(store, CompanionDir, Kind, accession);

    /// <summary>The deposits a store's bundles are searches of, bare and in order: what a fetch with no accessions named covers.</summary>
    public static List<string> StoreAccessions(string store) =>
        Directory.Exists(store)
            ? Directory.EnumerateDirectories(store)
                .Select(d => System.IO.Path.GetFileName(d)!)
                .Where(d => !d.StartsWith('_') && !d.StartsWith('.'))
                .Where(d => Directory.EnumerateFiles(System.IO.Path.Combine(store, d), BundleWriter.ManifestName, SearchOption.AllDirectories).Any())
                .Select(Usi.DepositAccession).Distinct(StringComparer.Ordinal).Order(SourcesPy.CodePointOrder).ToList()
            : [];

    /// <summary>A PRIDE project as kept: the fields the three tables read, every value as mzLib read it, e-mail included
    /// (D65). A project PRIDE has no record of is kept too (<c>found = false</c>), so "no record" is never confused with
    /// "not fetched".</summary>
    public static OrderedDictionary<string, object?> RecordOf(string accession, bool found, PrideProject? project)
    {
        static OrderedDictionary<string, object?> Contact(PrideContact c) => new(StringComparer.Ordinal)
        {
            ["name"] = c.Name, ["title"] = c.Title, ["first_name"] = c.FirstName, ["last_name"] = c.LastName,
            ["affiliation"] = c.Affiliation, ["country"] = c.Country, ["email"] = c.Email, ["orcid"] = c.Orcid,
            ["pride_contact_id"] = Convert.ToString(c.Id, CultureInfo.InvariantCulture),
        };
        var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accession"] = accession, ["archive"] = Archive, ["found"] = found,
        };
        if (!found || project is null) return record;
        record["lab_heads"] = (project.LabPIs ?? []).Select(c => (object?)Contact(c)).ToList();
        record["submitters"] = (project.Submitters ?? []).Select(c => (object?)Contact(c)).ToList();
        record["references"] = (project.References ?? []).Select(r => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reference_line"] = r.ReferenceLine,
            ["pubmed_id"] = Convert.ToInt64(r.PubmedId, CultureInfo.InvariantCulture),
            ["doi"] = r.Doi,
        }).ToList();
        return record;
    }

    /// <summary>Fetches a record through mzLib's <see cref="PrideArchiveClient"/>: PRIDE's answer, or no project
    /// (<c>found = false</c>, PRIDE's 404). An outage throws, so it is never kept as "no record".</summary>
    public static Func<string, OrderedDictionary<string, object?>> Client(PrideArchiveClient client) => accession =>
    {
        var (found, project) = client.TryGetProjectAsync(accession).GetAwaiter().GetResult();
        return RecordOf(accession, found, project);
    };

    /// <summary>The mzLib that reads a record, as <c>fetch.json</c> records it.</summary>
    public static string MzLibVersion => typeof(PrideArchiveClient).Assembly.GetName().Version!.ToString(3);

    /// <summary>Fetches each accession's record and keeps it unless the newest one kept is the same.</summary>
    /// <param name="fetch">The record for an accession; <see cref="Client"/> in use, a stand-in in tests.</param>
    /// <param name="now">When the fetch happened; the clock when null.</param>
    /// <remarks>A failure is reported and the next accession is fetched: one PRIDE outage does not lose the rest.</remarks>
    public static List<PrideFetchResult> Fetch(string store, IEnumerable<string> accessions, Func<string, OrderedDictionary<string, object?>> fetch,
        Func<DateTime>? now = null)
    {
        now ??= () => DateTime.UtcNow;
        var results = new List<PrideFetchResult>();
        foreach (var raw in accessions)
        {
            var accession = Usi.DepositAccession(raw);
            OrderedDictionary<string, object?> record;
            try
            {
                record = fetch(accession);
            }
            catch (Exception e) when (e is HttpRequestException or MzLibUtil.MzLibException or TaskCanceledException or IOException)
            {
                results.Add(new PrideFetchResult(accession, "failed", null, $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}"));
                continue;
            }
            var text = PyFormat.JsonIndented(record) + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var found = record["found"] is true;
            var newest = Kept(store, accession).LastOrDefault();
            if (newest is not null && newest.Sha256 == sha)
            {
                results.Add(new PrideFetchResult(accession, "unchanged", found, newest.RecordId));
                continue;
            }
            var when = now().ToUniversalTime();
            var id = $"{when.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{sha[..12]}";
            var dir = AccessionDir(store, accession);
            Directory.CreateDirectory(dir);
            var staging = System.IO.Path.Combine(dir, $".{id}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
            Directory.CreateDirectory(staging);
            try
            {
                File.WriteAllBytes(System.IO.Path.Combine(staging, RecordFile), bytes);
                var meta = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["accession"] = accession,
                    ["archive"] = Archive,
                    ["fetched_utc"] = when.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
                    ["record_sha256"] = sha,
                    ["mzlib"] = MzLibVersion,
                    ["client"] = "PrideArchiveClient.TryGetProjectAsync",
                    ["datarepo"] = BundleWriter.PackageVersion,
                };
                File.WriteAllText(System.IO.Path.Combine(staging, FetchFile), PyFormat.JsonIndented(meta) + "\n", new UTF8Encoding(false));
                Directory.Move(staging, System.IO.Path.Combine(dir, id));
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                throw;
            }
            results.Add(new PrideFetchResult(accession, "written", found, id));
        }
        return results;
    }

    /// <summary>Every record kept for an accession, oldest first (by fetch time, then id).</summary>
    /// <exception cref="CatalogException">A record whose <c>record.json</c> no longer hashes to what its fetch recorded.</exception>
    public static List<PrideRecordRef> Kept(string store, string accession)
    {
        var dir = AccessionDir(store, accession);
        if (!Directory.Exists(dir)) return [];
        var kept = new List<PrideRecordRef>();
        foreach (var d in Directory.EnumerateDirectories(dir).Where(d => !System.IO.Path.GetFileName(d)!.StartsWith('.')))
        {
            var metaPath = System.IO.Path.Combine(d, FetchFile);
            var recordPath = System.IO.Path.Combine(d, RecordFile);
            if (!File.Exists(metaPath) || !File.Exists(recordPath))
                throw new CatalogException($"{d} is not a whole PRIDE record: it needs {RecordFile} and {FetchFile}");
            var meta = CatalogBuilder.ReadJsonObject(metaPath);
            var sha = SourcesPy.Str(meta.GetValueOrDefault("record_sha256"));
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(recordPath)));
            if (actual != sha)
                throw new CatalogException(
                    $"{recordPath} hashes to {actual}, and its fetch recorded {sha}: it changed after it was fetched. Fetch the "
                    + "record again rather than editing it.");
            var record = CatalogBuilder.ReadJsonObject(recordPath);
            if (SourcesPy.Str(record.GetValueOrDefault("accession")) != accession)
                throw new CatalogException($"{recordPath} is the record of {SourcesPy.Str(record.GetValueOrDefault("accession"))}, kept under {accession}");
            kept.Add(new PrideRecordRef(accession, System.IO.Path.GetFileName(d)!, d, sha,
                SourcesPy.Str(meta.GetValueOrDefault("fetched_utc")), SourcesPy.Str(meta.GetValueOrDefault("mzlib")), record));
        }
        return kept.OrderBy(k => k.FetchedUtc, StringComparer.Ordinal).ThenBy(k => k.RecordId, StringComparer.Ordinal).ToList();
    }

    /// <summary>The newest record of each deposit the bundles are searches of, in accession order. A deposit never fetched
    /// has none, and the catalog then has no row for it: not fetched, which is not "PRIDE has no record".</summary>
    public static List<PrideRecordRef> Select(string store, IReadOnlyList<BundleRef> bundles) =>
        bundles.Select(r => Usi.DepositAccession(r.DatasetId)).Distinct(StringComparer.Ordinal).Order(SourcesPy.CodePointOrder)
            .Select(a => Kept(store, a).LastOrDefault()).Where(r => r is not null).Select(r => r!).ToList();

    /// <summary>The three tables, from the records given (written even when there are none, so a query never fails on a
    /// catalog built before anything was fetched).</summary>
    internal static void CreateTables(DuckDBConnection con, IReadOnlyList<PrideRecordRef> records)
    {
        CatalogBuilder.Exec(con, $@"
        CREATE TABLE ""{CatalogBuilder.DepositRecordsTable}"" (
            accession VARCHAR, archive VARCHAR, found BOOLEAN, fetched_utc VARCHAR, record_id VARCHAR,
            record_sha256 VARCHAR, mzlib_version VARCHAR
        )");
        CatalogBuilder.Exec(con, $@"
        CREATE TABLE ""{CatalogBuilder.DepositContactsTable}"" (
            accession VARCHAR, role VARCHAR, ordinal BIGINT, name VARCHAR, title VARCHAR, first_name VARCHAR,
            last_name VARCHAR, affiliation VARCHAR, country VARCHAR, email VARCHAR, orcid VARCHAR, pride_contact_id VARCHAR
        )");
        CatalogBuilder.Exec(con, $@"
        CREATE TABLE ""{CatalogBuilder.DepositReferencesTable}"" (
            accession VARCHAR, ordinal BIGINT, reference_line VARCHAR, pubmed_id BIGINT, doi VARCHAR
        )");
        static string? Text(object? v) => v is string s && s.Length > 0 ? s : null;
        foreach (var r in records)
        {
            var found = r.Record.GetValueOrDefault("found") is true;
            CatalogBuilder.Exec(con, $"INSERT INTO \"{CatalogBuilder.DepositRecordsTable}\" VALUES (?, ?, ?, ?, ?, ?, ?)",
                r.Accession, SourcesPy.Str(r.Record.GetValueOrDefault("archive")), found, r.FetchedUtc, r.RecordId, r.Sha256, r.MzLibVersion);
            foreach (var (role, key) in new[] { ("lab_head", "lab_heads"), ("submitter", "submitters") })
            {
                var ordinal = 0L;
                foreach (var c in r.Record.GetValueOrDefault(key) as IEnumerable<object?> ?? [])
                {
                    var contact = (IReadOnlyDictionary<string, object?>)c!;
                    CatalogBuilder.Exec(con, $"INSERT INTO \"{CatalogBuilder.DepositContactsTable}\" VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                        r.Accession, role, ++ordinal, Text(contact.GetValueOrDefault("name")), Text(contact.GetValueOrDefault("title")),
                        Text(contact.GetValueOrDefault("first_name")), Text(contact.GetValueOrDefault("last_name")),
                        Text(contact.GetValueOrDefault("affiliation")), Text(contact.GetValueOrDefault("country")), Text(contact.GetValueOrDefault("email")),
                        Text(contact.GetValueOrDefault("orcid")), Text(contact.GetValueOrDefault("pride_contact_id")));
                }
            }
            var n = 0L;
            foreach (var x in r.Record.GetValueOrDefault("references") as IEnumerable<object?> ?? [])
            {
                var reference = (IReadOnlyDictionary<string, object?>)x!;
                var pubmed = reference.GetValueOrDefault("pubmed_id") is { } p ? Convert.ToInt64(p, CultureInfo.InvariantCulture) : 0L;
                CatalogBuilder.Exec(con, $"INSERT INTO \"{CatalogBuilder.DepositReferencesTable}\" VALUES (?, ?, ?, ?, ?)",
                    r.Accession, ++n, Text(reference.GetValueOrDefault("reference_line")), pubmed > 0 ? pubmed : null,
                    Text(reference.GetValueOrDefault("doi")));
            }
        }
    }
}
