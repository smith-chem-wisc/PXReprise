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

    /// <summary>
    /// D35: label-free-dda@4 searches MetaMorpheus's NoUPS panel as it is. The shipped copy is the #2858 file byte for byte
    /// (216 entries), and none of the 48 UPS proteins is left: neither the 41 aging's D54 list removed nor the 7 human ones
    /// it kept.
    /// </summary>
    [Test]
    public void LabelFreeDda4SearchesTheNoUpsPanelAsItIs()
    {
        var p = Config.ProfileLoader.Load(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-4.toml"));
        Assert.That((p.ContaminantFile, p.ContaminantExclude), Is.EqualTo(("MetaMorpheusContaminants_NoUPS.xml", (string?)null)));
        string panel = p.ListPath(p.ContaminantFile!);
        Assert.That(Sha(panel), Is.EqualTo("5b6e2059abf229f3d52b894f6abe4c949d6c37dc78667f19f447eab8205004a9"));
        var rec = ContaminantPanel.ShippedRecord(panel);
        Assert.That(rec["entries"]!.GetValue<int>(), Is.EqualTo(216));
        string text = File.ReadAllText(panel);
        var ups = ContaminantPanel.Excluded(List)
            .Concat(new[] { "P02768", "P02787", "P02788", "P00709", "P61626", "P69905", "P68871" }).ToList();
        Assert.That(ups, Has.Count.EqualTo(48));
        Assert.That(ups.Where(a => text.Contains($"<accession>{a}</accession>", StringComparison.Ordinal)), Is.Empty);
        // The reagent copies stay contaminants: bovine serum albumin and chicken lysozyme.
        Assert.That(text, Does.Contain("<accession>P02769</accession>").And.Contain("<accession>P00698</accession>"));
    }

    [TestCase("panel = \"a.xml\"\nfile = \"MetaMorpheusContaminants_NoUPS.xml\"", "exactly one of")]
    [TestCase("file = \"MetaMorpheusContaminants_NoUPS.xml\"\nexclude = \"contaminant_panel_exclude_v1.tsv\"", "used as it is")]
    [TestCase("file = \"noups.xml\"", "\"Contaminants\"")]
    public void AShippedPanelIsCheckedWhenTheProfileLoads(string contaminants, string error)
    {
        string src = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-4.toml"));
        string bad = System.Text.RegularExpressions.Regex.Replace(src, @"(?ms)^\[contaminants\].*?(?=^\[quant\])",
            "[contaminants]\n" + contaminants + "\n\n");
        var e = Assert.Throws<Config.ConfigException>(() =>
            Config.ProfileLoader.Load(TestSupport.WriteFile(TestSupport.TempDir(), "p.toml", bad)));
        Assert.That(e!.Message, Does.Contain(error));
    }

    private static string Sha(string p)
    {
        using var s = File.OpenRead(p);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
