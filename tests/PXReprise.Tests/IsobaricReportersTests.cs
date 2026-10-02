using MassSpectrometry;
using MzLibUtil;
using PXReprise.Config;
using PXReprise.Qc;
using Readers;

namespace PXReprise.Tests;

// G4 (aging S64): PXD052189's text never says TMT, so a TMT six-plex was searched as label-free. The spectra say it.
public class IsobaricReportersTests
{
    private static QcGates Gates => ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@1"].Qc;

    private static MsDataScan Scan(int n, int msn, params double[] mz)
    {
        var sorted = mz.OrderBy(x => x).ToArray();
        return new MsDataScan(new MzSpectrum(sorted, sorted.Select(_ => 1e5).ToArray(), false), n, msn, true, Polarity.Positive,
            n / 10.0, new MzRange(100, 2000), "", MZAnalyzerType.Orbitrap, 1e6, 10, null, $"scan={n}",
            selectedIonMz: msn > 1 ? 500 : null, dissociationType: DissociationType.HCD);
    }

    [Test]
    public void TheKitsAreMzLibsAndHoldTheReporterMassesMetaMorpheusUses()
    {
        var kits = IsobaricReporters.Kits.ToDictionary(k => k.Tag, k => k.Mzs);
        Assert.That(kits.Keys, Is.SupersetOf(new[] { "TMT6-plex", "TMT10", "TMT11", "TMT18", "iTRAQ-4plex", "iTRAQ-8plex" }));
        Assert.That(kits["TMT18"], Has.Length.EqualTo(18));
        Assert.That(kits["TMT10"][0], Is.EqualTo(126.1277).Within(0.0001));
        Assert.That(IsobaricReporters.Family("TMT6-plex"), Is.EqualTo("TMT"));
        Assert.That(IsobaricReporters.Family("iTRAQ-8plex"), Is.EqualTo("iTRAQ"));
    }

    [Test]
    public void ReporterIonsInMostSpectraMakeAFileTmtAndLabelFreePeaksDoNot()
    {
        double[] tmt6 = IsobaricReporters.Kits.Single(k => k.Tag == "TMT6-plex").Mzs;
        var labelled = Enumerable.Range(1, 40).Select(i => Scan(i, 2, tmt6.Concat(new[] { 175.119, 300.2, 450.7 }).ToArray())).ToList();
        Assert.That(IsobaricReporters.Detect(labelled).Tag, Is.EqualTo("TMT"));

        // Immonium and y1 ions sit near the reporters but not within 3 mDa of three of them.
        var labelFree = Enumerable.Range(1, 40).Select(i => Scan(i, 2, 110.0713, 120.0808, 126.0913, 129.1022, 136.0757, 147.1128, 175.119)).ToList();
        var none = IsobaricReporters.Detect(labelFree);
        Assert.That((none.Tag, none.Fraction), Is.EqualTo(((string?)null, 0.0)));

        // A few labelled spectra in a label-free file are noise, not a label.
        var sparse = labelFree.Take(36).Concat(labelled.Take(4)).ToList();
        Assert.That(IsobaricReporters.Detect(sparse).Tag, Is.Null);
    }

    [Test]
    public void SpsMs3TmtIsFoundInItsMs3Spectra()
    {
        // MetaMorpheus's test file (Test/TMT_test, MIT): TMT11, ion-trap MS2 with the reporters in SPS-MS3.
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "MS3_TMT11_Mouse_snip.mzML");
        var scans = MsDataFileReader.GetDataFile(path).LoadAllStaticData().GetAllScansList();
        var ev = IsobaricReporters.Detect(scans);
        Assert.That(ev.Tag, Is.EqualTo("TMT"));
        Assert.That(ev.Fraction, Is.EqualTo(0.5), "every MS3 carries reporters, no MS2 does");

        var qc = SpectraQc.Evaluate(scans, Gates, refuseIsobaric: true);
        Assert.That(qc.FailReasons, Does.Contain(SpectraQc.IsobaricLabelled));
        Assert.That(SpectraQc.Evaluate(scans, Gates).FailReasons, Does.Not.Contain(SpectraQc.IsobaricLabelled), "only a profile that refuses isobaric labels looks");
    }
}
