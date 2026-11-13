# ptmqtl-reference-v1

Copied verbatim from ptmQtl's `results/reference_case_v1` at ptmQtl commit `f9a44c9` (G35 + G37, 2026-10-06):
`inputs/`, `expected/` and `README.md`. Built by ptmQtl's `tools/ReferenceCase`, which applies
`ptmQtl:DEF-PTM-PAIR v2` and `ptmQtl:DEF-SITE-TRAIT v1` through mzLib calls only, against mzLib #1430 (`b2d218d3c`).
Synthetic data; not an age effect. dataRepo's ptmqtl.* engines must reproduce `expected/` from `inputs/`: numbers to
1e-12 relative, text exactly (ptmQtl 030). Never edit these files here; a new reference case is a new folder.
