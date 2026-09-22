# `datarepo study`

Writes the rows a **study layer** contributes as an immutable, content-addressed **study bundle**,
so `datarepo build` can load them beside the search bundles.

```bash
datarepo study   /path/to/stage7/study.yaml                 # write the delivery
datarepo inspect /path/to/store/_study/aging/<bundle-id>    # what did it write?
datarepo build   /path/to/instance/manifest.yaml --study aging=<bundle-id>
```

## Why this is a separate command

An `age_effect` is the output of a modelling stage that runs long after a search — aging's stage 7.
It is not in the folder `datarepo ingest` reads, and it is not a different view of the same
measurements: it is a different kind of object produced at a different time. So it cannot arrive the
way a PSM does, and giving it its own path was
[DATAREPO-20(a)](../design/OPEN_QUESTIONS.md).

The path had to keep three promises.

**Delivering a model result never forces a re-ingest.** A study bundle is separately
content-addressed. Writing one does not read, touch or re-identify a single search bundle, so a
re-fit cannot move the id of a bundle somebody has cited. A test asserts exactly that, byte for
byte, and it is the first test in `tests/test_study_bundle.py` because it is the reason the object
exists.

**A study layer adds tables and never alters a core one** (U5). True of the files as well as the
schema: the bundle holds only its layer's tables, and `build` unions it in beside the core.

**The producer declares what they are handing over**, exactly as D9 makes them declare a search.

## The delivery manifest

`study.yaml` is deliberately the dumbest contract that works — one file per table, columns named as
the schema names them:

```yaml
study_manifest_version: 1
layer: aging
instance: ncems-aging
delivery: stage7-2026-10-02          # a label; prose, and not in the content hash
store: F:/aging_data/repo/store      # the same store the search bundles live in
definitions:                         # the register every definition_id must resolve against
  - aging:DEF-AGE-EFFECT
  - aging:DEF-AGE-EFFECT-META
tables:
  age_effects: age_effects.tsv
  age_effect_refusals: refusals.tsv
  age_effect_meta: meta.parquet
  sample_ages: sample_ages.tsv
```

Relative paths resolve against the manifest's own directory, so a delivery folder can be moved or
copied whole.

dataRepo does not know what shape a modelling stage writes internally and does not guess. The one
thing it insists on is that the rows land in the schema the definition was transcribed into.

### File formats

`.parquet`, `.tsv` and `.csv`. The delimiter comes from the extension and is never sniffed — a guess
turns one column of `A;B` into two on somebody else's machine.

In a **text** file, a list-valued column is split on `;`. Parquet carries real lists and is left
alone. `age_effect_meta.dataset_ids` is the column that makes this matter: C3 and D1 need *which*
datasets a pooled estimate came from, not how many.

An empty cell is a null, never `0` and never `""` — the same coercion the core ingest uses, because
it is literally the same function.

## What it refuses

The definition's rules are enforced **here**, on the way in, not documented beside the column:

- **A `beta` without an `se` is not an age effect.** `beta`, `se`, `age_span_years`, `covariates`,
  `normalization`, `method` and `method_version` are NOT NULL, so a missing one is a write error.
- **A refused fit has nowhere to write a null `beta`**, because `age_effect_refusals` has no `beta`
  column at all. That is `DEF-AGE-EFFECT v1` §5 — no row, not a row with a null — enforced by shape.
- **A column the layer does not have** stops the write, naming it. A typo'd header is not silently
  dropped.
- **A `definition_id` the delivery does not declare** stops the write. `study.yaml` carries a
  `definitions:` register, and an id not among them refuses. aging asked for this check and
  corrected its target: their definitions are not produced by a search and have no business in a
  search bundle, so the core `definitions` table is the wrong register (their 024 §2a). A delivery
  declaring no register is not checked -- a producer who has not adopted it is not silently held to
  a stricter contract than the one they agreed to, and declaring one definition opts fully in.
- **Two rows for one fit** stop the write. The keys in `integrity.STUDY_COMPOSITE_IDENTIFIERS` were
  declared in 0.6.0 while the tables were still empty, precisely so this check existed the moment
  something filled them. Which of two answers is right is the producer's call, not the ingester's.

## Content addressing

`<store>/_study/<layer>/<bundle-id>/`, one Parquet file per delivered table plus `study.json`.

The id covers the delivered files by SHA-256, which table each one fills, the layer and its version,
the schema version and `study.STUDY_INGESTER_VERSION`. It does **not** cover `instance`, `delivery`
or `notes`: rewording a label must not re-identify rows that are byte-identical. That is the
over-hashing that has already bitten twice (aging thread 021 §4), and `STUDY_CONTENT_FIELDS` /
`STUDY_NON_CONTENT_FIELDS` classify every manifest field with its reason, with a test that fails on
an unclassified one.

`STUDY_INGESTER_VERSION` is separate from `bundle.INGESTER_VERSION` and from `__version__`, for the
same reason those are separate from each other: **bump it in the same commit as any change to what
this module reads, parses, coerces or writes.** A change to how a `.psmtsv` is parsed says nothing
about a delivered age effect, and neither says anything about the package version.

Re-running an unchanged delivery is a no-op that says so. A changed row gives a new bundle **beside**
the old one, so a catalog that cited the previous fit still resolves.

## Loading it: `build --study`

```bash
datarepo build manifest.yaml --study aging=5c44672424   # pin one delivery
datarepo build manifest.yaml --study-latest aging       # take the newest
```

**Study bundles are opt-in.** A build that names no layer gets the empty study tables it has had
since 0.6.0. A catalog that silently picked up whichever model results happened to be in the store
would answer a benchmark question differently from one built an hour earlier, with nothing in either
to say why. When a delivery is on offer and none was loaded, `build` says so and prints the flag
that would include it.

`--study-latest` is refused with `--release`, for the reason D11 refuses `--latest` for datasets: a
release that can pick up a later re-fit is not a release.

### What changes in the catalog

- The layer's tables hold rows, and every row carries `study_layer` and `study_bundle_id`. Those two
  are deliberately **not** the core's `dataset_id` / `bundle_id`: a study bundle spans datasets, and
  an `age_effect_meta` row is pooled across several by construction, so stating one dataset would be
  false. Study tables that *are* per dataset keep their own `dataset_id`, which is a fact about the
  row rather than about the bundle.
- `catalog_study_bundles` records which delivery was loaded, its layer version and where it came
  from. `catalog_tables` already labelled these tables `study:<layer>`.
- **The catalog id moves.** A catalog built with a delivery of age effects and one built without it
  answer 46 of aging's benchmark questions differently, so they cannot share an id.

### Checks it adds

Run on the union and reported in `catalog_checks`, and a failure refuses the build:

| Kind | What it asserts |
|---|---|
| `study-unique` | no table repeats its own declared key |
| `study-reference` | `age_effects.dataset_id` and `age_effect_refusals.dataset_id` are datasets the catalog holds; `sample_ages.sample_id` is a sample it holds; `clock_features.clock_id` is a clock it holds |

An age effect naming a dataset the catalog does not hold is **refused, not dropped**. Section D's
answer would otherwise come back smaller than the delivery supports, with nothing to say why.

Unlike the core's, these reference checks are not scoped by `dataset_id`. A study layer's rows are
not namespaced per dataset by us — the producer chose their identifiers — and `age_effect_meta` is
cross-dataset by construction.

### One thing still deliberately unchecked

- **`feature_id` resolves against nothing yet.** DATAREPO-20(c) asked what a feature's cross-dataset
  identity even *is*, and aging answered in their 024 §1: **the UniProt accession**, with the join
  going through **membership in `protein_accessions`**, not through the id string. They measured it
  — a membership join pools 2,608 accessions across ≥2 datasets against 2,497 for any id-based one,
  and the 111 lost "are not a random tail, they are the paralogue families" (CALM1, VAMP2, RAB6A,
  GLUD2, TEAD3). So the check is now buildable; it is not built yet, and that is tracked as G32
  rather than claimed here.
- **`definition_id` ~~is not resolved~~ now resolves against the delivery's own register.** aging
  asked for the check and moved its target rather than accepting ours: their definitions are not
  produced by a search, so the core `definitions` table was the wrong place to look (024 §2a). See
  *What it refuses*, above.

## Not in the study path yet

- **`sample_ages` has no producer.** The three ingested SDRFs carry no age column, and parsing an age
  string is sdrf/mzLib's job (D1). The table takes `age_raw` verbatim beside the normalized
  `age_years` so that a normalizer correction can be re-run against the source rather than against a
  previous normalization.
- **`organelle_age_summaries` stays not-definition-backed.** Rolling per-feature effects up to a
  compartment is an operation nobody has defined, and how a protein annotated to two compartments is
  counted is the part that would bite.
- **The path is now an agreed contract, not a recorded default.** aging replied in their 024 §2:
  "you built ahead of us and the shape is right; the delay was ours." They will copy
  `examples/study_delivery/`. Their two changes are both in: the definition register, and `delivery`
  being advisory rather than identifying — which was already true, and
  `test_the_producers_prose_does_not_move_the_id` already proved it.
