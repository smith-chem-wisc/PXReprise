# study fixture

Expected outputs for `StudyTests.cs`, which checks `DataRepo.Study.StudyWriter` (port of `src/datarepo/study.py`)
and `DataRepo.Study.PyCsv` (Python's `csv.DictReader`, which `study.read_table_file` uses).

Written on 2026-10-04 by a scratch script (not committed: no Python ships, PORTING.md) run with Python 3.13.11,
pyarrow and PyYAML from the repo environment, importing `src/datarepo` from this commit's tree
(`PYTHONPATH=src`, datarepo **0.32.0**, `STUDY_INGESTER_VERSION` **0.6.0**). For each folder under `cases/` it
called, exactly as `datarepo study <case>/study.yaml --store <scratch>` does:

    manifest = study.load_study_manifest(str(case / "study.yaml"))
    result = study.write_study_bundle(manifest, store=<scratch store>)

## Files

- `cases/<name>/`: the deliveries. The script wrote every byte (LF line endings unless a case is about CRLF),
  so `.gitattributes` marks this tree `-text`: the file bytes are hashed into the bundle ids.
  - `example`: `examples/study_delivery` as committed (`git show HEAD:...`, LF).
  - `csv_lists`, `reserved`, `parquet`, `version_true_string_definitions`, `undeclared_definitions_unchecked`:
    deliveries Python writes. Between them: a BOM, CRLF, a CSV with quoted delimiters, doubled quotes, a quoted
    newline, a quote followed by text, a file with no final newline; blank and short rows; `;` list cells with
    blanks and spaces; `NaN`/`inf`/`1_000.5`/`1e-3`/`2.7` in numeric columns; reserved-word values with delivered
    flags in every spelling; Parquet inputs typed `float32`, `int32`, `large_string` and `bool`;
    `study_manifest_version: true`, a string `definitions`, non-ASCII `instance`, an integer `delivery`, and a
    header-only table.
  - every other case: a delivery Python refuses, one per refusal path in `study.py` (and in
    `bundle.rows_to_table` as `study` reaches it).
- `expected.json`: per case, either `ok: true` with `bundle_id`, `row_counts` and `study.json` minus
  `written_utc` and `ingester.version` (source paths with the case folder replaced by `<dir>`, `/` separators),
  or `ok: false` with the Python exception's type name and message (same path normalisation).
- `python-0.32.0/<case>/<bundle id>/`: the bundle Python wrote, `study.json` and every Parquet file, copied
  as written (CRLF `study.json`: Python writes it in text mode on Windows). The test compares the C# bundle's
  rows with these, and its `study.json` text with these after masking `written_utc`, `version` and source paths.

## Not compared word for word

- `bad_yaml`: the text after `is not valid YAML: ` is PyYAML's parser message; only the prefix is compared.
- `bad_number`: Python raises `ValueError: could not convert string to float: 'abc'` from `bundle._coerce`, not an
  `IngestError`; the C# raises `FormatException` with .NET's wording. The test checks the type only.
