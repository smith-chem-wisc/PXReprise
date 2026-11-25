# ptmqtl-reference-v3

Copied verbatim from ptmQtl's `results/reference_case_v3` (G62, 2026-11-23): `inputs/`, `expected/` and `README.md`. Built
by ptmQtl's `tools/ReferenceCase` against the released mzLib 1.0.600. That builder now lives here, as
`tools/ptmqtl-reference` (`dotnet run -c Release --project tools/ptmqtl-reference -- <dir>`), and regenerates every file
of `inputs/` and `expected/` identically (checked 2026-11-23, line endings aside). The README's build line names its old
home. It applies `ptmQtl:DEF-PTM-PAIR v3` (bounds, depth
and D78's depth null) and `ptmQtl:DEF-SITE-TRAIT v2` through mzLib calls only. Synthetic data; not an age effect.
The ptmqtl.* engines must reproduce `expected/` from `inputs/`: numbers to 1e-12 relative, text exactly. Since the
engine's site-trait fit became DEF-SITE-TRAIT v2, `expected/trait_effects.tsv` is compared here too, and v1's fixture
(`ptmqtl-reference-v1`, which nothing read any more) was removed; it stays in ptmQtl as `results/reference_case_v1`.
Never edit these files here; a new reference case is a new folder.
