# MetaMorpheus output samples

PXReprise reads MetaMorpheus's output: the engine reads `results.txt` and the task files, and datarepo reads the
search results, the quantification tables and the searched databases. A MetaMorpheus release can change any of them.
So every supported release searches the same small inputs, kept here, and what PXReprise relies on is checked on
that real output. A test belongs to a function (label-free quantification, TMT, ...), never to a profile, and runs on
every release from its first one on. A release that breaks a test is either read the old way and the new way, or the
code learns the new shape; where a release changes a meaning on purpose, the test says both and from which release.

The inputs are small and shared, and what is tested is MetaMorpheus's OUTPUT, not MetaMorpheus: the search does not
have to be a good one. Nothing is made up where MetaMorpheus's or mzLib's own test data already serves.

## How it runs

`.github/workflows/metamorpheus-output.yml` runs each release in `releases.tsv`. It does not run on pull requests: it
runs by hand (Actions, "MetaMorpheus output", Run workflow) and from `release.yml`, which publishes a release only when
it is green. For each release it downloads `MetaMorpheus_CommandLine.zip` from MetaMorpheus's GitHub release, checks
its sha256 (cached after the first download), seeds a settings folder with `-g --mmsettings` (an empty one crashes
MetaMorpheus), checks that the banner names the release, and runs `dotnet CMD.dll` on each input set below, on Linux.
Each release's output is kept as a workflow artifact for 7 days.

**Not yet here: the checks.** For now the workflow fails only when a release cannot be fetched, does not start, or
cannot search an input. The tests that read the output and check what the engine and datarepo rely on, one function
at a time, come next.

**To add a release:** add its line to `releases.tsv` (the release, the zip's URL, the zip's sha256 as GitHub's release
page gives it) and run the workflow.

## `lfq/`: label-free quantification with match-between-runs

| File | What it is | Bytes | sha256 |
|---|---|---|---|
| `lfq_a.mzML`, `lfq_b.mzML` | MetaMorpheus's own `MetaMorpheus/Test/TestData/TaGe_SA_A549_3_snip.mzML` (MIT; the same git blob), unchanged: one minute (45.20-46.28 min) of an Orbitrap Velos HCD run of A549 cells, 15 MS1 and 147 MS2 scans | 1,566,855 | `3205502773c515e601e2fd2253a442f54311ed56bc1293d550726c5987236c1c` |
| `lfq_c.mzML` | the same file with every third MS2 spectrum (49 of 147) replaced by a single peak of intensity 1, by `tools/metamorpheus-output/ThinMs2.cs` (mzLib 1.0.598); every other scan, scan number and precursor link unchanged | 1,447,145 | `4c9389f44a469eb050ca827b6ddf09e584ecd7ac9a01b0429f3f04524839a321` |
| `TaGe_SA_A549_3_snip.fasta` | MetaMorpheus's own database for that file, 703 human proteins | 635,036 | `2a11eab63e3135da0655c9ec641cc72314e2e7ffd9a977d5b6732b5c27eee035` |
| `lfq_library.msp` | a spectral library MetaMorpheus 1.1.14 wrote once from this search (`WriteSpectralLibrary = true`) | 67,394 | `94dd193b973da54f9996a8088bb3828fe65945bd8cefb0fe1c295c836ba2688d` |
| `lfq_contaminants.xml` | three entries of MetaMorpheus's `MetaMorpheusContaminants_NoUPS.xml`, copied byte for byte with its header and UniProt's CC BY 4.0 copyright block: the keratins this search labels contaminant (Q497I4, P15241, P25691) | 28,621 | `a1f05c3908caac79c087cfe810f3fb2642a649d160f4ae07ff6fd033417f1b92` |
| `ExperimentalDesign.tsv` | `lfq_a` and `lfq_b` condition A (bioreps 1 and 2), `lfq_c` condition B; MetaMorpheus wants each file named with its extension | 100 | `c06e7c1c01b5025762618aad81acbb13dbe928886fd822cc98c0f2daf3f5a300` |
| `tasks/1_CalibrationTask.toml`, `2_GptmdTask.toml`, `3_SearchTask.toml` | the task files the engine wrote for a label-free deposit under `label-free-dda@14` (PXD036532: trypsin, fixed carbamidomethyl C, HCD, match-between-runs on, q-value notch at 0.01), with `MaxThreadsToUsePerFile` 32 lowered to 4 | 2,628 / 4,632 / 3,764 | |

Why three names: the same file under two names gives no match-between-runs rows, because FlashLFQ transfers a peptide
only into a file that did not identify it. Peptides identified only from the blanked scans in `lfq_a` and `lfq_b` are
transferred into `lfq_c`. The library and the contaminant file are passed as further `-d` databases, as production
searches pass them; without a library, `results.txt` lacks the "New databases" block a production search writes, and
the task files' `UpdateSpectralLibrary = true` crashes the library update.

What 1.1.14 gives on this input (the same counts on Windows and on Linux): calibration succeeds on all three files; 318
target PSMs, 84 peptides and 80 protein groups at 1%; 2 of the 80 quantified protein groups labelled contaminant, and
2 peptides with a mixed target/contaminant label; `AllQuantifiedPeaks.tsv` holds 232 MSMS peaks and 29 MBR peaks, all
MBR peaks in `lfq_c`.

**PEP is not trained, on purpose.** MetaMorpheus trains PEP only on a search far larger than this one (more than 1,000
PSMs, and 8 names of this file still did not train it), so every PEP is 0 and every PEP q-value 2, MetaMorpheus's
"not computed". The columns are there and are read; trained values would need a much larger input.

## Known differences on Linux

- **1.1.14 writes the updated spectral library beside `Task3SearchTask`, not in it**, as a file named
  `Task3SearchTask\updateSpectralLibrary_<time>.msp`: `MetaMorpheusTask.cs` builds the path with a hard-coded `\\`
  (line 1477 at 1.1.14, still at master). On Windows the backslash is a separator, so the file lands inside.
- Files MetaMorpheus writes end their lines with LF on Linux and CRLF on Windows, so their sizes and checksums differ
  between the two (the GPTMD database here: 982,007 bytes on Linux, 993,303 on Windows); the content is the same.

`.gitattributes` keeps every file here byte for byte (no line-ending conversion), and the repository's `.gitignore`
makes an exception for the spectra and the FASTA.
