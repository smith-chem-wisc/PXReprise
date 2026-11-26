namespace PXReprise.Tests;

/// <summary>
/// PXR-R80: results.txt as MetaMorpheus 1.1.14 and 1.1.15 write it, cut to the lines the engine reads: the header, the
/// all-files counts, and the FDR engine's block. Every line is as MetaMorpheus wrote it.
/// <list type="bullet">
/// <item>The <c>Lfq</c> ones are searches of the shared inputs, <c>tests/metamorpheus-output/lfq</c>. 1.1.14 and 1.1.15's
/// notch are the search task of the MetaMorpheus output workflow's full run (2026-11-21), under the task files kept there.
/// 1.1.15's bare q-value and PEP q-value are its search task alone, run on the same inputs (2026-11-22) with 1.1.15's
/// default task file, <c>IdentificationFilter</c> set to each filter. These inputs are too small to train PEP (README).</item>
/// <item><see cref="Pxd031782PepTrained"/> is a trial search of PXD031782 under 1.1.15's PEP q-value filter, where PEP
/// trained.</item>
/// </list>
/// </summary>
internal static class ResultsTxtSamples
{
    /// <summary>MetaMorpheus 1.1.14: the q-value and the notch together; the FDR engine counts "within 1% FDR".</summary>
    public const string Lfq1114 = """
        MetaMorpheus: version 1.1.14
        Time to run task: 00:00:07.7376647

        --------------------------------------------------

        All target PSMs with q-value <= 0.01: 318
        All target peptides with q-value <= 0.01: 84
        All target protein groups with q-value <= 0.01 (1% FDR): 80
        All Precursors: 1605
        All MS2 Scans: 441

        --------------------------------------------------

        Engine Results:

        Engine type: FdrAnalysisEngine
        Time to run engine: 00:00:00.0040120
        PSMs within 1% FDR: 323
        Delta Score Used for FDR Analysis: False

        """;

    /// <summary>MetaMorpheus 1.1.15 under the q-value notch, the filter its legacy keys map to.</summary>
    public const string Lfq1115Notch = """
        MetaMorpheus: version 1.1.15
        Time to run task: 00:00:09.1889916

        --------------------------------------------------

        All target PSMs with q-value notch <= 0.01: 318
        All target peptides with q-value notch <= 0.01: 84
        All target protein groups with q-value notch <= 0.01 (1% FDR): 80
        All Precursors: 1605
        All MS2 Scans: 441

        --------------------------------------------------

        Engine Results:

        Engine type: FdrAnalysisEngine
        Time to run engine: 00:00:00.0042113
        PSMs with q-value notch <= 0.01: 323
        Delta Score Used for FDR Analysis: False

        """;

    /// <summary>MetaMorpheus 1.1.15 under a bare q-value: the q-value alone (#2989), the wording 1.1.14 used for both.</summary>
    public const string Lfq1115QValue = """
        MetaMorpheus: version 1.1.15
        Time to run task: 00:00:03.4194516

        --------------------------------------------------

        All target PSMs with q-value <= 0.01: 332
        All target peptides with q-value <= 0.01: 88
        All target protein groups with q-value <= 0.01 (1% FDR): 84
        All Precursors: 1605
        All MS2 Scans: 441

        --------------------------------------------------

        Engine Results:

        Engine type: FdrAnalysisEngine
        Time to run engine: 00:00:00.0166739
        PSMs with q-value <= 0.01: 332
        Delta Score Used for FDR Analysis: False

        """;

    /// <summary>
    /// MetaMorpheus 1.1.15 under the PEP q-value where PEP was not trained: the sentence on the line above each count it
    /// qualifies says so, by digestion agent, and that those matches were judged by the notch.
    /// </summary>
    public const string Lfq1115PepNotTrained = """
        MetaMorpheus: version 1.1.15
        Time to run task: 00:00:03.3750730

        --------------------------------------------------

        PEP filtering was requested, but PEP was not trained for 332 target PSMs (trypsin: 332), so those were filtered by q-value notch <= 0.01.
        All target PSMs with pep q-value <= 0.01: 332
        PEP filtering was requested, but peptide-level PEP q-values were not computed for 88 target peptides (trypsin: 88), so those were filtered by q-value notch <= 0.01.
        All target peptides with pep q-value <= 0.01: 88
        All target protein groups with pep q-value <= 0.01 (1% FDR): 84
        All Precursors: 1605
        All MS2 Scans: 441

        --------------------------------------------------

        Engine Results:

        Engine type: FdrAnalysisEngine
        Time to run engine: 00:00:00.0217728
        PSMs with pep q-value <= 0.01: 332
        Delta Score Used for FDR Analysis: False

        """;

    /// <summary>MetaMorpheus 1.1.15 under the PEP q-value where PEP trained: no sentence.</summary>
    public const string Pxd031782PepTrained = """
        MetaMorpheus: version 1.1.15
        Time to run task: 00:06:13.6903940

        --------------------------------------------------

        All target PSMs with pep q-value <= 0.01: 84532
        All target peptides with pep q-value <= 0.01: 15444
        All target protein groups with pep q-value <= 0.01 (1% FDR): 2652

        --------------------------------------------------

        Engine Results:

        Engine type: FdrAnalysisEngine
        Time to run engine: 00:01:03.0201158
        PSMs with pep q-value <= 0.01: 85963
        Delta Score Used for FDR Analysis: False

        """;
}
