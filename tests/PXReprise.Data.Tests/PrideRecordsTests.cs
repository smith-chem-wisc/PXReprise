using System.Net.Http;
using PXReprise.Bundle;
using PXReprise.Catalog;
using DuckDB.NET.Data;
using UsefulProteomicsDatabases;

namespace PXReprise.Data.Tests;

/// <summary>1.5.0 build list item 12 (U20, U21; ptmQtl's DATAREPO-P35, our 061, their 062): each deposit's PRIDE record,
/// fetched by the operator and kept beside the bundles; and the view of runs whose raw file is served more than once.</summary>
public class PrideRecordsTests
{
    private string _store = "";

    [SetUp]
    public void MakeStore() => _store = Path.Combine(Path.GetTempPath(), "datarepo-pride-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void RemoveStore()
    {
        if (Directory.Exists(_store)) Directory.Delete(_store, true);
    }

    private static PrideContact Person(string last, string email = "someone@example.org", string id = "101") => new()
    {
        Title = "Dr", FirstName = "Ann", LastName = last, Name = $"Ann {last}", Affiliation = "", Email = email,
        Country = "United States", Orcid = "", Id = id,
    };

    /// <summary>A project as mzLib reads it: one lab head, one submitter, two papers (one with PubMed 0 and no DOI).</summary>
    private static PrideProject Project(string labHead = "Smith") => new()
    {
        Accession = "PXD000001",
        LabPIs = [Person(labHead)],
        Submitters = [Person("Jones", id: "2254706")],
        References = [new PrideReference { ReferenceLine = "Smith A, et al. J Proteome Res 2026", PubmedId = 12345, Doi = "10.1021/x" },
            new PrideReference { ReferenceLine = "", PubmedId = 0, Doi = "" }],
    };

    private static DateTime At(int minute) => new(2026, 11, 12, 20, minute, 0, DateTimeKind.Utc);

    /// <summary>Every value as mzLib read it, e-mail included, and free text untouched (D65: PRIDE is public, and the user's
    /// group writes to depositors and lab heads).</summary>
    [Test]
    public void ARecordKeepsWhatMzLibReadEmailIncluded()
    {
        var project = Project();
        project.LabPIs[0].Affiliation = "Some Institute, Rm 1 | Tel: 555-0100 | Email: a.person@example.org";
        var record = PrideRecords.RecordOf("PXD000001", true, project);
        Assert.Multiple(() =>
        {
            Assert.That(record["found"], Is.EqualTo(true));
            var head = (IReadOnlyDictionary<string, object?>)((List<object?>)record["lab_heads"]!).Single()!;
            var submitter = (IReadOnlyDictionary<string, object?>)((List<object?>)record["submitters"]!).Single()!;
            Assert.That(head["email"], Is.EqualTo("someone@example.org"));
            Assert.That(head["affiliation"], Is.EqualTo("Some Institute, Rm 1 | Tel: 555-0100 | Email: a.person@example.org"), "free text verbatim");
            Assert.That(submitter["affiliation"], Is.EqualTo(""), "PRIDE's empty string is kept as written; the catalog makes it NULL");
            Assert.That(head["pride_contact_id"], Is.EqualTo("101"));
            Assert.That(((List<object?>)record["references"]!).Count, Is.EqualTo(2));
            Assert.That(PrideRecords.RecordOf("PXD000002", false, null).Keys, Is.EqualTo(new[] { "accession", "archive", "found" }),
                "no project: kept as found = false, with nothing else");
        });
    }

    [Test]
    public void AFetchKeepsANewRecordOnlyWhenItChanged()
    {
        var labHead = "Smith";
        OrderedDictionary<string, object?> Fetch(string a) => PrideRecords.RecordOf(a, true, Project(labHead));
        var minute = 0;
        DateTime Now() => At(minute++);

        var first = PrideRecords.Fetch(_store, ["PXD000001"], Fetch, Now).Single();
        var again = PrideRecords.Fetch(_store, ["PXD000001"], Fetch, Now).Single();
        labHead = "Smith-Jones";
        var changed = PrideRecords.Fetch(_store, ["PXD000001"], Fetch, Now).Single();
        labHead = "Smith";
        var back = PrideRecords.Fetch(_store, ["PXD000001"], Fetch, Now).Single();
        var kept = PrideRecords.Kept(_store, "PXD000001");
        Assert.Multiple(() =>
        {
            Assert.That((first.Outcome, again.Outcome, changed.Outcome, back.Outcome), Is.EqualTo(("written", "unchanged", "written", "written")),
                "a record that changes back is kept again, so the newest is what PRIDE says now");
            Assert.That(again.Detail, Is.EqualTo(first.Detail), "unchanged names the record already kept");
            Assert.That(kept.Select(k => k.RecordId), Is.EqualTo(new[] { first.Detail, changed.Detail, back.Detail }));
            Assert.That(kept[^1].Sha256, Is.EqualTo(kept[0].Sha256));
            Assert.That(kept[^1].FetchedUtc, Is.EqualTo("2026-11-12T20:02:00+00:00"), "the clock is read only for a record kept");
            Assert.That(kept[^1].MzLibVersion, Is.EqualTo(PrideRecords.MzLibVersion));
        });
    }

    /// <summary>A split deposit's parts are one deposit, so they share one record; an outage is reported and the rest
    /// are still fetched; PRIDE's "no such project" is kept, an outage never is.</summary>
    [Test]
    public void AFailureIsReportedAndNothingIsKeptForIt()
    {
        OrderedDictionary<string, object?> Fetch(string a) => a switch
        {
            "PXD000002" => throw new HttpRequestException("PRIDE is down"),
            "PXD000003" => PrideRecords.RecordOf(a, false, null),
            _ => PrideRecords.RecordOf(a, true, Project()),
        };
        var results = PrideRecords.Fetch(_store, ["PXD000001-mouse", "PXD000002", "PXD000003"], Fetch, () => At(0));
        Assert.Multiple(() =>
        {
            Assert.That(results.Select(r => (r.Accession, r.Outcome, r.Found)),
                Is.EqualTo(new[] { ("PXD000001", "written", (bool?)true), ("PXD000002", "failed", null), ("PXD000003", "written", false) }));
            Assert.That(results[1].Detail, Does.Contain("PRIDE is down"));
            Assert.That(PrideRecords.Kept(_store, "PXD000002"), Is.Empty);
            Assert.That(Directory.Exists(PrideRecords.AccessionDir(_store, "PXD000001-mouse")), Is.False, "kept under the bare accession");
        });
    }

    [Test]
    public void ARecordEditedAfterItsFetchIsRefused()
    {
        var written = PrideRecords.Fetch(_store, ["PXD000001"], a => PrideRecords.RecordOf(a, true, Project()), () => At(0)).Single();
        var path = Path.Combine(PrideRecords.AccessionDir(_store, "PXD000001"), written.Detail, PrideRecords.RecordFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Smith", "Smyth", StringComparison.Ordinal));
        Assert.That(() => PrideRecords.Kept(_store, "PXD000001"),
            Throws.TypeOf<CatalogException>().With.Message.Contains("changed after it was fetched"));
    }

    private static void Exec(DuckDBConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>The view on the cases ptmQtl 061 found: a file under two deposits' names, one under two runs of one deposit,
    /// and the searches of a split deposit; a file served once, and one with no sha256, are not listed.</summary>
    [Test]
    public void SharedRawFilesFindsEveryCopyBySha256NotByName()
    {
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        Exec(con, "CREATE TABLE runs (dataset_id VARCHAR, run_id VARCHAR, file_name VARCHAR, sha256 VARCHAR)");
        Exec(con, "INSERT INTO runs VALUES "
            + "('PXD033430', 'PXD033430:Control-1', 'Control-1.raw', 'aaa'), "
            + "('PXD057544', 'PXD057544:Con1_Lum', 'Con1_Lum.raw', 'aaa'), "
            + "('PXD006815', 'PXD006815:Z73a', 'Z73a.raw', 'bbb'), "
            + "('PXD006815', 'PXD006815:Z74', 'Z74.raw', 'bbb'), "
            + "('PXD027754-rat', 'PXD027754-rat:r1', 'r1.raw', 'ccc'), "
            + "('PXD027754-mouse', 'PXD027754-mouse:r1', 'r1.raw', 'ccc'), "
            + "('PXD000009', 'PXD000009:alone', 'alone.raw', 'ddd'), "
            + "('PXD000009', 'PXD000009:unhashed', 'unhashed.raw', NULL), "
            + "('PXD000010', 'PXD000010:unhashed', 'unhashed.raw', NULL)");
        var (name, body) = CatalogBuilder.RunViews.Single();
        Exec(con, $"CREATE VIEW {name} AS {body}");
        using var c = con.CreateCommand();
        c.CommandText = "SELECT run_id, accession, n_copies, other_run_ids, in_same_accession, in_other_accession FROM shared_raw_files ORDER BY run_id";
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add($"{r.GetString(0)} {r.GetString(1)} {r.GetInt64(2)} [{string.Join(",", ((System.Collections.IEnumerable)r.GetValue(3)).Cast<object>())}] {r.GetBoolean(4)} {r.GetBoolean(5)}");
        Assert.That(rows, Is.EqualTo(new[]
        {
            "PXD006815:Z73a PXD006815 2 [PXD006815:Z74] True False",
            "PXD006815:Z74 PXD006815 2 [PXD006815:Z73a] True False",
            "PXD027754-mouse:r1 PXD027754 2 [PXD027754-rat:r1] True False",
            "PXD027754-rat:r1 PXD027754 2 [PXD027754-mouse:r1] True False",
            "PXD033430:Control-1 PXD033430 2 [PXD057544:Con1_Lum] False True",
            "PXD057544:Con1_Lum PXD057544 2 [PXD033430:Control-1] False True",
        }));
    }
}
