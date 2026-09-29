# Getting started

In about ten minutes this page takes you from a fresh clone to a working repository: one dataset
ingested, a catalog built, a public site written, and an agent able to ask it questions. It uses the
small example instance that ships in this repository (`tests/data/`), so you need no real data and
no search run of your own.

Every command and every line of output below was run on this example. Output is abbreviated with
`…`, and your ids will differ: they are content hashes, and the version of dataRepo is part of what
they hash.

**Words used here.** An **instance** is one project's collection of reanalysed datasets, described
by a `manifest.yaml`. A **bundle** is one dataset's results, as Parquet tables. A **catalog** is one
DuckDB file built from many bundles. The [glossary](README.md#glossary) has the rest.

## 1. Install

You need Python 3.11 or later and git. (A download that needs no Python is planned; see
[PXReprise's request](../design/threads/PXReprise/).)

```bash
git clone https://github.com/trishorts/dataRepo.git
cd dataRepo
python -m venv .venv
# Windows:      .venv\Scripts\activate
# Linux/macOS:  source .venv/bin/activate
pip install ".[readers,mcp]"
datarepo doctor
```

`readers` adds pyMzLib (the `mzlib` package), which parses MetaMorpheus output; `ingest` needs it.
`mcp` adds the server agents talk to. `doctor` checks both:

```
datarepo 0.30.0
  pyarrow          25.0.1
  duckdb           1.5.5
  pymzlib          0.2.0
  mzLib bridge     …/site-packages/pymzlib/_dotnet/win-x64/mzlib-bridge.exe
  mcp SDK          installed
…
ready
```

If it does not say `ready`, it says what is missing.

## 2. Copy the example instance

The example is a miniature producing instance: a manifest, and the work folder a pipeline would
have written. Copy it out, so nothing you make lands inside the repository:

```bash
cp -r tests/data ../example-instance        # PowerShell: Copy-Item -Recurse tests\data ..\example-instance
cd ../example-instance
datarepo manifest manifest.yaml
```

```
store      …/example-instance/store
licence    CC-BY-4.0 - Test Working Group

 + PXD999999    include  NCBITaxon:9606 DDA label-free
     run    run_test/PXD999999  (2 files, MetaMorpheus 1.1.11)
     flags  low_id_rate, no_design_file
 - PXD000000    exclude
     reason A fixture for the refusal path: the producer judged this run unfit, …
 - PXD111111    hold
     reason Not yet reviewed by the producer.
```

The manifest is the contract between whoever ran the searches and dataRepo. It says which datasets
may be loaded (`include`) and which may not, and why. dataRepo never decides that itself.

## 3. Ingest a dataset

```bash
datarepo ingest manifest.yaml PXD999999
```

```
PXD999999
  bundle   …/store/PXD999999/3b56a967d6968d39
  tables   datasets 1, samples 2, sample_characteristics 8, runs 2, assays 2, psms 60, peptidoforms 40,
           protein_groups 5, proteins 62, ptm_sites 36, ptm_stoichiometry 4, quant_values 37, …
  WARN     3 unresolved modification(s): UniProt:N-acetylalanine on A, …
  findings 5 open warning(s): low_id_rate, no_design_file, occupancy_not_stored, sdrf_skeleton,
           unresolved_modifications
```

Three things happened:

- **The search output became tables.** Every PSM, peptidoform, protein group, PTM site and
  quantity now has a row, and each PSM carries a Universal Spectrum Identifier pointing at its
  spectrum in PRIDE.
- **The counts were reconciled.** The ingester recounted what it wrote and compared it with the
  search engine's own totals. A disagreement is printed and recorded, never smoothed over.
- **What is wrong was written down.** The five *findings* are warnings about this dataset (an
  unusually low identification rate, no experimental design, a sample sheet with no biology in it).
  They travel with the data, so nobody reads a count without its caveats.

The bundle's name is a hash of everything that went into it. Ingest the same files with the same
dataRepo again and nothing happens. Change a byte and you get a second bundle beside the first,
so a result someone cited never changes under them.

Now try the dataset the producer excluded:

```bash
datarepo ingest manifest.yaml PXD000000
```

```
PXD000000
  refused  PXD000000 has status 'exclude' in manifest.yaml and will not be loaded. The producer's
           reason: A fixture for the refusal path: …
```

It is refused, with the producer's reason, and the exit status is `1`.

## 4. Publish: build the catalog and the site

```bash
datarepo publish manifest.yaml --site site --title "Example repository" \
    --purpose "how a tutorial fixture behaves"
```

```
catalog  …/example-instance/catalog.duckdb
  id       866fbaeaa433c611
  dataset  PXD999999    bundle 3b56a967d6968d39
  …
  checks   57 run, all passed
site     site
  catalog  866fbaeaa433c611
  wrote    36 files, 1 dataset pages
  skipped  croissant.json: no --data-url: a Croissant file describes downloadable files, …
  skipped  robots.txt: no --base-url: both need the site's absolute address
  skipped  sitemap.xml: no --base-url: both need the site's absolute address
```

`publish` does two things. It builds the **catalog**, one DuckDB file holding every ingested
dataset, with 57 integrity checks run on the result. Then it writes the **site**, a static website
with one page per dataset, an `llms.txt` for agents and JSON for programs. Open `site/index.html` in a
browser. Every number on it comes from the catalog whose id is printed in its footer.

Run the same command again. The catalog is `unchanged` and the site is byte-identical, so a
published copy can be diffed against a regenerated one. When you later host the site, pass
`--base-url` for the sitemap and `--data-url` for the download links and Croissant file (see
[site.md](site.md)).

## 5. Ask it something

From the command line, in SQL:

```bash
datarepo query catalog.duckdb "SELECT dataset_id, n_psms_1pct, n_protein_groups_1pct, n_open_findings FROM dataset_overview"
```

```
dataset_id  n_psms_1pct  n_protein_groups_1pct  n_open_findings
----------  -----------  ---------------------  ---------------
PXD999999   58           3                      5
```

Before you trust an answer, read its dataset's findings:

```bash
datarepo query catalog.duckdb "SELECT code, severity, message FROM findings ORDER BY severity, code" --format tsv
```

The [query cookbook](querying.md) has real questions with the SQL that answers them, and a section
on queries that look right and are wrong.

## 6. Hand it to an agent

```bash
datarepo mcp --catalog catalog.duckdb --check      # open it and report, without serving
datarepo mcp --catalog catalog.duckdb --install    # register it with Claude Code
```

```
catalog  catalog.duckdb
  id       866fbaeaa433c611
  built    … by datarepo 0.30.0
  dataset  PXD999999           58 PSMs at 1%
  tools    datarepo_describe, datarepo_search, datarepo_sql
  empty    26 table(s) present with no rows
```

The agent gets three tools: `describe` (what is here and what each column means), `search` (find a
protein, gene or dataset) and `sql` (read-only SQL). Every answer carries the `catalog_id` it came
from. When a query reads an empty table, the answer says so, so "nothing was delivered" is never
mistaken for "the answer is no". [mcp.md](mcp.md) covers the tools and the guarantees.

## Where next

| You want to | Read |
|---|---|
| load your own MetaMorpheus searches | [operating.md](operating.md), then [ingest.md](ingest.md) |
| deliver model results (age effects, curated sample metadata) | [study.md](study.md) |
| know every command and flag | [cli.md](cli.md) |
| query well, and avoid confident wrong answers | [querying.md](querying.md), [limitations.md](limitations.md) |
| understand the design | [architecture.md](architecture.md), [schema/core.md](schema/core.md) |
