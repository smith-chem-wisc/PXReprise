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
    // v0.4.34 (TMT trial, PXD034564): the KIT, for MetaMorpheus's MultiplexModId. A kit's extra channel seen in only a few
    // spectra (an impurity or isotope peak; 131C beside TMT10's 131N there) does not make it the larger kit.
    private static double[] Reporters(Omics.Modifications.IsobaricMassTagType kit) =>
        Omics.Modifications.IsobaricMassTag.TryGetIsobaricMassTag(kit, out var tag) ? tag!.ReporterIonMzs : throw new InvalidOperationException();

    [Test]
    public void TheKitIsTheLargestWhoseEveryChannelIsSeenAsOftenAsTheOthers()
    {
        double[] tmt10 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT10);
        double[] tmt11 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT11);
        double c131 = tmt11.Except(tmt10).Single();
        // 100 spectra with TMT10's ten channels; 20 of them also carry 131C (a fifth: the trial's tail of 131N).
        var spectra = Enumerable.Range(0, 100).Select(i => Scan(i, 3, i < 20 ? tmt10.Append(c131).ToArray() : tmt10).MassSpectrum).ToList();
        var kit = IsobaricReporters.Kit(spectra, "TMT");
        Assert.That(kit?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT10));
        Assert.That(IsobaricReporters.ModificationId(kit!.Value.Kit), Is.EqualTo("TMT10"));
        // When 131C is as common as the rest, it is TMT11.
        var eleven = Enumerable.Range(0, 100).Select(i => Scan(i, 3, tmt11).MassSpectrum).ToList();
        Assert.That(IsobaricReporters.Kit(eleven, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT11));
        // TMTpro 18 (all eighteen) is searched under TMT18; TMT16 too.
        var pro = Enumerable.Range(0, 100).Select(i => Scan(i, 2, Reporters(Omics.Modifications.IsobaricMassTagType.TMT18)).MassSpectrum).ToList();
        Assert.That(IsobaricReporters.Kit(pro, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT18));
        Assert.That(IsobaricReporters.ModificationId(Omics.Modifications.IsobaricMassTagType.TMT16), Is.EqualTo("TMT18"));
        // TMT6's six channels only.
        var six = Enumerable.Range(0, 100).Select(i => Scan(i, 2, Reporters(Omics.Modifications.IsobaricMassTagType.TMT6)).MassSpectrum).ToList();
        Assert.That(IsobaricReporters.Kit(six, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT6));
        Assert.That(IsobaricReporters.Kit(spectra, "unknown family"), Is.Null);
    }

    // v0.4.34 (PLAN_tmt-dda-2 stage 4): SPS-MS3, as PXD034564: MS2 in the ion trap (CID) carries no reporters, MS3 in the
    // Orbitrap carries them. Under an isobaric profile the low-res MS2 gate is waived and the kit is read from MS3.
    [Test]
    public void AnIsobaricProfileReadsTheKitAndAcceptsSpsMs3()
    {
        double[] tmt10 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT10);
        MsDataScan IonTrapMs2(int n) => new(new MzSpectrum(new[] { 300.0, 500.0, 700.0 }, new[] { 1e4, 1e4, 1e4 }, false), n, 2, true, Polarity.Positive,
            n / 10.0, new MzRange(100, 2000), "", MZAnalyzerType.IonTrap2D, 1e6, 10, null, $"scan={n}", selectedIonMz: 500, dissociationType: DissociationType.CID);
        var scans = Enumerable.Range(0, 60).SelectMany(i => new[] { IonTrapMs2(2 * i), Scan(2 * i + 1, 3, tmt10) }).ToList();

        var tmt = SpectraQc.Evaluate(scans, Gates, isobaric: true);
        Assert.That(tmt.FailReasons, Does.Not.Contain(SpectraQc.LowResMs2), "SPS-MS3: the ion-trap MS2 is expected");
        Assert.That((tmt.IsobaricKit, tmt.SpsMs3), Is.EqualTo(("TMT10", (bool?)true)));
        Assert.That(SpectraQc.Evaluate(scans, Gates).FailReasons, Does.Contain(SpectraQc.LowResMs2), "a label-free profile keeps the gate");

        var unlabelled = Enumerable.Range(0, 60).Select(i => Scan(i, 2, 300, 500, 700)).ToList();
        var none = SpectraQc.Evaluate(unlabelled, Gates, isobaric: true);
        Assert.That(none.FailReasons, Does.Contain(SpectraQc.NoReporters));
        Assert.That((none.IsobaricKit, none.SpsMs3), Is.EqualTo(((string?)null, (bool?)false)));
    }
}
