# Operating an instance

This page is for whoever runs a dataRepo **instance**: the collection of reanalysed datasets one
project serves, with its catalog, its site and its agent server. dataRepo ships the software and
runs nothing itself (decision D27); the **operator** runs every step below. Today that is the
`aging` project, and [PXReprise](https://github.com/smith-chem-wisc/PXReprise) is making the same
steps available to anyone.

If you have not run dataRepo before, do [getting-started.md](getting-started.md) first. It runs this
whole loop on a small example in ten minutes.

## The loop

```
  your pipeline                 dataRepo (you run it)                       readers
  ─────────────                 ─────────────────────                       ───────
  search a PRIDE deposit
  (MetaMorpheus)  ──────►  manifest.yaml says: include
                           datarepo ingest   ──► bundle per dataset
                           datarepo run      ──► engine outputs (optional)
                           datarepo study    ──► study bundles (optional)
                           datarepo publish  ──► catalog.duckdb + site/ ───► people: the site
                                                        │
                                                        └── datarepo mcp ─► agents
```

Each step is idempotent. Re-running it on unchanged inputs does nothing and says so, so a pipeline
can call every step after every dataset without keeping track of what it already did.

## 1. Install one released version

Run every step from **one released version, unpacked once and never edited**. Each
[release](https://github.com/smith-chem-wisc/dataRepo/releases) carries one archive per platform,
each holding a `datarepo/` folder with the program, `datarepo` (`datarepo.exe` on Windows), and
everything it needs: the .NET runtime, mzLib, DuckDB and the Parquet writer. Nothing else is
installed, and no .NET or Python is needed.

| Platform | Archive |
|---|---|
| Windows x64 | `datarepo-<version>-win-x64.zip` |
| Linux x64 | `datarepo-<version>-linux-x64.tar.gz` |
| macOS, Apple silicon | `datarepo-<version>-osx-arm64.tar.gz` |
| macOS, Intel | `datarepo-<version>-osx-x64.tar.gz` |

```bash
mkdir -p /opt/datarepo-1.1.0 && cd /opt/datarepo-1.1.0
curl -LO https://github.com/smith-chem-wisc/dataRepo/releases/download/v1.1.0/datarepo-1.1.0-linux-x64.tar.gz
tar -xzf datarepo-1.1.0-linux-x64.tar.gz          # tar keeps the executable bit; a zip would lose it
/opt/datarepo-1.1.0/datarepo/datarepo doctor
```

Call the program by its path, put its folder on `PATH`, or point a tool at it (PXReprise's machine
file: `datarepo = "…"`). On macOS, an archive downloaded with a browser is quarantined and the
program refused as unverified: download with `curl`, or run `xattr -dr com.apple.quarantine` on the
unpacked folder. The commands, arguments and outputs are the ones in [cli.md](cli.md).

Three things depend on running a release:

- **A bundle id hashes the ingest path's version** (`cs-1.1.0` in 1.1.0). A bundle written by code
  nobody released cannot be reproduced by anyone, so its id means nothing.
- **`datarepo run` refuses a development build.** It records the exact build behind every engine
  output: the version and the commit the release was built from. A build that reports `0.0.0-dev`,
  or carries no commit, has no fixed identity to record.
- **Each download was verified by running it.** The `dotnet-binaries` workflow publishes each
  platform's program, runs the example instance end to end with no .NET on the machine's `PATH`,
  checks that it reports the tagged version, and only then attaches the archive to a draft release,
  which a person publishes.

Build the catalog and the site with **the same version** that ingested. The program's version is
part of the catalog id, and the site's footer names it.

**Moving from the Python package (0.32.0 and earlier).** The C# program writes the same tables, with
the changes its [changelog](../CHANGELOG.md) lists (among them, `peptidoforms.is_unique` now means
one gene in the searched sequences: see [limitations.md](limitations.md#6--unique-means-one-gene-in-the-searched-sequences)).
Its first ingest path (`cs-1.0.0`) and schema 0.0.14 give every dataset a new bundle id, so
re-ingest each dataset once with 1.0.0. 1.1.0 moves the schema to 0.0.15 (each run's start time and instrument), so a store written by 1.0.0 is re-ingested once more; an instance still on the Python package should go straight to 1.1.0. `build` refuses a bundle written against schema 0.0.13 and
names it. A Python install is no longer needed for anything.

### Building from source

To build the program yourself you need the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
git clone https://github.com/smith-chem-wisc/dataRepo.git
dotnet build dataRepo/dotnet/src/DataRepo.Cli -c Release -o datarepo-dev
datarepo-dev/datarepo --version                    # datarepo 0.0.0-dev
```

A source build reports version `0.0.0-dev`, and `datarepo run` refuses it by design. It is for
development and testing; serve an instance from a release. A self-contained folder like a release's
comes from `dotnet publish dataRepo/dotnet/src/DataRepo.Cli -c Release -r <rid> --self-contained -o
<dir>` (`<rid>` is `win-x64`, `linux-x64`, `osx-arm64` or `osx-x64`); a release build also passes
`-p:Version=<version>` and `-p:SourceRevisionId=<commit>`, which is what
`.github/workflows/dotnet-binaries.yml` does.

## 2. Describe your instance: `manifest.yaml`

The manifest is the contract. It lists every dataset you have searched, says which may be loaded,
and gives each one's facts. dataRepo never scans your work folder and never decides for itself
whether a run is fit to serve.

```yaml
manifest_version: 1
instance: my-instance
work_root: /data/runs            # where your pipeline writes; relative paths resolve from this file
store: /data/repo/store          # where bundles go
licence: CC-BY-4.0
credit: My Working Group

datasets:
  - accession: PXD036557
    status: include              # include | hold | exclude
    run: run_2026-09-18/PXD036557
    stages: {qc: 02b_qc, search: 04_search}
    search_results: 04_search/mm/Task3SearchTask
    files: 18
    organism: NCBITaxon:9606
    acquisition: DDA
    quant_method: label-free
    metamorpheus: "1.1.11"
  - accession: PXD048658
    status: exclude
    reason: TMT data whose SDRF says label-free; its quant is invalid.
```

`hold` and `exclude` are refused, with your `reason`, whenever someone tries to load them. Every
field is described in [ingest.md](ingest.md#the-manifest-is-the-contract).

**Your pipeline's `provenance.json`.** Each search stage writes one. The ingester reads its
`schema` field first and accepts only schemas whose field meanings it knows:

| `schema` | Read as |
|---|---|
| `aging-provenance/2` | the older layout, in which `id_rate.psms_1pct` holds the FDR engine's count |
| `aging-provenance/3` | the current layout, with the two PSM counts in separately named fields |
| `pxreprise-provenance/1` | the same layout as `aging-provenance/3`, under PXReprise's neutral name |

Any other schema is refused. A count read under the wrong field meaning is a wrong number that
carries a right-looking definition id, so the ingester does not guess.

## 3. Ingest each dataset

```bash
datarepo ingest manifest.yaml PXD036557           # one dataset
datarepo ingest manifest.yaml                     # every 'include' dataset
```

Pass `--mm-settings <MetaMorpheus install>` so modification names resolve against the release that
searched. Read the output: `WARN` lines and open findings are facts about the dataset, and they
appear on its page. A pipeline can pass `--json` instead and read one JSON object on standard output
(each dataset's status, bundle id, row counts, checks and open warnings) and the exit code; the
human report then goes to standard error. See [cli.md](cli.md#datarepo-ingest).

Run `datarepo manifest manifest.yaml` after editing the manifest. Besides listing the datasets, it
checks every `run_enrichment` map the way `ingest` would, against the run folder where it can read
it, and exits `1` on a map `ingest` would refuse, so a wrong map is found before a re-ingest.

Runs that were searched but must be left out of **analysis** (a failed QC run, a blank) go in a
dataset's `excluded_runs`, one reason per run, keyed by the raw file name without its extension:

```yaml
    excluded_runs:
      20170317_VM_17: failed spectra QC (see DECISIONS.md D67)
```

The runs stay in the bundle, and the field is not identity, so changing it needs no re-ingest: the
next `build` writes it to the catalog's `run_exclusions` table and moves the catalog id. `ingest`
does not read it. `datarepo manifest` checks every name against the dataset's runs and exits `1` on
one that is not a run, and `build` refuses such a name, so a misspelt exclusion never leaves the run
it meant looking fit for analysis.

The **discovery census** (every accession you screened, with its verdict) goes in a tab-separated file
that the manifest names at the top level, relative to the manifest's folder or absolute:

```yaml
candidates: candidates.tsv
```

```text
census_version	accession	included	exclusion_reason	definition_id
2026-10-05	PXD036557	true		DEF-AGING-SCREEN
2026-10-05	PXD000002	false	spectra gate: not HCD Orbitrap	DEF-AGING-SCREEN
```

The header row is required and names the schema's columns. `census_version`, `accession` and
`included` are required, and every row must fill them. `exclusion_reason` and `definition_id` may be
left out of the header, and an empty cell is NULL. `included` is `true` or `false`. Cells are read
verbatim (no quoting, no trimming), and an accession listed twice is refused. Like `excluded_runs` it
is not identity: `ingest` never reads it, and the next `build` loads it into `dataset_candidates` and
hashes the file into the catalog id. `build` names any included accession it holds no dataset for,
and any dataset the census does not include, in `catalog_checks` (kind `census`) without failing.
`datarepo manifest` reads the file the way `build` does and exits `1` on a malformed one.

## 4. Optional: engines and study results

- **`datarepo run`** runs a released engine on stored data and writes its output beside the
  bundles, never into one. Today that engine is logs' gene resolution. See
  [the charter](../design/CHARTER.md#the-runner-datarepo-ships-it-built-in-datarepo-0200).
- **`datarepo study`** delivers a study layer's rows, such as age effects, sample ages, or curated
  tissue and cell type. They go into a separately hashed study bundle, so delivering a model result
  never re-identifies a search bundle. See [study.md](study.md).

## 5. Publish

```bash
datarepo publish manifest.yaml --site /data/repo/site \
    --title "My proteomics repository" \
    --purpose "how organelle proteomes change with age" \
    --about about.md \
    --base-url https://example.org/repo/ \
    --study-latest aging
```

`publish` builds the catalog from **every `include` dataset that has a bundle**, naming each one
that has none, and then writes the site from that catalog. When nothing changed, the catalog is kept
and the site comes out byte-identical. Every flag is in [cli.md](cli.md#datarepo-publish).

| Flag | Use it to |
|---|---|
| `--purpose TEXT` | state the question your instance serves. It is shown in the tagline, the overview, `llms.txt` and the structured data. Without it no page claims a purpose |
| `--about FILE` | give the front page a Markdown overview of your project |
| `--keyword WORD` | add a schema.org keyword to every dataset page (repeatable) |
| `--study-latest LAYER` | load that study layer's newest delivery. Study results are never loaded unless you name them |
| `--base-url URL` | set where the site will be served. Enables the sitemap and absolute links |
| `--data-url URL` | set where the bundle store is served. Enables download links and `croissant.json` |
| `--notice TEXT` | put a banner on every page, e.g. "preview: data will be regenerated" |

`publish` does **not** run engines, does not load a study delivery you did not name, and does not
upload anything. Putting the site on the web is your step, because it is outward-facing.

### Hosting the site on GitHub Pages

Make `--site` a clone of a `gh-pages` branch. The generator deletes only the files its own
`.datarepo-site.json` marker lists, so `.git` and `.nojekyll` survive a regeneration.

```bash
git clone --branch gh-pages https://github.com/<you>/<repo>.git /data/repo/site
datarepo publish manifest.yaml --site /data/repo/site --base-url https://<you>.github.io/<repo>/
git -C /data/repo/site add -A        # right here, and only here: the clone holds nothing but output
git -C /data/repo/site commit -m "Regenerate from catalog <id>"
git -C /data/repo/site push
```

**Read the generated front page before you push.** It is the check that catches a number rendered
wrong.

### Serving the catalog to agents

```bash
datarepo mcp --catalog /data/repo/catalog.duckdb --install
```

A running MCP server keeps the catalog it opened. After a rebuild it serves the **previous** one
until it is reconnected, and every answer's provenance then carries `catalog_file_changed`. In Claude
Code, run `/mcp`, select the server, and reconnect.

## When a dataRepo upgrade needs a re-ingest

The changelog for each release says which of these it changes. Only the first two cost a re-ingest.
1.0.0 changes both, so moving to it from the Python package is a full re-ingest.

| What changed | Effect on your instance |
|---|---|
| core schema version | **re-ingest every dataset.** `build` refuses a bundle written against another schema version, and names it |
| the ingest path (`cs-1.0.0`, `BundleWriter.IngesterVersion`) | **re-ingest to pick up the change.** Old bundles still load, so a catalog can mix them, but a fixed defect stays in the old ones |
| a study layer's version | re-deliver that layer; a stored delivery of another layer version is refused |
| the catalog version, or the program only | rebuild the catalog (`publish` does it); no re-ingest |

## Releases

A release is a catalog built from **pinned** bundles, so it can be rebuilt exactly:

```bash
datarepo build manifest.yaml --release v0.1 --bundle PXD036557=<id> --bundle PXD027318=<id> …
```

`--release` refuses `--latest` and `--study-latest`: a release that could pick up a later bundle is
not a release. A DOI for each release is the operator's to mint (Zenodo is the recommendation).
