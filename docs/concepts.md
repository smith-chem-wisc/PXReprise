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
| `tmt-dda@1` | pending | TMT-labelled DDA. Not runnable yet: a question that lists it gets a count of the TMT deposits it would unlock |

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
  apart, a protease or modification MetaMorpheus does not know, a label).
- **When the search fails:** if MetaMorpheus reports it could not use a modification or a per-file protease. It does
  not search on without them.

The default, `chemistry = "fixed"`, searches every deposit with MetaMorpheus's defaults, as `@1` and `@2` do.

A profile can also take an experimental design (`[quant] design = "sdrf"`, none of the profiles above yet): before the
search, PXReprise writes MetaMorpheus's `ExperimentalDesign.tsv` from the question's own SDRF for that deposit, or else
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
