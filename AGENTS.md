# PXReprise: instructions for an AI agent

You are an AI coding agent (Claude Code, Codex, Cursor, or similar) and a person has asked you to install PXReprise
and run it for them. Follow this file top to bottom. It is written to be executed, not skimmed: every step says what
to run, what success looks like, and what to do when it fails.

**What PXReprise is.** A command-line program that re-analyses public proteomics data. It searches PRIDE (the public
archive of mass-spectrometry experiments) for deposits relevant to a biological question, downloads their raw files,
checks their quality, searches them with MetaMorpheus, and writes the results with a full record of how they were made.
The question is a small text file (`question.toml`); nothing in the program is specific to one question.

**Ground rules for you, the agent.**

- Ask the person before: accepting the Thermo licence (step 4), starting a long batch (step 7), or deleting anything.
- Never edit files under `profiles/`. A profile is a versioned scientific method; changing one silently changes
  results. If a profile does not fit, tell the person.
- Every `pxreprise` command prints ONE JSON object on stdout: `{"ok":true,"data":...}` or
  `{"ok":false,"error":{"type":...,"message":...}}`. Exit code 0 = success, 1 = handled failure, 2 = the command
  was typed wrong. Read the JSON; do not scrape the text on stderr (that is a progress log for humans).
- An error whose type is `ServiceUnavailable` means PRIDE or UniProt is down or slow: wait and retry. Any other error
  type is real: stop and report it.

---

## 1. Check the prerequisites

| Need | Check | If missing |
|---|---|---|
| .NET 10 SDK | `dotnet --list-sdks` shows a `10.` line | Install from https://dotnet.microsoft.com/download/dotnet/10.0 (the SDK, not only the runtime) |
| git | `git --version` | Install git |
| Disk | at least 100 GB free where the work will go | Ask the person where to put the work folder |
| Network | `https://www.ebi.ac.uk/pride/` reachable | Nothing works offline; stop |

Windows is the tested platform. Linux and macOS should work (everything runs on .NET) but are untested: say so to
the person if they are on one.

Python is **not** needed.

## 2. Get PXReprise and build it

```
git clone https://github.com/smith-chem-wisc/PXReprise.git
cd PXReprise
dotnet build -c Release
dotnet test -c Release --filter "Category!=ExternalService&Category!=LocalCorpus"
```

Success: the build reports `0 Error(s)` and the tests report `Failed: 0`. Some tests say "Skipped"; that is expected
(they need data that exists only on the developers' machine).

Then publish the program to a folder of its own, so the batch runs from a fixed build:

```
dotnet publish src/PXReprise -c Release -o <TOOLS>/pxreprise
```

`<TOOLS>` is any folder the person chooses. From here on, `pxreprise` means `<TOOLS>/pxreprise/pxreprise.exe`
on Windows or `<TOOLS>/pxreprise/pxreprise` elsewhere. Check it:

```
pxreprise version
```

Success: `"ok": true` and a list of verbs.

## 3. Get MetaMorpheus

The search engine is MetaMorpheus, a separate program. The profile says which release it needs (`1.1.11` for
`label-free-dda@2`).

1. Download `MetaMorpheus_CommandLine.zip` from https://github.com/smith-chem-wisc/MetaMorpheus/releases/tag/1.1.11
2. Unzip it to `<TOOLS>/MetaMorpheus-1.1.11/`.
3. Check: `dotnet <TOOLS>/MetaMorpheus-1.1.11/CMD.dll --help` prints MetaMorpheus's help text.

## 4. Write the machine file

A machine file says where things live on this computer. Copy the template and edit it:

```
cp machines/example.toml machines/my-machine.toml
```

In `machines/my-machine.toml`:

- `work_root`: an absolute path with room (step 1), e.g. `D:/pxreprise`. Use forward slashes, even on Windows.
- `[metamorpheus]`, `"1.1.11"`: the absolute path to `CMD.dll` from step 3.
- `accept_thermo_licence`: Thermo `.raw` files can only be read under Thermo's RawFileReader licence
  (https://github.com/thermofisherlsms/RawFileReader). **Ask the person** whether they accept it. Set it to `true`
  only if they say yes. If they say no, PXReprise cannot process `.raw` data; stop and tell them.
- Leave `datarepo` and `qc_python` out. They are optional integrations with other projects' tools.

`machines/my-*.toml` is ignored by git, so the person's paths never get committed.

## 5. Prove the installation (about one hour)

`examples/first-run` sends one small public deposit (PXD058082: mouse, 15 files, 4.5 GB) through the whole pipeline.
Its queue is already written, so it needs no census.

```
pxreprise validate examples/first-run/question.toml
pxreprise batch run examples/first-run/question.toml --machine machines/my-machine.toml
```

`batch run` runs until the queue is done, then prints `{"ok":true,"data":{"finished":true}}`. Progress goes to
`examples/first-run/work/state/batch.log`; read it to report progress. In order you should see: `database: downloading UniProt UP000000589`
(first run only; the mouse proteome takes a few minutes), `database: ...`, `PROBE qc ok`, `FETCH raw_files=15 of 15`, `SEARCH starting`, `SEARCH rc=0 success=True`,
`QC-PAYLOAD`, `CLEANUP`, then `queue exhausted`.

Then check the outcome:

```
pxreprise batch status examples/first-run/question.toml
```

Success: `"by_status": {"searched": 1}`. The results are in `examples/first-run/work/runs/PXD058082/` (see
"Reading the results" below). Tell the person the number of PSMs from the `SEARCH rc=0` line of the log.

If it fails, see "When something goes wrong".

## 6. Design the person's question with them

The question decides which public data the person's conclusions rest on, so it is **their** scientific choice. Your
job is to turn their words into rules, show them the consequences, and let them decide. They should never have to
write a regular expression. The method is the one in the tutorial,
https://smith-chem-wisc.github.io/PXReprise/tutorial.html. Read it first; its finished example is
`examples/muscle-ageing/`.

**6a. Interview.** Ask these, in plain words, one or two at a time. Do not ask about regular expressions.

1. What are you studying? One sentence. (This becomes `description`.)
2. Which words would a paper or dataset about it use? Include synonyms, and the disease or model names.
   (These become `keywords`, and the basis of `require_any`.)
3. Which organisms? (Human, mouse and rat can be searched; others are counted only.)
4. Which tissues or sample types count, and which do not? (For example: skeletal muscle yes, heart muscle no.)
5. What looks similar but should NOT count? (For example: type 1 diabetes in a type 2 question; cancer cachexia in an
   ageing one.) (These become `exclude_if_any`.)
6. Is there anything that should count even when it looks excluded? (These become `unless_any`, kept narrow.)
7. Do you already know any datasets that must be in, or must be out? Accession numbers if they have them.
   (These become `decisions.tsv` entries, each with the person's reason, and a check that the rules agree.)

**6b. Draft.** Write `question.toml` in a folder the person chooses, outside the PXReprise clone. Start broad.
- `profiles = ["label-free-dda@2", "tmt-dda@1"]` (TMT deposits are then counted, not searched).
- `[discover] organisms`: exact PRIDE spellings: `"Homo sapiens (human)"`, `"Mus musculus (mouse)"`, `"Rattus norvegicus (rat)"`.
- Every rule: case-insensitive; `\b` around words (in TOML `\\b`) so `old` cannot match `fold`; match topic words,
  not incidental ones ("3 months old" is an age, not ageing); proximity windows of about 150 characters
  (`A.{0,150}B` and `B.{0,150}A`), not 60.
- `[batch]`: `run_root = "work/runs"`, `state_dir = "work/state"`, `queue = "work/queue.json"`.

Then `pxreprise validate <path>/question.toml`.

**6c. Loop: census, read, show, fix.** Repeat until the person is satisfied (usually 3 to 5 rounds, a minute or two each):

```
pxreprise census <path>/question.toml --out <path>/census/round-N
```

Read `<out>/review.md` (not `census.tsv`: it is summarised for you). Then:

- **Show the person** the outcome counts, and 5 to 10 rows from each of these sections, with the quoted evidence:
  *Relevant: are these right?*, *Excluded* (all of them if there are few: each is a deposit lost), and
  *Found by a keyword, but no rule matched*. Ask: which are wrong? Which are missing?
- **Look for yourself** at what the samples may miss: filter `census.tsv` for `relevance` = `not_relevant` whose
  `title` contains the topic's words, and show any that look relevant.
- **Fix, and say what you changed and why**, in terms of the person's answers:
  - a relevant deposit that is wrong because of how a word was matched: tighten that rule (`\b`, topic words);
  - a class of wrong deposits: add to `exclude_if_any`;
  - a good deposit lost to an exclusion: narrow the exclusion, or add to `unless_any`;
  - a good deposit no rule matched: add its phrasing to `require_any`, or widen a window;
  - a handful of specific cases no rule should decide: `decisions.tsv` entries (tab-separated
    `accession	verdict	reason`, verdict `include` or `exclude`, the reason in the person's words). If the call
    depends on what was measured, open `https://www.ebi.ac.uk/pride/archive/projects/<accession>` and check first.
- Use the review's **Keywords** table to judge keywords by their *relevant* column, not their *found* column, and
  the **Rules** table to spot a rule that matches nothing, or an `unless_any` that rescues too much.

Keep every round's census folder: `review.md` samples are chosen by accession, so rounds can be compared row by row.

## 7. Write the queue, and report what the batch would do

When the person is happy with the last round:

```
pxreprise census <path>/question.toml --queue
```

`--queue` installs the queue at the question's `[batch] queue` path (it refuses to replace an existing one). Report
from `summary.json`: how many deposits are in scope, how many will be searched (`routes.search`, and `queued`), and
what the rest wait on (`waiting_on`, e.g. `dia`, `timstof`, `tmt-dda@1`): capabilities a future profile would add.

**Ask the person before step 8.** A batch runs for hours to days and downloads hundreds of GB over its life (each
deposit's raw files are deleted after its search).

## 8. Run the batch

```
pxreprise batch run <path>/question.toml --machine machines/my-machine.toml
```

Run it in the background; it can take days. It processes the queue one deposit at a time: screen, probe (download 3
files and check their quality), fetch the whole deposit, check every file, search, write QC, delete the raw files. It
downloads the next deposit while the current one is searching.

- Progress: `<state_dir>/batch.log`, and `pxreprise batch status <question.toml>`.
- Stop cleanly: `pxreprise batch stop <question.toml>` (the current search finishes first).
- Restart: run `batch run` again. It skips everything already settled and resumes where it stopped.

## Reading the results

Each deposit gets `<run_root>/<PXD accession>/`:

| Folder | What is in it |
|---|---|
| `02_fetch/` | the download record (`fetch_manifest.json`: every file, its size and SHA-256); the raw files themselves are deleted after the search |
| `02b_qc/` | per-file quality checks (`qc_report.json`) |
| `04_search/mm/Task3SearchTask/` | MetaMorpheus's results: `AllPSMs.psmtsv`, `AllPeptides.psmtsv`, `AllQuantifiedProteinGroups.tsv`, `AllQuantifiedPeptides.tsv`, `results.txt` |
| `05_qc/` | a QC report for people (`report.html`) and its data (`qc_payload.json`) |
| every folder | `provenance.json`: exact inputs, commands, tool versions and checksums |

Per-deposit status lives in `<state_dir>/state.json`. The statuses a person will ask about:

| Status | Meaning |
|---|---|
| `searched` | done; results in the run folder |
| `deferred_needs_design_selection` | more than 60 raw files; not subsampled automatically, because choosing runs is a scientific decision |
| `deferred_large_files`, `deferred_search_too_large` | too big for this profile's limits |
| `skipped_acquisition` | the files are not what the profile accepts (e.g. MS2 read in the ion trap) |
| `waiting_*` | the deposit needs a capability no available profile has (DIA, TMT, timsTOF, ...) |
| `fetch_unavailable`, `probe_fetch_unavailable` | PRIDE dropped the downloads through every retry; tried again on a later pass (`fetch_passes`, default 3), not settled |
| `fetch_failed`, `probe_fetch_failed` | downloads failed on every pass, or broke in a way a retry cannot fix (e.g. a checksum mismatch) |
| `search_failed` | MetaMorpheus failed; see `04_search/metamorpheus.log` |

## When something goes wrong

| Symptom | Cause and fix |
|---|---|
| `"type":"usage"`, exit 2 | The command was typed wrong; the message says how. |
| `"type":"ServiceUnavailable"` | PRIDE or UniProt is down. Wait, then rerun; the batch also retries on its next pass. |
| `names no MetaMorpheus 1.1.11` | The machine file's `[metamorpheus]` table does not name that release (step 4). |
| `contaminant database missing` | `[metamorpheus]` points somewhere other than the unzipped release folder (step 3). |
| log says `free space ... below ... floor: stopping` | The work disk is fuller than `min_free_gb`; free space or lower it. |
| `failed on availability, pass 1 of 3: retry next pass` | PRIDE dropped the downloads through every attempt. The batch waits `pass_wait_minutes` (default 30) and tries again; nothing to do. |
| `failed on availability, pass 3 of 3: settled` | PRIDE failed the deposit on every pass; it is settled as `fetch_failed`. Report it; do not edit `state.json` by hand. |
| A deposit you expected is missing | Look it up in the census's `census.tsv`: the `route` and `route_reason` columns say why. |
| A search fails and `04_search/metamorpheus.log` mentions the Thermo licence | `accept_thermo_licence` is not `true` (step 4). |

When reporting a problem to the developers, include the command, its JSON output, the last 50 lines of `batch.log`,
and the output of `pxreprise version`.

## Reference

- Full documentation: https://smith-chem-wisc.github.io/PXReprise/
- Every command: `docs/commands.md`
- Writing a question: `docs/questions.md`; the tutorial: `docs/tutorial.md`
