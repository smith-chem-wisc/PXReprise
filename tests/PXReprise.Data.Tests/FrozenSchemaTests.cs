using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PXReprise.Data.Tests;

/// <summary>
/// The released schemas under <c>schemas/datarepo/releases/</c> are frozen: each folder is the schema text a released version
/// carried, and it is never edited (external review R1). A bundle records only its <c>schema_version</c>, so this folder is
/// where its schema text is read; these tests keep it what was released.
/// </summary>
public class FrozenSchemaTests
{
    /// <summary>Every released core schema version (see the folder's README for how the list was decided).</summary>
    internal static readonly string[] Released =
    [
        "0.0.2", "0.0.3", "0.0.4", "0.0.5", "0.0.6", "0.0.7", "0.0.8", "0.0.9", "0.0.10",
        "0.0.11", "0.0.12", "0.0.13", "0.0.14", "0.0.15", "0.0.16",
    ];

    private static string Releases => Path.Combine(DataPaths.Root, "schemas", "datarepo", "releases");

    [Test]
    public void EveryReleasedVersionHasAFolderAndNoOtherFolderExists()
    {
        var folders = Directory.GetDirectories(Releases).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
        Assert.That(folders, Is.EquivalentTo(Released));
    }

    [TestCaseSource(nameof(Released))]
    public void AFrozenFolderMatchesItsChecksums(string version)
    {
        string dir = Path.Combine(Releases, version);
        var listed = File.ReadAllLines(Path.Combine(dir, "SHA256SUMS"))
            .Where(l => l.Length > 0)
            .Select(l => Regex.Match(l, @"^([0-9a-f]{64})  (\S.*)$"))
            .ToList();
        Assert.That(listed.All(m => m.Success), "SHA256SUMS lines are '<sha256>  <path>'");
        var expected = listed.ToDictionary(m => m.Groups[2].Value, m => m.Groups[1].Value, StringComparer.Ordinal);

        var present = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(f => f != "SHA256SUMS")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.That(present, Is.EquivalentTo(expected.Keys), $"{version}: a file was added to or removed from a frozen folder");

        foreach (var (path, sha) in expected)
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, path))));
            Assert.That(actual, Is.EqualTo(sha), $"{version}/{path} changed: a frozen schema is never edited");
        }
    }

    [TestCaseSource(nameof(Released))]
    public void AFrozenCoreSchemaCarriesItsFoldersVersion(string version)
    {
        string text = File.ReadAllText(Path.Combine(Releases, version, "datarepo.yaml"));
        var m = Regex.Match(text, @"^version:\s*(\S+)\s*$", RegexOptions.Multiline);
        Assert.That(m.Success, $"{version}: no version line");
        Assert.That(m.Groups[1].Value, Is.EqualTo(version));
    }

    [Test]
    public void TheWorkingSchemaIsNotAReleasedVersion()
    {
        string text = File.ReadAllText(DataPaths.Schema);
        string working = Regex.Match(text, @"^version:\s*(\S+)\s*$", RegexOptions.Multiline).Groups[1].Value;
        Assert.That(Released, Does.Not.Contain(working),
            "the working schema carries a released version: bump it before changing it, or freeze it here when it is released");
    }

    /// <summary>Every value an enum of Python 0.32.0's schema 0.0.13 has gained since is listed in
    /// <see cref="PXReprise.Bundle.SchemaContract.AddedEnumValues"/>, and nothing else is, so a Python-parity answer (the
    /// MCP server's <c>describe</c>) hides exactly those. An enum 0.0.13 did not have is not listed: no 0.0.13 column
    /// names it. The rule as <c>AddedColumns</c> holds it for columns (review of #55).</summary>
    [Test]
    public void EveryEnumValueAddedSince0013IsInAddedEnumValues()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Releases, PXReprise.Bundle.SchemaContract.Python0320SchemaVersion, "datarepo.yaml"))));
        static YamlDotNet.RepresentationModel.YamlScalarNode Key(string k) => new(k);
        var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;
        var then = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, node) in ((YamlDotNet.RepresentationModel.YamlMappingNode)root.Children[Key("enums")]).Children)
        {
            var values = node is YamlDotNet.RepresentationModel.YamlMappingNode e && e.Children.TryGetValue(Key("permissible_values"), out var pv)
                ? ((YamlDotNet.RepresentationModel.YamlMappingNode)pv).Children.Keys.Select(k => ((YamlDotNet.RepresentationModel.YamlScalarNode)k).Value!)
                : [];
            then[((YamlDotNet.RepresentationModel.YamlScalarNode)name).Value!] = values.ToHashSet(StringComparer.Ordinal);
        }
        var gained = PXReprise.Bundle.SchemaDocs.Enums
            .Where(e => then.ContainsKey(e.Key))
            .SelectMany(e => e.Value.Values.Where(v => !then[e.Key].Contains(v)).Select(v => (e.Key, v)))
            .ToList();
        Assert.That(gained, Is.Not.Empty, "0.0.13 is read: some enum has gained a value since");
        Assert.That(PXReprise.Bundle.SchemaContract.AddedEnumValues.Select(a => (a.Enum, a.Value)), Is.EquivalentTo(gained));
    }
}
