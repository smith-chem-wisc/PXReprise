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
| `02b_qc_probe/`, `02b_qc/` | Quality checks on the probe files, then on every file (`qc_report.json`). Each file's entry also records what the file says about itself, while it still exists: `start_time`, `instrument_model`, `instrument_model_accession` and `instrument_serial`, each only when the reader gives it. A Thermo RAW `start_time` is the instrument's local clock with no time zone, written as read and never converted. |
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
| `deferred_mixed_labelling` | Some files' spectra carry isobaric reporter ions (TMT, iTRAQ) and some do not. Searching only part of the deposit is a design decision; the state entry's `isobaric_files` lists the labelled files. | yes |
| `search_interrupted` | The search was cut off by the environment, not by its data: the machine's .NET runtimes changed under it (searched again on a later pass, twice at most; set `dotnet_root` to prevent it), or the batch itself stopped mid-search (a reboot, a crash). In the second case the next `batch run` moves the partial output aside and searches the deposit again, up to three times. | no |
| `excluded_duplicate` | Every raw file (name and size) is in a deposit already searched, or in the one being searched at that moment; the state entry's `duplicate_of` names it. Decided from PRIDE's file list, before any download. | yes |
| `waiting_*` | The deposit needs a capability no available profile has, named after the underscore: `waiting_dia`, `waiting_tmt_dda_1`, `waiting_crosslinking`, `waiting_o18_labelling`, `waiting_nonspecific_cleavage` (immunopeptidomes and peptidomes, whose peptides no protease made), `waiting_top_down` (intact proteins), `waiting_dileu_labelling`, and so on. Under a profile that reads each deposit's chemistry: `waiting_multi_protease` (several proteases, and the files cannot be told apart), `waiting_unknown_protease`, `waiting_unknown_modification`, `waiting_<label>_labelling` (e.g. `waiting_tmt_labelling`, when PRIDE or the SDRF says the deposit is labelled). | until that profile exists |
| `fetch_unavailable`, `probe_fetch_unavailable` | PRIDE kept dropping the downloads through every retry. Tried again on a later pass, `fetch_passes` times in all (default 3). | no |
| `fetch_failed`, `probe_fetch_failed` | The downloads failed on every pass, or failed in a way a retry cannot fix (for example a checksum mismatch). | yes |
| `search_failed` | MetaMorpheus failed; see `04_search/metamorpheus.log`. | yes |
| `on_hold_user` | Held by the question's `[holds]`. | until removed |
| `requeued_user` | Put back in the queue by `pxreprise batch retry`; the entry's `requeued` list says who, when, why, and what it was before. | no |

**After a reboot or a crash,** run the same `batch run` command again. It finishes what was cut off: a search is redone,
and a deposit whose delivery to the repository did not finish is delivered again. The state directory's `driver.last.json`
records the last batch's exact command, so a script or a scheduled task can restart it.

To see **why** a deposit has its status, search `batch.log` for its accession: each decision is logged with its reason. A status's `detail` belongs to that status; when a later status replaces it (a download that failed once and then succeeded), the old text moves to `earlier_detail`.

An enriched deposit (for example an affinity pulldown) is searched like any other, and its record carries an
`enrichment` field, because an enrichment is not a whole proteome and its intensities must not be pooled as one.

If every raw file's name says what kind of sample it is (for example `Lysate_`, `IP_`, `EV_`) and there is more than one kind, the
manifest entry gets the flag `possibly_mixed_enrichment` and a note, and `batch.log` says `POSSIBLY MIXED SAMPLES`. Nothing
else changes: which run is which enrichment is for you to curate (dataRepo's `run_enrichment`), not for file names to decide.
