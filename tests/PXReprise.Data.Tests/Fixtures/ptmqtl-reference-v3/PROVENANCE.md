# ptmqtl-reference-v3

Copied verbatim from ptmQtl's `results/reference_case_v3` (G62, 2026-11-23): `inputs/`, `expected/` and `README.md`. Built
by ptmQtl's `tools/ReferenceCase` against the released mzLib 1.0.600. It applies `ptmQtl:DEF-PTM-PAIR v3` (bounds, depth
and D78's depth null) and `ptmQtl:DEF-SITE-TRAIT v2` through mzLib calls only. Synthetic data; not an age effect.
The ptmqtl.* engines must reproduce `expected/` from `inputs/`: numbers to 1e-12 relative, text exactly. This engine's
site-trait fit is still DEF-SITE-TRAIT v1, so `expected/trait_effects.tsv` is not yet compared here; v1's fixture is.
Never edit these files here; a new reference case is a new folder.
