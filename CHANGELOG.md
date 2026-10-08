# Changelog

Every release says three things before anything else: whether it needs a different MetaMorpheus, whether your machine
file needs a change, and whether it is safe to update while a batch is part-way through a queue. How to update:
section 7, "Updating", of [Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

A new version of the engine never changes a finished result. Methods live in versioned profiles
(`label-free-dda@2`), and every result's `provenance.json` records the PXReprise version that made it.

## 0.4.2 (unreleased)

**Updating:** no MetaMorpheus change (1.1.12). **Machine file:** nothing required. The aging box turns on
`catch_up_every = 4` (D39). **Databases:** profile `label-free-dda@5` names three new files under `database_dir`:
`UP000005640_all_genecentric_2026_03_agingPTM-v3.xml`, `UP000000589_...` and `UP000002494_...`. They are built by
`tools/ptm-db/BuildPtmDb.cs` from UniProt's canonical proteomes (see `data/ptm-db/agingPTM-v3/`).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again.

- **A new profile, `label-free-dda@5`:** `@4` on UniProt's **canonical** reference proteomes (D38). That is one protein
  per gene, Swiss-Prot plus TrEMBL for genes Swiss-Prot lacks: human 20,652, mouse 21,860, rat 21,471 entries, against
  20,416, 17,277 and 8,228 reviewed. agingPTM-v3 carries v2's sites. The deposit caps rise to 120 raw files and 3,000,000
  estimated MS2 (were 60 and 1,500,000), because MetaMorpheus 1.1.12 and two search slots search much faster.
- **A new profile for MS2 read in the ion trap, `label-free-dda-lowres@1`.** It runs the Search task only: no
  calibration until low-res calibration is merged in MetaMorpheus, and no GPTMD (the user). It searches against the same
  canonical agingPTM-v3 XML, with fragmentation written per file (ion-trap CID as LowCID, product tolerance
  ±0.5000 Absolute), and keeps no spectral library. The screen never routes a deposit to it. A probe whose files fail
  only `low_res_ms2` under the question's earlier profile is re-checked under it, when the question names it. 34 aging
  deposits were skipped as `low_res_ms2` before this profile existed.
- **`BuildPtmDb.cs` reads either UniProt namespace.** It used to read only `http://uniprot.org/uniprot`, which UniProt's
  REST files use; UniProt's FTP files declare `https://` and were read as empty. It now refuses to write a database
  that matches fewer than half of the sites, instead of writing it with zero sites.

## 0.4.1 (unreleased, superseded by 0.4.2 before it ran)

**Updating:** no MetaMorpheus change (1.1.12 as in 0.4.0). **Machine file:** two optional keys, both off by default:
- `search_slots` (default 1): how many searches run at once;
- `catch_up_every` (default 0 = off): every Nth search started is a re-search of a deposit searched under a profile
  the question no longer names (D39).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again.

- **Searches run side by side** (`search_slots = 2` on the aging box; the user: "start a second search immediately
  following any complete download"). A deposit starts searching as soon as it is downloaded and a slot is free, and the
  next one is screened and fetched meanwhile. One slot keeps the old rhythm.
  - **Another species goes first:** while a search runs, the next deposit is the first queued one of another species,
    if any. Two searches of one species cannot both extend its spectral library: the second to finish is refused
    registration (no spectrum is lost; that run's library stays in its folder) and the search itself still succeeds.
  - **Shared steps stay one at a time:** delivery (manifest, ingest, publish) and spectral-library registration.
- **Catch-up re-searches** (D39, off until the canonical-proteome profile): every Nth start re-searches an old deposit.
  The earlier search is moved aside as `batch research` does. Order by gain: rat first, then deposits the question has
  a curated design for, then the oldest, preferring a species no running search has.
- **Every database GPTMD wrote is recorded** with its sha256 (G33). Before, only the first one found was recorded,
  which was the contaminant file, never the proteome the final search loaded.

## 0.4.0 (unreleased: waits on the MetaMorpheus 1.1.12 trial)

**Updating:** **MetaMorpheus 1.1.12** (mzLib 1.0.594) for the new profile `label-free-dda@4`; 1.1.11 stays for the
older profiles. **Machine file changes:**
- add `"1.1.12" = "<path to 1.1.12's CMD.exe>"` under `[metamorpheus]`;
- before the first ingest of a 1.1.12 search, `metamorpheus_settings_root` must hold a `1.1.12` folder (a copy of the
  install), or datarepo resolves no modification and says so only in its findings;
- keep `fetch_stall_minutes` at 5 or more (below).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again. Results already searched keep the
version that made them.

- **A new profile, `label-free-dda@4`:** `@3` (each deposit's own chemistry) on MetaMorpheus 1.1.12, with three more
  changes:
  - **an experimental design from SDRF** (`[quant] design = "sdrf"`). The question's curated SDRF comes first, then the
    deposit's own, through mzLib's `SdrfLabelFreeDesign`. With neither, PXReprise writes its own default SDRF
    (`pxreprise_sdrf/<PXD>.pxreprise_default.sdrf.tsv`: one condition, `all`, and each file its own biological
    replicate) and builds the design from that, so no search runs without one. MetaMorpheus quantifies with MBR from the
    design. A fractionated deposit needs a curated SDRF: under the default, each fraction counts as a sample;
  - **the search writes its own SDRF**, `experiment.sdrf.tsv`, stamped with the deposit's ProteomeXchange accession
    (`[engine] write_sdrf = true`; MetaMorpheus #2816, #2870);
  - **new PTM-annotated databases**, `*_agingPTM-v2.xml`;
  - **Run-level QC and contamination read protein-group columns through the design.** With a design, FlashLFQ names
    protein-group columns by sample (`aged_1`), not by file. Each label is now mapped to its one file through the
    search's `ExperimentalDesign.tsv`. A sample spanning several files (fractions, re-injections) gets no run-level value,
    and the payload's notes say so. Before this, the 1.1.12 trial reported `pg_missing_frac_msms = 1` for every run and
    lost `contaminant_intensity_frac`.
  - **Fragmentation per file, from the MS2 headers (new profile option, unused by any shipped profile):** with
    `[engine] fragmentation = "per_file"`:
    - QC decides each file's `DissociationType` from its own scans: HCD, CID, ETD or EThcD; LowCID for CID read in an
      ion trap; Autodetect only for a high-resolution mix of supported types;
    - QC fails an unsupported or mixed-analyzer file (`unsupported_fragmentation`);
    - the search writes the type into the file's `<raw>.toml`, beside its protease.
  - **A probe search before the full download (new profile option, unused by any shipped profile):** with
    `[engine] probe_search = true`:
    - the probe files are searched briefly against every species database the profile has, with variable phospho and
      GG;
    - each run's species, cysteine-peptide share and phospho/GG shares are measured and kept in the experiment record;
    - a run of another species parks `waiting_species`; a run mixing species parks `waiting_mixed_species`; a deposit
      expected to be carbamidomethylated but with almost no cysteine peptides parks `waiting_alkylation_check`. Each
      park is announced;
    - a measured enrichment is recorded beside the screen's call.
  - **Every deposit gets an experiment record, and every park that needs a person is announced.**
    - At the probe stage each deposit gets `01_experiment/experiment.json` and `experiment_notes.md`: every fact with
      its source and evidence, guesses marked, each probe run's measured fragmentation and reporter ions.
    - A park that needs a person's decision is listed once in the run root's `decisions_needed.tsv`, with the
      evidence and the choices: an unknown protease, modification or alkylant; several proteases; mixed labelling;
      too many files for a whole search; unsupported fragmentation.
  - **An unmodelled thiol reagent parks the deposit.** NBF-Cl named in a protocol, with no modelled alkylant beside it,
    now waits as `waiting_unknown_alkylant` instead of being searched as carbamidomethyl.
  - **MetaMorpheus's contaminant panel without the UPS spike-in standard**, used as it is
    (`MetaMorpheusContaminants_NoUPS.xml`, 216 entries; MetaMorpheus #2858). It ships under `profiles/lists/` because
    the MetaMorpheus 1.1.12 install does not have it yet. All 48 UPS proteins are out, including the 7 human ones
    (albumin, transferrins, lactalbumin, lysozyme, haemoglobins) that the older `contaminant_panel_exclude_v1.tsv`
    kept. In mouse and rat searches, fewer native proteins are therefore labelled contaminant. A profile now names a
    shipped panel with `[contaminants] file = "..."`, or MetaMorpheus's own with `panel = "..."`.

  Known result changes from 1.1.11 to 1.1.12, so do not pool or diff across the boundary:
  - protein N-terminal occupancy now counts Met-removed forms (mzLib #1337);
  - PEP is trained iteratively (MetaMorpheus #2844);
  - four modifications gain Unimod accessions;
  - PEP is faster (#2841).
- **PTM databases are rebuilt for every new pipeline build** (`tools/ptm-db/BuildPtmDb.cs`):
  - from a copy of the live catalog, never the file a publish may be writing;
  - **a site is never added from a deposit that enriches that PTM**, whether the enrichment is declared or measured
    (any run with at least 5% phospho or 2% GG among its 1% target PSMs);
  - the builder now reads the catalog with DuckDB 1.5.5, the version dataRepo writes with.
- **Per-deposit exceptions, as question data** (`[exceptions.PXD…]`):
  - a deposit can have its own `max_files`, `max_file_mb`, `max_est_ms2` and `defer_if_median_file_mb`;
  - it can choose its own runs with `include_files` / `exclude_files` (case-insensitive regular expressions on raw file
    names);
  - a reason is required;
  - a deposit deferred earlier gets one more look when an exception is added;
  - every use is logged, kept in fetch provenance (`subset_by_question`) and noted in the manifest
    (`question_exception`).
- **A search fails when MetaMorpheus ignores its design.** MetaMorpheus can drop an invalid design after one warning
  and quantify without it. The protein table must now carry one sample column per design sample.
- **New flags:**
  - `cysteine_poor`: under 5% of 1% target peptides contain cysteine (whole proteomes run 12-26%), which usually means
    the lab used an alkylant the search did not. Written to the manifest for non-enriched deposits only;
  - `output_sdrf_failed`: MetaMorpheus's SDRF writer crashed; the results stand.
- **Chemistry:**
  - NEM named as the only alkylant, even in a lysis buffer, is searched as variable NEM on C;
  - MMTS blocking is read as Methylthio;
  - two resolutions of one UNIMOD id must agree on mass, or the deposit waits (`waiting_unknown_modification`).
- **Fetch (mzLib 1.0.594: retry and resume inside PRIDE's client):**
  - an empty FTP listing, which mzLib now throws, is retried as before;
  - SDRF downloads are retried and size-checked like raw files;
  - an SDRF present only in PRIDE's FTP tree is flagged `sdrf_ftp_only`;
  - provenance says why `pride_checksum` is empty (PRIDE's REST list never fills it).
- **Census:** a `factor value[biological replicate]` column is no longer read as an experimental factor.
- **Manifest:**
  - `no_design_file` and `no_output_sdrf` now come from what the search did;
  - a re-search keeps every key it does not write (an owner's `excluded_runs`, `restrictions`, ...).
- **QC payload:** a file left out of the search is listed with `"excluded": true` (qctemplates 0.2.0).
- **`pxreprise batch research <question.toml> <PXD> --reason "..." [--profile key@n]`** searches a finished deposit again under another
  profile. The earlier output is moved aside, and the manifest entry is replaced once, keeping what its owner wrote.

## 0.3.11 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. The existing profiles search exactly as before; the new chemistry handling is a new profile a question opts into.

- **A new profile, `label-free-dda@3`, searches each deposit with its own chemistry.** See
  [Concepts](https://smith-chem-wisc.github.io/PXReprise/concepts.html).
  - **What it reads, before any download:** the protease, the cysteine alkylation and any label, from the question's
    SDRF, the deposit's SDRF, PRIDE's identified modifications and quantification method, and the protocol text.
  - **What it searches with:**
    - another protease than trypsin;
    - a different protease per raw file, through MetaMorpheus's per-file settings;
    - another alkylant than iodoacetamide;
    - a light/heavy pair such as d0/d5-NEM, as two variable modifications.
  - **Where names come from:** protease names and modification names come from mzLib, so they match what MetaMorpheus
    reads.
  - **What it records:** each choice and its source go into the search's `provenance.json`. A choice read only from
    protocol text is flagged `chemistry_guessed` in the manifest.
  - **When a deposit waits:** if it cannot be searched as read (`waiting_multi_protease`, `waiting_unknown_protease`,
    `waiting_unknown_modification`, `waiting_<label>_labelling`).
  - Otherwise `@3` is `@1`.
- **A search fails when MetaMorpheus ignores a setting.** MetaMorpheus only warns about a modification it does not know
  ("Unrecognized mod") or a per-file settings file it cannot read, then searches on without it. Either now fails the
  search. No search made so far logged either warning.
- **Top-down and DiLeu deposits wait** (`waiting_top_down`, `waiting_dileu_labelling`) instead of being searched as
  label-free bottom-up.
  - Top-down is read from the title, description and protocols only, because PRIDE's "Top-down proteomics" keyword is
    often set on bottom-up deposits.
  - DiLeu is never sent to the isobaric profile, which searches TMT masses.
- **Two new kinds of discovery keyword in a question's `[discover]`.** See
  [Writing a question](https://smith-chem-wisc.github.io/PXReprise/questions.html).
  - `disease_keywords`: a deposit found only by these is queued only if its PRIDE text names a reference group.
  - `watch_keywords`: deposits are found and screened, never queued.
  - The census writes `watch.tsv` and counts how many deposits each keyword would queue.
- **`pxreprise rank`** orders a census's deposits biggest first, within a file-count and size limit, from PRIDE file
  listings; no spectra are downloaded.
- **`pxreprise chemistry`** reports each deposit's protease, alkylation and label with their sources, and where they
  differ from MetaMorpheus's defaults.

## 0.3.10 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no search result changes.

- **Immunopeptidomes and peptidomes are no longer searched as tryptic.** Their peptides are not made by a protease, so
  a tryptic search finds almost nothing (two aging deposits: an identification rate of about 0.0015).
  - **What the screen reads:** the PRIDE record's text (immunopeptidome, ligandome, peptidomics, MHC or HLA peptides or
    ligands, an unspecified or absent enzyme) and its project tags (hupo-hipp).
  - **Where such a deposit goes:** it waits as `waiting_nonspecific_cleavage` until a profile that searches it exists.
    A profile opts in with `nonspecific_cleavage = true` under `[accepts]`; none does yet.
- **QC-excluded raw files are recorded for dataRepo again.** The search's `provenance.json` carries `excluded_files`
  (`files` and `reason`), which dataRepo reads to leave a blank or failed injection out of the deposit's runs. Since
  the move to C#, such a file was ingested as a run.
- **dataRepo 1.0.0's JSON result is read.** At batch start PXReprise runs `datarepo --version`. From 1.0.0 on it calls
  `ingest --json`, and a deposit counts as delivered only when the exit code is 0 and its status is `ingested` or
  `unchanged`. The state records the status, bundle id and dataRepo's reasons. An older dataRepo is judged by its exit
  code, as before.
- **`pxreprise validate` checks `[designs]`.** It refuses a `dir` that does not exist and lists the deposits it holds
  a design for.

## 0.3.9 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no search result changes.

- **Each raw file's acquisition start time and instrument are recorded.** `qc_report.json` gains `start_time`,
  `instrument_model`, `instrument_model_accession` and `instrument_serial` for each file. These facts exist only in
  the raw file, which is deleted after the search.
  - Each key is written only when the reader gives the value, and is never an empty string.
  - A Thermo RAW start time is the instrument's local clock with no time zone. It is written as read, never converted.
  - Reading these values never fails a file's QC.
- The release notes now keep nested list items on their own lines.

## 0.3.8 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no result changes.

- **A batch survives a reboot or a crash.** Run the same `batch run` again and it finishes what was cut off:
  - **A search cut off part-way is searched again.** Before, its half-written output made the next start mark it
    `search_failed` and never try again. The partial output is moved aside, and the deposit is `search_interrupted`.
    A fourth cut-off on one deposit settles it.
  - **A delivery cut off part-way is delivered again.** Before, an ingest that never finished looked delivered.
  - **A stale `driver.pid` no longer blocks the start.** After a reboot its number can belong to another program;
    the driver's start time now tells them apart.
- **`driver.last.json`** in the state directory records the running batch's command line, working directory, pid and
  start time, so a script or scheduled task can start the batch again after a reboot.
- **`batch run` no longer deletes the STOP file of a batch that is still running.** A second start is refused, as
  before, but it used to remove STOP first.
- **The manifest is never seen half-written.** Each new entry is written to a file beside it, which then replaces it.

## 0.3.7 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no result changes.

- **A duplicate of the deposit being searched is now caught.** The batch fetches the next deposit while the current one
  searches, and the duplicate check compared only with deposits whose search had finished. PXD042302 (PXD042301
  deposited again: same title, same paper, 30 of 31 raw files byte-identical) was checked 7 seconds after PXD042301's
  search began, and was fetched and searched in full. The deposit being searched now counts too, and the log says
  `..., being searched now`.
- **Possibly mixed samples are flagged.** When every raw file's name says what kind of sample it is (`Lysate_`, `IP_`,
  `EV_`, `phospho`, ...) and there is more than one kind, the manifest entry gets the flag `possibly_mixed_enrichment`
  and a note, and `batch.log` says `POSSIBLY MIXED SAMPLES`. Which run is which enrichment stays yours to curate; the
  flag does not change the ingest. On the 96 searched aging deposits it flags one, PXD077298 (lysate, IP and
  extracellular vesicles).
- **A state entry's `detail` belongs to its status.** When a later status replaces it (a download that failed once,
  then succeeded), the old text moves to `earlier_detail`. Before, a searched deposit could still show the error of an
  earlier pass.
- **Experimental designs (not used by any shipped profile yet).** A profile can set `[quant] design = "sdrf"`. Before
  the search, PXReprise writes MetaMorpheus's `ExperimentalDesign.tsv` from the question's own SDRF for the deposit
  (`[designs]` in `question.toml`), or else from the deposit's, after mzLib checks it the way MetaMorpheus reads it. A
  question's design that fails the check stops that deposit's search, with the reasons. `pxreprise search` takes
  `--design` and `--condition-columns`. See [Writing a question](https://smith-chem-wisc.github.io/PXReprise/questions.html).
- Every stage's `provenance.json` now says `"definitions": "pxreprise"`, so dataRepo can cite PXReprise's own
  definitions of its numbers (`DEFINITIONS.md`). dataRepo 0.32.0 ignores the field.

## 0.3.6 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: new optional `fetch_stall_minutes`. Safe mid-batch: stop
the batch, update, run it again.

From a first-run report on a slow, unreliable link (one 4.5 GB deposit over two days):

- **A stalled download can no longer hang the batch.** Two downloads sat on frozen `.partial` files for 6 hours with no
  error and no log line. The batch now watches each download's file. When it receives no data for
  `fetch_stall_minutes` (default 5), the download is abandoned and retried, even if the network read does not respond
  to cancellation. It counts as a dropped transfer: retried, then left for a later pass.
- **A file is complete only at PRIDE's listed size.** A file already in `spectra/` at the wrong size (cut short by a
  crash, or downloaded by hand) is deleted and downloaded again, and a download that arrives short is retried. Before,
  any file present by name was taken as complete. Across 2,222 aging downloads, PRIDE's size and the downloaded size
  never differed.
- **Downloads are logged as they happen:** `FETCH 7 of 15 done: <file> 305 MB in 212 s`, and each retry with its error
  type, for example `FETCH retry <file> attempt 2 of 8: TimeoutException: ...`. Before, a fetch was silent for hours.
- **Failures name the exception type** in `batch.log` and in the state entry's `detail`.
- **New `pxreprise batch retry <question.toml> <PXD> --reason "..."`** puts a settled deposit back in the queue, so
  undoing a `fetch_failed` no longer means editing `state.json`. It keeps the old status, time, user and reason, and
  refuses while a batch is running.
- The TLS drop in that report ("Received an unexpected EOF or 0 bytes from the transport stream") was already fixed in
  0.3.3. That run used 0.3.1.

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
