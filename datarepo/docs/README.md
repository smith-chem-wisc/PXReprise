# dataRepo documentation

Start here. Every page below is a reference for one part of the system; this page says which one you
want and in what order.

## Pick your path

**"I am new here."**
→ [**getting-started.md**](getting-started.md): ten minutes, from a download to a catalog, a
site and an agent, on the example instance shipped in this repository. Every command on it was run.

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

**"I run an instance: I have searches and want them served."**
→ [**operating.md**](operating.md): the operator's loop (install a release, write the manifest,
ingest, publish, host), and when an upgrade costs a re-ingest.
→ [**build.md**](build.md): turning bundles into one DuckDB catalog, choosing bundles, and what a
failed check means.
→ [**site.md**](site.md): the static site, `llms.txt` and Croissant, with no server.

**"I want the exact flags."**
→ [**cli.md**](cli.md): every command and argument, generated from the program's own help.

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
   This is why the program's version is *not* in a bundle id, and why the ingest path's version
   (`BundleWriter.IngesterVersion`) is. `Manifest.ContentFields` / `NonContentFields` classify every
   manifest field with its reason.
4. **Emit the data, let the consumer filter.** No run-time switch changes what a file contains,
   because changing your mind then costs a re-run of everything. The acceptance views
   (`psms_1pct` and friends) are views, not filters applied at write time.
5. **Anything that certifies an answer must come from the engine, not from the query or its output.**
   A query that named a CTE after a real table once came back stamped with that table's row count and
   a real bundle id. Provenance is a fact about the server.

## Glossary

| Term | Meaning |
|---|---|
| **instance** | One project's collection of reanalysed datasets, described by one `manifest.yaml`, with its store, catalog and site. dataRepo is the software; an instance is the data. |
| **operator** | Whoever runs an instance's steps (ingest, run, publish). dataRepo itself operates nothing (D27). Today: `aging`. |
| **producer** | The pipeline that searched the datasets and wrote the files `ingest` reads (MetaMorpheus output, `provenance.json`, the SDRF). |
| **manifest** | `manifest.yaml`: the operator's list of datasets, each `include`, `hold` or `exclude`, with its facts. The contract every command reads. |
| **bundle** | One dataset's results as Parquet tables plus `bundle.json`, written by `ingest`. Immutable, and named by the hash of its inputs, the schema version and the ingester version. |
| **store** | The folder of bundles: `<store>/<accession>/<bundle id>/`. Study bundles live under `_study/`, engine outputs under `_engine/`. |
| **catalog** | One DuckDB file built from chosen bundles by `build` or `publish`. Named by a hash of what went in (`catalog_id`), which every answer carries. |
| **study layer** | Tables one study adds beside the core, keyed on core ids, never altering a core table (for example, `aging`'s age effects). Delivered with `datarepo study`. |
| **engine** | Code another project owns that runs on stored results (logs' gene resolution, go's annotation). Its output is an **artefact** beside the bundles. |
| **finding** | A recorded fact about what is wrong or limited in a dataset (a low identification rate, a skeleton SDRF). Shown before the counts it qualifies. |
| **definition id** | `owner:DEF-NAME`: which project defined a number and where its text is. Every stored number carries one. |
| **acceptance view** | `psms_1pct`, `peptidoforms_1pct`, `protein_groups_1pct`: the search engine's own 1% FDR rule, applied once so no query restates it. |

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
| `docs/schema/core.md`, `docs/schema/study-aging.md` | `python tools/build_docs.py` (needs `requirements-dev.txt`) |
| the table definitions and column descriptions (`dotnet/src/DataRepo.Bundle/Generated/`) | `dotnet run --project dotnet/src/DataRepo.SchemaGen` |
| `docs/cli.md` | `python tools/build_cli_docs.py` (builds the program first; needs the .NET 10 SDK) |

CI fails on drift, so after any schema edit run the first two, and after any change to a command's
arguments or help text, the last. `examples/ingested_bundle.yaml` was written by the Python release
(`tools/build_example_bundle.py`, which needs that frozen package) and is not regenerated by the C#
program.
