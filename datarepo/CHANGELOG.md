# Changelog

All notable changes to the dataRepo **software and schema**. Data releases are versioned separately by
each instance. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may break the schema.

## [Unreleased]

### Added
- **`datarepo ingest`** (`src/datarepo/`): one dataset's pipeline output to one immutable Parquet
  bundle. Reads the producing instance's `manifest.yaml` as its contract and refuses any dataset the
  producer did not mark `include`. Parses `.psmtsv` through pyMzLib, translates MetaMorpheus
  modification notation to ProForma 2 with UNIMOD accessions, mints a USI for every PSM against the
  deposited file names, melts FlashLFQ's wide tables to long, and writes 16 tables plus the
  producer's own metadata files and a `bundle.json` naming every input by hash.
- Bundle directories are content-addressed, so re-ingesting unchanged inputs is a no-op and a
  changed input can never overwrite a bundle somebody already cited.
- Reconciliation: every bundle recounts itself against the producer's `results.txt` and provenance,
  using the producer's own acceptance rule, and raises a `count_mismatch` finding for any
  disagreement rather than hiding it.
- Referential integrity is checked before anything is written; a dangling reference stops the write.
- `datarepo doctor`, `datarepo manifest`, `datarepo inspect`.
- Arrow schema generator (`tools/build_tables.py`) so Parquet columns come from the LinkML schema
  rather than a second, drifting list. CI fails on drift.
- Real ingester output checked in as `examples/ingested_bundle.yaml` and validated by CI, generated
  by `tools/build_example_bundle.py`.
- Ingester reference (`docs/ingest.md`) and a test suite with a miniature producing instance.
- Core LinkML schema v0 (`schema/datarepo.yaml`): 27 tables covering catalog, design, PSMs with USIs,
  peptidoforms, protein groups, PTM sites and stoichiometry, glycopeptides, inferred proteoforms,
  long-format quantities, stored annotations, definitions, metrics, provenance and findings.
- Aging study layer stub (`schema/study/aging.yaml`): SampleAge, AgeEffect (with `response`),
  OrganelleAgeSummary, ClockModel, ClockFeature, AgeMapping.
- Schema reference generator (`tools/build_docs.py`) and generated docs (`docs/schema/`).
- Example bundle and a must-fail example (`examples/`).
- CI: schema lint, example validation, a negative test, and a docs-drift check.
- Coverage map of the 168-question benchmark against the schema (`design/SCHEMA_COVERAGE.md`).

### Changed
- `Psm.q_value_notch`, `Peptidoform.best_q_value_notch`, `Peptidoform.target_decoy` and
  `ProteinGroup.target_decoy` added to the core schema. Without them a bundle could not exclude
  decoys from its own peptidoform and protein-group tables, nor reproduce the producer's headline
  counts, which accept a match on both q-values.
- `SearchModification.modification` is no longer required. Metal adducts and engine-specific entries
  have no UNIMOD accession; `name` always identifies the modification.
