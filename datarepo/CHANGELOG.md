# Changelog

All notable changes to the dataRepo **software and schema**. Data releases are versioned separately by
each instance. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may break the schema.

## [0.18.0] - 2026-09-24

**Core schema 0.0.8 -> 0.0.9, `INGESTER_VERSION` 0.11.0 -> 0.12.0 and `CATALOG_VERSION` 4 -> 5, so
every bundle re-ids and a re-ingest is owed; `build` refuses a catalog that mixes 0.0.8 and 0.0.9
bundles.** Three changes that each needed a bump, released together so the corpus re-ingests once.
Answers DATAREPO-39 (aging) and DATAREPO-44 (aging 058); closes G63's manifest path and G15; makes
the three schema fixes promised to go (009 section 4).

Measured before release on all 24 datasets aging serves, re-ingested to a scratch store and
compared with their 0.11.0 bundles: every table other than `quant_values` has the same row count;
every non-zero quantity is unchanged in number and now carries QuantProject's id (checked on 23 of
them against their 0.11.0 bundles); 1,295,912 zero
spectral counts are new rows (below); PXD058611's runs 178-198 read `chemical_probe` and 199-213
`none`, exactly aging's assignment. The scratch catalog builds and passes all 399 checks.

### Fixed
- **The `quant_values` description said "never 0"**, which after the fix below would have led a
  reader to count every spectral-count row as a detection (an agent did: 18 of 18 runs instead of
  15 and 3). It now says a stored 0 is a measured zero, appears only for a count, and that
  detections are counted with `value > 0`.
- **A spectral count of 0 was stored as missing.** Both columns of a protein-group block went
  through the intensity rule, where 0 means "not measured" and becomes no row. QuantProject's
  `DEF-PROT-SPC` says the opposite for a count: *"0 is a real zero here: no qualifying PSM in that
  sample group. Unlike an intensity cell, it is a measurement."* Found by filling in their
  definition text. On the 24-dataset corpus this adds **1,295,912** rows with value 0 (70% of
  spectral-count cells). **A query that counts spectral-count rows as detections now counts every run**; test
  `value > 0` instead. Intensities are unchanged: a 0 or blank intensity is still no row.

### Added
- **Enrichment per run** (G63, aging DATAREPO-39/DATAREPO-44). `runs.enrichment` and
  `runs.enrichment_source`, and `datasets.enrichment_mixed` (also in `dataset_overview`).
  `datasets.enrichment` stays the producer's declaration. A dataset not flagged `mixed_enrichment`
  gives every run its declaration; a mixed one gives NULL unless the manifest's new
  `run_enrichment` names every run. The map is refused if it leaves a run out, names a non-run or a
  run twice, uses a value outside the vocabulary, assigns an undeclared value, or contradicts the
  flag. `run_enrichment` and the `mixed_enrichment` flag are manifest content fields; no other flag
  is. See `docs/ingest.md`. **Not built:** reading `comment[enrichment process]` from an SDRF, which
  would take precedence over the manifest; it waits for a real SDRF that carries it.
- **A `mixed_enrichment` finding** on every dataset whose runs differ, stating the split (PXD058611:
  21 runs `[chemical_probe]`, 15 `[none]`). A filter on `datasets.enrichment` alone gets a mixed
  deposit wrong both ways, and a finding reaches every answer that cites the dataset (D19); the
  column alone was found to be the only clue by an agent given the tools and not the source.
- **An `sdrf_uncoded` finding replaces `sdrf_skeleton` where the SDRF has text but no ontology
  term.** `sdrf_skeleton` said organism part, cell type, disease and individual were "all absent"
  for PXD010115 and PXD034432, whose SDRFs say `Blood serum` and `Urine` without a UBERON term. The
  new finding names the text values and says where they are (`sample_characteristics`).
- The site says when a dataset's runs differ in enrichment, in the summary, the facts and the index.

### Changed
- **The three quant values carry QuantProject's definitions** (G15, QuantProject 003 section 1):
  `QuantProject:DEF-PEP-INT`, `QuantProject:DEF-PROT-INT` and `QuantProject:DEF-PROT-SPC` replace
  `PROVISIONAL:PEPTIDE-INTENSITY`, `PROVISIONAL:PROTEIN-INTENSITY` and
  `PROVISIONAL:PROTEIN-SPECTRAL-COUNT`. The texts are theirs, verbatim, from definitions v3.5
  (`f4bb910`), with their grain-and-unit row appended. No `PROVISIONAL:` definition remains.
- **`protein_localizations` drops `organelle_map_version`** (go 009 section 4). Under go D28 an
  annotation row names no map, so a `required` column there had no true value. It joins to
  categories on (`compartment`, `go_release`).
- **`organelle_term_categories` gains `category_map_name`**, in its natural key: go writes one
  category file per consumer map, and two consumers' maps can share a version. Its description now
  states the coverage check that replaces the lapsed per-row cross-check. Both tables are still
  empty; no reader exists yet (G53).
- MCP `search --kind localization` returns the map name and version with each category.

## [0.16.0] - 2026-09-23

**Core schema 0.0.7 -> 0.0.8 and `INGESTER_VERSION` 0.10.0 -> 0.11.0, so every bundle re-ids and
a re-ingest is owed.** It is timed to ride aging's own manifest correction (enrichment and fractions
for seven datasets, aging 045 section 3), which moves the same ids once. Answers DATAREPO-33
(aging 045), DATAREPO-34 (aging 046), DATAREPO-29/30 (go 004) and DATAREPO-P1/P2/P3 (ptmQtl 002).
Closes G51.

### Fixed
- **A C-terminal modification was stored one past the protein's end, on residue `-`** (G51,
  DATAREPO-33). MetaMorpheus writes it as `KPVADYFL-[mod]`; the ProForma parser appended the `-`
  to the base sequence. It is now the terminus marker. The site is keyed on the peptide's last
  residue at its own position, typed `protein_c_term` when that residue ends the searched protein
  and `peptide_c_term` otherwise, as aging ruled. The ProForma string, and so the peptidoform id,
  is unchanged. On PXD050351 the corpus's one such site becomes
  `PXD050351:P60510:L307:Leucine methyl ester on L@protein_c_term`, and the site check passes.
  A C-terminal site on a protein with no sequence is not typed; it is counted under
  `c_term_no_sequence` in the `unplaced_ptm_sites` finding.

### Added
- **`Enrichment`: `immunoprecipitation`, `proximity_labelling`, `affinity_purification`,
  `chemical_probe`** (DATAREPO-34). Seven of aging's first ten datasets are capture enrichments,
  and `other` could not tell a lysosome TurboID from a kinobead. Values come from the manifest.
- **`peptidoforms.engine_full_sequences`**: the producer's own notation, verbatim, as a set
  (DATAREPO-P3). `peptidoform` is UNIMOD ProForma, which cannot say whether `S[UNIMOD:21]` was
  `UniProt:Phosphoserine` or `Common Biological:Phosphorylation on S`; this column can.
- **`organelle_term_categories`** (go's producer; empty until go's first file): a term's organelle
  category, stored once per (compartment, `organelle_map_version`, `go_release`) instead of on
  every protein row (DATAREPO-29/30).
- **`trait_effects` and `ptm_pairs`** (ptmQtl's producer; shape only, nothing writes them yet): a
  generic per-feature trait effect (two-part hurdle, `estimable_*` flags instead of NULL answers)
  and a two-peptidoform relationship table. aging's `age_effects` is unchanged.
- Natural keys for all three new tables, declared while they are empty.

### Changed
- **`protein_localizations` drops `organelle_label`** and gains `organelle_map_version` and
  `go_release`, the key into `organelle_term_categories`. Its description now cites go's own
  rulings, not the superseded REQ-GO-2..10. The MCP `search` joins the category in.

## [0.15.0] - 2026-09-22

**`INGESTER_VERSION` 0.9.0 -> 0.10.0, so every bundle re-ids and a re-ingest is owed.** aging
study layer 0.2.0 -> 0.3.0, `STUDY_INGESTER_VERSION` 0.3.0 -> 0.4.0. Core schema (0.0.7) is
unchanged. Answers DATAREPO-32 (aging 043) and closes G44.

### Fixed
- **`ptm_sites` put a shared peptide's modifications at the wrong protein positions.**
  `ptm_site_rows` paired the psmtsv's `Start and End Residues In Full Sequence` with its
  `Accession` list by index, falling back to the first span. MetaMorpheus writes that column
  **de-duplicated** -- `P60709|P63261|Q6S8J3` beside `[216 to 238]|[916 to 938]` -- and once per
  occurrence of a repeated peptide, so no index pairing is right, including when the two lists
  happen to be the same length. aging measured the result on their ten-dataset catalog: 2,266 sites
  at positions no alignment supports, 2,166 real sites missing, 1,452 naming a residue not at that
  position, 297 beyond the protein's length (gamma-actin carrying POTE-E's numbering). **All at
  ambiguity levels carrying protein ambiguity, none at level 1**, which is why the level filter hid
  it and why release v0.1 (cut under that filter) is unaffected. Sites are now placed by finding
  the peptide in each member protein of the searched database and emitting every occurrence, as
  MetaMorpheus's occupancy code does. `site_type`'s initiator-methionine test reads the residue
  before the peptide from the same sequence, because `Previous Residue` is collapsed the same way.
- **Decoy `Protein.organism_name` is NULL** (G44). MetaMorpheus wrote a species for some reversed
  decoys and not others: 11,804 of 11,804 decoys had none in PXD024803, but only 149 of 7,157 in
  PXD023381, so the rest carried a species name.

### Added
- The ingest reads **every protein database in the search provenance's `inputs`**
  (`sources/protein_db.py`; UniProt XML and FASTA, accession and sequence only), checks each
  against the sha256 the search recorded, and **stops on a mismatch**. They are hashed into the
  bundle id under `protein_database:<file name>` and never copied into the bundle.
- Finding **`unplaced_ptm_sites`**, with counts in `bundle.json` under `protein_databases`: pairs
  that could not be placed are counted rather than guessed.
- **Every ingest checks its own sites**: each written site's residue against its position in the searched sequence, recorded in `bundle.json` as `protein_databases.site_residue_check`, with a `ptm_site_residue_mismatch` finding on any failure. The check aging ran from outside to find this now runs on the path that cannot be skipped.
- **`tools/verify_ptm_sites.py`**: the same check from the written Parquet, for a bundle you did not build. `--db` checks bundles built before 0.15.0, and it fails on aging's current 0.9.0 bundles (PXD036557: 29 wrong residues, 4 beyond length).
- **`docs/ingest.md` "Reproducing a bundle"**: the inputs that must be byte-identical, now including both searched databases, and what happens when one is missing.
- `lxml` in the `readers` extra; the standard-library fallback is tested to read identically.
- **`age_effect_refusals.organism`** (NCBITaxon, required, denormalised, outside the key), as on
  `age_effects` (aging 042 section 2).
- The test fixture carries two small databases cut from the real ones, so the suite exercises
  alignment end to end.

### Verified, on all ten of aging's datasets (scratch store, not aging's)
Every target site checked against the searched sequence by `tools/verify_ptm_sites.py`:

| | 0.9.0 (aging's current bundles) | 0.15.0 |
|---|---|---|
| residue at its position | 103,551 | **105,482** |
| wrong residue | 1,674 | **0** |
| position beyond the protein | 344 | **1** (see below) |

Residue-level target site keys: 89,300 agree, 1,711 only in 0.9.0 (misplaced), 1,847 only in
0.15.0 (recovered). 7,510 (PSM, protein) pairs are unplaced, and in the three datasets classified
(PXD036557, PXD050351, PXD067622) **every one** is a level 4/5 PSM whose protein carries a
different candidate peptide from the stored one. Row counts in every other table are unchanged,
and every reconciliation check gives the same result as on 0.9.0.

**The one remaining failure is not this defect.** It is the corpus's first **C-terminal**
modification, `KPVADYFL-[UNIMOD:34]` in PXD050351: the ProForma parse leaves the `-` in
`base_sequence` (3 PSMs, 2 peptidoforms) and the site is written with residue `-` at the
protein's length + 1. aging's DATAREPO-26 answer called this hole latent; it is now real, the
0.9.0 bundle carries it too, and the new self-check is what surfaced it (G51).

### Changed
- An ingest now takes about 20 seconds longer: parsing a 1 GB UniProt XML takes 15 s with `lxml`, or
  47 s with the standard library if `lxml` is not installed.

## [0.14.0] - 2026-09-22

**aging study layer 0.1.0 -> 0.2.0, `STUDY_INGESTER_VERSION` 0.2.0 -> 0.3.0.** Core schema
(0.0.7) and `INGESTER_VERSION` (0.9.0) are unchanged, so **no search bundle re-ids and no
re-ingest is owed**. Closes G40, on aging's rulings in their 039 and 040.

### Added
- **`age_effects.organism`** (NCBITaxon, required) and **`age_effects.age_centre_years`** (float,
  required, no n/a). `organism` is denormalised from `dataset_id` on purpose: the pooling guard
  has to be checkable without a join. `age_centre_years` replaces the literal 50 that sat in the
  model formula as a human constant; it moves the intercept and spline/breakpoint knots, never
  `beta`.
- **`age_effect_meta.organism`**, required and the **first component of the key**
  (`aging:DEF-AGE-EFFECT-META v1.2` section 6.4). A human and a mouse effect on one feature id are
  two rows. Nothing stopped them pooling before.

### Changed
- `beta` is documented as **per decade of chronological age, within one organism**, on
  `(age_years - age_centre_years) / 10`, never lifespan-normalised and never with a per-organism
  denominator (aging 040 corrected their own 039, which had said per year).
- `age_mappings` is documented as aging's (their R4): an analysis choice, not sample metadata.
- Both tables hold 0 rows in every catalog, so this costs no migration.

## [0.13.0] - 2026-09-22

**`INGESTER_VERSION` 0.8.0 -> 0.9.0, so every bundle re-ids and a re-ingest is owed.** No schema
change. One defect, found while checking a claim before making it publicly.

### Fixed
- **A collapsed producer column was zipped positionally, and every accession after the first lost
  its value.** MetaMorpheus writes ONE entry when all proteins on a row share it:

  ```
  Accession     = P60709|P63261
  Organism Name = Homo sapiens        <- one entry for two proteins, not a missing one
  ```

  `protein_rows` read `organism_parts[j]`, so `P63261` got an empty string. On aging's four-dataset
  catalog that cost **2,678 uniprot proteins, 4,523 decoys and 9 contaminants** their species --
  in 0.11.0, the release whose entire purpose was handling species correctly. `Gene Name` collapses
  the same way and was affected too (62 rows in 60,000, against 2,062 for organism).
- The same assumption in `add_group_proteins`, where a short gene list could put one protein's gene
  symbol on another.

### The rule, for the case that stays ambiguous
`_per_accession` takes three branches. Counts match -> zip positionally. One entry, many
accessions -> broadcast, because the producer collapsed it *precisely because they agree*.
**Anything else (3 accessions, 2 genes) -> NULL for all of them**, never a positional guess: the
alignment is genuinely unknown, and a real gene symbol on the wrong protein reads as a fact where
a null reads as "not recorded". Where the choice is a guess or a null, only the null cannot be
quoted back as evidence.

### How it was found, which is the part worth keeping
Not by a test and not by the two rounds of agent review, which both missed it. It surfaced because
a thread to aging was about to assert *"MetaMorpheus wrote no species for these seven
accessions"* -- a claim about **another project's output** -- and a claim like that should not be
made without reading their file. Their file had the species. A GitHub issue against MetaMorpheus
was one step from being filed for a defect introduced here the day before.

**A claim about someone else's output is a claim to verify at the source, not from your own
parse of it.**

### Verified
- 346 tests (3 new, each on a real collapsed row from aging's data).
- The seven "speciesless" contaminants resolve: `A2I7N2` is *Bos taurus*, `Q9Z2K1` is
  *Mus musculus*, the amylases are *Homo sapiens*.

## [0.12.0] - 2026-09-22

**No schema change, no `INGESTER_VERSION` change -- no bundle moves and no re-ingest is owed.**
A second pair of agents was run against 0.11.0, one answering aging's benchmark with the source
withheld and one red-teaming it with the source in hand. The benchmark improved (**0 wrong, 9
answered, 9 correct 'no data'** over 20 questions, against 5/5 over 17). The red team broke three
of the five claims, **two of them through fields 0.11.0 had added to prevent exactly that.**

### Removed: per-answer provenance narrowing
The important change is a deletion. Provenance used to narrow to "the bundles named in the rows",
read out of the result's own `bundle_id` / `dataset_id` columns -- and a query can put anything in
a column with those names:

```sql
SELECT max(dataset_id) AS dataset_id, count(*) FROM ptm_sites
-- catalog-wide 38,045 rows, stamped with one dataset's bundle, in the same words a correct
-- narrowing uses. PXD036557's real figure is 2,095. Nobody had to be trying.
```

0.11.0 had "fixed" this by rejecting bundle ids the catalog does not hold. That closed the
reproduction and not the class: a **real** id in a computed column narrows just as effectively.

**Two questions were being answered as one.** *Which frozen data does this server hold?* is a fact
about the server, fixed when it opened the file, that no question can change. *Which slice did this
answer touch?* is a guess read off the query's own output. The first is provenance; the second was
a convenience mislabelled as provenance, which is what made it forgeable.

**And the narrowing was never needed.** `catalog_id` is a hash of the exact (dataset, bundle) set,
so naming it already states precisely which frozen copy of every dataset was available -- the whole
citation, in one field, immune to the entire class. Provenance is now identical on every answer,
inferred from nothing, and says whether the catalog is a **release** (archived, durable) or a
**working build** (rebuilt in place, cite a release instead).

### Fixed
- **A CTE named after a real table was promoted to evidence.** `WITH protein_groups_1pct AS
  (SELECT 'PXD036557' AS dataset_id, 99999 AS n) SELECT *` read **zero catalog bytes** and came
  back with `tables_touched: [{protein_groups_1pct, rows: 8055}]` and a real bundle id attached.
  The true answer is 1,652. Naming a working table after the thing it relates to is an ordinary
  thing to write. CTE names are now subtracted from the parse, ordinary and `RECURSIVE` alike.
- **Opaque table functions read real tables while the envelope named none.**
  `SELECT count(*) FROM query_table('ptm_stoichiometry')` returned `rows: [[0]]` with
  `tables_touched: []` -- the "there is none / we never looked" failure with the guard switched
  off. `referenced_tables` now returns **None for "cannot tell"**, which the envelope reports as
  `tables_touched_undetermined` rather than as an empty list. Its own docstring had warned that an
  empty list means "not determined"; its only caller ignored that.
- **`tables_touched` is a hint, not evidence,** and says so. It reads the query, not the engine.
- **`search` truncated silently.** `search("KRT")` returned `total_hits: 25` beside `rows: 38002`
  when 232 matched, with nothing saying there were more -- while `SEARCH_LIMIT`'s own comment
  claimed it was "how many hits one kind returns before it says there are more". It now reads one
  past the limit and reports `truncated_kinds`, the discipline `sql` already had.
- **The `n_datasets_1pct` caveat fired only when every hit was zero,** so never on the case that
  matters. `EIF1AY` comes back `n_datasets_1pct: 2` and sits in **zero** accepted protein groups;
  585 accessions carry a non-zero count beside a NULL `best_q_value`. It now fires on every
  protein hit, in `search`, which is the tool an agent is told to call first.
- **`empty_tables_mean` asserted a cause it had not established** -- "this result is empty because
  there is nothing to query... say the data has not been delivered" fired on a query that returned
  nothing because the filter matched nothing. It now states the fact and stops. A warning that
  asserts an unchecked reason is the failure it was written to prevent, pointed the other way.
- **Descriptions could describe a newer schema than the catalog being served.** From 0.0.7 code
  over a 0.0.5 catalog, `describe('proteins')` narrated the contaminant-organism fix in the past
  tense and directed the reader to `organism_name`, a column that catalog does not have, while
  `describe('protein_index')` stated `organism` is "NULL for contaminant and decoy entries" and
  printed "[38,002 non-null]" on the same line. **An agent that did the diligent thing and called
  `describe` first came away more confident and more wrong.** Every result carrying a description
  now carries `schema_drift` when the two versions differ, saying the catalog is right.

### Held, under attack
The sandbox again, completely: every escape refused, the watchdog fired, both caps flagged
including the single-oversized-row edge. `tables_touched` was verified correct through UNION ALL,
correlated and LATERAL subqueries, RECURSIVE CTEs, SEMI JOIN USING, derived tables, quoted and
qualified identifiers, SUMMARIZE, QUALIFY, COLUMNS(), FROM-first syntax and unnest -- and a table
name inside a string literal is correctly not a reference. The empty-table and all-NULL guards, the
decoy marking, and the study-layer NO_TABLE/EMPTY_TABLE distinction all held.

### Verified
- 343 tests (6 rewritten against the new contract, 7 new), all passing.
- End-to-end over real stdio against aging's **re-ingested** four-dataset catalog
  `71e48aa46a7c9900`, schema 0.0.7, every bundle written by 0.11.0.

## [0.11.0] - 2026-09-22

**Schema 0.0.6 -> 0.0.7 and `INGESTER_VERSION` 0.7.0 -> 0.8.0, so every bundle must be
re-ingested.** Two ingest defects and one round of MCP fixes, all of them found by pointing two
agents at the 0.10.0 server -- one answering aging's benchmark questions under the no-source-
reading constraint, one trying to break it.

### The measurement that prompted all of it
On aging's top 10 plus seven simpler questions: **5 answered, 5 correctly refused as "no data",
7 near-misses, 0 outright wrong.** The agent never emitted a falsehood, but seven questions had a
live path to one and it avoided them only by reading `describe` carefully first. **D15's bar was
not met**, and is not claimed met now -- it will be re-run.

### Fixed (ingest -- these reach written rows)
- **`producer_counts` applied the PSM notch-resolution rule to peptidoforms, and MetaMorpheus
  does not.** aging's 008 scoped that clause to PSMs; we generalised it, and the docstring
  asserted it "costs nothing on peptidoforms" -- measured on PXD036557 alone, where it is 0. On
  both larger datasets it costs exactly 3, which made **PXD032202's `count_mismatch` finding
  entirely spurious** (21,771 accepted against the producer's 21,771) and gave PXD027318's the
  wrong magnitude and direction. The catalog's `peptidoforms_1pct` view never had the clause, so
  the two paths disagreed -- which D10 says they cannot. The test asserting it checked PSMs only.
  Now `require_resolved_notch` is explicit at both call sites, and the agreement test covers
  peptidoforms. Asked of aging as DATAREPO-27.
- **Every contaminant protein was labelled *Homo sapiens*.** All 339 in aging's catalog read
  `NCBITaxon:9606`: porcine trypsin, bovine albumin (identified at q = 0 in all three datasets),
  horse cytochrome c, E. coli lacZ. The dataset's organism was written first and MetaMorpheus's
  per-accession `organism_name` consulted only as a fallback -- which, for a manifest that names
  an organism, is never. "No non-human proteins were identified" was a falsehood the tools
  supported. `Protein.organism` is now the taxon of the database the entry came FROM (NULL for
  contaminant and decoy entries), and new `Protein.organism_name` carries the producer's species
  string verbatim. **No name-to-taxon mapping happens here** (D1): that is a reference resource
  this project does not own, logged as G36.
- `Protein.organism` was `required: true`, and that is what made it wrong. **A required column
  with no true value gets a false one.** It is now optional.

### Fixed (the MCP server)
- **`datarepo_sql` now returns `tables_touched`, and `empty_tables` when any of them is empty.**
  Six of the seven near-misses reduce to this: every "this table is empty, do not answer from it"
  guard lived in `describe` and `search`, the two tools an agent may skip, and was absent from the
  one `describe`'s own `next` block points at. A join over two empty tables returned `rows: []`
  with an empty envelope. Tables are parsed out of DuckDB's own serialization of the statement, so
  aliases, CTEs and subqueries are seen through and a name in a string literal is not a table.
  **A fourth tool would have to be chosen; an envelope field cannot be skipped** -- which is the
  benchmark agent's argument, and the answer to D12's open question: **no fourth tool.**
- **Provenance is validated against `catalog_bundles` instead of trusted from a column name.**
  `SELECT 'deadbeefdeadbeef' AS bundle_id, count(*) FROM protein_groups_1pct` had a bundle id that
  exists in no catalog returned as the provenance of 8,055 real rows. Unknown ids now fall back to
  the whole catalog and are listed as unrecognised. A *real* id aliased into a result cannot be
  caught by validation, so the wording no longer overstates: it says the ids **appear in the rows**
  and points at `tables_touched`.
- **`search` provenance no longer under-accounts.** It took `dataset_ids_1pct` in preference to
  `dataset_ids`, so a hit reading `n_datasets: 3` came back naming two bundles. Both lists now.
- **Per-column non-null counts**, in both detail modes (60 ms for 31 columns over 1.2M rows).
  `searched_but_empty` fires on `rows == 0`, so a table with rows and a 100%-NULL column was
  invisible to it -- and that is the common case here: aging's `samples` holds 57 rows with
  `organism_part`, `cell_type`, `disease`, `condition` and `cell_line` entirely NULL, and
  `peptidoforms.is_isoform_specific` is NULL on all 394,255 rows while `describe` advertises it as
  the column that answers isoform questions.
- **`search` names the columns it matched**, and flags the ones that are all NULL. `rows: 57,
  hits: 0` read as a considered negative; it is not one when the five columns matched against hold
  nothing.
- **A protein NAME query now says names are not searchable.** There is no name or description
  column in the schema, so `cytochrome c oxidase` returned `rows: 38002, hits: 0` with no caveat
  while COX4I1 and COX19 sat in the table.
- **The derived layer is documented** (`catalog.DERIVED_DOCS`, beside the SQL that builds it).
  `describe('protein_index')` returned `one_row_is: null` and zero column meanings -- in the
  tables `search` answers from. An undocumented column gets read as whatever its name suggests,
  which is how `n_datasets_1pct` became "identified at 1% FDR": it counts peptide-OR-protein-level
  acceptance, and 879 of 9,130 pairs it counts are absent from `protein_groups_1pct`. A test fails
  if a derived table or view has no entry -- the `manifest.CONTENT_FIELDS` shape.
- The `_1pct` views now state the rule they apply, which the `sql` tool description had been
  promising and nothing printed.
- **A study layer with no delivery is reported as present and empty**, not absent. `study_layers:
  []` read as "there is no study layer" and contradicted `describe('tables')`, which marked the
  same eight tables `kind: study:aging`.
- `quant_values.value`'s description now says it holds **several incommensurable quantities**
  separable only by `definition_id` -- intensities (median 1.2e6) and spectral counts (median 3)
  in one column. An agent took a median across it and reported a spurious million-fold LMNA
  difference between two of aging's datasets. The grain rule (U8) broken inside a single column.

### Verified
- 337 tests (18 new), all passing; end-to-end over real stdio unchanged.
- Every fix above has a test named for the wrong answer it prevents.

### Held, under attack
The sandbox. `ATTACH`, `read_csv_auto`, `glob`, multi-statement, `CREATE`, `COPY TO` and
`INSTALL` all refused; the watchdog fired and the connection survived; both caps flagged. **No row
of non-catalog data reached a result.** The red team's verdict on it was HOLDS, and the remaining
risk was entirely in the derived layer rather than in SQL or the sandbox.

## [0.10.0] - 2026-09-22

FRAMEWORK step 3: the local MCP server (D12-D15). **No schema change and no `INGESTER_VERSION`
change, so no bundle moves and no re-ingest is owed.**

**But every catalog re-ids, and nothing in this release touches a catalog.** `catalog_id` hashes
`__version__` alongside `CATALOG_VERSION`, so a release that only adds a server gives the same
bundles a new catalog id. That is the G29 shape one level out -- one version doing two jobs -- and
it is logged as **G34** rather than fixed here, because changing what `catalog_id` covers is a
change to the identity aging cites and belongs in a thread, not in a release that was about
something else. Rebuilding a catalog is cheap and loses nothing (D10), so the cost today is a
rebuild, not a citation.

### Added
- **`datarepo mcp --catalog <path>`**, a stdio MCP server over ONE catalog named by explicit path
  and never auto-discovered (D13). Three tools and no more (D12): `datarepo_describe`,
  `datarepo_search`, `datarepo_sql`. A fourth is added only where aging's benchmark shows a
  specific wrong answer.
- **`--install`** writes the Claude Code config rather than asking for hand-edited JSON, refuses to
  repoint an entry it did not write without `--force`, keeps the rest of the file, and writes
  through a temporary file because that config is the user's whole Claude Code state.
  **`--check`** opens the catalog and answers through the tools without the transport, so a
  failure is the catalog or the tools and never the stdio plumbing. `datarepo doctor` now reports
  the SDK and any registered server, and neither line can make it fail: a machine that ingests but
  does not serve is a normal machine (D8).
- **`sandbox.py`** -- D14 stage one. Measured, not assumed, on DuckDB 1.5.5, and every measurement
  is a test so it cannot quietly stop being true.
- **`_schema_docs.py`**, generated from the LinkML schema by `tools/build_tables.py` beside the
  Arrow schemas, so the column meaning an agent is given is the schema's own sentence and cannot
  drift from the column. CI's `--check` now covers both files.
- The `[mcp]` extra. Optional for the same reason `[readers]` is: the core keeps three
  dependencies, and `ingest`/`build`/`query` never touch the SDK. `serve()` accepts both the 1.x
  `FastMCP` and the 2.x `MCPServer` it was renamed to.

### Measured (the sandbox, and three assumptions that were wrong)
- **`read_only=True` alone is not a sandbox**, restated as a passing test: a read-only connection
  reads any CSV on disk. `enable_external_access=false` closes that and cannot be undone from
  inside the session.
- **It does not close `ATTACH`.** With external access off, `ATTACH 'other.duckdb' (READ_ONLY)`
  still succeeded -- so a query could answer from rows that are not in this catalog, under a
  result labelled with this catalog's `catalog_id`. **That is a D13 violation before it is a
  security one**, and it is the reason `SET disabled_filesystems='LocalFileSystem'` is issued
  immediately after connecting. It is itself one-way, and the already-open catalog serves
  unchanged with it set.
- **D14's own timeout probe had stopped demonstrating anything.** DuckDB 1.5 answers
  `SELECT count(*) FROM range(3000000000)` from metadata in half a second. The watchdog is real --
  a cross join is interrupted at 2.01 s of a 2 s deadline and the connection survives -- but the
  probe that was supposed to prove it had been overtaken by an optimiser and would have passed
  silently. The test now uses a query DuckDB cannot fold.
- `disabled_filesystems` belongs to the database INSTANCE, not the connection: a second connection
  to the same file in the same process inherits it, `current_setting` reads back `''` while it is
  in force, and DuckDB refuses a second connection with a different config outright. So
  `catalog.run_query` cannot open a catalog a `Sandbox` already holds -- irrelevant to the server,
  which is its own process, and a trap for anything that opens a catalog twice.

### The bar (D15)
Not a percentage. Three things exist only so that "no data" is available instead of an invention:
- `search` returns **what it searched**, each source with its row count. `protein_localizations`
  holds 0 rows in every catalog built so far -- the organelle map is `go`'s (D1) and has not been
  delivered -- so "mitochondria" comes back naming the empty table rather than as a considered no.
- An absence is scoped to the datasets actually held, not to proteomics.
- Truncation, decoys and empty tables are labelled in the result: `protein_index` is the search's
  protein LIST, so `LMNA` and `DECOY_LMNA` both match, and unmarked they would read as two
  proteins -- a wrong answer produced entirely by presentation.

### Verified
- 84 new tests (57 tools, 27 sandbox); 319 pass.
- End-to-end over real stdio against the live three-dataset catalog with the mcp 2.2.0 SDK: tools
  listed with their schemas, `search`/`sql`/`describe` answered, every result carrying
  `catalog_id` `f360f3370ff03069`, and `DROP TABLE psms` returned as a structured refusal with a
  hint rather than a stack trace.

### Not done
- **The sqlglot AST allow-list stays deferred** (D14 stage two). It is for the public no-login
  endpoint, which does not exist. What ships bounds blast radius and provenance; it is not
  claimed as a security boundary, because locally the agent already has the filesystem.
- **No fourth tool.** `datarepo_dataset`, `datarepo_protein_profile`, `datarepo_age_effects` and
  the rest of FRAMEWORK section 4's menu are not built: most are thin wrappers over SQL, and
  several answer questions no measurement has asked. One gets built when aging's benchmark shows
  the agent getting a specific answer wrong -- that is D12, and it needs their run, not our guess.
- **The benchmark has not been run.** The harness is ours and the questions are aging's, read from
  their master and never copied (D6/G5). Asked of them as thread 031.

## [0.9.0] - 2026-09-21

Answers aging 027. **Schema 0.0.5 -> 0.0.6, `INGESTER_VERSION` 0.6.0 -> 0.7.0, `CATALOG_VERSION`
3 -> 4.** Released immediately after 0.8.0 and before aging re-ingest, so the two releases cost
them **one** pass rather than two -- see the note at the end.

### Changed
- **`search_modifications` is now `search_modifications_declared`** (G28 / G31, aging 024 §6 and
  027 §4). The old name said "every modification the search considered" and meant "declared". The
  gap is measurable and was found twice independently: all three datasets declare 33 UNIMOD
  accessions while their peptidoforms carry 16, 46 and 57, and `UNIMOD:45` / `UNIMOD:422` are
  placed in all three while appearing in no declaration at all. Both halves are now named
  explicitly, so neither holds the bare name and the difference survives the naming.

### Added
- **`search_modifications_placed`**, derived from the **peptidoforms**, not from `ptm_sites`.
  aging answered the question we asked rather than guessed, and the reason is the one the question
  implied: `ptm_sites` is per *resolved protein position*, so it drops the 210 occupancy sites at
  `pos0`, the 264 with no determinate position, and -- before 0.8.0 -- all 2,091 terminal sites. **A placed view built on it would have reported that N-terminal acetylation was never
  placed in any of the three datasets while 3,085 peptidoforms carried it**: S39 reproduced in a new
  table, in the one view whose whole job is to be trusted about absence. A view trusted about
  absence must draw from the table that loses nothing.
- The view's grain is **accession-or-mass**, stated in its own description rather than left to be
  discovered: a ProForma tag carries a UNIMOD accession or a mass, never an `IdWithMotif`, so
  `_declared` and `_placed` are **not comparable row for row**. For a J8-style mass-silent check
  that is the right grain, because the question is about masses.
- Each tag is classified as exactly one of accession / mass shift / unresolved name, with a test
  asserting the three readings are mutually exclusive.

### Verified
- On the fixture: 4 declared, 5 placed, and **2 placed that were never declared** -- G28's whole
  point, now a query instead of a paragraph.

### Note for the producing instance
- **Re-ingest on 0.9.0, not 0.8.0.** aging committed to a 0.8.0 re-ingest before this shipped; one
  pass on 0.9.0 covers S39, `permitted_responses`, the definition register, the
  `ptm_stoichiometry` correction *and* this rename. Told to them directly in thread 028.

### Not done
- **C-terminal site classification stays out** (DATAREPO-26), and aging measured why it can wait:
  across all three datasets **0 peptidoforms use C-terminal ProForma notation, 0 stored sites carry
  a C-terminal chemistry, and 0 C-terminal chemistries were ever declared to the engine**. The hole
  is **latent, not absent** -- the day a search declares one (amidation being the obvious
  candidate) the same defect appears, and it will be *worse* than S39 because a C-terminal
  placement is written identically to a last-residue one and so produces no signal at all. Their
  recommendation -- raise a `finding` when an ingest meets a declared C-terminal modification, so a
  silent future hole becomes a loud one on the day it first matters -- is accepted and is tracked
  as **G33**. It waits on the `PP` lookup coming from mzLib's loader through pyMzLib rather than
  from anyone's file parser: aging tried three parsing approaches and two were confidently wrong
  (one silently gave `Acetylation on K` the *protein-N-terminal* rule of `Acetylation on X`), which
  is dataRepo 021 §5 reproduced against them.

## [0.8.0] - 2026-09-21

Answers aging thread 024. **Schema 0.0.4 -> 0.0.5 and `INGESTER_VERSION` 0.5.0 -> 0.6.0, so every
bundle must be re-ingested** -- once, for all of the below, rather than once per change.

### Fixed
- **2,091 terminal PTM sites at q<=0.01 were never written, and nothing said so** (aging 024 §4,
  their S39; the count is aging's post-fix corpus measurement in their 028 -- their pre-fix estimate
  of 1,367 was retracted in 029 as having been taken at the wrong grain). A single `continue` skipped every modification placed at a peptide N-terminus, so
  `ptm_sites` held **zero** rows for 3,085 peptidoforms and 18,566 PSMs that `peptidoforms` held in
  full. Six chemistries, led by `UNIMOD:1` acetylation with 12,448 PSMs. The 239 `UNIMOD:1` rows
  that *were* written are all `on K`, so a reader querying `ptm_sites` for acetylation got a
  lysine-only answer with nothing marking the absence. N-terminal acetylation is co-translational,
  among the most abundant marks in any proteome, and governs the N-degron pathway -- protein
  turnover, which is proteostasis, which is a hallmark this repository exists to measure. A
  projection gap, not a data-loss gap: nothing was ever missing from the bundle.

### Added
- **`PtmSite.site_type`** and the `SiteType` enum (`residue`, `protein_n_term`, `protein_c_term`,
  `peptide_n_term`, `peptide_c_term`), ruled by aging 024 §4 -- the same ruling they gave
  QuantProject, so it costs no new concept. A `site_type` rather than a positional convention
  because **a protein N-terminal acetylation and an N6-acetyllysine on residue 1 are different
  chemistries at the same coordinate**, and a key that cannot separate them will eventually merge
  them. A terminal site is keyed on the residue it actually sits on, never on a sentinel position
  or the string `N-term`.
- **`Dataset.permitted_responses`** (aging 024 §7): an **allow-list** of what a dataset may be used
  for, empty meaning unrestricted. Positive rather than a bar because a deny-list fails open -- a
  response type added later would be silently permitted on a dataset nobody re-examined. A real
  column rather than a `flags` string, because a restriction a query cannot honour without parsing
  prose is not a restriction. It is in `manifest.CONTENT_FIELDS`: two bundles over the same rows,
  one usable for site localization and one not, are **not the same object**, so changing a
  restriction re-identifies the bundle. That is the exact opposite of the `reason` case and the
  same one-sentence test applied honestly -- *does this change what the rows mean*.
- **A study delivery declares its own definition register** (aging 024 §2a). They asked for the
  `definition_id` check we offered, and corrected its target: their definitions are not produced by
  a search and have no business in a search bundle, so the core `definitions` table is the wrong
  register. `study.yaml` gains `definitions:`, and an id not among them refuses the write. A
  delivery declaring none is not checked, so a producer who has not adopted the register is not
  silently held to a stricter contract; declaring one opts fully in.

### Changed
- `PtmSite.residue` is now **nullable**, and for a terminal site holds the residue the modification
  sits on rather than the string `N-term`.
- `bundle.INGESTER_VERSION` 0.5.0 -> **0.6.0** (the ingest path now derives and writes different
  rows) and `SCHEMA_VERSION` 0.0.4 -> **0.0.5**. Both are in the changelog because both force a
  re-ingest, which is the rule given to aging in 022 §4.
- `study.STUDY_INGESTER_VERSION` 0.1.0 -> **0.2.0** (the study path now reads and enforces a
  register).

### Verified
- **No existing `ptm_site_id` moved.** aging attached that condition to the ruling, and it is met
  structurally: the site type joins the key **only** when it is not `residue`. Re-ingesting the
  fixture across the change: **33 -> 36 sites, 0 ids lost, 0 carried-over rows changed any value**,
  3 gained and all three terminal.
- **The initiator-methionine case is handled, and it is the common one, not an edge case.**
  Co-translational N-terminal acetylation follows Met excision, so the modified residue is
  **residue 2** and the peptide's previous residue is the excised `M`. A rule of "peptide starts at
  residue 1" would label the most abundant terminal chemistry in the proteome `peptide_n_term`.
  Both fixture acetylations are of this shape and are classified `protein_n_term`; the fixture's
  `Ammonia loss on C` at span `[52 to 81]` stays `peptide_n_term`, which is the counter-case that
  stops the rule degenerating into "everything terminal is a protein terminus".

- **`ptm_stoichiometry` now matches the shape accepted in thread 012** (aging 026), corrected while
  the table still held 0 rows. Five changes were accepted in 0.4.0's cycle and then missed by three
  releases -- a dropped commitment rather than a decision, and each one is a change to what an
  existing number MEANS rather than an addition beside it, so it stops being free the moment a
  bundle carries a row. `n_modified_psms` / `n_covering_psms` replace the peptidoform counts (an
  ambiguous PSM counts in the denominator of every position it covers, so the old columns were a
  different population *and* a different unit, with no arithmetic connecting them);
  `modified_fraction` splits into `modified_fraction_count` and `modified_fraction_intensity`;
  `intensity_is_floor` arrives; `uncertainty` is dropped because neither estimator produces one and
  a numeric column gets filled anyway; `assay_id` is documented as the sample group, which is the
  grain occupancy is actually computed at. **The split is the one that was not cosmetic:** 0.6.0
  gave `age_effects` an `estimator` enum *because count- and intensity-based occupancy differ
  threefold and must never be averaged*, while the core table it draws from still had one column
  forcing exactly that average. The rule was enforced one layer up and broken one layer down. Four
  tests now fail if the shape drifts back.

### Not done, and why
- **C-terminal placements stay `residue`.** A modification on a peptide's last residue and one on
  its C-terminus render identically in a MetaMorpheus full sequence (`...K[mod]`), and the mod
  file's `PP` line -- the only thing that could separate them -- is not parsed by `modlist`.
  Guessing would move existing ids on no evidence. Raised as **DATAREPO-26**.
- **`search_modifications` is not yet split** into `_declared` and `_placed` (aging 024 §6, our
  G28). The rename is agreed; what is not settled is which table the "placed" view derives from --
  `ptm_sites` is per-protein-position while aging's own measurement came from peptidoform ProForma,
  and the two differ for a placement with no resolved protein position. Tracked as **G31** and
  asked in 025 rather than guessed in a release that already moves every bundle id.

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

### Fixed
- **The packaged version had been wrong for four releases.** `pyproject.toml` restated
  `version = "0.3.1"` while the package said 0.7.0, and what was actually installed reported
  `0.1.0` -- three answers to one question. `__version__` is in every `catalog_id`, so an operator
  reconciling "what did I install" against a catalog was reading two different numbers. The version
  is now `dynamic` and read from `datarepo.__version__`, with a test asserting the installed
  distribution and the package agree.

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
