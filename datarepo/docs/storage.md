# Sharing bundles: `push` and `pull`

Ingest writes bundles into a folder on the machine that ran the search, and `build` reads a folder on the machine that
builds. `push` and `pull` move whole bundles, engine outputs and study deliveries between those folders and a **shared
store**, which is another folder or a prefix in an S3 bucket:

```
datarepo push s3://<bucket>/<instance>/store --store /data/store           # this machine -> shared
datarepo pull s3://<bucket>/<instance>/store --store /srv/store PXD033599  # shared -> this machine
```

Several people can each search on their own machine and push to one shared store. A machine that builds or serves a
catalog pulls what it needs to its own disk first: DuckDB reading S3 for every query is slow (seconds per query), so
nothing in dataRepo reads S3 directly.

## What the copy guarantees

- **Nothing is replaced.** Each file is written once. On S3 that is a conditional write (`If-None-Match: *`), so S3
  itself refuses a second writer. A file already there with the same content is accepted; with different content the
  copy stops with an error and changes nothing.
- **Each folder's record is written last**: `bundle.json` for a bundle, `run.json` for an engine's output, `study.json`
  for a study delivery. A folder exists for every reader once its record does. A copy that stops halfway (a dropped
  connection, a closed laptop) leaves files that no reader counts, and running the same command again finishes it.
- **Every bundle file is checked against the bundle's own record first.** A copied source must have the `sha256`
  recorded in `sources`, and a table its `sha256` when `tables` records one (1.5.0, dataRepo D61). A table's Parquet row
  count must equal its count in `tables`, which is the only check for a bundle written before 1.5.0. A bundle that does
  not match is not copied. Engine outputs and study deliveries are copied as they are. On S3, each object also carries
  its sha256, which S3 checks on arrival.
- **A folder already in the destination is skipped** (`present`), so pushing a whole store again copies only what is
  new.

`--dry-run` lists what would be copied and checks each bundle has its tables, without writing.

## What is copied

With no accession, `push` and `pull` copy every complete folder in the store:

| Folder | Complete once it holds |
|---|---|
| `<dataset>/<bundle_id>/` | `bundle.json` |
| `_engine/<engine>/<artefact_id>/` | `run.json` |
| `_study/<layer>/<id>/` | `study.json` |

Naming accessions copies only those datasets' bundles. A folder whose name starts with `.` is one still being written,
and is skipped.

Not copied:

- anything else under `_` (1.5.0's `_companion/` is added once its record file is named);
- catalogs and the derived tables beside them, since a catalog is built for the machine that reads it;
- the search output an ingest read, which stays on the machine that searched. A re-ingest runs there.

## S3 credentials

dataRepo never stores a key. It uses the AWS SDK's default credential chain: for the lab, sign in with
`aws sso login --profile <profile>` and set `AWS_PROFILE=<profile>` (and `AWS_REGION` if the profile has none). Keep
the bucket's name out of scripts and notes that go into git; read it from your own environment instead.
