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
pip install pymzlib            # parses the producer's .psmtsv files
datarepo doctor                # says whether this machine can ingest
```

`doctor` is the first thing to run on a new machine:

```
datarepo 0.1.0  schema 0.0.1
  pyarrow          25.0.1
  pymzlib          0.1.1
  mzLib bridge     …/mzlib-bridge.exe
ready
```

If it reports the bridge as unavailable, build it in the pyMzLib checkout and point
`PYMZLIB_BRIDGE` at the executable. There is no in-house fallback for `.psmtsv`, by design:
parsing producer file formats belongs to pyMzLib.

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
| `*.sdrf.tsv` | dataRepo | `Sample`, `SampleCharacteristic`, `Assay` |
| `AllPSMs.psmtsv`, `AllPeptides.psmtsv` | **pyMzLib** | `Psm`, `Peptidoform`, `Protein`, `PtmSite` |
| `AllQuantifiedPeptides.tsv`, `AllQuantifiedProteinGroups.tsv`, `AllQuantifiedPeaks.tsv` | dataRepo | `QuantValue`, `ProteinGroup` |
| the executed task `.toml` files | dataRepo | `SearchModification` |
| `results.txt` | dataRepo | `Metric`, and the numbers the ingest reconciles against |
| the searching MetaMorpheus install's `Mods/`, `Data/ptmlist.txt` | dataRepo | UNIMOD accessions for ProForma |

pyMzLib owns producer file formats. Where dataRepo reads one itself it is because pyMzLib 0.1.x
cannot, and `bundle.json`'s `readers` block records the reason for every such file, so the in-house
code is deletable rather than permanent. See the table in
[`src/datarepo/readers.py`](../src/datarepo/readers.py).

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

## Reconciliation: how you know the bundle is faithful

An ingester that silently drops a tenth of the PSMs still writes a plausible-looking bundle. So every
bundle recounts itself and compares with what the producer reported.

```
$ datarepo ingest … PXD036557 -v
  MISMATCH psms_target_1pct: bundle 26594 vs producer 26582 (results.txt: All target PSMs …)
  ok       peptidoforms_target_1pct: 5541
  ok       protein_groups_1pct: 1652
  ok       runs: 18
  ok       ms2_spectra: 266402
```

The counts use the **producer's own acceptance rule** — target, and both `q_value` and
`q_value_notch` at or below 1% — because a count taken under a different rule would differ for a
reason that says nothing about whether the ingest was faithful.

A mismatch is never fatal and never hidden. It goes into `bundle.json` and becomes a `Finding` on the
dataset, because some mismatches are real and already known, and carrying them with their
explanation is the repository's job.

> **Known residual (PXD036557):** the PSM total is 12 higher than `results.txt`, 0.05%. The peptide
> and protein-group totals match exactly under the same rule. The exact predicate behind aging's
> `DEF-PSM-1PCT v1` is asked as DATAREPO-14.

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

The producer's own flags arrive as findings too, keeping their original text.

## Not in the ingester yet

Tables the schema defines that no producer fills today: `PtmStoichiometry`, `Glycopeptide`,
`ProteoformInference` (MetaMorpheus, R7/R7b/R16), `ProteinLocalization` and `ProteinAnnotation`
(`go`), `FeatureSet`, `DatasetCandidate` (aging's discovery census, R1), and `Release` /
`ReleaseChange` (the instance owner's, D8). Each arrives when its producer does.

Also absent, deliberately: `Run.acquisition_datetime` and run-level `instrument_model` from the raw
header. aging 006 asks dataRepo **not** to parse raw headers; the instrument comes from the archive's
record via the SDRF, and the date waits for pyMzLib REQ-PYMZ-2.
