using System.Text.Json;
using DataRepo.Bundle;

namespace DataRepo.Tests;

/// <summary>The schema's prose as the MCP server will serve it.</summary>
public class SchemaDocsTests
{
    [Test]
    public void EveryTableHasItsDocumentationAndEveryColumnMatchesItsSpec()
    {
        Assert.That(SchemaDocs.Tables.Keys, Is.EqualTo(Tables.Core.Select(t => t.Name)));
        foreach (var spec in Tables.Core)
            Assert.That(SchemaDocs.Tables[spec.Name].Columns.Select(c => c.Name), Is.EqualTo(spec.Columns.Select(c => c.Name)), spec.Name);
        Assert.That(SchemaDocs.StudyTables.Keys, Is.EqualTo(Tables.Study.Keys));
        Assert.That(SchemaDocs.Enums, Is.Not.Empty);
    }

    /// <summary>Writes the docs as JSON in the shape of Python's <c>_schema_docs.py</c>, for a one-off comparison
    /// (set DATAREPO_SCHEMA_DOCS_OUT). Checked equal to Python's on 2026-10-04.</summary>
    [Test, Explicit]
    public void DumpForComparison()
    {
        var path = Environment.GetEnvironmentVariable("DATAREPO_SCHEMA_DOCS_OUT");
        Assume.That(path, Is.Not.Null.And.Not.Empty);
        object Columns(TableDoc t) => t.Columns.ToDictionary(c => c.Name, c =>
        {
            var d = new Dictionary<string, object?> { ["description"] = c.Description, ["range"] = c.Range };
            if (c.Enum is not null) d["enum"] = c.Enum;
            if (c.Identifier) d["identifier"] = true;
            if (c.Multivalued) d["multivalued"] = true;
            if (c.Unit is not null) d["unit"] = c.Unit;
            return (object)d;
        });
        object Table(TableDoc t) => new Dictionary<string, object?> { ["class"] = t.Class, ["description"] = t.Description, ["columns"] = Columns(t) };
        object Enum(EnumDoc e) => new Dictionary<string, object?> { ["description"] = e.Description, ["values"] = e.Values };
        var dump = new Dictionary<string, object?>
        {
            ["SCHEMA_TITLE"] = SchemaDocs.SchemaTitle,
            ["SCHEMA_DESCRIPTION"] = SchemaDocs.SchemaDescription,
            ["ENUMS"] = SchemaDocs.Enums.ToDictionary(kv => kv.Key, kv => Enum(kv.Value)),
            ["TABLE_DOCS"] = SchemaDocs.Tables.ToDictionary(kv => kv.Key, kv => Table(kv.Value)),
            ["STUDY_TABLE_DOCS"] = SchemaDocs.StudyTables.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(t => t.Key, t => Table(t.Value))),
            ["STUDY_ENUMS"] = SchemaDocs.StudyEnums.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(e => e.Key, e => Enum(e.Value))),
        };
        File.WriteAllText(path!, JsonSerializer.Serialize(dump));
    }
}
