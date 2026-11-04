// pxr-evidence selftest: the figure-claim rules, offline (no key, no network). Exit 0 when every case holds.
using Readers;
using SampleEvidenceModel;

internal static class SelfTest
{
    internal static int Run()
    {
        var input = new PublicationInput("PXD000001", "Hippocampus of young and aged mice.",
            "## Methods\nProteins were digested with trypsin and analysed on an Orbitrap.", Array.Empty<SupplementTable>(),
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
            { "data_file": "", "data_file_pattern": "", "label": "", "column": "characteristics[organism part]", "value": "hippocampus", "source": "pride record", "quote": "Hippocampus of young and aged mice", "confidence": "likely" }
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
        foreach (var x in r.Result.Rejected) Console.WriteLine($"     rejected: {x}");
        Console.WriteLine(failures == 0 ? "selftest passed" : $"selftest FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
