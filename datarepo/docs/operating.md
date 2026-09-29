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

## 1. Pin one released install

Run every step from **one released version, installed once and never edited**, for example a venv
built from a tagged commit:

```bash
python -m venv /opt/datarepo-0.30.0
/opt/datarepo-0.30.0/bin/pip install "datarepo[readers,mcp] @ git+https://github.com/trishorts/dataRepo.git@<commit>"
```

Two things depend on this:

- **A bundle id hashes the ingester's version.** Bundles written by a working tree that nobody
  committed cannot be reproduced by anyone, so their ids mean nothing.
- **`datarepo run` refuses an editable install.** It records the exact install behind every engine
  output, and an editable tree has no fixed identity to record.

Build the catalog and the site with **the same install** that ingested. The builder's version is part
of the catalog id, and the site's footer names it.

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
appear on its page. Large results are read in windows (512 MiB of source each), so a 2 GB psmtsv
ingests, slowly.

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

| What changed | Effect on your instance |
|---|---|
| core schema version | **re-ingest every dataset.** `build` refuses a bundle written against another schema version, and names it |
| `INGESTER_VERSION` | **re-ingest to pick up the change.** Old bundles still load, so a catalog can mix them, but a fixed defect stays in the old ones |
| a study layer's version | re-deliver that layer; a stored delivery of another layer version is refused |
| `CATALOG_VERSION`, or the package only | rebuild the catalog (`publish` does it); no re-ingest |

## Releases

A release is a catalog built from **pinned** bundles, so it can be rebuilt exactly:

```bash
datarepo build manifest.yaml --release v0.1 --bundle PXD036557=<id> --bundle PXD027318=<id> …
```

`--release` refuses `--latest` and `--study-latest`: a release that could pick up a later bundle is
not a release. A DOI for each release is the operator's to mint (Zenodo is the recommendation).
