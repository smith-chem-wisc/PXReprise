using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Labels;
using PXReprise.Qc;

namespace PXReprise.Tests;

/// <summary>
/// D95, D100 (G136): the reagent-naming cysteine probe. The call's cases are the calibration's own deposits (project
/// results/cysteine-probe-calibration_2026-11-25), as counts that give the shares it measured.
/// </summary>
public class ReagentProbeTests
{
    private static readonly IReadOnlyDictionary<string, Profile> Profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);

    private static ReagentProbe.Count Counted(params (string Form, int N)[] forms) =>
        new(0, 0, 0, 0, forms.ToDictionary(f => f.Form, f => f.N, StringComparer.Ordinal), new Dictionary<string, IReadOnlyDictionary<string, int>>());

    private static string Id(string form) => ReagentProbe.Candidates.Single(c => c.Form == form).Variable.MetaMorpheusId!.Replace('\t', ':');

    private static DepositChemistry Read(string source = ChemistrySource.Default, string? park = null) => new(
        new("trypsin", ChemistrySource.Default), null, new("Carbamidomethyl", source, source == ChemistrySource.Default ? null : "alkylated with iodoacetamide"),
        [new CysMod("Carbamidomethyl", "UNIMOD:4", true)], new("label-free", ChemistrySource.Default), park);

    private static RunMeasures Run(int peptides, int withC) => new(peptides, new Dictionary<string, int> { ["Mus musculus"] = peptides }, 0, peptides, peptides, withC, 0, 0);

    public static IEnumerable<TestCaseData> Calibration()
    {
        yield return new TestCaseData(new[] { ("carbamidomethyl", 2465), ("carboxymethyl", 73), ("beta-mercaptoethanol", 69), ("unmodified", 51),
            ("NEM", 15), ("methylthio", 14), ("ethanolyl", 13), ("propionamide", 12), ("NBF", 11) }, ReagentProbe.Reagent, "carbamidomethyl", "Carbamidomethyl", true)
            .SetName("PXD031782: carbamidomethyl, carboxymethyl folded into it, 98% complete, so fixed");
        yield return new TestCaseData(new[] { ("methylthio", 442), ("unmodified", 742), ("NEM", 12), ("ethanolyl", 12), ("beta-mercaptoethanol", 12) },
            ReagentProbe.Reagent, "methylthio", "Methylthio", false).SetName("PXD018969: MMTS on 36%, 61% free, named from the modified ones, variable");
        yield return new TestCaseData(new[] { ("beta-mercaptoethanol", 472), ("carbamidomethyl", 200), ("propionamide", 98), ("unmodified", 91), ("methylthio", 40), ("NEM", 32) },
            ReagentProbe.Mixed, "beta-mercaptoethanol", null, false).SetName("PXD007999: DeStreak leads with 56% of the modified, a mixture");
        yield return new TestCaseData(new[] { ("unmodified", 760), ("ethanolyl", 65), ("beta-mercaptoethanol", 58), ("carbamidomethyl", 55), ("methylthio", 50),
            ("NEM", 50), ("propionamide", 48), ("NBF", 45), ("other: Common Biological:Hypothetical on C", 128) }, ReagentProbe.None, null, null, false)
            .SetName("PXD015079: mostly free, no reagent's form reaches 20%, none");
        yield return new TestCaseData(new[] { ("NEM", 2013), ("d5-NEM", 453), ("NEM, ring-opened", 166), ("NBF", 76), ("unmodified", 100), ("carbamidomethyl", 97) },
            ReagentProbe.Reagent, "NEM", "Nethylmaleimide + NEM:2H(5)", false).SetName("PXD001054: the NEM family, light and heavy declared, ring-opened left to GPTMD");
        yield return new TestCaseData(new[] { ("ethanolyl", 2867), ("unmodified", 371), ("methylthio", 141), ("carbamidomethyl", 200), ("NEM", 243) },
            ReagentProbe.Reagent, "ethanolyl", "Ethanolyl", false).SetName("PXD057499: iodoethanol, where the search assumed carbamidomethyl");
        yield return new TestCaseData(new[] { ("carboxymethyl", 800), ("carbamidomethyl", 50), ("unmodified", 30) },
            ReagentProbe.Reagent, "carboxymethyl", "Carboxymethyl", true).SetName("iodoacetic acid: carboxymethyl leads, so it is not folded");
        yield return new TestCaseData(new[] { ("carbamidomethyl", 60), ("unmodified", 10) }, ReagentProbe.Unknown, null, null, false)
            .SetName("70 cysteines: too few to call");
        yield return new TestCaseData(new[] { ("unmodified", 500) }, ReagentProbe.None, null, null, false).SetName("every cysteine free: none");
    }

    [TestCaseSource(nameof(Calibration))]
    public void TheCallIsTheCalibrations((string, int)[] forms, string verdict, string? family, string? declared, bool isFixed)
    {
        var call = ReagentProbe.Call(Counted(forms));
        Assert.Multiple(() =>
        {
            Assert.That(call.Verdict, Is.EqualTo(verdict), call.Evidence);
            Assert.That(call.Family, Is.EqualTo(family));
            Assert.That(call.Declare.Count == 0 ? null : string.Join(" + ", call.Declare.Select(m => m.Name)), Is.EqualTo(declared));
            Assert.That(call.Declare.All(m => m.Fixed == isFixed), Is.True, "fixed only when one form covers at least 95% of the cysteines");
            Assert.That(call.Cysteines, Is.EqualTo(forms.Sum(f => f.Item2)));
            Assert.That(ReagentProbe.ToJson(call)["verdict"]!.GetValue<string>(), Is.EqualTo(verdict));
        });
    }

    [Test]
    public void EveryCandidateResolvesToAMetaMorpheusModificationOnC() =>
        Assert.That(ReagentProbe.Candidates.Where(c => c.Variable.MetaMorpheusId is null).Select(c => c.Form), Is.Empty);

    // The count: target rows with one candidate sequence and one or two C; each C is one observation.
    [Test]
    public void TheCountTakesEachCysteinesFormFromTheTargetsWithOneOrTwoC()
    {
        string psms = ProbeSearchTests.Psms(new[]
        {
            ProbeSearchTests.Row("a", "PEPCK", full: $"PEPC[{Id("carbamidomethyl")}]K"),
            ProbeSearchTests.Row("a", "PCEPCK", full: $"PC[{Id("ethanolyl")}]EPCK"),
            ProbeSearchTests.Row("b", "PEPCK", full: "PEPC[Common Biological:Hypothetical on C]K"),
            ProbeSearchTests.Row("a", "CCCK"),
            ProbeSearchTests.Row("a", "PEPCK|PEPCR", full: "PEPCK|PEPCR"),
            ProbeSearchTests.Row("a", "PEPTIDEK"),
            ProbeSearchTests.Row("a", "DECOYCK", dct: "D"),
        });
        var c = ReagentProbe.CountForms(psms);
        Assert.Multiple(() =>
        {
            Assert.That((c.PsmsRead, c.PsmsCounted, c.Ambiguous, c.MoreThanTwoC), Is.EqualTo((6, 3, 1, 1)), "the decoy is not read");
            Assert.That(c.Forms, Is.EquivalentTo(new Dictionary<string, int>
            {
                ["carbamidomethyl"] = 1, ["ethanolyl"] = 1, [ReagentProbe.UnmodifiedForm] = 1, ["other: Common Biological:Hypothetical on C"] = 1,
            }));
            Assert.That(c.PerRun["a"], Is.EquivalentTo(new Dictionary<string, int> { ["carbamidomethyl"] = 1, ["ethanolyl"] = 1, [ReagentProbe.UnmodifiedForm] = 1 }));
            Assert.That((c.Cysteines, c.Unmodified), Is.EqualTo((4, 1)));
        });
    }

    [Test]
    public void ItIsSuspiciousUnderTenPercentPooledOverTheRuns()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReagentProbe.IsSuspicious(new Dictionary<string, RunMeasures> { ["a"] = Run(100, 4), ["b"] = Run(100, 15) }), Is.True, "9.5% pooled");
            Assert.That(ReagentProbe.IsSuspicious(new Dictionary<string, RunMeasures> { ["a"] = Run(100, 10) }), Is.False, "10% exactly");
            Assert.That(ReagentProbe.IsSuspicious(new Dictionary<string, RunMeasures> { ["a"] = Run(0, 0) }), Is.False, "no peptide: nothing to read");
            Assert.That(ReagentProbe.CysteineShare(new Dictionary<string, RunMeasures>()), Is.Null);
        });
    }

    private static string Ethanolyl(int ethanolyl = 100, int free = 10) => ProbeSearchTests.Psms(
        Enumerable.Range(0, ethanolyl).Select(i => ProbeSearchTests.Row("a", $"PEPCK{new string('A', i % 7)}", full: $"PEPC[{Id("ethanolyl")}]K{new string('A', i % 7)}"))
            .Concat(Enumerable.Range(0, free).Select(i => ProbeSearchTests.Row("a", $"PEPCR{new string('A', i % 3)}"))));

    [Test]
    public async Task ASuspiciousProbeSearchesAgainWithEveryCandidateVariableAndCallsTheReagent()
    {
        string out1 = TestSupport.TempDir();
        var seen = new List<(IReadOnlyList<string> Files, DepositChemistry? Chem, double Minutes)>();
        string psms = Ethanolyl();
        var p = Profiles["label-free-dda@16"];
        var m = Machine.Load(TestSupport.WriteFile(TestSupport.TempDir(), "m.toml", "work_root = \"F:/w\"\n[metamorpheus]\n\"1.1.16\" = \"C:/mm/CMD.exe\"\n"));
        Task<string> Search(Profile pr, Machine mm, IReadOnlyList<string> files, DepositChemistry? chem, IReadOnlyDictionary<string, FragmentationCall>? fr, string outDir, CancellationToken ct)
        {
            seen.Add((files, chem, pr.ProbeSearchMinutes));
            return Task.FromResult(psms);
        }
        var spectra = new[] { "F:/s/r1.raw", "F:/s/r2.raw" };
        var suspicious = new Dictionary<string, RunMeasures> { ["r1"] = Run(100, 3), ["r2"] = Run(100, 5) };

        var call = await ReagentProbe.RunAsync(Search, p, m, spectra, Read(), null, suspicious, out1, CancellationToken.None);
        var notRun = await ReagentProbe.RunAsync(Search, p, m, spectra, Read(), null, new Dictionary<string, RunMeasures> { ["r1"] = Run(100, 20) }, out1, CancellationToken.None);
        var noChemistry = await ReagentProbe.RunAsync(Search, p, m, spectra, null, null, suspicious, out1, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That((notRun, noChemistry), Is.EqualTo(((ReagentProbe.ReagentCall?)null, (ReagentProbe.ReagentCall?)null)));
            Assert.That(seen, Has.Count.EqualTo(1), "only the suspicious probe searches again");
            Assert.That(seen[0].Files, Is.EqualTo(spectra), "Orbitrap: every probe file");
            Assert.That(seen[0].Minutes, Is.EqualTo(ReagentProbe.Minutes));
            Assert.That(seen[0].Chem!.CysMods.Select(c => (c.Name, c.Fixed)), Is.EquivalentTo(ReagentProbe.Candidates.Select(c => (c.UnimodName, false))), "no fixed C, every candidate variable");
            Assert.That(seen[0].Chem!.Protease.Value, Is.EqualTo("trypsin"), "the deposit's own chemistry otherwise");
            Assert.That((call!.Verdict, call.Family, call.Files), Is.EqualTo((ReagentProbe.Reagent, "ethanolyl", (IReadOnlyList<string>)new[] { "r1.raw", "r2.raw" })));
            Assert.That(File.ReadAllText(Path.Combine(out1, "reagent_probe.json")), Does.Contain("\"ethanolyl\""));
        });
    }

    [Test]
    public async Task AnIonTrapDepositSearchesItsOneFileWithTheMostCysteinePeptides_AndAFailedSearchIsUnknown()
    {
        var seen = new List<IReadOnlyList<string>>();
        var p = Profiles["label-free-dda-lowres@10"];
        var m = Machine.Load(TestSupport.WriteFile(TestSupport.TempDir(), "m.toml", "work_root = \"F:/w\"\n[metamorpheus]\n\"1.1.16\" = \"C:/mm/CMD.exe\"\n"));
        var lowRes = new Dictionary<string, FragmentationCall> { ["r1.raw"] = new("LowCID", true, null, "ion trap"), ["r2.raw"] = new("LowCID", true, null, "ion trap") };
        var runs = new Dictionary<string, RunMeasures> { ["r1"] = Run(100, 2), ["r2"] = Run(100, 6) };
        var call = await ReagentProbe.RunAsync((pr, mm, files, c, fr, o, ct) => { seen.Add(files); throw new TimeoutException("the probe search did not finish"); },
            p, m, new[] { "F:/s/r1.raw", "F:/s/r2.raw" }, Read(), lowRes, runs, TestSupport.TempDir(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Single(), Is.EqualTo(new[] { "F:/s/r2.raw" }));
            Assert.That(call!.Verdict, Is.EqualTo(ReagentProbe.Unknown));
            Assert.That(call.Evidence, Does.Contain("TimeoutException").And.Contain("did not finish"));
            Assert.That(ReagentProbe.AlkylationJson(call, Read()), Is.Null, "an unknown call sets nothing");
        });
    }

    [Test]
    public void AReagentCallIsTheSearchsAlkylation_AndNoneOnlyWhereNothingWasNamed()
    {
        var reagent = ReagentProbe.Call(Counted(("ethanolyl", 300), ("unmodified", 30))) with { Files = ["r1.raw"] };
        var none = ReagentProbe.Call(Counted(("unmodified", 300), ("ethanolyl", 10))) with { Files = ["r1.raw"] };
        var mixed = ReagentProbe.Call(Counted(("ethanolyl", 150), ("NBF", 140), ("unmodified", 10)));
        var alk = ReagentProbe.AlkylationJson(reagent, Read(ChemistrySource.ProtocolText))!;
        var chem = ReagentProbe.Apply(Read(ChemistrySource.ProtocolText, park: "waiting_unknown_alkylant"), alk);
        var noneChem = ReagentProbe.Apply(Read(), ReagentProbe.AlkylationJson(none, Read()));
        Assert.Multiple(() =>
        {
            Assert.That((chem.Alkylation.Value, chem.Alkylation.Source), Is.EqualTo(("Ethanolyl", ChemistrySource.ProbeSpectra)));
            Assert.That(chem.Alkylation.Evidence, Does.Contain("r1.raw").And.Contain("the deposit's words: Carbamidomethyl [protocol_text] alkylated with iodoacetamide"));
            Assert.That(chem.CysMods.Select(c => (c.Name, c.Unimod, c.Fixed)), Is.EqualTo(new[] { ("Ethanolyl", "UNIMOD:278", false) }));
            Assert.That(chem.Park, Is.Null, "a modelled reagent was named: no wait for an unmodelled one");
            Assert.That(ReagentProbe.Apply(Read(park: "waiting_multi_protease"), alk).Park, Is.EqualTo("waiting_multi_protease"));
            Assert.That((noneChem.Alkylation.Value, noneChem.CysMods.Count), Is.EqualTo(("none", 0)), "none, where the words named nothing: no cysteine modification");
            Assert.That(ReagentProbe.AlkylationJson(none, Read(ChemistrySource.ProtocolText)), Is.Null, "a named reagent is not overruled by 'none'");
            Assert.That((mixed.Verdict, ReagentProbe.AlkylationJson(mixed, Read())), Is.EqualTo((ReagentProbe.Mixed, (System.Text.Json.Nodes.JsonObject?)null)));
            var words = Read();
            Assert.That(ReagentProbe.Apply(words, null), Is.SameAs(words), "nothing recorded: the words stand");
        });
    }

    // D95: the labels verb's cysteine verdict comes from the probe when the probe set the search's chemistry.
    [Test]
    public void TheLabelsVerdictIsTheProbesWhenItSetTheSearchsChemistry()
    {
        var c = DepositLabeller.Cysteine("PXD057499", new ChemistryFact("Ethanolyl", ChemistrySource.ProbeSpectra, "the reagent probe on r1.raw: ..."),
            [new SearchCysMod("Ethanolyl", "UNIMOD:278", false)], 0.006, []);
        Assert.Multiple(() =>
        {
            Assert.That((c.Reagent, c.Verdict), Is.EqualTo(("iodoethanol", CysteineVerdict.Matches)), "0.6% of peptides with C would read 'unknown' by the share line");
            Assert.That(c.Sources, Is.EqualTo(new[] { LabelSource.DataCheck }));
            Assert.That(c.Reason, Does.Contain("the reagent probe named iodoethanol"));
        });
    }
}
