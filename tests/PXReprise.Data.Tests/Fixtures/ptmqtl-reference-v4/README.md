# ptmqtl.* reference case v4 (G64; D37, D87, D88, D90)

The ptmQtl engines in PXReprise (`src/PXReprise.Runner/PtmQtl*`) must reproduce `expected/` from `inputs/`. Built by
`tools/ptmqtl-reference` (2026-11-25) against the released mzLib 1.0.601. It applies ptmQtl's `design/DEFINITIONS.md`
(`ptmQtl:DEF-PTM-PAIR v3`, `ptmQtl:DEF-SITE-TRAIT v3`) through mzLib calls only. Synthetic data, NOT AN AGE EFFECT.

    dotnet run -c Release --project tools/ptmqtl-reference -- <dir>

Compare numbers to 1e-12 relative, except a site-trait row with counted runs: the logit-normal model's optimizer gives
it, and its result differs across platforms near the 10th digit (Linux against this Windows build: 1.4e-10 relative on a
replicate variance), so compare those rows to 1e-8. Text columns must match exactly.

## What changed from v3

Type A is unchanged, and so are D1-D3's pair rows and depth-null counts, byte for byte. The site-trait fit moved to
`DEF-SITE-TRAIT v3`:
- **Bounds as counts (D37).** The fit reads type A's cells. A ceiling, a floor, a count-only cell or a covered zero
  enters as "k of N covering peptidoforms carried the modification" (mzLib's `BoundCellModel.Sampling`). Each sample's
  depth (log2 target PSMs, as type A's) is in the detection link, so a sample with no depth is left out of the fit.
- **Peptidoform counts on every cell.** Every stored cell takes the engine's covering and modified peptidoform counts
  for its run, not only count-only cells. The detection link is fitted on every cell's counts. Type A reads them only on
  count-only cells, so its rows do not move.
- **A covariate.** `runs.tsv` gains `sex`. D1's and D4's fits take it, coded against the level that sorts first
  (`sex=M`).
- **D4, new:** a deposit built for the fit.
- `expected/trait_effects.tsv` gains `covariates`, `covariate_effects`, `residual_variance_borrowed` and
  `counted_runs`.

## D4

A MetaMorpheus 1.1.13 whole proteome of 12 animals:
- A1-A4 were injected twice and A5-A12 once, 16 runs.
- Age is 3, 12 or 24 months by k % 3, and sex is F or M by k % 2, so each age and sex pair holds two animals.
- Ten phospho sites, one peptide each (`LGS<x>R`), two per protein R1-R5. There are enough cells for the detection link
  (mzLib's `MinLinkCells`, 100), which D1's four sites do not have.
- Each site's log odds per run is its own level, plus:
  - an age slope (sites 0-2: R1 S12, R1 S32, R2 S12);
  - a sex shift;
  - the animal's offset;
  - run noise (seed 20261125).
- One-form runs of every kind occur: a ceiling (unmodified form absent), a covered zero (modified form absent; stored
  by MetaMorpheus), a floor (modified form identified without intensity), and a count-only cell.
- **Site 8 (R5 S12)** is never seen in a second injection, so no animal holds two of its runs. That is D88's case.
- **Site 9 (R5 S32)** is seen only modified in the second injections, so no animal is measured twice. That is D87's case.

## What the expected rows show on mzLib 1.0.601

- **D4:** 9 of 10 sites are `Fitted` with counted runs. Sites 0-2 rise with age (about +0.11 log2 odds per month,
  p < 0.001), as built (0.9 natural-log odds per year). The others were built with an eighteenth of that slope.
  - Site 8 is `TooFewGroups`: mzLib 1.0.601 refuses a site whose replicates hold one run each.
  - Site 9 estimates its own run-to-run variance from runs that carry nothing about it.
- **D1:** S14_2 has no depth, so it is left out. Every animal then has one run.
  - P1 K17 has no counted run and is `TooFewGroups`.
  - The three sites with counted runs are `NoDetectionLink`: four sites are too few cells to fit the link from.
- With mzLib's #1629 (D87, D88), site 8 and D1's P1 K17 are fitted without the replicate term, and site 9 borrows the
  run-to-run variance. This case is rebuilt then, on that release.

## Inputs

As in v3 (`observations.tsv`, `occupancy.tsv`, `runs.tsv`, `depth.tsv`, `datasets.tsv`, `enrichment.tsv`,
`unimod.tsv`). `runs.tsv` gains `sex`; D4's rows join every file.
