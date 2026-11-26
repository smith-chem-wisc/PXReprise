---
title: "0094 Each run's sample type and each search's cysteine chemistry are labels PXReprise sets"
parent: Decisions
nav_order: 94
status: proposed
date: 2026-11-25
decision-makers: the maintainer
---

# Each run's sample type and each search's cysteine chemistry are labels PXReprise sets

## Context and Problem Statement

ptmQtl's main measurement is occupancy: the share of a residue that carries a modification in one run. It means
something only when a run's modified and unmodified peptides sit in their natural proportion. Two things break that,
and the numbers alone do not show it:

* **An enrichment** keeps the modified peptides and discards most of the unmodified ones, so occupancy comes out
  falsely high. No occupancy result is made on one.
* **A cysteine chemistry the search did not assume.** The leftover mass is reported as a biological modification on a
  nearby residue (NEM's +68 Da as crotonyl-K), and the cysteine sites are wrong.

Until now ptmQtl's engines took both from hand-made lists of datasets (`--input enrichment`, `capture_runs`,
`cysteine`), because the repositories' descriptions are often wrong: a ubiquitin pulldown can be described only as
"affinity purification". A list works only while someone keeps it current. ptmQtl's maintainers decided that each
dataset should carry its own labels instead: PXReprise sets them, and ptmQtl decides what each label means for its
results.

## Considered Options

* PXReprise labels each run and each search, from what the pipeline already records, and a person can overrule it
* ptmQtl publishes versioned lists that ship with the engine
* The question names the lists

## Decision Outcome

Chosen option: "PXReprise labels each run and each search". The pipeline already reads the deposit's text, measures
its probe runs and records its search's chemistry; a label combines them, and says where it came from.

* **Two labels.** Each run's **sample type**: `whole_proteome`, `ptm_capture` (with the modification), `protein_pulldown`
  (with the kind) or `unknown`. Each search's **cysteine chemistry**: the reagent the lab used, what the search
  assumed on C, the share of its 1% target peptides that contain C, and a verdict, `matches`, `mismatch` or `unknown`.
* **Every label carries its source** (`repository_text`, `data_check`, `person`), its evidence, and a reason in words.
* **A disagreement between the text and the data is `unknown`, never the text repeated.** Once one probed run of a
  deposit contradicts its text, no run of it takes the text's label: a fraction of an enriched sample is enriched too,
  and only a design or a person can say which runs are the capture and which the lysate.
* **The data check uses the probe search's per-run shares**, at the thresholds the probe already judges by: phospho
  5%, GG 2% and acetyl-K 5% of a run's PSMs, from runs with at least 50 PSMs at 1%. A run the probe did not search has
  only the text, and says so. The cysteine verdict uses the search's own cysteine-peptide share and its
  `cysteine_poor` line, 5%: at or above it the search matches; below it, a reagent named and searched is `unknown`
  (the two disagree), and no reagent named is a `mismatch`. NEM named but not declared in the search is a `mismatch`.
* **A person's decision outranks both** and is kept with who made it, when and why: a run's own decision first, then
  one for every run of the dataset. A decision naming an accession covers its split datasets.
* **The `labels` verb** writes a deposit's `07_labels/labels.json` (schema `pxreprise-labels/1`) from the deposit's
  folder, reading no spectra.
* **Not decided here:** the labels' place in the stored record (the bundle's format is datarepo's), where a person's
  decisions live so that they survive reprocessing, the batch step that runs the verb after each search, and a data
  check of every run rather than the probe's few.

### Consequences

* Good, because a new deposit is labelled as it is processed, with no list to keep current.
* Good, because every label can be traced to the words or the measurement behind it.
* Bad, because the probe measures only phospho, GG and acetyl-K, and only on a few runs per deposit, so a capture of
  anything else (S-nitrosylation, succinyl, a glycan) is taken from the text alone, and so is every unprobed run.
* Bad, because a deposit whose runs contradict its text loses every run to `unknown` until a person decides.

### Confirmation

Checked on the 284 deposits of the first question's batch with a search (8,507 runs), against ptmQtl's hand lists.
Without any decision, 82 of the 98 deposits on its capture list get the same effect from the labels; 2 are looser (an
S-nitrosylation capture the text calls none, and a deposit with no probe measures), and 7 stricter. With the lists
loaded as a person's decisions, 90 agree, the 7 others have no search there, and the last is one the list leaves to
the labels. Of the 11 deposits on the cysteine list, 9 have a search there and all 9 agree once loaded; without them,
5 agree and 4 are looser (two MMTS searches at 10-12% cysteine peptides, and two at 5.1% and 7.6%).
