# `datarepo mcp`

Serves **one** catalog to an AI agent over stdio, as an MCP server. This is layer 3 of the
[architecture](architecture.md) and step 3 of the FRAMEWORK roadmap — the first of the four "doors"
onto the catalog, and the only one blocked by nobody: no host, no proxy, no operator decision.

```bash
pip install 'datarepo[mcp]'

datarepo mcp --catalog /path/to/catalog.duckdb --install   # register with Claude Code
datarepo mcp --catalog /path/to/catalog.duckdb --check     # open it and report, without serving
datarepo mcp --catalog /path/to/catalog.duckdb             # serve (the client runs this, not you)
datarepo mcp --list                                        # what is registered
datarepo doctor                                            # SDK present? server registered?
```

`--install` writes the Claude Code config so no JSON is hand-edited. It refuses to repoint an entry
it did not write (`--force` overrides, `--name` registers a second catalog alongside), keeps the
rest of the file, and writes through a temporary file — that config is your whole Claude Code
state, not ours. Restart Claude Code afterwards.

## One catalog, by explicit path

The path is required and the catalog is **never auto-discovered**. A server that went looking for a
catalog would answer today from a working build and tomorrow from a release, with nothing in either
answer saying which — and the same question answered twice, differently, with no way to tell what
each saw, is the failure this whole repository keeps writing threads about.

Exploration is **not** restricted to release catalogs, because that would make new data unqueryable
until a release is cut. Any answer can be pinned after the fact instead, since a release already
names its exact bundles (`build --release`).

To serve a release and a working catalog at once, register both:

```bash
datarepo mcp --catalog .../releases/v0.1/catalog.duckdb --name datarepo-v0.1 --install
datarepo mcp --catalog .../catalog.duckdb                --name datarepo      --install
```

## Three tools

Three, and a fourth only when a benchmark shows the agent getting a specific answer wrong. The
eight curated tools in FRAMEWORK section 4 are a menu, not a build list: most are thin wrappers
over SQL the agent would write anyway, and several answer questions no measurement has asked yet.

### `datarepo_describe(target=None, detail="concise")`

- **no target** — the catalog: its datasets with their counts, every table with its row count,
  which tables are present but **empty**, and the open findings.
- **a table or view name** — its columns, each with the catalog's own type and the schema's own
  sentence about what it means. `detail="detailed"` returns the same facts as objects rather than
  one line per column.
- **an enum name** (`Acquisition`, `QuantMethod`, …) — its permissible values.
- **a definition id** (`aging:DEF-PSM-1PCT`, `PROVISIONAL:…`) — the published text behind a stored
  number, and which metrics cite it.
- **`"tables"`** — every table with its kind and what one row of it is.

The column meanings are generated from `schema/datarepo.yaml` into `src/datarepo/_schema_docs.py`
by the same tool that generates the Arrow schemas, and CI fails on drift. A description an agent
reads therefore cannot fall out of step with the column it describes.

### `datarepo_search(query, kind=None, limit=25)`

Finds the **ids** behind a name: a dataset accession, a UniProt accession, a gene symbol, a peptide
sequence, a modification, a tissue, a cell type, a raw file, a definition. `kind` restricts it;
omitted, every kind is searched, so a query never has to be classified before it can be answered.

It returns the hits **and `searched`** — every source it looked in, with the rows that source holds
and the hits it gave. That list is the point:

```
searched: [ {source: "protein_localizations", rows: 0, hits: 0}, ... ]
```

`protein_localizations` holds 0 rows in every catalog built so far — the organelle map belongs to
`go`, and dataRepo only stores it — so a search for `mitochondria` that returned a bare "no hits"
would let an agent conclude that no protein is mitochondrial. Instead the result names the empty
table and says in as many words that an undelivered table is not a negative answer. An absence is
also scoped to the datasets actually held, never to proteomics.

Decoys are **marked, not hidden**. `protein_index` is built from the search's protein *list*, so
`LMNA` and `DECOY_LMNA` both match a search for `LMNA`; unmarked they would read as two proteins.

### `datarepo_sql(query, max_rows=None)`

One read-only DuckDB statement. Results carry `columns`, `rows`, `row_count`, `truncated`, the
limits in force, and **`tables_touched`** — every catalog table the statement referenced, with its
row count, parsed out of DuckDB's own serialization so aliases, CTEs and subqueries are seen
through and a table name inside a string literal is not counted.

When any of those tables is empty, the result also carries **`empty_tables`** and a sentence
saying what that means. This is the most important field in the envelope. A join over two empty
tables returns `rows: []`, and an agent that reads that as a negative finding has just answered
*"organelles do not age at measurably different rates"* from two tables nobody has delivered yet.
The guard cannot be skipped, which is why it is a field and not a fourth tool.

Prefer the acceptance views — `psms_1pct`, `peptidoforms_1pct`, `protein_groups_1pct` — which apply
the producing search engine's rule once so no caller restates it. Each one states the rule it
applies in its own `describe`.

## Every answer carries its provenance

Every result from every tool carries a `provenance` block: the `catalog_id`, the schema and catalog
versions, who built it and when, the instance, the release if there is one, the version of
`datarepo` serving it, and the bundles the answer drew on.

Where the rows carry `bundle_id` or `dataset_id`, the bundle list is narrowed and says so. Where
they do not, it is the catalog's whole bundle set and says *that* — it never guesses a narrower
claim than the rows support.

**The ids are validated against the catalog, not trusted from a column's name.** They are values in
columns that happen to be called `bundle_id` and `dataset_id`, and a query can put anything there:
`SELECT 'deadbeefdeadbeef' AS bundle_id, count(*) FROM protein_groups_1pct` once had a bundle id
that exists in no catalog anywhere returned as the provenance of 8,055 real rows. Unknown ids now
fall back to the whole catalog and are listed under `unrecognised_ids_in_result`.

A *real* id aliased into a result cannot be caught that way — the id is genuine, the rows just
aren't from it — so the wording does not overstate. It says the bundles **appear in the rows
returned**, and points at `tables_touched` for what was actually scanned.

## Empty is not negative, and neither is NULL

Roughly half of aging's benchmark questions cannot be answered from the current data — the
organelle map belongs to `go`, the age effects to aging, and neither has been delivered. The tools
are built so that "no data" stays distinguishable from "no", in four places:

| Where | What it says |
|---|---|
| `describe()` | `tables_empty` — every table present with zero rows |
| `describe(table)` | per-column non-null counts, and `all_null` on a column that holds nothing |
| `search()` | `searched` with each source's row count, `searched_but_empty`, the columns matched and which of them are entirely NULL |
| `sql()` | `tables_touched`, and `empty_tables` when any is empty |

The column-level half matters as much as the table-level half. `samples` holds 57 rows with
`organism_part`, `cell_type`, `disease`, `condition` and `cell_line` NULL on every one, so a search
for `plasma` returning `rows: 57, hits: 0` reads as a considered negative unless something says the
columns are empty. `peptidoforms.is_isoform_specific` is NULL on all 394,255 rows while its
description names the exact question class it answers — the natural query returns a clean,
confident false negative.

Two more presentation traps are handled rather than hidden. `protein_index` is built from the
search's protein **database**, so `LMNA` and `DECOY_LMNA` both match a search for LMNA: decoys are
marked `is_decoy` and sorted last. And `protein_index.n_datasets_1pct` counts peptide-**or**
protein-level acceptance, which is looser than "identified at 1% protein FDR" — 879 of 9,130 pairs
it counts are absent from `protein_groups_1pct` — so its description says so in as many words.

## What the sandbox does, and what it does not

`datarepo_sql` runs inside `datarepo.sandbox`, which is **stage one** of a two-stage design. What
ships now bounds **blast radius and provenance**; it is not claimed as a security boundary, because
locally the agent already has your filesystem through Claude Code. The sqlglot AST allow-list in
FRAMEWORK section 4 is stage two, for the day a public no-login endpoint exists and the thing being
bounded is an attacker.

| | |
|---|---|
| Connection | `read_only=True` **and** `enable_external_access=false` |
| Filesystem | `disabled_filesystems='LocalFileSystem'`, set immediately after connecting |
| Statements | one per call; `SELECT`, `EXPLAIN`, `PRAGMA`, `SHOW` only — refused **by name, before running**, with a message saying what to send instead |
| Rows | 1,000, with a `truncated` flag and a sentence saying the answer is not whole |
| Characters | 50,000, dropping **whole rows** — never half a peptidoform |
| Time | 30 s, enforced by a watchdog thread calling `con.interrupt()` |

Three things there were measured rather than assumed, on DuckDB 1.5.5, and each is now a test:

- **`read_only=True` alone is not a sandbox.** A read-only connection reads any CSV on disk.
- **`enable_external_access=false` does not close `ATTACH`.** Another DuckDB file could still be
  attached and queried — returning rows that are not in this catalog, under a result labelled with
  this catalog's `catalog_id`. That is a provenance failure before it is a security one, and it is
  why the filesystem is disabled too. Both settings are one-way once the database is running.
- **The obvious timeout probe no longer proves anything.** DuckDB 1.5 answers
  `SELECT count(*) FROM range(3000000000)` from metadata in half a second.

One consequence worth knowing: `disabled_filesystems` belongs to the database *instance*, not the
connection, and DuckDB refuses a second connection to an open file with a different config. So
`datarepo query` cannot open a catalog a server in the **same process** already holds. The server
is its own process, so this costs nothing in practice.

## The bar: zero silently-wrong answers

Not a percentage. `design/SCHEMA_COVERAGE.md` says 94 of aging's 168 benchmark questions wait on a
producer, so a score would mostly measure aging's data: it would jump when they deliver age effects
with nothing about the server having changed.

The bar instead: on an answerable question, right; on an unanswerable one, **"no data" rather than
an invention**. Either kind of wrong is a failure. The percentage gets reported, never targeted.

The harness is ours, so iteration is fast. The **questions are aging's**, read from their master
and never copied, and the score goes to them in a thread rather than being claimed here.
