# `datarepo ingest`

Turns one dataset's pipeline output into one immutable Parquet bundle that conforms to
[`schema/datarepo.yaml`](../schema/datarepo.yaml).

dataRepo stores and serves. The ingester re-runs nothing, recomputes nothing, and decides nothing the
producer has already decided.

```
producer's work root                          bundle store
  <run>/<PXD>/02_fetch/…                        <PXD>/<bundle-id>/
  <run>/<PXD>/02b_qc/…          ingest            *.parquet   (one file per table)
  <run>/<PXD>/04_search/…        ------>          sources/    (the producer's own files, copied)
  instance/manifest.yaml                          bundle.json (what was read, what came out)
```

---

## Install

```bash
python -m venv .venv
# Windows: .venv\Scripts\activate    Linux/macOS: source .venv/bin/activate
pip install -e .
pip install mzlib              # pyMzLib: parses the producer's .psmtsv files
datarepo doctor                # says whether this machine can ingest
```

`doctor` is the first thing to run on a new machine:

```
datarepo 0.1.0  schema 0.0.1
  pyarrow          25.0.1
  duckdb           1.5.5
  pymzlib          0.1.1
  mzLib bridge     …/site-packages/pymzlib/_dotnet/win-x64/mzlib-bridge.exe
ready
```

pyMzLib's wheels are per-platform and carry the mzLib bridge inside them, so on a released version
nothing has to be built and `PYMZLIB_BRIDGE` is not needed. It is needed only against a source
checkout, which ships no bridge; `doctor` says so, and points at the path it looked in. There is no
in-house fallback for `.psmtsv`, by design: parsing producer file formats belongs to pyMzLib.

## Run it

```bash
datarepo manifest  /path/to/instance/manifest.yaml            # what the instance offers
datarepo ingest    /path/to/instance/manifest.yaml PXD036557  # build one bundle
datarepo ingest    /path/to/instance/manifest.yaml            # build every 'include' dataset
datarepo inspect   /path/to/store/PXD036557/<bundle-id>       # what a bundle holds
```

| Option | Effect |
|---|---|
| `--store DIR` | Write somewhere other than the manifest's store. |
| `--mm-settings DIR` | Read modification definitions from a specific MetaMorpheus install. |
| `--overwrite` | Rebuild a bundle that already exists at the same content hash. |
| `-v` | Print every reconciliation check, not only the ones that disagree. |

Exit codes: `0` success (including "already built, nothing to do"), `1` a refusal or a failure to act
on, `2` bad usage. Re-running an unchanged dataset is a no-op, so the pipeline can call it after
every stage.

## The manifest is the contract

`datarepo ingest` reads the producing instance's `manifest.yaml` and does **not** scan the work root
(aging thread 006). Which run is canonical for a dataset, and whether it may be loaded at all, is the
producer's decision, recorded in their repository.

```yaml
manifest_version: 1
instance: aging
work_root: F:/aging_data          # relative paths resolve against the manifest's own folder
store: F:/aging_data/repo/store
licence: CC-BY-4.0
credit: NCEMS Aging Proteome Working Group

datasets:
  - accession: PXD036557
    status: include               # include | hold | exclude
    run: run_2026-09-18/PXD036557_n18
    stages: {qc: 02b_qc, search: 04_search_mm1111}
    search_results: 04_search_mm1111/mm/Task3SearchTask
    files: 18
    organism: NCBITaxon:9606      # the D5 scope axes, as real columns
    acquisition: DDA
    quant_method: label-free
    metamorpheus: "1.1.11"
    flags: [low_id_rate, no_design_file, no_output_sdrf]
```

Only `include` is ingested. `hold` and `exclude` are **refused with the producer's own reason**:

```
$ datarepo ingest manifest.yaml PXD048658
PXD048658
  refused  PXD048658 has status 'exclude' … The producer's reason: TMT data whose community SDRF
           says label-free (S1). It was searched as label-free, so its quant is invalid …
```

That refusal is the point. The producer has already judged the run unfit, and loading it would put
invalid quantities into the repository under a status that says not to.

## What it reads, and who parses it

| File | Parsed by | Becomes |
|---|---|---|
| `provenance.json` (search + its declared upstream stages) | dataRepo | `ProvenanceRecord`, `Metric`, `Finding` |
| `fetch_manifest.json` | dataRepo | `Run` (file names, SHA-256, archive checksum) |
| `qc_report.json` | dataRepo | `Run` (MS2 count, length, dissociation, QC verdict), `Metric` |
| `*.sdrf.tsv` | **pyMzLib** | `Sample`, `SampleCharacteristic`, `Assay` |
| `AllPSMs.psmtsv`, `AllPeptides.psmtsv` | **pyMzLib** | `Psm`, `Peptidoform`, `Protein`, `PtmSite` |
| `AllQuantifiedPeptides.tsv`, `AllQuantifiedProteinGroups.tsv`, `AllQuantifiedPeaks.tsv` | dataRepo | `QuantValue`, `ProteinGroup` |
| the executed task `.toml` files | dataRepo | `SearchModification` |
| `results.txt` | dataRepo | `Metric`, and the numbers the ingest reconciles against |
| the searching MetaMorpheus install's `Mods/`, `Data/ptmlist.txt` | dataRepo | UNIMOD accessions for ProForma |

pyMzLib owns producer file formats. Where dataRepo reads one itself it is because pyMzLib 0.1.x
cannot, and `bundle.json`'s `readers` block records the reason for every such file, so the in-house
code is deletable rather than permanent. See the table in
[`src/datarepo/readers.py`](../src/datarepo/readers.py).

That deletion has now happened once. dataRepo read `*.sdrf.tsv` itself because pyMzLib's *generic*
projection joined header and cells with `;`, which SDRF values contain themselves, so the columns
could not be recovered (DATAREPO-13). pyMzLib 0.1.1 answers that with a dedicated `pymzlib.sdrf`
module; on the real PXD036557 file it agrees with the deleted code cell for cell, including all 144
cells containing a `;`. Note that the generic `read_records()` path is unchanged and still lossy on
an SDRF, so `readers.read_sdrf` calls `pymzlib.sdrf.read` specifically.

## What it writes

A bundle directory named by a **content hash** of the inputs, the schema version and the ingester
version. Unchanged inputs and unchanged code give the same directory; any change gives a new one,
which is what makes a released bundle safe to cite.

```
store/PXD036557/6fea2187b2d9f737/
  datasets.parquet  samples.parquet  sample_characteristics.parquet  runs.parquet  assays.parquet
  psms.parquet  peptidoforms.parquet  protein_groups.parquet  proteins.parquet  ptm_sites.parquet
  quant_values.parquet  search_modifications.parquet  metrics.parquet  definitions.parquet
  provenance_records.parquet  findings.parquet
  sources/     the producer's provenance, QC report, SDRF, task files and results.txt, copied
  bundle.json  inputs with hashes, row counts, reader log, reconciliation, modification registry
```

Column names, order and types come from the LinkML schema: `tools/build_tables.py` generates
`src/datarepo/_tables.py` from it, and CI fails if that file is stale. There is no second, drifting
copy of the column list.

### Rules the writer enforces

- **Missing is missing.** A `NotDetected` cell or a zero intensity produces **no row**. A zero
  q-value is kept, because there zero is a real measurement.
- **Every number carries a `definition_id`**, which is how intensity and spectral count share one
  `value` column unambiguously. Definitions dataRepo does not own are copied from their register;
  where no owner has published one yet the ID is prefixed `PROVISIONAL:` and says so in its text.
- **Decoys and above-threshold matches are kept**, so a caller can recompute FDR. Filtering is a
  question the caller asks, not a decision taken for them.
- **Ambiguity is carried, not resolved.** MetaMorpheus separates alternatives with `|`; the first
  becomes the stored peptidoform and `ambiguity_level` says what it is. PTM sites are derived only
  from unambiguous, target, below-threshold evidence.
- **References must resolve.** Before anything is written, every foreign key and every
  `QuantValue.feature_id` is checked against the table it names. A dangling reference stops the
  write — it is an ingester bug, not a property of the data.
- **Identifiers are unique, with one lossless escape.** A producer can write the same row twice:
  MetaMorpheus wrote one protein group three times in PXD027318, byte-identical in every column.
  Rows that share an identifier **and are identical in every column** are collapsed to one, because
  the copies carry nothing the kept row does not; the collapse is recorded in `bundle.json` under
  `collapsed_duplicates` and raised as a `collapsed_duplicate_rows` finding. Rows that share an
  identifier and **differ anywhere** still stop the write: the producer is then saying two
  different things about one thing, and that is not the ingester's to resolve.
  `psms` and `findings` are excluded from the collapse on purpose — a row there is an observation
  and the number of rows is itself a reported number, so a duplicate `psm_id` is an ingester bug to
  fix rather than a duplicate to absorb. `quant_values` is included and matters most: it has no
  identifier of its own, so three identical group rows became three identical quantities per run
  and a caller summing intensities would have read the group as three times as abundant.
- **A per-file number stays per file.** The contaminant intensity share is defined per raw file
  (`QuantProject:DEF-QC-9`) and is written one `Metric` row per run. The producer's median, min and
  max ride along at dataset scope under names that say they are summaries, so nothing forces a
  caller to recompute them and nothing lets a caller mistake one for the measurement. On PXD036557
  the dataset figure is 7.0% and the per-file values run 2.6% to 18.9%, grouped by cell line.

## Reconciliation: how you know the bundle is faithful

An ingester that silently drops a tenth of the PSMs still writes a plausible-looking bundle. So every
bundle recounts itself and compares with what the producer reported.

```
$ datarepo ingest … PXD036557 -v
  ok       psms_target_1pct: 26582
  ok       peptidoforms_target_1pct: 5541
  ok       protein_groups_1pct: 1652
  ok       runs: 18
  ok       ms2_spectra: 266402
```

The counts use the **producer's own acceptance rule** — target, both `q_value` and `q_value_notch`
at or below 1%, **and a notch that actually resolved** — because a count taken under a different rule
would differ for a reason that says nothing about whether the ingest was faithful.

That last clause is the one nobody guesses. A search that cannot settle on one notch writes its
candidates separated by `|`, and the producer does not count such a match even though both its
q-values pass. On PXD036557 it is worth exactly 12 PSMs out of 26,594, and applying it is what turns
the last mismatch into agreement. Every PSM stores the notch verbatim in `Psm.notch` and the
conclusion in `Psm.notch_ambiguous`, so the exclusion can be audited rather than trusted:

```sql
SELECT psm_id, notch FROM psms WHERE notch_ambiguous;
-- PXD036557:QE-002106_GM1_a:42808:2   0.00000|1.00290
```

A mismatch is never fatal and never hidden. It goes into `bundle.json` and becomes a `Finding` on the
dataset, because some mismatches are real and already known, and carrying them with their
explanation is the repository's job.

> **Closed (PXD036557):** the PSM total used to be 12 higher than `results.txt`. aging gave the
> missing clause of `DEF-PSM-1PCT v1` in thread 008 — the unresolved notch — and all five checks now
> agree. The bundle written before that clause existed is still on disk and still citable; it was
> written against schema 0.0.1 and says so.

## USIs

Every PSM gets one:

```
mzspec:PXD036557:QE-002123_GM7_b:scan:40132:KLADQC[UNIMOD:4]TGLQGFLVFHSFGGGTGSGFTSLLMER/3
```

The trap is the run name. A USI must name the file **as deposited in the archive**, but the search
ran on calibrated copies and reports `QE-002123_GM7_b-calib`. The mapping is resolved against the
fetch manifest's own file list, so a run that cannot be matched produces **no USI** rather than one
that fails to resolve, and the dataset gets an `unmatched_runs` finding.

## ProForma and modification accessions

The schema stores peptidoforms as ProForma 2 with UNIMOD accessions. MetaMorpheus writes its own
notation (`C[Common Fixed:Carbamidomethyl on C]`), so the ingester translates. The accession mapping
is **not invented**: it is read from the modification files shipped with the MetaMorpheus build that
did the search (`Mods/*.txt`, `Data/ptmlist.txt`), which pins it to the version in the manifest.

| Registry says | ProForma |
|---|---|
| a UNIMOD accession | `C[UNIMOD:4]` |
| only a monoisotopic mass | `D[+37.946941]` |
| neither | `K[Info:Nameless on K]`, plus an `unresolved_modifications` finding |

This translation is a stop-gap. pyMzLib's `psmtsv` records already have a `pro_forma` field; it is
null for MetaMorpheus files in 0.1.x (DATAREPO-12). When it is populated, this code goes.

## Findings a bundle can carry

| Code | Severity | Meaning |
|---|---|---|
| `low_id_rate` | warning | The search identified an unusually small fraction of MS2 spectra. |
| `no_design_file` | warning | No experimental design, so between-group comparisons are unavailable. |
| `no_output_sdrf` | info | The search wrote no SDRF back out. |
| `sdrf_skeleton` | warning | The deposited SDRF has no biological annotation at all. |
| `no_sdrf` | warning | No SDRF; each run was given a synthetic, unannotated sample. |
| `count_mismatch` | warning | A count disagrees with the producer's summary. |
| `unresolved_modifications` | warning | A modification has neither a UNIMOD accession nor a mass. |
| `unmatched_runs` | warning | A run the search reported is not a deposited file. |
| `collapsed_duplicate_rows` | info | The producer wrote a row more than once, identical in every column; the copies were dropped. |
| `metric_conflict` | warning | One metric reached the bundle from two sources under one definition, and they disagree. |

The producer's own flags arrive as findings too, keeping their original text.

## Not in the ingester yet

Tables the schema defines that no producer fills today: `PtmStoichiometry`, `Glycopeptide`,
`ProteoformInference` (MetaMorpheus, R7/R7b/R16), `ProteinLocalization` and `ProteinAnnotation`
(`go`), `FeatureSet`, `DatasetCandidate` (aging's discovery census, R1), and `Release` /
`ReleaseChange` (the instance owner's, D8). Each arrives when its producer does.

Also absent, deliberately: `Run.acquisition_datetime` and run-level `instrument_model` from the raw
header. aging 006 asks dataRepo **not** to parse raw headers; the instrument comes from the archive's
record via the SDRF, and the date waits for pyMzLib REQ-PYMZ-2.
