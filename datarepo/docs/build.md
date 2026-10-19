# `datarepo build`

Loads the immutable Parquet bundles an instance holds into **one DuckDB catalog**, so questions that
span datasets can be asked in SQL. This is layer 2 of the [architecture](architecture.md) and step 2
of the FRAMEWORK roadmap. Layer 1 — the bundles — stays the product; the catalog is derived from it
and can be deleted and rebuilt at any time without losing anything.

```bash
datarepo build   /path/to/instance/manifest.yaml            # every 'include' dataset
datarepo catalog /path/to/instance/catalog.duckdb           # what went into it
datarepo query   /path/to/instance/catalog.duckdb "SELECT * FROM dataset_overview"
```

The default output is `<store>/../catalog.duckdb`, which for the aging instance is
`F:/aging_data/repo/catalog.duckdb`. `--out` puts it anywhere, which is how a release freezes one
into `releases/<version>/` beside the frozen manifest.

## The manifest is still the contract

`build` reads the producing instance's `manifest.yaml`, exactly as `ingest` does (D9). It does not
walk the store looking for bundles to load. A dataset the producer has since marked `hold` or
`exclude` is refused **with their reason**, even though its bundle is still sitting on disk — a
withdrawn dataset must not reappear in a catalog through the back door.

## Choosing bundles, and refusing to guess

A dataset can have more than one bundle: re-ingesting changed inputs writes a new content hash
beside the old one instead of overwriting it. A catalog holds exactly one per dataset, so when there
is a choice, `build` makes you make it.

```bash
datarepo build manifest.yaml --latest                      # newest bundle per dataset
datarepo build manifest.yaml --bundle PXD036557=6fea2187   # this exact one (prefixes are fine)
```

With neither flag and more than one candidate, it stops and lists them.

**A release must pin.** `--release <version>` requires every dataset to carry a `--bundle` pin and
refuses `--latest` outright, rather than merely defaulting to pinning:

```bash
datarepo build manifest.yaml PXD036557 \
  --bundle PXD036557=84ca279df425c0a2 \
  --release v0.1 --out .../releases/v0.1/catalog.duckdb
```

A release that can silently pick up a later re-ingest is not a release (aging thread 011), and the
failure it would cause is invisible — the catalog builds cleanly and says something different from
what was cited. `--release` is also recorded in `catalog_meta.notes`.

## What it writes

One DuckDB file. Tables are **materialised, not views over Parquet**, so the catalog is one movable
artifact — copy it to a server, mount it in a container, hand it to an agent (D1).

**Every schema table exists**, even the ones no bundle filled, except `psms`. A bundle omits a table it has
no rows for, which is right for a file that is the product; a catalog that did the same would leave a caller
unable to tell "no such table" from "no such rows".

**PSMs stay in the bundles** (catalog format 13, D48 step 2; PXReprise's public-data contract, rule 3). They are
82% of a store's bytes, and nothing a catalog serves needs a row of them. Each dataset's PSM counts are in
`dataset_overview` (`n_psms_1pct`, `n_psms_all`), taken from the counts its bundle recorded at ingest. The PSMs
themselves are each bundle's `psms.parquet`, which DuckDB reads directly:

```sql
SELECT count(*) FROM read_parquet('<store>/PXD036557/84ca279df425c0a2/psms.parquet');
```

**Every row says where it came from.** Each table gains `dataset_id` and `bundle_id` in front of its
own columns. `dataset_id` is taken from the bundle rather than from the row, so the tables that have
no `dataset_id` of their own — `proteins`, `definitions` — get one too. Without it the same UniProt
accession from two datasets would be indistinguishable.

### Views that apply the producer's acceptance rule

| View | Rule |
|---|---|
| `peptidoforms_1pct` | target, `best_q_value` ≤ 0.01, `best_q_value_notch` ≤ 0.01 where there is one |
| `protein_groups_1pct` | anything not a decoy — **contaminants count** — with `q_value` ≤ 0.01 |

The rule is MetaMorpheus's and it is not guessable, so it is applied once here rather than restated
in every query. It is the same rule the ingester counts by when a bundle reconciles itself against
`results.txt`, and a test asserts the SQL and the ingester's count agree, so the two cannot drift. Counting
through these views is what makes the catalog's headline numbers the same numbers the bundle
reconciled: 5,541 peptides and 1,652 protein groups for PXD036557.

PSMs follow the same rule, with one more clause: a notch that resolved. A search that cannot settle on
one notch reports its candidates separated by `|`, and the producer excludes such a match from its
headline count even though both q-values pass. The ingester counts by it, and that count is
`dataset_overview.n_psms_1pct` (26,582 for PXD036557). In a bundle's `psms.parquet`, `notch` holds the
text and `notch_ambiguous` the conclusion. A catalog built before format 13 has the rule as a
`psms_1pct` view.

### A view that coarsens a grain, rather than applying a rule

| View | Grain | Rows (3 datasets) |
|---|---|---|
| `ptm_sites_by_chemistry` | one row per (protein, position, residue, chemistry) | 35,610 over 35,615 stored |

`ptm_sites` is keyed on the search engine's own name for a modification, because that is the only
key every site can form -- a chemistry with no UNIMOD cross-reference has no accession to key on,
and keying on one deleted those sites outright. One chemistry can arrive under two names, though
(`Phosphorylation on S` from the search's variable-mod list, `Phosphoserine on S` from a UniProt
annotation), so the stored table is one row finer than a chemistry-level table at 5 sites in 35,615.

This view is where they come back together, and it is the only place that merge happens. Grouping
this way reproduces the accession-keyed table exactly -- `n_psms` summing, `best_q_value` taking the
minimum -- on all 35,568 groups, zero mismatches. A site with no UNIMOD term groups on its name
instead, so two different unmapped chemistries on one residue stay two rows.

The direction matters and it is the grain rule doing its work: store at the grain the measurement
was made at, and coarsen in a view. The reverse -- storing the merged row and hoping nobody needed
the split -- is not recoverable.

### The study layer

A **study layer** adds tables keyed on core identifiers and never alters a core table (U5). The core
schema knows nothing about aging, so another reanalysis project could use it unchanged; the
aging-specific tables live in `schema/study/aging.yaml` and are generated into `Tables.Study`
separately from `Tables.Core` (`dotnet/src/DataRepo.Bundle/Generated/Tables.g.cs`).

| Table | Grain | Definition |
|---|---|---|
| `age_effects` | (dataset, feature, response, estimator, quant_basis, model_form, stratum) | `aging:DEF-AGE-EFFECT v1` |
| `age_effect_refusals` | one row per fit **not** performed, with the reason | `aging:DEF-AGE-EFFECT v1` §5 |
| `age_effect_meta` | (feature, response, estimator, quant_basis, stratum, tissue, acquisition, quant_method) | `aging:DEF-AGE-EFFECT-META v1` |
| `sample_ages` | one row per sample | not yet definition-backed |
| `curated_sample_characteristics` | (sample, characteristic) | aging's curation, versioned by `curation_version` (aging 078) |
| `organelle_age_summaries`, `clock_models`, `clock_features`, `age_mappings` | — | not yet definition-backed |

**They are empty unless a delivery is loaded, and the catalog creates them either way.** Nothing in
the *ingest* path writes them: an age effect is the output of a modelling stage that runs long after
a search, so it arrives by its own route -- `datarepo study`, which writes a separate study bundle
that `build --study` loads (see **[docs/study.md](study.md)**, DATAREPO-20(a)).

Creating the tables regardless of whether anything filled them is the point -- `NO_TABLE` and
`EMPTY_TABLE` are different answers, and only the second one says *this repository can hold that,
and holds none of it*. The query section D asks -- do organelles age at different rates -- parses
and joins whether or not a delivery is loaded:

```sql
SELECT pl.compartment, count(*) AS n, median(ae.beta) AS median_beta
FROM age_effects ae
JOIN protein_localizations pl ON pl.protein_accession = ae.feature_id
WHERE ae.response = 'abundance' AND ae.q_value <= 0.05 AND ae.stratum = 'all'
GROUP BY 1 ORDER BY median_beta;
```

**Loading a delivery is opt-in, and it moves the catalog id.** `--study aging=<bundle-id>` pins one;
`--study-latest aging` takes the newest and is refused with `--release`. A build that names no layer
gets the empty tables, and says which deliveries were on offer. A catalog built with age effects and
one built without them answer 46 benchmark questions differently, so they cannot share an id.
Loading one also adds `study-unique` and `study-reference` checks, and records the delivery in
`catalog_study_bundles`. Full reference: **[docs/study.md](study.md)**.

**A study layer's version is part of the catalog's identity, and not part of a bundle's.** Adding a
column to `age_effects` must give a different catalog id, because the catalog holds something
different; it must *not* give a different bundle id, because the bundles hold byte-identical rows
from an unchanged ingest path. `CatalogBuilder.CatalogVersion` and `Tables.StudyVersions` are in
`catalog_id` and not in `BundleWriter.BundleId`, which is why adding this whole layer re-identified every catalog and not
one bundle.

### Cross-dataset tables

A bundle can answer "what is in this dataset". Only the catalog can answer "which datasets have this
protein", which is the question the repository exists for.

| Table | Grain | For |
|---|---|---|
| `dataset_overview` | 1/dataset | axes, run and sample counts, accepted identification counts, open findings |
| `protein_index` | 1/accession | gene, organism, how many datasets have it, and how many have **accepted** evidence for it |
| `protein_datasets` | 1/accession/dataset | accepted protein groups and peptidoforms, best group q-value |
| `peptide_index` | 1/base sequence | how many datasets it appears in |
| `dataset_annotations` | 1/dataset | the producer's notes, flags, status and reason, its declared `sdrf`, and the `operator_sdrf` the ingest read in place of any other SDRF (G107), from the manifest `build` was given |
| `run_exclusions` | 1/excluded run | runs the manifest's `excluded_runs` leaves out of analysis, with the reason; `run_id` joins `runs` |
| `dataset_restrictions` | 1/dataset/use | uses the manifest's `restrictions` says the whole dataset is unfit for, with the reason; the same uses are listed on `dataset_overview.restrictions` (NULL when the manifest has no entry for the dataset) |

`dataset_annotations` is taken from the manifest at **build**, not from the bundle. These fields are not
identity (rewording a note must not re-id a bundle), so they never reach a bundle at all; a reworded
note reaches the catalog at the next `build`, with no re-ingest, and moves the catalog id. A dataset the
manifest does not list keeps NULLs and a `catalog_checks` row of kind `manifest` names it.
`run_exclusions` is built the same way. Its runs are still in `runs` and `quant_values`, in the bundles'
PSMs and in every count, so an analysis anti-joins it (`run_id NOT IN (SELECT run_id FROM run_exclusions)`). `build`
refuses an exclusion that names no run of the dataset's bundle.

`dataset_candidates`, the core table that answers "why is PXDn not here?", is also filled at build from
the manifest: its top-level `candidates` key names the producer's discovery census, a TSV with one row
per screened accession (format in [operating.md](operating.md#3-ingest-each-dataset)). It is a fact
about the instance, not about any dataset, so no bundle carries it. The census file's sha256 goes into
the catalog id, and `catalog_meta.notes.candidates` records its path, sha256 and row count.
`catalog_tables.kind` is `manifest` for the table when a census was loaded. `dataset_id` and
`bundle_id` are the catalog's own for the accession, or NULL when the catalog holds none. Two
`catalog_checks` rows of kind `census` always pass and name the gaps: accessions the census includes
that the catalog holds no dataset for (a census may run ahead of ingest), and catalog datasets the
census omits or lists as not included. A manifest without `candidates` leaves the table empty, and
the catalog id is the same as before.

`protein_index` carries both `n_datasets` and `n_datasets_1pct` on purpose. `proteins` is the
search's protein *list*, decoys and sub-threshold matches included, not its answer. An agent asking
"which datasets have this protein" almost always means the second column, so both are there with
names that say which is which.

In a one-file catalog, point-lookup indexes are built on the columns those questions land on —
`peptidoforms.base_sequence`, `quant_values.feature_id`, `proteins.gene` and the rest. List-valued
columns such as `protein_accessions` cannot carry a DuckDB index; the derived tables cover them.

### The catalog's account of itself

| Table | Holds |
|---|---|
| `catalog_meta` | catalog id, schema and QPX versions, builder version, when, which instance |
| `catalog_bundles` | every bundle loaded: its id, path, when it was written, and whether **its own** reconciliation passed |
| `catalog_tables` | every table with its row count, marked `bundle` / `derived` / `view` |
| `catalog_checks` | every check the build ran, and its result |

`catalog_bundles.reconciliation_ok` carries a bundle's known mismatches forward rather than hiding
them: a bundle whose own counts disagreed with its producer is flagged here, `datarepo catalog`
names the check that failed, and a catalog therefore never looks cleaner than the data it was built
from. PXD036557 was flagged this way until its 12-PSM difference was explained and closed.

## Checks, and what a failure means

Everything is built into a temporary file beside the target and moved into place only once it
passes, so **a failed build leaves the previous catalog serving**.

- **Row counts.** Every table must hold exactly the rows its `bundle.json` recorded. This is the one
  thing a content hash cannot catch: Parquet that was truncated or edited after the manifest was
  written.
- **Identifiers are unique** within a dataset, and **references resolve** within a dataset — driven
  by the same lists in `Integrity` (`DataRepo.Bundle`) that the ingester checks a bundle against, so there is one
  statement of what points at what.
- **Schema version.** A bundle written against a different schema version is refused with the
  version it carries, rather than unioned into columns that no longer mean the same thing.

A failure here is a bug or a damaged store, not a property of the data, so it stops the build. That
is the same line the ingester draws: a Finding is for something true about the dataset, and this
would be something false about the catalog.

## A catalog folder: views over the store, versioned files, a pointer

`--out` decides the layout. A name ending `.duckdb` writes one movable file holding a copy of every row, as before.
Any other path is a **catalog folder** (D48 step 1, PXReprise's public-data contract):

```
catalogs/
  catalog-7af21bbdd37814ef.duckdb    one versioned catalog per build, never replaced
  catalog-511e89f90d259bf9.duckdb    an older one, kept until --keep prunes it
  latest.json                        names the current file, with its catalog_id and release_id
  derived/2b6d2776916b293a/          each deposit's derived rows, computed once (step 2b)
```

- **The bundle and engine tables are views** over the store's Parquet (`quant_values`, `peptidoforms`, `proteins`, ...), with
  `dataset_id` and `bundle_id` written into each view per file, so a filter on one dataset reads that dataset's files
  only. `samples` is still copied (a few thousand rows), because its `*_name` columns are added to it. The derived
  tables, the study tables, the manifest's tables and the catalog's own tables are copied too: they are small. A
  catalog folder is megabytes, not tens of gigabytes, and a build copies no bundle rows.
- **Each deposit's derived rows are computed once** (D48 step 2b) and kept in the folder, under
  `derived/<derivation id>/<dataset>/<bundle>/`: its `dataset_overview` row, its `protein_datasets` and
  `search_modifications_placed` rows, and the partial rows `protein_index` and `peptide_index` are aggregated from. They
  are the same SQL a one-file catalog runs over the whole corpus, run over that deposit's own files. A later build
  reuses them while the bundle and the derivation id are the same. The id hashes that SQL, the catalog format, the
  schema and DuckDB's version, so a change to any of them derives every deposit again, once. `protein_datasets` and
  `search_modifications_placed` are views over those files; `dataset_overview` and the two indexes are copied. A
  deposit's directory is written beside its final name and moved into place whole, `derived.json` (each file's row
  count) last, and one whose files no longer hold those rows is derived again. On aging's store (170 deposits) the
  directory is 155 MB, about 0.9 MB a deposit; `build` prints how many deposits it computed and how many it reused.
- **A build writes a new file and then the pointer.** No file a reader holds is ever replaced: an MCP server or a
  script keeps the catalog it opened, and the next one opened gets the new file. `latest.json` is written beside
  itself and moved into place, so a reader never sees half of one. An unchanged rebuild writes nothing and points
  `latest.json` at the existing file again.
- **`--keep N`** deletes the folder's catalog files beyond the newest N, never the current one and never one built
  for a release (`--release`).
- **Two ids.** `catalog_id` names one catalog file, and hashes the builder, the layout and (for a folder) the store's
  path. `release_id` names the data: the schema and the bundles, study deliveries and engine artefacts. A folder and a
  one-file catalog of the same bundles share their `release_id`. Both are in `catalog_meta`, with `layout` (`views` or
  `copies`), `data_root` and `derived_root`. The MCP sandbox for a folder allows those two directories and nothing else.
- **Checks.** The row counts are checked against every bundle's `bundle.json` in both layouts: a copied table is
  counted in the catalog, and a view's files (and the PSMs) from each Parquet file's footer. A folder build does not
  re-run ingest's uniqueness and reference checks over every table: each bundle passed them when it was written, and
  every identifier is namespaced by its dataset. `catalog_checks` has one `integrity` row saying so.
- **The views read the store where the build found it.** Moving the store means rebuilding the folder catalog. The
  store must be one folder, holding the bundles, `_study/` and `_engine/`; the build refuses otherwise.

Every reader takes the folder or its `latest.json` in place of a file: `query`, `catalog`, `site`, `publish` and
`mcp`.

## Rebuilding is cheap and idempotent

The catalog is content-addressed on the bundle ids, the schema version and the builder version, like
a bundle is on its inputs. Building again from the same bundles prints `unchanged` and does nothing;
`--overwrite` forces it. Changing any bundle, the schema or the builder gives a new catalog id.

A build writes the new catalog beside the output as `.<name>.<catalog id>.building` and moves it into
place only at the end, after checking that the file is as long as DuckDB's own header says. Beside the
staged file it records which process is building it (`.building.owner`). A killed build cannot clean up
after itself, so every build first removes the staged files whose builder is no longer running, and
prints `removed` for each (DATAREPO-83). A staged file from 1.3.2 or earlier has no such record. On
Windows it is removed when nothing holds it open. Elsewhere it is left alone and named in a `note`. A
second build of the same catalog while one is running is refused.

A catalog that has lost its tail since it was built still opens and answers until a query reaches the
missing part. The MCP server checks the file's length when it opens a catalog, and refuses a short one
by name.

## Querying it

```bash
datarepo query catalog.duckdb "SELECT * FROM dataset_overview" --format tsv
datarepo query catalog.duckdb "
  SELECT i.gene, d.dataset_id, d.n_peptidoforms
  FROM protein_index i JOIN protein_datasets d USING (protein_accession)
  WHERE i.gene = 'LMNA'"
```

The connection is opened read-only, so a statement that tries to write is refused by DuckDB itself.
`--limit` caps the rows (50 by default, `0` for none) and `--format` is `table`, `tsv` or `json`.

Any DuckDB client works just as well — this is one ordinary `.duckdb` file. The MCP tools
([mcp.md](mcp.md)) read the same catalog rather than a second code path.

**One trap worth knowing.** `quant_values` is long (D5): a protein group in a run has **two** rows,
an intensity and a spectral count, told apart by `definition_id`. A query that forgets to filter on
it gets both, and they are not comparable. Every number in the catalog carries the definition ID of
the project that defines it; `definitions` holds the text, and the ones named `PROVISIONAL:*` say in
their own text that they are not published yet (G15).

## Not in the catalog yet

- **Releases.** `releases` and `release_changes` are defined in the schema but no bundle fills them:
  they are instance tables the producer writes (D8). `--release` records the version in
  `catalog_meta`; freezing the manifest and minting the DOI is aging's step.
- **Annotation and study layers.** `protein_localizations` (owned by `go`), `age_effect` and
  `organelle_age_summary` (owned by aging) exist as empty tables until their producers deliver rows.
- **QPX view names.** Column names are written against an unpinned QPX (G13), so the catalog exposes
  no QPX-compatible views yet.
