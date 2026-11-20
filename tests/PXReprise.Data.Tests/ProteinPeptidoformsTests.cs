using PXReprise.Catalog;
using PXReprise.Ingest;
using DuckDB.NET.Data;

namespace PXReprise.Data.Tests;

/// <summary>dataRepo 136: <c>protein_peptidoforms</c>, a protein's accepted peptidoforms as a lookup on a sorted column.
/// In either layout it is <c>peptidoforms_1pct</c> unnested over <c>protein_accessions</c>, row for row, sorted by
/// accession, and a folder build that reuses a deposit's derived rows gives the same table as one that derives them.</summary>
/// <remarks>Runs on the current rules: a Python 0.32.0 parity build has no such table, which <c>CatalogTests</c> proves by
/// matching Python 0.32.0's catalogs table for table.</remarks>
public class ProteinPeptidoformsTests
{
    private const string Table = CatalogBuilder.ProteinPeptidoformsTable;

    /// <summary>The unnest, written here apart from the builder's SQL: a row per accession of each accepted peptidoform,
    /// an accession the list names twice once.</summary>
    private const string Unnested =
        "SELECT DISTINCT unnest(protein_accessions) AS protein_accession, dataset_id, bundle_id, peptidoform_id, base_sequence, best_q_value "
        + "FROM peptidoforms_1pct";

    private string _root = "";
    private string Store => Path.Combine(_root, "store");
    private string ManifestPath => Path.Combine(_root, "data", "manifest.yaml");
    private string Folder => Path.Combine(_root, "catalogs");

    [SetUp]
    public void MakeScratch()
    {
        _root = Path.Combine(Path.GetTempPath(), "datarepo-protein-peptidoforms-" + Guid.NewGuid().ToString("N"));
        var from = DataPaths.Instance;
        var to = Path.Combine(_root, "data");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        Ingester.Ingest(ManifestPath, "PXD999999", Store, rules: IngestRules.Current);
    }

    [TearDown]
    public void RemoveScratch()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { /* a DuckDB file still mapped on Windows; the temp folder is the OS's to clear */ }
    }

    private CatalogResult Build(string output)
    {
        var manifest = Manifest.Load(ManifestPath);
        var bundles = CatalogBuilder.SelectBundles(manifest, ["PXD999999"], Store);
        var (artefacts, checks) = CatalogBuilder.SelectArtefacts(Store, bundles);
        return CatalogBuilder.BuildCatalog(bundles, output, instance: manifest.Instance, artefacts: artefacts, engineChecks: checks, manifest: manifest);
    }

    private static List<object?[]> Rows(string catalog, string sql)
    {
        using var con = CatalogBuilder.Open(catalog, readOnly: true);
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The table's rows in the order they are stored.</summary>
    private static List<object?[]> Stored(string catalog) => Rows(catalog, $"SELECT * FROM {Table} ORDER BY rowid");

    /// <summary>The fixture manifest as ingested, with one note on the dataset: a change to the catalog id, never to a
    /// deposit's rows.</summary>
    private void Note(string original, string text) => File.WriteAllText(ManifestPath, original.Replace(
        "    flags: [low_id_rate, no_design_file]\n", $"    flags: [low_id_rate, no_design_file]\n    notes: {text}\n", StringComparison.Ordinal));

    [Test]
    public void ItIsPeptidoformsAtOnePercentUnnestedRowForRow()
    {
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Folder);
        foreach (var catalog in new[] { file.Path, folder.Path })
        {
            var expected = Rows(catalog, $"SELECT * FROM ({Unnested}) ORDER BY ALL");
            var peptidoforms = (long)Rows(catalog, "SELECT count(*) FROM peptidoforms_1pct").Single()[0]!;
            Assert.Multiple(() =>
            {
                Assert.That(Rows(catalog, $"SELECT column_name || ' ' || data_type FROM information_schema.columns WHERE table_name = '{Table}' ORDER BY ordinal_position")
                        .Select(r => (string)r[0]!),
                    Is.EqualTo(new[]
                    {
                        "protein_accession VARCHAR", "dataset_id VARCHAR", "bundle_id VARCHAR", "peptidoform_id VARCHAR",
                        "base_sequence VARCHAR", "best_q_value DOUBLE",
                    }), catalog);
                Assert.That(expected, Has.Count.GreaterThan(peptidoforms),
                    "the fixture has peptidoforms parsimony assigned to several accessions, so the unnest is exercised");
                Assert.That(Rows(catalog, $"SELECT * FROM {Table} ORDER BY ALL"), Is.EqualTo(expected), $"{catalog}: row for row");
                Assert.That(Rows(catalog, $"SELECT count(DISTINCT peptidoform_id) FROM {Table}").Single()[0], Is.EqualTo(peptidoforms),
                    "every accepted peptidoform is there, under at least one accession");
                Assert.That(Rows(catalog, $"SELECT count(*) FROM (SELECT DISTINCT protein_accession, dataset_id, peptidoform_id FROM {Table})").Single()[0],
                    Is.EqualTo((long)expected.Count), "accession, dataset and peptidoform are a key");
                Assert.That(Rows(catalog, $"SELECT rows, kind FROM catalog_tables WHERE table_name = '{Table}'").Single(),
                    Is.EqualTo(new object?[] { (long)expected.Count, "derived" }), catalog);
                // protein_datasets counts the same rows per (dataset, accession).
                Assert.That(Rows(catalog, $"SELECT dataset_id, protein_accession, count(*) FROM {Table} GROUP BY ALL ORDER BY ALL"),
                    Is.EqualTo(Rows(catalog, "SELECT dataset_id, protein_accession, n_peptidoforms FROM protein_datasets WHERE n_peptidoforms > 0 ORDER BY ALL")),
                    $"{catalog}: protein_datasets.n_peptidoforms");
            });
        }
        Assert.That(Rows(folder.Path, "SELECT table_type FROM information_schema.tables WHERE table_name = 'protein_peptidoforms'").Single()[0],
            Is.EqualTo("BASE TABLE"), "a folder copies the rows into the catalog file: the sort is what makes an accession a lookup");
    }

    [Test]
    public void ItsRowsAreStoredSortedByAccession()
    {
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var folder = Build(Folder);
        foreach (var catalog in new[] { file.Path, folder.Path })
        {
            var stored = Stored(catalog).Select(r => ((string)r[0]!, (string)r[1]!, (string)r[3]!)).ToList();
            var sorted = stored.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal)
                .ThenBy(k => k.Item3, StringComparer.Ordinal).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(stored.Select(k => k.Item1).Distinct().Count(), Is.GreaterThan(1), "more than one accession, so there is an order to keep");
                Assert.That(stored, Is.EqualTo(sorted), $"{catalog}: stored by accession, then dataset and peptidoform");
            });
        }
        Assert.That(Stored(folder.Path), Is.EqualTo(Stored(file.Path)), "both layouts store the same rows in the same order");
    }

    /// <summary>D48 step 2b for this table: a folder build keeps each deposit's rows in its derived directory, a later build
    /// reuses them, and a deposit whose file was cut is derived again; every one of them stores the table a one-file build
    /// does.</summary>
    [Test]
    public void AReusedDerivedPartialGivesTheSameTable()
    {
        var file = Build(Path.Combine(_root, "catalog.duckdb"));
        var first = Build(Folder);
        Assert.That((first.DerivedComputed, first.DerivedReused), Is.EqualTo((1, 0)));
        var deposit = Directory.GetDirectories(Path.Combine(Folder, "derived", CatalogBuilder.DerivationId, "PXD999999")).Single();
        var partial = Path.Combine(deposit, $"{Table}.parquet");
        Assert.That(File.Exists(partial), "the deposit's rows are kept beside its other derived rows");

        var original = File.ReadAllText(ManifestPath);
        Note(original, "a note");
        var second = Build(Folder);
        Assert.That((second.DerivedComputed, second.DerivedReused), Is.EqualTo((0, 1)), "a note is not a deposit's rows");

        File.WriteAllBytes(partial, File.ReadAllBytes(partial)[..64]);
        Note(original, "another note");
        var third = Build(Folder);
        Assert.That((third.DerivedComputed, third.DerivedReused), Is.EqualTo((1, 0)), "a cut file is derived again, not served");

        var expected = Stored(file.Path);
        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.Not.Empty);
            foreach (var built in new[] { first, second, third })
            {
                Assert.That(built.FailedChecks, Is.Empty);
                Assert.That(Stored(built.Path), Is.EqualTo(expected), $"{built.Path}: the one-file build's table, in its order");
            }
        });
    }
}
