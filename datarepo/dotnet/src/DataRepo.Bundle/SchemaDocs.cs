namespace DataRepo.Bundle;

/// <summary>One enum's description and permissible values, in the schema's order.</summary>
public sealed record EnumDoc(string? Description, IReadOnlyList<string> Values);

/// <summary>One column's documentation: what it means, not its storage type (that is <see cref="ColumnSpec"/>).</summary>
public sealed record ColumnDoc(string Name, string? Description, string Range, string? Enum, bool Identifier, bool Multivalued, string? Unit);

/// <summary>One table's documentation: the LinkML class its rows are, what one row is, and its columns in order.</summary>
public sealed record TableDoc(string Class, string? Description, IReadOnlyList<ColumnDoc> Columns);

/// <summary>An insertion-ordered dictionary, so generated documentation enumerates in the schema's order.</summary>
public class OrderedDocs<T> : System.Collections.Generic.OrderedDictionary<string, T>, IReadOnlyDictionary<string, T>
{
    public OrderedDocs() : base(StringComparer.Ordinal) { }
}

public sealed class OrderedEnums : OrderedDocs<EnumDoc>;

public sealed class OrderedTables : OrderedDocs<TableDoc>;

/// <summary>The schema's prose (generated into <c>SchemaDocs.g.cs</c> by DataRepo.SchemaGen), for the MCP
/// server's <c>describe</c> tool: an agent reading a stale description is the silently-wrong answer D15 forbids,
/// so it is generated from the same schema as the tables, never hand-kept.</summary>
public static partial class SchemaDocs;
