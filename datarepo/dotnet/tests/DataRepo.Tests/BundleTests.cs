using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataRepo.Bundle;
using DataRepo.SchemaGen;

namespace DataRepo.Tests;

/// <summary>Phase 1: the schema, the writer and the content hash agree with the Python 0.32.0 ingester.</summary>
/// <remarks>The fixture under <c>Fixtures/python-0.32.0</c> is the Python ingester's own output for the
/// repository's test dataset (see its PROVENANCE.md), so these tests compare against what Python wrote,
/// not against what we expect it to have written.</remarks>
public class BundleTests
{
    private static readonly string Fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "python-0.32.0");
    private static readonly string FixtureBundle = Path.Combine(Fixtures, "PXD999999", "aeb10630abbcaf72");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [Test]
    public void TheGeneratedTablesAreCurrent()
    {
        var root = RepoRoot();
        var rendered = SchemaGenerator.Render(Path.Combine(root, "schema", "datarepo.yaml"), Path.Combine(root, "schema", "study"));
        var committed = File.ReadAllText(Path.Combine(root, "dotnet", "src", "DataRepo.Bundle", "Generated", "Tables.g.cs")).Replace("\r\n", "\n");
        Assert.That(committed, Is.EqualTo(rendered), "schema changed: run dotnet run --project dotnet/src/DataRepo.SchemaGen");
    }

    [Test]
    public void TheFixtureBundleRoundTripsIdentically()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "datarepo-roundtrip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = RoundTrip.Verify(FixtureBundle, scratch);
            Assert.That(result.RecomputedId, Is.EqualTo("aeb10630abbcaf72"), "the bundle id rule differs from Python's");
            Assert.That(result.Tables, Has.Count.EqualTo(17));
            foreach (var t in result.Tables)
            {
                Assert.That(t.SchemaMatchesSpec, $"{t.Table}: stored schema vs generated spec: {t.FirstDifference}");
                Assert.That(t.SchemaMatchesOriginal, $"{t.Table}: rewritten schema: {t.FirstDifference}");
                Assert.That(t.RowsIdentical, $"{t.Table}: {t.FirstDifference}");
            }
            Assert.That(result.IntegrityProblems, Is.Empty);
            Assert.That(result.Passed);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    [Test]
    public void ADeclarationHashesToPythonsBytes()
    {
        // Python: json.dumps(entry.content_declaration(), sort_keys=True, default=str, ensure_ascii=False)
        var text = File.ReadAllText(Path.Combine(Fixtures, "manifest_entry_declaration.json"), new UTF8Encoding(false));
        using var doc = JsonDocument.Parse(text);
        var payload = Plain(doc.RootElement);
        Assert.That(PyFormat.Json(payload), Is.EqualTo(text));

        var writer = new BundleWriter(Path.GetTempPath(), "PXD999999");
        var entry = writer.AddDeclaration("manifest_entry", payload);
        Assert.That(entry["sha256"], Is.EqualTo("506a4b86bd8abf6e04289abdb9ceb8ad2f2755762023fa3f3a49df167fe61d84"));
    }

    /// <summary>A parsed JSON value as the plain objects a row or a declaration carries.</summary>
    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    [TestCase(0.1, "0.1")]
    [TestCase(1e-05, "1e-05")]
    [TestCase(0.0001, "0.0001")]
    [TestCase(1.5e-07, "1.5e-07")]
    [TestCase(1e16, "1e+16")]
    [TestCase(1e15, "1000000000000000.0")]
    [TestCase(123.0, "123.0")]
    [TestCase(-3.0, "-3.0")]
    [TestCase(2.5, "2.5")]
    [TestCase(1.0 / 3.0, "0.3333333333333333")]
    [TestCase(1e22, "1e+22")]
    [TestCase(123456789012345678.0, "1.2345678901234568e+17")]
    [TestCase(5e-324, "5e-324")]
    [TestCase(0.0, "0.0")]
    [TestCase(180.00184, "180.00184")]
    public void FloatsAreWrittenAsPythonReprWritesThem(double value, string python) =>
        Assert.That(PyFormat.FloatRepr(value), Is.EqualTo(python));

    [Test]
    public void JsonEscapesAsPythonDoes()
    {
        Assert.That(PyFormat.Json(new Dictionary<string, object?> { ["b"] = "é\"\n\u0001", ["a"] = null }),
            Is.EqualTo("{\"a\": null, \"b\": \"é\\\"\\n\\u0001\"}"));
        // Checked against CPython: json.dumps('é\x7f') == '"\\u00e9\\u007f"'; ensure_ascii=False keeps DEL as is.
        Assert.That(PyFormat.Json("é\u007f", ensureAscii: true), Is.EqualTo("\"\\u00e9\\u007f\""));
        Assert.That(PyFormat.Json("\u007f", ensureAscii: false), Is.EqualTo("\"\u007f\""));
    }

    private static readonly ColumnSpec Text = new("t", ColumnType.String, false, true);
    private static readonly ColumnSpec Number = new("n", ColumnType.Float64, false, true);
    private static readonly ColumnSpec Count = new("c", ColumnType.Int64, false, true);
    private static readonly ColumnSpec Flag = new("f", ColumnType.Boolean, false, true);
    private static readonly ColumnSpec Names = new("l", ColumnType.String, true, true);

    [Test]
    public void ABlankOrNaNIsAbsenceNeverZeroOrEmpty()
    {
        Assert.That(Coercion.Coerce("  ", Text), Is.Null);
        Assert.That(Coercion.Coerce(double.NaN, Number), Is.Null);
        Assert.That(Coercion.Coerce("nan", Number), Is.Null);
        Assert.That(Coercion.Coerce("", Names), Is.Null);
        Assert.That(Coercion.Coerce(Array.Empty<string>(), Names), Is.Null);
    }

    [Test]
    public void ValuesAreBentAsThePythonWriterBendsThem()
    {
        Assert.That(Coercion.Coerce("3.7", Count), Is.EqualTo(3L));
        Assert.That(Coercion.Coerce(-3.7, Count), Is.EqualTo(-3L));
        Assert.That(Coercion.Coerce(" Yes ", Flag), Is.True);
        Assert.That(Coercion.Coerce("no", Flag), Is.False);
        Assert.That(Coercion.Coerce(2.0, Text), Is.EqualTo("2.0"));
        Assert.That(Coercion.Coerce(true, Text), Is.EqualTo("True"));
        Assert.That(Coercion.Coerce("P12345", Names), Is.EqualTo(new List<object?> { "P12345" }));
    }

    [Test]
    public void AnUnknownColumnOrAMissingRequiredValueIsRefused()
    {
        var columns = new[] { new ColumnSpec("id", ColumnType.String, false, false) };
        var unknown = new Row { ["id"] = "a", ["extra"] = 1 };
        Assert.That(() => ArrowTables.FromRows("x", columns, [unknown]),
            Throws.TypeOf<IngestException>().With.Message.Contains("unknown columns ['extra']"));
        var blank = new Row { ["id"] = " " };
        Assert.That(() => ArrowTables.FromRows("x", columns, [blank]),
            Throws.TypeOf<IngestException>().With.Message.Contains("no value for required 'id'"));
    }

    [Test]
    public void ADanglingReferenceOrDuplicateIdentifierIsFound()
    {
        var tables = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
        {
            ["datasets"] = [new Row { ["dataset_id"] = "D" }],
            ["runs"] = [new Row { ["run_id"] = "D:r1", ["dataset_id"] = "D" }, new Row { ["run_id"] = "D:r1", ["dataset_id"] = "X" }],
        };
        var problems = Integrity.Check(tables);
        Assert.That(problems, Has.Some.Contains("runs.run_id: 1 duplicate identifier(s), e.g. D:r1"));
        Assert.That(problems, Has.Some.Contains("runs.dataset_id -> datasets.dataset_id: 1 value(s) with no matching row, e.g. X"));
    }

    [Test]
    public void IdenticalRowsCollapseAndDifferingOnesDoNot()
    {
        var tables = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>
        {
            ["protein_groups"] = [new Row { ["protein_group_id"] = "g", ["q"] = 0.1 }, new Row { ["protein_group_id"] = "g", ["q"] = 0.1 }],
            ["proteins"] = [new Row { ["protein_accession"] = "P", ["q"] = 1.0 }, new Row { ["protein_accession"] = "P", ["q"] = 2.0 }],
            ["psms"] = [new Row { ["psm_id"] = "s" }, new Row { ["psm_id"] = "s" }],
        };
        var collapses = Integrity.CollapseExactDuplicates(tables);
        Assert.That(collapses, Has.Count.EqualTo(1));
        Assert.That(collapses[0], Is.EqualTo(new Collapse("protein_groups", ["protein_group_id"], "g", 2)).Using<Collapse>((a, b) =>
            a.Table == b.Table && a.Identifier == b.Identifier && a.Written == b.Written));
        Assert.That(tables["protein_groups"], Has.Count.EqualTo(1));
        Assert.That(tables["proteins"], Has.Count.EqualTo(2), "rows that differ are left for Check to refuse");
        Assert.That(tables["psms"], Has.Count.EqualTo(2), "a PSM is an event: never collapsed");
    }

    [Test]
    public void AWrittenBundleIsContentAddressedAndNotOverwritten()
    {
        var store = Path.Combine(Path.GetTempPath(), "datarepo-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A real, complete dataset row: the one Python wrote for the fixture.
            var (_, datasetRows) = ArrowTables.ReadParquet(Path.Combine(FixtureBundle, "datasets.parquet"));
            var writer = new BundleWriter(store, "PXD999999");
            writer.AddDeclaration("manifest_entry", new Dictionary<string, object?> { ["accession"] = "PXD999999" });
            writer.Add("datasets", datasetRows);
            var output = writer.Write();
            Assert.That(Path.GetFileName(output), Is.EqualTo(writer.BundleId));
            var (_, rows) = ArrowTables.ReadParquet(Path.Combine(output, "datasets.parquet"));
            Assert.That(RoundTrip.FirstDifference(datasetRows, rows), Is.Null);
            Assert.That(() => writer.Write(), Throws.TypeOf<IngestException>().With.Message.Contains("already exists"));
            Assert.That(writer.Write(overwrite: true), Is.EqualTo(output));

            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"datarepo/{BundleWriter.IngesterVersion}\nschema/{Tables.SchemaVersion}\nPXD999999\n"
                + $"manifest_entry\t{writer.Sources[0]["sha256"]}\n")))[..16];
            Assert.That(writer.BundleId, Is.EqualTo(expected));
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }
    }

    [Test]
    public void ADanglingReferenceStopsTheWrite()
    {
        var store = Path.Combine(Path.GetTempPath(), "datarepo-store-" + Guid.NewGuid().ToString("N"));
        var writer = new BundleWriter(store, "PXD000001");
        writer.Add("runs", [new Row { ["run_id"] = "PXD000001:r", ["dataset_id"] = "PXD000001", ["file_name"] = "r.raw" }]);
        Assert.That(() => writer.Write(), Throws.TypeOf<IngestException>().With.Message.Contains("do not hold together"));
        Assert.That(Directory.Exists(store), Is.False, "nothing is written when the tables do not hold together");
    }
}
