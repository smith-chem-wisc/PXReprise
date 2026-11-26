---
title: "0100 The reagent probe names the cysteine reagent from spectra, when the probe's cysteines are suspicious"
parent: Decisions
nav_order: 100
status: accepted
date: 2026-11-26
decision-makers: the maintainer
---

# The reagent probe names the cysteine reagent from spectra, when the probe's cysteines are suspicious

## Context and Problem Statement

A search declares how a deposit's cysteines were blocked, its alkylation reagent. Until now that came only from the
deposit's words (ChemistryDetector), or from the default, carbamidomethyl.
- **Words can be wrong or missing.** PXD057499 and PXD057490 were searched with carbamidomethyl, and their spectra
  show iodoethanol on about 75% of cysteines.
- **ptmQtl needs a definitive call, made from spectra, before a search reaches their analysis** (their 055,
  PXR-P29). The labels verb's cysteine verdict ([0094](0094-run-and-search-labels.md)) rested on a 5% share line,
  which they rejected (PXR-P28).

Nothing in MetaMorpheus or mzLib names the reagent (the oracle of 2026-11-25). The maintainer:
- "yes. the reagent probe goes in" (1.0.0);
- "add the reagent probe to the worker when the cysteine count is suspicious".

## Considered Options

* Search the probe's files again with every candidate cysteine modification variable, and count the forms the
  confident cysteine peptides carry
* An open or notch search, read through MetaMorpheus's mass-difference histogram
* GPTMD on the probe files
* Run it on every deposit with a probe, or only where the probe's cysteines are suspicious

## Decision Outcome

Chosen option: the second probe search with ten candidates variable, only where the probe's pooled share of peptides
with C is under 10%.
- **Why the candidate search:** the calibration (13 deposits of known chemistry) called each one right. The rejected
  options:
  - the histogram stays a cross-check only;
  - in GPTMD, decoy sites inflate the counts, and it explains one shift per peptide.
- **Why the 10% line:** on the calibration's deposits, every one whose call mattered read 0.3% to 7.6% in its probe:
  a reagent the search did not assume, a mixture, incomplete or no alkylation. Every one alkylated as its search
  assumed read 10.3% to 21.2%.
- **Where it runs:** wherever the probe runs. The worker runs nearly all probes, and its call reaches the controller
  in its package's state.

**The call:**
- **`reagent`:** a family holds at least 60% of the modified cysteines, with the unmodified share reported apart.
  Families:
  - NEM, d5-NEM and ring-opened NEM are one reagent;
  - carboxymethyl folds into carbamidomethyl unless it leads.
- **`mixed`:** no family does.
- **`none`:** no family reaches 20% of every counted cysteine.
- **`unknown`:** fewer than 100 counted, or the search failed.

**What the call sets:**
- **`reagent`:** the search's alkylation, with source `probe_spectra`. It outranks the deposit's words, and only a
  question's "never alkylated" exception outranks it. The search declares it variable unless one form covers 95% of
  the cysteines.
- **`none`:** no cysteine modification, only where the deposit named no reagent.

### Consequences

* Good, because a deposit whose words name the wrong reagent, or none, is searched with the one its spectra show, and
  a mismatch like PXD057499's is not delivered again.
* Good, because the labels verb reports such a search's cysteine chemistry from the data.
* Good, because a deposit whose cysteines look normal pays nothing.
* Bad, because a suspicious deposit's probe takes about 2.4 times as long: 8 to 38 min on three Orbitrap files, and
  38 min on one ion-trap file.
* Bad, because the thresholds come from 13 deposits. They are to be re-examined as calls accumulate.
* Neutral: deposits already delivered are not searched again by this record. Re-searching those whose call differs
  from their search (PXD057499, PXD057490) goes with the 1.0.0 swap.
