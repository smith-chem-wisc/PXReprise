using System.Text;
using DataRepo.Bundle;

namespace DataRepo.Study;

/// <summary>
/// Python's <c>csv.DictReader</c> over a file opened with <c>encoding="utf-8-sig", newline=""</c>, in the
/// default <c>excel</c> dialect with a chosen delimiter.
/// </summary>
/// <remarks>
/// A delivered table's cells are rows, so the splitting has to be Python's and not merely a reasonable CSV
/// reader's: the quote character is honoured in a <c>.tsv</c> as well as a <c>.csv</c>, a quote inside an
/// unquoted field is literal, characters after a closing quote are kept (<c>strict=False</c>), a quoted field
/// may span lines, a blank line is skipped, a short row's missing cells are <c>None</c>, and a long row's
/// extra cells go under the key <c>None</c> (<see cref="RestKey"/>). The state machine is
/// <c>Modules/_csv.c</c>'s <c>parse_process_char</c>, driven line by line as <c>Reader.__next__</c> drives it.
/// </remarks>
public static class PyCsv
{
    /// <summary>Where a row longer than the header keeps its extra cells: Python's <c>restkey=None</c>.</summary>
    /// <remarks>A .NET dictionary cannot hold a null key, so this stands in for it. It is never a column
    /// name: no schema column contains a NUL.</remarks>
    public const string RestKey = "\0restkey";

    private const char Quote = '"';

    private enum State { StartRecord, StartField, InField, InQuotedField, QuoteInQuotedField, EatCrnl }

    /// <summary>The file's header, or null when the file has no line at all.</summary>
    /// <param name="path">The file.</param>
    /// <param name="delimiter">The field delimiter.</param>
    /// <param name="rows">The data rows as <c>DictReader</c> yields them (blank lines skipped).</param>
    /// <exception cref="DecoderFallbackException">The file is not UTF-8.</exception>
    public static List<string>? ReadDicts(string path, char delimiter, out List<Row> rows)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, throwOnInvalidBytes: true));
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];  // utf-8-sig
        var records = Records(text, delimiter).GetEnumerator();
        rows = [];
        if (!records.MoveNext()) return null;
        var header = records.Current;
        while (records.MoveNext())
        {
            var record = records.Current;
            if (record.Count == 0) continue;
            var row = new Row();
            for (var i = 0; i < Math.Min(header.Count, record.Count); i++) row[header[i]] = record[i];
            if (header.Count < record.Count)
                row[RestKey] = record.Skip(header.Count).Cast<object?>().ToList();
            else
                for (var i = record.Count; i < header.Count; i++) row[header[i]] = null;
            rows.Add(row);
        }
        return header;
    }

    /// <summary>Python's <c>csv.reader</c> over already-decoded text: one list of fields per record, an empty
    /// list for a blank line.</summary>
    public static IEnumerable<List<string>> Records(string text, char delimiter)
    {
        var state = State.StartRecord;
        var fields = new List<string>();
        var field = new StringBuilder();
        var fieldStarted = false;  // _csv.c's field_len != 0, for the end-of-data check

        void Save()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void Add(char c)
        {
            field.Append(c);
            fieldStarted = true;
        }

        // Process one character, or the end of a line (null).
        void Process(char? c)
        {
            var eol = c is null;
            var newline = c is '\n' or '\r';
            switch (state)
            {
                case State.StartRecord:
                    if (eol) return;  // an empty line: the record is []
                    if (newline) { state = State.EatCrnl; return; }
                    state = State.StartField;
                    goto case State.StartField;
                case State.StartField:
                    if (newline || eol) { Save(); state = eol ? State.StartRecord : State.EatCrnl; }
                    else if (c == Quote) state = State.InQuotedField;
                    else if (c == delimiter) Save();
                    else { Add(c!.Value); state = State.InField; }
                    return;
                case State.InField:
                    if (newline || eol) { Save(); state = eol ? State.StartRecord : State.EatCrnl; }
                    else if (c == delimiter) { Save(); state = State.StartField; }
                    else Add(c!.Value);
                    return;
                case State.InQuotedField:
                    if (eol) return;  // the line ended inside quotes; the field continues on the next line
                    if (c == Quote) state = State.QuoteInQuotedField;
                    else Add(c!.Value);
                    return;
                case State.QuoteInQuotedField:
                    if (c == Quote) { Add(Quote); state = State.InQuotedField; }
                    else if (c == delimiter) { Save(); state = State.StartField; }
                    else if (newline || eol) { Save(); state = eol ? State.StartRecord : State.EatCrnl; }
                    else { Add(c!.Value); state = State.InField; }  // strict=False keeps it
                    return;
                case State.EatCrnl:
                    if (newline) return;
                    if (eol) { state = State.StartRecord; return; }
                    throw new FormatException("new-line character seen in unquoted field - do you need to open the file with newline=''?");
            }
        }

        var position = 0;
        while (position < text.Length)
        {
            // One line as io's newline="" splits it: ends after \n, \r\n, or a lone \r, and keeps the ending.
            var end = position;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            if (end < text.Length)
                end += text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            for (var i = position; i < end; i++) Process(text[i]);
            Process(null);
            position = end;
            if (state == State.StartRecord)
            {
                yield return fields;
                fields = [];
            }
        }
        // End of data inside a quoted field (or with a field started): strict=False saves what there is.
        if (fieldStarted || state == State.InQuotedField)
        {
            Save();
            yield return fields;
        }
    }
}
