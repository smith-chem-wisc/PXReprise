using System.Security.Cryptography;
using PXReprise.Search;

namespace PXReprise.Tests;

public class ContaminantPanelTests
{
    private static string List => Path.Combine(TestSupport.ProfilesDir, "lists", "contaminant_panel_exclude_v1.tsv");

    [Test]
    public void NoListMeansTheShippedPanel()
    {
        var (panel, rec) = ContaminantPanel.Resolve("shipped.xml", null, TestSupport.TempDir());
        Assert.That(panel, Is.EqualTo("shipped.xml"));
        Assert.That(rec, Is.Null);
    }

    [Test]
    public void AListNamingAnEntryThePanelLacksIsRefused()
    {
        string dir = TestSupport.TempDir();
        string panel = TestSupport.WriteFile(dir, "p.xml", "<uniprot>\n<entry><accession>P1</accession></entry>\n</uniprot>\n");
        string list = TestSupport.WriteFile(dir, "l.tsv", "accession\treason\nP9\tnot there\n");
        var e = Assert.Throws<SearchSetupException>(() => ContaminantPanel.Resolve(panel, list, dir));
        Assert.That(e!.Message, Does.Contain("P9"));
    }

    [Test]
    public void ListedEntriesAreRemovedAndTheRestKept()
    {
        string dir = TestSupport.TempDir();
        string panel = TestSupport.WriteFile(dir, "p.xml",
            "<uniprot>\n<entry><accession>P1</accession></entry>\n<entry><accession>P2</accession></entry>\n</uniprot>\n");
        string list = TestSupport.WriteFile(dir, "l.tsv", "# comment\naccession\treason\nP1\tspike-in\n");
        var (reduced, rec) = ContaminantPanel.Resolve(panel, list, Path.Combine(dir, "out"));
        string text = File.ReadAllText(reduced);
        Assert.That(text, Does.Contain("P2").And.Not.Contain("P1"));
        Assert.That(rec!["excluded"]![0]!.GetValue<string>(), Is.EqualTo("P1"));
    }

    /// <summary>
    /// The reduced panel the aging batch searched with since 2026-09-25 is rebuilt byte for byte: same content-addressed
    /// name, same sha256. Needs the MetaMorpheus install and the aging work disk.
    /// </summary>
    [Test, Category("LocalCorpus")]
    public void TheAgingReducedPanelIsRebuiltByteForByte()
    {
        const string shipped = @"F:\ClaudeTestBuilds\aging\MetaMorpheus-1.1.11\Contaminants\MetaMorpheusContaminants.xml";
        const string existing = @"F:\aging_data\db\contaminants\MetaMorpheusContaminants.minus-contaminant_panel_exclude_v1.130830b4a0d0.xml";
        if (!File.Exists(shipped) || !File.Exists(existing)) Assert.Ignore("the MetaMorpheus install or the aging work disk is not attached");
        var (built, rec) = ContaminantPanel.Resolve(shipped, List, TestSupport.TempDir());
        Assert.That(Path.GetFileName(built), Is.EqualTo(Path.GetFileName(existing)));
        Assert.That(Sha(built), Is.EqualTo(Sha(existing)));
        Assert.That(rec!["excluded"]!.AsArray(), Has.Count.EqualTo(41));
    }

    private static string Sha(string p)
    {
        using var s = File.OpenRead(p);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
