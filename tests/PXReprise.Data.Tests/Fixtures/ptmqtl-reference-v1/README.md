# ptmqtl.* reference case v1 (G37)

dataRepo's `ptmqtl.*` engines must reproduce `expected/` from `inputs/`. Built by `tools/ReferenceCase` (2026-10-06),
which applies `design/DEFINITIONS.md` (`ptmQtl:DEF-PTM-PAIR v2`, `ptmQtl:DEF-SITE-TRAIT v1`) through mzLib calls only.
It builds against mzLib #1430 (`code/mzLib-fractions`), until a release carries `CombineRuns` / `CombineObservations`.
Synthetic data, NOT AN AGE EFFECT.

    dotnet run -c Release --project tools/ReferenceCase -- results/reference_case_v1

## Inputs (what the engine maps from a bundle)

| file | stands for |
|---|---|
| `observations.tsv` | per-run peptidoforms: MetaMorpheus full sequence (with `Category:IdWithMotif`), protein, start, end, MS/MS intensity (empty = identified, not quantified), previous residue |
| `occupancy.tsv` | stored per-run cells (`ptm_stoichiometry`): modification as IdWithMotif without category, state, 4-dp fraction, intensities to 4 significant figures, `unmodified_quantified` |
| `runs.tsv` | run -> `combine_to` (fractions to their sample; injections keep their own run), biological replicate, trait |
| `enrichment.tsv` | the consumer's list of PTM-enriched deposits (S3) |
| `unimod.tsv` | IdWithMotif -> UNIMOD, for the cross-dataset key (S5) |

## What each rule should do

- **S2 in:** `Common Biological` phospho P1 S13, `Trypsin Digested` GG P1 K17, and `UniProt` phosphoserine P2 S5 and N-acetylalanine P3.
- **S2 out:** `Common Biological:Formylation on K` (exclusion list), `Common Variable:Oxidation on M`, and
  `AspN Digested` SUMO on D (mzLib #1431). None of these appear in `expected/`.
- **S3:** D3 is phospho-enriched, so it gets type P rows only.
- **S4:** in D1, S1_F1 + S1_F2 are one sample, and S8_1 / S8_2 are two injections of one animal, kept as two runs
  with one replicate. That gives 9 runs (`n` = 9) and 8 replicates.
- **S5:**
  - the protein N-terminus is `P3:@protein_n_term:...`;
  - pooled rows use UNIMOD keys (`P1:S13:UNIMOD:21`);
  - P1 S13 (Common Biological) and P2 S5 (UniProt) are both UNIMOD:21 on different proteins, so they stay two sites.
- **Type A edge cases:**
  - P1 S13 and K17 share a peptide: the row is `overlapping`, with no `q`.
  - D2's P3 occupancy is constant: ρ is undefined, so `value`, `p`, `q` and `sign` are empty, and the row is not pooled.

Compare numbers to 1e-12 relative. Text columns must match exactly.
