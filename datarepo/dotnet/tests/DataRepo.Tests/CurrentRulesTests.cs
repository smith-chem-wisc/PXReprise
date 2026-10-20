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

    // ---------------------------------------------------------------- items 6 and 8: the SDRF

    private static string WriteSdrf(params string[][] rows)
    {
        string[] header =
        [
            "source name", "characteristics[organism]", "characteristics[biological replicate]", "assay name",
            "comment[label]", "comment[instrument]", "comment[fraction identifier]", "comment[technical replicate]",
            "comment[data file]",
        ];
        return WriteSdrfWith(header, rows);
    }

    private static string WriteSdrfWith(string[] header, params string[][] rows)
    {
        var dir = Path.Combine(Path.GetTempPath(), "datarepo-g83-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "t.sdrf.tsv");
        File.WriteAllText(path, string.Join("\n", new[] { header }.Concat(rows).Select(r => string.Join("\t", r))) + "\n",
            new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>A multiplexed run: one file, one row per channel, and the rows disagree on a run-level fact.</summary>
    [TestCase(5, "NT=Q Exactive", "NT=Orbitrap Fusion", "1", "1")]   // the instrument
    [TestCase(6, "NT=Q Exactive", "NT=Q Exactive", "1", "2")]        // the fraction
    public void ARunsFactIsWhatEveryRowSays(int column, string instrumentA, string instrumentB, string fractionA, string fractionB)
    {
        var path = WriteSdrf(
            ["s1", "homo sapiens", "1", "a1", "TMT126", instrumentA, fractionA, "1", "run1.raw"],
            ["s2", "homo sapiens", "1", "a2", "TMT127", instrumentB, fractionB, "1", "run1.raw"],
            ["s3", "homo sapiens", "1", "a3", "TMT126", "NT=Q Exactive", "3", "1", "run2.raw"],
            ["s4", "homo sapiens", "1", "a4", "TMT127", "NT=Q Exactive", "3", "1", "run2.raw"]);
        var current = Sdrf.Parse(path, "PXD1", rules: IngestRules.Current);
        var python = Sdrf.Parse(path, "PXD1", rules: IngestRules.Python0320);
        var key = column == 5 ? "instrument_model" : "fraction";
        Assert.Multiple(() =>
        {
            Assert.That(current.RunFacts["run1"][key], Is.Null, "the rows disagree: no row's value is the run's");
            Assert.That(python.RunFacts["run1"][key], Is.EqualTo(column == 5 ? (object)"Orbitrap Fusion" : 2L), "0.32.0: the last row won");
            Assert.That(current.RunFacts["run2"]["instrument_model"], Is.EqualTo("Q Exactive"), "agreeing rows keep it");
            Assert.That(current.RunFacts["run2"]["fraction"], Is.EqualTo(3L));
            Assert.That(current.RunFacts["run1"][column == 5 ? "fraction" : "instrument_model"], Is.Not.Null, "the other fact agrees");
            Assert.That(current.Assays, Has.Count.EqualTo(4), "every channel is still an assay");
        });
    }

    /// <summary>Digit characters that are not decimal digits: superscripts, a circled digit, a fraction.</summary>
    [TestCase("²")]
    [TestCase("1³")]
    [TestCase("①")]
    public void ANonDecimalDigitIsNotANumber(string cell)
    {
        var path = WriteSdrf(["s1", "homo sapiens", cell, "a1", "label free sample", "NT=Q Exactive", cell, cell, "run1.raw"]);
        var current = Sdrf.Parse(path, "PXD1", rules: IngestRules.Current);
        Assert.Multiple(() =>
        {
            Assert.That(current.Samples.Single()["biological_replicate"], Is.Null);
            Assert.That(current.RunFacts["run1"]["fraction"], Is.Null);
            Assert.That(current.RunFacts["run1"]["technical_replicate"], Is.Null);
            Assert.That(() => Sdrf.Parse(path, "PXD1", rules: IngestRules.Python0320), Throws.TypeOf<FormatException>(),
                "0.32.0: isdigit accepted it and int() refused the ingest");
        });
    }

    [TestCase("12", 12L)]
    [TestCase("١٢", 12L)]  // Arabic-Indic decimal digits are decimal digits; int() reads them
    public void DecimalDigitsAreStillNumbers(string cell, long value)
    {
        var path = WriteSdrf(["s1", "homo sapiens", cell, "a1", "label free sample", "NT=Q Exactive", cell, cell, "run1.raw"]);
        Assert.That(Sdrf.Parse(path, "PXD1", rules: IngestRules.Current).RunFacts["run1"]["fraction"], Is.EqualTo(value));
    }

    // ---------------------------------------------------------------- G106: one source name, rows that disagree

    private static readonly string[] SampleHeader =
    [
        "source name", "characteristics[organism]", "characteristics[age]", "characteristics[individual]",
        "comment[age source]", "comment[characteristics source]", "comment[fraction identifier]",
        "comment[fraction identifier source]", "comment[data file]",
    ];

    private static string? Characteristic(SdrfTable table, string sampleId, string name) =>
        (string?)table.Characteristics.SingleOrDefault(c => (string)c["sample_id"]! == sampleId && (string)c["name"]! == name)?["value"];

    /// <summary>PXD012307's shape: two animals, 24 and 3 months old, written as one source name over 4 files.</summary>
    [Test]
    public void ACellTheRowsDisagreeOnHasNoValue()
    {
        var path = WriteSdrfWith(SampleHeader,
            ["Sample 1", "mus musculus", "24M", "old_14", "", "", "1", "deposited", "a.raw"],
            ["Sample 1", "mus musculus", "24M", "old_14", "", "", "2", "deposited", "b.raw"],
            ["Sample 1", "mus musculus", "3M", "young_11", "", "", "1", "drafted", "c.raw"],
            ["Sample 1", "mus musculus", "3M", "young_11", "", "", "2", "drafted", "d.raw"]);
        var current = Sdrf.Parse(path, "PXD1", rules: IngestRules.Current);
        var python = Sdrf.Parse(path, "PXD1", rules: IngestRules.Python0320);
        var sample = current.Samples.Single();
        Assert.Multiple(() =>
        {
            Assert.That(Characteristic(current, "PXD1:Sample 1", "characteristics[age]"), Is.Null, "no row's age is the sample's");
            Assert.That(Characteristic(current, "PXD1:Sample 1", "characteristics[individual]"), Is.Null);
            Assert.That(sample["individual_id"], Is.Null, "the typed column read from the cell is NULL too");
            Assert.That(Characteristic(current, "PXD1:Sample 1", "characteristics[organism]"), Is.EqualTo("mus musculus"), "agreeing rows keep it");
            Assert.That(sample["organism"], Is.EqualTo("NCBITaxon:10090"));
            Assert.That(current.Conflicts.Select(c => c.Column),
                Is.EqualTo(new[] { "characteristics[age]", "characteristics[individual]" }),
                "a fraction is the file's, so rows differing there are no conflict");
            var age = current.Conflicts[0];
            Assert.That(age.SampleId, Is.EqualTo("PXD1:Sample 1"));
            Assert.That(age.Values.Select(v => v.Value), Is.EqualTo(new[] { "24M", "3M" }));
            Assert.That(age.Values.Select(v => string.Join(",", v.Runs)), Is.EqualTo(new[] { "a,b", "c,d" }), "each value names its rows' runs");
            Assert.That(current.Assays, Has.Count.EqualTo(4), "every file is still an assay of the sample");
            Assert.That(Characteristic(python, "PXD1:Sample 1", "characteristics[age]"), Is.EqualTo("24M"), "0.32.0: the first row won");
            Assert.That(python.Conflicts, Is.Empty);
        });
    }

    /// <summary>An empty cell says nothing, so it neither agrees nor disagrees; a reserved word is a value.</summary>
    [TestCase("24M", "", "24M", false)]
    [TestCase("", "3M", "3M", false)]             // 0.32.0 read the first row only, and so lost it
    [TestCase("24M", "not available", null, true)]
    [TestCase("24M", " 24M ", "24M", false)]      // the same value, padded
    public void OnlyWrittenCellsDisagree(string first, string second, string? value, bool conflict)
    {
        var path = WriteSdrfWith(SampleHeader,
            ["s1", "homo sapiens", first, "", "", "", "1", "", "a.raw"],
            ["s1", "homo sapiens", second, "", "", "", "2", "", "b.raw"]);
        var current = Sdrf.Parse(path, "PXD1", rules: IngestRules.Current);
        Assert.Multiple(() =>
        {
            Assert.That(Characteristic(current, "PXD1:s1", "characteristics[age]"), Is.EqualTo(value));
            Assert.That(current.Conflicts.Count > 0, Is.EqualTo(conflict));
        });
    }

    /// <summary>A characteristic's own source the rows disagree on is NULL: it is not absent, so the row default
    /// (<c>comment[characteristics source]</c>) must not stand in for it (G75).</summary>
    [Test]
    public void ASourceTheRowsDisagreeOnIsNotTheRowDefault()
    {
        var path = WriteSdrfWith(SampleHeader,
            ["s1", "homo sapiens", "24M", "", "publication", "deposited", "1", "", "a.raw"],
            ["s1", "homo sapiens", "24M", "", "pride project record", "deposited", "2", "", "b.raw"]);
        var current = Sdrf.Parse(path, "PXD1", rules: IngestRules.Current);
        var age = current.Characteristics.Single(c => (string)c["name"]! == "characteristics[age]");
        var organism = current.Characteristics.Single(c => (string)c["name"]! == "characteristics[organism]");
        Assert.Multiple(() =>
        {
            Assert.That(age["value"], Is.EqualTo("24M"), "the value agrees");
            Assert.That(age["source"], Is.Null);
            Assert.That(organism["source"], Is.EqualTo("deposited"), "an agreed row default still applies");
            Assert.That(current.Conflicts.Single().Column, Is.EqualTo("comment[age source]"));
        });
    }

    /// <summary>An organism the rows disagree on falls back to the manifest's, as an organism with no term does:
    /// <c>samples.organism</c> is required.</summary>
    [Test]
    public void AnOrganismTheRowsDisagreeOnIsTheManifests()
    {
        var path = WriteSdrfWith(SampleHeader,
            ["s1", "homo sapiens", "", "", "", "", "1", "", "a.raw"],
            ["s1", "mus musculus", "", "", "", "", "2", "", "b.raw"]);
        var current = Sdrf.Parse(path, "PXD1", "NCBITaxon:10116", rules: IngestRules.Current);
        Assert.Multiple(() =>
        {
            Assert.That(current.Samples.Single()["organism"], Is.EqualTo("NCBITaxon:10116"));
            Assert.That(Characteristic(current, "PXD1:s1", "characteristics[organism]"), Is.Null);
        });
    }

    // ---------------------------------------------------------------- helpers for file-backed items

    private static string TempFile(string name, string text, bool bom = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "datarepo-g83-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(bom));
        return path;
    }

    // ---------------------------------------------------------------- item 7: no field size limit

    [TestCase(131_073)]
    [TestCase(1_000_000)]
    public void AFieldLongerThanPythonsCsvLimitIsRead(int length)
    {
        var cell = new string('A', length);
        var path = TempFile("AllQuantifiedProteinGroups.tsv", "Protein Accession\tGene\n" + cell + "\tG1\n");
        var (_, rows) = DataRepo.Ingest.Readers.ReadTsv(path, rules: IngestRules.Current);
        Assert.Multiple(() =>
        {
            Assert.That(rows.Single()[0], Has.Length.EqualTo(length));
            Assert.That(() => DataRepo.Ingest.Readers.ReadTsv(path, rules: IngestRules.Python0320), Throws.TypeOf<InvalidDataException>());
        });
    }

    // ---------------------------------------------------------------- items 10 and 17: the protein database

    /// <summary>Every kind of letter mzLib's loader turns into X before MetaMorpheus digests the sequence.</summary>
    [TestCase("PEPBTIDE", "PEPXTIDE")]
    [TestCase("ZEPJTIDE", "XEPXTIDE")]
    [TestCase("PEPÉTIDE", "PEPXTIDE")]
    [TestCase("PEPTIDEUO", "PEPTIDEUO")]  // selenocysteine and pyrrolysine are residues
    public void ASequenceIsReadAsTheSearchSawIt(string written, string searched)
    {
        var fasta = TempFile("db.fasta", ">sp|P1|ONE_HUMAN one\n" + written + "\n");
        var xml = TempFile("db.xml",
            "<?xml version=\"1.0\"?><uniprot xmlns=\"http://uniprot.org/uniprot\"><entry><accession>P1</accession>"
            + "<sequence>" + written + "</sequence></entry></uniprot>");
        foreach (var path in new[] { fasta, xml })
        {
            var current = new ProteinSequences();
            var python = new ProteinSequences();
            ProteinDb.ReadDatabase(path, current, IngestRules.Current);
            ProteinDb.ReadDatabase(path, python, IngestRules.Python0320);
            Assert.Multiple(() =>
            {
                Assert.That(current.Get("P1"), Is.EqualTo(new[] { searched }), Path.GetFileName(path));
                Assert.That(python.Get("P1"), Is.EqualTo(new[] { written.ToUpperInvariant() }), "0.32.0 kept the letters");
            });
        }
    }

    [Test]
    public void APeptideSearchedAcrossABIsPlaced()
    {
        // The peptide as MetaMorpheus wrote it carries the X mzLib put where the database has B.
        var path = TempFile("db.fasta", ">sp|P1|ONE_HUMAN one\nMKPEPBTIDEK\n");
        var current = new ProteinSequences();
        var python = new ProteinSequences();
        ProteinDb.ReadDatabase(path, current, IngestRules.Current);
        ProteinDb.ReadDatabase(path, python, IngestRules.Python0320);
        Assert.Multiple(() =>
        {
            Assert.That(ProteinDb.Occurrences("PEPXTIDEK", current.Get("P1")[0]), Is.EqualTo(new[] { 3 }));
            Assert.That(ProteinDb.Occurrences("PEPXTIDEK", python.Get("P1")[0]), Is.Empty);
        });
    }

    [Test]
    public void AFastaWithAByteOrderMarkKeepsItsFirstProtein()
    {
        var path = TempFile("db.fasta", ">sp|P1|ONE_HUMAN one\nMKA\n>sp|P2|TWO_HUMAN two\nMKB\n", bom: true);
        var current = ProteinDb.IterFasta(path, IngestRules.Current).Select(p => p.Accession).ToList();
        var python = ProteinDb.IterFasta(path, IngestRules.Python0320).Select(p => p.Accession).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(current, Is.EqualTo(new[] { "P1", "P2" }));
            Assert.That(python, Is.EqualTo(new[] { "P2" }), "0.32.0 read the BOM as a character and lost P1");
        });
    }

    // ---------------------------------------------------------------- DATAREPO-84: entries of another source

    /// <summary>One Swiss-Prot entry and one of aging's Ensembl-only isoforms, in aging's own format (their 116).</summary>
    private const string EnsemblDb = """
        <?xml version="1.0" encoding="UTF-8"?>
        <uniprot xmlns="http://uniprot.org/uniprot">
        <entry dataset="Swiss-Prot"><accession>Q8R429</accession><name>AT2A1_MOUSE</name>
          <gene><name type="primary">Atp2a1</name></gene>
          <organism><name type="scientific">Mus musculus</name><dbReference type="NCBI Taxonomy" id="10090"/></organism>
          <sequence length="6">MEAAHS</sequence></entry>
        <entry dataset="Ensembl" created="2026-10-20" modified="2026-10-20" version="1">
          <accession>ENSMUSP00000292642</accession><name>ENSMUSP00000292642_MOUSE</name>
          <protein><recommendedName><fullName>Ensembl-only form of Atp2a1 (not a UniProt entry)</fullName></recommendedName></protein>
          <gene><name type="primary">Atp2a1</name></gene>
          <organism><name type="scientific">Mus musculus</name><dbReference type="NCBI Taxonomy" id="10090"/></organism>
          <comment type="miscellaneous"><text>Not a UniProt entry.</text></comment>
          <dbReference type="Ensembl" id="ENSMUST00000000001.1"><property type="protein sequence ID" value="ENSMUSP00000292642.1"/></dbReference>
          <sequence length="7">MEAAHSK</sequence></entry>
        </uniprot>
        """;

    /// <summary>A target entry of another source is read with its accession, sequence, source and its own taxon; a
    /// UniProt entry names no other source; 0.32.0's reading is unchanged.</summary>
    [Test]
    public void AnEnsemblEntryIsReadWithItsSourceAndTaxon()
    {
        var path = TempFile("aging_isoforms_mouse.xml", EnsemblDb);
        var sequences = new ProteinSequences();
        Assert.That(ProteinDb.ReadDatabase(path, sequences), Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(sequences.Get("ENSMUSP00000292642"), Is.EqualTo(new[] { "MEAAHSK" }));
            Assert.That(sequences.OtherSource("ENSMUSP00000292642"), Is.EqualTo(("ensembl", (string?)"NCBITaxon:10090")));
            Assert.That(sequences.OtherSource("Q8R429"), Is.Null, "a Swiss-Prot entry is UniProt's");
            Assert.That(ProteinDb.IterUniprotXml(path, IngestRules.Python0320).ToList(),
                Is.EqualTo(new[] { ("Q8R429", "MEAAHS"), ("ENSMUSP00000292642", "MEAAHSK") }));
        });
        var contaminants = new ProteinSequences();
        ProteinDb.ReadDatabase(TempFile("my_contaminants.xml", EnsemblDb), contaminants);
        Assert.That(contaminants.OtherSource("ENSMUSP00000292642"), Is.Null, "a contaminant panel names no target source");
    }

    /// <summary>The protein row says the source the entry names, takes the entry's own taxon, and is its own
    /// canonical; a UniProt accession beside it keeps its row.</summary>
    [Test]
    public void AnEnsemblProteinRowSaysEnsembl()
    {
        var sequences = new ProteinSequences();
        ProteinDb.ReadDatabase(TempFile("aging_isoforms_mouse.xml", EnsemblDb), sequences);
        var columns = new Dictionary<string, List<object?>>
        {
            ["accession"] = ["Q8R429|ENSMUSP00000292642"],
            ["gene_name"] = ["primary:Atp2a1"],
            ["organism_name"] = ["Mus musculus"],
            ["decoy_contam_target"] = ["T"],
        };
        var rows = Identifications.ProteinRows([columns], "PXD1", "NCBITaxon:10090", otherSource: sequences.OtherSource)
            .ToDictionary(r => (string)r["protein_accession"]!);
        Assert.Multiple(() =>
        {
            var e = rows["ENSMUSP00000292642"];
            Assert.That((e["source_db"], e["canonical_accession"], e["organism"], e["organism_name"], e["gene"]),
                Is.EqualTo(("ensembl", "ENSMUSP00000292642", "NCBITaxon:10090", "Mus musculus", "Atp2a1")));
            var u = rows["Q8R429"];
            Assert.That((u["source_db"], u["canonical_accession"], u["organism"]), Is.EqualTo(("uniprot", "Q8R429", "NCBITaxon:10090")));
            var without = Identifications.ProteinRows([columns], "PXD1", "NCBITaxon:10090").Single(r => (string)r["protein_accession"]! == "ENSMUSP00000292642");
            Assert.That(without["source_db"], Is.EqualTo("uniprot"), "0.32.0's rows: every target read uniprot");
        });
    }

    // ---------------------------------------------------------------- DATAREPO-D7: localization-ambiguous peptide rows

    /// <summary>A <c>|</c>-joined peptide row (MetaMorpheus #2917) is left out and counted, never stored under its first
    /// full sequence's site; 0.32.0's rows keep the first.</summary>
    [Test]
    public void ALocalizationAmbiguousPeptideRowIsLeftOutNotStoredAsItsFirstSite()
    {
        var path = TempFile("AllQuantifiedPeptides.tsv",
            "Sequence\tBase Sequence\tIntensity_run1\tIntensity_run2\tDetection Type_run1\tDetection Type_run2\n"
            + "PEPS[Common Biological:Phosphorylation on S]TIDE\tPEPSTIDE\t100\t200\tMSMS\tMSMS\n"
            + "PEPS[Common Biological:Phosphorylation on S]TIDEK|PEPST[Common Biological:Phosphorylation on T]IDEK\tPEPSTIDEK\t300\t\tMSMS\t\n");
        string? Proforma(string s) => s.Replace("[Common Biological:Phosphorylation on S]", "[UNIMOD:21]").Replace("[Common Biological:Phosphorylation on T]", "[UNIMOD:21]");
        var runs = new RunNameMap(["run1", "run2"]);
        var ambiguous = new Dictionary<string, long>(StringComparer.Ordinal);
        var rows = Quant.PeptideQuantRows(path, "PXD1", runs, Proforma, ambiguous: ambiguous);
        var python = Quant.PeptideQuantRows(path, "PXD1", runs, Proforma, rules: IngestRules.Python0320);
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => (string)r["feature_id"]!).Distinct(), Is.EqualTo(new[] { "PXD1:PEPS[UNIMOD:21]TIDE" }));
            Assert.That(ambiguous.Single().Value, Is.EqualTo(1), "one value left out");
            Assert.That(python.Count(r => (string)r["feature_id"]! == "PXD1:PEPS[UNIMOD:21]TIDEK"), Is.EqualTo(1), "0.32.0 keyed it on the first");
        });
    }

    // ---------------------------------------------------------------- PXR-R12: UniProt namespaces, empty reads

    /// <summary>Every namespace a UniProt XML the search read can carry: UniProt's http and https, the
    /// <c>undefined</c> mzLib's writer can emit, none, and a prefixed form.</summary>
    [TestCase("<uniprot xmlns=\"http://uniprot.org/uniprot\"><entry><accession>P1</accession><sequence>MKA</sequence></entry></uniprot>", true)]
    [TestCase("<uniprot xmlns=\"https://uniprot.org/uniprot\"><entry><accession>P1</accession><sequence>MKA</sequence></entry></uniprot>", false)]
    [TestCase("<uniprot xmlns=\"undefined\"><entry><accession>P1</accession><sequence>MKA</sequence></entry></uniprot>", false)]
    [TestCase("<uniprot><entry><accession>P1</accession><sequence>MKA</sequence></entry></uniprot>", false)]
    [TestCase("<u:uniprot xmlns:u=\"https://uniprot.org/uniprot\"><u:entry><u:accession>P1</u:accession><u:sequence>MKA</u:sequence></u:entry></u:uniprot>", false)]
    public void AUniprotXmlIsReadInAnyNamespace(string body, bool pythonRead)
    {
        var path = TempFile("db.xml", "<?xml version=\"1.0\"?>" + body);
        var current = ProteinDb.IterUniprotXml(path, IngestRules.Current).ToList();
        var python = ProteinDb.IterUniprotXml(path, IngestRules.Python0320).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(current, Is.EqualTo(new[] { ("P1", "MKA") }));
            Assert.That(python.Count, Is.EqualTo(pythonRead ? 1 : 0), "0.32.0 read http:// only");
        });
    }

    /// <summary>A database the search used that reads as empty is refused, whatever made it empty.</summary>
    [TestCase("db.xml", "<?xml version=\"1.0\"?><uniprot xmlns=\"http://uniprot.org/uniprot\"></uniprot>")]
    [TestCase("db.xml", "<?xml version=\"1.0\"?><uniprot xmlns=\"http://uniprot.org/uniprot\"><entry><accession>P1</accession></entry></uniprot>")]
    [TestCase("db.fasta", "")]
    [TestCase("db.fasta", "MKA\n")]  // sequence with no header
    public void ADatabaseThatReadsEmptyIsRefused(string name, string text)
    {
        var path = TempFile(name, text);
        Assert.Multiple(() =>
        {
            Assert.That(() => ProteinDb.ReadDatabase(path, new ProteinSequences(), IngestRules.Current),
                Throws.TypeOf<DataRepo.Bundle.IngestException>().With.Message.Contains("no entries"));
            Assert.That(ProteinDb.ReadDatabase(path, new ProteinSequences(), IngestRules.Python0320), Is.Zero, "0.32.0 read it as empty");
        });
    }

    // ---------------------------------------------------------------- items 11 and 12: one run per run id

    private static Dictionary<string, object?> Fetch(params string[] names) => new()
    {
        ["files"] = names.Select(n => (object?)new Dictionary<string, object?> { ["name"] = n, ["category"] = "RAW", ["sha256"] = "sha-" + n }).ToList(),
    };

    private static Dictionary<string, object?> Qc(params string[] keys) =>
        keys.ToDictionary(k => k, k => (object?)new Dictionary<string, object?> { ["ms2"] = 100L, ["pass"] = true });

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> NoFacts =
        new Dictionary<string, IReadOnlyDictionary<string, object?>>();

    /// <summary>The QC report names the deposited file another way: by its stem, or by another extension.</summary>
    [TestCase("b.raw", "b")]
    [TestCase("b.raw", "b.mzML")]
    public void ADepositedFileAndItsQcNameAreOneRun(string deposited, string qcName)
    {
        var (current, metrics) = Runs.Build("PXD1", Fetch(deposited, "a.raw"), Qc(qcName, "a.raw"), NoFacts, rules: IngestRules.Current);
        var (python, _) = Runs.Build("PXD1", Fetch(deposited, "a.raw"), Qc(qcName, "a.raw"), NoFacts, rules: IngestRules.Python0320);
        var b = current.Single(r => (string)r["run_id"]! == "PXD1:b");
        Assert.Multiple(() =>
        {
            Assert.That(current.Select(r => r["run_id"]), Is.Unique);
            Assert.That(current, Has.Count.EqualTo(2));
            Assert.That(b["file_name"], Is.EqualTo(deposited), "the deposited name");
            Assert.That(b["sha256"], Is.EqualTo("sha-" + deposited));
            Assert.That(b["ms2_spectra"], Is.EqualTo(100L), "its QC entry, found under the other name");
            Assert.That(metrics.Count(m => (string)m["scope_id"]! == "PXD1:b" && (string)m["name"]! == "ms2"), Is.EqualTo(1));
            Assert.That(python.Count(r => (string)r["run_id"]! == "PXD1:b"), Is.EqualTo(2), "0.32.0: two rows, one id");
        });
    }

    [Test]
    public void QcNamesAloneFoldToTheFileName()
    {
        var (runs, _) = Runs.Build("PXD1", null, Qc("b", "b.raw"), NoFacts, rules: IngestRules.Current);
        Assert.That(runs.Select(r => r["file_name"]), Is.EqualTo(new[] { "b.raw" }));
    }

    [TestCase("c.raw", "c.mzML")]
    [TestCase("c.raw", "c.d")]
    public void TwoDepositedFilesWithOneStemAreRefusedByName(string one, string two)
    {
        Assert.That(() => Runs.Build("PXD1", Fetch(one, two), null, NoFacts, rules: IngestRules.Current),
            Throws.TypeOf<DataRepo.Bundle.IngestException>().With.Message.Contains("share the run id PXD1:c"));
    }

    [Test]
    public void EveryRunOfABaseNameGetsItsEnrichment()
    {
        DataRepo.Bundle.Row Run(string file) => new() { ["run_id"] = "PXD1:" + Path.GetFileNameWithoutExtension(file), ["file_name"] = file, ["enrichment"] = null, ["enrichment_source"] = null };
        foreach (var rules in new[] { IngestRules.Current, IngestRules.Python0320 })
        {
            var runs = new List<DataRepo.Bundle.Row> { Run("a.raw"), Run("b.raw"), Run("b.mzML") };
            Runs.AssignEnrichment(runs, "PXD1", ["phospho"], false, [("a", "none"), ("b", "phospho")], rules);
            var filled = runs.Count(r => r["enrichment"] is not null);
            Assert.That(filled, Is.EqualTo(rules == IngestRules.Current ? 3 : 2), rules.ToString());
        }
    }

    // ---------------------------------------------------------------- item 13: no null metric

    [TestCase("mbr_rows")]
    [TestCase("mbr_kept")]
    [TestCase("msms_peaks")]
    [TestCase("kept_over_msms")]
    [TestCase("mbr_fdr_threshold")]
    public void AnMbrKeyWithNoValueWritesNoMetric(string key)
    {
        var mbr = new Dictionary<string, object?> { ["mbr_rows"] = 10L, ["mbr_kept"] = 5L, ["msms_peaks"] = 7L, ["kept_over_msms"] = 0.5, ["mbr_fdr_threshold"] = 0.01 };
        mbr[key] = null;
        var doc = new Dictionary<string, object?> { ["schema"] = "aging-provenance/3", ["mbr"] = mbr };
        var current = Provenance.MetricRows(doc, "PXD1", 3, rules: IngestRules.Current);
        var python = Provenance.MetricRows(doc, "PXD1", 3, rules: IngestRules.Python0320);
        Assert.Multiple(() =>
        {
            Assert.That(current.Select(r => r["name"]), Does.Not.Contain(key));
            Assert.That(current.Count(r => (string)r["name"]! != key), Is.EqualTo(4), "the others are kept");
            Assert.That(current.Select(r => r["value"]), Has.None.Null);
            Assert.That(python.Single(r => (string)r["name"]! == key)["value"], Is.Null, "0.32.0 wrote a null measurement");
        });
    }

    // ---------------------------------------------------------------- items 14 and 15: task files

    private static string Task(string fileName, string body) => TempFile(fileName, body);

    [Test]
    public void ANameWithNoResidueHasResiduesUnspecified()
    {
        var path = Task("Task1SearchTask.toml",
            "TaskType = \"Search\"\n[CommonParameters]\nListOfModsVariable = \"Common Variable\tOxidation on M\t\tCommon Biological\tHydroxylation\t\tX\tA on B on C\"\n");
        var current = SearchParams.ModificationRows([path], "PXD1", (_, _) => null, IngestRules.Current).ToDictionary(r => (string)r["name"]!, r => r["residues"]);
        var python = SearchParams.ModificationRows([path], "PXD1", (_, _) => null, IngestRules.Python0320).ToDictionary(r => (string)r["name"]!, r => r["residues"]);
        Assert.Multiple(() =>
        {
            Assert.That(current["Hydroxylation"], Is.EqualTo("unspecified"));
            Assert.That(python["Hydroxylation"], Is.EqualTo("Hydroxylation"), "0.32.0: the whole name");
            Assert.That(current["Oxidation on M"], Is.EqualTo("M"));
            Assert.That(current["A on B on C"], Is.EqualTo("C"), "the last ' on ' decides");
        });
    }

    [Test]
    public void TheTaskTypeDecidesWhichTasksAreSearches()
    {
        // A glyco task, whose file name says "search", carrying a SearchParameters table MetaMorpheus never reads.
        var glyco = Task("Task2GlycoSearchTask.toml", "TaskType = \"GlycoSearch\"\n[SearchParameters]\nTCAmbiguity = \"RemoveTarget\"\n");
        // A search task whose file name does not say "search".
        var renamed = Task("Task1MyTask.toml", "TaskType = \"Search\"\n[SearchParameters]\nTCAmbiguity = \"RemoveTarget\"\n");
        // A calibration task whose file name does.
        var calibrate = Task("Task0CalibrateSearchTask.toml", "TaskType = \"Calibrate\"\n[SearchParameters]\nTCAmbiguity = \"RenameProtein\"\n");
        Assert.Multiple(() =>
        {
            Assert.That(SearchParams.TcAmbiguity([glyco], IngestRules.Current), Is.EqualTo("RemoveContaminant"), "glyco: DatabaseLoadingEngine's default");
            Assert.That(SearchParams.TcAmbiguity([glyco], IngestRules.Python0320), Is.EqualTo("RemoveTarget"));
            Assert.That(SearchParams.TcAmbiguity([renamed], IngestRules.Current), Is.EqualTo("RemoveTarget"));
            Assert.That(SearchParams.TcAmbiguity([renamed], IngestRules.Python0320), Is.Null);
            Assert.That(SearchParams.TcAmbiguity([calibrate, renamed], IngestRules.Current), Is.EqualTo("RemoveTarget"), "a calibration task is not a search");
            Assert.That(SearchParams.TcAmbiguity([calibrate], IngestRules.Current), Is.Null);
            Assert.That(SearchParams.TcAmbiguity([glyco, renamed], IngestRules.Current), Is.Null, "two tasks that disagree");
        });
    }

    // ---------------------------------------------------------------- item 16: the provenance schema

    [TestCase("aging-provenance/3\n")]
    [TestCase("aging-provenance/٣")]  // an Arabic-Indic three
    public void ASchemaThatIsNotExactlyListedIsRefused(string schema)
    {
        var doc = new Dictionary<string, object?> { ["schema"] = schema };
        Assert.Multiple(() =>
        {
            Assert.That(() => Provenance.SchemaVersion(doc, IngestRules.Current), Throws.TypeOf<DataRepo.Bundle.UnsupportedProvenanceException>());
            Assert.That(Provenance.SchemaVersion(doc, IngestRules.Python0320), Is.EqualTo(3), "0.32.0 read it as layout 3");
            Assert.That(Provenance.SchemaVersion(new Dictionary<string, object?> { ["schema"] = "aging-provenance/3" }, IngestRules.Current), Is.EqualTo(3));
        });
    }

    [Test]
    public void ANullSchemaIsNotTheWordNone()
    {
        var doc = new Dictionary<string, object?> { ["schema"] = null, ["stage"] = "qc" };
        Assert.Multiple(() =>
        {
            Assert.That(Provenance.RecordRow(doc, "PXD1", "02b_qc", "p", "00", IngestRules.Current)["provenance_schema"], Is.Null, "unknown, never a value the record did not write");
            Assert.That(Provenance.RecordRow(new Dictionary<string, object?> { ["stage"] = "qc" }, "PXD1", "02b_qc", "p", "00", IngestRules.Current)["provenance_schema"], Is.Null, "as an absent one");
            Assert.That(Provenance.RecordRow(doc, "PXD1", "02b_qc", "p", "00", IngestRules.Python0320)["provenance_schema"], Is.EqualTo("None"));
        });
    }

    // ---------------------------------------------------------------- PXR-R21: a parsed name holding ':'

    /// <summary>The <c>ptm_sites</c> rows of one target PSM on P12345, whose peptide <c>AEGSDCIMLK</c> starts at
    /// residue 5, so the peptide's C6 is the protein's C10.</summary>
    private static List<DataRepo.Bundle.Row> Sites(string fullSequence, IngestRules rules)
    {
        var columns = new Dictionary<string, List<object?>>
        {
            ["full_sequence"] = [fullSequence],
            ["accession"] = ["P12345"],
            ["q_value"] = [0.001],
            ["decoy_contam_target"] = ["T"],
        };
        var sequences = new ProteinSequences();
        sequences.Add("P12345", "MKLAAEGSDCIMLK");
        return Identifications.PtmSiteRows(columns, "PXD000001", new ProformaCache(new ModRegistry(), rules), sequences);
    }

    /// <summary>Unimod names every isotope label with a <c>:</c> (<c>NEM:2H(5)</c>, <c>Label:13C(6)</c>,
    /// <c>Dimethyl:2H(4)</c>). A token that parsed keys its site, and the key splits back into its name.</summary>
    [TestCase("NEM:2H(5)")]
    [TestCase("Label:13C(6)15N(2)")]
    [TestCase("Dimethyl:2H(4)")]
    public void AParsedNameHoldingAColonKeysItsSite(string unimodName)
    {
        var name = $"{unimodName} on C";
        var full = $"AEGSDC[Unimod:{name}]IMLK";
        var site = Sites(full, IngestRules.Current).Single();
        var key = (string)site["ptm_site_id"]!;
        Assert.Multiple(() =>
        {
            Assert.That(key, Is.EqualTo($"PXD000001:P12345:C10:{name}"));
            Assert.That(key.Split(':', 4)[3], Is.EqualTo(name), "the name is the key's last part, whole");
            Assert.That(site["modification_name"], Is.EqualTo(name));
            Assert.That(site["position"], Is.EqualTo(10L));
            Assert.That(() => Sites(full, IngestRules.Python0320),
                Throws.TypeOf<DataRepo.Bundle.IngestException>().With.Message.Contains("ptm_site_id separator"), "0.32.0 refused it");
        });
    }

    /// <summary>The guard's purpose survives: a token that is not <c>Category:Name on X</c> keeps its whole text,
    /// category included, and that is not a name to key a site on.</summary>
    [Test]
    public void AnUnparsedTokenHoldingAColonIsStillRefused()
    {
        Assert.That(() => Sites("AEGSDC[Common Fixed:Carbamidomethyl]IMLK", IngestRules.Current),
            Throws.TypeOf<DataRepo.Bundle.IngestException>().With.Message.Contains("is not `Category:Name on X`"));
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

    // ---------------------------------------------------------------- G101: one accession, two sequences

    // The proteome's keratin and the contaminant panel's older version of it, as O76011 and P19013 are: PEPCIDEK at 5
    // in the first (its C is residue 8) and at 9 in the second (residue 12).
    private const string KeratinTarget = "MAAAPEPCIDEKGGG";
    private const string KeratinPanel = "MAAAQQQQPEPCIDEKGGG";

    /// <summary>K1 from a proteome and from a second database (by default the contaminant panel), and P2, which
    /// carries the same peptide at 2 (its C is residue 5), from the proteome only.</summary>
    private static ProteinSequences Keratin(string panel = KeratinPanel, string panelFile = "contaminants.fasta")
    {
        var sequences = new ProteinSequences();
        ProteinDb.ReadDatabase(TempFile("proteome.fasta", ">sp|K1|K1_HUMAN one\n" + KeratinTarget + "\n>sp|P2|P2_HUMAN two\nMPEPCIDEKW\n"), sequences);
        ProteinDb.ReadDatabase(TempFile(panelFile, ">sp|K1|K1_HUMAN one\n" + panel + "\n"), sequences);
        return sequences;
    }

    private static List<DataRepo.Bundle.Row> KeratinSites(ProteinSequences sequences, string accession, string range, OrderedDictionary<string, long>? unplaced = null)
    {
        var columns = new Dictionary<string, List<object?>>
        {
            ["full_sequence"] = ["PEPC[Common Fixed:Carbamidomethyl on C]IDEK"],
            ["accession"] = [accession],
            ["start_and_end_residues_in_parent_sequence"] = [range],
            ["q_value"] = [0.001],
            ["decoy_contam_target"] = ["T"],
        };
        return Identifications.PtmSiteRows(columns, "PXD1", new ProformaCache(new ModRegistry(), IngestRules.Current), sequences, unplaced);
    }

    private static IEnumerable<string> Positions(List<DataRepo.Bundle.Row> sites) =>
        sites.Select(s => $"{s["protein_accession"]}:{s["position"]}");

    [TestCase("RemoveContaminant", KeratinTarget, "target")]
    [TestCase("RemoveTarget", KeratinPanel, "contaminant")]
    public void TheCopyTheSearchKeptIsTheOneSitesArePlacedOn(string tc, string kept, string kind)
    {
        var sequences = Keratin();
        Assert.That(Positions(KeratinSites(sequences, "K1", "[5 to 12]")), Is.EquivalentTo(new[] { "K1:8", "K1:12" }),
            "before the choice, one PSM gave two sites 4 residues apart: the defect");
        sequences.KeepSearched(tc);
        Assert.Multiple(() =>
        {
            Assert.That(sequences.Get("K1"), Is.EqualTo(new[] { kept }));
            Assert.That(sequences.Resolved["K1"], Is.EqualTo(kind));
            Assert.That(sequences.Ambiguous, Is.Empty);
            Assert.That(Positions(KeratinSites(sequences, "K1", "[5 to 12]")), Is.EqualTo(new[] { kind == "target" ? "K1:8" : "K1:12" }));
            Assert.That(sequences.DatabaseStatus("K1"), Is.EqualTo("both"), "the contaminant label is still the setting's to decide");
        });
    }

    /// <summary>Where the search held one copy and nothing says which, no site is placed against either.</summary>
    [TestCase(null)]
    [TestCase("RenameProtein")]
    public void AnUnnamedCopyPlacesNoSite(string? tc)
    {
        var sequences = Keratin();
        sequences.KeepSearched(tc);
        var unplaced = new OrderedDictionary<string, long>(StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(sequences.Get("K1"), Is.Empty);
            Assert.That(sequences.Ambiguous, Is.EqualTo(new[] { "K1" }));
            Assert.That(Positions(KeratinSites(sequences, "K1|P2", "[5 to 12]|[2 to 9]", unplaced)), Is.EqualTo(new[] { "P2:5" }),
                "the other protein of the PSM keeps its site");
            Assert.That(unplaced["ambiguous_sequence"], Is.EqualTo(1));
            Assert.That(Positions(KeratinSites(sequences, "K1", "[5 to 12]")), Is.EqualTo(new[] { "K1:8" }),
                "a PSM on K1 alone falls back to the producer's own span, which is the copy it matched");
        });
    }

    [Test]
    public void TwoCopiesFromDatabasesOfOneKindAreAmbiguous()
    {
        // MetaMorpheus keeps the copy with the most annotated modifications, which a sequence read cannot see.
        var sequences = Keratin(panelFile: "isoforms.fasta");
        sequences.KeepSearched("RemoveContaminant");
        Assert.Multiple(() =>
        {
            Assert.That(sequences.Ambiguous, Is.EqualTo(new[] { "K1" }));
            Assert.That(sequences.Resolved, Is.Empty);
        });
    }

    // ---------------------------------------------------------------- schema 0.0.16: sequence_window (P30), split keys (G95)

    [TestCase(10, "LAAEGSDCIMLK___")]   // residues 3-17: 7 before the C, the C, 4 after, 3 past the end
    [TestCase(1, "_______MKLAAEGS")]    // the first residue, as a protein N-terminal site takes it
    [TestCase(14, "GSDCIMLK_______")]   // the last residue, as a protein C-terminal site takes it
    public void ASiteWindowIsFifteenResiduesWithUnderscoresPastTheEnds(long position, string window)
    {
        var got = Identifications.SequenceWindow("MKLAAEGSDCIMLK", position);
        Assert.Multiple(() =>
        {
            Assert.That(got, Is.EqualTo(window));
            Assert.That(got![7], Is.EqualTo("MKLAAEGSDCIMLK"[(int)position - 1]), "the site's residue is the centre");
        });
    }

    [TestCase(0)]
    [TestCase(15)]
    public void APositionOutsideTheSequenceHasNoWindow(long position) =>
        Assert.That(Identifications.SequenceWindow("MKLAAEGSDCIMLK", position), Is.Null);

    [Test]
    public void AnAccessionWithoutOneSequenceHasNoWindow()
    {
        var sequences = Keratin();
        sequences.KeepSearched(null);  // K1 ambiguous: no sequence
        var sites = new List<DataRepo.Bundle.Row>
        {
            new() { ["protein_accession"] = "K1", ["position"] = 8L },
            new() { ["protein_accession"] = "P2", ["position"] = 5L },
            new() { ["protein_accession"] = "NONE", ["position"] = 1L },
        };
        Identifications.AddSequenceWindows(sites, sequences);
        Assert.That(sites.Select(s => s["sequence_window"]), Is.EqualTo(new object?[] { null, "___MPEPCIDEKW__", null }));
    }

    /// <summary>ptmQtl S5 (D35): a protein N-terminal site takes residue 1's window, also when it is keyed on residue 2
    /// after the initiator Met was removed; any other site keyed on residue 2 takes residue 2's.</summary>
    [Test]
    public void AProteinNTerminalSiteTakesResidueOnesWindow()
    {
        var sequences = Keratin();
        sequences.KeepSearched(null);
        var sites = new List<DataRepo.Bundle.Row>
        {
            new() { ["protein_accession"] = "P2", ["position"] = 2L, ["site_type"] = "protein_n_term" },
            new() { ["protein_accession"] = "P2", ["position"] = 1L, ["site_type"] = "protein_n_term" },
            new() { ["protein_accession"] = "P2", ["position"] = 2L, ["site_type"] = "residue" },
        };
        Identifications.AddSequenceWindows(sites, sequences);
        Assert.That(sites.Select(s => s["sequence_window"]), Is.EqualTo(new object?[] { "_______MPEPCIDE", "_______MPEPCIDE", "______MPEPCIDEK" }));
    }

    /// <summary>A split deposit's search is its own dataset, and its spectra are still the deposit's (G95).</summary>
    [TestCase("PXD073999", "PXD073999")]
    [TestCase("PXD073999-g2", "PXD073999")]
    [TestCase("MSV000012345-part_b", "MSV000012345")]
    public void AUsiNamesTheDepositsAccession(string datasetId, string accession) =>
        Assert.That(Usi.Mint(datasetId, "run1", 7, "PEPTIDE", 2), Is.EqualTo($"mzspec:{accession}:run1:scan:7:PEPTIDE/2"));

    // ---------------------------------------------------------------- G26: modifications from mzLib's own loaders

    /// <summary>A MetaMorpheus install holding only <c>Mods/</c> files, written in MetaMorpheus's own format.</summary>
    private static string ModsInstall(params (string File, string Text)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "datarepo-g26-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));
        foreach (var (file, text) in files)
            File.WriteAllText(Path.Combine(dir, "Mods", file), text, new System.Text.UTF8Encoding(false));
        return dir;
    }

    private static string ModBlock(string id, string target, string type, string formula, int? unimod) =>
        $"ID   {id}\nTG   {target}\nPP   Anywhere.\nMT   {type}\nCF   {formula}\n" + (unimod is null ? "" : $"DR   Unimod; {unimod}.\n") + "//\n";

    [Test]
    public void AModificationResolvesByItsExactToken()
    {
        var install = ModsInstall(("Mods.txt", ModBlock("Carbamidomethyl", "C", "Common Fixed", "C2H3N1O1", 4)
            + ModBlock("Hydroxybutyrylation", "K", "Common Biological", "C4H6O2", null)));
        var registry = ModRegistry.FromMzLib(install);
        var fixedC = registry.Lookup("Carbamidomethyl on C", "C", IngestRules.Current, "Common Fixed");
        var noAccession = registry.Lookup("Hydroxybutyrylation on K", "K", IngestRules.Current, "Common Biological");
        Assert.Multiple(() =>
        {
            Assert.That(fixedC?.UnimodCurie, Is.EqualTo("UNIMOD:4"));
            Assert.That(fixedC!.MonoisotopicMass, Is.EqualTo(57.021464).Within(1e-6), "the mass mzLib computes from the formula");
            Assert.That(noAccession?.UnimodCurie, Is.Null);
            Assert.That(noAccession!.MonoisotopicMass, Is.EqualTo(86.036779).Within(1e-6), "the file reader had no mass for it");
            Assert.That(registry.Lookup("Carbamidomethyl on C", "C", IngestRules.Current, "Common Variable"), Is.Null, "another category is another token");
            Assert.That(registry.Lookup("Carbamidomethyl on C", "C", IngestRules.Current), Is.Null, "no category, no token");
            Assert.That(registry.Lookup("Carbamidomethyl", "C", IngestRules.Current, "Common Fixed"), Is.Null, "nothing is guessed from a stem");
            Assert.That(registry.Loader, Does.StartWith("mzLib "));
        });
    }

    /// <summary>MetaMorpheus keeps the first modification loaded under an <c>IdWithMotif</c>, whatever its category.</summary>
    [Test]
    public void TheFirstFileToLoadAnIdWins()
    {
        var install = ModsInstall(
            ("b.txt", ModBlock("Thing", "K", "Second", "C2H2O1", 2)),
            ("a.txt", ModBlock("Thing", "K", "First", "C2H2O1", 1)));
        var registry = ModRegistry.FromMzLib(install);
        Assert.Multiple(() =>
        {
            Assert.That(registry.Lookup("Thing on K", "K", IngestRules.Current, "First")?.UnimodCurie, Is.EqualTo("UNIMOD:1"));
            Assert.That(registry.Lookup("Thing on K", "K", IngestRules.Current, "Second"), Is.Null, "MetaMorpheus skipped it, so no search wrote it");
            Assert.That(registry.Count, Is.EqualTo(1));
        });
    }

    /// <summary>An install with a <c>ptmlist.txt</c> of two charged UniProt entries and the PSI-MOD terms naming their
    /// charges, in the real files' grammar; optionally its own mzLib assembly.</summary>
    private static string ChargedInstall(bool withOwnMzLib)
    {
        var dir = Path.Combine(Path.GetTempPath(), "datarepo-g26c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        string Term(string id, string charge) =>
            $"<term><id>{id}</id><name>{id}</name><xref_analog><acc></acc><dbname>FormalCharge</dbname><name>{charge}</name></xref_analog></term>";
        File.WriteAllText(Path.Combine(dir, "Data", "PSI-MOD.obo.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><obo>" + Term("MOD:90001", "1-") + Term("MOD:90002", "1+") + "</obo>");
        string Entry(string id, string formula, string mass, string psimod, string extra = "") =>
            $"ID   {id}\nAC   PTM-9{psimod[^1]}\nFT   MOD_RES\nTG   Lysine.\nPA   Amino acid side chain.\nPP   Anywhere.\n"
            + $"CF   {formula}\nMM   {mass}\nDR   PSI-MOD; {psimod}.\n{extra}//\n";
        File.WriteAllText(Path.Combine(dir, "Data", "ptmlist.txt"),
            Entry("Minus thing", "C2 H1 O2", "57.000000", "MOD:90001")
            + Entry("Plus thing", "C3 H7", "43.054227", "MOD:90002", "DR   Unimod; 37.\n"));
        if (withOwnMzLib)
            File.Copy(typeof(UsefulProteomicsDatabases.Loaders).Assembly.Location, Path.Combine(dir, "UsefulProteomicsDatabases.dll"));
        return dir;
    }

    /// <summary>A charge mzLib's sign fix (#1299) did not change is applied as the search applied it; a negative one
    /// is applied only for an install whose own mzLib read its sign, and otherwise the mass is withheld.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public void AChargedMassIsTheOneTheSearchUsed(bool withOwnMzLib)
    {
        const double proton = 1.007276466812;
        var registry = ModRegistry.FromMzLib(ChargedInstall(withOwnMzLib));
        var plus = registry.Lookup("Plus thing on K", "K", IngestRules.Current, "UniProt");
        var minus = registry.Lookup("Minus thing on K", "K", IngestRules.Current, "UniProt");
        Assert.Multiple(() =>
        {
            Assert.That(plus!.MonoisotopicMass, Is.EqualTo(43.054227 - proton).Within(1e-6), "+1: one proton off UniProt's cation mass");
            Assert.That(plus.UnimodCurie, Is.EqualTo("UNIMOD:37"));
            Assert.That(minus, Is.Not.Null, "the token still resolves");
            if (withOwnMzLib)
            {
                Assert.That(minus!.MonoisotopicMass, Is.EqualTo(57.000000 + proton).Within(1e-6), "-1, read with its sign");
                Assert.That(registry.MassesWithheld, Is.Empty);
            }
            else
            {
                Assert.That(minus!.MonoisotopicMass, Is.Null, "an install whose mzLib may have read -1 as +1 searched another mass");
                Assert.That(registry.MassesWithheld.Keys, Is.EqualTo(new[] { "UniProt:Minus thing on K" }));
                Assert.That(registry.MassesWithheld.Values.Single(), Does.Contain("MOD:90001").And.Contain("#1299"));
            }
        });
    }

    [Test]
    public void AMissingInstallGivesAnEmptyRegistry() =>
        Assert.That(ModRegistry.FromMzLib(Path.Combine(Path.GetTempPath(), "no-such-install-" + Guid.NewGuid().ToString("N"))).Count, Is.Zero);

    /// <summary>What G26 changes on aging's real install, measured over every token in its store (2026-10-19).</summary>
    [Test, Category("RealData")]
    public void TheRealInstallResolvesWhatTheFileReaderCouldNot()
    {
        const string install = @"F:\aging_data\mm_settings\1.1.12";
        if (!Directory.Exists(install)) Assert.Ignore($"{install} is not on this machine");
        var mzlib = ModRegistry.FromMzLib(install);
        var files = ModRegistry.FromMetaMorpheus(install);
        ModEntry? Mz(string category, string name) => mzlib.Lookup(name, null, IngestRules.Current, category);
        Assert.Multiple(() =>
        {
            Assert.That(Mz("Unimod", "Nethylmaleimide on C")?.UnimodCurie, Is.EqualTo("UNIMOD:108"), "unimod.xml, which the file reader never read");
            Assert.That(files.Lookup("Nethylmaleimide on C", "C", IngestRules.Current), Is.Null);
            Assert.That(Mz("UniProt", "N6,N6,N6-trimethyllysine on K")!.MonoisotopicMass, Is.EqualTo(42.046951).Within(1e-6), "formal charge removed");
            Assert.That(files.Lookup("N6,N6,N6-trimethyllysine on K", "K", IngestRules.Current)!.MonoisotopicMass, Is.EqualTo(43.054227).Within(1e-6), "UniProt's MM, a proton heavier");
            Assert.That(Mz("Common Biological", "Hydroxybutyrylation on K")!.MonoisotopicMass, Is.EqualTo(86.036779).Within(1e-6));
            Assert.That(Mz("Common Fixed", "Carbamidomethyl on C")?.UnimodCurie, Is.EqualTo("UNIMOD:4"));
            Assert.That(Mz("Trypsin Digested", "GG (Ubiquitination Site) on K")?.UnimodCurie, Is.EqualTo("UNIMOD:121"));
            Assert.That(mzlib.MassesWithheld, Is.Empty, "1.1.12 ships mzLib 1.0.594, which reads a charge's sign");
            const string older = @"F:\aging_data\mm_settings\1.1.11";
            if (Directory.Exists(older))
                Assert.That(ModRegistry.FromMzLib(older).MassesWithheld, Is.Empty,
                    "1.1.11 ships mzLib 1.0.591, from before #1299, and none of its charged entries is negative");
        });
    }

    [Test]
    public void AnIdenticalCopyIsNoChoice()
    {
        var sequences = Keratin(panel: KeratinTarget);
        sequences.KeepSearched(null);
        Assert.Multiple(() =>
        {
            Assert.That(sequences.Get("K1"), Is.EqualTo(new[] { KeratinTarget }));
            Assert.That(sequences.Resolved, Is.Empty);
            Assert.That(sequences.Ambiguous, Is.Empty);
        });
    }
}
