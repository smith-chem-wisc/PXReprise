# Changelog

All notable changes to the dataRepo **software and schema**. Data releases are versioned separately by
each instance. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may break the schema.

## [0.7.0] - 2026-09-21

### Added
- **`datarepo study`: how a study layer's rows actually reach the repository** (DATAREPO-20(a)).
  An `age_effect` is the output of a modelling stage that runs long after a search, so it cannot
  arrive the way a PSM does. It now arrives as its own **study bundle** -- separately
  content-addressed, under `<store>/_study/<layer>/<bundle-id>/` -- written from a `study.yaml`
  delivery manifest that names one file per table. `.parquet`, `.tsv` and `.csv`, with the delimiter
  taken from the extension rather than sniffed, and list columns split on `;` in text formats.
  Reference: **[docs/study.md](docs/study.md)**; a worked, tested delivery is in
  `examples/study_delivery/`.
- **`datarepo build --study <layer>=<id>` / `--study-latest <layer>`** loads a delivery beside the
  search bundles. Study bundles are **opt-in**: a build that names no layer gets the empty tables it
  has had since 0.6.0, and now says which deliveries were on offer. `--study-latest` is refused with
  `--release`, for the reason D11 refuses `--latest` for datasets.
- Every study row carries `study_layer` and `study_bundle_id`. Deliberately not the core's
  `dataset_id` / `bundle_id`: a study bundle spans datasets, and an `age_effect_meta` row is pooled
  across several by construction, so stating one would be false.
- `catalog_study_bundles`, and two new check kinds refused at build time: `study-unique` (no table
  repeats its declared key) and `study-reference` (`age_effects.dataset_id` and
  `age_effect_refusals.dataset_id` name datasets the catalog holds, `sample_ages.sample_id` a sample
  it holds, `clock_features.clock_id` a clock it holds). An age effect for a dataset the catalog
  lacks is **refused, not dropped** -- section D's answer would otherwise come back smaller than the
  delivery supports, with nothing to say why.
- `integrity.STUDY_REFERENCES`, and `study.STUDY_CONTENT_FIELDS` / `STUDY_NON_CONTENT_FIELDS` with a
  test that fails on an unclassified manifest field -- the same discipline as
  `manifest.CONTENT_FIELDS`, one object over.
- `datarepo inspect` summarises a study bundle as well as a search bundle.

### Changed
- `CATALOG_VERSION` 2 -> **3**, and `catalog_id` now hashes the loaded study bundle ids. A catalog
  built with a delivery of age effects and one built without it answer 46 of aging's benchmark
  questions differently; sharing an id would make them indistinguishable to anyone citing one.
- `bundle.table_from_rows` is now a thin wrapper over `bundle.rows_to_table`, so a study layer's
  rows go through **exactly** the core's coercion and its two refusals. That is what makes
  `DEF-AGE-EFFECT v1`'s rules enforced rather than documented: a `beta` with no `se` is a write
  error, and a refused fit has nowhere to put a null `beta` because it has a table of its own.
- `study.STUDY_INGESTER_VERSION` (0.1.0) is the only version in a study bundle's content hash --
  a third instance of the same rule, separate from `bundle.INGESTER_VERSION` and `__version__`
  because the three paths move independently.

### Unchanged on purpose
- **`bundle.INGESTER_VERSION` stays 0.5.0 and no search bundle id moves.** Nothing in the ingest
  path reads, parses, derives or writes anything differently, and the whole reason a study bundle is
  a separate object is that delivering a model result must never force a re-ingest. A test asserts
  it byte for byte; verified end to end on the fixture dataset, whose bundle id is unchanged.
- **Two columns are deliberately unchecked, and their absence is the point.** `feature_id` resolves
  against nothing (DATAREPO-20(c) asks what a feature's cross-dataset identity even is, and a
  foreign key written now would freeze a guess with the authority of a constraint), and
  `definition_id` is not resolved against `definitions` (whether a study layer's definitions land in
  a search bundle is unsettled).

### Note
- **This is the default from thread 022 section 2a, built while the question is still open** (D7).
  aging has not replied. If they want a different hand-over, the reader changes; the bundle and
  catalog contracts do not.

## [0.6.0] - 2026-09-20

### Added
- **The study layer is instantiated** (G19). `schema/study/aging.yaml` goes from a stub to eight
  real tables, generated into `STUDY_TABLES` by `tools/build_tables.py` exactly as the core's are,
  and created by `datarepo build`. `age_effects`, `age_effect_refusals` and `age_effect_meta` are
  transcribed column-for-column from `aging:DEF-AGE-EFFECT v1` and `-META v1`; `sample_ages`,
  `organelle_age_summaries`, `clock_models`, `clock_features` and `age_mappings` carry their
  not-yet-definition-backed status in their own descriptions.
- Enums `AgeResponse`, `AgeEstimator`, `QuantBasis`, `ModelForm` and `FitRefusal`, each carrying in
  its description the benchmark question that forces it to exist.
- `integrity.STUDY_COMPOSITE_IDENTIFIERS`: every study table's natural key, declared while the
  tables are empty, with a test that fails on a study table having no key. `quant_values` shipped
  with none and a duplicated source row wrote one measurement three times.
- Catalog tables are listed with `kind = 'study:<layer>'`, so a caller can tell a layer's tables
  from the core's.

### Changed
- `CATALOG_VERSION` 1 → **2**, and `catalog_id` now hashes each study layer's version. A change to
  what `build` writes must re-id catalogs and must **not** re-id bundles that hold byte-identical
  rows from an unchanged ingest path. Same principle as `manifest.CONTENT_FIELDS`, one level up.
- **`bundle_id` is hashed on `bundle.INGESTER_VERSION`, not on `__version__`.** The package version
  moves for reasons a bundle cannot see -- 0.6.0 is entirely a `build` change -- and hashing it
  would have re-identified every bundle in every store for rows that are byte-identical. The new
  constant is bumped in the same commit as any change to what an ingest reads, parses, derives or
  writes, and it lags `__version__` on purpose. Verified: the three real datasets re-ingest to the
  same bundle ids they had on 0.5.0, and two tests pin both directions. `bundle.json` records both
  versions, because they answer different questions.

### Note
- **Every study table is empty and will stay empty until a producer delivers rows.** Nothing in the
  ingest path writes them; how they arrive is DATAREPO-20. Creating them anyway is the deliverable:
  aging's benchmark distinguishes `NO_TABLE` from `EMPTY_TABLE`, and the 46 questions that need an
  age effect scored the first. Section D's own query -- do organelles age at different rates --
  now parses, joins `protein_localizations` and returns nothing.

## [0.5.0] - 2026-09-20

### Changed
- **`ptm_site_id` is keyed on the search engine's own name for the modification, not its UNIMOD
  accession** (`<dataset_id>:<accession>:<residue><position>:<modification_name>`). A key ending in
  the accession could not be formed for a modification that has none, so those sites were not
  written as nulls -- they were not written at all, and a query for them returned an empty answer
  indistinguishable from "never identified". Across the three ingested datasets that cost **42 sites
  over 10 chemistries** (195 PSMs), of which only one chemistry was ever named in a finding; the
  other nine resolved to a mass but not an accession and left no trace anywhere. `GG (Ubiquitination
  Site) on K` -- the diGly remnant -- has no cross-reference in mzLib 1.0.591, so on a diGly dataset
  the deletion would have been the subject of the experiment (aging threads 019 §3 and 020).
  `PtmSite.modification` is now nullable and `PtmSite.modification_name` is required.
- The bundle content hash covers **the manifest fields that shape content**, not the raw manifest
  entry. Rewording a `reason` used to move the bundle id while every row stayed identical, so two
  sites ingesting byte-identical search output got different ids (aging 019 §1).
  `manifest.CONTENT_FIELDS` / `NON_CONTENT_FIELDS` classify every field with its reason, and a test
  fails on a `DatasetEntry` field that is in neither.
- Schema version 0.0.3 → **0.0.4**; ingester 0.4.0 → **0.5.0**. Both are in the bundle hash, so every
  dataset re-ingests to a new bundle id.

### Added
- Catalog view **`ptm_sites_by_chemistry`**, which restates `ptm_sites` at chemistry grain. The name
  key is one row finer than an accession key where a chemistry arrives under two names (5 sites in
  35,615); this view groups them back, and doing so reproduces the accession-keyed table exactly --
  `n_psms` summing, `best_q_value` taking the minimum -- on all 35,568 groups, zero mismatches. A
  site with no UNIMOD term groups on its name, so two unmapped chemistries on one residue stay two
  rows.
- `_site_key_name` refuses a modification name containing ':', which is the key separator. Every
  name MetaMorpheus writes is `<id> on <residue>` and carries none; a colon means the mod token did
  not parse, and there is then nothing honest to write.

## [0.4.0] - 2026-09-20

### Added
- **Lossless duplicate collapse** (AGING-Q2). Rows sharing an identifier and identical in every
  column collapse to one, recorded in `bundle.json` under `collapsed_duplicates` and as a
  `collapsed_duplicate_rows` finding (severity info). Rows sharing an identifier and differing
  anywhere still refuse the bundle. PXD027318 ingests: 7,463 protein groups from the file's 7,465,
  and `protein_groups_1pct` 4,478 matches the producer exactly.
- A natural key for `quant_values` -- `(assay_id, feature_type, feature_id, definition_id)`. The
  table had no identifier at all, so three identical group rows wrote three identical spectral-count
  rows and a caller summing intensities would have read the group as three times as abundant.
  Verified 0 duplicates in PXD036557's 50,085 and PXD032202's 141,327 rows before it became a rule.
  `psms` and `findings` are deliberately not collapsible: a row there is an event and the row count
  is itself a reported number.
- `reconcile.metric_conflicts` and the `metric_conflict` finding: `psms_1pct` reaches a bundle from
  provenance *and* from `results.txt` under one definition, and nothing was comparing them.
- Contamination as metrics, not only a finding: `contamination_psm_share` at dataset scope
  (`aging:DEF-CONTAM-PSM v1`) and `contamination_intensity_share` **per run**
  (`QuantProject:DEF-QC-9 v2`), one row per file, keyed on the deposited `run_id`. On PXD036557: 18
  per-run rows spanning 0.0264-0.1892 against a 7.0% dataset figure.

### Changed
- The five provisional definition IDs carry aging's published ones with their text verbatim:
  `aging:DEF-PEPTIDE-1PCT`, `DEF-PROTEINGROUP-1PCT`, `DEF-MS2`, `DEF-RUN-MINUTES`, `DEF-PRECURSORS`.
  The three QuantProject quantities stay `PROVISIONAL:` -- an ID implying a ruling that has not
  happened is worse than a placeholder.

## [0.3.0] - 2026-09-19

### Changed
- `ptm_sites` drops the ambiguity-level and target-only filters, because the producer's occupancy
  population applies neither: 1,370 → **1,997** sites on PXD036557, and uncovered occupancy sites
  217 → 29. `PtmSite.best_ambiguity_level` and `PtmSite.target_decoy` keep the old derivation
  available as a `WHERE` clause.
- `datarepo build --release` requires a `--bundle` pin for every dataset and refuses `--latest`
  (D11). A release that can silently pick up a later re-ingest is not a release.
- Schema version 0.0.2 → **0.0.3**.

## [0.2.0] - 2026-09-19

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
