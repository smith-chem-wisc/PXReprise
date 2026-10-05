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

    // PXD047288 and eight siblings: ubiquitin studies; NEM in the lysis buffer inhibits deubiquitinases.
    [Test]
    public void NemInALysisBufferIsNotTheAlkylation()
    {
        var c = ChemistryDetector.Detect(Project("Samples were homogenized in denaturing lysis buffer (0.05% SDS) with 70mM NEM, briefly centrifuged"), null, null, Files);
        Assert.That((c.Alkylation.Value, c.Alkylation.Source), Is.EqualTo(("Carbamidomethyl", ChemistrySource.Default)));
    }

    [TestCase("alkylated with 55 mM iodoacetamide", "Carbamidomethyl", true)]
    [TestCase("alkylated with chloroacetamide (CAA)", "Carbamidomethyl", true)]
    [TestCase("cysteines were alkylated with acrylamide", "Propionamide", true)]
    [TestCase("blocked with MMTS", "Methylthio", true)]
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

    [Test]
    public void SdrfCellFieldsAreRead() =>
        Assert.That(ChemistryDetector.Field("NT=Carbamidomethyl; AC=UNIMOD:4;TA=C;MT=Fixed", "AC"), Is.EqualTo("UNIMOD:4"));
}
