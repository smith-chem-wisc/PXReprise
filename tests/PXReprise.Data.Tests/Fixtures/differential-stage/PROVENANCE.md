# differential-stage fixture

Cut on 2026-11-28 from aging's real `differential` stage folders, read only, by a scratch script (not shipped):

- `lfq/` is PXD036557's stage (`F:\aging_data\run_2026-09-21\PXD036557\06_differential`, PXReprise 1.0.2, mzLib
  1.0.601, `diff-1.0`), with only its `mbr_kept` analysis:
  - `DifferentialResults.tsv`: mzLib's header and the first two real rows of each status;
  - `DifferentialDecisions.tsv`: the header and the first two real rows of each scope;
  - `DifferentialResults.metadata.json`: real, with `row_count` (`total`, `by_status`) recomputed for the rows kept;
  - `StatisticalReport.md`, `DifferentialAssumptions.md` and `figures/figure_07_global_shift.{svg,tsv}`: real, whole;
  - `provenance.json`: real, its `analyses` cut to `mbr_kept` with that analysis's counts recomputed.
- `one_condition/provenance.json` is PXD003111's stage record (TMT, `analysed: false`, finding `one_condition`), whole.

Two changes bind the fixture to the test's bundle, the fixture deposit PXD999999, as a real stage is bound to its search:

- Every input sha256 is a placeholder, `{{sha:<role>}}`, that the test fills from the bundle's source of that role
  (`provenance:04_search`, `peptide_quant`, `protein_group_quant`).
- PXD999999's search has no design file, so the `experimental_design` input is left out of both records.

Every cell of the two tables is as mzLib wrote it.
