namespace PXReprise.Data.Tests;

/// <summary>Where the data code's test inputs live in this repository.</summary>
/// <remarks>dataRepo's tree moved into PXReprise on 2026-11-13 and was split into its folders: the schema and examples
/// under <c>schemas/datarepo/</c>, the fixture instance at <c>tests/datarepo-instance/</c>, the docs under
/// <c>docs/data/</c>. Fixtures written before the move name files by dataRepo's own layout (<c>tests/data/...</c>);
/// <see cref="FromDataRepo"/> and <see cref="ToDataRepo"/> translate, so those fixtures stay byte-identical.</remarks>
internal static class DataPaths
{
    /// <summary>The repository's root: the folder holding <c>schemas/datarepo/datarepo.yaml</c>.</summary>
    public static string Root { get; } = FindRoot();

    public static string Schema => Path.Combine(Root, "schemas", "datarepo", "datarepo.yaml");
    public static string StudySchemas => Path.Combine(Root, "schemas", "datarepo", "study");
    public static string Examples => Path.Combine(Root, "schemas", "datarepo", "examples");
    /// <summary>The fixture instance (dataRepo's <c>tests/data</c>): a manifest, a work root and PXD999999.</summary>
    public static string Instance => Path.Combine(Root, "tests", "datarepo-instance");
    public static string Docs => Path.Combine(Root, "docs", "data");
    public static string Generated => Path.Combine(Root, "src", "PXReprise.Bundle", "Generated");

    /// <summary>dataRepo's layout to this repository's, by leading folder; '/'-separated input.</summary>
    private static readonly (string DataRepo, string Here)[] Layout =
    [
        ("dotnet/tests/DataRepo.Tests", "tests/PXReprise.Data.Tests"),
        ("tests/data", "tests/datarepo-instance"),
        ("schema", "schemas/datarepo"),
        ("examples", "schemas/datarepo/examples"),
        ("docs", "docs/data"),
    ];

    /// <summary>A path relative to dataRepo's root (as a pre-move fixture names it), on this machine.</summary>
    public static string FromDataRepo(string relative)
    {
        var r = relative.Replace('\\', '/');
        foreach (var (old, here) in Layout)
            if (r == old || r.StartsWith(old + "/", StringComparison.Ordinal))
            {
                r = here + r[old.Length..];
                break;
            }
        return Path.Combine(Root, r.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Writes this machine's paths in <paramref name="text"/> as <c>&lt;repo&gt;/</c> plus dataRepo's layout, as
    /// pre-move fixtures expect them.</summary>
    public static string ToDataRepo(string text)
    {
        foreach (var (old, here) in Layout)
        {
            var local = Path.Combine(Root, here.Replace('/', Path.DirectorySeparatorChar));
            text = text.Replace(local, "<repo>/" + old);
        }
        return text.Replace(Root, "<repo>");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "schemas", "datarepo", "datarepo.yaml")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repository root (schemas/datarepo/datarepo.yaml) not found above the test directory");
    }
}
