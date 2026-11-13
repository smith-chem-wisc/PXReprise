# readers-sdrf fixture

Expected outputs for `ReadersSdrfTests.cs`, which checks `DataRepo.Ingest.Readers` (port of
`src/datarepo/readers.py`) and `DataRepo.Ingest.Sources.Sdrf` (port of `src/datarepo/sources/sdrf.py`).

Written on 2026-10-04 by a scratch script (not committed: no Python ships, PORTING.md) run in a clean venv
with **pyMzLib 0.4.0** (`pip install mzlib==0.4.0`, Python 3.13.11), whose bridge is built on
**mzLib 1.0.593**, the same mzLib the C# references. The script imported `src/datarepo/readers.py` and
`src/datarepo/sources/sdrf.py` from this commit's tree (registering a bare `datarepo` package so that
`datarepo/__init__.py`, which needs pyarrow, is not run) and called them exactly as the ingester does:

- `readers.read_psmtsv(path, log)` -> the columns dict
- `readers.read_occupancy(path, log)` -> `record_count`, `returned_count`, `row_count`,
  `truncated_cell_count`, `failed_fields`, `absent_fields`, `column_names`, `sample_labels`, `records`
- `readers.read_sdrf(path, log)`, `readers.read_tsv(path, log, note)` + `readers.iter_dicts(...)`,
  `readers.read_results_txt(path, log)`
- `sources.sdrf.parse(path, dataset_id, default_organism=..., log=log)` -> `samples`, `characteristics`,
  `assays`, `run_facts`, `sample_of_run`, `columns`; and `sources.sdrf.parse_value(raw)` on 15 cells
- every reader log as `log.entries`. Python wrote backend `pymzlib`; the C# writes `mzlib` by decision, and
  the tests map one to the other. Everything else in a log entry is compared as written.

Values are compared as Python's `json.dumps(value, ensure_ascii=False)` (C#: `PyFormat.Json(value,
sortKeys: false)`), so int-vs-float is part of every check. That matters: the bridge serialised doubles
with System.Text.Json, which writes `-1.0` as `-1`, so Python received an `int` for every whole-valued
double (`spectral_angle` -1, `pep` 0, an occupancy `numerator` 2, a `fraction` of 1).

## Files

- `expected_fixture.json`: full expected outputs for the repository's fixture tree
  (`tests/data/work_root/run_test/PXD999999`: both `.psmtsv`, `AllQuantifiedProteinGroups.tsv` occupancy,
  the three quant TSVs, `results.txt`, the SDRF) and for the synthetic inputs below. Keys are paths
  relative to the repository root. These tests run everywhere, CI included.
- `inputs/`: synthetic inputs written by the same session's script (byte-exact; this folder is `-text`):
  - `edge.sdrf.tsv`: a repeated column name (`comment[modification parameters]` twice,
    `characteristics[organism part]` twice), reserved words in any case, `NT=`/`AC=` cells with spaces,
    a plain value with `=`, per-column and row-level source comments (G62/G75), a `-calib.mzML` data file,
    a TMT channel, `label free` / `none` labels, a data file falling back to `assay name`, a row with no
    source name, a SHORT (ragged) row, a row with no run, `02` / `1.0` / `x` replicate cells, an unknown
    organism (parsed with and without a manifest default).
  - `edge.tsv`: a BOM, CRLF / LF / bare-CR line ends, blank lines, a line of one tab, quote characters
    (literal under `QUOTE_NONE`), short rows, no final newline. `empty.tsv`, `header_only.tsv`.
  - `occupancy_edge/AllQuantifiedProteinGroups.tsv`: the fixture's protein-group table with a count cell
    cut mid-site, an intensity cell replaced by `Output too long for Excel`, a malformed count cell
    (mzLib's `FormatException`, so `failed_fields`), and fractions of exactly 1 and 0. (mzLib identifies a
    file by its name, so the folder carries the variant.)
  - `edge_results.txt`: every line shape the four `results.txt` regexes take, a repeated total (the last
    wins), a per-run `All MS2 Scans`, a 5% line (ignored), a BOM and a CRLF.
- `expected_real_digest.json`: the same reads on real aging files under `F:/aging_data` (read only), kept
  as digests: per-column sha256 of the `json.dumps` of the column (`.psmtsv`), sha256 of the records /
  rows / dicts / each parse output, counts, the reader logs, and a few sample rows. The `RealData` tests
  use it and are ignored where the files are absent. Files:
  - `.psmtsv`: `run_2026-09-18/PXD036557` (2,502 PSMs, 2,189 peptides), `run_2026-09-18/PXD048658`
    (13,692 / 12,649), `run_2026-09-21/PXD028282` (32,422 / 18,832); 73 columns each.
  - occupancy, the three quant TSVs and `results.txt` of those three searches.
  - all 27 SDRFs matching `F:/aging_data/run_*/*/*/metadata/*.sdrf.tsv` on 2026-10-04 (26 of them
    repeat a column name), each read and parsed (dataset id from the folder, default organism
    `NCBITaxon:9606`).

## Result when written

All equal: every column and every value of the six real `.psmtsv` reads and the fixture's two, every
occupancy record, every SDRF cell and every parse output. Regenerate only on purpose, and say why here.
