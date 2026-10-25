---
title: How it works
nav_order: 4
---

# How it works

PXReprise keeps three things apart: **what** you study, **how** data is searched, and **where** things live on your
computer. Each is its own file.

| | Answers | File | Who writes it |
|---|---|---|---|
| **Question** | What am I studying? Which deposits are relevant? | `question.toml`, in your own folder | you |
| **Profile** | How is one kind of data searched? | `profiles/*.toml`, shipped with PXReprise | the PXReprise developers |
| **Machine** | Where do things live on this computer? | `machines/my-machine.toml` | you, once per computer |

## Questions

A question lists PRIDE keywords and relevance rules: see [Writing a question](questions.md). It names the profiles it
will accept, but it never says how to search. So two questions that take the same deposit get the same search, and
a new question needs no change to the program.

## Profiles

A profile is a complete, versioned search method: which experiments it accepts (for example label-free DDA on an
Orbitrap), the MetaMorpheus release and tasks, the protein databases, the quality gates, and how whole deposits are
handled. Its name carries its version: `label-free-dda@2`. A profile never changes after release; a changed method
is a new version, so every result says exactly which method made it.

| Profile | Status | Accepts |
|---|---|---|
| `label-free-dda@2` | available | label-free DDA, Thermo Orbitrap with HCD, `.raw` files; human, mouse and rat. UniProt databases are downloaded automatically. **Use this one.** |
| `label-free-dda@1` | available | the same search, with the ageing project's own PTM-annotated databases; only runs where those databases exist |
| `label-free-dda@3` | available | `@1`, searched with each deposit's own protease and cysteine chemistry (below) |
| `label-free-dda@4` | available | `@3` on MetaMorpheus 1.1.12, with an experimental design (below), the ageing project's second database release, and MetaMorpheus's contaminant panel |
| `label-free-dda@5` | available | `@4` on UniProt's canonical reference proteomes (one protein per gene) with the ageing project's sites added; larger deposits accepted (120 files, an estimated 3 million MS2 spectra) |
| `label-free-dda@6` | available | `@5`, plus a thiol reagent with no UniMod entry searched with a modification the profile ships (below); the first is NBF-Cl |
| `label-free-dda@7` | available | `@6`, plus a short probe search before the whole deposit is downloaded (below) |
| `label-free-dda@8` | available | `@7` on the ageing project's fourth database release (agingPTM-v4): the same proteomes, with the PTM sites rebuilt from the ageing catalog of 2026-10-17 |
| `label-free-dda@9` | available | `@8`, plus two side reactions of a deposit's own chemistry offered to GPTMD, never searched as variable: ring-opened NEM where NEM blocks the cysteines, and propionamide where the protocol names a gel (below). A deposit with neither is searched exactly as under `@8` |
| `label-free-dda-lowres@1` | available | label-free DDA whose MS2 is read in the ion trap (CID): MetaMorpheus's Search task alone, each file's fragmentation written from its own scans, no spectral library. A deposit is sent here only when its probe files fail the Orbitrap check and nothing else |
| `label-free-dda-lowres@2` | available | `lowres@1`, plus MS2 fragmented by HCD and read in the ion trap (a Fusion, Lumos or Eclipse "HCD-IT" method) |
| `label-free-dda-lowres@3` | available | `lowres@2`, plus the probe search, which measures each deposit's precursor mass offset. The search then accepts and quantifies precursors within a window wide enough for that offset (below). These profiles run no calibration, and an instrument whose masses are a few ppm off loses most of its identifications at MetaMorpheus's 5 ppm |
| `label-free-dda-lowres@4` | available | `lowres@3`, with MS2 fragmented by HCD and read in the ion trap searched as MetaMorpheus's LowCID, its low-resolution path, in the probe and the search. **Not recommended: use `lowres@5`.** Its evidence was measured under the probe's settings, where one test file gave 107 PSMs as HCD and 2,442 as LowCID. In the full search LowCID lost PSMs on every deposit searched both ways (1% to 16%) |
| `label-free-dda-lowres@5` | available | `lowres@3`, with MS2 fragmented by HCD and read in the ion trap taken as LowCID in the probe search only. Under the probe's settings such a deposit can identify nothing as HCD and park; the full search keeps `lowres@3`'s HCD at the low-resolution tolerance. In full searches HCD found more than LowCID in three deposits, and about as much in the deposit the probe change was made for |
| `tmt-dda@1` | pending | TMT-labelled DDA. Not runnable yet: a question that lists it gets a count of the TMT deposits it would unlock |

Every profile but `@2` needs the ageing project's own databases in the machine's `database_dir`, and runs only where they
exist.

**A deposit's own chemistry** (`[engine] chemistry = "deposit"`, `label-free-dda@3`). Before any download, PXReprise reads
which protease made the deposit's peptides, how its cysteines were alkylated, and whether it carries a label.
- **Where it reads them, in this order:**
  - the question's own SDRF;
  - the deposit's SDRF;
  - PRIDE's list of identified modifications and its quantification method;
  - the sample-processing protocol;
  - otherwise MetaMorpheus's defaults: trypsin, carbamidomethyl on C.
- **What it searches with:** the protease it read, set per raw file when the deposit used several. Another alkylant
  replaces carbamidomethyl, and a light/heavy pair such as d0/d5-NEM is searched as two variable modifications.
- **What it records:** each choice and where it came from go into the search's `provenance.json`. A choice read only
  from protocol text is flagged `chemistry_guessed` in the manifest.
- **When it waits instead:** if the deposit cannot be searched as read (two proteases with no way to tell the files
  apart, a protease or modification MetaMorpheus does not know, a label). A thiol reagent with no UniMod entry, such as
  NBF-Cl, waits too, unless the profile models it.
- **A profile can model such a reagent** (`label-free-dda@6`). It ships a MetaMorpheus modification file
  (`[engine] custom_modifications`, under `profiles/lists/`) and maps the reagent to one of its modifications
  (`[thiol_reagents]`). The deposit is then searched with that modification, variable. Before such a search,
  PXReprise copies the file into the MetaMorpheus settings folder the search uses.
- **A profile can offer side reactions to GPTMD** (`label-free-dda@9`, `[chemistry_artifacts]`). They are things the
  chemistry makes likely on some peptides, never on most, so they are never searched as variable:
  - **ring-opened NEM** (`Nethylmaleimide+water on C`, +143.058 Da) where NEM blocks the cysteines: NEM's ring opens
    with water;
  - **propionamide** (`Propionamide on C`, +71.037 Da) where the protocol names a gel (SDS-PAGE, in-gel digestion, gel
    bands): free cysteines meet the gel's acrylamide.
  - Both are typed `Common Artifact` in `profiles/lists/PXReprise_custom_modifications_v2.txt`. Only a deposit whose
    chemistry reads them gets them in its GPTMD list.
  - On two files of each kind, GPTMD placed ring-opened NEM on 7% of the NEM-modified cysteine matches and propionamide
    on 0.25% of the cysteine matches. Before, those masses could be read as other modifications (methyl on K, for one).
- **PXReprise's own proteases** (since 0.4.28, any profile). `profiles/lists/PXReprise_custom_proteases_v1.tsv` holds
  digestion agents mzLib's list lacks, in mzLib's own columns. Before a search that uses one, PXReprise adds it to
  the `proteases_custom.tsv` in the MetaMorpheus settings folder the search uses. Today the list holds one:
  - **`trypsin+chymotrypsin`** cuts after K and R, and after F, W, Y and L except before P. It is meant for the
    chymotrypsin fraction of a MED-FASP digest, where chymotrypsin digests what trypsin left. On one such run, a
    non-specific search found both kinds of cut at either end of the peptides; chymotrypsin alone identified 651 PSMs,
    this protease 7,028.
  - **How a question names it:** in its SDRF, `NT=trypsin+chymotrypsin`, or both trypsin and chymotrypsin in one
    file's cleavage-agent columns.
- **When the search fails:** if MetaMorpheus reports it could not use a modification or a per-file protease. It does
  not search on without them.

The default, `chemistry = "fixed"`, searches every deposit with MetaMorpheus's defaults, as `@1` and `@2` do.

**A probe search** (`[engine] probe_search = true`, `label-free-dda@7`). Before the whole deposit is downloaded, its
probe files (three, even when the whole deposit is already on disk) are searched briefly against every species
database the profile has, with variable phospho and GG. Each
run's species, share of cysteine-containing peptides, and phospho, GG and acetyl-K shares go into the deposit's
experiment record. The spectra win over the text:
- a deposit none of whose probe runs reaches 50 PSMs waits (`waiting_no_identifications`): its peptides are not what
  the search looks for, such as a label the text did not make clear;
- a run whose PSMs are mostly another species waits (`waiting_species`), and so does a run of mixed species
  (`waiting_mixed_species`). A PSM of a contaminant-panel protein, such as a human skin keratin from handling, counts
  for no species. A run is judged only when it has at least 500 PSMs specific to one species: in a run with little
  protein, skin proteins from handling can outnumber the sample's own;
- a whole proteome expected to be carbamidomethylated, whose runs show under 2% cysteine-containing peptides, waits
  (`waiting_alkylation_check`): its cysteines were blocked some other way, or not at all;
- an enrichment the text did not state is recorded beside the text's call, and flagged in the manifest.

Each of these waits is listed for a person in `decisions_needed.tsv`. A deposit that passes is searched as under `@6`.

**Mass windows without calibration** (`[engine] precursor_window = "probe"`, `label-free-dda-lowres@3`).
- **Why:** the `label-free-dda` profiles calibrate each file before they search it, so MetaMorpheus's 5 ppm windows suit
  them. The `lowres` profiles do not calibrate. On a deposit whose precursor masses were 8.7 ppm off, `lowres@2`
  identified 0.37% of the spectra. FlashLFQ does not correct an offset either: it looks for a peptide's MS1 peaks within
  its window of the theoretical mass.
- **How:** the probe search takes precursors within ±20 ppm and measures each probe run's median offset. The search
  then sets both windows to 5 ppm beyond the largest offset, rounded up, at most ±20 ppm. When no offset exceeds 2 ppm,
  MetaMorpheus's own windows are kept, because a wider window costs identifications on a deposit with no offset.
- **Where it is recorded:** each search's `provenance.json` (`precursor_window`) records the offsets and the window.

A profile can also take an experimental design (`[quant] design = "sdrf"`, `label-free-dda@4` onward and every `lowres`
profile): before the search, PXReprise writes MetaMorpheus's `ExperimentalDesign.tsv` from the question's own SDRF for that deposit, or else
from the deposit's, so quantification knows the conditions and replicates. Without one, every file is quantified as its
own sample.

A deposit that no available profile accepts (DIA, timsTOF, TMT, metabolic labelling, ...) is not forced through an
unsuitable method. It is recorded as *waiting on* that capability, so a census tells you what each missing capability
would unlock.

## The machine file

Paths, thread count and download settings for one computer: see
[`machines/example.toml`](https://github.com/smith-chem-wisc/PXReprise/blob/master/machines/example.toml). Nothing in
it changes a scientific result.

## What happens to each deposit

`pxreprise census` does steps 1 and 2 for a whole question, with no downloads. `pxreprise batch run` does every step,
one deposit at a time. It downloads the next deposit while the current one is being searched.

1. **Discover.** Search PRIDE for each keyword, and apply the question's relevance rules to each deposit's title,
   description, protocols and keywords.
2. **Screen and route.** Read the deposit's live PRIDE record: acquisition mode, labelling, instrument, enrichment.
   Send it to the first of the question's profiles that accepts it, or record what it waits on.
3. **Size gates.** Whole deposits only: a deposit with more raw files than the profile allows is *deferred*, never
   subsampled, because choosing which runs to keep is a scientific decision.
4. **Probe.** Download three files (the median by size, and the first and last by name) and check them. This catches
   deposits whose files are not what the record says, before downloading everything.
5. **Fetch.** Download every raw file, with retries, and record each file's size and SHA-256.
6. **Quality check.** Every file must pass the profile's gates (for example, MS2 spectra read in the Orbitrap).
   A blank or failed injection can be excluded, with a record.
7. **Search.** One MetaMorpheus run over the whole deposit: calibration, GPTMD (finding modifications), search, and
   label-free quantification with match-between-runs.
8. **QC report.** A readable report (`report.html`) and its data.
9. **Clean up.** Delete the raw files; keep the results and every record.

## Provenance

Every step writes a `provenance.json`: its inputs, with checksums, the exact commands, the versions of PXReprise,
mzLib and MetaMorpheus, and anything unusual that happened (a retried download, an excluded file). Anyone can see
how each number was produced.

## What PXReprise is built on

PXReprise does orchestration, policy and record-keeping only. File reading, chemistry and quantification come from
[mzLib](https://github.com/smith-chem-wisc/mzLib); searching is [MetaMorpheus](https://github.com/smith-chem-wisc/MetaMorpheus).
