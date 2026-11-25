# ptmqtl-reference-v4

Built here by `tools/ptmqtl-reference` (`dotnet run -c Release --project tools/ptmqtl-reference -- <dir>`), 2026-11-25,
against the released mzLib 1.0.601: `inputs/` and `expected/` as the program writes them, and `README.md`. ptmQtl keeps a
pointer to it as `results/reference_case_v4`. It applies `ptmQtl:DEF-PTM-PAIR v3` and `ptmQtl:DEF-SITE-TRAIT v3`
through mzLib calls only. Synthetic data; not an age effect.

The ptmqtl.* engines must reproduce `expected/` from `inputs/`: numbers to 1e-12 relative, text exactly. It replaces
v3 (`ptmqtl-reference-v3`), whose pair rows and depth-null counts it keeps unchanged; v3 stays in ptmQtl as
`results/reference_case_v3`, and its files are in this repository's history at 8fc5ae8. Never edit these files here; a
new reference case is a new folder, and a rebuild on a new mzLib is recorded in this file.
