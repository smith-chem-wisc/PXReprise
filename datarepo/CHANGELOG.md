# Changelog

All notable changes to the dataRepo **software and schema**. Data releases are versioned separately by
each instance. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may break the schema.

## [Unreleased]

### Added
- **`datarepo build`** (`src/datarepo/catalog.py`): an instance's Parquet bundles into one DuckDB
  catalog. The manifest is the contract here as it is for ingest, so a withdrawn dataset is refused
  with the producer's reason and a dataset with several bundles is pinned (`--bundle`) or taken
  newest (`--latest`) rather than guessed. Tables are materialised so the catalog is one movable
  file; every schema table exists even when empty; every row carries `dataset_id` and `bundle_id`.
- Catalog views `psms_1pct`, `peptidoforms_1pct` and `protein_groups_1pct`, which apply the
  producing search engine's acceptance rule once so the catalog's headline numbers are the numbers
  the bundle reconciled. A test asserts the SQL and the ingester's Python agree.
- Cross-dataset tables `dataset_overview`, `protein_index`, `protein_datasets` and `peptide_index`,
  plus point-lookup indexes. `protein_index` reports both `n_datasets` and `n_datasets_1pct`,
  because `proteins` is the search's protein list rather than its answer.
- Catalogs are content-addressed on their bundles, the schema version and the builder version, so
  rebuilding from the same bundles is a no-op. Row counts, uniqueness and references are re-checked
  against the bundle manifests before anything is served, and the build stages to a temporary file
  so a failure leaves the previous catalog serving.
- `datarepo catalog` and `datarepo query` (read-only SQL, `table`/`tsv`/`json`).
- Catalog reference (`docs/build.md`).
- `Psm.notch` and `Psm.notch_ambiguous`. A search that cannot resolve a match's notch writes its
  candidates separated by `|`, and the producer excludes such a match from its headline count even
  though both q-values pass (aging thread 008, `DEF-PSM-1PCT v1`).

### Changed
- **`*.sdrf.tsv` is now read by pyMzLib**, not by dataRepo. The in-house read existed because
  pyMzLib's generic projection joined header and cells with `;`, which SDRF values contain
  themselves; `pymzlib.sdrf.read` (0.1.1) fixes that and agrees with the deleted code cell for cell
  on real data. SDRF characteristics are copied by position, so a repeated column name is kept
  rather than overwritten.
- The reconciliation predicate applies the notch clause, which closes the last count mismatch:
  PXD036557 now reconciles on all five checks (26,582 PSMs, 5,541 peptidoforms, 1,652 protein
  groups, 18 runs, 266,402 MS2).
- The `readers` extra installs **`mzlib`**, the distribution pyMzLib actually publishes. It was
  declared as `pymzlib`, which is the import name and does not exist on PyPI, so the extra could
  never resolve. Released wheels carry the mzLib bridge, so `PYMZLIB_BRIDGE` is needed only against
  a source checkout; CI installs the wheel and runs the `.psmtsv` tests instead of skipping them.
- Schema version 0.0.1 → **0.0.2** (the two `Psm` notch columns). A catalog refuses bundles from a
  different schema version, so bundles written at 0.0.1 must be re-ingested to be loaded.

### Added (0.1.0 groundwork)
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
