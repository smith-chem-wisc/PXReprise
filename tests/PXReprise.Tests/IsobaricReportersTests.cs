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

    // v0.4.43 (PXD007229, 2026-11-09): a chance peak within 3 mDa of a channel the deposit never used, in a few of a file's thousands
    // of spectra, is not a channel. PXD007229 is six TMT 6-plex sets (its paper; LTQ Orbitrap XL, 2016). Its files' six channels are each
    // in about 97% of the reporter spectra, and ten other channels of the largest kit in 0.02% to 0.3%. The "typical" share was the median
    // of every channel present at all: ten noise channels outnumber six real ones, the median fell to 0.001, and any channel in four
    // spectra was seen. 32 of its 60 files read TMT18, 5 TMT10, 23 TMT6, and its search was refused for mixing reagents. Each case is
    // one real file's measured share (per 10,000 reporter spectra) and isotope height; a noise channel is given its parent's full
    // height, so only the share rule can leave it out.
    private static List<MzSpectrum> MeasuredSpectra(string channels)
    {
        var tag = Tmt18;
        var labels = tag.ChannelLabels.ToArray();
        var parsed = channels.Split(' ').Select(c => c.Split(':')).ToDictionary(p => p[0], p => (Count: int.Parse(p[1]), Height: p.Length > 2 ? double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) : 1.0));
        return Enumerable.Range(0, 10_000).Select(i =>
        {
            var points = new SortedDictionary<double, double>();
            for (int c = 0; c < labels.Length; c++)
                if (parsed.TryGetValue(labels[c], out var p) && i < p.Count) points[tag.ReporterIonMzs[c]] = 1000 * p.Height;
            return new MzSpectrum(points.Keys.ToArray(), points.Values.ToArray(), false);
        }).ToList();
    }

    [TestCase("126:9655 127N:9750 128C:9847 129N:9847 130C:9767 131N:9805 132N:3149:0.03 127C:12 128N:2 129C:2 132C:9 133N:3 133C:3 134N:5 134C:2 135N:7",
        "TMT6", "126 127N 128C 129N 130C 131N", TestName = "A TMT6 file with chance peaks on ten other channels is TMT6, not TMT18 (PXD007229 M1-1)")]
    [TestCase("126:9743 127N:9749 128C:9796 129N:9860 130C:9690 131N:9795 132N:3174:0.03 127C:17 128N:14 129C:12 130N:15 132C:5 133N:2 133C:3 134N:3 134C:3",
        "TMT6", "126 127N 128C 129N 130C 131N", TestName = "A TMT6 file with a chance peak on 129C is TMT6, and 129C holds no sample (PXD007229 M1-8)")]
    [TestCase("126:9452 127N:9858 128C:9782 129N:9847 130C:9612 131N:9722 132N:3031:0.03 127C:2 128N:2 130N:14 132C:9 133N:2 133C:8 134N:3 134C:5 135N:3",
        "TMT6", "126 127N 128C 129N 130C 131N", TestName = "A TMT6 file with chance peaks on 132C and 133C is TMT6, not TMTpro 16 (PXD007229 M2-1)")]
    [TestCase("126:9700 127N:9700 129C:9700 130N:9700 130C:9700 131N:9700 127C:12 128N:9 128C:14 129N:6 132C:9 133N:3 133C:3 134N:5 134C:2 135N:7",
        "TMT10", "126 127N 129C 130N 130C 131N", TestName = "A TMT10 experiment on six channels keeps exactly those six beside chance peaks (as PXD006743)")]
    public void AChancePeakOnAnUnusedChannelIsNotAChannel(string measured, string kit, string seen)
    {
        var k = IsobaricReporters.Kit(MeasuredSpectra(measured), "TMT");

        Assert.That(k, Is.Not.Null);
        Assert.That(IsobaricReporters.ModificationId(k!.Value.Kit), Is.EqualTo(kit == "TMT6" ? "TMT6-plex" : kit));
        Assert.That(k.Value.Seen, Is.EqualTo(seen.Split(' ')), "the channels a default design calls samples");
    }

    // v0.4.41: which channels hold a sample, for a default design. Each case is a delivered deposit at its measured shares and
    // heights. The kit stays what it was; these channels were delivered as samples.
    [Test]
    public void TheKitSaysWhichChannelsHoldASample()
    {
        // PXD051031, h00445: 130C's isotope on 131C in 48% of the spectra, at 0.045.
        var pxd051031 = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, new Dictionary<string, (int, double)> { ["131C"] = (48, 0.045) }, Tmt10Labels)).ToList();
        Assert.That(IsobaricReporters.Kit(pxd051031, "TMT")!.Value.Seen, Is.EquivalentTo(Tmt10Labels));

        // PXD027548: 130C unused, 129C's isotope on it at 0.047. The kit is still TMT10: 131N is TMT6's too, but 127C is not.
        var nine = Tmt10Labels.Where(l => l != "130C").ToArray();
        var pxd027548 = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, new Dictionary<string, (int, double)> { ["130C"] = (60, 0.047) }, nine)).ToList();
        var k027548 = IsobaricReporters.Kit(pxd027548, "TMT")!.Value;
        Assert.That(k027548.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT10));
        Assert.That(k027548.Seen, Is.EquivalentTo(nine));

        // PXD043296: twelve channels of TMTpro, 132C and 133N only isotopes, the rest of the kit empty.
        var twelve = Tmt10Labels.Concat(new[] { "131C", "132N" }).ToArray();
        var pxd043296 = Enumerable.Range(0, 200).Select(i => ReporterSpectrum(i, 200, new Dictionary<string, (int, double)> { ["132C"] = (44, 0.03), ["133N"] = (56, 0.03) }, twelve)).ToList();
        var k043296 = IsobaricReporters.Kit(pxd043296, "TMT")!.Value;
        Assert.That(k043296.Kit, Is.EqualTo(Omics.Modifications.IsobaricMassTagType.TMT16));
        Assert.That(k043296.Seen, Is.EquivalentTo(twelve));
    }

    // PXD035577: samples 1 to 8 in Ref_735_*, on 126 to 129N, 130N and 131N; samples 9 to 16 in Ref_735_9_16_*, on 127N to 130C. Seen across
    // the deposit, every channel holds a sample, so a list per deposit would mark nothing. Each file's own list is what a default design wants.
    [Test]
    public void EachFilesChannelsAreItsOwn()
    {
        var channels = IsobaricReporters.ChannelsByFile(new (string, IReadOnlyList<string>?)[] { ("a.raw", new[] { "126", "127N" }), ("b.raw", new[] { "127N", "127C" }) });
        Assert.That(channels!["a.raw"], Is.EqualTo(new[] { "126", "127N" }));
        Assert.That(channels["b.raw"], Is.EqualTo(new[] { "127N", "127C" }));

        Assert.That(IsobaricReporters.ChannelsByFile(new (string, IReadOnlyList<string>?)[] { ("a.raw", new[] { "126" }), ("b.raw", null) }), Is.Null, "a QC report from before v0.4.41 has no list: nothing is marked");
        Assert.That(IsobaricReporters.ChannelsByFile(Array.Empty<(string, IReadOnlyList<string>?)>()), Is.Null);
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

    // v0.4.41: reporters of an SPS-MS3 file are read in MS3, and its ion-trap MS2 cannot carry them. Counting both halves the share, so
    // two of PXD035577's real fractions failed as no_isobaric_reporters: Ref_735_fr_11, 0.178 of MS2 + MS3 and 0.356 of MS3 alone;
    // Ref_735_fr_12, 0.225 and 0.451. Ref_735_fr_10 is 0.084 and 0.167: too few by either count, and it stays a failure.
    [TestCase(36, true, TestName = "An SPS-MS3 file whose MS3 clears the reporter gate passes, though its MS2 halve the share")]
    [TestCase(16, false, TestName = "An SPS-MS3 file whose MS3 alone is under the reporter gate still fails")]
    public void AnSpsMs3FilesReporterShareIsReadInMs3Alone(int percentOfMs3WithReporters, bool passes)
    {
        double[] tmt10 = Reporters(Omics.Modifications.IsobaricMassTagType.TMT10);
        MsDataScan IonTrapMs2(int n) => new(new MzSpectrum(new[] { 300.0, 500.0, 700.0 }, new[] { 1e4, 1e4, 1e4 }, false), n, 2, true, Polarity.Positive,
            n / 10.0, new MzRange(100, 2000), "", MZAnalyzerType.IonTrap2D, 1e6, 10, null, $"scan={n}", selectedIonMz: 500, dissociationType: DissociationType.CID);
        var scans = Enumerable.Range(0, 100).SelectMany(i => new[]
        {
            IonTrapMs2(2 * i),
            i < percentOfMs3WithReporters ? Scan(2 * i + 1, 3, tmt10) : Scan(2 * i + 1, 3, 300, 500, 700),
        }).ToList();

        var qc = SpectraQc.Evaluate(scans, Gates, isobaric: true);

        if (passes)
        {
            Assert.That(qc.FailReasons, Does.Not.Contain(SpectraQc.NoReporters));
            Assert.That(qc.FailReasons, Does.Not.Contain(SpectraQc.LowResMs2), "SPS-MS3: the ion-trap MS2 is expected");
            Assert.That((qc.IsobaricKit, qc.SpsMs3), Is.EqualTo(("TMT10", (bool?)true)));
        }
        else
        {
            Assert.That(qc.FailReasons, Does.Contain(SpectraQc.NoReporters));
            Assert.That(qc.IsobaricKit, Is.Null);
        }
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
        Assert.That(tmt.IsobaricChannels, Is.EquivalentTo(Tmt10Labels));
        Assert.That(SpectraQc.Evaluate(scans, Gates).FailReasons, Does.Contain(SpectraQc.LowResMs2), "a label-free profile keeps the gate");

        var unlabelled = Enumerable.Range(0, 60).Select(i => Scan(i, 2, 300, 500, 700)).ToList();
        var none = SpectraQc.Evaluate(unlabelled, Gates, isobaric: true);
        Assert.That(none.FailReasons, Does.Contain(SpectraQc.NoReporters));
        Assert.That((none.IsobaricKit, none.SpsMs3), Is.EqualTo(((string?)null, (bool?)false)));
        Assert.That(none.IsobaricChannels, Is.Null);
    }
}
