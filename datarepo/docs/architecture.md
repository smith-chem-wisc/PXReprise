# Architecture

How dataRepo is meant to work, and which parts are **decided** and which are still **proposed**. The full
proposal, with its reasoning and the research behind it, is [`design/FRAMEWORK.md`](../design/FRAMEWORK.md).

## Decided

| # | Decision |
|---|---|
| D1 | **Hosting.** Prototype locally; production runs from a portable Docker Compose package, handed to operators (NCEMS). No Windows-only paths. |
| D2 | **Access.** Public from day one, no login anywhere. Rate limiting lives in the operators' proxy. Every release is versioned and gets a DOI. |
| D3 | **Licences.** Code MIT; data CC BY 4.0. |
| D4 | **Format.** A QPX-compatible superset: QPX names and types where QPX has a matching view, plus extensions for proteoforms, USIs, organelles and study layers. Each file records the QPX version it matches. |
| D5 | **Scope.** Human and rodent; DDA and DIA; label-free and TMT. These are real columns, and quantities are long (assay, feature, value). Ingesters are written as producers deliver each kind of data. |
| D6 | **Benchmark.** The question set comes from the producing project (aging). dataRepo is scored against it and doesn't set the science agenda. |
| D7 | **Open questions** are tracked with defaults in [`design/OPEN_QUESTIONS.md`](../design/OPEN_QUESTIONS.md). Work proceeds on the defaults. |
| D8 | **Code, not data.** This repo ships software. The producing project hosts the data instance: bundles, releases, DOIs and the deployed service. |

## Built

**Ingest (layer 1)** is implemented: `datarepo ingest` turns one dataset's pipeline output into one
immutable, content-addressed Parquet bundle. Reference: [`ingest.md`](ingest.md).

The parts of the proposal below that it settled in practice:

- **The manifest is the contract.** The ingester reads the producing instance's `manifest.yaml`
  rather than scanning its work root, so which run is canonical and whether it may be loaded is the
  producer's recorded decision (aging thread 006).
- **Parquet columns come from the schema.** `tools/build_tables.py` generates the Arrow schemas from
  `schema/datarepo.yaml`, so there is no second column list to drift.
- **Bundles are content-addressed.** The directory name hashes the inputs, the schema version and
  the ingester version, which makes re-ingest idempotent and makes a cited bundle safe.
- **Every bundle recounts itself** against the producer's own totals, and a disagreement becomes a
  finding rather than a silent difference.
- **Nothing is written that does not hold together.** Foreign keys are checked before the write.

**Catalog (layer 2)** is implemented: `datarepo build` loads an instance's bundles into one DuckDB
file that answers across datasets. Reference: [`build.md`](build.md).

The parts of the proposal below that it settled in practice:

- **The manifest is the contract here too.** A dataset the producer has withdrawn is refused even
  though its bundle is still on disk, and a dataset with several bundles is never guessed at: the
  build pins one or stops.
- **The catalog is derived, never the product.** It is one movable file built from the bundles and
  safe to delete, and it is content-addressed on them, so rebuilding is a no-op.
- **The producer's acceptance rule is applied once.** `psms_1pct`, `peptidoforms_1pct` and
  `protein_groups_1pct` are views, so the catalog's headline numbers are the numbers the bundle
  reconciled rather than a second opinion.
- **Cross-dataset indexes are the point.** `protein_index`, `protein_datasets` and `peptide_index`
  answer "which datasets have this", which no single bundle can.
- **Nothing is served that does not hold together.** Row counts, uniqueness and references are
  re-checked against the bundle manifests, and a failed build leaves the previous catalog serving.

Still to build, in order: the Python client and MCP server, then REST and the deploy package.

## Proposed (not yet locked, gap G1)

```
 producer output (psmtsv, FlashLFQ tsv, provenance.json, SDRF, QC)
        │
        ▼
 INGEST     parse (pyMzLib readers) → validate against the LinkML schema → reshape wide→long
            → attach ontology IDs → mint USIs → one immutable Parquet bundle per dataset
        ▼
 LAYER 1    canonical files: Parquet (ZSTD) per table per dataset, plus the original SDRF and provenance
        ▼   offline build
 LAYER 2    one DuckDB catalog with cross-dataset indexes and precomputed summaries
        ▼
 LAYER 3    one code path, several doors:
            Python client · REST (FastAPI, OpenAPI) · MCP server · static pages · bulk download
```

**Why this stack:**
- **It's small.** About 3 MB of tables per raw file, so a few GB at hundreds of datasets. One DuckDB file answers queries in milliseconds, and no database administrator is needed.
- **It runs anywhere.** It works the same on a Windows workstation and a Linux server.
- **It's proven.** The quantms portal, Open Targets and CELLxGENE converged on "files are the product, APIs are views".

**The agent interface (proposed):**
- **A few curated MCP tools:** search, describe the schema, dataset, protein profile, organelle summary, age effects and spectrum. Plus bounded read-only SQL.
- **All on the same functions as REST.**
- **Scored against the benchmark.**

## Schema

- The schema is written once in **LinkML**. JSON Schema, Pydantic models, SQL DDL and the
  [reference docs](schema/core.md) are all generated from it.
- The **core** is generic.
- **Study layers** add tables keyed on core IDs and never alter core tables.
- Coverage against the benchmark: [`design/SCHEMA_COVERAGE.md`](../design/SCHEMA_COVERAGE.md).

## Findable and citable (proposed)

Each release gets:
- Bioschemas JSON-LD on its dataset pages, and `llms.txt` at the site root;
- Croissant metadata and an RO-Crate for provenance;
- a Zenodo DOI.
