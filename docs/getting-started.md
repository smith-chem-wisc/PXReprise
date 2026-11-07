---
title: Getting started
nav_order: 3
---

# Getting started

About 30 minutes of setup, then an hour for a first test run. Commands are shown for Windows PowerShell; on Linux or
macOS use `/` paths and drop `.exe`.

## 1. Install the prerequisites

- **.NET 10 SDK**: <https://dotnet.microsoft.com/download/dotnet/10.0>. Check with `dotnet --list-sdks`: you need a
  line starting `10.`.
- **git**: <https://git-scm.com/>.
- **Disk**: at least 100 GB free on the drive where the work will go.

You do **not** need Python.

## 2. Download and build PXReprise

```
git clone https://github.com/smith-chem-wisc/PXReprise.git
cd PXReprise
dotnet build -c Release
dotnet test -c Release --filter "Category!=ExternalService&Category!=LocalCorpus"
```

The tests should end with `Failed: 0`. A few say *Skipped*: they need data that exists only on the developers'
machine.

Now publish the program to a folder of its own. Pick any tools folder; these docs use `D:\tools`:

```
dotnet publish src/PXReprise -c Release -o D:\tools\pxreprise
D:\tools\pxreprise\pxreprise.exe version
```

`version` should print `"ok": true`. To type just `pxreprise` from now on, add `D:\tools\pxreprise` to your `PATH`.

## 3. Download MetaMorpheus

1. Download **MetaMorpheus_CommandLine.zip** from the
   [MetaMorpheus 1.1.13 release](https://github.com/smith-chem-wisc/MetaMorpheus/releases/tag/1.1.13).
2. Unzip it to `D:\tools\MetaMorpheus-1.1.13`.

**Why not the newest MetaMorpheus?** Each profile names the release it runs on, and `label-free-dda-uniprot@1`, the
profile a new installation uses, names 1.1.13. Its protein databases download by themselves. A profile never changes
after release, so a newer MetaMorpheus arrives with a new profile version, once it has been measured: see
[Profiles](concepts.md#profiles).

## 3b. Download dataRepo

[dataRepo](https://github.com/smith-chem-wisc/dataRepo) turns the search results into a repository you can query, and
a website. It is a ready-to-run download: no Python.

1. From the [dataRepo 1.4.3 release](https://github.com/smith-chem-wisc/dataRepo/releases/tag/v1.4.3), download the
   file for your computer: `datarepo-1.4.3-win-x64.zip`, `datarepo-1.4.3-linux-x64.tar.gz`,
   `datarepo-1.4.3-osx-arm64.tar.gz` (Apple silicon) or `datarepo-1.4.3-osx-x64.tar.gz` (Intel).
2. Unpack it into `D:\tools`, which makes `D:\tools\datarepo`. On Linux and macOS use `tar -xzf`.
3. Check it: `D:\tools\datarepo\datarepo.exe --version` should print `datarepo 1.4.3`, and
   `D:\tools\datarepo\datarepo.exe doctor` should end with `ready`. On macOS, if the program is blocked, run
   `xattr -dr com.apple.quarantine datarepo` once in the folder you unpacked.

Use one release for the whole repository: ingest, catalog and website. Its version is part of each bundle's and catalog's
identity, and its release notes say when moving to a newer one means ingesting every deposit again.

Without dataRepo, everything still runs; it stops after the search.

## 4. Describe your machine

Copy the template, then open the copy in a text editor:

```
copy machines\example.toml machines\my-machine.toml
```

Set four things. Use forward slashes in paths, even on Windows.

- `work_root`: where the work goes, e.g. `"D:/pxreprise"`.
- `"1.1.13"` under `[metamorpheus]`: `"D:/tools/MetaMorpheus-1.1.13/CMD.dll"`.
- `datarepo`: `"D:/tools/datarepo/datarepo.exe"`.
- `accept_thermo_licence`: Thermo `.raw` files are read with Thermo's RawFileReader, under
  [its licence](https://github.com/thermofisherlsms/RawFileReader). Read it; set `true` only if you accept it.

Files named `machines/my-*.toml` are never committed, so your paths stay yours. The template explains every other
setting; section 8 below says which ones matter for a long batch.

## 5. Run the first-run test

`examples/first-run` sends one small public deposit (PXD058082: mouse, 15 files, 4.5 GB) through every step.

```
pxreprise validate examples/first-run/question.toml
pxreprise batch run examples/first-run/question.toml --machine machines/my-machine.toml
```

It takes about an hour, longer on a busy machine. Watch progress in `examples/first-run/work/state/batch.log`.

The first time, it downloads three proteomes from UniProt before anything else: human, mouse and rat, about 1.7 GB, because
the probe search checks each deposit's species against all three. They are kept for later runs. This takes minutes when
UniProt is well and far longer when it is slow. A download that fails is tried again on the next pass, 30 minutes later,
and the log says `still waiting on PRIDE or UniProt` if the batch gives up for now; run it again later. When it ends:

```
pxreprise batch status examples/first-run/question.toml
```

should show `"searched": 1`. Then open, in a browser:

- `examples/first-run/work/site/index.html`: the website dataRepo built from the results, one page per deposit;
- `examples/first-run/work/runs/PXD058082/05_qc/qc_payload.json`: the quality data. A readable `report.html` is written beside
  it only with the optional `qc_python` setting.

The repository itself is in `examples/first-run/work/store/`. [Reading the results](results.md) explains the rest.
The log may show `MISMATCH psms_target_1pct` with two numbers a few PSMs apart. That is expected: dataRepo and
MetaMorpheus count the 1% PSMs slightly differently.

## 6. Ask your own question

1. Work through [Tutorial: your first question](tutorial.md) (fifteen minutes). It builds a real question and
   teaches the loop that makes rules good: census, read `review.md`, fix, repeat.
2. Copy `examples/muscle-ageing/question.toml` to a folder of your own and change it to your topic
   ([every setting](questions.md)). Check it: `pxreprise validate path/to/question.toml`.
3. Run the loop: `pxreprise census path/to/question.toml`, read `review.md` in the census folder, fix, repeat.
   When the samples look right, write the queue: `pxreprise census path/to/question.toml --queue`.
4. Run it: `pxreprise batch run path/to/question.toml --machine machines/my-machine.toml`. This can take days: it
   works through the queue one deposit at a time. Stop it cleanly with `pxreprise batch stop path/to/question.toml`;
   start it again with `batch run`, and it carries on where it stopped.

## 7. Updating

New versions are listed, newest first, on the [releases page](https://github.com/smith-chem-wisc/PXReprise/releases)
and in `CHANGELOG.md`. Each one says whether it needs a different MetaMorpheus, whether your machine file needs a
change, and whether it is safe to update part-way through a batch. Read that first. To see what you have:
`pxreprise version` (the `pxreprise` line is the version and the exact commit).

1. **Stop any running batch** and wait for it to exit (the current search finishes first). On Windows the program
   cannot be replaced while it runs.

   ```
   pxreprise batch stop path/to/question.toml
   ```

2. **Get the new code, build and test it**, in the folder you cloned:

   ```
   git pull
   dotnet build -c Release
   dotnet test -c Release --filter "Category!=ExternalService&Category!=LocalCorpus"
   ```

   The tests should end with `Failed: 0`. If they don't, stop: your installed program is still the old one and still
   works.
3. **Replace the program**, then check it:

   ```
   dotnet publish src/PXReprise -c Release -o D:\tools\pxreprise
   pxreprise version
   ```

4. **Do what the release notes ask**, if anything: download a new MetaMorpheus, or add a line to your machine file.
5. **Start the batch again** with `batch run`. It carries on where it stopped.

What an update keeps:
- **Your machine file.** `machines/my-*.toml` is never committed, so `git pull` does not touch it. New settings come
  with defaults.
- **Your questions, queue, `state.json` and results.** They live in your own folders.
- **Finished results.** A new version never changes them.

**To keep the old version** (useful during a long batch), publish the new one to a new folder instead, e.g.
`D:\tools\pxreprise-0.2.0`, and run it from there. Going back is then just running the old folder's program.

## 8. Running a batch for weeks

A question of any size runs for days to weeks. What has mattered on the machine that runs one:

- **Run from a fixed folder.** Publish each version to a folder of its own, as above. A running batch then never changes
  under itself, and moving to a new version is a deliberate step: stop, wait for the batch to exit, start the new folder.
- **Stopping takes time.** `batch stop` lets the searches under way finish, and each raw file is hashed before a search
  starts, so a stop can take hours. Don't kill a batch.
- **Decisions wait for you.** A deposit the batch cannot settle by itself (the spectra disagree with the record, its
  chemistry is unclear, it would not fit on the disk) waits, with a row in `decisions_needed.tsv` in the question's
  `run_root`. Read that file regularly. The answer goes in the question file, and the batch reads the question only when it
  starts, so a change takes effect at the next start.
- **After a reboot,** run the same `batch run` again. It redoes what was cut off. `driver.last.json` in the state folder
  records the exact command, so a scheduled task can do this for you. Set such a task to priority 4: Windows Task
  Scheduler's default of 7 runs the batch and its searches at low memory and I/O priority, and a search can then crawl for
  hours with no error.
- **Machine file settings to look at** (`machines/example.toml` explains each):
  - `search_slots` and `max_threads`: how much of the machine to use.
  - `dotnet_root`: a private copy of .NET, so an automatic update cannot remove the runtime under a running search.
  - `min_free_gb`: where the batch stops rather than fill the disk.
  - `[deposit_limits]`: how big a deposit this machine takes whole. Raise it one step at a time; a running batch re-reads it.
  - `aspera_ascp` and `aspera_key`: IBM Aspera for downloads, often much faster from EBI when EBI's side is healthy.
    `aspera_attempts = 0` turns it off without removing the lines.
- **Keep the search output.** A later dataRepo release can mean ingesting every deposit again, which reads `04_search`.

## When something goes wrong

Every command prints one JSON object. `"ok": false` comes with an error `type` and a `message`.

| You see | What to do |
|---|---|
| exit code 2, `"type": "usage"` | The command was typed wrong; the message says how to fix it. |
| `"type": "ServiceUnavailable"` | PRIDE or UniProt is down or slow. Try again later; a running batch retries by itself. |
| `names no MetaMorpheus 1.1.13` | Your machine file's `[metamorpheus]` entry is missing or misspelled. |
| `contaminant database missing` | The `[metamorpheus]` path is not inside the unzipped release folder. |
| `batch.log` says `free space ... below ... floor: stopping` or `paused at the disk floor` | Free up disk, or lower `min_free_gb` in your machine file, then run the batch again. |
| a deposit you expected was not searched | Find it in the census's `census.tsv`: the `route` and `route_reason` columns say why. |
| a deposit's status starts `waiting_` | [Reading the results](results.md#what-each-status-means) says what it waits on. If it needs a person, it has a row in `decisions_needed.tsv`. |
| `MISMATCH psms_target_1pct` in the log, with two numbers a few PSMs apart | Expected: dataRepo and MetaMorpheus count the 1% PSMs slightly differently. |

To report a problem, open an issue at <https://github.com/smith-chem-wisc/PXReprise/issues> with the command, its JSON
output, the last 50 lines of `batch.log`, and the output of `pxreprise version`.
