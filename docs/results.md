---
title: Reading the results
nav_order: 8
---

# Reading the results

## Where everything is

A question's `[batch]` settings name two folders:

- **`state_dir`** holds the batch's own records: `batch.log` (what happened, in order, with times in UTC),
  `state.json` (every deposit's status) and `queue.json`.
- **`run_root`** holds one folder per deposit, named by its PRIDE accession.

Inside a deposit's folder:

| Folder | Contents |
|---|---|
| `02_fetch/` | `fetch_manifest.json`: every downloaded file, with its size and SHA-256. The raw files themselves are deleted after the search. |
| `02b_qc_probe/`, `02b_qc/` | Quality checks on the probe files, then on every file (`qc_report.json`). |
| `04_search/` | The MetaMorpheus run: `metamorpheus.log`, the exact task settings (`mm/Task Settings/`), and the results. |
| `04_search/mm/Task3SearchTask/` | **The results.** `AllPSMs.psmtsv` (every peptide-spectrum match), `AllPeptides.psmtsv`, `AllQuantifiedProteinGroups.tsv` and `AllQuantifiedPeptides.tsv` (label-free intensities per file), `results.txt` (the summary). |
| `05_qc/` | `report.html`, a quality report to open in a browser, and its data (`qc_payload.json`, `tables/`, `figures/`). |
| `09_cleanup/` | What was deleted, and how much space it freed. |

Every folder has a `provenance.json`: the exact inputs with checksums, the commands, the tool versions, and notes on
anything unusual.

MetaMorpheus's output formats are documented in the
[MetaMorpheus wiki](https://github.com/smith-chem-wisc/MetaMorpheus/wiki).

## What each status means

`pxreprise batch status question.toml` counts deposits by status. Most settled statuses are final: the batch does not
spend time on them again.

| Status | Meaning | Final? |
|---|---|---|
| `searched` | Done. Results are in the deposit's folder. | yes |
| `probing`, `fetched`, `searching` | In progress. | no |
| `requeued_transient_fetch` | Put back in the queue after a download problem. | no |
| `deferred_needs_design_selection` | More raw files than the profile allows (60). Not subsampled automatically: choosing which runs to keep is a scientific decision. | yes |
| `deferred_large_files` | Files so large that PRIDE downloads tend to fail midway. | yes |
| `deferred_search_too_large` | Too many spectra for one search under this profile. | yes |
| `skipped_acquisition`, `skipped_acquisition_full` | The files failed the quality gates (for example, MS2 read in the ion trap), in the probe or the full check. | yes |
| `skipped_organism` | The profile has no database for this organism. | yes |
| `waiting_*` | The deposit needs a capability no available profile has, named after the underscore: `waiting_dia`, `waiting_tmt_dda_1`, and so on. | until that profile exists |
| `fetch_failed`, `probe_fetch_failed` | PRIDE downloads kept failing after every retry. | yes |
| `search_failed` | MetaMorpheus failed; see `04_search/metamorpheus.log`. | yes |
| `on_hold_user` | Held by the question's `[holds]`. | until removed |

To see **why** a deposit has its status, search `batch.log` for its accession: each decision is logged with its reason.

An enriched deposit (for example an affinity pulldown) is searched like any other, and its record carries an
`enrichment` field, because an enrichment is not a whole proteome and its intensities must not be pooled as one.
