# ptmqtl.* reference case v3 (G62; D37, D40, D42-D44, D78, D82, D83)

The ptmQtl engines in PXReprise (`src/PXReprise.Runner/PtmQtl*`) must reproduce `expected/` from `inputs/`. Built by
`tools/ReferenceCase` (2026-11-23) against the released mzLib 1.0.600. It applies `design/DEFINITIONS.md`
(`ptmQtl:DEF-PTM-PAIR v3`, `ptmQtl:DEF-SITE-TRAIT v2`) through mzLib calls only. Synthetic data, NOT AN AGE EFFECT.

    dotnet run -c Release --project tools/ReferenceCase -- results/reference_case_v3

Compare numbers to 1e-12 relative. Text columns must match exactly.

## What changed from v2

Type A moved to `DEF-PTM-PAIR v3`, which is v5's method (`results/pairs_6a81_v5`):
- **Bounds (D37, D42-D44).** A cell seen in one form is tied at one end of its site's ranks. A covered-zero or a floor
  is tied lowest. A ceiling, or a count-only cell where only the modified form was identified, is tied highest. A
  count-only cell where both forms were identified has no rank, and is left out of that test only.
- **Depth (D40).** The correlation is partial, given each sample's depth: log2 of the target PSMs over all of its runs,
  so fractions add up. A sample with no target PSM has no depth and is left out of every pair. The PSMs are those the
  search's own filter kept (S7, D82).
- **Where covered-zeros come from (S1).** From MetaMorpheus 1.1.13 on, MetaMorpheus stores them (`covered_zero`). Up to
  1.1.12 it stores none, and the engine reads them from the observations' coverage, wherever nothing is stored. The two
  are never mixed. Every count-only cell takes the engine's covering and modified peptidoform counts, by which it is
  ranked.
- **D78's depth null (D83).** On each deposit's type A, each site's sample cells are permuted within blocks of 3 samples
  adjacent in depth, 3 replicates (seed 20261110, as `tools/ReferenceCase` writes it). A deposit is labelled
  depth-like when the null's calls (tested, not overlapping, q <= 0.1) per replicate reach 10 % of its real ones.
  - Its type A rows carry the flag `depth_like`, and so does a pooled row with a labelled dataset behind it. The rows
    are kept.
  - `expected/depth_null.tsv` holds the counts.
- `DEF-SITE-TRAIT v2` is unchanged. It reads the stored cells without bounds: a stored covered-zero is left out.

## Inputs (what the engine maps from a bundle)

As in v1, plus two files:

| file | stands for |
|---|---|
| `observations.tsv` | per-run peptidoforms: MetaMorpheus full sequence (with `Category:IdWithMotif`), protein, start, end, MS/MS intensity (empty = identified, not quantified), previous residue |
| `occupancy.tsv` | stored per-run cells (`ptm_stoichiometry`): modification as IdWithMotif without category, state (`quantified`, `floor`, `count_only`, `covered_zero`), 4-dp fraction, intensities to 4 significant figures, `unmodified_quantified` |
| `runs.tsv` | run -> `combine_to` (fractions to their sample; injections keep their own run), biological replicate, trait |
| `depth.tsv` | **new:** each run's target PSMs under S7's filter; a run with none is absent |
| `datasets.tsv` | **new:** each dataset's MetaMorpheus release (`datasets.search_engine_version`) |
| `enrichment.tsv` | the consumer's list of PTM-enriched deposits (S3) |
| `unimod.tsv` | IdWithMotif -> UNIMOD, for the cross-dataset key (S5) |

## What each rule should do

As in v1 for S2-S5 (S2 in and out, the enriched D3, D1's two fractions and two injections, the protein N-terminus, UNIMOD
keys), and:

- **D1, a MetaMorpheus 1.1.12 search:** 13 samples plus two injections of animal S14. P1 S13 and P2 S5 co-vary, P3's
  N-terminus falls as they rise, and P1 K17 follows neither. Depth does not follow any of them.
  - **S3:** P2 S5's modified form is absent. Nothing is stored for it, so the engine adds a covered-zero.
  - **S4:** P3's N-terminus is seen only acetylated: a ceiling.
  - **S5:** P1 S13's modified forms are identified but not quantified: a floor.
  - **S6:** both of P2's forms are identified, neither quantified: count-only with both forms, so no rank. P2's pairs
    have `n` = 13.
  - **S7:** only P2's modified form is identified, not quantified: count-only, all modified, tied highest.
  - **S14_2** has no target PSM, so no depth: every pair leaves it out (`n` = 14, from 15 samples).
- **D2, a MetaMorpheus 1.1.13 search:** 12 runs, depth rising with the run. P1 S13 and P2 S5 both follow a U in depth,
  which a linear partial on depth's ranks cannot take out.
  - **R3:** P2 S5's covered-zero is stored by MetaMorpheus, and the engine adds none.
  - **R5:** P3's N-terminus is seen only acetylated: a ceiling.
- **D78:**
  - **D1 is labelled depth-like:** 3 real calls, and the null makes 0, 1 and 1 (22 %). The deposit was built so that
    depth explains nothing. With 3 real calls, though, two null calls in three replicates already cross 10 %. That is
    the label's known fragility on few calls (G61: 24 of the 32 labelled deposits had 1 to 18 calls).
  - **D2 is not labelled:** 1 real call, and the null makes none.
  - So D1's type A rows, and every pooled type A row (D1 is behind each of them), carry `depth_like`. D2's rows do not.
