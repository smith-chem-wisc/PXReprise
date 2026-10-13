using PXReprise.Discovery;
using Readers;
using UsefulProteomicsDatabases;
using CvParam = MzLibUtil.CvParam;

namespace PXReprise.Tests;

/// <summary>G19, decided 2026-10-05 (D22 to D24): a deposit's protease, alkylation and label, and where each came from.</summary>
public class ChemistryTests
{
    private static PrideProject Project(string protocol, string[]? ptms = null, string[]? quant = null) => new()
    {
        Accession = "PXD000900", Title = "t", SampleProcessingProtocol = protocol,
        IdentifiedPTMStrings = (ptms ?? Array.Empty<string>()).Select(n => new CvParam { Name = n }).ToList(),
        QuantificationMethods = (quant ?? Array.Empty<string>()).Select(n => new CvParam { Name = n }).ToList(),
    };

    private static readonly string[] Files = { "a.raw", "b.raw" };

    [Test]
    public void NothingStatedIsTrypsinCarbamidomethylLabelFree()
    {
        var c = ChemistryDetector.Detect(Project("Proteins were digested and analysed."), null, null, Files);
        Assert.That((c.Protease.Value, c.Protease.Source), Is.EqualTo(("trypsin", ChemistrySource.Default)));
        Assert.That(c.CysMods, Is.EqualTo(new[] { new CysMod("Carbamidomethyl", "UNIMOD:4", true) }));
        Assert.That((c.Label.Value, c.AnyGuessed, c.Park), Is.EqualTo(("label_free", false, (string?)null)));
    }

    // PXD001054 / PXD005014 / PXD010091 (aging 033): PRIDE lists light NEM; the protocol names the heavy d5 partner.
    [TestCase("Nethylmaleimide")]                         // PXD001054's spelling
    [TestCase("N-ethylmaleimide derivatized cysteine")]   // PXD005014, PXD010091
    public void TheNemDepositsGetTheLightHeavyPairAsVariable(string prideName)
    {
        var c = ChemistryDetector.Detect(Project("reduced Cys were blocked with d0-NEM; reversibly oxidised Cys were labelled with d5-NEM",
            new[] { prideName, "Oxidation" }), null, null, Files);
        Assert.That(c.Alkylation.Source, Is.EqualTo(ChemistrySource.PridePtm));
        Assert.That(c.CysMods, Is.EqualTo(new[] { new CysMod("Nethylmaleimide", "UNIMOD:108", false), new CysMod("NEM:2H(5)", "UNIMOD:776", false) }));
    }

    // PXD001054's own wording: "d(0) NEM" in a thiol blocking buffer, then "alkylated with ... d(5) NEM".
    [Test]
    public void TheProtocolsOwnSpellingOfTheHeavyPartnerIsRead()
    {
        var c = ChemistryDetector.Detect(Project("thiol blocking buffer containing d(0) NEM ... Cys residues were subsequently alkylated with 10 µl of 200 mM d(5) NEM",
            new[] { "Nethylmaleimide" }), null, null, Files);
        Assert.That(c.CysMods.Select(m => (m.Name, m.Fixed)), Is.EqualTo(new[] { ("Nethylmaleimide", false), ("NEM:2H(5)", false) }));
    }

    // PXD047288 and nine siblings (aging 043, PXR-A37; the user's D-3): NEM in the lysis buffer inhibits deubiquitinases AND
    // caps every free cysteine (the deposits are 0.2-3.2% Cys). With no other alkylant named, it IS the alkylant, variable.
    [Test]
    public void NemInALysisBufferIsTheAlkylantWhenNoOtherIsNamed()
    {
        var c = ChemistryDetector.Detect(Project("Samples were homogenized in denaturing lysis buffer (0.05% SDS) with 70mM NEM, briefly centrifuged"), null, null, Files);
        Assert.That((c.Alkylation.Value, c.Alkylation.Source), Is.EqualTo(("Nethylmaleimide", ChemistrySource.ProtocolText)));
        Assert.That(c.CysMods, Is.EqualTo(new[] { new CysMod("Nethylmaleimide", "UNIMOD:108", false) }));

        var both = ChemistryDetector.Detect(Project("Tissue was lysed in RIPA buffer with 10 mM NEM to inhibit deubiquitinases. Proteins were "
            + "precipitated overnight, resuspended in urea, digested on beads with Lys-C and then trypsin; peptides were reduced with DTT and "
            + "alkylated with iodoacetamide"), null, null, Files);
        Assert.That(both.CysMods.Select(m => (m.Name, m.Fixed)), Is.EqualTo(new[] { ("Carbamidomethyl", true) }), "another alkylant named wins");
    }

    // PXD013640 (aging 049): "blocked with 200 mM MMTS", searched with carbamidomethyl fixed. PXD053288 the same (aging 055).
    [Test]
    public void MmtsBlockingIsMethylthio()
    {
        var c = ChemistryDetector.Detect(Project("Free thiols were blocked with 200 mM MMTS in HEN buffer"), null, null, Files);
        Assert.That((c.CysMods.Single().Name, c.CysMods.Single().Fixed), Is.EqualTo(("Methylthio", true)));
        Assert.That(c.CysMods.Single().MetaMorpheusId, Does.EndWith("on C"), "MetaMorpheus can search it");
    }

    // PXD058611 (aging 061, S82, PXR-A54): thiols blocked with NBF-Cl, 0.20% cysteine peptides. No modelled modification, so
    // the deposit waits; before, it fell through to the default carbamidomethyl.
    [TestCase("Free thiols were blocked with NBF-Cl before labelling with DCP-Bio1")]
    [TestCase("lysed in buffer containing 4-chloro-7-nitrobenzofurazan")]
    public void AnUnmodelledThiolReagentParksInsteadOfDefaulting(string protocol)
    {
        var c = ChemistryDetector.Detect(Project(protocol), null, null, Files);
        Assert.That((c.Park, c.CysMods.Count, c.Alkylation.Source), Is.EqualTo(("waiting_unknown_alkylant", 0, ChemistrySource.ProtocolText)));
        var both = ChemistryDetector.Detect(Project(protocol + "; peptides were alkylated with iodoacetamide"), null, null, Files);
        Assert.That((both.Park, both.CysMods.Single().Name), Is.EqualTo(((string?)null, "Carbamidomethyl")), "a modelled alkylant named beside it wins");
    }

    // G40 (aging 072 PXR-A69, our 074): a profile that ships a modification for the reagent searches it VARIABLE instead
    // of parking (the PXD058611 probe: NBF on 76-86% of identified cysteines, 14-24% unmodified).
    [TestCase("Free thiols were blocked with NBF-Cl before labelling with DCP-Bio1")]
    [TestCase("lysed in buffer containing 4-chloro-7-nitrobenzofurazan")]
    public void AThiolReagentTheProfileModelsIsSearchedVariable(string protocol)
    {
        var reagents = new Dictionary<string, string> { ["NBF-Cl"] = "PXReprise\tNBF on C" };
        var c = ChemistryDetector.Detect(Project(protocol), null, null, Files, reagents);
        Assert.That(c.Park, Is.Null);
        Assert.That(c.Alkylation.Value, Is.EqualTo("NBF-Cl"), "the reagent's canonical name, however the protocol spells it");
        Assert.That(c.CysMods, Is.EqualTo(new[] { new CysMod("NBF", "", false, "PXReprise\tNBF on C") }));
        Assert.That(c.CysMods.Single().MetaMorpheusId, Is.EqualTo("PXReprise\tNBF on C"));
        Assert.That(c.Alkylation.Evidence, Does.Contain("variable PXReprise NBF on C"));

        var other = ChemistryDetector.Detect(Project(protocol), null, null, Files, new Dictionary<string, string>());
        Assert.That(other.Park, Is.EqualTo("waiting_unknown_alkylant"), "a profile that models no reagent parks it, as before");
        var named = ChemistryDetector.Detect(Project(protocol + "; peptides were alkylated with iodoacetamide"), null, null, Files, reagents);
        Assert.That(named.CysMods.Single().Name, Is.EqualTo("Carbamidomethyl"), "a modelled alkylant named beside it still wins");
    }

    [TestCase("alkylated with 55 mM iodoacetamide", "Carbamidomethyl", true)]
    [TestCase("alkylated with chloroacetamide (CAA)", "Carbamidomethyl", true)]
    [TestCase("cysteines were alkylated with acrylamide", "Propionamide", true)]
    [TestCase("blocked with MMTS", "Methylthio", true)]
    [TestCase("reduced with DTT and alkylated with iodoethanol in 8 M urea", "Ethanolyl", true)]   // PXD057499 (aging 089)
    public void AStatedAlkylantIsReadFromTheProtocol(string protocol, string mod, bool isFixed)
    {
        var c = ChemistryDetector.Detect(Project(protocol), null, null, Files);
        Assert.That((c.CysMods.Single().Name, c.CysMods.Single().Fixed, c.Alkylation.Guessed), Is.EqualTo((mod, isFixed, true)));
    }

    [Test]
    public void APolyacrylamideGelIsNotAnAlkylantAndNoAlkylationMeansNoCysMod()
    {
        Assert.That(ChemistryDetector.Detect(Project("separated on a 12% polyacrylamide gel"), null, null, Files).Alkylation.Source,
            Is.EqualTo(ChemistrySource.Default));
        var none = ChemistryDetector.Detect(Project("digested without reduction and alkylation"), null, null, Files);
        Assert.That((none.Alkylation.Value, none.CysMods.Count), Is.EqualTo(("none", 0)));
    }

    [TestCase("digested with Lys-C and then trypsin overnight", "trypsin")]
    [TestCase("cells were detached with trypsin-EDTA; proteins were digested with Glu-C", "Glu-C")]
    [TestCase("cells were harvested by trypsin treatment and digested with chymotrypsin", "chymotrypsin|P")]
    [TestCase("digestion with Asp-N", "Asp-N")]
    public void TheProteaseIsTheOneThatDigested(string protocol, string protease)
    {
        Assert.That(ChemistryDetector.Detect(Project(protocol), null, null, Files).Protease.Value, Is.EqualTo(protease));
    }

    [Test]
    public void SeveralProteasesAreAssignedPerFileByNameElseParked()
    {
        const string text = "aliquots were digested with trypsin or with Glu-C";
        var named = ChemistryDetector.Detect(Project(text), null, null, new[] { "S1_Tryp_1.raw", "S1_GluC_1.raw" });
        Assert.That((named.Protease.Value, named.Park), Is.EqualTo(("multiple", (string?)null)));
        Assert.That(named.ProteasePerFile, Is.EquivalentTo(new Dictionary<string, string> { ["S1_Tryp_1.raw"] = "trypsin", ["S1_GluC_1.raw"] = "Glu-C" }));
        var unnamed = ChemistryDetector.Detect(Project(text), null, null, new[] { "S1_1.raw", "S1_2.raw" });
        Assert.That((unnamed.Park, unnamed.ProteasePerFile), Is.EqualTo(("waiting_multi_protease", (IReadOnlyDictionary<string, string>?)null)));
    }

    [Test]
    public void AnSdrfOutranksPrideAndTheText()
    {
        string dir = TestSupport.TempDir();
        string sdrf = "source name\tcomment[data file]\tcomment[cleavage agent details]\tcomment[modification parameters]\tcomment[label]\n"
            + "s1\ta.raw\tNT=Lys-C;AC=MS:1001309\tNT=Carbamidomethyl;AC=UNIMOD:4;TA=C;MT=Fixed\tlabel free sample\n"
            + "s2\tb.raw\tNT=Lys-C;AC=MS:1001309\tNT=Carbamidomethyl;AC=UNIMOD:4;TA=C;MT=Fixed\tlabel free sample\n";
        var doc = new SdrfDocument(TestSupport.WriteFile(dir, "x.sdrf.tsv", sdrf));
        var c = ChemistryDetector.Detect(Project("digested with trypsin; NEM", new[] { "Nethylmaleimide" }, new[] { "TMT" }), null, doc, Files);
        Assert.That((c.Protease.Value, c.Protease.Source), Is.EqualTo(("Lys-C|P", ChemistrySource.DepositedSdrf)));
        Assert.That((c.Alkylation.Value, c.Alkylation.Source), Is.EqualTo(("Carbamidomethyl", ChemistrySource.DepositedSdrf)));
        Assert.That(c.Label.Source, Is.EqualTo(ChemistrySource.PrideQuant), "a label-free SDRF label says nothing; PRIDE's TMT is next");
    }

    // Oracle 2026-10-05: MetaMorpheus's mod strings come from mzLib's lookups, never from a table of ours.
    [TestCase("UNIMOD:4", "Common Fixed\tCarbamidomethyl on C")]
    [TestCase("UNIMOD:108", "Unimod\tNethylmaleimide on C")]
    [TestCase("UNIMOD:776", "Unimod\tNEM:2H(5) on C")]
    [TestCase("UNIMOD:39", "Unimod\tMethylthio on C")]
    [TestCase("Unimod:24", "Less Common\tPropionamidation on C")]   // mzLib's own entry; MetaMorpheus 1.1.11's Mods.txt has it too
    [TestCase("UNIMOD:278", "Unimod\tEthanolyl on C")]   // iodoethanol (aging 089)
    [TestCase("4", "Common Fixed\tCarbamidomethyl on C")]
    public void CysteineModsResolveToMetaMorpheusStringsThroughMzLib(string unimod, string expected) =>
        Assert.That(ChemistryDetector.MetaMorpheusModId(unimod, 'C'), Is.EqualTo(expected));

    [Test]
    public void ProteaseNamesResolveThroughMzLibsDictionary()
    {
        foreach (string p in new[] { "trypsin", "Lys-C|P", "Glu-C", "Asp-N", "chymotrypsin|P", "Arg-C", "Lys-N", "non-specific" })
            Assert.That(ChemistryDetector.ResolveProtease(p), Is.Not.Null, p);
        Assert.That(ChemistryDetector.ResolveProtease("no such enzyme"), Is.Null);
    }

    [Test]
    public void AnSdrfsOwnFixedOrVariableIsKeptAndATmtLabelIsReadByMzLibsAuditor()
    {
        string dir = TestSupport.TempDir();
        string sdrf = "source name\tcomment[data file]\tcomment[modification parameters]\tcomment[modification parameters]\tcomment[label]\n"
            + "s1\ta.raw\tNT=Nethylmaleimide;AC=UNIMOD:108;TA=C;MT=Variable\tNT=Oxidation;AC=UNIMOD:35;TA=M;MT=Variable\tTMT126\n"
            + "s2\ta.raw\tNT=Nethylmaleimide;AC=UNIMOD:108;TA=C;MT=Variable\tNT=Oxidation;AC=UNIMOD:35;TA=M;MT=Variable\tTMT127\n";
        var c = ChemistryDetector.Detect(Project("x"), null, new SdrfDocument(TestSupport.WriteFile(dir, "y.sdrf.tsv", sdrf)), Files);
        Assert.That(c.CysMods.Select(m => (m.MetaMorpheusId, m.Fixed)), Is.EqualTo(new[] { ("Unimod\tNethylmaleimide on C", false) }), "only cysteine mods, as the SDRF says");
        Assert.That((c.Label.Value, c.Label.Source), Is.EqualTo(("tmt", ChemistrySource.DepositedSdrf)));
    }

    [Test]
    public void SdrfCellFieldsAreRead() =>
        Assert.That(ChemistryDetector.Field("NT=Carbamidomethyl; AC=UNIMOD:4;TA=C;MT=Fixed", "AC"), Is.EqualTo("UNIMOD:4"));
}
