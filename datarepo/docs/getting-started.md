# Getting started

In about ten minutes this page takes you from a fresh clone to a working repository: one dataset
ingested, a catalog built, a public site written, and an agent able to ask it questions. It uses the
small example instance that ships in this repository (`tests/data/`), so you need no real data and
no search run of your own.

Every command and every line of output below was run on this example with datarepo 0.31.0. Long
paths are shortened to `…`. The ids are content hashes, so on 0.31.0 you should get the same ones;
on another version they will differ (a later section says why).

**Words used here.**
- A **producer** is the pipeline that searched the data, here MetaMorpheus.
- An **instance** is one project's collection of reanalysed datasets. It is described by a
  `manifest.yaml`.
- A **bundle** is one dataset's results, as Parquet tables.
- A **catalog** is one DuckDB file built from many bundles.
- A **PSM** (peptide-spectrum match) is one spectrum matched to one peptide.
- **1% FDR** means a list of matches accepted at a 1% false discovery rate, which is the search
  engine's own threshold.

The [glossary](README.md#glossary) has the rest.

## 1. Install

You need Python 3.11 or later and git. (Self-contained downloads that need no Python are built and
verified, but not yet published: see [operating.md](operating.md#without-python-the-self-contained-executable).)

```bash
git clone https://github.com/trishorts/dataRepo.git
cd dataRepo
python -m venv .venv
```

Activate the environment. The command depends on your shell:

| Shell | Command |
|---|---|
| Linux or macOS | `source .venv/bin/activate` |
| Git Bash on Windows | `source .venv/Scripts/activate` |
| PowerShell | `.venv\Scripts\Activate.ps1` (if it is blocked: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`) |
| cmd | `.venv\Scripts\activate.bat` |

Then install and check:

```bash
pip install ".[readers,mcp]"
datarepo doctor
```

`readers` adds pyMzLib (the `mzlib` package), which parses MetaMorpheus output; `ingest` needs it.
`mcp` adds the server that agents talk to. `doctor` checks both:

```
datarepo 0.31.0  schema 0.0.13
  pyarrow          25.0.1
  duckdb           1.5.5
  pymzlib          0.2.0
  mzLib bridge     …/site-packages/pymzlib/_dotnet/win-x64/mzlib-bridge.exe
  mcp SDK          installed
  mcp registered   no (`datarepo mcp --catalog <path> --install`)
                   config would be …/.claude.json
ready
```

Your `pyarrow` and `duckdb` versions may be newer. If the last line is not `ready`, the lines above
it say what is missing.

## 2. Copy the example instance

The example is a miniature producing instance: a manifest, plus the work folder a pipeline would
have written (`work_root/`). The `go/` folder beside them holds inputs for an optional engine that
this tutorial does not use. Copy it all out, so nothing you make lands inside the repository:

```bash
cp -r tests/data ../example-instance      # PowerShell: Copy-Item -Recurse tests\data ..\example-instance
cd ../example-instance
datarepo manifest manifest.yaml
```

```
instance   test (manifest v1)
work root  …/example-instance/work_root
store      …/example-instance/store
licence    CC-BY-4.0 - Test Working Group

 + PXD999999    include  NCBITaxon:9606 DDA label-free
     run    run_test/PXD999999  (2 files, MetaMorpheus 1.1.11)
     flags  low_id_rate, no_design_file
 - PXD000000    exclude
     run    run_test/PXD000000  (? files, MetaMorpheus ?)
     reason A fixture for the refusal path: the producer judged this run unfit, …
 - PXD111111    hold
     run    run_test/PXD111111  (? files, MetaMorpheus ?)
     reason Not yet reviewed by the producer.
```

The manifest is the contract between whoever ran the searches and dataRepo. It says which datasets
may be loaded (`include`), which may not (`exclude`, or `hold` while undecided), and why. dataRepo
never makes that call itself. A `?` means the manifest does not state that fact for the dataset.
Nothing is needed for a dataset that will not be loaded.

## 3. Ingest a dataset

```bash
datarepo ingest manifest.yaml PXD999999
```

```
PXD999999
  bundle   …/store/PXD999999/3b56a967d6968d39
  tables   datasets 1, samples 2, sample_characteristics 8, runs 2, assays 2, psms 60, peptidoforms 40,
           protein_groups 5, proteins 62, ptm_sites 36, ptm_stoichiometry 4, quant_values 37,
           definitions 14, provenance_records 3, findings 6, metrics 18, search_modifications_declared 4
  WARN     3 unresolved modification(s): UniProt:N-acetylalanine on A, …
  findings 5 open warning(s): low_id_rate, no_design_file, occupancy_not_stored, sdrf_skeleton,
           unresolved_modifications
```

Three things happened:

- **The search output became tables.** Every PSM, peptidoform, protein group, PTM site and
  quantity now has a row. Each PSM carries a
  [Universal Spectrum Identifier](https://www.psidev.info/usi), an address for its spectrum in PRIDE.
- **The counts were reconciled.** The ingester recounted what it wrote and compared it with the
  search engine's own totals. A disagreement is printed and recorded, never smoothed over.
- **What is wrong was written down.** `findings 6` are six recorded facts about this dataset.
  Five are warnings: an unusually low identification rate, no experimental design, a sample sheet
  with no biology in it, and so on. One is informational. They travel with the data, so nobody
  reads a count without its caveats.

The bundle's name, `3b56a967d6968d39`, is a hash of its input files, the schema version and the
ingester version. Run the command again and it says the bundle is unchanged. Change one byte of
input and you get a second bundle beside the first, so a result someone cited never changes under
them.

Now try the dataset the producer excluded:

```bash
datarepo ingest manifest.yaml PXD000000
```

```
PXD000000
  refused  PXD000000 has status 'exclude' in manifest.yaml and will not be loaded. The producer's
           reason: A fixture for the refusal path: …
```

It is refused with the producer's own reason, and the command exits with status `1`.

## 4. Publish: build the catalog and the site

```bash
datarepo publish manifest.yaml --site site --title "Example repository" --purpose "how a tutorial fixture behaves"
```

```
catalog  …/example-instance/catalog.duckdb
  id       a90e1b47c81f28d5
  dataset  PXD999999    bundle 3b56a967d6968d39
  note     logs.resolve_genes coverage (databases with an artefact): 0 of 1; no artefact for 89fb8c7a1140 (PXD999999)
  tables   assays 2, datasets 1, definitions 14, findings 6, gene_resolutions 0, …
  indexes  22
  checks   57 run, all passed
site     site
  catalog  a90e1b47c81f28d5
  wrote    36 files, 1 dataset page
  skipped  croissant.json: no --data-url: a Croissant file describes downloadable files, …
  skipped  robots.txt: no --base-url: both need the site's absolute address
  skipped  sitemap.xml: no --base-url: both need the site's absolute address
```

`publish` does two things:

- **It builds the catalog.** That is one DuckDB file holding every ingested dataset, with 57
  integrity checks run on the result. Any failed check would have stopped it.
- **It writes the site.** That is a static website with one page per dataset, an `llms.txt` for
  agents, and JSON for programs.

Open `site/index.html` in a browser. Every number on it comes from the catalog whose id is in its
footer. `--purpose` is the question your repository serves, and the pages say it; leave it out and
they claim none.

The `note` line says that no **gene resolution** has been run for this dataset's protein database.
That is an optional engine that maps proteins to genes (see [operating.md](operating.md)). Nothing
is wrong.

Run the same command again. The catalog is reported `unchanged` and the site comes out
byte-identical, so a published copy can always be diffed against a regenerated one. When you host
the site, add `--base-url` (for the sitemap) and `--data-url` (for download links and a
[Croissant](https://mlcommons.org/croissant/) file, which machine-learning tools load). See
[site.md](site.md).

**Why the ids are what they are.** A bundle id hashes the *ingester's* version, so it moves only
when ingestion itself changes. A catalog id also hashes the *package* version, so any new release of
dataRepo gives a new catalog id from the same bundles. Rebuilding a catalog is cheap; re-ingesting is
not.

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

58 of the 60 PSMs pass 1% FDR. The other two are kept, not deleted: the base `psms` table holds
every match, and views such as `psms_1pct` apply the search engine's threshold for you.

Before you trust a number, read its dataset's findings, most serious first:

```bash
datarepo query catalog.duckdb "SELECT code, severity FROM findings ORDER BY CASE severity WHEN 'error' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, code"
```

```
code                      severity
------------------------  --------
low_id_rate               warning
no_design_file            warning
occupancy_not_stored      warning
sdrf_skeleton             warning
unresolved_modifications  warning
occupancy_decoy_groups    info
```

Add `message` to the column list to read each one in full. The [query cookbook](querying.md) has
real questions with the SQL that answers them, and a section on queries that look right and are
wrong.

## 6. Hand it to an agent

First check that the catalog opens:

```bash
datarepo mcp --catalog catalog.duckdb --check
```

```
catalog  catalog.duckdb
  id       a90e1b47c81f28d5
  built    … by datarepo 0.31.0
  dataset  PXD999999           58 PSMs at 1%
  tools    datarepo_describe, datarepo_search, datarepo_sql
  empty    26 table(s) present with no rows
```

`26 table(s) present with no rows` is expected here. The schema has tables for data this small
example does not have (glycopeptides, age effects, GO annotation). They exist and are empty, and an
agent is told so. An empty table is never a negative answer.

To register the catalog with [Claude Code](https://claude.com/claude-code):

```bash
datarepo mcp --catalog catalog.duckdb --install
```

This records the catalog's **absolute** path in your Claude Code user configuration
(`~/.claude.json`, the path `doctor` printed), under the server name `datarepo`. Restart Claude Code
to pick it up. `--list` shows what is registered, and [mcp.md](mcp.md) covers other clients and
removing an entry.

The agent gets three tools:
- `describe`: what is here, and what each column means;
- `search`: find a protein, gene or dataset;
- `sql`: read-only SQL.

Every answer carries the `catalog_id` it came from. When a query reads an empty table, the answer
says so, so "nothing was delivered" is never mistaken for "the answer is no".

## Where next

| You want to | Read |
|---|---|
| load your own MetaMorpheus searches | [operating.md](operating.md), then [ingest.md](ingest.md) |
| deliver model results (age effects, curated sample metadata) | [study.md](study.md) |
| know every command and flag | [cli.md](cli.md) |
| query well, and avoid confident wrong answers | [querying.md](querying.md), [limitations.md](limitations.md) |
| understand the design | [architecture.md](architecture.md), [schema/core.md](schema/core.md) |
