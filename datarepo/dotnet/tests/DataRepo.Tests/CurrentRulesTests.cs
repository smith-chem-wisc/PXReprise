using DataRepo.Ingest;
using DataRepo.Ingest.Sources;

namespace DataRepo.Tests;

/// <summary>G83: the Python 0.32.0 behaviours the port reproduced on purpose, fixed under
/// <see cref="IngestRules.Current"/> only. Each test states the class of input the fix covers and checks both
/// rules: <see cref="IngestRules.Python0320"/> must keep 0.32.0's answer, so parity bundles stay reproducible.</summary>
/// <remarks>Unlike the parity tests, these say what the C# is meant to do, because 0.32.0's answer is the defect.</remarks>
public class CurrentRulesTests
{
    // ---------------------------------------------------------------- item 2: lines outside an entry

    /// <summary>Every line kind an entry reads (TG, MM, DR) placed where no entry is open: after a <c>//</c>
    /// and before the next <c>ID</c>, and before the first <c>ID</c> of the file.</summary>
    [TestCase("TG   Serine.\n", "targets")]
    [TestCase("MM   99.5\n", "mass")]
    [TestCase("DR   Unimod; 5.\n", "unimod")]
    public void LinesOutsideAnEntryBelongToNoEntry(string stray, string field)
    {
        foreach (var where in new[] { "between", "before" })
        {
            var text = where == "between"
                ? "ID   First\nTG   Lysine.\nMM   42.0\nDR   Unimod; 1.\n//\n" + stray + "ID   Second\n//\n"
                : stray + "ID   Second\n//\n";
            var current = ModList.ParseEntries(text, "t.txt", IngestRules.Current).Single(e => e.Name == "Second");
            var python = ModList.ParseEntries(text, "t.txt", IngestRules.Python0320).Single(e => e.Name == "Second");
            Assert.Multiple(() =>
            {
                Assert.That(current.Targets, Is.Empty, $"{where}: Current targets");
                Assert.That(current.MonoisotopicMass, Is.Null, $"{where}: Current mass");
                Assert.That(current.Unimod, Is.Null, $"{where}: Current unimod");
                object? leaked = field switch
                {
                    "targets" => python.Targets.SingleOrDefault(),
                    "mass" => python.MonoisotopicMass,
                    _ => python.Unimod,
                };
                Assert.That(leaked, Is.EqualTo(field switch { "targets" => (object)"S", "mass" => 99.5, _ => 5L }),
                    $"{where}: 0.32.0 leaked the stray {field} into the next entry");
            });
        }
    }

    // ---------------------------------------------------------------- item 1: no guessed modification

    private static ModEntry Entry(string name, string targets, long? unimod, double? mass = null, string source = "Mods.txt") =>
        new(name, ModList.Targets(targets), unimod, mass, source);

    /// <summary>A registry where the name exists, but for other residues than the one asked about.</summary>
    private static ModRegistry Acetyl() => new(
    [
        Entry("N6-acetyllysine", "Lysine.", 1, 42.010565, "ptmlist.txt"),
        Entry("Hydroxylation", "K", 35, 15.994915),
        Entry("Hydroxylation", "P", 35, 15.994915),
        Entry("Glyco", "Nxs", 43, 203.079373),
        Entry("Acetylation", "X", 1, 42.010565),
        Entry("Mixed", "K", 1, 42.0),
        Entry("Mixed", "R", 2, 43.0),
    ]);

    [TestCase("N6-acetyllysine on S", null)]          // the G83 example: the entry targets K only
    [TestCase("N6-acetyllysine", "S")]
    [TestCase("Hydroxylation on M", null)]           // two entries, neither for M
    [TestCase("Mixed", null)]                       // no residue, and the candidates disagree
    public void ANameWhoseEntriesDoNotFitIsNotResolved(string name, string? residue)
    {
        var registry = Acetyl();
        Assert.Multiple(() =>
        {
            Assert.That(registry.Lookup(name, residue, IngestRules.Current), Is.Null, "Current");
            Assert.That(registry.Lookup(name, residue, IngestRules.Python0320)?.UnimodCurie, Is.Not.Null, "0.32.0 guessed one");
        });
    }

    [TestCase("N6-acetyllysine on K", null, "UNIMOD:1")]
    [TestCase("N6-acetyllysine", "K", "UNIMOD:1")]
    [TestCase("Hydroxylation on P", null, "UNIMOD:35")]
    [TestCase("Glyco on Nxs", null, "UNIMOD:43")]   // a motif names its first residue
    [TestCase("Glyco", "Nxt", "UNIMOD:43")]
    [TestCase("N6-acetyllysine on Lysine", null, "UNIMOD:1")]  // a residue spelled out, as a TG line spells it
    [TestCase("Acetylation on M", null, "UNIMOD:1")]  // an X entry fits any residue
    [TestCase("Hydroxylation", null, "UNIMOD:35")]  // no residue, and the candidates agree
    public void ANameWhoseEntryFitsIsResolved(string name, string? residue, string unimod) =>
        Assert.That(Acetyl().Lookup(name, residue, IngestRules.Current)?.UnimodCurie, Is.EqualTo(unimod));

    [Test]
    public void TheExactTargetWinsOverAnyResidue()
    {
        var registry = new ModRegistry([Entry("Thing", "X", 1), Entry("Thing", "K", 2)]);
        Assert.That(registry.Lookup("Thing on K", rules: IngestRules.Current)?.Unimod, Is.EqualTo(2));
    }

    /// <summary>A token on a residue whose own residue is another one, first, middle and last.</summary>
    [TestCase("PEPS[Common Biological:Hydroxylation on K]EK")]
    [TestCase("S[Common Biological:Hydroxylation on K]EPK")]
    [TestCase("PEPKES[Common Biological:Hydroxylation on P]")]
    public void ATokenThatContradictsItsResidueIsNotResolved(string sequence)
    {
        var current = Proforma.Parse(sequence, Acetyl(), IngestRules.Current);
        var python = Proforma.Parse(sequence, Acetyl(), IngestRules.Python0320);
        Assert.Multiple(() =>
        {
            Assert.That(current.Mods.Single().Unimod, Is.Null, current.Proforma);
            Assert.That(current.Proforma, Does.Contain("[Info:"));
            Assert.That(current.Unresolved, Has.Count.EqualTo(1));
            Assert.That(python.Mods.Single().Unimod, Is.Not.Null, "0.32.0 resolved it");
        });
    }

    /// <summary>A terminal bracket is exempt: mzLib's reversed decoys carry the target's N-terminal modification onto
    /// another first residue (<c>[UniProt:N-acetylglycine on G]SVAA...</c>, every one of 17,667 such PSMs in aging's
    /// corpus a decoy), and the modification is still the one the engine scored.</summary>
    [TestCase("[UniProt:N6-acetyllysine on K]SEPK", "[UNIMOD:1]-SEPK")]
    [TestCase("PEPS-[Common Biological:Hydroxylation on K]", "PEPS-[UNIMOD:35]")]
    public void ATerminalTokenIsNotCheckedAgainstTheResidue(string sequence, string proforma) =>
        Assert.That(Proforma.Parse(sequence, Acetyl(), IngestRules.Current).Proforma, Is.EqualTo(proforma));

    [TestCase("PEPK[Common Biological:Hydroxylation on K]EK", "PEPK[UNIMOD:35]EK")]
    [TestCase("[UniProt:N6-acetyllysine on K]KEPS", "[UNIMOD:1]-KEPS")]
    [TestCase("PEPK-[Common Biological:Hydroxylation on K]", "PEPK-[UNIMOD:35]")]
    [TestCase("[Common Biological:Acetylation on X]SEPK", "[UNIMOD:1]-SEPK")]
    public void ATokenOnItsOwnResidueIsResolved(string sequence, string proforma) =>
        Assert.That(Proforma.Parse(sequence, Acetyl(), IngestRules.Current).Proforma, Is.EqualTo(proforma));

    // ---------------------------------------------------------------- item 3: an unclosed bracket

    [TestCase("PEPK[Common Biological:Hydroxylation on K", "Common Biological:Hydroxylation on K", "Common Biological:Hydroxylation on ")]
    [TestCase("[Foo:Bar", "Foo:Bar", "Foo:Ba")]
    [TestCase("AB[x[y]z", "x[y]z", "x[y]")]
    [TestCase("A[\U0001F600", "\U0001F600", "")]  // a surrogate pair is one code point, dropped whole by 0.32.0
    public void AnUnclosedBracketKeepsEverythingAfterIt(string sequence, string current, string python)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Proforma.Split(sequence, IngestRules.Current).Select(p => p.Bracket).Last(b => b is not null), Is.EqualTo(current));
            Assert.That(Proforma.Split(sequence, IngestRules.Python0320).Select(p => p.Bracket).Last(b => b is not null), Is.EqualTo(python));
        });
    }

    [Test]
    public void AClosedBracketIsUnchanged()
    {
        foreach (var rules in new[] { IngestRules.Current, IngestRules.Python0320 })
            Assert.That(Proforma.Split("[a]PE[b[c]]K", rules).Select(p => (p.Residue, p.Bracket)),
                Is.EqualTo(new (string, string?)[] { ("", "a"), ("P", null), ("E", "b[c]"), ("K", null) }), rules.ToString());
    }

    // ---------------------------------------------------------------- item 4: unresolved counted per sequence

    [Test]
    public void UnresolvedIsCountedOncePerDistinctSequence()
    {
        var current = new ProformaCache(new ModRegistry(), IngestRules.Current);
        var python = new ProformaCache(new ModRegistry(), IngestRules.Python0320);
        foreach (var cache in new[] { current, python })
            foreach (var s in new[] { "PEP[Foo:Bar on K]", "PEP[Foo:Bar on K]", "PEP[Foo:Bar on K]", "AK[Foo:Bar on K]", "QQ" })
                cache.Get(s);
        Assert.Multiple(() =>
        {
            Assert.That(current.Unresolved["Foo:Bar on K"], Is.EqualTo(2), "two distinct sequences carry it");
            Assert.That(python.Unresolved["Foo:Bar on K"], Is.EqualTo(4), "0.32.0 counted every call");
        });
    }

    [Test]
    public void AnEntrysOwnLinesAreStillRead()
    {
        // ptmlist style: no `//` between entries, and Mods.txt style with one.
        foreach (var text in new[] { "ID   A\nTG   K\nMM   1.5\nDR   Unimod; 7\nID   B\nTG   S\n", "ID   A\nTG   K\nMM   1.5\nDR   Unimod; 7\n//\nID   B\nTG   S\n//\n" })
        {
            var entries = ModList.ParseEntries(text, "t.txt", IngestRules.Current).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "A", "B" }));
                Assert.That(entries[0].Targets, Is.EquivalentTo(new[] { "K" }));
                Assert.That(entries[0].MonoisotopicMass, Is.EqualTo(1.5));
                Assert.That(entries[0].Unimod, Is.EqualTo(7));
                Assert.That(entries[1].Targets, Is.EquivalentTo(new[] { "S" }));
                Assert.That(entries[1].MonoisotopicMass, Is.Null);
                Assert.That(entries[1].Unimod, Is.Null);
            });
        }
    }
}
