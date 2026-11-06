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

**Which one you can run.**
- **On a new installation: `label-free-dda-uniprot@1`** (since 0.4.41). It is today's label-free method, the settings of
  `label-free-dda@14` on MetaMorpheus 1.1.13, on UniProt proteomes the engine downloads itself the first time it needs them.
  They are reviewed entries only: human 20,414, mouse 17,279, rat 8,235 on 2026-11-06, against 20,652, 21,872 and 21,416 in
  the one-protein-per-gene sets the ageing project's databases start from, so rat loses the most.
- **`label-free-dda@2`** also downloads its databases, but it is the older method, on MetaMorpheus 1.1.11: no probe search, no
  reading of each deposit's chemistry and no experimental design.
- **A downloaded database is identified by its checksum, not by the profile.** UniProt changes, so two machines that download
  at different times can hold different proteins under one profile. Each search records the file's date and SHA-256, so compare
  those before pooling results.
- **Where the ageing project's databases exist:** the newest of each kind, which is what that project runs today:
  `label-free-dda@14`, `label-free-dda-lowres@7` and `tmt-dda@3`, all on MetaMorpheus 1.1.13. Every other profile names that
  project's PTM-annotated databases in the machine's `database_dir`. They are not published, so without them these profiles
  cannot search.
- **Older versions stay shipped** so that a question can keep naming them and the deposits searched under them are not
  searched again. Start no new search under a MetaMorpheus 1.1.12 profile (`@4` to `@11`, `lowres@1` to `lowres@5`): since
  0.4.33 the engine's mzLib keeps gaps in an SDRF's replicate numbers, and 1.1.12 refuses such a design.

`pxreprise profiles` lists what your installation ships.

| Profile | Status | Accepts |
|---|---|---|
| `label-free-dda@2` | available | label-free DDA, Thermo Orbitrap with HCD, `.raw` files; human, mouse and rat. UniProt databases are downloaded automatically. The older method: `label-free-dda-uniprot@1` runs today's |
| `label-free-dda@1` | available | the same search, with the ageing project's own PTM-annotated databases; only runs where those databases exist |
| `label-free-dda@3` | available | `@1`, searched with each deposit's own protease and cysteine chemistry (below) |
| `label-free-dda@4` | available | `@3` on MetaMorpheus 1.1.12, with an experimental design (below), the ageing project's second database release, and MetaMorpheus's contaminant panel |
| `label-free-dda@5` | available | `@4` on UniProt's canonical reference proteomes (one protein per gene) with the ageing project's sites added; larger deposits accepted (120 files, an estimated 3 million MS2 spectra) |
| `label-free-dda@6` | available | `@5`, plus a thiol reagent with no UniMod entry searched with a modification the profile ships (below); the first is NBF-Cl |
| `label-free-dda@7` | available | `@6`, plus a short probe search before the whole deposit is downloaded (below) |
| `label-free-dda@8` | available | `@7` on the ageing project's fourth database release (agingPTM-v4): the same proteomes, with the PTM sites rebuilt from the ageing catalog of 2026-10-17 |
| `label-free-dda@9` | available | `@8`, plus two side reactions of a deposit's own chemistry offered to GPTMD, never searched as variable: ring-opened NEM where NEM blocks the cysteines, and propionamide where the protocol names a gel (below). A deposit with neither is searched exactly as under `@8` |
| `label-free-dda@10` | available | `@9`, with GPTMD adding a site only where placing the modification improves the match's score (MetaMorpheus's `ImprovedScoreFilter`; below) |
| `label-free-dda@11` | available | `@10` on agingPTM-v5: v4 without its GPTMD-added hydroxylation on P and K, which was often oxidised Met or Trp placed on the wrong residue. UniProt's own hydroxyproline and hydroxylysine stay |
| `label-free-dda@12` | available | `@11` on MetaMorpheus 1.1.13 and agingPTM-v6, with cleavage-blocking modifications respected in every task (below). Its deliveries need datarepo 1.4.0 or newer |
| `label-free-dda@13` | available | `@12` on agingPTM-v7, the first regular databases: UniProt's own PTM sites stay only where a deposit not enriched for that class identifies them. GPTMD also offers formyl on S and T, and no longer malonyl on K |
| `label-free-dda@14` | available (0.4.40) | `@13` without MetaMorpheus's individual-file tables. Identifications and quantification are unchanged. Until a MetaMorpheus release writes each file's `MS2 Scans` and `Precursors` lines without those tables (MetaMorpheus #2955), dataRepo has neither per run for these deposits; dataset totals are unchanged. **The ageing project's current label-free method** |
| `label-free-dda-uniprot@1` | available (0.4.41) | `@14`'s settings, unchanged, on UniProt reviewed proteomes the engine downloads, with UniProt's own PTM sites and none added. **Use this one on a new installation.** Not the same search as `@14`, so its results are not pooled with it as one method. A question grows its own PTM sites from its own catalog later, under a new version of its own profile |
| `label-free-dda-lowres@1` | available | label-free DDA whose MS2 is read in the ion trap (CID): MetaMorpheus's Search task alone, each file's fragmentation written from its own scans, no spectral library. A deposit is sent here only when its probe files fail the Orbitrap check and nothing else |
| `label-free-dda-lowres@2` | available | `lowres@1`, plus MS2 fragmented by HCD and read in the ion trap (a Fusion, Lumos or Eclipse "HCD-IT" method) |
| `label-free-dda-lowres@3` | available | `lowres@2`, plus the probe search, which measures each deposit's precursor mass offset. The search then accepts and quantifies precursors within a window wide enough for that offset (below). These profiles run no calibration, and an instrument whose masses are a few ppm off loses most of its identifications at MetaMorpheus's 5 ppm |
| `label-free-dda-lowres@4` | available | `lowres@3`, with MS2 fragmented by HCD and read in the ion trap searched as MetaMorpheus's LowCID, its low-resolution path, in the probe and the search. **Not recommended: use `lowres@5`.** Its evidence was measured under the probe's settings, where one test file gave 107 PSMs as HCD and 2,442 as LowCID. In the full search LowCID lost PSMs on every deposit searched both ways (1% to 16%) |
| `label-free-dda-lowres@5` | available | `lowres@3`, with MS2 fragmented by HCD and read in the ion trap taken as LowCID in the probe search only. Under the probe's settings such a deposit can identify nothing as HCD and park; the full search keeps `lowres@3`'s HCD at the low-resolution tolerance. In full searches HCD found more than LowCID in three deposits, and about as much in the deposit the probe change was made for |
| `label-free-dda-lowres@6` | available | `lowres@5` on MetaMorpheus 1.1.13, with cleavage-blocking modifications respected. Its databases stay agingPTM-v3: v4's added sites cost an ion-trap run 4% of its PSMs, and the later releases have not been measured on ion-trap data |
| `label-free-dda-lowres@7` | available (0.4.40) | `lowres@6` without the individual-file tables, as `@14`. **The ageing project's current ion-trap method** |
| `tmt-dda@1` | pending | TMT-labelled DDA. Not runnable: a question that lists it gets a count of the TMT deposits it would unlock. Use `tmt-dda@2` |
| `tmt-dda@2` | available (0.4.35) | Isobaric-labelled DDA (TMT, iTRAQ) on MetaMorpheus 1.1.13: the kit read from each file's spectra, MS2 or SPS-MS3 reporters, Calibration then Search (no GPTMD), reporter-ion quantification on a channel design from the SDRF (else one plex per file, flagged `no_channel_design`), a spectral library per kit. Its deliveries need datarepo 1.4.1 or newer |
| `tmt-dda@3` | available (0.4.40) | `tmt-dda@2` on the agingPTM-v7 databases, with a deposit's enriched PTM classes searched as variable modifications. Phospho (S, T, Y), acetyl-K, succinyl-K and GG-K are variable in every task when the deposit enriches them, with up to 3 modifications a peptide. Under `tmt-dda@2` an enriched deposit waits (`waiting_isobaric_enriched`), because its sites would be searched only where the database annotates one. **The ageing project's current isobaric method** |

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
- **A profile can name GPTMD's filters** (`label-free-dda@10`, `[engine] gptmd_filters`). Without one, GPTMD adds every
  placement that fits a mass difference. With `ImprovedScoreFilter` it adds a site only where placing the modification
  raises the match's score. On three test deposits that cut the share of GPTMD's sites that were false from 4-13% to
  1-4%, for under 4% fewer peptides. A filter name MetaMorpheus does not know is refused when the profile loads,
  because MetaMorpheus itself would ignore it without a warning.
  - On two files of each kind, GPTMD placed ring-opened NEM on 7% of the NEM-modified cysteine matches and propionamide
    on 0.25% of the cysteine matches. Before, those masses could be read as other modifications (methyl on K, for one).
- **A profile can take modifications out of GPTMD's list** (`label-free-dda@13`, `[engine] gptmd_drop_mods`). MetaMorpheus's
  default list is every Common Biological, Common Artifact and Metal entry of its modification file, so a class the data
  refuse could not otherwise leave it.
- **A profile can respect cleavage-blocking modifications** (`label-free-dda@12`,
  `RespectCleavageBlockingModifications`). No peptide then ends at a cut on a modification that blocks the protease, such as
  citrulline, acyl-K or GG-K after trypsin, and GPTMD no longer places one there.
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
from the deposit's, so quantification knows the conditions and replicates. With no usable SDRF it writes a default design,
one condition with each file its own replicate, and the search says so. An isobaric profile does the same with
`TmtDesign.txt`, and a search on its default design is flagged `no_channel_design`.

**Spectral libraries** (`[engine] spectral_library = true`). A search that has a library reads the organism's current
version, and adds its own spectra as the next version. `pxreprise library list` shows them.
- **Enriched data keep out of the regular library** (since 0.4.38). An enriched deposit's spectra of the PTM class it
  enriches go to a chain of that class's own (`mouse_phospho`), and a search reads only the regular chain.
- **Each version is cleaned as it is written** (since 0.4.32): spectra whose cut C-terminus carries a modification that
  blocks trypsin, and GPTMD's hydroxylation on P or K, are left out.
- **Isobaric data have a chain per kit** (`mouse_tmt10`), starting blank, because every labelled peptide's mass is shifted.
- **Low-resolution MS2 writes and reads no library.** At the ion trap's tolerance, library matching scores decoys as it
  does targets.

A deposit that none of the question's profiles can take (DIA, timsTOF, metabolic labelling, or TMT when the question names
only `tmt-dda@1`, ...) is not forced through an unsuitable method. It is recorded as *waiting on* that capability, so a
census tells you what each missing capability would unlock.

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
   deposits whose files are not what the record says, before downloading everything. A profile with a probe search also
   searches them briefly (above).
5. **Fetch.** Download every raw file, with retries, check each against PRIDE's checksum and for the error text EBI can
   write into a file it serves, and record each file's size and SHA-256.
6. **Quality check.** Every file must pass the profile's gates (for example, MS2 spectra read in the Orbitrap).
   A blank or failed injection can be excluded, with a record.
7. **Search.** One MetaMorpheus run over the whole deposit, with the profile's tasks. A `label-free-dda` profile runs
   calibration, GPTMD (finding modifications), search, and label-free quantification with match-between-runs. A `lowres`
   profile runs the search alone. A `tmt-dda` profile runs calibration and search, with reporter-ion quantification. Since
   0.4.35 each raw file is deleted as soon as it has been calibrated.
8. **QC report.** The quality data (`qc_payload.json`), and a readable report (`report.html`) where the machine file names the
   optional `qc_python`.
9. **Clean up.** Delete any raw files left; keep the results and every record.
10. **Deliver,** if the question has a `[publish]` table and the machine names `datarepo`: add the deposit to the manifest,
    run `datarepo ingest`, then delete the search outputs nothing reads. Only `INGEST rc=0` means delivered. The website or
    other publish step runs on a schedule, not after each deposit.

## Provenance

Every step writes a `provenance.json`: its inputs, with checksums, the exact commands, the versions of PXReprise,
mzLib and MetaMorpheus, and anything unusual that happened (a retried download, an excluded file). Anyone can see
how each number was produced.

## What PXReprise is built on

PXReprise does orchestration, policy and record-keeping only. File reading, chemistry and quantification come from
[mzLib](https://github.com/smith-chem-wisc/mzLib); searching is [MetaMorpheus](https://github.com/smith-chem-wisc/MetaMorpheus);
the repository and website are [dataRepo](https://github.com/smith-chem-wisc/dataRepo).
