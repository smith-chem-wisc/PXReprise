// Transcribed by a one-time script from src/datarepo/definitions.py (branch wip/d37-g81-python, which is
// master's 0.32.0 definitions plus D37's ten pxreprise: twins). The texts are the owners', word for word.
// From here on this file is the source: edit it, never regenerate it.
namespace DataRepo.Ingest;

public static partial class Definitions
{
    public static readonly Def Psm1pct = new(
        "aging:DEF-PSM-1PCT", "v1", "aging",
        "Target PSMs at 1% FDR, as MetaMorpheus's results.txt summary line reports them: 'All target PSMs with q-value <= 0.01'. Target PSMs only. This is the canonical PSM count. Four conditions, all required (aging thread 011): Decoy/Contaminant/Target == 'T', QValue <= 0.01, QValue Notch <= 0.01, and notch_ambiguous == false (aging:DEF-PSM-NOTCH-AMBIGUOUS). The fourth is not inferable from the written file alone, which is why a count taken without it comes out high.");

    public static readonly Def NotchAmbiguous = new(
        "aging:DEF-PSM-NOTCH-AMBIGUOUS", "v1", "aging",
        "A PSM is notch-ambiguous when the Notch cell of AllPSMs.psmtsv contains a '|' separator, i.e. MetaMorpheus wrote more than one notch hypothesis for the match. For such a PSM, SpectralMatch.ResolveAllAmbiguities leaves the in-memory PsmFdrInfo.QValueNotch unresolved at > 1, while PsmTsvWriter.AddMatchScoreData writes the MINIMUM notch q-value across hypotheses. The written 'QValue Notch' can therefore pass a threshold the counted one fails, which is why aging:DEF-PSM-1PCT needs this condition as well as the two q-values.");

    public static readonly Def PsmFdrEngine = new(
        "aging:DEF-PSM-FDRENGINE", "v1", "aging",
        "PSMs within 1% FDR as MetaMorpheus's FDR engine logs them. Higher than DEF-PSM-1PCT; it appears to include contaminant PSMs. In aging-provenance/2 and earlier this is the number stored as id_rate.psms_1pct, which is why the provenance field name cannot be trusted alone.");

    public static readonly Def IdRate = new(
        "aging:DEF-ID-RATE", "v1", "aging",
        "Identified fraction of MS2 spectra, as the pipeline records it in provenance id_rate.rate: the stage's PSM count over the MS2 count. Which PSM count it uses follows the provenance schema version (DEF-PSM-FDRENGINE in /2 and earlier).");

    public static readonly Def Mbr = new(
        "QuantProject:DEF-QC-MBR", "v1", "QuantProject",
        "Match-between-runs quality block as the pipeline records it: rows attempted, rows kept at the MBR FDR threshold, MS/MS-anchored peaks, and the kept-over-MSMS ratio. Copied from the provenance record that names this definition.");

    public static readonly Def PeptideIntensity = new(
        "QuantProject:DEF-PEP-INT", "v1", "QuantProject",
        "DEF-PEP-INT \u2014 `Intensity_<file>`. Unit: one peptide in one spectra file (`<file>` is the file name without its extension). Value: the intensity of the most intense peak kept for that peptide in that file. It's the maximum over peaks, not a sum (`FlashLFQResults.cs:191`). A peak's intensity is its apex isotopic envelope: the single MS1 scan, in a single charge state, where the envelope's summed isotope intensity is highest (`ChromatographicPeak.cs:85-93`). It is not an area under the elution curve, because MetaMorpheus leaves FlashLFQ's `Integrate` off. Normalization: with `Normalize` off (the default), this is the raw value. Which peaks count: MS/MS-identified peaks, plus MBR peaks that pass `DEF-MBR-KEPT`. Nothing else. 0 means \"no value\". It can mean not detected, identified but without a quantifiable peak, or shared with another peptide (see `DEF-PEP-DT`). Read 0 as NA, never as a measured zero. Grain and unit (v3.5): intensity, arbitrary units, apex isotopic envelope (not an area); one value per peptide \u00d7 spectra file; without a design: file; with an LFQ design: file, one column per raw file, fractions not summed (mzLib `FlashLFQ/Peptide.cs:78`). [dataRepo stores no row for a 0, so every stored value is a measurement.] [Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5.]");

    public static readonly Def ProteinIntensity = new(
        "QuantProject:DEF-PROT-INT", "v1 as corrected by v3.3", "QuantProject",
        "DEF-PROT-INT \u2014 `Intensity_<file>` in `AllQuantifiedProteinGroups.tsv`. Two bullets below are CORRECTED by v3.3. At 1.1.9+ the not-quantified cell is blank, not `0` (`DEF-PROT-ENCODING`); and with a design the fractions of one sample do not share a value \u2014 one file carries it and the rest are 0 (`FlashLFQResults.cs:609`). The rest stands. Method: FlashLFQ's median polish over the protein group's peptides (`FlashLFQResults.cs:407`, unchanged at 1.0.591). It isn't a sum and it isn't top-3. Which peptides count: only peptides unique to the group (`UseSharedPeptidesForLFQ` = false by default), and only those with an unambiguous quantification. Unit: one sample, i.e. one (condition, biological replicate). Without a design file each file is its own sample. With a design, fractions of one sample share a value. 0 means no value. Read it as NA. Grain and unit (v3.5): intensity, arbitrary units, median polish; one value per protein group \u00d7 sample group; without a design: file; with an LFQ design: (condition, biological replicate), one fraction's column carries the value, the others are 0 (v3.3); TMT: file \u00d7 channel. The protein table is written unfiltered (decoys, contaminants, groups above 1% FDR); a statistic over it needs `DEF-PROTSET-1PCT` first. [dataRepo stores no row for a blank or a 0, so every stored value is a measurement.] [Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5.]");

    public static readonly Def ProteinSpectralCount = new(
        "QuantProject:DEF-PROT-SPC", "v3.5", "QuantProject",
        "DEF-PROT-SPC \u2014 `SpectralCount_<label>` in `AllQuantifiedProteinGroups.tsv` (and `AllProteinGroups.tsv`). Value: the number of distinct PSMs assigned to the protein group, in the files of that sample group. An integer. Which PSMs: those passing the search's PSM-level q-value filter (`filterAtPeptideLevel: false`, high-q PSMs excluded; `ProteinScoringAndFdrEngine.cs:62-66`) whose best-matching peptides include any peptide of the group (`:67-87`). Shared peptides count. A PSM matching a peptide shared by two groups counts in both. This is the opposite of `DEF-PROT-INT`, which uses unique peptides only. The two columns of one block do not describe the same evidence, and a ratio of intensity to spectral count mixes them. Modified forms: with `ModPeptidesAreDifferent` off (the default, `SearchParameters.cs:22`) a PSM needs only a resolved base sequence, so a PSM whose modification is ambiguous still counts. 0 is a real zero here: no qualifying PSM in that sample group. Unlike an intensity cell, it is a measurement. Grain: summed over a sample group's fractions and technical replicates. Grain and unit (v3.5): count of PSMs, dimensionless integer; one value per protein group \u00d7 sample group; without a design: file; with an LFQ design: (condition, biological replicate), fractions and technical replicates summed; TMT: file -- every channel of a file carries the same value, written once per file. [dataRepo stores a 0 as a row with value 0, because it is a measurement.] [Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5.]");

    public static readonly Def Occupancy = new(
        "QuantProject:DEF-OCC-CELL", "v3.2", "QuantProject",
        "DEF-OCC-CELL (v3, grammar superseded by v3.2) with DEF-OCC-COUNT, DEF-OCC-INT, DEF-OCC-PSMS, DEF-OCC-ABSENT, DEF-OCC-INT-ZERO, DEF-OCC-COUNTONLY, DEF-OCC-KEY, DEF-OCC-ACCESSION, DEF-OCC-GROUPING and DEF-OCC-MINIMUM. Cells: `CountOccupancy_<label>` and `IntensityOccupancy_<label>` of the protein-group table, one entry `pos{p}[{mod},info:fraction={f}({numerator}/{denominator})]` per (position, modification), `;` within a protein, `|` between proteins; a protein with no entry is skipped with no placeholder, so the `|` segments are a SUBSEQUENCE of the accession column and cannot be zipped with it by index. `p` is 1-based in that accession's own sequence, 0 for the protein N-terminus, Length + 1 for the C-terminus. <label> is the sample group; with no design each raw file is its own group, so every denominator is a single-injection denominator. Evidence (DEF-OCC-PSMS): the group's PSMs passing the PSM-level q-value filter (default 0.01), in the group's files; modification types `Common Variable` and `Common Fixed` are excluded, and so are peptide-terminal modifications; an ambiguous PSM counts in the denominator of every position its base sequence covers and marks no site. Count (DEF-OCC-COUNT): numerator = PSMs whose resolved form carries the modification at the position; denominator = PSMs covering the position. The integers are exact; the fraction is written to 2 decimals. Written for every site with at least one modified PSM. Intensity (DEF-OCC-INT): the same sums weighted by each PSM's intensity share (a peptidoform's DEF-PEP-INT apex intensity in that file, split over its PSMs there). MBR transfers do not enter. The fraction is written to 4 decimals and is authoritative; the pair is rounded to 4 significant figures. Written only when the denominator > 0; not computed for TMT or SILAC. States (DEF-OCC-COUNTONLY): quantified (both entries, intensity numerator > 0); floor (intensity entry 0.0000 with numerator 0: modified form identified, never quantified -- censored, not a zero, DEF-OCC-INT-ZERO); count-only (count entry, no intensity entry: nothing covering the site was quantified); absent (no entry: no modified form seen -- NA, never 0, DEF-OCC-ABSENT). No minimum evidence is applied by the writer; N = 5 covering PSMs is the recommended reporting floor (DEF-OCC-MINIMUM). No uncertainty is reported. [dataRepo resolves each `|` segment to its accession against the searched sequence, maps `p` to `ptm_sites` coordinates, and stores both bases, both halves of each cell and the state.] [Source: QuantProject design/DATA-DEFINITIONS.md at f4bb910, definitions v3.5.]");

    public static readonly Def PeptideCount1pct = new(
        "aging:DEF-PEPTIDE-1PCT", "v1", "aging",
        "Target peptides at 1% FDR: results.txt line 'All target peptides with q-value <= 0.01'. From AllPeptides.psmtsv: Decoy/Contaminant/Target == 'T', QValue <= 0.01, QValue Notch <= 0.01. MetaMorpheus computes it at peptide-level FDR, collapsing to one row per full sequence (lowest PEP). Verified on PXD036557: 5,541. No ambiguous-notch rows survive the collapse, so aging:DEF-PSM-NOTCH-AMBIGUOUS does not bite here.");

    public static readonly Def ProteinGroupCount1pct = new(
        "aging:DEF-PROTEINGROUP-1PCT", "v1", "aging",
        "Target protein groups at 1% FDR: results.txt line 'All target protein groups with q-value <= 0.01 (1% FDR)'. The predicate is Protein QValue <= 0.01 && !IsDecoy, so contaminant groups ARE counted -- unlike the PSM and peptide lines, which exclude them. Verified on PXD036557: 1,652 not-decoy against 1,623 strictly 'T'.");

    public static readonly Def Ms2Count = new(
        "aging:DEF-MS2", "v1", "aging",
        "MS2 scans in the run's raw files, counted by aging's QC stage from pymzlib.readers.read_spectra (scans with MS level 2), summed over files. Verified against MetaMorpheus's own 'All MS2 Scans' line on PXD036557: both 266,402.");

    public static readonly Def RunMinutes = new(
        "aging:DEF-RUN-MINUTES", "v1", "aging",
        "Acquisition length of one raw file: the maximum retention time over all its scans, in minutes, rounded to 2 dp. On PXD036557 all 18 files report 180.0, checked at full precision on two of them (180.00184 and 179.99996 min), so an identical value across files is a real method length rather than a rounding artefact or a default.");

    public static readonly Def PrecursorCount = new(
        "aging:DEF-PRECURSORS", "v1", "aging",
        "results.txt line 'All Precursors': the precursor envelopes MetaMorpheus deconvoluted from the MS2 scans, which is more than one per scan (495,127 over 266,402 scans on PXD036557). It is NOT a count of scans, and it is not a count of distinct species.");

    public static readonly Def ContamPsmShare = new(
        "aging:DEF-CONTAM-PSM", "v1", "aging",
        "Contaminant share of the identifications in a dataset: contaminant PSMs over (target + contaminant) PSMs, over the whole dataset. An identification-level share, so it says how much of the evidence came from the contaminant database, not how much of the signal did -- QuantProject:DEF-QC-9 is the intensity-level answer and the two differ by several fold.");

    public static readonly Def ContamIntensityShare = new(
        "QuantProject:DEF-QC-9", "v2", "QuantProject",
        "Contaminant share of the measured intensity in ONE raw file: summed intensity of contaminant features over summed intensity of all features. It is defined per file, and aggregating it to a dataset hides real structure: on PXD036557 the per-file values run 2.6% to 18.9% and are grouped by cell line, which is serum carryover differing sevenfold inside one experiment (aging thread 014).");

    public static readonly Def PxrPsm1pct = new(
        "pxreprise:DEF-PSM-1PCT", "v1", "PXReprise",
        "**Grain:** dataset.\n\nTarget PSMs at 1% FDR, as MetaMorpheus's `results.txt` summary line reports them: `All target PSMs with q-value <= 0.01`. Target PSMs only; contaminants are excluded. This is the canonical PSM count. Reproducing it from `AllPSMs.psmtsv` needs four conditions, all of them required: `Decoy/Contaminant/Target` is exactly `T`; `QValue <= 0.01`; `QValue Notch <= 0.01`; and the PSM is not notch-ambiguous (`pxreprise:DEF-PSM-NOTCH-AMBIGUOUS`). Even then the reproduction is an approximation, not the definition. `QValue` is printed to six decimals, so a true q-value just above 0.01 prints as `0.010000` and passes a file-side test that MetaMorpheus's own count rejects. No file-side predicate can reproduce a count whose threshold coincides with a printable value. The canonical number is always the one MetaMorpheus prints. A consumer that selects rows with the predicate should expect a small disagreement in either direction (measured: 0, -14 and -6 PSMs on three datasets), and report it rather than reconcile it away. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrNotchAmbiguous = new(
        "pxreprise:DEF-PSM-NOTCH-AMBIGUOUS", "v1", "PXReprise",
        "**Grain:** PSM.\n\nA PSM is notch-ambiguous when the `Notch` cell of `AllPSMs.psmtsv` contains a `|` separator, that is, when MetaMorpheus wrote more than one notch hypothesis for the match. For such a PSM, `SpectralMatch.ResolveAllAmbiguities` leaves the in-memory `PsmFdrInfo.QValueNotch` unresolved at a value above 1, while `PsmTsvWriter.AddMatchScoreData` writes the **minimum** notch q-value across the hypotheses. The written `QValue Notch` can therefore pass a threshold that the counted one fails. That is why `pxreprise:DEF-PSM-1PCT` needs this condition as well as the two q-values. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrPsmFdrEngine = new(
        "pxreprise:DEF-PSM-FDRENGINE", "v1", "PXReprise",
        "**Grain:** dataset.\n\nPSMs within 1% FDR, as MetaMorpheus's FDR engine logs them: the first `PSMs within 1% FDR: <n>` line. It is higher than `pxreprise:DEF-PSM-1PCT` and appears to include contaminant PSMs. It is recorded for comparison and never reported. In aging's provenance schema `/2` and earlier, this was the number stored as `id_rate.psms_1pct`. So that field name cannot be trusted without the record's schema version. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrPeptideCount1pct = new(
        "pxreprise:DEF-PEPTIDE-1PCT", "v1", "PXReprise",
        "**Grain:** dataset.\n\nTarget peptides at 1% FDR: the `results.txt` line `All target peptides with q-value <= 0.01`. From `AllPeptides.psmtsv`, the predicate is `Decoy/Contaminant/Target` is exactly `T`, `QValue <= 0.01` and `QValue Notch <= 0.01`. MetaMorpheus computes it at peptide-level FDR and collapses it to one row per full sequence (lowest PEP). No ambiguous-notch rows survive the collapse, so `pxreprise:DEF-PSM-NOTCH-AMBIGUOUS` does not apply here. Verified on PXD036557: 5,541. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrProteinGroupCount1pct = new(
        "pxreprise:DEF-PROTEINGROUP-1PCT", "v1", "PXReprise",
        "**Grain:** dataset.\n\nTarget protein groups at 1% FDR: the `results.txt` line `All target protein groups with q-value <= 0.01 (1% FDR)`. The predicate is `Protein QValue <= 0.01` and not decoy, so **contaminant groups are counted**, unlike the PSM and peptide lines, which exclude them. Verified on PXD036557: 1,652 groups that are not decoys, against 1,623 that are strictly `T`. MetaMorpheus has no leading, razor or representative protein. A group is an unordered set whose accession string is sorted alphabetically, so no quantity may be built on \"the first member\". [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrIdRate = new(
        "pxreprise:DEF-ID-RATE", "v1", "PXReprise",
        "**Grain:** dataset.\n\nThe identified fraction of MS2 spectra, as the search stage records it in `provenance.json` `id_rate.rate`: `pxreprise:DEF-PSM-1PCT` divided by `pxreprise:DEF-MS2` summed over the searched files. A file that QC excluded from the search is left out of both. The `low_id_rate` flag uses this value. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrMs2Count = new(
        "pxreprise:DEF-MS2", "v1", "PXReprise",
        "**Grain:** run; summed to the dataset for reporting.\n\nMS2 scans in one raw file. PXReprise's spectra-QC stage counts them by reading the file with mzLib's readers: the scans whose MSn order is 2. The sum over the searched files equals MetaMorpheus's own `All MS2 Scans` line. Verified on PXD036557: both 266,402. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrRunMinutes = new(
        "pxreprise:DEF-RUN-MINUTES", "v1", "PXReprise",
        "**Grain:** run. Never a dataset figure.\n\nThe acquisition length of one raw file: the largest retention time over all its scans, in minutes, rounded to 2 decimal places (half to even). On PXD036557 all 18 files report 180.0, and two were checked at full precision (180.00184 and 179.99996 min). So an identical value across files is a real method length, not a rounding artefact or a default. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrPrecursorCount = new(
        "pxreprise:DEF-PRECURSORS", "v1", "PXReprise",
        "**Grain:** run; summed to the dataset for reporting.\n\nThe `results.txt` line `All Precursors`: the precursor envelopes MetaMorpheus deconvoluted from the MS2 scans. This is more than one per scan (495,127 over 266,402 scans on PXD036557). It is **not** a count of scans, and it is not a count of distinct species. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    public static readonly Def PxrContamPsmShare = new(
        "pxreprise:DEF-CONTAM-PSM", "v1", "PXReprise",
        "**Grain:** dataset.\n\nThe contaminant share of the identifications in a dataset: contaminant PSMs divided by (target + contaminant) PSMs, at `QValue <= 0.01`, decoys excluded, over the whole dataset. A PSM counts as a contaminant only when its `Decoy/Contaminant/Target` value is exactly `C`. An ambiguous `C|T` counts as not-contaminant and stays in the denominator. This is an identification-level share: it says how much of the evidence came from the contaminant database, not how much of the signal did. `QuantProject:DEF-QC-9` is the intensity-level answer, and the two differ by several fold. A per-file share is a different quantity with its own ID, never this one measured per file. [Source: PXReprise DEFINITIONS.md at smith-chem-wisc/PXReprise 1ac199e7e40eab638c74b5c6cb6aa7acb596dadd.]");

    internal const string PepText = "DEF-PEP -- `PEP` and `PEP_QValue` in MetaMorpheus's AllPSMs.psmtsv / AllPeptides.psmtsv (dataRepo `psms.pep`, `psms.pep_q_value`, `peptidoforms.best_pep`). RUN-RELATIVE. `PepAnalysisEngine` trains an ML.NET gradient-boosted classifier on the search's OWN targets and decoys and writes its output onto every PSM. No fixed model ships, so two datasets are scored by two models trained on different class balances, even on one release (one dataset, same 30 files, trained at 22:1 target:decoy under a +-0.5 Da search and 1.43:1 under +-20 ppm). The value is a Platt-calibrated classifier score, not an error rate: measured 13.8 sigma optimistic in its most confident bin at peptide level. Comparable: the ranking by PEP within one dataset, and counts at a threshold (e.g. targets at PEP_QValue <= 0.01). Not comparable: PEP values or distributions across datasets or releases. `PEP_QValue` is computed by ordering on PEP and moves with it; the plain q-value comes from the search-score ordering, ignores PEP, and is the more stable column across releases. No PEP method identifier exists; this definition's version is the MetaMorpheus release and the PEP regime (standard / top-down / crosslink / RNA, each a different feature set in `PsmData.trainingInfos`). The per-run training metrics (AUC, LogLoss, training counts) in results.txt are the only record of the model that produced the numbers. [Source: pep thread 002 to dataRepo, 2026-09-25.]";
}
