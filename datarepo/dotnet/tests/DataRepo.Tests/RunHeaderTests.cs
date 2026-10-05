using Apache.Arrow;
using DataRepo.Bundle;
using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>G87: a run's start time and instrument, read from the QC report PXReprise writes (DATAREPO-72,
/// PXReprise 52cd138), and kept out of a Python 0.32.0 parity bundle.</summary>
/// <remarks>The QC entries use the shapes PXReprise's <c>SpectraQc.Metadata</c> writes (.NET
/// <c>yyyy-MM-ddTHH:mm:ss.FFFFFFFK</c>: no zone for a RAW header, <c>Z</c> for a UTC reader, an offset for a local
/// one; a key only for a value the reader gave), with the values of their 013 table.</remarks>
public class RunHeaderTests
{
    private static Dictionary<string, object?> Entry(params (string Key, object? Value)[] keys)
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pass"] = true, ["scans"] = 100L, ["ms2"] = 80L, ["run_minutes"] = 120.5,
            ["ms2_analyzer_dissociation"] = new Dictionary<string, object?> { ["Orbitrap/HCD"] = 80L },
        };
        foreach (var (key, value) in keys) entry[key] = value;
        return entry;
    }

    private static Dictionary<string, Row> Build(
        Dictionary<string, object?> qc, IngestRules rules = IngestRules.Current,
        Dictionary<string, IReadOnlyDictionary<string, object?>>? facts = null)
    {
        var (runs, _) = Runs.Build("PXD000001", null, qc,
            facts ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal), rules: rules);
        return runs.ToDictionary(r => (string)r["file_name"]!, StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> Report() => new(StringComparer.Ordinal)
    {
        ["local.raw"] = Entry(("start_time", "2021-03-16T12:09:07.375"), ("instrument_model", "Orbitrap Fusion"), ("instrument_serial", "FSN10189")),
        ["utc.raw"] = Entry(("start_time", "2021-03-16T12:09:07.375Z"), ("instrument_model", "Orbitrap Exploris 480"),
            ("instrument_model_accession", "MS:1003028"), ("instrument_serial", "MA10422C")),
        ["offset.raw"] = Entry(("start_time", "2016-05-13T14:21:12-05:00"), ("instrument_model", "LTQ Orbitrap Velos")),
        ["unread.raw"] = Entry(),
    };

    [Test]
    public void AZonelessStartTimeIsKeptVerbatimAndNeverReadAsUtc()
    {
        var run = Build(Report())["local.raw"];
        Assert.That(run["acquisition_start_local"], Is.EqualTo("2021-03-16T12:09:07.375"));
        Assert.That(run["acquisition_datetime"], Is.Null, "a time with no zone must not reach the UTC column");
        Assert.That(run["instrument_model"], Is.EqualTo("Orbitrap Fusion"));
        Assert.That(run["instrument_model_source"], Is.EqualTo(Runs.ModelFromQcReport));
        Assert.That(run["instrument_model_accession"], Is.Null, "no accession key: not given, so NULL");
        Assert.That(run["instrument_serial"], Is.EqualTo("FSN10189"));
    }

    [Test]
    public void AZonedStartTimeIsConvertedToUtc()
    {
        var runs = Build(Report());
        Assert.That(runs["utc.raw"]["acquisition_datetime"],
            Is.EqualTo(new DateTimeOffset(2021, 3, 16, 12, 9, 7, 375, TimeSpan.Zero)));
        Assert.That(runs["utc.raw"]["acquisition_start_local"], Is.Null);
        Assert.That(runs["utc.raw"]["instrument_model_accession"], Is.EqualTo("MS:1003028"));
        Assert.That(runs["utc.raw"]["instrument_serial"], Is.EqualTo("MA10422C"));

        var offset = (DateTimeOffset)runs["offset.raw"]["acquisition_datetime"]!;
        Assert.That(offset, Is.EqualTo(new DateTimeOffset(2016, 5, 13, 19, 21, 12, TimeSpan.Zero)));
        Assert.That(offset.Offset, Is.EqualTo(TimeSpan.Zero), "stored in UTC");
        Assert.That(runs["offset.raw"]["acquisition_start_local"], Is.Null);
        Assert.That(runs["offset.raw"]["instrument_serial"], Is.Null);
    }

    [Test]
    public void MissingKeysAreNull()
    {
        var run = Build(Report())["unread.raw"];
        foreach (var column in new[] { "acquisition_datetime", "acquisition_start_local", "instrument_model",
                     "instrument_model_source", "instrument_model_accession", "instrument_serial" })
            Assert.That(run[column], Is.Null, column);
    }

    [Test]
    public void TheSdrfInstrumentIsTheFallbackAndItsTermTravelsWithIt()
    {
        var facts = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["local"] = new Dictionary<string, object?> { ["instrument_model"] = "Q Exactive", ["instrument_term"] = "MS:1001911" },
            ["unread"] = new Dictionary<string, object?> { ["instrument_model"] = "Q Exactive", ["instrument_term"] = "MS:1001911" },
        };
        var runs = Build(Report(), facts: facts);
        Assert.That(runs["unread.raw"]["instrument_model"], Is.EqualTo("Q Exactive"));
        Assert.That(runs["unread.raw"]["instrument_model_source"], Is.EqualTo(Runs.ModelFromSdrf));
        Assert.That(runs["unread.raw"]["instrument_model_accession"], Is.EqualTo("MS:1001911"));
        // The file's own header wins, and is never paired with the SDRF's term for what may be another instrument.
        Assert.That(runs["local.raw"]["instrument_model"], Is.EqualTo("Orbitrap Fusion"));
        Assert.That(runs["local.raw"]["instrument_model_source"], Is.EqualTo(Runs.ModelFromQcReport));
        Assert.That(runs["local.raw"]["instrument_model_accession"], Is.Null);
    }

    [Test]
    public void Python0320ReadsNoneOfTheKeys()
    {
        var facts = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["local"] = new Dictionary<string, object?> { ["instrument_model"] = "Q Exactive", ["instrument_term"] = "MS:1001911" },
        };
        var runs = Build(Report(), IngestRules.Python0320, facts);
        foreach (var run in runs.Values)
        {
            Assert.That(run.Keys, Has.None.AnyOf("acquisition_start_local", "instrument_model_source",
                "instrument_model_accession", "instrument_serial"));
            Assert.That(run["acquisition_datetime"], Is.Null);
        }
        Assert.That(runs["local.raw"]["instrument_model"], Is.EqualTo("Q Exactive"), "0.32.0 took the SDRF's");
        Assert.That(runs["unread.raw"]["instrument_model"], Is.Null);
    }

    [TestCase("2021-03-16 12:09:07")]
    [TestCase("2021-03-16T12:09")]
    [TestCase("2021-13-16T12:09:07")]
    [TestCase("16/03/2021 12:09:07")]
    [TestCase("2021-03-16T12:09:07 EST")]
    public void AMalformedStartTimeIsRefused(string value)
    {
        var qc = new Dictionary<string, object?> { ["a.raw"] = Entry(("start_time", value)) };
        var error = Assert.Throws<IngestException>(() => Build(qc))!;
        Assert.That(error.Message, Does.Contain("a.raw").And.Contain("start_time"));
    }

    [Test]
    public void AHeaderKeyThatIsNotTextIsRefused()
    {
        var qc = new Dictionary<string, object?> { ["a.raw"] = Entry(("instrument_serial", 10189L)) };
        Assert.Throws<IngestException>(() => Build(qc));
    }

    [Test]
    public void AParityBundleWritesTheColumnsPython0320Wrote()
    {
        var rows = Build(Report()).Values.OrderBy(r => (string)r["file_name"]!, StringComparer.Ordinal)
            .Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();
        var current = ArrowTables.FromRows("runs", rows);
        Assert.That(current.Schema.FieldsList.Select(f => f.Name), Is.SupersetOf(SchemaContract.AddedColumns.Select(a => a.Column)));
        var utc = (TimestampArray)current.Column(current.Schema.GetFieldIndex("acquisition_datetime"));
        Assert.That(utc.GetTimestamp(rows.FindIndex(r => (string)r["file_name"]! == "utc.raw")),
            Is.EqualTo(new DateTimeOffset(2021, 3, 16, 12, 9, 7, 375, TimeSpan.Zero)));

        var parityRows = Build(Report(), IngestRules.Python0320).Values.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();
        using (SchemaContract.Python0320())
        {
            var parity = ArrowTables.FromRows("runs", parityRows);
            var expected = Tables.ByName["runs"].Columns.Select(c => c.Name)
                .Where(n => !SchemaContract.AddedColumns.Any(a => a.Column == n)).ToList();
            Assert.That(parity.Schema.FieldsList.Select(f => f.Name), Is.EqualTo(expected), "0.0.13's columns, in order");
            // A current row cannot be written as a parity one: the columns it fills do not exist there.
            Assert.Throws<IngestException>(() => ArrowTables.FromRows("runs", rows));
        }
    }

    [Test]
    public void ColumnsAtFollowTheVersionThatAddedThem()
    {
        var runs = Tables.ByName["runs"];
        Assert.That(SchemaContract.ColumnsAt(runs, Tables.SchemaVersion), Is.EqualTo(runs.Columns));
        Assert.That(SchemaContract.ColumnsAt(runs, "0.0.14").Select(c => c.Name), Has.None.EqualTo("instrument_serial"));
        Assert.That(SchemaContract.ColumnsAt(Tables.ByName["psms"], "0.0.13"), Is.EqualTo(Tables.ByName["psms"].Columns));
        foreach (var (version, table, column) in SchemaContract.AddedColumns)
            Assert.That(Tables.ByName[table].Columns.Select(c => c.Name), Does.Contain(column), $"{version} {table}.{column}");
    }
}
