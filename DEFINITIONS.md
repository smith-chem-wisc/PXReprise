# Definitions

PXReprise's pipeline counts, and what each one means. Every number PXReprise writes into a `provenance.json` carries a
definition ID, `pxreprise:<ID> v<n>`. **PXReprise owns these ten.** They describe how the engine counts. They say
nothing about any biological question. Quantitative definitions (intensities, match-between-runs, contaminant
intensity) belong to QuantProject and keep its IDs (`QuantProject:DEF-QC-9`, ...). PXReprise never redefines another
project's number.

**Where they came from.** Each one is the twin of the `aging:` definition with the same short ID, version `v1`, in aging's
register. The meaning is the same. When PXReprise took over running the pipeline (2026-09-29), it took over the
definitions of the pipeline's own numbers. So `pxreprise:DEF-PSM-1PCT v1` and `aging:DEF-PSM-1PCT v1` give the same
number for the same search. Bundles made before the switch keep the `aging:` IDs, and none is rewritten.

**How a record declares them.** A stage's `provenance.json` that holds `"definitions": "pxreprise"` uses the IDs below. A
record that declares nothing is read as `aging:`.

**The grain rule.** A number is stored at the grain at which it was measured, never coarser and never finer. Two grains
of one quantity are two definitions. A scan count is a partition: every MS2 scan belongs to exactly one file, so the
dataset figure is the sum and nothing is invented. A 1% FDR count is not, because the FDR is recomputed on each subset.

**Changing a definition.** The text of a published version never changes. A changed meaning gets a new version
(`v2`), and data keep the version that produced them.

All ten were checked against MetaMorpheus 1.1.11's source code and its output.

---

## pxreprise:DEF-PSM-1PCT v1

**Grain:** dataset.

Target PSMs at 1% FDR, as MetaMorpheus's `results.txt` summary line reports them: `All target PSMs with q-value <= 0.01`.
Target PSMs only; contaminants are excluded. This is the canonical PSM count. Reproducing it from `AllPSMs.psmtsv` needs
four conditions, all of them required: `Decoy/Contaminant/Target` is exactly `T`; `QValue <= 0.01`; `QValue Notch <= 0.01`;
and the PSM is not notch-ambiguous (`pxreprise:DEF-PSM-NOTCH-AMBIGUOUS`). Even then the reproduction is an approximation,
not the definition. `QValue` is printed to six decimals, so a true q-value just above 0.01 prints as `0.010000` and passes
a file-side test that MetaMorpheus's own count rejects. No file-side predicate can reproduce a count whose threshold
coincides with a printable value. The canonical number is always the one MetaMorpheus prints. A consumer that selects rows
with the predicate should expect a small disagreement in either direction (measured: 0, -14 and -6 PSMs on three datasets),
and report it rather than reconcile it away.

## pxreprise:DEF-PSM-NOTCH-AMBIGUOUS v1

**Grain:** PSM.

A PSM is notch-ambiguous when the `Notch` cell of `AllPSMs.psmtsv` contains a `|` separator, that is, when MetaMorpheus
wrote more than one notch hypothesis for the match. For such a PSM, `SpectralMatch.ResolveAllAmbiguities` leaves the
in-memory `PsmFdrInfo.QValueNotch` unresolved at a value above 1, while `PsmTsvWriter.AddMatchScoreData` writes the
**minimum** notch q-value across the hypotheses. The written `QValue Notch` can therefore pass a threshold that the counted
one fails. That is why `pxreprise:DEF-PSM-1PCT` needs this condition as well as the two q-values.

## pxreprise:DEF-PSM-FDRENGINE v1

**Grain:** dataset.

PSMs within 1% FDR, as MetaMorpheus's FDR engine logs them: the first `PSMs within 1% FDR: <n>` line. It is higher than
`pxreprise:DEF-PSM-1PCT` and appears to include contaminant PSMs. It is recorded for comparison and never reported. In
aging's provenance schema `/2` and earlier, this was the number stored as `id_rate.psms_1pct`. So that field name
cannot be trusted without the record's schema version.

## pxreprise:DEF-PEPTIDE-1PCT v1

**Grain:** dataset.

Target peptides at 1% FDR: the `results.txt` line `All target peptides with q-value <= 0.01`. From `AllPeptides.psmtsv`, the
predicate is `Decoy/Contaminant/Target` is exactly `T`, `QValue <= 0.01` and `QValue Notch <= 0.01`. MetaMorpheus computes it
at peptide-level FDR and collapses it to one row per full sequence (lowest PEP). No ambiguous-notch rows survive the
collapse, so `pxreprise:DEF-PSM-NOTCH-AMBIGUOUS` does not apply here. Verified on PXD036557: 5,541.

## pxreprise:DEF-PROTEINGROUP-1PCT v1

**Grain:** dataset.

Target protein groups at 1% FDR: the `results.txt` line `All target protein groups with q-value <= 0.01 (1% FDR)`. The
predicate is `Protein QValue <= 0.01` and not decoy, so **contaminant groups are counted**, unlike the PSM and peptide
lines, which exclude them. Verified on PXD036557: 1,652 groups that are not decoys, against 1,623 that are strictly `T`.
MetaMorpheus has no leading, razor or representative protein. A group is an unordered set whose accession string is
sorted alphabetically, so no quantity may be built on "the first member".

## pxreprise:DEF-ID-RATE v1

**Grain:** dataset.

The identified fraction of MS2 spectra, as the search stage records it in `provenance.json` `id_rate.rate`:
`pxreprise:DEF-PSM-1PCT` divided by `pxreprise:DEF-MS2` summed over the searched files. A file that QC excluded from
the search is left out of both. The `low_id_rate` flag uses this value.

## pxreprise:DEF-MS2 v1

**Grain:** run; summed to the dataset for reporting.

MS2 scans in one raw file. PXReprise's spectra-QC stage counts them by reading the file with mzLib's readers: the scans
whose MSn order is 2. The sum over the searched files equals MetaMorpheus's own `All MS2 Scans` line. Verified on
PXD036557: both 266,402.

## pxreprise:DEF-RUN-MINUTES v1

**Grain:** run. Never a dataset figure.

The acquisition length of one raw file: the largest retention time over all its scans, in minutes, rounded to 2 decimal
places (half to even). On PXD036557 all 18 files report 180.0, and two were checked at full precision (180.00184 and
179.99996 min). So an identical value across files is a real method length, not a rounding artefact or a default.

## pxreprise:DEF-PRECURSORS v1

**Grain:** run; summed to the dataset for reporting.

The `results.txt` line `All Precursors`: the precursor envelopes MetaMorpheus deconvoluted from the MS2 scans. This is more
than one per scan (495,127 over 266,402 scans on PXD036557). It is **not** a count of scans, and it is not a count of
distinct species.

## pxreprise:DEF-CONTAM-PSM v1

**Grain:** dataset.

The contaminant share of the identifications in a dataset: contaminant PSMs divided by (target + contaminant) PSMs, at
`QValue <= 0.01`, decoys excluded, over the whole dataset. A PSM counts as a contaminant only when its
`Decoy/Contaminant/Target` value is exactly `C`. An ambiguous `C|T` counts as not-contaminant and stays in the
denominator. This is an identification-level share: it says how much of the evidence came from the contaminant
database, not how much of the signal did. `QuantProject:DEF-QC-9` is the intensity-level answer, and the two differ by
several fold. A per-file share is a different quantity with its own ID, never this one measured per file.
