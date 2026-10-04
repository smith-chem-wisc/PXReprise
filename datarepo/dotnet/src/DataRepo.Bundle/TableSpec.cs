using Apache.Arrow;
using Apache.Arrow.Types;

namespace DataRepo.Bundle;

/// <summary>A column's storage type, from its LinkML range (see <c>DataRepo.SchemaGen</c>).</summary>
public enum ColumnType
{
    String,
    Int64,
    Float64,
    Boolean,
    /// <summary>Arrow <c>date32</c>: days since 1970-01-01.</summary>
    Date,
    /// <summary>Arrow <c>timestamp[us, tz=UTC]</c>.</summary>
    TimestampUtc,
}

/// <summary>One column of a bundle table.</summary>
/// <param name="Name">The column name, as the schema spells it.</param>
/// <param name="Type">The element type.</param>
/// <param name="IsList">A multivalued slot, stored as an Arrow list of <paramref name="Type"/>.</param>
/// <param name="Nullable">False for a <c>required</c> or <c>identifier</c> slot: the writer refuses a
/// null, because a required column with no true value would otherwise get a false one.</param>
public sealed record ColumnSpec(string Name, ColumnType Type, bool IsList, bool Nullable)
{
    /// <summary>The Arrow type the column is written as: the same types pyarrow writes for it.</summary>
    public IArrowType ArrowType => IsList ? new ListType(ElementType(Type)) : ElementType(Type);

    /// <summary>The Arrow field, with the schema's nullability.</summary>
    public Field ToField() => new(Name, ArrowType, Nullable);

    internal static IArrowType ElementType(ColumnType type) => type switch
    {
        ColumnType.String => StringType.Default,
        ColumnType.Int64 => Int64Type.Default,
        ColumnType.Float64 => DoubleType.Default,
        ColumnType.Boolean => BooleanType.Default,
        ColumnType.Date => Date32Type.Default,
        ColumnType.TimestampUtc => new TimestampType(TimeUnit.Microsecond, "UTC"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

/// <summary>One bundle table: its name, the LinkML class its rows are, and its columns in order.</summary>
public sealed record TableSpec(string Name, string Class, IReadOnlyList<ColumnSpec> Columns)
{
    /// <summary>The Arrow schema the table is written with, columns in the schema's order.</summary>
    public Schema ToArrowSchema()
    {
        var builder = new Schema.Builder();
        foreach (var column in Columns)
            builder.Field(column.ToField());
        return builder.Build();
    }
}

public static partial class Tables
{
    private static readonly Lazy<IReadOnlyDictionary<string, TableSpec>> ByNameLazy =
        new(() => Core.ToDictionary(t => t.Name, StringComparer.Ordinal));

    /// <summary>The core tables by name.</summary>
    public static IReadOnlyDictionary<string, TableSpec> ByName => ByNameLazy.Value;
}
