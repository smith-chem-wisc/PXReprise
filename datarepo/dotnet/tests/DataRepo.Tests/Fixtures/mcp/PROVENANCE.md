# mcp fixture

Expected answers for `DataRepo.Mcp` (`mcp.py`, `sandbox.py`), written by the Python on 2026-10-04: datarepo
**0.32.0** (`src/` identical to master `908f2bf` but for line endings), DuckDB **1.5.5**, pydantic-core **2.41.5** (pydantic 2.12.4), mcp **2.2.0**,
Python 3.13.11, Windows, machine time zone America/Chicago. The C# side runs DuckDB.NET.Data.Full 1.5.5 (DuckDB 1.5.5).

## What is compared

`mcp_parity.py` builds a corpus of tool calls from the catalog it is pointed at (every table described, ids
taken from the catalog for `search`, ~190 SQL statements covering answers, every refusal, DuckDB's own errors,
the empty-table guard, CTE and computed-column forgeries, opaque table functions, `pep` through aliases, CTEs
and `*`, study tables, the row/character caps, the watchdog and every DuckDB type), sends each call through
`mcp.CatalogServer` as `bound_tools` does (a `DataRepoError` becomes `{error, message, hint}`; anything else is
recorded as an exception, which the SDK reports as a tool error), and records the answer as the Python SDK
renders it for the agent: `pydantic_core.to_json(result, fallback=str, indent=2)`. `McpParityTests` sends the
same calls through the C# server and compares the text byte for byte.

Normalised, and nothing else: `elapsed_seconds` (a wall clock), `served_by` (the serving package's version) and
the catalog's path as each side was given it. Six lists are compared as multisets, because the Python's
`ORDER BY` for them is not total and DuckDB returns tied rows in a different order from one call to the next,
in the Python itself (`open_findings`; `search` hits for modification, sample, definition, localization,
peptide). A call the Python answered with a non-`DataRepoError` exception must raise one in C# too.

`pytz` must be importable for the Python to return a TIMESTAMP WITH TIME ZONE at all (without it DuckDB's
client raises "Required module 'pytz' failed to import", and `SELECT * FROM runs` is a CatalogError). The
script puts a `pylib/` folder beside itself on `sys.path` for that; it was `pip install --target pylib pytz`
(2026.5).

## Files

- `catalog_study.duckdb.gz`: `gzip -9 -n` of the catalog `build_study_catalog.py` wrote, run from `Fixtures/`:
  the `python-0.32.0/PXD999999` bundle plus `catalog/store`'s aging study delivery and logs artefact, built as
  `datarepo build --study-latest aging` does. Catalog id `ad07b102de0f2f34`, the same as
  `catalog/expected_fixture.json`. It is the catalog with study rows on samples (`study_tables_on_these_samples`)
  and a loaded study layer.
- `expected_fixture.json.gz`: `python mcp_parity.py fixture.duckdb expected_fixture.json` on
  `site/catalog.duckdb.gz` (catalog `153cf55067d58441`), then `gzip -9 -n`. 367 calls.
- `expected_study.json.gz`: the same on `catalog_study.duckdb`. 367 calls.
- `python_tools.json`: `tools/list` from the Python server over real stdio (`stdio_client.py`, the `mcp`
  package's client), for the in-process SDK test: names, titles, descriptions, input and output schemas.

## Real data (`[Category("RealData")]`)

`OnTheRealCatalogEveryAnswerIsPythons` reads aging's serving catalog (`F:/aging_data/repo/catalog.duckdb`, read
only) and needs `DATAREPO_MCP_PARITY_DIR`, a folder holding `real.json`:

    python mcp_parity.py F:/aging_data/repo/catalog.duckdb <dir>/real.json --real

`--real` adds about 60 calls in the style of aging's benchmark (`aging/design/QUESTIONS.md`: A1/A5/A8-A10,
B1/B3/B5, C1/C4/C6/C8, D6/D17, K1, G1, the pep and J traps). It is inconclusive, not failed, when the catalog
has been rebuilt since `real.json` was written.

Regenerate only on purpose, and say why here.
