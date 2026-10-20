using DataRepo.Bundle;
using DataRepo.Runner;
using Quantification.PtmQtl;

namespace DataRepo.Tests;

/// <summary>ptmQtl's site rules that are not mzLib's (G99): S2c, S2d and S3b's sample types, and the operator lists
/// they read (<c>ptmQtl/design/DEFINITIONS.md</c>, 2026-10-19).</summary>
public class PtmQtlRulesTests
{
    // ---------------------------------------------------------------- full sequences

    [Test]
    public void AFullSequenceIsReadByBracketDepth()
    {
        var (bases, mods) = PtmQtlRules.Parse("[UniProt:N-acetylserine on S]SPEK[Common Biological:Acetylation on K]D[Metal:Cu[I] on D]K");
        Assert.Multiple(() =>
        {
            Assert.That(bases, Is.EqualTo("SPEKDK"));
            Assert.That(mods, Is.EqualTo(new[]
            {
                new PtmQtlRules.Placement("UniProt:N-acetylserine on S", 0, null),
                new PtmQtlRules.Placement("Common Biological:Acetylation on K", 4, 'K'),
                new PtmQtlRules.Placement("Metal:Cu[I] on D", 5, 'D'),
            }));
        });
    }

    // ---------------------------------------------------------------- S3, S3b

    [TestCase(new[] { "none" }, null, "whole_proteome")]
    [TestCase(new[] { "phospho" }, null, "ptm_capture")]
    [TestCase(new[] { "immunoprecipitation" }, null, "protein_pulldown")]
    [TestCase(new[] { "chemical_probe" }, null, "protein_pulldown")]
    [TestCase(new[] { "other" }, null, "unknown")]
    [TestCase(new[] { "none", "phospho" }, null, "unknown")]
    [TestCase(null, null, "unknown")]
    [TestCase(new[] { "none" }, "ptm_capture", "ptm_capture")]
    [TestCase(new[] { "none" }, "held", "ptm_capture")]
    [TestCase(new[] { "none" }, "no_ptm_capture", "whole_proteome")]
    public void ARunsSampleTypeIsTheCaptureListsThenItsOwnLabels(string[]? labels, string? captureClass, string type) =>
        Assert.That(PtmQtlRules.RunType("PXD1:r1", labels, new PtmQtlDeposit(captureClass)), Is.EqualTo(type));

    [Test]
    public void ACaptureRunNamedByTheListIsAPtmCapture()
    {
        var deposit = new PtmQtlDeposit("capture_runs", new HashSet<string> { "PXD1:r2" });
        Assert.Multiple(() =>
        {
            Assert.That(PtmQtlRules.RunType("PXD1:r1", ["none"], deposit), Is.EqualTo(PtmQtlRules.WholeProteome));
            Assert.That(PtmQtlRules.RunType("PXD1:r2", ["none"], deposit), Is.EqualTo(PtmQtlRules.PtmCapture), "the list overrides a wrong label");
        });
    }

    // ---------------------------------------------------------------- S2c

    [TestCase("Acetylation on K", 'K', true)]
    [TestCase("N6-acetyllysine on K", 'K', true)]
    [TestCase("GG (Ubiquitination Site) on K", 'K', true)]
    [TestCase("Citrullination on R", 'R', true)]
    [TestCase("Citrulline on R", 'R', true)]
    [TestCase("N6-succinyllysine on K", 'K', true)]
    [TestCase("Crotonylation on K", 'K', true)]
    [TestCase("Hydroxybutyrylation on K", 'K', true)]
    [TestCase("N6-(2-hydroxyisobutyryl)lysine on K", 'K', true)]
    [TestCase("Malonylation on K", 'K', true)]
    [TestCase("Glutarylation on K", 'K', true)]
    [TestCase("Methylation on K", 'K', false)]
    [TestCase("Acetylation on X", 'K', false)]
    [TestCase("Acetylation on K", 'R', false)]
    public void TheBlockingModificationsAreMeasurePysPatterns(string idWithMotif, char residue, bool blocking) =>
        Assert.That(PtmQtlRules.IsBlocking(idWithMotif, residue), Is.EqualTo(blocking));

    [Test]
    public void ABlockingModificationOnAPeptidesLastResidueLeavesItsSiteOut()
    {
        PeptidoformObservation O(string run, string full, int start) => new(run, full, "P1", start, start + 8, 1.0);
        var sites = PtmQtlRules.BlockedSites([
            O("r1", "PEPTIDEAK[Common Biological:Acetylation on K]", 10),     // last residue: K18 out
            O("r2", "PEPTIDEAK[Common Biological:Acetylation on K]", 10),     // the same, another run: once
            O("r1", "PEK[Common Biological:Acetylation on K]TIDEAR", 30),    // internal: K32 stays
            O("r1", "PEPTIDEAR[Common Biological:Citrullination on R]", 50), // R58 out
            O("r1", "PEPTIDEAK[Common Biological:Methylation on K]", 70),     // not blocking
        ]);
        Assert.That(sites.Select(s => $"{s.Protein}:{s.Residue}{s.Position}:{s.Modification}"),
            Is.EqualTo(new[] { "P1:K18:Acetylation on K", "P1:R58:Citrullination on R" }));
        Assert.That(sites.All(s => s.Rule == "S2c" && s.Reason.StartsWith("S2c:", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- S2d

    [Test]
    public void AnNemDepositLosesEverySiteACysPeptideCovers()
    {
        var sites = PtmQtlRules.CoveredSites([("P1", 10, 20), ("P2", 5, 9)],
            [("P1", 9, 'K'), ("P1", 10, 'K'), ("P1", 20, 'S'), ("P1", 21, 'K'), ("P2", 7, 'C'), ("P3", 12, 'K')]);
        Assert.That(sites.Select(s => $"{s.Protein}:{s.Position}"), Is.EqualTo(new[] { "P1:10", "P1:20", "P2:7" }));
        Assert.That(sites.All(s => s.Modification is null && s.Rule == "S2d (a)"), "every modification at a covered place");
    }

    [Test]
    public void ACysteinePoorDepositLosesItsResidueCysteineSitesOnly()
    {
        var sites = PtmQtlRules.CysteineSites([("P1", 4, 'C', "residue"), ("P1", 4, 'C', "residue"), ("P1", 1, 'C', "protein_n_term"), ("P1", 9, 'K', "residue")], "MMTS");
        Assert.That(sites.Select(s => $"{s.Protein}:{s.Position}"), Is.EqualTo(new[] { "P1:4" }));
        Assert.That(sites[0].Reason, Is.EqualTo("S2d (a): cysteine-poor deposit (MMTS), cysteine site"));
    }

    [TestCase(68.026215, "NEM")]                          // UNIMOD:1363 crotonyl, NEM's leftover
    [TestCase(86.036779, "NEM, ring opened by water")]    // hydroxybutyryl
    [TestCase(14.015650, "acrylamide (propionamide)")]    // methyl
    [TestCase(0.984016, "carboxymethyl")]                 // deamidation, citrullination's mass
    [TestCase(42.010565, null)]                           // acetyl matches no label
    [TestCase(68.02815, "NEM")]                           // 0.00194 off: within 0.002
    [TestCase(68.0283, null)]                             // 0.00209 off: not
    public void AModificationMatchesALabelLessCarbamidomethyl(double mass, string? label) =>
        Assert.That(PtmQtlRules.LabelOf(mass), Is.EqualTo(label));

    [Test]
    public void TheScreenFlagsAModificationOverRepresentedOnCysPeptides()
    {
        const string crot = "[Common Biological:Crotonylation on K]";
        var rows = new List<(string, string)>();
        // 12 crotonyl peptidoforms, 6 with a C over 6 sequences: 50 %. Baseline: 100 unmodified, 10 with a C: 10 %.
        for (var i = 0; i < 6; i++) rows.Add(($"PEC{(char)('A' + i)}K", $"PEC{(char)('A' + i)}K{crot}"));
        for (var i = 0; i < 6; i++) rows.Add(($"PEA{(char)('D' + i)}K", $"PEA{(char)('D' + i)}K{crot}"));
        for (var i = 0; i < 100; i++) rows.Add((i < 10 ? $"C{i}" : $"A{i}", i < 10 ? $"C{i}" : $"A{i}"));
        // Methylation: 10 peptidoforms, all with a C but over 4 sequences only: not flagged.
        for (var i = 0; i < 10; i++) rows.Add(($"PCM{i % 4}K", $"PCM{i % 4}K[Common Biological:Methylation on K]"));
        var screened = PtmQtlRules.Screen(rows).ToDictionary(s => s.Name);
        var c = screened["Common Biological:Crotonylation on K"];
        Assert.Multiple(() =>
        {
            Assert.That((c.Peptidoforms, c.WithC, c.SequencesWithC, c.Share, c.BaselineShare, c.Flag), Is.EqualTo((12L, 6L, 6L, 0.5, 0.1, true)));
            Assert.That(screened["Common Biological:Methylation on K"].Flag, Is.False, "fewer than 5 Cys-containing sequences");
        });
    }

    [Test]
    public void AFlaggedMatchedModificationLeavesTheSitesCysPeptidesCarryItOn()
    {
        const string name = "Common Biological:Crotonylation on K";
        var sites = PtmQtlRules.CarriedSites([
            ("P1", 10, $"PEPCK[{name}]AR"),       // K14
            ("P2", 10, $"PEPCK[{name}]AR"),       // a shared PSM: its one start for each accession
            ("P1", 30, $"PEPCK[Common Biological:Methylation on K]AR"),
        ], name, "NEM");
        Assert.That(sites.Select(s => $"{s.Protein}:{s.Position}:{s.Modification}"), Is.EqualTo(new[] { "P1:14:Crotonylation on K", "P2:14:Crotonylation on K" }));
        Assert.That(sites[0].Reason, Is.EqualTo("S2d (b): Crotonylation on K = NEM - carbamidomethyl, carried by a Cys-containing peptidoform"));
    }

    // ---------------------------------------------------------------- the operator's lists

    private static string Write(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public void TheListsDescribeEachDeposit()
    {
        var dir = Directory.CreateTempSubdirectory("datarepo-ptmqtl-lists-").FullName;
        try
        {
            var inputs = new Dictionary<string, string>
            {
                ["enrichment"] = Write(dir, "deposits.tsv", "dataset\tclass\tenriched_ptm\nPXD1\tptm_capture\tphospho\nPXD2\tcapture_runs\tacetyl\nPXD3\tno_ptm_capture\t\n"),
                ["capture_runs"] = Write(dir, "capture_runs.tsv", "dataset\trun_id\tlabel\nPXD2\tPXD2:r1\tacetyl\n"),
                ["cysteine"] = Write(dir, "cysteine.tsv", "dataset\tclass\treason\nPXD3\tnem\t\nPXD4\tcysteine_poor\tMMTS\n"),
            };
            var deposits = PtmQtlEngine.Deposits(inputs);
            Assert.Multiple(() =>
            {
                Assert.That(deposits["PXD1"].Enriched);
                Assert.That(deposits["PXD2"].CaptureRuns, Is.EquivalentTo(new[] { "PXD2:r1" }));
                Assert.That((deposits["PXD3"].CaptureClass, deposits["PXD3"].CysteineClass, deposits["PXD3"].CysteineReason), Is.EqualTo(("no_ptm_capture", "nem", (string?)null)));
                Assert.That((deposits["PXD4"].CaptureClass, deposits["PXD4"].CysteineClass, deposits["PXD4"].CysteineReason), Is.EqualTo(((string?)null, "cysteine_poor", "MMTS")));
                Assert.That(PtmQtlEngine.DepositOf(deposits, "PXD4-g2").CysteineClass, Is.EqualTo("cysteine_poor"), "a split search is its deposit's");
                Assert.That(PtmQtlEngine.DepositOf(deposits, "PXD9").CaptureClass, Is.Null);
            });

            var oldList = new Dictionary<string, string>(inputs) { ["enrichment"] = Write(dir, "v3.tsv", "dataset\tenriched_ptm\nPXD1\tphospho\n") };
            oldList.Remove("capture_runs");
            Assert.That(PtmQtlEngine.Deposits(oldList)["PXD1"].Enriched, "a list without classes names captures (v1-v3)");

            var unnamed = new Dictionary<string, string>(inputs);
            unnamed.Remove("capture_runs");
            Assert.That(() => PtmQtlEngine.Deposits(unnamed), Throws.TypeOf<RunnerException>().With.Message.Contains("names none of their runs"));
            var badClass = new Dictionary<string, string>(inputs) { ["cysteine"] = Write(dir, "bad.tsv", "dataset\tclass\nPXD3\tiodoacetamide\n") };
            Assert.That(() => PtmQtlEngine.Deposits(badClass), Throws.TypeOf<RunnerException>().With.Message.Contains("not nem or cysteine_poor"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
