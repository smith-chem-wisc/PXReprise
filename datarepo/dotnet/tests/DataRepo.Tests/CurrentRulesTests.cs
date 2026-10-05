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
        var current = SearchParams.ModificationRows([path], "PXD1", _ => null, IngestRules.Current).ToDictionary(r => (string)r["name"]!, r => r["residues"]);
        var python = SearchParams.ModificationRows([path], "PXD1", _ => null, IngestRules.Python0320).ToDictionary(r => (string)r["name"]!, r => r["residues"]);
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
