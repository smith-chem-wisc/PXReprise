using PXReprise.Cli;
using PXReprise.Config;
using PXReprise.Discovery;

namespace PXReprise.Tests;

public class ConfigTests
{
    /// <summary>
    /// The user, 2026-11-07: "move to mm 1.1.14", TMT first (label-free waits for QuantProject on the MBR floor, G85).
    /// tmt-dda@4 is tmt-dda@3 on MetaMorpheus 1.1.14 (calibration fixed, #2939: PXD034564 calibrated 16 to 32 files, +16%
    /// PSMs; results/mm-1.1.14-trial_2026-11-06) and on agingPTM-v8, the D29 rebuild for it. Nothing else may differ: lines,
    /// not parsed fields, so a key the Profile record does not carry is compared too.
    /// </summary>
    [Test]
    public void TmtDda4_IsTmtDda3_OnMetaMorpheus1114AndAgingPtmV8()
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();

        var four = Settings("tmt-dda-4.toml");
        Assert.That(four.Where(l => l.StartsWith("metamorpheus = ")), Is.EqualTo(new[] { "metamorpheus = \"1.1.14\"" }));
        var proteomes = four.Where(l => l.StartsWith("proteome = ")).ToList();
        Assert.That(proteomes, Has.Count.EqualTo(3));
        Assert.That(proteomes, Has.All.EndWith("_agingPTM-v8.xml\""));
        Assert.That(four.Select(l => l.Replace("metamorpheus = \"1.1.14\"", "metamorpheus = \"1.1.13\"").Replace("_agingPTM-v8.xml", "_agingPTM-v7.xml")),
            Is.EqualTo(Settings("tmt-dda-3.toml")), "nothing else differs from tmt-dda@3");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (three, p4) = (all["tmt-dda@3"], all["tmt-dda@4"]);
        Assert.Multiple(() =>
        {
            Assert.That(p4.Status, Is.EqualTo(ProfileStatus.Available));
            Assert.That(p4.MetaMorpheus, Is.EqualTo("1.1.14"));
            Assert.That(three.MetaMorpheus, Is.EqualTo("1.1.13"), "a released profile never changes");
            Assert.That(p4.Description, Does.Contain("MetaMorpheus 1.1.14").And.Contain("agingPTM-v8").And.Not.Contain("1.1.13"));
        });
    }

    /// <summary>
    /// 2026-11-20 (approved by the maintainer): tmt-dda@5 is tmt-dda@4 plus MS2 fragmented by HCD and read in the ion trap,
    /// searched as LowCID. SPS-MS3 TMT methods on a Fusion, Lumos or Ascend often read the MS2 in the ion trap, and QC refused
    /// those files under @4 (unsupported_fragmentation). On one file each of two such deposits, LowCID found 9 to 14% more
    /// PSMs than HCD at the low-resolution tolerance. Nothing else may differ: lines, not parsed fields, as above.
    /// </summary>
    [Test]
    public void TmtDda5_IsTmtDda4_WithIonTrapHcdSearchedAsLowCid()
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();

        string[] added = { "ion_trap_hcd = true", "ion_trap_hcd_lowcid = true" };
        var five = Settings("tmt-dda-5.toml");
        Assert.That(five.Where(added.Contains), Is.EqualTo(added), "@5 carries the two lines once each");
        Assert.That(five.Where(l => !added.Contains(l)), Is.EqualTo(Settings("tmt-dda-4.toml")), "nothing else differs from tmt-dda@4");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (p4, p5) = (all["tmt-dda@4"], all["tmt-dda@5"]);
        Assert.Multiple(() =>
        {
            Assert.That(p5.Status, Is.EqualTo(ProfileStatus.Available));
            Assert.That((p5.Fragmentation, p5.IonTrapHcd, p5.IonTrapHcdLowCid, p5.IonTrapHcdProbeLowCid), Is.EqualTo(("per_file", true, true, false)));
            Assert.That((p4.IonTrapHcd, p4.IonTrapHcdLowCid), Is.EqualTo((false, false)), "a released profile never changes");
            Assert.That((p5.MetaMorpheus, p5.SpectralLibrary, p5.LowResProductTolerance), Is.EqualTo((p4.MetaMorpheus, p4.SpectralLibrary, p4.LowResProductTolerance)));
            Assert.That(p5.Description, Does.Contain("LowCID for ion-trap MS2, CID or HCD"));
            Assert.That(p5.Description.Replace("LowCID for ion-trap MS2, CID or HCD", "LowCID for ion-trap MS2"), Is.EqualTo(p4.Description));
        });
    }

    /// <summary>
    /// D84 (2026-11-21): label-free-dda@15, label-free-dda-lowres@9 and tmt-dda@6 are @14, @8 and @5 on MetaMorpheus 1.1.15,
    /// with the identification filter at 1% set in every task (D82) and the design's SDRF passed to the search, and
    /// (2026-11-22) the QC's MS2 floor lowered from 5000 to 2500. The filter is the q-value notch in all three, not 1.1.15's
    /// default PEP q-value (D85, D87: under the PEP filter MetaMorpheus 1.1.15's protein FDR can collapse, depending on the
    /// deposit). The databases are agingPTM-v9, except lowres@9's, which stay v3 (v9 lost to v3 on an ion-trap deposit,
    /// 2026-11-22). The Search task writes no per-file mzIdentML (WriteMzId = false; the user, 2026-11-22: "we don't need
    /// any per file outputs"). The two label-free versions keep match-between-runs transfers below a q-value of 5%, not
    /// 1% (MbrFdrThreshold = 0.05; the user, 2026-11-23); TMT runs no match-between-runs. Nothing else may differ: lines,
    /// not parsed fields, as above.
    /// </summary>
    [TestCase("label-free-dda-15.toml", "label-free-dda-14.toml", "label-free-dda@15", new[] { "Calibration", "Gptmd", "Search" }, "v9", true)]
    [TestCase("label-free-dda-lowres-9.toml", "label-free-dda-lowres-8.toml", "label-free-dda-lowres@9", new[] { "Search" }, "v3", true)]
    [TestCase("tmt-dda-6.toml", "tmt-dda-5.toml", "tmt-dda@6", new[] { "Calibration", "Search" }, "v9", false)]
    public void TheMetaMorpheus1115VersionsChangeOnlyTheReleaseTheFilterTheInputSdrfTheDatabasesTheMs2FloorTheMzIdAndTheMbrThreshold(string next, string previous, string key, string[] tasks,
        string db, bool mbrAtFivePercent)
    {
        const string filter = "QValueNotch";
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();
        string Moved(string l)
        {
            string m = System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(l, @"^metamorpheus = "".*""$", @"metamorpheus = ""1.1.15"""),
                @"_agingPTM-v\d+\.xml""$", $@"_agingPTM-{db}.xml""");
            return m == "min_ms2 = 5000" ? "min_ms2 = 2500" : m;
        }

        string[] added = new[] { "input_sdrf = true", $"IdentificationFilter = \"{filter}\"", "FilterThreshold = 0.01", "WriteMzId = false" }
            .Concat(mbrAtFivePercent ? new[] { "MbrFdrThreshold = 0.05" } : Array.Empty<string>()).ToArray();
        var lines = Settings(next);
        Assert.That(lines.Where(added.Contains), Is.EqualTo(added), "the added lines once each");
        Assert.That(lines.Where(l => !added.Contains(l)), Is.EqualTo(Settings(previous).Select(Moved)), "nothing else differs but the release, the databases and the MS2 floor");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (p, old) = (all[key], all[key[..key.IndexOf('@')] + "@" + (int.Parse(key[(key.IndexOf('@') + 1)..]) - 1)]);
        Assert.Multiple(() =>
        {
            Assert.That((p.MetaMorpheus, p.InputSdrf, p.WriteSdrf), Is.EqualTo(("1.1.15", true, true)));
            Assert.That(old.InputSdrf, Is.False, "a released profile never changes");
            Assert.That((p.Qc.MinMs2, old.Qc.MinMs2), Is.EqualTo((2500, 5000)), "the MS2 floor, lowered in the new version only");
            Assert.That(p.Tasks, Is.EqualTo(tasks));
            Assert.That(p.MetaMorpheusSettings!.Where(s => s.Key is "IdentificationFilter" or "FilterThreshold").Select(s => (s.Scope, s.Key, s.TomlValue)),
                Is.EqualTo(new[] { ("all", "IdentificationFilter", $"\"{filter}\""), ("all", "FilterThreshold", "0.01") }), "in every task");
            Assert.That(p.MetaMorpheusSettings!.Where(s => s.Key == "WriteMzId").Select(s => (s.Scope, s.TomlValue)),
                Is.EqualTo(new[] { ("search", "false") }), "no per-file mzIdentML, in the Search task");
            Assert.That(old.MetaMorpheusSettings!.Any(s => s.Key == "WriteMzId"), Is.False, "a released profile never changes");
            Assert.That((p.MbrFdrThreshold, old.MbrFdrThreshold), Is.EqualTo((mbrAtFivePercent ? 0.05 : 0.01, 0.01)),
                "the transfer threshold our counts use, as the Search task runs it");
            Assert.That(p.Databases.Values.Select(d => d.Proteome), Has.All.EndsWith($"_agingPTM-{db}.xml"));
            Assert.That(p.Description, Does.EndWith("; the q-value notch at 1% in every task; the curated SDRF passed to the search"));
        });
    }

    /// <summary>
    /// 2026-11-24: label-free-dda@16 and tmt-dda@7 are @15 and @6 on agingPTM-v10 (UniProt 2026_04, cumulative, decision
    /// 0091), and tmt-dda@7 and label-free-dda-lowres@10 set engine.ion_trap_library (decision 0092: ion-trap MS2 reads its
    /// own chain, which PXReprise builds from the search's PSMs); lowres@10 also turns its spectral library on, and keeps
    /// agingPTM-v3 (D44). Nothing else may differ: lines, not parsed fields, as above.
    /// </summary>
    [TestCase("label-free-dda-16.toml", "label-free-dda-15.toml", "label-free-dda@16", true, false, "")]
    [TestCase("tmt-dda-7.toml", "tmt-dda-6.toml", "tmt-dda@7", true, true, "")]
    [TestCase("label-free-dda-lowres-10.toml", "label-free-dda-lowres-9.toml", "label-free-dda-lowres@10", false, true, "spectral_library = false")]
    public void TheVersionsOfDecisions0091And0092ChangeOnlyTheDatabasesAndTheIonTrapLibrary(string next, string previous, string key,
        bool v10, bool ionTrapLibrary, string libraryWas)
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();
        string Moved(string l) =>
            l == libraryWas ? "spectral_library = true"
            : v10 ? l.Replace("_all_genecentric_2026_03_agingPTM-v9.xml\"", "_all_genecentric_2026_04_agingPTM-v10.xml\"") : l;

        var added = ionTrapLibrary ? new[] { "ion_trap_library = true" } : Array.Empty<string>();
        var lines = Settings(next);
        Assert.That(lines.Where(added.Contains), Is.EqualTo(added), "the added line once");
        Assert.That(lines.Where(l => !added.Contains(l)), Is.EqualTo(Settings(previous).Select(Moved)), "nothing else differs");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (p, old) = (all[key], all[key[..key.IndexOf('@')] + "@" + (int.Parse(key[(key.IndexOf('@') + 1)..]) - 1)]);
        Assert.Multiple(() =>
        {
            Assert.That((p.SpectralLibrary, p.IonTrapLibrary), Is.EqualTo((true, ionTrapLibrary)));
            Assert.That(old.IonTrapLibrary, Is.False, "a released profile never changes");
            Assert.That(p.Databases.Values.Select(d => d.Proteome),
                Has.All.EndsWith(v10 ? "_2026_04_agingPTM-v10.xml" : "_2026_03_agingPTM-v3.xml"));
            Assert.That(p.MetaMorpheus, Is.EqualTo(old.MetaMorpheus));
        });
    }

    /// <summary>
    /// 2026-11-25 (the user, 2026-11-24: "This next version will be the one used for pxreprise"): label-free-dda@17,
    /// label-free-dda-lowres@11 and tmt-dda@8 are @16, @10 and @7 on MetaMorpheus 1.1.16, PXReprise 1.0.0's, and (D99,
    /// the user, 2026-11-26: "yes. move to the pep filter") with the PEP q-value as the identification filter in place
    /// of the q-value notch. Those two lines are the only settings that may differ: lines, not parsed fields, as above.
    /// The release must be in releases.tsv, so the machine file can name a zip whose sha256 is known.
    /// </summary>
    [TestCase("label-free-dda-17.toml", "label-free-dda-16.toml", "label-free-dda@17")]
    [TestCase("label-free-dda-lowres-11.toml", "label-free-dda-lowres-10.toml", "label-free-dda-lowres@11")]
    [TestCase("tmt-dda-8.toml", "tmt-dda-7.toml", "tmt-dda@8")]
    public void TheMetaMorpheus1116VersionsChangeOnlyTheReleaseAndTheFilter(string next, string previous, string key)
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();
        static string Moved(string l) => l switch
        {
            "metamorpheus = \"1.1.15\"" => "metamorpheus = \"1.1.16\"",
            "IdentificationFilter = \"QValueNotch\"" => "IdentificationFilter = \"PepQValue\"",
            _ => l,
        };

        var lines = Settings(next);
        Assert.That(lines.Count(l => l == "metamorpheus = \"1.1.16\""), Is.EqualTo(1), "the release, once");
        Assert.That(lines.Count(l => l.StartsWith("IdentificationFilter = ")), Is.EqualTo(1), "one filter, for every task");
        Assert.That(lines, Does.Contain("IdentificationFilter = \"PepQValue\""), "the PEP q-value (D99)");
        Assert.That(Settings(previous), Does.Contain("IdentificationFilter = \"QValueNotch\""), "the version before took the notch (D87)");
        Assert.That(lines, Is.EqualTo(Settings(previous).Select(Moved)), "nothing else differs");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (p, old) = (all[key], all[key[..key.IndexOf('@')] + "@" + (int.Parse(key[(key.IndexOf('@') + 1)..]) - 1)]);
        var releases = File.ReadAllLines(Path.Combine(LibraryFromPsmsTests.RepoRoot(), "tests", "metamorpheus-output", "releases.tsv"))
            .Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0], c => c);
        Assert.Multiple(() =>
        {
            Assert.That((p.MetaMorpheus, old.MetaMorpheus), Is.EqualTo(("1.1.16", "1.1.15")), "a released profile never changes");
            Assert.That(releases.ContainsKey("1.1.16"), "releases.tsv lists 1.1.16");
            Assert.That(releases.GetValueOrDefault("1.1.16")?[2], Does.Match("^[0-9a-f]{64}$"), "with its CommandLine zip's sha256");
        });
    }

    /// <summary>
    /// 2026-11-08 (the user: "lets do the custom modifications now"): label-free-dda-lowres@8 is lowres@7 with the custom
    /// modification file and the NBF-Cl mapping that label-free-dda@14 ships (G40), so an ion-trap deposit whose cysteines
    /// carry NBF (PXD048079, PXD045806) can be searched. An ion-trap profile runs no GPTMD, so @14's [chemistry_artifacts],
    /// which go only to GPTMD, stay out. Lines, not parsed fields, so a key the Profile record does not carry is compared too.
    /// </summary>
    [Test]
    public void Lowres8_IsLowres7_WithTheCustomModificationsOfLabelFreeDda14()
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description) = "))
            .ToList();

        string[] custom = { "custom_modifications = \"PXReprise_custom_modifications_v2.txt\"", "[thiol_reagents]", "\"NBF-Cl\" = \"PXReprise\\tNBF on C\"" };
        var eight = Settings("label-free-dda-lowres-8.toml");
        Assert.That(eight.Where(custom.Contains), Is.EqualTo(custom), "lowres@8 carries the three lines once each");
        Assert.That(eight.Where(l => !custom.Contains(l)), Is.EqualTo(Settings("label-free-dda-lowres-7.toml")), "nothing else differs from lowres@7");
        Assert.That(custom, Is.SubsetOf(Settings("label-free-dda-14.toml")), "the same lines as label-free-dda@14");

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (seven, p8) = (all["label-free-dda-lowres@7"], all["label-free-dda-lowres@8"]);
        Assert.Multiple(() =>
        {
            Assert.That(p8.Status, Is.EqualTo(ProfileStatus.Available));
            Assert.That(p8.ChemistryArtifacts, Is.Null, "GPTMD-only artifacts have no task to go to");
            Assert.That(seven.CustomModifications, Is.Null, "a released profile never changes");
            Assert.That(p8.Description, Is.EqualTo(seven.Description + "; NBF-Cl modelled by a shipped modification (G40)"));
        });
    }

    /// <summary>
    /// Issue #4: label-free-dda-uniprot@1 is today's method on databases the engine downloads. The promise is that nothing else
    /// differs from the profile it was made from, so a setting added to neither by hand can drift. Lines, not parsed fields,
    /// so a key the Profile record does not carry is compared too.
    /// </summary>
    [Test]
    public void TheUniProtProfileDiffersFromLabelFreeDda14OnlyInItsDatabases()
    {
        static List<string> Settings(string name) => File.ReadAllLines(Path.Combine(TestSupport.ProfilesDir, name))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+#.*$", "").TrimEnd())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(id|version|description|proteome|uniprot) = "))
            .ToList();

        Assert.That(Settings("label-free-dda-uniprot-1.toml"), Is.EqualTo(Settings("label-free-dda-14.toml")));

        var all = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (current, uniprot) = (all["label-free-dda@14"], all["label-free-dda-uniprot@1"]);
        Assert.Multiple(() =>
        {
            Assert.That(uniprot.Status, Is.EqualTo(current.Status));
            Assert.That(uniprot.Databases.Keys, Is.EquivalentTo(current.Databases.Keys));
            foreach (var (organism, proteome) in new[] { ("human", "UP000005640"), ("mouse", "UP000000589"), ("rat", "UP000002494") })
            {
                Assert.That(uniprot.Databases[organism].UniProt, Is.EqualTo(proteome), organism);
                Assert.That(uniprot.Databases[organism].Proteome, Is.Null, $"{organism} must not name a file nobody else has");
                Assert.That(uniprot.Databases[organism].Taxon, Is.EqualTo(current.Databases[organism].Taxon), organism);
            }
        });
    }

    [Test]
    public void ShippedProfilesLoadWithTheAgingV1Settings()
    {
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        Assert.That(profiles.Keys, Is.EquivalentTo(new[] { "label-free-dda@1", "label-free-dda@2", "label-free-dda@3", "label-free-dda@4",
            "label-free-dda@5", "label-free-dda@6", "label-free-dda@7", "label-free-dda@8", "label-free-dda@9", "label-free-dda@10", "label-free-dda@11", "label-free-dda@12", "label-free-dda@13", "label-free-dda@14", "label-free-dda@15", "label-free-dda@16", "label-free-dda@17", "label-free-dda-uniprot@1", "label-free-dda-lowres@1", "label-free-dda-lowres@2",
            "label-free-dda-lowres@3", "label-free-dda-lowres@4", "label-free-dda-lowres@5", "label-free-dda-lowres@6", "label-free-dda-lowres@7", "label-free-dda-lowres@8", "label-free-dda-lowres@9", "label-free-dda-lowres@10", "label-free-dda-lowres@11", "tmt-dda@1", "tmt-dda@2", "tmt-dda@3", "tmt-dda@4", "tmt-dda@5", "tmt-dda@6", "tmt-dda@7", "tmt-dda@8" }));

        // D44 (2026-10-17): @8 is @7 on agingPTM-v4; nothing else. No lowres version on v4: it cost an ion-trap run 4% of its PSMs.
        foreach (var (newer, older) in new[] { ("label-free-dda@8", "label-free-dda@7") })
        {
            var n = profiles[newer];
            var o = profiles[older];
            Assert.That(n.Databases.Values.Select(d => d.Proteome), Is.All.EndWith("_all_genecentric_2026_03_agingPTM-v4.xml"), newer);
            Assert.That(n.Databases.Select(kv => (kv.Key, kv.Value.Proteome!.Replace("agingPTM-v4", "agingPTM-v3"), kv.Value.Taxon, kv.Value.UniProt, string.Join(";", kv.Value.Extra))),
                Is.EquivalentTo(o.Databases.Select(kv => (kv.Key, kv.Value.Proteome!, kv.Value.Taxon, kv.Value.UniProt, string.Join(";", kv.Value.Extra)))),
                $"{newer}: the same proteomes, one tag on");
            Assert.That(n with { Version = o.Version, Description = o.Description, Databases = o.Databases, Accepts = o.Accepts, Tasks = o.Tasks,
                GptmdExtraMods = o.GptmdExtraMods, Deposit = o.Deposit, Qc = o.Qc, ThiolReagents = o.ThiolReagents },
                Is.EqualTo(o), $"every other scalar setting of {newer} equals {older}'s");
            Assert.That((n.Tasks, n.GptmdExtraMods, n.Deposit, n.Qc with { Excludable = o.Qc.Excludable }), Is.EqualTo((o.Tasks, o.GptmdExtraMods, o.Deposit, o.Qc)), newer);
            Assert.That(n.Qc.Excludable, Is.EqualTo(o.Qc.Excludable), newer);
            Assert.That(n.ThiolReagents ?? new Dictionary<string, string>(), Is.EquivalentTo(o.ThiolReagents ?? new Dictionary<string, string>()), newer);
        }

        // PXD045821 (2026-10-17): lowres@3 is lowres@2 plus the probe search and the windows it measures; nothing else.
        var low2x = profiles["label-free-dda-lowres@2"];
        var low3 = profiles["label-free-dda-lowres@3"];
        Assert.That((low3.ProbeSearch, low3.PrecursorWindow, low2x.ProbeSearch, low2x.PrecursorWindow), Is.EqualTo((true, "probe", false, "default")));
        Assert.That(low3 with { Version = 2, ProbeSearch = false, PrecursorWindow = "default", Description = low2x.Description, Databases = low2x.Databases,
            Accepts = low2x.Accepts, Tasks = low2x.Tasks, GptmdExtraMods = low2x.GptmdExtraMods, Deposit = low2x.Deposit, Qc = low2x.Qc },
            Is.EqualTo(low2x), "every other scalar setting equals lowres@2's");
        Assert.That((low3.Tasks, low3.Deposit, low3.Qc with { Excludable = low2x.Qc.Excludable }), Is.EqualTo((low2x.Tasks, low2x.Deposit, low2x.Qc)));
        Assert.That(low3.Qc.Excludable, Is.EqualTo(low2x.Qc.Excludable));
        Assert.That(low3.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(low2x.Databases.Select(kv => (kv.Key, kv.Value.Proteome))),
            "no database change, so no D29 rebuild");

        // v0.4.28 (PXD022393, PXD016430): lowres@4 is lowres@3 with ion-trap HCD searched as LowCID; nothing else.
        var low4 = profiles["label-free-dda-lowres@4"];
        Assert.That((low4.IonTrapHcd, low4.IonTrapHcdLowCid, low3.IonTrapHcdLowCid), Is.EqualTo((true, true, false)));
        Assert.That(low4 with { Version = 3, IonTrapHcdLowCid = false, Description = low3.Description, Databases = low3.Databases,
            Accepts = low3.Accepts, Tasks = low3.Tasks, GptmdExtraMods = low3.GptmdExtraMods, Deposit = low3.Deposit, Qc = low3.Qc },
            Is.EqualTo(low3), "every other scalar setting equals lowres@3's");
        Assert.That((low4.Tasks, low4.Deposit, low4.Qc with { Excludable = low3.Qc.Excludable }), Is.EqualTo((low3.Tasks, low3.Deposit, low3.Qc)));
        Assert.That(low4.Qc.Excludable, Is.EqualTo(low3.Qc.Excludable));
        Assert.That(low4.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(low3.Databases.Select(kv => (kv.Key, kv.Value.Proteome))),
            "no database change, so no D29 rebuild");

        // v0.4.29 (2026-10-21): lowres@5 is lowres@3 with ion-trap HCD as LowCID in the probe search only; nothing else.
        // lowres@4's LowCID full searches lost 1-16% of PSMs.
        var low5 = profiles["label-free-dda-lowres@5"];
        Assert.That((low5.IonTrapHcd, low5.IonTrapHcdProbeLowCid, low5.IonTrapHcdLowCid, low4.IonTrapHcdProbeLowCid, low3.IonTrapHcdProbeLowCid),
            Is.EqualTo((true, true, false, false, false)));
        Assert.That(low5 with { Version = 3, IonTrapHcdProbeLowCid = false, Description = low3.Description, Databases = low3.Databases,
            Accepts = low3.Accepts, Tasks = low3.Tasks, GptmdExtraMods = low3.GptmdExtraMods, Deposit = low3.Deposit, Qc = low3.Qc },
            Is.EqualTo(low3), "every other scalar setting equals lowres@3's");
        Assert.That((low5.Tasks, low5.Deposit, low5.Qc with { Excludable = low3.Qc.Excludable }), Is.EqualTo((low3.Tasks, low3.Deposit, low3.Qc)));
        Assert.That(low5.Qc.Excludable, Is.EqualTo(low3.Qc.Excludable));
        Assert.That(low5.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(low3.Databases.Select(kv => (kv.Key, kv.Value.Proteome))),
            "no database change, so no D29 rebuild");

        // The user 2026-10-12 (aging 089 PXR-A94, ptmQtl 018 PXR-P17): @7 is @6 with the probe search on; nothing else.
        var v6x = profiles["label-free-dda@6"];
        var v7 = profiles["label-free-dda@7"];
        Assert.That((v6x.ProbeSearch, v7.ProbeSearch), Is.EqualTo((false, true)));
        Assert.That(v7 with { Version = 6, ProbeSearch = false, Description = v6x.Description, Databases = v6x.Databases, Accepts = v6x.Accepts,
            Tasks = v6x.Tasks, GptmdExtraMods = v6x.GptmdExtraMods, Deposit = v6x.Deposit, Qc = v6x.Qc, ThiolReagents = v6x.ThiolReagents },
            Is.EqualTo(v6x), "every other scalar setting equals /6's");
        Assert.That((v7.Tasks, v7.GptmdExtraMods, v7.Deposit, v7.Qc with { Excludable = v6x.Qc.Excludable }), Is.EqualTo((v6x.Tasks, v6x.GptmdExtraMods, v6x.Deposit, v6x.Qc)));
        Assert.That(v7.Qc.Excludable, Is.EqualTo(v6x.Qc.Excludable));
        Assert.That(v7.ThiolReagents, Is.EquivalentTo(v6x.ThiolReagents!));
        Assert.That(v7.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(v6x.Databases.Select(kv => (kv.Key, kv.Value.Proteome))),
            "no database change, so no D29 rebuild");

        // G40: @6 is @5 plus a shipped modification file and NBF-Cl mapped to it; nothing else.
        var v5b = profiles["label-free-dda@5"];
        var v6 = profiles["label-free-dda@6"];
        Assert.That((v5b.CustomModifications, v5b.ThiolReagents), Is.EqualTo(((string?)null, (IReadOnlyDictionary<string, string>?)null)));
        Assert.That(v6 with { Version = 5, CustomModifications = null, ThiolReagents = null, Description = v5b.Description, Databases = v5b.Databases,
            Accepts = v5b.Accepts, Tasks = v5b.Tasks, GptmdExtraMods = v5b.GptmdExtraMods, Deposit = v5b.Deposit, Qc = v5b.Qc },
            Is.EqualTo(v5b), "every other scalar setting equals /5's");
        Assert.That((v6.Tasks, v6.GptmdExtraMods, v6.Deposit, v6.Qc with { Excludable = v5b.Qc.Excludable }), Is.EqualTo((v5b.Tasks, v5b.GptmdExtraMods, v5b.Deposit, v5b.Qc)));
        Assert.That(v6.Qc.Excludable, Is.EqualTo(v5b.Qc.Excludable));
        Assert.That(v6.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(v5b.Databases.Select(kv => (kv.Key, kv.Value.Proteome))),
            "no database change, so no D29 rebuild");

        // PXR-A68: lowres@2 is lowres@1 plus ion-trap HCD, and nothing else; @1 keeps refusing it.
        var low1 = profiles["label-free-dda-lowres@1"];
        var low2 = profiles["label-free-dda-lowres@2"];
        Assert.That((low1.IonTrapHcd, low2.IonTrapHcd), Is.EqualTo((false, true)));
        Assert.That(low2 with { Version = 1, IonTrapHcd = false, Description = low1.Description, Databases = low1.Databases,
            Accepts = low1.Accepts, Tasks = low1.Tasks, GptmdExtraMods = low1.GptmdExtraMods, Deposit = low1.Deposit, Qc = low1.Qc },
            Is.EqualTo(low1), "every other scalar setting equals lowres@1's");
        Assert.That((low2.Tasks, low2.Deposit, low2.Qc with { Excludable = low1.Qc.Excludable }), Is.EqualTo((low1.Tasks, low1.Deposit, low1.Qc)));
        Assert.That(low2.Databases.Select(kv => (kv.Key, kv.Value.Proteome)), Is.EquivalentTo(low1.Databases.Select(kv => (kv.Key, kv.Value.Proteome))));

        // D38: @5 is @4 on UniProt's canonical proteomes (agingPTM-v3), with the deposit caps raised (120 files, 3M MS2).
        var v4a = profiles["label-free-dda@4"];
        var v5 = profiles["label-free-dda@5"];
        Assert.That(v5.Databases.Values.Select(d => d.Proteome), Is.All.EndWith("_all_genecentric_2026_03_agingPTM-v3.xml"));
        Assert.That((v5.Deposit.MaxFiles, v5.Deposit.MaxEstimatedMs2), Is.EqualTo((120, 3_000_000)));
        Assert.That(v5 with { Version = 4, Description = v4a.Description, Databases = v4a.Databases, Deposit = v4a.Deposit,
            Accepts = v4a.Accepts, Tasks = v4a.Tasks, GptmdExtraMods = v4a.GptmdExtraMods, Qc = v4a.Qc, SourceDir = v4a.SourceDir },
            Is.EqualTo(v4a), "every other scalar setting equals /4's");
        Assert.That((v5.Deposit with { MaxFiles = 60, MaxEstimatedMs2 = 1_500_000 }, v5.Qc with { Excludable = v4a.Qc.Excludable }), Is.EqualTo((v4a.Deposit, v4a.Qc)));
        Assert.That(v5.Tasks, Is.EqualTo(v4a.Tasks));

        // D28: @4 is @3 on MetaMorpheus 1.1.12 with an SDRF design, the search's own SDRF and the agingPTM-v2 databases;
        // D35: and MetaMorpheus's NoUPS contaminant panel as it is, in place of /3's panel minus aging's D54 list.
        var v3x = profiles["label-free-dda@3"];
        var v4 = profiles["label-free-dda@4"];
        Assert.That((v4.MetaMorpheus, v4.Design, v4.WriteSdrf, v4.Chemistry), Is.EqualTo(("1.1.12", "sdrf", true, "deposit")));
        Assert.That(v4.Databases.Values.Select(d => d.Proteome), Is.All.EndWith("_agingPTM-v2.xml"));
        Assert.That((v4.ContaminantFile, v4.ContaminantExclude), Is.EqualTo(("MetaMorpheusContaminants_NoUPS.xml", (string?)null)));
        Assert.That(v4 with { Version = 3, MetaMorpheus = "1.1.11", Design = "none", WriteSdrf = false, TimeoutHours = v3x.TimeoutHours,
            ContaminantPanel = v3x.ContaminantPanel, ContaminantExclude = v3x.ContaminantExclude, ContaminantFile = v3x.ContaminantFile,
            Description = v3x.Description, Databases = v3x.Databases, Accepts = v3x.Accepts, Tasks = v3x.Tasks,
            GptmdExtraMods = v3x.GptmdExtraMods, Deposit = v3x.Deposit, Qc = v3x.Qc, SourceDir = v3x.SourceDir },
            Is.EqualTo(v3x), "every other scalar setting equals /3's");
        Assert.That((v4.Deposit, v4.Qc with { Excludable = v3x.Qc.Excludable }, v4.Tasks), Is.EqualTo((v3x.Deposit, v3x.Qc, v3x.Tasks)));

        // G19: @3 is @1 with the deposit's own chemistry, and nothing else; @1 keeps MetaMorpheus's defaults.
        var v1 = profiles["label-free-dda@1"];
        var v3 = profiles["label-free-dda@3"];
        Assert.That((v1.Chemistry, v3.Chemistry), Is.EqualTo(("fixed", "deposit")));
        Assert.That(v3 with { Version = 1, Chemistry = "fixed", Description = v1.Description, Databases = v1.Databases, Accepts = v1.Accepts,
            Tasks = v1.Tasks, GptmdExtraMods = v1.GptmdExtraMods, Deposit = v1.Deposit, Qc = v1.Qc },
            Is.EqualTo(v1), "every scalar setting but chemistry equals /1's");
        Assert.That(v3.Databases.Keys, Is.EquivalentTo(v1.Databases.Keys));
        Assert.That(v3.Tasks, Is.EqualTo(v1.Tasks));
        Assert.That(v3.Deposit, Is.EqualTo(v1.Deposit));
        Assert.That(v3.Qc with { Excludable = v1.Qc.Excludable }, Is.EqualTo(v1.Qc));

        var lf = profiles["label-free-dda@1"];
        Assert.Multiple(() =>
        {
            Assert.That(lf.Status, Is.EqualTo(ProfileStatus.Available));
            Assert.That(lf.MetaMorpheus, Is.EqualTo("1.1.11"));
            Assert.That(lf.Tasks, Is.EqualTo(new[] { "Calibration", "Gptmd", "Search" }));
            Assert.That(lf.GptmdExtraMods, Is.EqualTo(new[] { "Trypsin Digested\tGG (Ubiquitination Site) on K" }));
            Assert.That(lf.MatchBetweenRuns, Is.True);
            Assert.That(lf.Deposit.MaxFiles, Is.EqualTo(60));
            Assert.That(lf.Deposit.MaxEstimatedMs2, Is.EqualTo(1_500_000));
            Assert.That(lf.Qc.MinMs2, Is.EqualTo(5000));
            Assert.That(lf.Qc.MinFractionOrbitrapHcd, Is.EqualTo(0.9));
            Assert.That(lf.Databases.Keys, Is.EquivalentTo(new[] { "human", "mouse", "rat" }));
            Assert.That(lf.Databases["human"].Proteome, Does.EndWith("_agingPTM-v1.xml"));
            Assert.That(lf.Accepts.Instruments, Does.Not.Contain(InstrumentClass.Astral));
        });
        Assert.That(profiles["tmt-dda@1"].Status, Is.EqualTo(ProfileStatus.Pending));
        // v0.4.35: datarepo 1.4.1 ingests isobaric searches (dataRepo 078), so tmt-dda@2 routes.
        Assert.That(profiles["tmt-dda@2"].Status, Is.EqualTo(ProfileStatus.Available));
    }

    [Test]
    public void TheGenericProfileIsV1WithUniProtDatabases()
    {
        var profiles = ProfileLoader.LoadDirectory(TestSupport.ProfilesDir);
        var (v1, v2) = (profiles["label-free-dda@1"], profiles["label-free-dda@2"]);
        Assert.Multiple(() =>
        {
            Assert.That(v2.Databases["human"].UniProt, Is.EqualTo("UP000005640"));
            Assert.That(v2.Databases.Values.All(d => d.Proteome is null), Is.True);
            // Everything but the databases is /1's, so the two versions' results stay comparable.
            Assert.That(SameSearch(v1, v2), Is.True);
        });
    }

    private static bool SameSearch(Profile a, Profile b) =>
        a.MetaMorpheus == b.MetaMorpheus && a.Tasks.SequenceEqual(b.Tasks) && a.GptmdExtraMods.SequenceEqual(b.GptmdExtraMods)
        && a.SpectralLibrary == b.SpectralLibrary && a.TimeoutHours == b.TimeoutHours && a.ContaminantPanel == b.ContaminantPanel
        && a.ContaminantExclude == b.ContaminantExclude && a.ContaminantFile == b.ContaminantFile && a.QuantMethod == b.QuantMethod && a.MatchBetweenRuns == b.MatchBetweenRuns
        && a.Deposit == b.Deposit && a.Qc.Ms2Analyzer == b.Qc.Ms2Analyzer && a.Qc.MinMs2 == b.Qc.MinMs2
        && a.Qc.MinFractionOrbitrapHcd == b.Qc.MinFractionOrbitrapHcd && a.Qc.Excludable.SequenceEqual(b.Qc.Excludable)
        && a.Qc.MaxExcludedFraction == b.Qc.MaxExcludedFraction && a.Accepts.Modes.SequenceEqual(b.Accepts.Modes)
        && a.Accepts.Labellings.SequenceEqual(b.Accepts.Labellings) && a.Accepts.Instruments.SequenceEqual(b.Accepts.Instruments);

    [TestCase("proteome = \"a.xml\"\nuniprot = \"UP000005640\"", "exactly one of")]
    [TestCase("taxon = 9606", "exactly one of")]
    [TestCase("uniprot = \"human\"", "UniProt proteome ID")]
    public void AnOrganismDatabaseNamesExactlyOneValidSource(string table, string expected)
    {
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-2.toml")).ReplaceLineEndings("\n")
            .Replace("uniprot = \"UP000005640\"\ntaxon = 9606", table);
        string dir = TestSupport.TempDir();
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(TestSupport.WriteFile(dir, "p.toml", text)));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void AMisspeltProfileKeyIsRefusedNotIgnored()
    {
        string dir = TestSupport.TempDir();
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-1.toml"))
            .Replace("mbr = true", "mbr = true\nmatch_between_run = true");
        TestSupport.WriteFile(dir, "p.toml", text);
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(Path.Combine(dir, "p.toml")));
        Assert.That(e!.Message, Does.Contain("'quant.match_between_run'"));
    }

    [Test]
    public void AnUnknownEnumValueNamesTheAllowedOnes()
    {
        string dir = TestSupport.TempDir();
        string text = File.ReadAllText(Path.Combine(TestSupport.ProfilesDir, "label-free-dda-1.toml"))
            .Replace("labelling = [\"label_free\"]", "labelling = [\"labelfree\"]");
        TestSupport.WriteFile(dir, "p.toml", text);
        var e = Assert.Throws<ConfigException>(() => ProfileLoader.Load(Path.Combine(dir, "p.toml")));
        Assert.That(e!.Message, Does.Contain("label_free").And.Contain("isobaric"));
    }

    [Test]
    public void AQuestionLoadsAsData()
    {
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "decisions.tsv", "accession\tverdict\treason\nPXD000001\texclude\tstreptozotocin model, not T2D\n");
        var q = QuestionLoader.Load(TestSupport.WriteFile(dir, "question.toml",
            TestSupport.MinimalQuestion.Replace("unless_any = [\"type (2|ii) diabet\"]",
                "unless_any = [\"type (2|ii) diabet\"]\ndecisions = \"decisions.tsv\"")
            + "\n[holds]\nPXD000002 = \"waits for the authors\"\n[overlays.human]\nextra_xml = [\"iso.xml\"]\n"));
        Assert.Multiple(() =>
        {
            Assert.That(q.Name, Is.EqualTo("t2d"));
            Assert.That(q.Profiles, Is.EqualTo(new[] { "label-free-dda@1", "tmt-dda@1" }));
            Assert.That(q.Relevance.Decisions["PXD000001"].Verdict, Is.EqualTo(DecisionVerdict.Exclude));
            Assert.That(q.Holds["PXD000002"], Is.EqualTo("waits for the authors"));
            Assert.That(q.Overlays["human"], Is.EqualTo(new[] { "iso.xml" }));
        });
    }

    [TestCase("profiles = [\"label-free-dda\"]", "id@version")]
    [TestCase("question = \"T2D\"", "lower-case")]
    [TestCase("require_any = [\"(unclosed\"]", "not a valid regex")]
    public void AnInvalidQuestionSaysWhatIsWrong(string replacement, string expected)
    {
        string key = replacement.Split(" = ")[0];
        string text = string.Join("\n", TestSupport.MinimalQuestion.Split('\n')
            .Select(l => l.TrimStart().StartsWith(key + " =") ? replacement : l));
        string dir = TestSupport.TempDir();
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(dir, "q.toml", text)));
        Assert.That(e!.Message, Does.Contain(expected));
    }

    [Test]
    public void UnlessWithoutExcludeIsRefused()
    {
        // A Windows checkout (core.autocrlf) gives the raw string CRLF endings.
        string text = TestSupport.MinimalQuestion.ReplaceLineEndings("\n").Replace("exclude_if_any = [\"type (1|i) diabet\"]\n", "");
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.Load(TestSupport.WriteFile(TestSupport.TempDir(), "q.toml", text)));
        Assert.That(e!.Message, Does.Contain("unless_any"));
    }

    [Test]
    public void ADecisionWithoutAReasonIsRefused()
    {
        string dir = TestSupport.TempDir();
        TestSupport.WriteFile(dir, "d.tsv", "accession\tverdict\treason\nPXD000001\tinclude\t \n");
        var e = Assert.Throws<ConfigException>(() => QuestionLoader.LoadDecisions(Path.Combine(dir, "d.tsv")));
        Assert.That(e!.Message, Does.Contain("needs a reason"));
    }

    [Test]
    public void AConfigErrorIsAUsageError()
    {
        Assert.That(Envelope.Classify(new ConfigException("q.toml", "bad")), Is.EqualTo(("usage", Envelope.ExitUsage)));
    }
}
