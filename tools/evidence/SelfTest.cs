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
        foreach (var x in r.Result.Rejected) Console.WriteLine($"     rejected: {x}");
        Console.WriteLine(failures == 0 ? "selftest passed" : $"selftest FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
