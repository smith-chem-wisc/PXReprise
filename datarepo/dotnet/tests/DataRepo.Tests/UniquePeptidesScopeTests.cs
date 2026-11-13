using DataRepo.Catalog;
using DuckDB.NET.Data;

namespace DataRepo.Tests;

/// <summary><c>dataset_overview.unique_peptides_scope</c> (AGING-R1, QuantProject 064): what
/// <c>protein_groups.unique_peptides</c> counts, from the MetaMorpheus release that searched the dataset.</summary>
public class UniquePeptidesScopeTests
{
    [TestCase("MetaMorpheus", "1.1.11", "database")]
    [TestCase("MetaMorpheus", "1.1.12", "database")]
    [TestCase("MetaMorpheus", "1.1.9", "database")]
    [TestCase("MetaMorpheus", "1.1.13", "protein_group")]
    [TestCase("MetaMorpheus", "1.2.0", "protein_group")]
    [TestCase("MetaMorpheus", "1.1.100", "protein_group")]
    [TestCase("metamorpheus", "1.1.13", "protein_group")]
    [TestCase("MetaMorpheus", "1.1.13-beta", null)]
    [TestCase("MetaMorpheus", "", null)]
    [TestCase("MetaMorpheus", null, null)]
    [TestCase("MaxQuant", "2.4.0", null)]
    public void TheScopeFollowsTheRelease(string engine, string? version, string? scope)
    {
        using var con = new DuckDBConnection("Data Source=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        var column = CatalogBuilder.UniquePeptidesScopeColumn.TrimEnd().TrimEnd(',');
        cmd.CommandText = $"SELECT {column} FROM (SELECT $engine AS search_engine, CAST($version AS VARCHAR) AS search_engine_version) d";
        cmd.Parameters.Add(new DuckDBParameter("engine", engine));
        cmd.Parameters.Add(new DuckDBParameter("version", (object?)version ?? DBNull.Value));
        var got = cmd.ExecuteScalar();
        Assert.That(got is DBNull ? null : got, Is.EqualTo(scope));
    }
}
