---
title: Commands
nav_order: 7
---

# Commands

PXReprise is one program, `pxreprise`. You run it with a **command word** saying what to do, then the files it
needs:

```
pxreprise <command> <files> [--options]
```

For almost everything you need three commands, in this order:

| Step | Command | What it does | How long |
|---|---|---|---|
| 1 | `pxreprise validate question.toml` | Checks your question file for mistakes. | seconds |
| 2 | `pxreprise census question.toml --queue` | Searches PRIDE and decides, for every deposit, whether it would be searched and why. Writes the list the batch will work through. Downloads no data. | minutes |
| 3 | `pxreprise batch run question.toml --machine my-machine.toml` | Does everything: download, check, search and report, one deposit after another. | hours to days |

The other commands are single steps of the batch, useful for testing one deposit or one stage by hand.

## Output

Every command prints **one JSON object** to standard output, so scripts and agents can read it:

```json
{"ok": true, "data": { ... }}
{"ok": false, "error": {"type": "usage", "message": "..."}}
```

and exits with **0** (success), **1** (a handled failure; the error `type` says which) or **2** (the command was typed
wrong). An error of type `ServiceUnavailable` means PRIDE or UniProt could not be reached: try again later. Progress
messages go to standard error, for people.

## The main commands

### `validate`

```
pxreprise validate question.toml
```

Loads the question and every profile it names, and reports what it found. Nothing is downloaded.

### `census`

```
pxreprise census question.toml [--out folder] [--queue]
```

Searches PRIDE for each of the question's keywords, applies the relevance rules, and routes every deposit to a
profile, or records why not. Writes to `--out` (default: `census/<today>` beside the question):

| File | Contents |
|---|---|
| `census.tsv` | One row per deposit: relevance and the text that decided it, route, profile, reason, instrument, files, organisms, title. Opens in Excel. `organisms` is what PRIDE's search listed, which can be wrong; `organisms_of_record` is what the project record says, and it is what the census acts on. `organism_source` says which one was used: `project`, `search`, or `search_fallback` (PRIDE had no record to give). |
| `review.md` | **Read this first.** The census made readable: how much each keyword and rule contributed, a sample of relevant deposits and of keyword hits no rule matched, and every exclusion, each quoting the text that decided it. See the [tutorial](tutorial.md). |
| `summary.json` | The counts: deposits found, relevant, searchable per profile, and what the rest wait on. |
| `queue.json` | The deposits the batch would search, in order. |
| `provenance.json` | When the census ran and with which versions. PRIDE changes daily, so a census is a dated snapshot. |

`--queue` also copies `queue.json` to the question's `[batch] queue` path, where `batch run` reads it. It refuses to
replace a queue that already exists, because a running batch may be using it. Delete or rename the old one first.

### `batch`

```
pxreprise batch run    question.toml --machine my-machine.toml
pxreprise batch status question.toml
pxreprise batch stop   question.toml
pxreprise batch retry  question.toml PXD000000 --reason "why"
```

- `run` works through the queue, one deposit at a time, until the queue is done, the disk is full, or it is stopped.
  Run it again at any time: deposits already settled are skipped, and it carries on where it stopped. Each downloaded
  file is logged as it finishes, and each retry with its error.
- `status` counts the deposits by status (see [Reading the results](results.md)) and shows whether a batch is running.
- `stop` asks a running batch to finish the deposit it is on, then exit.
- `retry` puts one settled deposit (for example `fetch_failed`) back in the queue for the next `run`. The reason is
  required; it is kept in `state.json` with the old status, the time and the user, and logged. Refused while a batch is
  running and for a deposit whose search finished.

Only one batch runs per question at a time; a second `run` is refused while the first is alive.

## Single steps

For testing a stage by hand. The batch runs all of these for you.

| Command | Does |
|---|---|
| `pxreprise fetch PXD012345 --out folder --machine m.toml` | Downloads one deposit's raw files, verified. `--max-files`, `--max-file-mb`, `--parallel` and `--attempts` adjust it. |
| `pxreprise qc --spectra folder --out folder --profile label-free-dda@2 --machine m.toml` | Checks raw files against the profile's quality gates. |
| `pxreprise search --spectra folder --qc folder --out folder --organism human --profile label-free-dda@2 --machine m.toml` | Runs the MetaMorpheus search on checked files. With a profile that takes a design (`[quant] design = "sdrf"`), `--design PXD012345.sdrf.tsv` gives the SDRF to use, and `--condition-columns` the factor columns its condition is built from when it has several. |
| `pxreprise qc-payload <search folder> <qc folder> <out folder> --accession PXD012345` | Builds the QC report from a finished search. |

## Information

| Command | Does |
|---|---|
| `pxreprise version` | Versions of PXReprise, mzLib and .NET, and the list of commands. |
| `pxreprise profiles` | The search methods this installation ships, with what each accepts. |

Options shared by several commands: `--profiles <folder>` uses a different profiles folder (for developing a new
profile); `--run-date YYYY-MM-DD` stamps a batch's records with a fixed date.
