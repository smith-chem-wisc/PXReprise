using Proteomics.ProteolyticDigestion;
using PXReprise.Discovery;
using PXReprise.Search;
using Readers;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.28: PXReprise's own digestion agents (profiles/lists/PXReprise_custom_proteases_v1.tsv). The first,
/// trypsin+chymotrypsin, is for a MED-FASP chymotrypsin fraction: on PXD006815's CT run, 651 PSMs as chymotrypsin|P and
/// 7,028 with it (a non-specific search found 8,139).
/// </summary>
public class CustomProteasesTests
{
    private static string Shipped => Path.Combine(TestSupport.ProfilesDir, "lists", CustomProteases.FileName);

    [SetUp]
    public void LoadTheShippedList() => CustomProteases.Load(Shipped);

    [Test]
    public void TheShippedListIsMergedIntoMzLibsDictionaryWithBothEnzymesSites()
    {
        Assert.That(CustomProteases.Names, Does.Contain(CustomProteases.TrypsinChymotrypsin));
        Assert.That(ProteaseDictionary.Dictionary.TryGetValue(CustomProteases.TrypsinChymotrypsin, out var p), Is.True);
        Assert.That(p!.DigestionMotifs.Select(m => m.InducingCleavage), Is.EquivalentTo(new[] { "K", "R", "F", "W", "Y", "L" }));
        Assert.That(p.DigestionMotifs.Where(m => m.PreventingCleavage is not null).Select(m => m.InducingCleavage), Is.EquivalentTo(new[] { "F", "W", "Y", "L" }),
            "the chymotrypsin sites keep chymotrypsin|P's no-proline rule; the trypsin sites are plain trypsin's");
        Assert.That(ChemistryDetector.ResolveProtease(CustomProteases.TrypsinChymotrypsin), Is.EqualTo(CustomProteases.TrypsinChymotrypsin));

        // Loading the profiles loads it, and loading it again changes nothing.
        Config.ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        CustomProteases.Load(Shipped);
        Assert.That(CustomProteases.Names.Count(n => n == CustomProteases.TrypsinChymotrypsin), Is.EqualTo(1));
    }

    [Test]
    public void ANameMzLibHasOrAChangedDefinitionIsRefused()
    {
        string dir = TestSupport.TempDir();
        const string header = "Name\tMotif\tSpecificity\tPSI-MS Accession\tPSI-MS Name\tCleavage Modification\n";
        Assert.That(() => CustomProteases.Load(TestSupport.WriteFile(dir, "builtin.tsv", header + "trypsin\tK|,R|\tfull\t\tTrypsin\n")),
            Throws.InstanceOf<Config.ConfigException>().With.Message.Contains("mzLib already has"));
        Assert.That(() => CustomProteases.Load(TestSupport.WriteFile(dir, "changed.tsv", header + "trypsin+chymotrypsin\tK|,R|,F|\tfull\t\t\n")),
            Throws.InstanceOf<Config.ConfigException>().With.Message.Contains("another definition"));
    }

    [Test]
    public void AnSdrfNamesItExactlyOrByNamingTrypsinAndChymotrypsinForOneFile()
    {
        string dir = TestSupport.TempDir();
        string sdrf = "source name\tcomment[data file]\tcomment[cleavage agent details]\tcomment[cleavage agent details]\tcomment[cleavage agent details]\n"
            + "s1\tL.raw\tNT=Lys-C;AC=MS:1001309\t\t\n"
            + "s1\tT.raw\tNT=Trypsin;AC=MS:1001251\t\t\n"
            + "s1\tCT.raw\tNT=Lys-C;AC=MS:1001309\tNT=Trypsin;AC=MS:1001251\tNT=Chymotrypsin;AC=MS:1001306\n"
            + "s1\tCT2.raw\tNT=trypsin+chymotrypsin\t\t\n";
        var c = ChemistryDetector.Detect(null, new SdrfDocument(TestSupport.WriteFile(dir, "x.sdrf.tsv", sdrf)), null,
            new[] { "L.raw", "T.raw", "CT.raw", "CT2.raw" });
        Assert.That(c.Park, Is.Null);
        Assert.That(c.ProteasePerFile, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["L.raw"] = "Lys-C|P", ["T.raw"] = "trypsin", ["CT.raw"] = CustomProteases.TrypsinChymotrypsin, ["CT2.raw"] = CustomProteases.TrypsinChymotrypsin,
        }));
        Assert.That(CustomProteases.UsedBy(c), Is.EqualTo(new[] { CustomProteases.TrypsinChymotrypsin }));
    }

    [Test]
    public void InstallingAddsOurEntryToMetaMorpheusesFileKeepingWhatIsThereAndItsLineEnds()
    {
        string settings = TestSupport.TempDir();
        const string theirs = "# MetaMorpheus's banner\r\nName\tMotif\tSpecificity\tPSI-MS Accession\tPSI-MS Name\tCleavage Modification\r\nmine\tE|\tfull\t\t\r\n";
        File.WriteAllText(Path.Combine(settings, CustomProteases.MetaMorpheusFileName), theirs);
        bool seeded = false;

        string path = CustomProteases.Install(settings, new[] { CustomProteases.TrypsinChymotrypsin }, () => seeded = true);
        string after = File.ReadAllText(path);
        Assert.That(seeded, Is.False, "the settings folder exists");
        Assert.That(after, Does.StartWith(theirs));
        Assert.That(after, Does.EndWith("trypsin+chymotrypsin\tK|,R|,F[P]|,W[P]|,Y[P]|,L[P]|\tfull\t\tTrypsin and chymotrypsin\r\n"));
        Assert.That(after.Replace("\r\n", "").Contains('\n'), Is.False, "the file's own CRLF line ends are kept");
        CustomProteases.Install(settings, new[] { CustomProteases.TrypsinChymotrypsin }, () => seeded = true);
        Assert.That(File.ReadAllText(path), Is.EqualTo(after), "installing again changes nothing");

        // mzLib reads the installed file back, as MetaMorpheus does at its start.
        Assert.That(ProteaseDictionary.LoadAndMergeCustomProteases(path).Skipped, Does.Contain(CustomProteases.TrypsinChymotrypsin));
    }

    [Test]
    public void InstallingSeedsAMissingSettingsFolderAndRefusesAnotherDefinitionThere()
    {
        string settings = Path.Combine(TestSupport.TempDir(), "1.1.12");
        string path = CustomProteases.Install(settings, new[] { CustomProteases.TrypsinChymotrypsin }, () => Directory.CreateDirectory(settings));
        Assert.That(File.ReadAllLines(path), Is.EqualTo(new[]
        {
            "Name\tMotif\tSpecificity\tPSI-MS Accession\tPSI-MS Name\tCleavage Modification",
            "trypsin+chymotrypsin\tK|,R|,F[P]|,W[P]|,Y[P]|,L[P]|\tfull\t\tTrypsin and chymotrypsin",
        }), "a file MetaMorpheus has not written yet gets mzLib's header first");

        File.WriteAllText(path, "Name\tMotif\tSpecificity\ntrypsin+chymotrypsin\tK|\tfull\n");
        Assert.That(() => CustomProteases.Install(settings, new[] { CustomProteases.TrypsinChymotrypsin }, () => { }),
            Throws.InstanceOf<SearchSetupException>().With.Message.Contains("otherwise"));
        Assert.That(() => CustomProteases.Install(settings, new[] { "no-such-protease" }, () => { }),
            Throws.InstanceOf<SearchSetupException>().With.Message.Contains("not a custom protease"));
    }
}
