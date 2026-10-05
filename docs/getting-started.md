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
   [MetaMorpheus 1.1.11 release](https://github.com/smith-chem-wisc/MetaMorpheus/releases/tag/1.1.11).
2. Unzip it to `D:\tools\MetaMorpheus-1.1.11`.

## 3b. Download dataRepo

[dataRepo](https://github.com/smith-chem-wisc/dataRepo) turns the search results into a repository you can query, and
a website. It is a ready-to-run download: no Python.

1. From the [dataRepo 1.0.0 release](https://github.com/smith-chem-wisc/dataRepo/releases/tag/v1.0.0), download the
   file for your computer: `datarepo-1.0.0-win-x64.zip`, `datarepo-1.0.0-linux-x64.tar.gz`,
   `datarepo-1.0.0-osx-arm64.tar.gz` (Apple silicon) or `datarepo-1.0.0-osx-x64.tar.gz` (Intel).
2. Unpack it into `D:\tools`, which makes `D:\tools\datarepo`. On Linux and macOS use `tar -xzf`.
3. Check it: `D:\tools\datarepo\datarepo.exe --version` should print `datarepo 1.0.0`, and
   `D:\tools\datarepo\datarepo.exe doctor` should end with `ready`. On macOS, if the program is blocked, run
   `xattr -dr com.apple.quarantine datarepo` once in the folder you unpacked.

PXReprise asks dataRepo for its version when a batch starts. From dataRepo 1.0.0 on, it reads ingest's JSON result
(`ingest --json`); an older dataRepo is judged by its exit code alone.

Without dataRepo, everything still runs; it stops after the search.

## 4. Describe your machine

Copy the template, then open the copy in a text editor:

```
copy machines\example.toml machines\my-machine.toml
```

Set four things. Use forward slashes in paths, even on Windows.

- `work_root`: where the work goes, e.g. `"D:/pxreprise"`.
- `"1.1.11"` under `[metamorpheus]`: `"D:/tools/MetaMorpheus-1.1.11/CMD.dll"`.
- `datarepo`: `"D:/tools/datarepo/datarepo.exe"`.
- `accept_thermo_licence`: Thermo `.raw` files are read with Thermo's RawFileReader, under
  [its licence](https://github.com/thermofisherlsms/RawFileReader). Read it; set `true` only if you accept it.

Files named `machines/my-*.toml` are never committed, so your paths stay yours.

## 5. Run the first-run test

`examples/first-run` sends one small public deposit (PXD058082: mouse, 15 files, 4.5 GB) through every step.

```
pxreprise validate examples/first-run/question.toml
pxreprise batch run examples/first-run/question.toml --machine machines/my-machine.toml
```

It takes about an hour. Watch progress in `examples/first-run/work/state/batch.log`. The first time, it downloads
the mouse proteome from UniProt (a few minutes; kept for later runs). When it ends:

```
pxreprise batch status examples/first-run/question.toml
```

should show `"searched": 1`. Then open, in a browser:

- `examples/first-run/work/site/index.html`: the website dataRepo built from the results, one page per deposit;
- `examples/first-run/work/runs/PXD058082/05_qc/report.html`: the quality report.

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

## When something goes wrong

Every command prints one JSON object. `"ok": false` comes with an error `type` and a `message`.

| You see | What to do |
|---|---|
| exit code 2, `"type": "usage"` | The command was typed wrong; the message says how to fix it. |
| `"type": "ServiceUnavailable"` | PRIDE or UniProt is down or slow. Try again later; a running batch retries by itself. |
| `names no MetaMorpheus 1.1.11` | Your machine file's `[metamorpheus]` entry is missing or misspelled. |
| `contaminant database missing` | The `[metamorpheus]` path is not inside the unzipped release folder. |
| `batch.log` says `free space ... below ... floor: stopping` | Free up disk, or lower `min_free_gb` in your machine file. |
| a deposit you expected was not searched | Find it in the census's `census.tsv`: the `route` and `route_reason` columns say why. |

To report a problem, open an issue at <https://github.com/smith-chem-wisc/PXReprise/issues> with the command, its JSON
output, the last 50 lines of `batch.log`, and the output of `pxreprise version`.
