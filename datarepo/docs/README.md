# dataRepo documentation

Start here. Every page below is a reference for one part of the system; this page says which one you
want and in what order.

## Pick your path

**"I want to ask this data a question."**
→ [**querying.md**](querying.md) — the cookbook. Real SQL against a real catalog, with the output it
actually returned, and a section on queries that look right and are wrong.
→ then [**limitations.md**](limitations.md), because most wrong answers here come from asking a
question the data cannot answer and getting a confident empty result.

**"I want an agent to use it."**
→ [**mcp.md**](mcp.md) — the three tools, the provenance every answer carries, and what the sandbox
does and does not do.
→ [**limitations.md**](limitations.md) again, harder. An agent cannot tell an empty table from a
missing fact unless you tell it.

**"I produce data and want it ingested."**
→ [**ingest.md**](ingest.md) — the manifest contract, what the ingester reads, what it writes, and
how its counts reconcile against yours.
→ [**study.md**](study.md) if you also have model results (age effects, sample ages) to deliver.

**"I want to understand the design."**
→ [**architecture.md**](architecture.md) — how the pieces fit, and which parts are decided versus
proposed.
→ [**schema/core.md**](schema/core.md) — every table, column, type and vocabulary. Generated from
`schema/datarepo.yaml`; do not edit by hand.

**"I want to publish the public website."**
→ [**site.md**](site.md) — `datarepo site` writes static pages, `llms.txt` and Croissant from one
catalog, with no server.

**"I need to run it."**
→ [**build.md**](build.md) — turning bundles into one DuckDB catalog, choosing bundles, and what a
failed check means.

## The whole system in one paragraph

A **producer** (currently [`aging`](https://github.com/trishorts/aging)) reanalyzes public PRIDE
datasets and writes search and quant output. **`datarepo ingest`** reads the producer's manifest and
turns one dataset's run into a **bundle**: Parquet tables, content-addressed on
`(inputs, schema version, ingester version)`. **`datarepo study`** does the same for a study layer's
model results, separately addressed so delivering a model never re-identifies a search bundle
somebody cited. **`datarepo build`** loads bundles into one **DuckDB catalog**, also content-addressed.
**`datarepo mcp`** serves exactly one catalog to an agent over stdio, and every answer carries the
`catalog_id` it came from.

```
producer run ──ingest──> bundle(s) ──build──> catalog ──mcp──> agent
                  │                     │                 │
          content-addressed     content-addressed   every answer carries
        on inputs+schema+       on bundle ids +      its catalog_id
          ingester version      schema + builder
```

## The five rules that explain most of the design

These recur in every page, so they are worth reading once here.

1. **`required: true` is a claim that a true value always exists.** `Protein.organism` was required,
   so 339 contaminant entries read `NCBITaxon:9606` — porcine trypsin and bovine albumin served as
   human. The failure was not a crash; it was a falsehood the tools fully supported.
2. **`empty` and `unknown` must not share a representation.** A table with no rows and a fact nobody
   recorded are different answers. `describe` reports a 100%-NULL column separately from an absent
   one for this reason, and [limitations.md](limitations.md) exists because the catalog still has
   places where the two collapse.
3. **Anything that reaches a written row is an input to the content hash — and nothing else is.**
   This is why the package version is *not* in a bundle id, and why `INGESTER_VERSION` is.
   `manifest.CONTENT_FIELDS` classifies every field with its reason, and a test fails on a field in
   neither list.
4. **Emit the data, let the consumer filter.** No run-time switch changes what a file contains,
   because changing your mind then costs a re-run of everything. The acceptance views
   (`psms_1pct` and friends) are views, not filters applied at write time.
5. **Anything that certifies an answer must come from the engine, not from the query or its output.**
   A query that named a CTE after a real table once came back stamped with that table's row count and
   a real bundle id. Provenance is a fact about the server.

## Conventions in these files

| Prefix | Meaning |
|---|---|
| `D<n>` | A locked decision, recorded in `.project/state.yaml` |
| `G<n>` | An open gap |
| `DATAREPO-<n>` | A question dataRepo has asked another project |
| `REQ-<PROJECT>-<n>` | A question another project must answer |
| `DEF-*` | A metric definition owned by QuantProject |

Cross-project correspondence lives in [`design/threads/`](../design/threads/), one directory per
peer, mirrored byte-identically in that peer's repository. Messages are never edited after posting.

## Generated files — do not hand-edit

| File | Regenerate with |
|---|---|
| `docs/schema/core.md`, `docs/schema/study-aging.md` | `python tools/build_docs.py` |
| the ingester's Arrow schemas | `python tools/build_tables.py` |
| `examples/ingested_bundle.yaml` | `python tools/build_example_bundle.py` |

CI fails on drift, so after any schema edit run the first two at minimum.
