using PXReprise.Batch;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.38's library routing (the user, 2026-11-03/04): a spectrum of a PTM class its deposit enriches goes to that
/// class's own chain, never into the organism's regular library; every other spectrum registers as before. Per class: a
/// GG enrichment's phospho stays regular.
/// </summary>
public class LibraryRoutingTests
{
    private const string PhosphoUniProt = "[UniProt:Phosphoserine on S]";
    private const string PhosphoGptmd = "[Common Biological:Phosphorylation on S]";
    private const string AcetylK = "[Common Biological:Acetylation on K]";
    private const string GG = "[Common Biological:GG (Ubiquitination Site) on K]";

    // GG is a MetaMorpheus Mods.txt entry, not one of mzLib's own: the search hands its Mods files to the cleaning.
    private static string ModsFile(string dir) => TestSupport.WriteFile(dir, "Mods.txt", """
        ID   GG (Ubiquitination Site)
        TG   K
        PP   Anywhere.
        MT   Common Biological
        CF   C4H6N2O2
        //
        """);

    private static string Database(string dir) => TestSupport.WriteFile(dir, "db.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <uniprot xmlns="http://uniprot.org/uniprot">
        <entry dataset="Swiss-Prot"><accession>P00001</accession><sequence length="17">MAAAPEPTIDEKAAAAR</sequence></entry>
        </uniprot>
        """);

    private static LibraryCleaning Cleaning(string dir) =>
        LibraryCleaning.For(new[] { ModsFile(dir) }, new[] { Database(dir) }, blockedCTerm: false);

    private static string Entry(string name, double peak) =>
        $"Name: {name}\nMW: 500.25\nComment: Parent=500.25 RT=42.5\nNum peaks: 1\n{peak:F5}\t1\t\"y1^1/0ppm\"\n";

    [Test]
    public void AClassIsJudgedByChemistryNotByName()
    {
        var c = Cleaning(TestSupport.TempDir());
        Assert.That(c.ClassesOf("PEPS" + PhosphoUniProt + "K"), Is.EquivalentTo(new[] { "phospho" }), "UniProt's name");
        Assert.That(c.ClassesOf("PEPS" + PhosphoGptmd + "K"), Is.EquivalentTo(new[] { "phospho" }), "GPTMD's name, the same class");
        Assert.That(c.ClassesOf("PEPK" + AcetylK + "AR"), Is.EquivalentTo(new[] { "acetylK" }));
        Assert.That(c.ClassesOf("[Common Biological:Acetylation on X]PEPTIDEK"), Is.Empty, "N-terminal acetylation is not acetyl-K");
        Assert.That(c.ClassesOf("PEPY[UniProt:Sulfotyrosine on Y]K"), Is.Empty, "sulfation is 0.0095 Da from phospho");
        Assert.That(c.ClassesOf("PEPK" + GG + "AR"), Is.EquivalentTo(new[] { "GG" }), "resolved from the Mods file");
        Assert.That(c.ClassesOf("PEPS[Nobody:Made this up on S]K"), Is.Empty, "a name that does not resolve is in no class");
        Assert.That(c.ClassesOf("PEPS" + PhosphoGptmd + "K" + GG + "R"), Is.EquivalentTo(new[] { "phospho", "GG" }));
    }

    [Test]
    public void OnlyPtmEnrichmentsRouteAClass()
    {
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "phospho" }), Is.EqualTo(new[] { "phospho" }));
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "none", "acetyl" }), Is.EqualTo(new[] { "acetylK" }), "run_enrichment keys");
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "phospho+GG" }), Is.EqualTo(new[] { "phospho", "GG" }), "the probe's measurement");
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "ubiquitin_GG" }), Is.EqualTo(new[] { "GG" }));
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "succinyl" }), Is.EqualTo(new[] { "succinylK" }));
        Assert.That(LibraryRouting.ClassesEnrichedBy(new[] { "affinity_purification", "immunoprecipitation", "proximity_labelling", "chemical_probe", "other", "none" }),
            Is.Empty, "a protein pulldown enriches no PTM class");
        Assert.That(LibraryRouting.ChainOf("human_tmt11", "acetylK"), Is.EqualTo("human_tmt11_acetylk"));
    }

    [Test]
    public void AnEnrichedClassGoesToItsOwnChainAndTheRegularChainKeepsItsOwnSpectra()
    {
        string root = TestSupport.TempDir(), run1 = TestSupport.TempDir(), run2 = TestSupport.TempDir(), run3 = TestSupport.TempDir();
        var cleaning = Cleaning(TestSupport.TempDir());
        string a = "PEPS" + PhosphoUniProt + "K/2", b = "PEPTIDEK/2", c = "AAS" + PhosphoGptmd + "K/2", d = "LLLK/2", e = "GGK" + AcetylK + "R/2";

        // A whole proteome writes the regular chain, its phospho included.
        TestSupport.WriteFile(run1, "SpectralLibrary_1.msp", Entry(a, 100) + Entry(b, 101));
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run1, "run1", "PXD000001", "1.1.13", cleaning);

        // A phospho enrichment: MetaMorpheus's update replaced A's spectrum, found a new phospho C, and new D and E.
        TestSupport.WriteFile(run2, "updateSpectralLibrary_2.msp", Entry(a, 200) + Entry(b, 101) + Entry(c, 202) + Entry(d, 203) + Entry(e, 204));
        var v2 = SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run2, "run2", "PXD000002", "1.1.13", cleaning, new[] { "phospho" });

        Assert.That(File.ReadAllText(Path.Combine(root, "mouse/mouse.v002.msp")),
            Is.EqualTo(Entry(a, 100) + Entry(b, 101) + Entry(d, 203) + Entry(e, 204)),
            "the regular chain keeps its own A, never takes C, and takes the rest (acetyl-K too: not this deposit's class)");
        Assert.That(File.ReadAllText(Path.Combine(root, "mouse_phospho/mouse_phospho.v001.msp")), Is.EqualTo(Entry(c, 202)));
        Assert.That((int)v2["routing"]!["kept_parent_spectrum"]!, Is.EqualTo(1));
        Assert.That((int)v2["routing"]!["routed"]!["mouse_phospho"]!, Is.EqualTo(1));
        Assert.That(v2["routed_versions"]!.AsArray(), Has.Count.EqualTo(1));
        var reg = SpectralLibrary.ReadRegistry(root);
        Assert.That(reg["organisms"]!["mouse"]!["current"]!.GetValue<string>(), Is.EqualTo("mouse/mouse.v002.msp"));
        Assert.That(reg["organisms"]!["mouse_phospho"]!["versions"]![0]!["produced_by"]!["routed_from"]!["version"]!.GetValue<int>(), Is.EqualTo(2));

        // A second phospho enrichment finds C again (a better spectrum) and a new phospho F: the class chain updates.
        string f = "GGS" + PhosphoGptmd + "AK/2";
        TestSupport.WriteFile(run3, "updateSpectralLibrary_3.msp", Entry(a, 100) + Entry(b, 101) + Entry(d, 203) + Entry(e, 204) + Entry(c, 302) + Entry(f, 303));
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run3, "run3", "PXD000003", "1.1.13", cleaning, new[] { "phospho" });
        Assert.That(File.ReadAllText(Path.Combine(root, "mouse_phospho/mouse_phospho.v002.msp")), Is.EqualTo(Entry(c, 302) + Entry(f, 303)));
        Assert.That(File.ReadAllText(Path.Combine(root, "mouse/mouse.v003.msp")), Is.EqualTo(Entry(a, 100) + Entry(b, 101) + Entry(d, 203) + Entry(e, 204)));
        Assert.That(SpectralLibrary.Plan(root, "mouse").LibraryIn, Is.EqualTo(Path.Combine(root, "mouse/mouse.v003.msp")),
            "searches still read the regular chain");
    }

    [Test]
    public void ANonEnrichedDepositsPhosphoStaysRegular()
    {
        string root = TestSupport.TempDir(), run1 = TestSupport.TempDir(), run2 = TestSupport.TempDir();
        var cleaning = Cleaning(TestSupport.TempDir());
        TestSupport.WriteFile(run1, "SpectralLibrary_1.msp", Entry("PEPTIDEK/2", 101));
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run1, "run1", "PXD000004", "1.1.13", cleaning);
        string phospho = "AAS" + PhosphoGptmd + "K/2";
        TestSupport.WriteFile(run2, "updateSpectralLibrary_2.msp", Entry("PEPTIDEK/2", 101) + Entry(phospho, 202));
        // A GG enrichment: its phospho is regular (the user, 11-03: "it is okay to keep the phospho in the regular ... library").
        var v = SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run2, "run2", "PXD000005", "1.1.13", cleaning, new[] { "GG" });
        Assert.That(File.ReadAllText(Path.Combine(root, "rat/rat.v002.msp")), Is.EqualTo(Entry("PEPTIDEK/2", 101) + Entry(phospho, 202)));
        Assert.That(v["routed_versions"]!.AsArray(), Is.Empty, "no GG spectrum, so no GG chain");
        Assert.That(SpectralLibrary.ReadRegistry(root)["organisms"]!.AsObject().Select(kv => kv.Key), Is.EquivalentTo(new[] { "rat" }));
    }

    [Test]
    public void RoutingNeedsTheCleaningRules()
    {
        string root = TestSupport.TempDir(), run = TestSupport.TempDir();
        TestSupport.WriteFile(run, "SpectralLibrary_1.msp", Entry("PEPTIDEK/2", 101));
        Assert.Throws<ArgumentException>(() =>
            SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run, "run", "PXD000006", "1.1.13", null, new[] { "phospho" }));
    }

    [Test]
    public void AManifestEntryGivesItsEnrichmentAndItsRunEnrichmentKeys()
    {
        // PXD030939's shape (manifest line 1032): a mixed-by-run entry, followed by another entry.
        string text = """
            datasets:
              - accession: PXD030939
                status: include
                enrichment: [phospho]
                run_enrichment:
                  phospho: ["pp01", "pp02"]
                  none: ["wp01", "wp02"]
                notes: >
                  MIXED BY RUN.
              - accession: PXD057544
                status: include
                enrichment: [none]
            """.ReplaceLineEndings("\n");
        Assert.That(ManifestEntry.EnrichmentsOf(text, "PXD030939"), Is.EqualTo(new[] { "phospho", "phospho", "none" }));
        Assert.That(ManifestEntry.EnrichmentsOf(text, "PXD057544"), Is.EqualTo(new[] { "none" }), "the first entry's map does not bleed");
        Assert.That(ManifestEntry.EnrichmentsOf(text, "PXD000001"), Is.Null);
        Assert.That(LibraryRouting.ClassesEnrichedBy(ManifestEntry.EnrichmentsOf(text, "PXD030939")!), Is.EqualTo(new[] { "phospho" }));
    }
}
