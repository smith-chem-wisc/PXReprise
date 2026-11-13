# catalog fixture

Expected outputs for `DataRepo.Catalog` (`catalog.py`), written by the Python on 2026-10-04: datarepo
**0.32.0** (this worktree's `src/`, master `efcbb76`), DuckDB **1.5.5** under Python 3.13, Windows. The C#
side runs DuckDB.NET.Data.Full 1.5.5, which ships the same DuckDB 1.5.5, so the SQL runs on the same engine.
Nothing here was written by hand except the inputs: the study delivery's values, the artefact's rows
and the rewritten `g74` cells, all described below and all written through the Python's own writers.

## How a catalog is compared

Each catalog is dumped object by object (every table and view in `information_schema.tables`):

- `columns`: `(column_name, data_type)` in ordinal order, so DuckDB's types are compared too;
- `count`, and `rows`: each row as DuckDB's own `to_json(t)::VARCHAR`, so a float or a list is printed by
  the engine, not by either language. Rows are sorted, except in the five tables the Python fills in a
  meaningful order (`catalog_bundles`, `catalog_study_bundles`, `catalog_engine_artefacts`,
  `catalog_tables`, `catalog_checks`), which are taken `ORDER BY rowid` and compared in that order;
- `rows_hash_sum` instead of `rows` for a table over `full_limit` rows (real data only):
  `sum(hash(to_json(t)::VARCHAR)::HUGEINT)`, order-insensitive;
- view definitions (`duckdb_views().sql`) and indexes (`duckdb_indexes()`).

Left out, because they say who built the catalog, when, or where, not what is in it:
`catalog_meta.catalog_id / builder_version / built_utc / notes` and the `path` column of
`catalog_bundles`, `catalog_study_bundles` and `catalog_engine_artefacts`. `dataset_databases.database`
and `protein_genes.database` are kept apart under `platform`: they are `pathlib.Path(p).name` of a Windows
path, which is the file name on Windows and the whole string on Linux, in the Python as in the C#, so
they are compared on Windows only. `ptm_sites_by_chemistry.modification_names` is `list_sort`ed first: the
view builds it with `list(DISTINCT ...)` and no ORDER BY, so its order is not defined and was seen to
differ between two evaluations of the same view.

The C# test (`CatalogTests.Dump`) runs the same SQL as the generator's dump, word for word.

## Files

- `store/_study/aging/fc2561f2317e8df7/`: a study bundle written by `study.write_study_bundle` from a
  two-table delivery (`sample_ages` for the fixture's two samples, one `age_effects` row for PXD999999).
  `study.json` names the scratch paths of the delivered TSVs; the catalog reads only the Parquet and the
  manifest's identity fields.
- `store/_engine/logs.resolve_genes/c13935d24453e173/`: an engine artefact written by
  `runner.write_artefact` with five `gene_resolutions` rows on the fixture's target database
  (`89fb8c7a...`): resolved, a two-gene `multi_gene`, `not_in_source`, and a contaminant accession that
  `protein_genes` must not map.
- `expected_fixture.json` (CI): the dump of the catalog the Python built from `python-0.32.0/PXD999999`
  plus those two, with `select_bundles`, `select_study_bundles(latest=["aging"])`, `select_artefacts`
  and `build_catalog`, as `datarepo build --study-latest aging` does. `_about` has the Python's catalog id
  (`ad07b102de0f2f34`), the study bundle and artefact ids and the index count.
- `g74/sample_characteristics.parquet` + `expected_g74.json` (CI): the fixture bundle's eight
  `sample_characteristics` rows rewritten in place to names (a name with a term, `characteristics[...]`
  outranking `factor value[...]`, a header needing `lower(trim())`, two names under one header, a
  factor-value-only column), and the Python's `samples` and `sample_characteristics` tables from a catalog
  of the bundle with that file swapped in. The fixture's own cells are all `not available`, and so are
  aging's in the real catalog below, so this is the only place `samples.<column>_name` is filled.
- `expected_real.json` (`Category("RealData")`, skipped without `F:/aging_data`): the dump, `full_limit`
  100, of the catalog the Python built from aging's store:

      python -m datarepo.cli build F:/aging_data/batch/manifest.yaml PXD031782 PXD047289 PXD047292
          PXD047293 PXD051203 PXD051644 PXD056433 PXD056458 PXD058248 PXD058253 PXD036557 PXD035446
          PXD012754 --store F:/aging_data/repo/store --latest --study-latest aging --out <scratch>

  13 datasets (the ten the aging study layer delivers for, plus three), study bundle `bfd1f55807f661f7`,
  eight logs artefacts; 4,314,663 rows. `_about` lists the bundles in build order and the C# test pins them,
  so a later re-ingest leaves it on these bundles, and a bundle removed from the store skips it.

Regenerate only on purpose, and say why here.
