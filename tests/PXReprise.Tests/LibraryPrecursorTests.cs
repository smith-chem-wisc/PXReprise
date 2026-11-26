using System.Globalization;
using System.Text.Json.Nodes;
using Chemistry;
using Proteomics.ProteolyticDigestion;
using PXReprise.Search;

namespace PXReprise.Tests;

/// <summary>
/// D84 (the user, 2026-11-21: "yes add the library clean"; MetaMorpheus #2961, #2962). Libraries MetaMorpheus wrote
/// before 1.1.15 store each precursor at the m/z its scan measured, and write a match with no full sequence under an
/// empty name. Registration sets each entry's precursor to its full sequence's theoretical m/z and drops the nameless
/// entries, so each chain is corrected at its first update. On a copy of rat.v039 (320,344 entries, 2026-11-21): 320,115
/// corrected, 16 nameless dropped, none unresolved; 48,959 had been more than 5 ppm off, 35,225 of them by one or two
/// 13C spacings.
/// </summary>
public class LibraryPrecursorTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Database(string dir) => TestSupport.WriteFile(dir, "db.xml", """
        <?xml version="1.0" encoding="UTF-8"?>
        <uniprot xmlns="http://uniprot.org/uniprot">
        <entry dataset="Swiss-Prot"><accession>P00001</accession><sequence length="17">MAAAPEPTIDEKAAAAR</sequence></entry>
        </uniprot>
        """);

    private static LibraryCleaning Cleaning(string dir) => LibraryCleaning.For(Array.Empty<string>(), new[] { Database(dir) }, blockedCTerm: false);

    private static double Theoretical(string fullSequence, int z) =>
        new PeptideWithSetModifications(fullSequence).MonoisotopicMass.ToMz(z);

    private static string Entry(string name, double mw, double peak = 100) =>
        $"Name: {name}\nMW: {mw.ToString("R", Inv)}\nComment: Parent={mw.ToString("R", Inv)} RT=42.5\nNum peaks: 1\n{peak:F5}\t1\t\"y1^1/0ppm\"\n";

    private const string Oxidation = "[Common Variable:Oxidation on M]";

    [Test]
    public void TheTheoreticalMzIsMzLibsForTheFullSequenceAndItsCharge()
    {
        var c = Cleaning(TestSupport.TempDir());
        Assert.That(c.TheoreticalPrecursorMz("PEPTIDEK", 2), Is.EqualTo(Theoretical("PEPTIDEK", 2)));
        Assert.That(c.TheoreticalPrecursorMz("PEPTM" + Oxidation + "IDEK", 3)!.Value,
            Is.EqualTo(Theoretical("PEPTMIDEK", 3) + 15.9949146 / 3).Within(1e-5), "the modification's mass is in it");
        Assert.That(c.TheoreticalPrecursorMz("PEPT[Nope:Nothing on T]IDEK", 2), Is.Null, "a modification that does not resolve");
        Assert.That(c.TheoreticalPrecursorMz("PEPTIDEK|PEPTLDEK", 2), Is.Null, "an ambiguous sequence names no one form");
        Assert.That(c.TheoreticalPrecursorMz("PEPTIDEK", 0), Is.Null);
    }

    [Test]
    public void AMeasuredPrecursorIsSetToTheTheoreticalOneAndANamelessEntryGoes()
    {
        string dir = TestSupport.TempDir();
        var c = Cleaning(dir);
        double t2 = Theoretical("PEPTIDEK", 2), t3 = Theoretical("SEQENTIALK", 3);
        double c13 = t2 + 1.0033548 / 2;   // the 13C peak taken for the monoisotopic one
        string unresolved = Entry("PEPT[Nope:Nothing on T]IDEK/2", 500.25);
        string src = TestSupport.WriteFile(dir, "in.msp",
            Entry("PEPTIDEK/2", c13) + Entry("/2", 612.3) + Entry("SEQENTIALK/3", t3, 200) + unresolved);
        string dest = Path.Combine(dir, "out.msp");

        var r = SpectralLibrary.CorrectPrecursors(src, dest, c);

        Assert.That((r.Corrected, r.Nameless, r.Unresolved), Is.EqualTo((1, 1, 1)));
        Assert.That(File.ReadAllText(dest), Is.EqualTo(Entry("PEPTIDEK/2", t2) + Entry("SEQENTIALK/3", t3, 200) + unresolved),
            "MW and Parent= move, nothing else; an entry already at its m/z and one that cannot be judged stay byte for byte");
    }

    [Test]
    public void AFileWithNothingToCorrectIsNotWrittenAndAWindowsFileKeepsItsLineEnds()
    {
        string dir = TestSupport.TempDir();
        var c = Cleaning(dir);
        string clean = TestSupport.WriteFile(dir, "clean.msp", Entry("PEPTIDEK/2", Theoretical("PEPTIDEK", 2)));
        var none = SpectralLibrary.CorrectPrecursors(clean, Path.Combine(dir, "none.msp"), c);
        Assert.That(none.Changed, Is.False);
        Assert.That(File.Exists(Path.Combine(dir, "none.msp")), Is.False, "the caller keeps MetaMorpheus's own file");
        Assert.That(Directory.GetFiles(dir, "*.partial"), Is.Empty);

        string crlf = TestSupport.WriteFile(dir, "crlf.msp", Entry("PEPTIDEK/2", 500.25).Replace("\n", "\r\n"));
        string dest = Path.Combine(dir, "crlf.out.msp");
        SpectralLibrary.CorrectPrecursors(crlf, dest, c);
        Assert.That(File.ReadAllText(dest), Is.EqualTo(Entry("PEPTIDEK/2", Theoretical("PEPTIDEK", 2)).Replace("\n", "\r\n")));
    }

    [Test]
    public void EachRegistrationCorrectsWhatItInheritsAndSaysSo_ThenLeavesMetaMorpheusFileAsItIs()
    {
        string root = TestSupport.TempDir(), dir = TestSupport.TempDir();
        var c = Cleaning(dir);
        double t2 = Theoretical("PEPTIDEK", 2);

        // A version written by an older MetaMorpheus: the measured m/z and a nameless entry.
        string run1 = TestSupport.TempDir();
        TestSupport.WriteFile(run1, "SpectralLibrary_1.msp", Entry("PEPTIDEK/2", t2 + 1.0033548 / 2) + Entry("/2", 612.3));
        var v1 = SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run1, "run1", "PXD000001", "1.1.14", c);
        var cleaning = v1["cleaning"]!.AsObject();
        Assert.That(cleaning["precursor_rule"]!.GetValue<string>(), Does.StartWith("pxreprise-library-precursor/1"));
        Assert.That((int)cleaning["precursor_corrected"]!, Is.EqualTo(1));
        Assert.That((int)cleaning["dropped_nameless"]!, Is.EqualTo(1));
        Assert.That((int)v1["n_spectra"]!, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(root, "mouse/mouse.v001.msp")), Is.EqualTo(Entry("PEPTIDEK/2", t2)));
        Assert.That(Directory.GetFiles(Path.Combine(root, "mouse"), ".precursor-*"), Is.Empty, "the working copy is gone");

        // 1.1.15 updates the corrected version: nothing to correct, so the version is MetaMorpheus's file byte for byte,
        // and the prune stage can delete the run folder's copy.
        string run2 = TestSupport.TempDir();
        string written = TestSupport.WriteFile(run2, "updateSpectralLibrary_2.msp",
            Entry("PEPTIDEK/2", t2) + Entry("SEQENTIALK/3", Theoretical("SEQENTIALK", 3), 200));
        var v2 = SpectralLibrary.Register(SpectralLibrary.Plan(root, "mouse"), run2, "run2", "PXD000002", "1.1.15", c);
        Assert.That((int)v2["cleaning"]!["precursor_corrected"]!, Is.EqualTo(0));
        Assert.That(File.ReadAllBytes(Path.Combine(root, "mouse/mouse.v002.msp")), Is.EqualTo(File.ReadAllBytes(written)));
    }

    [Test]
    public void ARepairCorrectsTheCurrentVersionWithNoSearch()
    {
        string root = TestSupport.TempDir(), dir = TestSupport.TempDir();
        double t2 = Theoretical("PEPTIDEK", 2);
        string run1 = TestSupport.TempDir();
        TestSupport.WriteFile(run1, "SpectralLibrary_1.msp", Entry("PEPTIDEK/2", t2 + 1.0033548 / 2));
        SpectralLibrary.Register(SpectralLibrary.Plan(root, "rat"), run1, "run1", "PXD000001", "1.1.14");   // registered with no rules

        var repaired = SpectralLibrary.Repair(root, "rat", "D84 precursor correction", Cleaning(dir));
        Assert.That(repaired["repaired"]!.GetValue<bool>(), Is.True);
        Assert.That((int)repaired["cleaning"]!["precursor_corrected"]!, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(root, "rat/rat.v002.msp")), Is.EqualTo(Entry("PEPTIDEK/2", t2)));

        var again = SpectralLibrary.Repair(root, "rat", "again", Cleaning(dir));
        Assert.That(again["repaired"]!.GetValue<bool>(), Is.False, "a corrected chain has nothing to repair");
    }
}
