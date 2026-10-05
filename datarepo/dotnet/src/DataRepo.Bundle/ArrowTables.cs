using Apache.Arrow;
using Apache.Arrow.Types;
using ParquetSharp;
using ParquetSharp.Arrow;

namespace DataRepo.Bundle;

/// <summary>A row: column name to value. Missing keys are nulls.</summary>
public interface IRow : IReadOnlyDictionary<string, object?>;

/// <summary>A plain dictionary row.</summary>
public sealed class Row : Dictionary<string, object?>, IRow
{
    public Row() : base(StringComparer.Ordinal) { }
    public Row(IDictionary<string, object?> values) : base(values, StringComparer.Ordinal) { }
}

/// <summary>Row dictionaries to Arrow record batches and Parquet files, and back.</summary>
/// <remarks>
/// Writes through ParquetSharp, which wraps the same Arrow C++ Parquet writer pyarrow uses, so a C#
/// bundle carries the same column types and the same required/optional flags as a Python one.
/// </remarks>
public static class ArrowTables
{
    /// <summary>Builds one record batch from row dictionaries, in the table's column order.</summary>
    /// <param name="label">What to call the table in an error, e.g. <c>age_effects</c> or <c>aging.age_effects</c>.</param>
    /// <param name="columns">The columns to write.</param>
    /// <param name="rows">Row dictionaries keyed on column names.</param>
    /// <exception cref="IngestException">A row has a key the table has no column for, or a required
    /// column is null.</exception>
    public static RecordBatch FromRows(string label, IReadOnlyList<ColumnSpec> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var known = new HashSet<string>(columns.Select(c => c.Name), StringComparer.Ordinal);
        var values = columns.Select(_ => new List<object?>(rows.Count)).ToArray();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var extra = row.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (extra.Count > 0)
                throw new IngestException($"table {label}: row {index} has unknown columns [{string.Join(", ", extra.Select(e => $"'{e}'"))}]");
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                var value = Coercion.Coerce(row.TryGetValue(column.Name, out var raw) ? raw : null, column);
                if (value is null && !column.Nullable)
                    throw new IngestException($"table {label}: row {index} has no value for required '{column.Name}'");
                values[c].Add(value);
            }
        }

        var schema = new Schema.Builder();
        var arrays = new IArrowArray[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            schema.Field(columns[c].ToField());
            arrays[c] = BuildArray(columns[c], values[c]);
        }
        return new RecordBatch(schema.Build(), arrays, rows.Count);
    }

    /// <summary>Builds a record batch for the core table <paramref name="name"/>.</summary>
    public static RecordBatch FromRows(string name, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (!Tables.ByName.TryGetValue(name, out var spec))
            throw new IngestException($"no table named '{name}' in schema {SchemaContract.Version}");
        return FromRows(name, spec.Columns, rows);
    }

    private static IArrowArray BuildArray(ColumnSpec column, List<object?> values)
    {
        if (!column.IsList)
            return BuildScalarArray(column.Type, values);

        var builder = new ListArray.Builder(ColumnSpec.ElementType(column.Type));
        foreach (var value in values)
        {
            if (value is null)
            {
                builder.AppendNull();
                continue;
            }
            builder.Append();
            foreach (var item in (List<object?>)value)
                AppendScalar(builder.ValueBuilder, column.Type, item);
        }
        return builder.Build();
    }

    private static IArrowArray BuildScalarArray(ColumnType type, List<object?> values)
    {
        switch (type)
        {
            case ColumnType.String:
            {
                var b = new StringArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((string)v); }
                return b.Build();
            }
            case ColumnType.Int64:
            {
                var b = new Int64Array.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((long)v); }
                return b.Build();
            }
            case ColumnType.Float64:
            {
                var b = new DoubleArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((double)v); }
                return b.Build();
            }
            case ColumnType.Boolean:
            {
                var b = new BooleanArray.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((bool)v); }
                return b.Build();
            }
            case ColumnType.Date:
            {
                var b = new Date32Array.Builder();
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((DateOnly)v); }
                return b.Build();
            }
            case ColumnType.TimestampUtc:
            {
                var b = new TimestampArray.Builder(new TimestampType(Apache.Arrow.Types.TimeUnit.Microsecond, "UTC"));
                foreach (var v in values) { if (v is null) b.AppendNull(); else b.Append((DateTimeOffset)v); }
                return b.Build();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static void AppendScalar(IArrowArrayBuilder builder, ColumnType type, object? value)
    {
        switch (builder)
        {
            case StringArray.Builder b: if (value is null) b.AppendNull(); else b.Append((string)value); break;
            case Int64Array.Builder b: if (value is null) b.AppendNull(); else b.Append((long)value); break;
            case DoubleArray.Builder b: if (value is null) b.AppendNull(); else b.Append((double)value); break;
            case BooleanArray.Builder b: if (value is null) b.AppendNull(); else b.Append((bool)value); break;
            case Date32Array.Builder b: if (value is null) b.AppendNull(); else b.Append((DateOnly)value); break;
            case TimestampArray.Builder b: if (value is null) b.AppendNull(); else b.Append((DateTimeOffset)value); break;
            default: throw new NotSupportedException($"no list element builder for {type}");
        }
    }

    /// <summary>Writes one record batch as a zstd-compressed Parquet file, as the Python writer does.</summary>
    public static void WriteParquet(RecordBatch batch, string path)
    {
        using var properties = new WriterPropertiesBuilder().Compression(Compression.Zstd).Build();
        using var writer = new FileWriter(path, batch.Schema, properties);
        writer.WriteRecordBatch(batch);
        writer.Close();
    }

    /// <summary>A Parquet file's Arrow schema and rows, values as <see cref="Coercion.Coerce"/> returns them.</summary>
    public static (Schema Schema, List<Row> Rows) ReadParquet(string path)
    {
        using var reader = new FileReader(path);
        var schema = reader.Schema;
        var rows = new List<Row>();
        using var batches = reader.GetRecordBatchReader();
        while (batches.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult() is { } batch)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    var row = new Row();
                    for (var c = 0; c < schema.FieldsList.Count; c++)
                        row[schema.FieldsList[c].Name] = ValueAt(batch.Column(c), i);
                    rows.Add(row);
                }
            }
        }
        return (schema, rows);
    }

    /// <summary>One cell of an Arrow array as a plain .NET value.</summary>
    public static object? ValueAt(IArrowArray array, int index)
    {
        if (array.IsNull(index)) return null;
        return array switch
        {
            StringArray a => a.GetString(index),
            LargeStringArray a => a.GetString(index),
            Int64Array a => a.GetValue(index),
            Int32Array a => (long?)a.GetValue(index),
            // Narrower types a producer's own Parquet may carry (a study delivery): pyarrow's to_pylist gives
            // a Python int or float for each, so they widen to long and double here.
            Int16Array a => (long?)a.GetValue(index),
            Int8Array a => (long?)a.GetValue(index),
            UInt32Array a => (long?)a.GetValue(index),
            UInt16Array a => (long?)a.GetValue(index),
            UInt8Array a => (long?)a.GetValue(index),
            FloatArray a => (double?)a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            BooleanArray a => a.GetValue(index),
            Date32Array a => a.GetDateOnly(index),
            TimestampArray a => a.GetTimestamp(index),
            ListArray a => ListValues(a, index),
            _ => throw new NotSupportedException($"no reader for Arrow type {array.Data.DataType}"),
        };
    }

    private static List<object?> ListValues(ListArray array, int index)
    {
        var start = array.ValueOffsets[index];
        var end = array.ValueOffsets[index + 1];
        var list = new List<object?>(end - start);
        for (var j = start; j < end; j++)
            list.Add(ValueAt(array.Values, j));
        return list;
    }
}
