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

    // v0.4.41: every channel's m/z is the channel two below it plus one carbon-13, so a strong channel's isotope lands on the next
    // kit's channel. Measured 2026-11-06, each channel's median height against that parent, on real files: isotopes 0.03 to 0.07,
    // real channels 0.37 to 1.39 (isotopes up to 0.08 on all 39 files). These tests use the measured shares and heights.
    private static MzSpectrum ReporterSpectrum(int i, int ofHundred, IReadOnlyDictionary<string, (int In, double Height)> extra, string[] plain)
    {
        var tag = Tmt18;
        var points = new SortedDictionary<double, double>();
        var labels = tag.ChannelLabels.ToArray();
        for (int c = 0; c < labels.Length; c++)
        {
            if (plain.Contains(labels[c])) points[tag.ReporterIonMzs[c]] = 1000;
            else if (extra.TryGetValue(labels[c], out var e) && i * 100 / ofHundred < e.In) points[tag.ReporterIonMzs[c]] = 1000 * e.Height;
        }
        return new MzSpectrum(points.Keys.ToArray(), points.Values.ToArray(), false);
    }

    private static Omics.Modifications.IsobaricMassTag Tmt18 =>
        Omics.Modifications.IsobaricMassTag.TryGetIsobaricMassTag(Omics.Modifications.IsobaricMassTagType.TMT18, out var t) ? t! : throw new InvalidOperationException();

    private static readonly string[] Tmt10Labels = { "126", "127N", "127C", "128N", "128C", "129N", "129C", "130N", "130C", "131N" };

    [TestCase(60, 0.03, TestName = "A TMT10 file is TMT10 though 131N's isotope sits on 132N in 60 percent of its spectra (PXD034564 01_FS_50)")]
    [TestCase(90, 0.09, TestName = "A TMT10 file is TMT10 however often a bright 131N's isotope is seen")]
    public void AChannelsCarbon13IsotopeIsNotALargerKit(int inPercent, double height)
    {
        var extra = new Dictionary<string, (int, double)> { ["131C"] = (25, 0.07), ["132N"] = (inPercent, height) };
        var spectra = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, extra, Tmt10Labels)).ToList();

        Assert.That(IsobaricReporters.Kit(spectra, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT10));
    }

    [Test]
    public void ARealChannelStaysSeenAtItsOwnHeightEvenWhenItIsNotInEverySpectrum()
    {
        // 132N as tall as 131N is TMTpro's own channel, in however many spectra.
        var extra = new Dictionary<string, (int, double)> { ["132N"] = (60, 0.99) };
        var spectra = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, extra, Tmt10Labels)).ToList();

        Assert.That(IsobaricReporters.Kit(spectra, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT16));
    }

    [Test]
    public void RealTmtproIsStillTmtproAndItsOwnIsotopesAreNotCounted()
    {
        // PXD043296 Frac1: 126 to 132N in every spectrum at equal height; 132C and 133N its isotopes at 0.03 in 44% and 56%.
        var extra = new Dictionary<string, (int, double)> { ["132C"] = (44, 0.03), ["133N"] = (56, 0.03) };
        var plain = Tmt10Labels.Concat(new[] { "131C", "132N" }).ToArray();
        var spectra = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, extra, plain)).ToList();

        var kit = IsobaricReporters.Kit(spectra, "TMT");
        Assert.That(kit?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT16));
        Assert.That(IsobaricReporters.ModificationId(kit!.Value.Kit), Is.EqualTo("TMT18"), "the mod TMTpro is searched with");
    }

    [Test]
    public void AReal131CIsStillTmt11()
    {
        var extra = new Dictionary<string, (int, double)> { ["131C"] = (90, 0.97) };
        var spectra = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, extra, Tmt10Labels)).ToList();

        Assert.That(IsobaricReporters.Kit(spectra, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT11));
    }

    // v0.4.36 (TMT phospho trial, PXD027548, 2026-11-02): a deposit that leaves a channel unused is still its kit. Measured
    // on its Q Exactive MS2: 1.raw (TMT10 proteome) 130C in 42% of reporter spectra and every other channel in 98-100%;
    // Z9423TPSTB2_1.raw (TMT10 phospho) 131N in 34%, 131C in 20%. v0.4.35 wanted EVERY channel of a kit and found none
    // (TMT6 holds 130C and 131N too), so both files failed QC as no_isobaric_reporters.
    [Test]
    public void AKitWithAnUnusedChannelIsTheSmallestKitHoldingEveryChannelSeen()
    {
        double[] tmt10 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT10);
        double[] tmt11 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT11);
        double c130 = tmt10[8], n131 = tmt10[9], c131 = tmt11.Except(tmt10).Single();
        Assert.That((c130, n131), Is.EqualTo((130.141145, 131.13818)).Within(0.001), "TMT10's 130C and 131N");

        // 1.raw: 130C in 42 of 100 spectra.
        var proteome = Enumerable.Range(0, 100).Select(i => Scan(i, 2, tmt10.Where(x => x != c130 || i < 42).ToArray()).MassSpectrum).ToList();
        Assert.That(IsobaricReporters.Kit(proteome, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT10));

        // Z9423TPSTB2_1.raw: 131N in 34, 131C in 20.
        var phospho = Enumerable.Range(0, 100).Select(i => Scan(i, 2,
            tmt10.Where(x => x != n131 || i < 34).Concat(i >= 80 ? new[] { c131 } : Array.Empty<double>()).ToArray()).MassSpectrum).ToList();
        var kit = IsobaricReporters.Kit(phospho, "TMT");
        Assert.That(kit?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT10));
        Assert.That(kit!.Value.ChannelShares["131N"], Is.EqualTo(0.34).Within(1e-9), "the shares still say which channel is weak");

        // TMT11 with 131N unused: 131C is seen, so only TMT11 holds every channel seen.
        var eleven = Enumerable.Range(0, 100).Select(i => Scan(i, 2, tmt11.Where(x => x != n131).ToArray()).MassSpectrum).ToList();
        Assert.That(IsobaricReporters.Kit(eleven, "TMT")?.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT11));
    }

    // v0.4.37: PXD051031's search was refused because QC read TMT10 in some files and TMT11 in others (131C weak in a few
    // files). One reagent: the deposit is the largest kit. Different reagents, or a file with none, have no deposit kit.
    [Test]
    public void FilesOfOneReagentReadingDifferentKitsAreSearchedWithTheLargest()
    {
        Assert.That(IsobaricReporters.DepositKit(new[] { "TMT10", "TMT11", "TMT10" }), Is.EqualTo("TMT11"));
        Assert.That(IsobaricReporters.DepositKit(new[] { "TMT6-plex", "TMT10" }), Is.EqualTo("TMT10"));
        Assert.That(IsobaricReporters.DepositKit(new[] { "TMT18", "TMT18" }), Is.EqualTo("TMT18"));
        Assert.That(IsobaricReporters.DepositKit(new[] { "TMT11", "TMT18" }), Is.Null, "TMTpro is another reagent");
        Assert.That(IsobaricReporters.DepositKit(new[] { "iTRAQ-4plex", "iTRAQ-8plex" }), Is.Null, "so are the two iTRAQ kits");
        Assert.That(IsobaricReporters.DepositKit(new[] { "TMT10", null }), Is.Null);
        Assert.That(IsobaricReporters.DepositKit(new string?[] { null }), Is.Null);
        Assert.That(IsobaricReporters.DepositKit(Array.Empty<string?>()), Is.Null);
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
