# modlist-proforma fixture

Expected outputs for `ModList.cs` / `Proforma.cs`, written on 2026-10-04 by running the Python modules
`datarepo.modlist` and `datarepo.proforma` of datarepo **0.32.0** (Python 3.13.11, Unicode 15.1) from a one-off
scratch script that is not in the repository. Every expected value here is what Python returned; nothing was
written by hand. Regenerate only on purpose, and say why here.

Inputs (all read-only):

- MetaMorpheus installs `F:/aging_data/mm_settings/{1.1.9,1.1.10,1.1.11}` (aging's search builds), the repo's
  fixture install `tests/data/work_root/mm_settings/1.1.11`, and a directory that does not exist.
- Real `Full Sequence` cells from `AllPSMs.psmtsv` and `AllPeptides.psmtsv` of four aging datasets searched by
  MetaMorpheus 1.1.11, `F:/aging_data/run_2026-09-21/{PXD034432,PXD051644,PXD043964,PXD010091}/04_search/mm/Task3SearchTask/`
  (120,179 distinct cells; 43,118 modified, read with `csv` tab-delimited, `QUOTE_NONE`), and the PXD999999 fixture's
  two files under `tests/data/work_root/run_test/`.

Files:

- `mm_1.1.11/Mods/*.txt`, `mm_1.1.11/Data/ptmlist.txt`: byte copies of the MetaMorpheus 1.1.11 install's
  modification files (MetaMorpheus, MIT; `ptmlist.txt` is UniProt's, CC BY 4.0), so `ModRegistry.FromMetaMorpheus`
  is tested on the real files in CI. (Git may normalize their CRLF line ends; parsing is unaffected.)
- `registry_<label>.json` for `1.1.11`, `1.1.10`, `1.1.9`, `fixture`, `missing`: `len(registry)`, `registry.sources`,
  every entry in `_by_name` order (name, sorted targets, unimod, mass, source, `unimod_curie`), and `lookups` as
  `[name, residue, index-into-entries-or-null]` for every modification name the parses below produced (with and
  without its residue), every entry name with residues None/""/"k"/"X"/each target, its upper-case form, and
  `<name> on <t>` variants, plus edge cases.
- `parse_<label>.json` for `1.1.11`, `fixture`, `missing`: `parse(s, registry)` as proforma, base sequence,
  `is_modified`, mods `[position, residue, name, category, unimod, mass, resolved]` and `unresolved`. For 1.1.11 the
  real cells are a committed subset (4,481 of the 48,930 compared: every distinct bracket token in at least two
  sequences per placement kind, plus even samples), followed by synthetic cases: every registry entry placed on a
  residue, the N terminus and the C terminus, and hand-made edge cases (nested and unclosed brackets, `-` termini,
  lower-case and spelled-out residues, newlines, non-BMP characters). Non-finite masses are written as Python's repr.
- `mod_token.json`: `MOD_TOKEN.match` (category, name, residue, or nulls) for every bracket token seen plus odd ones.
- `synthetic.json`: one hand-made flat file (`text`) with `_parse_entries(text, "synthetic.txt")`, the registry
  built from it, parses and lookups. It exercises Python's `float()` (underscores, Unicode digits, inf/nan, overflow),
  `str.strip`/`splitlines` (every Python line break), `_targets`, the IGNORECASE `DR   Unimod;` line (Python's `i`
  also matches U+0130/U+0131), ptmlist-style blocks without `//`, and the `+.6f` mass tag (half-even ties).
- `cache_fixture.json`, `cache_1.1.11.json`: a `ProformaCache` fed `inputs` in order: final `unresolved` (in
  insertion order) and `size`.
- `pycase.json`: `str.upper/lower/casefold` of every code point whose mapping is not itself, and the ranges where
  `str.isalpha()` is true, from Python 3.13.11's Unicode 15.1 database.

The full comparison (48,930 real cells, and the 1.1.9 and 1.1.10 synthetic sets: 71,658 parses) was too large to
commit (~25 MB); it was run once, and passed, with `DATAREPO_MODLIST_FULL` pointing at the script's output folder
(`ModListProformaTests.FullComparisonWhenAvailable`).
