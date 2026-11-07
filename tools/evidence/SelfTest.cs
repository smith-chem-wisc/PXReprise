// pxr-evidence selftest: the figure-claim rules, offline (no key, no network). Exit 0 when every case holds.
using Readers;
using SampleEvidenceModel;

internal static class SelfTest
{
    internal static int Run()
    {
        var input = new PublicationInput("PXD000001", "Hippocampus of young and aged mice.",
            "## Methods\nProteins were digested with trypsin and analysed on an Orbitrap.\nAnimals: male , Fischer F344 rats were housed in pairs.\n"
            + "Sets: young vehicle\nold vehicle", Array.Empty<SupplementTable>(),
            new[] { "Y1.raw", "Y2.raw", "A1.raw", "A2.raw" }, Array.Empty<SdrfEvidence>());
        var shown = new[] { new Attachment("[fig:F1]", "image", "image/jpeg", new byte[] { 1 }, "is Figure 1 of the paper.") };
        const string json = """
        {
          "transcripts": [
            { "figure": "[fig:F1]", "text": "Young (3 mo) n = 6 | Aged (24 mo) n = 6 | male C57BL/6J mice | CA1" },
            { "figure": "[fig:F9]", "text": "female Sprague Dawley rats" }
          ],
          "claims": [
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[strain]", "value": "C57BL/6J", "source": "figure", "quote": "[fig:F1] male C57BL/6J mice", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[sex]", "value": "male", "source": "figure", "quote": "[fig:F1] male C57BL/6J mice", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[organism part]", "value": "dentate gyrus", "source": "figure", "quote": "[fig:F1] dentate gyrus slices", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[strain]", "value": "Sprague Dawley", "source": "figure", "quote": "[fig:F9] female Sprague Dawley rats", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[organism part]", "value": "hippocampus", "source": "pride record", "quote": "Hippocampus of young and aged mice", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[strain]", "value": "Fischer F344", "source": "paper", "quote": "male, Fischer F344 rats were housed", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[strain]", "value": "Fischer F345", "source": "paper", "quote": "male, Fischer F345 rats were housed", "confidence": "likely" },
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[treatment]", "value": "vehicle", "source": "paper", "quote": "young vehicle, old vehicle", "confidence": "likely" }
          ],
          "design": { "groups": [ { "name": "Young", "samples": 6, "quote": "[fig:F1] Young (3 mo) n = 6" } ],
                      "plexes": 0, "plexes_quote": "", "technical_replicates": 0, "technical_replicates_quote": "",
                      "fractions_per_sample": 0, "fractions_quote": "", "runs_stated": 0, "runs_quote": "" }
        }
        """;
        var r = RichReader.Interpret(json, input, shown, "end_turn");
        var e = r.Result.Evidence;
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }
        SdrfEvidence? Find(string column, string value) => e.FirstOrDefault(x => x.Column == column && x.Value == value);

        var strain = Find("characteristics[strain]", "C57BL/6J");
        Check(strain is { Source: "figure" } && strain.Locator.StartsWith("[fig:F1]", StringComparison.Ordinal),
            $"a figure claim quoting its transcript is kept, source figure, located at its figure ({strain?.Source} {strain?.Locator})");
        Check(Find("characteristics[sex]", "male") is { Source: "figure", Confidence: SdrfEvidenceConfidence.Likely }, "a second claim from the same transcript is kept, likely");
        Check(Find("characteristics[organism part]", "dentate gyrus") is null && r.Result.Rejected.Any(x => x.Contains("dentate gyrus") && x.Contains("not in the given text")),
            "a figure claim whose words are in no transcript is rejected");
        Check(r.Transcripts.Count == 1 && r.Result.Rejected.Any(x => x.Contains("F9") && x.Contains("not a label")), "a transcript of a figure that was not shown is dropped");
        Check(Find("characteristics[strain]", "Sprague Dawley") is null, "a claim quoting that dropped transcript is rejected");
        Check(Find("characteristics[organism part]", "hippocampus") is { Source: "pride record" }, "a text claim is judged as before");
        Check(r.Result.Design is { } d && d.Groups.Any(g => g.Name == "Young" && g.Samples == 6), "a design number quoted from a transcript is kept");
        Check(r.FigureClaims == 2, $"two figure claims counted ({r.FigureClaims})");
        Check(Find("characteristics[strain]", "Fischer F344") is { Source: "paper" } && r.Repaired == 1,
            $"a quote off only by a space before a comma is repaired and kept ({r.Repaired} repaired)");
        Check(Find("characteristics[strain]", "Fischer F345") is null, "a repaired-looking quote with a changed word is still rejected");
        // mzLib's exact check reads a line break as a space; the repair never joins lines (two rows, two paragraphs).
        Check(Find("characteristics[treatment]", "vehicle") is null, "a quote needing repair across two lines is not repaired");

        // Margin line numbers leave a PDF's text; a number inside the text stays.
        string pdf = Path.Combine(Path.GetTempPath(), $"pxr-evidence-selftest-{Environment.ProcessId}.pdf");
        try
        {
            var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            string[] lines =
            {
                "Cartilage pieces were digested with collagenase overnight at 37 degrees and protease inhibitor",
                "cocktail was added. Biologically independent protein preparations derived from two",
                "mice were performed for each sample set: young vehicle, young senolytic treated, old vehicle,",
                "old senolytic treated. Initial protein preparations were then processed with salt.",
            };
            for (int k = 0; k < lines.Length; k++)
            {
                page.AddText((90 + k).ToString(System.Globalization.CultureInfo.InvariantCulture), 9, new UglyToad.PdfPig.Core.PdfPoint(30, 700 - 14 * k), font);
                page.AddText(lines[k], 10, new UglyToad.PdfPig.Core.PdfPoint(72, 700 - 14 * k), font);
            }
            File.WriteAllBytes(pdf, builder.Build());
            string text = SupplementContent.PdfText(pdf);
            Check(text.Contains("derived from two mice were performed for each sample set", StringComparison.Ordinal) && !text.Contains(" 92 ", StringComparison.Ordinal),
                "margin line numbers are left out of a PDF's text");
            Check(text.Contains("at 37 degrees", StringComparison.Ordinal), "a number inside the text stays");

            // A two-column page whose content stream interleaves the columns line by line, as published PDFs often do: the
            // text must come back in reading order, or a quote spanning two lines of one column is "not in the given text"
            // (2026-11-07: 147 of the re-read's rejected quotes, e.g. PXD047292's "young (3-mo), middle-aged (12-mo) and
            // aged (23-mo) female rats").
            var b2 = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            var f2 = b2.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            var p2 = b2.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            string[] left =
            {
                "Tissue containing the CA1 region of the", "dorsal hippocampus was collected from", "young (3-mo), middle-aged (12-mo) and",
                "aged (23-mo) female rats, six per group.", "Proteins were digested with trypsin and", "each sample was analyzed twice, giving",
                "technical duplicates for every animal.", "Samples were kept frozen until use.",
            };
            string[] right =
            {
                "Spectra were searched against the rat", "proteome with a precursor tolerance of", "ten parts per million and a fragment",
                "tolerance of twenty parts per million.", "Peptides were filtered at one percent", "false discovery rate and quantified",
                "by their summed intensities in each run.", "Statistics used a two sided test.",
            };
            for (int k = 0; k < left.Length; k++)
            {
                p2.AddText(left[k], 10, new UglyToad.PdfPig.Core.PdfPoint(50, 760 - 14 * k), f2);
                p2.AddText(right[k], 10, new UglyToad.PdfPig.Core.PdfPoint(320, 760 - 14 * k), f2);
            }
            File.WriteAllBytes(pdf, b2.Build());
            string two = System.Text.RegularExpressions.Regex.Replace(SupplementContent.PdfText(pdf), @"\s+", " ");
            Check(two.Contains("dorsal hippocampus was collected from young (3-mo), middle-aged (12-mo) and aged (23-mo) female rats", StringComparison.Ordinal),
                "a two-column PDF's left column reads in order, across its lines");
            Check(two.Contains("proteome with a precursor tolerance of ten parts per million", StringComparison.Ordinal),
                "and so does its right column");
        }
        finally { File.Delete(pdf); }

        // D65 (8), the user 2026-11-07: "we create an independant sdrf and compare it to the community sdrf". With --independent no
        // deposited or community SDRF reaches the rules or the model: one named so, or one whose table has SDRF's own columns.
        Check(new[] { "PXD008344_community_annotated.sdrf.tsv", "BV2_Aldh1l1_Camk2a_SDRF.tsv", "sdrf.tsv" }.All(SupplementContent.NamedLikeSdrf),
            "a community SDRF, a submitter's SDRF and a bare sdrf.tsv are recognised by name");
        Check(!new[] { "Supplementary Table S1.xlsx", "s_study.txt", "README.txt" }.Any(SupplementContent.NamedLikeSdrf), "other supplements are not");
        static SupplementTable Table(params string[] header) =>
            new("t.tsv", "", "", header, new[] { (IReadOnlyList<string>)header.Select(_ => "x").ToArray() }, new[] { 2 }, false);
        Check(SupplementContent.ShapedLikeSdrf(Table("source name", "characteristics[organism]", "assay name", "comment[data file]")),
            "a table with SDRF's source name and comment[data file] columns is an SDRF, whatever the file is called");
        Check(!SupplementContent.ShapedLikeSdrf(Table("Source Name", "Characteristics[Organism]", "Protocol REF", "Sample Name")),
            "an ISA-Tab sample sheet (no comment[data file]) is the deposit's evidence, not an SDRF");
        Check(!SupplementContent.ShapedLikeSdrf(Table("Sample", "Group", "Raw file")), "a plain sample sheet is not an SDRF");
        static UsefulProteomicsDatabases.PrideArchiveFile File1(string name, string cat) =>
            new() { FileName = name, FileSizeBytes = 1000, FileCategory = new() { Value = cat } };
        var listed = new[] { File1("PXD000001_community_annotated.sdrf.tsv", "EXPERIMENTAL DESIGN"), File1("Table_S1.xlsx", "OTHER"), File1("a.raw", "RAW") };
        var rowsOut = new List<InputRow>();
        var kept = SupplementContent.DepositFiles(listed, 25_000_000, rowsOut, leaveOutSdrf: true);
        Check(kept.Select(f => f.FileName).SequenceEqual(new[] { "Table_S1.xlsx" }) && rowsOut.Any(x => x.Name.EndsWith(".sdrf.tsv") && x.UsedAs.StartsWith("left out")),
            "independent: the deposit's SDRF is not fetched, and inputs says it was left out");
        Check(SupplementContent.DepositFiles(listed, 25_000_000, new List<InputRow>(), leaveOutSdrf: false).Count == 2, "without --independent it is read as before");
        Check(Deposit.Name("draft.sdrf.tsv", ".independent") == "draft.independent.sdrf.tsv" && Deposit.Name("model.json", ".independent") == "model.independent.json"
              && Deposit.Name("evidence.model.tsv", ".independent") == "evidence.model.independent.tsv" && Deposit.Name("model.json", "") == "model.json",
            "independent outputs get their own names, so a reading paid for with the SDRF in view is never reused or overwritten");
        // D65 step 3 (the user, 2026-11-07: "make an effort to resolve differences. unresolved differences get reported"): how a
        // difference between our independent draft and a community SDRF is sorted before anything is asked.
        Check(SdrfComparison.Classify("lung", "lungs") == DiffKind.Wording && SdrfComparison.Classify("Alzheimer's disease", "alzheimer disease") == DiffKind.Wording
              && SdrfComparison.Classify("WT", "wild type") == DiffKind.Wording && SdrfComparison.Classify("label free sample", "label free") == DiffKind.Wording,
            "plural, possessive, wild type and the label-free spelling are wording, not conflicts");
        Check(SdrfComparison.Classify("heart", "heart left ventricle") == DiffKind.MoreSpecificTheirs && SdrfComparison.Classify("LTQ Orbitrap XL", "LTQ Orbitrap") == DiffKind.MoreSpecificOurs,
            "one value inside the other is the more specific one, either way round");
        Check(SdrfComparison.Classify("brain", "hippocampal formation") == DiffKind.Conflict && SdrfComparison.Classify("cardiovascular system disease", "normal") == DiffKind.Conflict,
            "different words are a conflict (ontology parents wait for terms, G76)");
        Check(SdrfComparison.Classify("not available", "liver") == DiffKind.OursMissing && SdrfComparison.Classify("liver", "") == DiffKind.TheirsMissing
              && SdrfComparison.Classify("NT=homo sapiens;AC=NCBITaxon:9606", "Homo sapiens") == DiffKind.Agree,
            "an empty or not-available side is an omission; an ontology-tagged value compares by its name");
        Check(SdrfComparison.SameInstrument("Q Exactive HF-X", "Q Exactive HF-X Orbitrap") && SdrfComparison.SameInstrument("NT=Orbitrap Exploris 480;AC=MS:1003028", "Orbitrap Exploris 480")
              && !SdrfComparison.SameInstrument("Q Exactive HF", "Q Exactive HF-X Orbitrap") && !SdrfComparison.SameInstrument("LTQ Orbitrap", "LTQ Orbitrap XL")
              && !SdrfComparison.SameInstrument("Orbitrap Fusion Lumos", "Orbitrap Eclipse"),
            "an SDRF instrument matches the raw header's model only when every distinguishing word does (HF is not HF-X, LTQ Orbitrap is not XL)");
        Check(SdrfComparison.Grouping(new[] { "1", "2", "1", "2" }, new[] { "7", "8", "7", "8" }) == DiffKind.Numbering
              && SdrfComparison.Grouping(new[] { "1", "1", "2", "2" }, new[] { "1", "2", "1", "2" }) == DiffKind.Grouping,
            "replicate numbers that group the same files the same way are numbering; different groups are a grouping difference");
        Check(SdrfComparison.Grouping(new[] { "1", "1", "1" }, new[] { "1", "2", "3" }) == DiffKind.OursMissing
              && SdrfComparison.Grouping(new[] { "1", "2", "3" }, new[] { "1", "1", "1" }) == DiffKind.TheirsMissing,
            "a side that gives every file the same number numbers nothing: an omission, not a grouping conflict");
        Check(SdrfComparison.Compared("characteristics[disease]") && SdrfComparison.Compared("comment[instrument]") && SdrfComparison.Compared("source name")
              && !new[] { "comment[organism part source]", "comment[characteristics source]", "comment[organism part source method]", "comment[organism part source reference]",
                          "comment[sdrf template]", "comment[sdrf annotation tool]", "comment[data file]" }.Any(SdrfComparison.Compared),
            "facts are compared; an SDRF's bookkeeping (our source columns, their template and tool) is not");
        var asked = new[] { new SdrfComparison.Question(0, "characteristics[disease]", "cardiovascular system disease", "normal", new[] { "c1.raw" }) };
        const string evidenceText = "Hearts were taken from control mice (normal) and from mice after TAC.";
        var v1 = SdrfComparison.Verdicts("""{"answers":[{"id":0,"choice":"value 2","quote":"control mice (normal)","why":"c1 is a control"}]}""", asked, evidenceText, swapped: new[] { false });
        Check(v1[0] is { Resolved: true, Side: "theirs" }, $"a choice quoted from the text resolves it, and the unlabelled value 2 maps back to theirs ({v1[0].Side})");
        var v2 = SdrfComparison.Verdicts("""{"answers":[{"id":0,"choice":"value 1","quote":"control mice (normal)","why":""}]}""", asked, evidenceText, swapped: new[] { true });
        Check(v2[0] is { Resolved: true, Side: "theirs" }, "with the two shown the other way round, value 1 is theirs");
        var v3 = SdrfComparison.Verdicts("""{"answers":[{"id":0,"choice":"value 1","quote":"all mice had heart disease","why":""}]}""", asked, evidenceText, swapped: new[] { false });
        Check(v3[0] is { Resolved: false } && v3[0].Note.Contains("not in the text"), "a quote not in the text resolves nothing");
        var v4 = SdrfComparison.Verdicts("""{"answers":[{"id":0,"choice":"cannot tell","quote":"","why":"no group is named"}]}""", asked, evidenceText, swapped: new[] { false });
        Check(v4[0] is { Resolved: false } && SdrfComparison.Verdicts("""{"answers":[]}""", asked, evidenceText, new[] { false })[0] is { Resolved: false },
            "cannot tell, or no answer, stays unresolved for the user");
        var contacts = SdrfComparison.Contacts(Newtonsoft.Json.Linq.JObject.Parse("""
            {"Submitters":[{"FirstName":"Ada","LastName":"Lovelace","Email":"ada@example.org","Affiliation":"Analytical Engine Lab","Orcid":"0000-0001"}],
             "LabPIs":[{"Name":"Prof. C. Babbage","Email":"cb@example.org","Affiliation":"","Orcid":""}]}
            """));
        Check(contacts.Count == 2 && contacts[0].Contains("submitter") && contacts[0].Contains("ada@example.org") && contacts[1].Contains("lab PI") && contacts[1].Contains("Prof. C. Babbage"),
            "the depositors' contacts are read from PRIDE's record, submitter and lab PI");
        foreach (var x in r.Result.Rejected) Console.WriteLine($"     rejected: {x}");
        Console.WriteLine(failures == 0 ? "selftest passed" : $"selftest FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
