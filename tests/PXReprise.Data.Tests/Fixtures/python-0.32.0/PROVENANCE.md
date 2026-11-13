# python-0.32.0 fixture

Written by the Python ingester, datarepo **0.32.0** (`INGESTER_VERSION` 0.22.0, schema 0.0.13), on
2026-10-04, from the repository's own test dataset:

    datarepo ingest tests/data/manifest.yaml PXD999999 --store <scratch>

- `PXD999999/aeb10630abbcaf72/`: the bundle's 17 Parquet tables and `bundle.json`, copied unchanged
  (the `sources/` copies are left out; the id is recomputed from `bundle.json`). The id is the one the
  getting-started tutorial publishes.
- `manifest_entry_declaration.json`: Python's
  `json.dumps(entry.content_declaration(), sort_keys=True, default=str, ensure_ascii=False)` for that
  entry, whose sha256 (`506a4b86...`) is the `manifest_entry` source in `bundle.json`.

The C# tests compare against these files, so they test the C# writer against what Python actually wrote.
Regenerate only on purpose, and say why here.
