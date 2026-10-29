using PXReprise.Config;
using PXReprise.Discovery;
using PXReprise.Search;
using UsefulProteomicsDatabases;
using CvParam = MzLibUtil.CvParam;

namespace PXReprise.Tests;

/// <summary>
/// v0.4.29 (ptmQtl 020, PXR-P18 and PXR-P19): two side reactions of a deposit's own chemistry, offered to GPTMD by
/// label-free-dda@9 and never searched variable (the user, 2026-10-25). Ring-opened NEM where NEM is the alkylant;
/// propionamide where the protocol names a gel.
/// </summary>
public class ChemistryArtifactTests
{
    private const string OpenNem = "Common Artifact\tNethylmaleimide+water on C";
    private const string Propionamide = "Common Artifact\tPropionamide on C";

    private static PrideProject Project(string protocol, string[]? ptms = null) => new()
    {
        Accession = "PXD000900", Title = "t", SampleProcessingProtocol = protocol,
        IdentifiedPTMStrings = (ptms ?? Array.Empty<string>()).Select(n => new CvParam { Name = n }).ToList(),
        QuantificationMethods = new List<CvParam>(),
    };

    private static readonly string[] Files = { "a.raw", "b.raw" };

    private static IEnumerable<string> Kinds(DepositChemistry c) => c.ArtifactsRead.Select(a => a.Kind);

    // The McDonagh three (PXR-P18): PRIDE lists light NEM, the protocol names d5-NEM. Searched with closed NEM only, the
    // ring opened by water left +86.037 Da that GPTMD placed as Hydroxybutyrylation on K.
    [Test]
    public void NemAsTheAlkylantReadsRingOpenedNem()
    {
        var c = ChemistryDetector.Detect(Project("reduced Cys were blocked with d0-NEM; reversibly oxidised Cys were labelled with d5-NEM",
            new[] { "Nethylmaleimide" }), null, null, Files);
        Assert.That(Kinds(c), Is.EqualTo(new[] { ChemistryDetector.RingOpenedNem }));
        Assert.That(c.CysMods.All(m => !m.Fixed), "the NEM pair stays as it was: variable, light and heavy");
        // The ten Jarome deposits: NEM in the lysis buffer is the alkylant (PXR-A37).
        var lysis = ChemistryDetector.Detect(Project("homogenized in denaturing lysis buffer (0.05% SDS) with 70mM NEM"), null, null, Files);
        Assert.That(Kinds(lysis), Is.EqualTo(new[] { ChemistryDetector.RingOpenedNem }));
    }

    [Test]
    public void AnOrdinaryDepositReadsNoSideReaction()
    {
        var c = ChemistryDetector.Detect(Project("Proteins were reduced with DTT, alkylated with iodoacetamide and digested with trypsin"), null, null, Files);
        Assert.That(c.ArtifactsRead, Is.Empty);
    }

    // PXR-P19: seven deposits whose protocols name a gel showed carbamidomethyl C plus methyl on K or R (+14.016 Da).
    [TestCase("Proteins were separated by SDS-PAGE and each lane cut into 10 slices")]
    [TestCase("in-gel digestion with trypsin after reduction and alkylation with iodoacetamide")]
    [TestCase("gel bands were excised and destained")]
    [TestCase("separated on a 12% polyacrylamide gel")]
    [TestCase("a short GeLC run")]
    [TestCase("35ug per tissue lysate was gel-digested and samples were injected in technical triplicate")]   // PXD006898
    public void AGelReadsPropionamide(string protocol)
    {
        var c = ChemistryDetector.Detect(Project(protocol), null, null, Files);
        Assert.That(Kinds(c), Is.EqualTo(new[] { ChemistryDetector.GelPropionamide }));
        Assert.That(c.CysMods, Is.EqualTo(new[] { new CysMod("Carbamidomethyl", "UNIMOD:4", true) }), "carbamidomethyl stays fixed");
    }

    [TestCase("proteins were purified by gel filtration")]
    [TestCase("peptides were desalted on StageTips")]
    public void NoGelReadsNoPropionamide(string protocol) =>
        Assert.That(ChemistryDetector.Detect(Project(protocol), null, null, Files).ArtifactsRead, Is.Empty);

    [Test]
    public void AnUnalkylatedDepositDropsRingOpenedNemAndKeepsTheGel()
    {
        var c = ChemistryDetector.Detect(Project("Free cysteines were blocked with NEM. Proteins were separated by SDS-PAGE"), null, null, Files);
        Assert.That(Kinds(c), Is.EquivalentTo(new[] { ChemistryDetector.RingOpenedNem, ChemistryDetector.GelPropionamide }));
        Assert.That(Kinds(c.Unalkylated("aging said so")), Is.EqualTo(new[] { ChemistryDetector.GelPropionamide }));
    }

    [Test]
    public void LabelFreeDda10IsAt9WithGptmdsImprovedScoreFilter()
    {
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var p10 = all["label-free-dda@10"];
        var p9 = all["label-free-dda@9"];
        Assert.That(p10.GptmdFilters, Is.EqualTo(new[] { "ImprovedScoreFilter" }));
        Assert.That(all.Where(kv => kv.Value.GptmdFilters is not null).Select(kv => kv.Key), Is.EquivalentTo(new[] { "label-free-dda@10", "label-free-dda@11", "label-free-dda@12" }),
            "no earlier profile changes");
        Assert.That(p10 with { Version = 9, Description = p9.Description, GptmdFilters = null, Databases = p9.Databases, Accepts = p9.Accepts,
            Tasks = p9.Tasks, GptmdExtraMods = p9.GptmdExtraMods, Deposit = p9.Deposit, Qc = p9.Qc, ThiolReagents = p9.ThiolReagents,
            ChemistryArtifacts = p9.ChemistryArtifacts }, Is.EqualTo(p9), "every other scalar setting of @10 equals @9's");
        Assert.That((p10.Tasks, p10.GptmdExtraMods, p10.Deposit, p10.Qc with { Excludable = p9.Qc.Excludable }),
            Is.EqualTo((p9.Tasks, p9.GptmdExtraMods, p9.Deposit, p9.Qc)));
        Assert.That(p10.Qc.Excludable, Is.EqualTo(p9.Qc.Excludable));
        Assert.That(p10.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(p9.Databases.Select(kv => (kv.Key, kv.Value.Proteome))));
        Assert.That(p10.ThiolReagents, Is.EquivalentTo(p9.ThiolReagents!));
        Assert.That(p10.ChemistryArtifacts, Is.EquivalentTo(p9.ChemistryArtifacts!));
    }

    [Test]
    public void LabelFreeDda12IsAt11OnTheNewMetaMorpheusAndAgingPtmV6()
    {
        // v0.4.33 (the user, 2026-10-28): @11 on MetaMorpheus's release of 2026-10-29 and agingPTM-v6, cleavage-blocking
        // modifications respected in every task, and the individual-file tables written again (#2914 turned them off).
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var p12 = all["label-free-dda@12"];
        var p11 = all["label-free-dda@11"];
        Assert.That(p12.Databases.Select(kv => (kv.Key, kv.Value.Proteome)),
            Is.EquivalentTo(p11.Databases.Select(kv => (kv.Key, kv.Value.Proteome.Replace("_agingPTM-v5.xml", "_agingPTM-v6.xml")))));
        Assert.That(p12.MetaMorpheus, Is.Not.EqualTo("1.1.12"));
        Assert.That(p12.MetaMorpheusSettings, Is.EqualTo(new[]
        {
            new MetaMorpheusSetting("all", "RespectCleavageBlockingModifications", "true"),
            new MetaMorpheusSetting("search", "WriteIndividualFiles", "true"),
        }));
        Assert.That(all.Where(kv => kv.Value.MetaMorpheusSettings is not null).Select(kv => kv.Key), Is.EquivalentTo(new[] { "label-free-dda@12", "label-free-dda-lowres@6" }));
        Assert.That(p12 with { Version = 11, Description = p11.Description, Databases = p11.Databases, Accepts = p11.Accepts,
            MetaMorpheus = p11.MetaMorpheus, MetaMorpheusSettings = null,
            Tasks = p11.Tasks, GptmdExtraMods = p11.GptmdExtraMods, GptmdFilters = p11.GptmdFilters, Deposit = p11.Deposit, Qc = p11.Qc,
            ThiolReagents = p11.ThiolReagents, ChemistryArtifacts = p11.ChemistryArtifacts }, Is.EqualTo(p11), "every other scalar setting of @12 equals @11's");
        Assert.That((p12.Tasks, p12.GptmdExtraMods, p12.GptmdFilters, p12.Deposit), Is.EqualTo((p11.Tasks, p11.GptmdExtraMods, p11.GptmdFilters, p11.Deposit)));
        Assert.That(p12.ThiolReagents, Is.EquivalentTo(p11.ThiolReagents!));
        Assert.That(p12.ChemistryArtifacts, Is.EquivalentTo(p11.ChemistryArtifacts!));
    }

    [Test]
    public void LabelFreeDda11IsAt10OnAgingPtmV5()
    {
        // v0.4.32 (the user, 2026-10-27): the same settings as @10, on the databases without GPTMD hydroxy-P/K.
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var p11 = all["label-free-dda@11"];
        var p10 = all["label-free-dda@10"];
        Assert.That(p11.Databases.Select(kv => (kv.Key, kv.Value.Proteome)),
            Is.EquivalentTo(p10.Databases.Select(kv => (kv.Key, kv.Value.Proteome.Replace("_agingPTM-v4.xml", "_agingPTM-v5.xml")))));
        Assert.That(p11.Databases.Values.Select(d => d.Proteome), Has.All.EndsWith("_agingPTM-v5.xml"));
        Assert.That(p11 with { Version = 10, Description = p10.Description, Databases = p10.Databases, Accepts = p10.Accepts,
            Tasks = p10.Tasks, GptmdExtraMods = p10.GptmdExtraMods, GptmdFilters = p10.GptmdFilters, Deposit = p10.Deposit, Qc = p10.Qc,
            ThiolReagents = p10.ThiolReagents, ChemistryArtifacts = p10.ChemistryArtifacts }, Is.EqualTo(p10), "every other scalar setting of @11 equals @10's");
        Assert.That((p11.Tasks, p11.GptmdExtraMods, p11.GptmdFilters, p11.Deposit), Is.EqualTo((p10.Tasks, p10.GptmdExtraMods, p10.GptmdFilters, p10.Deposit)));
        Assert.That(p11.Qc.Excludable, Is.EqualTo(p10.Qc.Excludable));
        Assert.That(p11.ThiolReagents, Is.EquivalentTo(p10.ThiolReagents!));
        Assert.That(p11.ChemistryArtifacts, Is.EquivalentTo(p10.ChemistryArtifacts!));
    }

    [TestCase("gptmd_filters = [\"ImprovedScore\"]", "MetaMorpheus 1.1.12 has")]
    [TestCase("gptmd_filters = [\"FragmentMassConsistencyFilter\"]", "MetaMorpheus 1.1.12 has")]
    public void AGptmdFilterMetaMorpheusDoesNotKnowIsRefused(string line, string expected)
    {
        // MetaMorpheus drops a filter name it cannot resolve without a word, so the profile refuses it at load.
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-10.toml"))
            .Replace("gptmd_filters = [\"ImprovedScoreFilter\"]", line);
        string dir = TestSupport.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "lists"));
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(dir, "lists", Path.GetFileName(f)));
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml", text)));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void LabelFreeDda9MapsBothToCommonArtifactsInItsOwnFileAndIsOtherwiseAt8()
    {
        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var p9 = all["label-free-dda@9"];
        var p8 = all["label-free-dda@8"];
        Assert.That(p9.ChemistryArtifacts, Is.EqualTo(new Dictionary<string, string>
            { [ChemistryDetector.GelPropionamide] = Propionamide, [ChemistryDetector.RingOpenedNem] = OpenNem }));
        Assert.That((p9.CustomModifications, p8.ChemistryArtifacts), Is.EqualTo(("PXReprise_custom_modifications_v2.txt", (IReadOnlyDictionary<string, string>?)null)));
        Assert.That(p9 with { Version = 8, Description = p8.Description, ChemistryArtifacts = null, CustomModifications = p8.CustomModifications,
            Databases = p8.Databases, Accepts = p8.Accepts, Tasks = p8.Tasks, GptmdExtraMods = p8.GptmdExtraMods, Deposit = p8.Deposit, Qc = p8.Qc,
            ThiolReagents = p8.ThiolReagents }, Is.EqualTo(p8), "every other scalar setting of @9 equals @8's");
        Assert.That((p9.Tasks, p9.GptmdExtraMods, p9.Deposit), Is.EqualTo((p8.Tasks, p8.GptmdExtraMods, p8.Deposit)));
        Assert.That(p9.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(p8.Databases.Select(kv => (kv.Key, kv.Value.Proteome))));
        Assert.That(p9.ThiolReagents, Is.EquivalentTo(p8.ThiolReagents!));

        // mzLib reads v2 as MetaMorpheus will, with no warning: v1's NBF and the two artifacts at UniMod's masses.
        var mods = Omics.Modifications.IO.ModificationLoader.ReadModsFromFile(p9.ListPath(p9.CustomModifications!), out var warnings)
            .ToDictionary(m => $"{m.ModificationType}\t{m.IdWithMotif}", m => m.MonoisotopicMass!.Value);
        Assert.That(warnings, Is.Empty);
        Assert.That(mods.Keys, Is.EquivalentTo(new[] { "PXReprise\tNBF on C", OpenNem, Propionamide }));
        Assert.That(mods[OpenNem], Is.EqualTo(143.058243).Within(0.00001));       // UNIMOD:320
        Assert.That(mods[Propionamide], Is.EqualTo(71.037114).Within(0.00001));   // UNIMOD:24
    }

    [Test]
    public void TheArtifactsGoToGptmdOnlyAndOnlyForTheDepositThatReadsThem()
    {
        var p9 = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@9"];
        var nem = ChemistryDetector.Detect(Project("Free cysteines were blocked with NEM", new[] { "Nethylmaleimide" }), null, null, Files);
        var plain = ChemistryDetector.Detect(Project("alkylated with iodoacetamide"), null, null, Files);
        Assert.That(SearchStage.ArtifactMods(p9, nem).Select(a => a.ModId), Is.EqualTo(new[] { OpenNem }));
        Assert.That(SearchStage.ArtifactMods(p9, plain), Is.Empty);
        var p8 = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir)["label-free-dda@8"];
        Assert.That(SearchStage.ArtifactMods(p8, nem), Is.Empty, "a profile that maps nothing searches as before");

        // The GPTMD task file takes the profile's own entry once the custom file's modifications are known; the
        // Search task's variable list is untouched.
        var known = CustomModificationFile.Known(p9);
        const string g = "ListOfModsGptmd = \"Common Biological\\tAcetylation on K\\t\\t\"\nMaxThreadsToUsePerFile = 8";
        var (text, added) = TaskFiles.Edit(g, TaskKind.Gptmd, new TaskSettings(8, false, new[] { OpenNem }), known);
        Assert.That(text, Does.Contain(@"Common Biological\tAcetylation on K\t\tCommon Artifact\tNethylmaleimide+water on C"""));
        Assert.That(added, Is.EqualTo(new[] { OpenNem }));
        Assert.That(nem.CysMods.Select(m => m.MetaMorpheusId), Has.None.Contains("water"), "never a variable or fixed cysteine modification");
    }

    [Test]
    public void AnArtifactTableNeedsDepositChemistryAndAGptmdTask()
    {
        string dir = TestSupport.TempDir();
        string lists = Path.Combine(dir, "lists");
        Directory.CreateDirectory(lists);
        foreach (string f in Directory.GetFiles(Path.Combine(TestSupport.ProfilesDir, "lists")))
            File.Copy(f, Path.Combine(lists, Path.GetFileName(f)));
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-9.toml"));
        string Error(string t) => Assert.Throws<ConfigException>(() => ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml", t)))!.Message;
        Assert.That(Error(text.Replace("tasks = [\"Calibration\", \"Gptmd\", \"Search\"]", "tasks = [\"Calibration\", \"Search\"]")), Does.Contain("no Gptmd task"));
        Assert.That(Error(text.Replace("ring_opened_nem =", "ring_closed_nem =")), Does.Contain("not a side reaction"));
        Assert.That(Error(text.Replace(@"Propionamide on C""", @"Propionamide on K""")), Does.Contain("on C"));
    }
}
