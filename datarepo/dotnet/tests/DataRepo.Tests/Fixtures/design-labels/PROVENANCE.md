# design-labels

PXReprise's two mocks of a MetaMorpheus 1.1.12 search with an experimental design (PXReprise 028, PXR-R9), built from
this repository's own test dataset (`tests/data`, PXD999999). Each folder overlays
`tests/data/work_root/run_test/PXD999999/04_search/mm/Task3SearchTask/`. Copied unchanged from PXReprise
`results/g27_ingest_repro_2026-10-08/<variant>/work_root/run_test/PXD999999/04_search/mm/Task3SearchTask/` at their
commit `c2509af` (2026-10-08).

| variant | design | protein-group columns | datarepo 1.3.0 |
|---|---|---|---|
| `A_one_file_per_sample` | `all`, bioreps 1 and 2, one file each | `Intensity_all_1`, `Intensity_all_2` | refused: `PXD999999:all_1:label_free` has no assay |
| `B_one_sample_two_fractions` | `all`, biorep 1, fractions 1 and 2 | `Intensity_all_1` only | refused: `PXD999999:all_1:label_free` has no assay |

- `AllQuantifiedProteinGroups.tsv`: the fixture's per-file column labels renamed to per-sample ones, as mzLib
  `SampleGroupBuilder` names them when conditions are defined. In B the second file's four columns are dropped, because
  one sample has one column set. The values are the fixture's, so A's rows must equal the baseline's.
- `ExperimentalDesign.tsv`: MetaMorpheus's layout (`FileName Condition Biorep Fraction Techrep`, `-calib.mzML` names).
- `experiment.sdrf.tsv`: what MetaMorpheus 1.1.12 writes beside it. The ingest does not read it (its `source name` is
  `all 1`, with a space; PXR-F3).

The peptide, peak and PSM tables are the fixture's, untouched.

| file | sha256 |
|---|---|
| `A_one_file_per_sample/AllQuantifiedProteinGroups.tsv` | `7434b1631e698262869bce233c7b5b69a93634d68ac4be8dc22c5b55d27911c5` |
| `A_one_file_per_sample/ExperimentalDesign.tsv` | `55a216d3298579eaefde709af6a7be9320bef70ca1e546e325c06279c307fe63` |
| `A_one_file_per_sample/experiment.sdrf.tsv` | `dcad5c8f931edcb0c3e2e0ccca014b3b06e9bee50fc00e955e2361391166e423` |
| `B_one_sample_two_fractions/AllQuantifiedProteinGroups.tsv` | `6a0f243e71b6fe551628dbd0a57125e22f3b2c1aee8cf7bde9125e617d4050f8` |
| `B_one_sample_two_fractions/ExperimentalDesign.tsv` | `f49e260c8305216072c44da054c2f41ab1b1e1920b1a6ab807b7b2f1bee48c06` |
| `B_one_sample_two_fractions/experiment.sdrf.tsv` | `54b3e3f7af1193464fd729d6c42fb54a64c6f3bb03ce47dbd96c095aa2a32c48` |
