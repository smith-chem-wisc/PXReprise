# Changelog

Every release says three things before anything else: whether it needs a different MetaMorpheus, whether your machine
file needs a change, and whether it is safe to update while a batch is part-way through a queue. How to update:
section 7, "Updating", of [Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

A new version of the engine never changes a finished result. Methods live in versioned profiles
(`label-free-dda@2`), and every result's `provenance.json` records the PXReprise version that made it.

## 0.3.5 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again.

- **mzLib 1.0.593.** PXReprise now uses mzLib 1.0.593, one release ahead of the 1.0.592 inside MetaMorpheus 1.1.11.
  That is safe because MetaMorpheus runs as its own process. Searches are unchanged: MetaMorpheus does them with its
  own mzLib.
- What changes for PXReprise: PRIDE now reports every dropped or timed-out download as `HttpRequestException`, the
  error the batch already retries, so dropped transfers no longer depend on PXReprise's own workaround from 0.3.3
  (kept as a safety net). Download error messages name the file and host, never the full URL.
- Every record's `tools.mzlib` now reads `1.0.593+…`.

## 0.3.4 (2026-10-02)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: new optional `dotnet_root` (a private .NET for
MetaMorpheus). Safe mid-batch: stop the batch, update, run it again.

- **TMT and iTRAQ are recognised from the spectra.** Some deposits never mention their labels in the text, so the
  text screen cannot see them (one TMT six-plex was searched as label-free). QC now looks for reporter ions in every
  MS2 and MS3 spectrum. It uses mzLib's reporter masses for TMT, iTRAQ and DiLeu, matched within 3 mDa. A file
  counts as labelled when at least a quarter of its spectra carry three or more reporters of one kit. On real data,
  label-free files show 0 to 0.01% and TMT files 50 to 79%.
- If every file is labelled, the deposit waits for the isobaric profile (`waiting_tmt_dda_1`), as if the text had said
  so. If only some files are, it becomes `deferred_mixed_labelling`: searching only the label-free files is a design
  decision, not one to make automatically. Only a profile that does not take isobaric labels looks for reporters.
- **A search that fails because .NET changed under it is searched again.** An automatic .NET update replaced the
  runtime that a running MetaMorpheus was using, and the search failed 2 h 21 m in. The batch now records the
  installed runtimes before each search. If a search fails and that set has changed, the deposit becomes
  `search_interrupted` and is searched again on a later pass, at most twice. Every search's provenance records the
  runtime it used.
- **Optional `dotnet_root` in the machine file** runs MetaMorpheus on a private copy of .NET that no updater manages,
  through `DOTNET_ROOT`. `machines/example.toml` says how to make one.

## 0.3.3 (2026-10-02)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. Deposits already settled keep their status; the new screens apply to deposits not yet probed.

- **A dropped TLS connection is retried, not fatal.** When EBI closes a connection mid-download, .NET can throw a plain
  `IOException` ("Received an unexpected EOF or 0 bytes from the transport stream") instead of an `HttpIOException`.
  The batch treated it as permanent and dropped the deposit after one failure. It is now retried like any other
  dropped transfer. A local disk error still fails at once.
- **Duplicate deposits are refused before any download.** If every raw file of a deposit (by name and size, from
  PRIDE's file list) is already in a searched deposit, the deposit gets the status `excluded_duplicate`, and its state
  entry's `duplicate_of` names the original. A deposit that shares only some files is searched, and the shared files
  are logged. On the aging corpus, the only duplicate among 78 searched deposits is PXD012985, which repeats PXD011740.
- **Crosslinking (XL-MS) deposits are screened out** as `waiting_crosslinking`. The screen looks for names that mean
  only crosslinking MS (`XL-MS`, `iqPIR`, `DSSO`, `DSBU`, `BS3`, "cross-linking mass spectrometry"). `DSS` and `PIR`
  count only beside a crosslinking word: DSS also names a colitis model, and PIR a protein database. "Crosslinked"
  alone never counts, because hydrogels and ChIP samples are crosslinked too. A profile can opt in with
  `crosslinking = true` under `[accepts]`; none does.
- **18O-labelled deposits are screened out** as `waiting_o18_labelling`, found by `18O`, `O18`, `16O/18O` and similar
  terms.
- Over the 259 queued aging deposits with a PRIDE record, the two new screens flag PXD062841 and PXD028282, the two
  that were searched by mistake, plus two that other screens had already excluded.

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
