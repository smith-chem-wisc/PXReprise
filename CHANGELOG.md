# Changelog

Every release says three things before anything else: whether it needs a different MetaMorpheus, whether your machine
file needs a change, and whether it is safe to update while a batch is part-way through a queue. How to update:
section 7, "Updating", of [Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

A new version of the engine never changes a finished result. Methods live in versioned profiles
(`label-free-dda@2`), and every result's `provenance.json` records the PXReprise version that made it.

## 0.3.2 (2026-09-30)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: yes. Only `census` changes;
a running batch reads the queue it already has.

- **The census takes organisms from PRIDE's project record, not its search index.** PRIDE's search index sometimes
  stores SDRF column headers as metadata terms. For example, `Characteristics[organism]` appears as an organism, and in
  one deposit it was the first organism listed. For every relevant deposit, the census now reads the project record.
  It uses that record's organisms both for the `[discover] organisms` filter and to choose the queue's database.
  The search index is still used to find deposits.
- `census.tsv` gains `organisms_of_record` and `organism_source` (`project`, `search`, or `search_fallback`), and
  `queue.json` entries gain `organism_source`. If PRIDE cannot serve a record, the deposit is kept. It uses the search
  index's organisms with the SDRF headers removed, and it is marked `search_fallback`.
- A census now fetches one project record per relevant deposit, so it takes longer.

## 0.3.1 (2026-09-30)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. On that restart, any deposit whose ingest had failed is ingested again.

- **A failed ingest is retried at the next start.** The batch writes a deposit's manifest entry just before it runs
  `datarepo ingest`, and it treated "in the manifest" as delivered. So a deposit that dataRepo refused was never
  ingested again, even after dataRepo was fixed. A deposit now counts as delivered only when its recorded ingest
  succeeded. When the batch starts again, it re-runs ingest and publish from the finished search, so nothing is
  downloaded or searched again.
## 0.3.0 (2026-09-29)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: recommended new line `datarepo` (the dataRepo download);
new optional `search_timeout_h`, `search_stall_minutes`. Safe mid-batch: stop the batch, update, run it again.

- **The whole pipeline, with no Python.** Each searched deposit now goes into a [dataRepo](https://github.com/smith-chem-wisc/dataRepo)
  repository and a website, using dataRepo's ready-to-run download (0.31.0; Windows, Linux, macOS). `examples/first-run`
  ends with `work/site/index.html`. Getting started, step 3b, installs it.
- **A new question's manifest is created for it,** with roots relative to the manifest; an entry's `run` is taken
  against the manifest's own `work_root`. A `[publish]` command may use `{datarepo}` and `{manifest_dir}`.
- **A busy machine no longer kills searches.** A search's time limit was the profile's `timeout_h` (6 h), so
  MetaMorpheus 1.1.11's long, silent PEP step could be killed on a loaded box while still working. The limits now live
  in the machine file: `search_timeout_h` (default 48 h) is a ceiling, and `search_stall_minutes` (default 90) kills a
  search only when MetaMorpheus has used no CPU for that long, i.e. when it is hung. Profiles' `timeout_h` is read but
  no longer used. Provenance records the limits used, and says which one killed a search.
- **Provenance points at the public repository.** Each stage's `provenance.json` now names
  `https://github.com/smith-chem-wisc/PXReprise` as `pipeline.repo`, instead of a development repository no reader
  could open.
- **The census record is renamed** from `pxreprise-provenance/1` to `pxreprise-run/1`. `pxreprise-provenance/1` is
  reserved for the per-stage record dataRepo ingests.

## 0.2.0 (2026-09-29)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged; two optional settings are new. Safe mid-batch:
stop the batch, update, run it again.

- **A PRIDE outage no longer loses deposits.** A deposit whose downloads fail through every retry was settled for good
  as `fetch_failed`. It is now `fetch_unavailable`, not settled. The batch finishes the queue, waits, and tries it
  again, up to three passes in all, before settling it. A checksum mismatch or a missing file still settles at once.
- **New optional machine settings:** `fetch_passes` (default 3) and `pass_wait_minutes` (default 30).
- **A server timeout counts as an outage.** An HTTP timeout from PRIDE used to escape the retry handling; it is now
  retried like any other outage.
- **Docs:** how to update an installation (Getting started, section 7; `AGENTS.md`, "Updating").

## 0.1.0 (2026-09-29)

First public release. The batch, census and question tooling; profiles `label-free-dda@1` and `@2` (MetaMorpheus
1.1.11); the `first-run`, `t2d` and `muscle-ageing` examples; the documentation site and tutorial.
