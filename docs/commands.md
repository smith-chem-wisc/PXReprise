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
| `census.tsv` | One row per deposit: relevance and the text that decided it, route, profile, reason, instrument, files, organisms, title, `watch` (why it is on the watch list, if it is) and `reference_group` (the words read as a reference group, for a disease-only deposit). Opens in Excel. `organisms` is what PRIDE's search listed, which can be wrong; `organisms_of_record` is what the project record says, and it is what the census acts on. `organism_source` says which one was used: `project`, `search`, or `search_fallback` (PRIDE had no record to give). |
| `review.md` | **Read this first.** The census made readable: how much each keyword and rule contributed, a sample of relevant deposits and of keyword hits no rule matched, and every exclusion, each quoting the text that decided it. See the [tutorial](tutorial.md). |
| `summary.json` | The counts: deposits found, relevant, searchable per profile, and what the rest wait on; `queued_by_keyword` (how many deposits each keyword would queue, after every screen) and `watched`. |
| `queue.json` | The deposits the batch would search, in order. |
| `watch.tsv` | Only when the question has `disease_keywords` or `watch_keywords`: the deposits found and screened but never queued, with the reason (`watch_list`, `no_reference_group_found`). |
| `provenance.json` | When the census ran and with which versions. PRIDE changes daily, so a census is a dated snapshot. |

`--queue` also copies `queue.json` to the question's `[batch] queue` path, where `batch run` reads it. It refuses to
replace a queue that already exists, because a running batch may be using it. Delete or rename the old one first.

### `rank`

```
pxreprise rank census-folder [--only accessions.tsv] [--out folder] [--max-files 60] [--large-gb 150]
```

Orders a census's queueable deposits, biggest first within limits. Deposits rarely state their design well enough to
rank on, so size decides. It makes one PRIDE file listing per deposit, plus the deposit's SDRF if it has one, and
downloads no spectra.
- **Tier 1:** at most `--max-files` raw files and `--large-gb` GB, most raw files first, then the smaller download.
- **Tier 2:** larger than either.
- **The design, for information only:** the groups and replicates read from a deposited SDRF, or guessed from the
  file names, are written beside the ranking.
- **`--only`:** ranks just the accessions in its first column.

Writes `ranked.tsv` and `provenance.json` to `--out` (default: `rank` inside the census folder).

### `chemistry`

```
pxreprise chemistry --accessions accessions.txt [--designs folder] [--out folder]
```

Reads each deposit's protease, cysteine alkylation and label, without downloading spectra, in the order a
`chemistry = "deposit"` profile reads them. See [Concepts](concepts.md).
- **The sources:** the question's own SDRF (from `--designs`), the deposit's SDRF, PRIDE's identified modifications and
  quantification method, and the protocol text.
- **What it writes:** `chemistry.tsv`, every fact with its source and the words it was read from.
- **`differs_from_v1`:** says where a deposit differs from MetaMorpheus's defaults (trypsin, fixed carbamidomethyl,
  no label). Use it to see which deposits searched under those defaults were searched with the wrong chemistry.

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
- `stop` asks a running batch to start no new search, finish the searches under way, then exit. Meanwhile it keeps
  downloading the next deposits; the next `run` checks those files instead of downloading them again, and searches
  them first. A download still under way when the last search ends is cut and taken up again by the next `run`.
- `retry` puts one settled deposit (for example `fetch_failed`) back in the queue for the next `run`. The reason is
  required; it is kept in `state.json` with the old status, the time and the user, and logged. Refused while a batch is
  running and for a deposit whose search finished.

Only one batch runs per question at a time; a second `run` is refused while the first is alive.

### `library`

```
pxreprise library list   --machine my-machine.toml
pxreprise library repair mouse --machine my-machine.toml --reason "why"
```

- `list` shows each organism's current spectral library: its version, how many spectra it holds, and how many versions
  its chain has.
- `repair` writes a new current version without the spectra mzLib cannot read back, and changes nothing when there
  are none. Those are spectra whose modification name holds a count in parentheses, such as Unimod's `NEM:2H(5)`:
  mzLib's reader takes them for crosslinks, and a search using that library then fails. Since 0.4.20 they are left
  out whenever a search adds to a library, so only a library from an earlier version can need this. Run it while no
  batch is running.

### `worker`

Since 0.4.26, a second machine can run the probes for the batch, while this one runs every full search and delivery.
The two share one folder. The machine that delivers names it in its machine file's `[workers]` table, the probing
machine in its `[worker]` table (see `machines/example.toml`).

```
pxreprise worker sums    <folder>
pxreprise worker check   <folder> [--sums SHA256SUMS]
pxreprise worker release <share> --reason "why" [--metamorpheus NAME] [--dotnet NAME] [--database-set NAME]
```

- `sums` writes `SHA256SUMS` for every file in a folder: a database set under the share's `db\`, or a software folder.
- `check` checks a folder's files against a `SHA256SUMS`, and fails (exit 1) on any file that is missing or differs.
  Files the list does not name are allowed.
- `release` publishes the build that runs it to the probing machine. It copies itself into the share's `software\`,
  writes `control\current.toml` with the next generation, and adds a line to `control\releases.tsv`. A value you leave
  out keeps the current release's. Run it with the new build each time the delivering machine changes version.

**How the two machines work together:**

- The delivering machine assigns the prober its next deposits to probe (`accessions\assigned.tsv`), up to `window` at
  a time: fresh deposits, re-searches, retries and second looks alike (since 0.4.27; 0.4.26 assigned fresh deposits
  only). While the prober is alive, the delivering machine probes only a deposit whose raw files it already holds, and,
  when the window is full and nothing is searching, the next deposit, rather than sit idle.
- The prober probes each assigned deposit, then hands back a package in `deposits\<accession>\`, with `READY.json`
  written last. A deposit that passes carries its probe files; a park carries its reason and its
  `decisions_needed.tsv` rows.
- The delivering machine checks every file of a package against `READY.json` before it takes the package. It also
  checks that the probe ran on its own PXReprise version and on the release's database set. It then runs its own
  duplicate screen, fetches the rest of the deposit, and searches it.
- **A package that fails its check** is set aside without its raw files, and the deposit is assigned again (three
  attempts).
- **A package from another version** is not used: that deposit is probed on the delivering machine.
- **When the prober goes quiet,** the delivering machine probes for itself. Quiet means no heartbeat
  (`status\status.json`) for `quiet_hours`, a heartbeat that says it stopped, or a deposit assigned for longer than
  `assignment_hours` with no package. The prober speeds the batch up; it never stops it.
- **The prober runs only the release `control\current.toml` names.**
  - It checks its databases against the release's set before it probes anything.
  - It stops between deposits as soon as the generation changes, and its status says why.
- **It runs the delivering machine's question.** At each start the delivering machine's batch publishes the question
  it loaded, with its decisions file and designs folder, to `control\question\`. The prober runs that copy
  (`batch run <share>\control\question\question.toml`). It stops between deposits when the copy changes, so a new
  exception or hold reaches its probes at its next start. Every package names the copy it was probed under, and the
  delivering machine assigns a package probed under another copy again.
- **The prober keeps no probe files.** It deletes them once it has handed them off. It deletes a deposit's whole folder
  once the delivering machine has taken the package, or has taken the deposit back.
- `control\STOP` stops the prober the way `batch stop` stops a batch: it finishes the probe under way and starts no
  new one.

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
