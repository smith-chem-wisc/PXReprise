# go fixture

Expected outputs for `Sources/Go.cs`, written by the Python on 2026-10-05 (Python 3.13, Windows). Nothing here
was written by hand.

## Which Python

`src/datarepo/sources/go.py` at dataRepo `fcdedcb` (GO-D1: columns by name, the `entrapment_members` refusal),
file sha256 `72cc50bd3323d4adb9b1299a9af0e6ac977630fae32c11988575dc7132fc4fac`. That is later than the 0.32.0
tag: go.py changed after the release, and nothing in the Python ingest calls it (G86), so no 0.32.0 row depends
on either version. `tests/test_go.py` passed (15 of 15) on the same tree before the fixture was written.

## Inputs

go's own pre-release files in `tests/data/go` (see that folder's PROVENANCE.md; sha256 `43ebad3d...` and
`76d336cf...`), and 37 edited copies of them.

## `cases.json`

Each result is `{"value": ...}` or `{"error": <Python exception name>, "message": <str(e)>}`.

- `annotation_refused`, `categories_refused`: the two files read without `allow_prerelease` (both refused:
  `#!mzlib_release none`).
- `annotation`, `categories`: the files read with `allow_prerelease=True`, summarised as header, row count,
  file sha256, `go_release`, `mzlib_release` and `not_stored` (annotation) or the map's name, version and sha256
  (categories).
- `source_id`, `source_row`, `localization_rows` (139 rows), `category_rows` (17 rows): in full.
- `flag`: `_flag` of six cells.
- `cases`: 37 edited copies. Each has `edit` (a list of operations: Python's `str.replace(old, new, count)`,
  `+ text`, an insertion before an anchor; or the name of one of `tests/test_go.py`'s table transforms, which
  `GoTests.cs` ports), `allow_prerelease`, and `input_sha256` of the edited bytes, which the C# test checks
  before comparing anything. For an annotation copy: `read` and, when it reads, `source_row` and
  `localization_rows` as `{"n", "sha256"}` over `json.dumps(rows, ensure_ascii=False)`. For a category copy:
  `read` and, when it reads, `coverage` against the unedited annotation file and `category_rows` as a digest.

## How

The generator (`gen_go.py`) lived in the session scratchpad and was not committed (PORTING.md: no new Python in
the repository). It imported `datarepo.sources.go` from this worktree's `src/` and wrote
`json.dumps(out, ensure_ascii=False, indent=1)`.

Regenerate only on purpose, and say why here.
