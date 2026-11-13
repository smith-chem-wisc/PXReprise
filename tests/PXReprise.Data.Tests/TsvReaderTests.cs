using System.Text;
using PXReprise.Bundle;
using PXReprise.Ingest;
using PXReprise.Ingest.Sources;

namespace PXReprise.Data.Tests;

/// <summary>PXR-R48: <see cref="TsvReader"/> reads a table of any size one row at a time, and must read every table exactly
/// as the whole-file reader it replaced did. A proven-identical reader moves no bundle id (D26), so the proof is here:
/// the old reader, kept below as the oracle, against the new one on every kind of input either can meet.</summary>
public class TsvReaderTests
{
    // ---------------------------------------------------------------- the reader it replaced

    private const int OldFieldLimit = 131072;

    private static string OldDecode(byte[] bytes)
    {
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
    }

    private static IEnumerable<string> OldLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r') continue;
            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }

    private static (List<string> Header, List<List<string>> Rows) OldRead(byte[] bytes, IngestRules rules)
    {
        var text = OldDecode(bytes);
        var header = new List<string>();
        var rows = new List<List<string>>();
        bool first = true;
        foreach (var line in OldLines(text))
        {
            List<string> row = line.Length == 0 ? [] : line.Split('\t').ToList();
            foreach (var field in row)
                if (field.Length > OldFieldLimit && rules == IngestRules.Python0320)
                    throw new InvalidDataException($"field larger than field limit ({OldFieldLimit})");
            if (first)
            {
                header = row;
                first = false;
            }
            else if (row.Count > 0)
            {
                rows.Add(row);
            }
        }
        return (header, rows);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Hands out one byte per read, so every boundary a real read could fall on is crossed.</summary>
    private sealed class Trickle(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }

    /// <summary>A table of any length that exists only as it is read: no file, no array.</summary>
    private sealed class GeneratedTable(byte[] header, byte[] row, long rows) : Stream
    {
        private long _position;
        private readonly long _length = header.Length + row.Length * rows;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int written = 0;
            while (written < buffer.Length && _position < _length)
            {
                var (source, at) = _position < header.Length
                    ? (header, (int)_position)
                    : (row, (int)((_position - header.Length) % row.Length));
                int n = Math.Min(buffer.Length - written, source.Length - at);
                source.AsSpan(at, n).CopyTo(buffer[written..]);
                written += n;
                _position += n;
            }
            return written;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (List<string> Header, List<List<string>> Rows) NewRead(Stream stream, IngestRules rules)
    {
        using var table = new TsvReader(stream, rules);
        var rows = new List<List<string>>();
        while (table.Read()) rows.Add(table.Row());
        Assert.That(table.Rows, Is.EqualTo(rows.Count), "Rows counts what Read returned");
        return (table.Header, rows);
    }

    /// <summary>Both readers give the same table, or both raise the same type.</summary>
    private static void SameAsOld(byte[] bytes, string label, IngestRules rules = IngestRules.Current)
    {
        (List<string> Header, List<List<string>> Rows)? expected = null;
        Type? expectedError = null;
        try { expected = OldRead(bytes, rules); }
        catch (Exception e) when (e is DecoderFallbackException or InvalidDataException) { expectedError = e.GetType(); }

        foreach (var (kind, open) in new (string, Func<Stream>)[]
        {
            ("whole", () => new MemoryStream(bytes)),
            ("one byte at a time", () => new Trickle(bytes)),
        })
        {
            if (expectedError is not null)
            {
                Assert.That(() => NewRead(open(), rules), Throws.TypeOf(expectedError), $"{label}, {kind}");
                continue;
            }
            var (header, rows) = NewRead(open(), rules);
            Assert.Multiple(() =>
            {
                Assert.That(header, Is.EqualTo(expected!.Value.Header), $"{label}, {kind}: header");
                Assert.That(rows, Is.EqualTo(expected!.Value.Rows), $"{label}, {kind}: rows");
            });
        }
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // ---------------------------------------------------------------- every kind of input

    private static IEnumerable<TestCaseData> Tables()
    {
        foreach (var (name, text) in new[]
        {
            ("empty", ""),
            ("one newline", "\n"),
            ("two newlines", "\n\n"),
            ("header, no terminator", "a\tb"),
            ("header, terminator", "a\tb\n"),
            ("LF", "a\tb\n1\t2\n"),
            ("CRLF", "a\tb\r\n1\t2\r\n"),
            ("lone CR", "a\tb\r1\t2\r"),
            ("LF then CR is two terminators", "a\n\r1\n\r2"),
            ("CR CR LF", "a\r\r\n1\r\r\n"),
            ("mixed", "a\r\n1\n2\r3"),
            ("no last terminator", "a\n1\n2"),
            ("blank first line is an empty header", "\na\tb\n1\t2\n"),
            ("blank rows dropped", "a\n\n1\n\n\n2\n"),
            ("rows of only tabs kept", "a\tb\n\t\n\t\t\t\n"),
            ("short and long rows", "a\tb\tc\n1\n1\t2\t3\t4\t5\n"),
            ("empty cells", "a\tb\tc\n\t2\t\n"),
            ("quotes are ordinary", "a\tb\n\"1\t2\"\n\"x\ny\"\n"),
            ("repeated names", "a\ta\tb\n1\t2\t3\n"),
            ("vertical tab, form feed and NEL do not end a line", "a\vb\tc\fd\n1\u00852\t3\u20284\u20295\n"),
            ("non-BMP", "a\tb\n\U0001F600\t\u00E9\u20AC\n"),
            ("NUL", "a\0b\n1\0\n"),
        })
        {
            yield return new TestCaseData(Utf8(text)).SetName($"Table: {name}");
            yield return new TestCaseData(Concat(Bom, Utf8(text))).SetName($"Table: BOM, {name}");
        }
        yield return new TestCaseData(Bom).SetName("Table: only a BOM");
        yield return new TestCaseData(Concat(Bom, Bom, Utf8("a\n1\n"))).SetName("Table: only the first of two BOMs is dropped");
        yield return new TestCaseData(Concat(Utf8("a\n"), Bom, Utf8("1\n"))).SetName("Table: a BOM inside the file is a character");
        yield return new TestCaseData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }).SetName("Table: a UTF-16 BOM never switches the encoding");
        yield return new TestCaseData(new byte[] { 0xEF, 0xBB }).SetName("Table: half a BOM");
        yield return new TestCaseData(new byte[] { 0x61, 0xFF, 0x0A }).SetName("Table: invalid byte");
        yield return new TestCaseData(Concat(Utf8("a\n"), [0xE2, 0x82])).SetName("Table: sequence cut at the end of the file");
        yield return new TestCaseData(new byte[] { 0x80, 0x0A }).SetName("Table: lone continuation byte");
        yield return new TestCaseData(new byte[] { 0xC0, 0xAF, 0x0A }).SetName("Table: overlong");
        yield return new TestCaseData(new byte[] { 0xED, 0xA0, 0x80, 0x0A }).SetName("Table: encoded surrogate");
    }

    [TestCaseSource(nameof(Tables))]
    public void ReadsLikeTheWholeFileReader(byte[] bytes) => SameAsOld(bytes, "table");

    /// <summary>A terminator or a multi-byte character cut by the reader's own buffer, at every offset around it.</summary>
    [TestCase("\r\n")]
    [TestCase("\r")]
    [TestCase("\n")]
    [TestCase("\u20AC\n")]
    [TestCase("\U0001F600\n")]
    public void ReadsAcrossItsBuffers(string after)
    {
        const int buffer = 1 << 16;
        for (int pad = buffer - 6; pad <= buffer + 6; pad++)
        {
            var bytes = Utf8("h\n" + new string('a', pad) + after + "b\tc\n");
            var (header, rows) = NewRead(new MemoryStream(bytes), IngestRules.Current);
            var expected = OldRead(bytes, IngestRules.Current);
            Assert.Multiple(() =>
            {
                Assert.That(header, Is.EqualTo(expected.Header), $"pad {pad}: header");
                Assert.That(rows.Select(r => string.Join('\t', r)), Is.EqualTo(expected.Rows.Select(r => string.Join('\t', r))), $"pad {pad}: rows");
            });
        }
    }

    /// <summary>The class, not the list above: tables built at random from everything that can matter.</summary>
    [Test]
    public void ReadsRandomTablesLikeTheWholeFileReader()
    {
        byte[][] pieces =
        [
            [0x61], [0x62], [0x09], [0x09], [0x0A], [0x0A], [0x0D], [0x0D, 0x0A], [0x22], [0x00], [0x0B], [0x0C],
            [0xC3, 0xA9], [0xE2, 0x82, 0xAC], [0xF0, 0x9F, 0x98, 0x80], [0xC2, 0x85], [0xE2, 0x80, 0xA8], Bom, [0xFF], [0xE2, 0x82],
        ];
        var random = new Random(48);
        for (int i = 0; i < 5000; i++)
        {
            // One table in five may hold an invalid piece; the others draw from the valid ones.
            int usable = i % 5 == 0 ? pieces.Length : pieces.Length - 2;
            var bytes = Enumerable.Range(0, random.Next(0, 40)).SelectMany(_ => pieces[random.Next(usable)]).ToArray();
            SameAsOld(bytes, $"random table {i}: {Convert.ToHexString(bytes)}");
        }
    }

    // ---------------------------------------------------------------- the rule of the old parser

    [Test]
    public void TheOldParsersFieldLimitIsKeptOnlyForTheOldRules()
    {
        var longField = new string('x', OldFieldLimit + 1);
        foreach (var (name, text) in new[]
        {
            ("in a row", $"a\tb\n1\t{longField}\n"),
            ("in the header", $"{longField}\tb\n1\t2\n"),
            ("at the limit", $"a\n{longField[1..]}\n"),
            ("in a dropped cell", $"a\n1\t{longField}\n"),
        })
        {
            SameAsOld(Utf8(text), name, IngestRules.Python0320);
            SameAsOld(Utf8(text), name, IngestRules.Current);
        }
    }

    // ---------------------------------------------------------------- cells by name

    [Test]
    public void ACellReadsAsIterDictsReadsIt()
    {
        const string text = "a\tb\ta\n1\t2\t3\n4\n5\t6\n";
        var bytes = Utf8(text);
        var (header, rows) = OldRead(bytes, IngestRules.Current);
        var dicts = PXReprise.Ingest.Readers.IterDicts(header, rows).ToList();
        using var table = new TsvReader(new MemoryStream(bytes));
        int a = table.Column("a"), b = table.Column("b"), none = table.Column("none");
        Assert.That(none, Is.EqualTo(-1));
        foreach (var dict in dicts)
        {
            Assert.That(table.Read(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(table.Cell(a), Is.EqualTo(dict["a"]), "a repeated name reads its last position, blank on a short row");
                Assert.That(table.Cell(b), Is.EqualTo(dict["b"]));
                Assert.That(table.Cell(none), Is.Empty);
                Assert.That(table.Span(none).IsEmpty, Is.True);
            });
        }
        Assert.That(table.Read(), Is.False);
    }

    // ---------------------------------------------------------------- past the old limits

    /// <summary>The class PXR-R48 reported: a table past .NET's longest string, 1,073,741,791 characters. It is generated as
    /// it is read, so the test needs neither the disk nor the memory the table would take.</summary>
    [Test]
    public void ATablePastTheLongestStringIsRead()
    {
        var header = Utf8("File Name\tScan\tFull Sequence\tPIP Q-Value\n");
        var row = Utf8("run_01\t12345\tPEPTIDEK\t0.001\n");
        const long longestString = 1_073_741_791;
        long rows = longestString / row.Length + 1000;
        using var table = new TsvReader(new GeneratedTable(header, row, rows));
        int sequence = table.Column("Full Sequence");
        long seen = 0;
        while (table.Read())
            if (table.Span(sequence).SequenceEqual("PEPTIDEK")) seen++;
        Assert.Multiple(() =>
        {
            Assert.That(header.Length + row.Length * rows, Is.GreaterThan(longestString), "the table is past the limit");
            Assert.That(seen, Is.EqualTo(rows));
            Assert.That(table.Rows, Is.EqualTo(rows));
        });
    }

    // ---------------------------------------------------------------- PeakQuality

    /// <summary>PeakQuality as it was: the whole table, a dictionary per row, and a run lookup per row.</summary>
    private static Dictionary<(string Run, string Sequence), (double? PipQValue, bool? MbrKept)> OldPeakQuality(
        byte[] bytes, RunNameMap runNames, double threshold)
    {
        var output = new Dictionary<(string Run, string Sequence), (double? PipQValue, bool? MbrKept)>();
        var (header, rows) = OldRead(bytes, IngestRules.Current);
        foreach (var row in PXReprise.Ingest.Readers.IterDicts(header, rows))
        {
            string Cell(string column) => row.TryGetValue(column, out var v) ? v : "";
            var reported = Cell("File Name");
            var run = runNames.Resolve(reported) ?? reported;
            var sequence = Cell("Full Sequence");
            if (sequence.Length == 0) continue;
            double? pipQ = PyFormat.TryParseFloat(Cell("PIP Q-Value"), out var q) ? q : null;
            var key = (run, sequence);
            if (!output.TryGetValue(key, out var current)
                || (pipQ is not null && (current.PipQValue is null || pipQ < current.PipQValue)))
                output[key] = (pipQ, pipQ is null ? null : pipQ <= threshold);
        }
        return output;
    }

    private static IEnumerable<TestCaseData> PeakTables()
    {
        const string h = "File Name\tFull Sequence\tPIP Q-Value\n";
        foreach (var (name, text) in new[]
        {
            ("the best q-value wins", h + "run1\tPEP\t0.5\nrun1\tPEP\t0.001\nrun1\tPEP\t0.2\n"),
            ("runs are kept apart", h + "run1\tPEP\t0.5\nrun2\tPEP\t0.001\n"),
            ("blank and dash cells are no q-value", h + "run1\tPEP\t\nrun1\tPEP\t-\nrun2\tPEP\t\nrun2\tPEP\t0.3\n"),
            ("NaN stays first and is then replaced only by rule", h + "run1\tPEP\tNaN\nrun1\tPEP\t0.1\nrun2\tPEP\t0.1\nrun2\tPEP\tNaN\n"),
            ("at the threshold", h + "run1\tPEP\t0.01\nrun1\tKEP\t0.0100001\n"),
            ("an unmatched name keeps its count", h + "gone\tPEP\t0.1\ngone\tKEP\t0.2\nrun1\tPEP\t0.3\ngone\tPEP\t0.05\n"),
            ("a row with no sequence still resolves its run", h + "gone\t\t0.1\nrun1\t\t0.1\ngone\t\t0.1\n"),
            ("a short row", h + "run1\nrun1\tPEP\ngone\n"),
            ("cells past the header", h + "run1\tPEP\t0.1\textra\tmore\n"),
            ("a name a pipeline suffixed", h + "run1-calib\tPEP\t0.1\nRUN1\tPEP\t0.05\n"),
            ("columns in another order", "PIP Q-Value\tX\tFull Sequence\tFile Name\n0.1\tq\tPEP\trun1\n"),
            ("a repeated column reads its last", "File Name\tFile Name\tFull Sequence\tPIP Q-Value\tPIP Q-Value\nrun1\trun2\tPEP\t0.5\t0.1\n"),
            ("no sequence column", "File Name\tPIP Q-Value\nrun1\t0.1\ngone\t0.1\n"),
            ("no file column", "Full Sequence\tPIP Q-Value\nPEP\t0.1\n"),
            ("no q-value column", "File Name\tFull Sequence\nrun1\tPEP\n"),
            ("only a header", h),
            ("empty", ""),
        })
            yield return new TestCaseData(Utf8(text)).SetName($"PeakQuality: {name}");
    }

    [TestCaseSource(nameof(PeakTables))]
    public void PeakQualityIsUnchanged(byte[] bytes)
    {
        var folder = Directory.CreateTempSubdirectory("tsvreader-").FullName;
        try
        {
            var path = Path.Combine(folder, "AllQuantifiedPeaks.tsv");
            File.WriteAllBytes(path, bytes);
            const double threshold = 0.01;
            string[] deposited = ["run1", "run2"];

            var oldRuns = new RunNameMap(deposited);
            var expected = OldPeakQuality(bytes, oldRuns, threshold);

            var newRuns = new RunNameMap(deposited);
            var log = new ReaderLog();
            var actual = Quant.PeakQuality(path, "PXD000001", newRuns, threshold, log);

            var (_, oldRows) = OldRead(bytes, IngestRules.Current);
            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.EquivalentTo(expected), "the index");
                Assert.That(newRuns.Unmatched, Is.EquivalentTo(oldRuns.Unmatched), "unmatched names, with their counts");
                var entry = log.Entries.Single();
                Assert.That(entry["file"], Is.EqualTo("AllQuantifiedPeaks.tsv"));
                Assert.That(entry["backend"], Is.EqualTo("datarepo-tsv"));
                Assert.That(entry["rows"], Is.EqualTo((long)oldRows.Count), "the reader log counts every row read");
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
