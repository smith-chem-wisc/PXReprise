---
title: "0092 One spectral library per fragmentation type"
parent: Decisions
nav_order: 92
status: accepted
date: 2026-11-24
decision-makers: the maintainer
---

# One spectral library per fragmentation type

## Context and Problem Statement

The batch keeps spectral libraries per organism, per isobaric kit (decision of 2026-10-29) and per enriched PTM class
(v0.4.38). The libraries are to be used for DIA, where every library spectrum is a candidate, so a spectrum of the wrong
kind is harmful. Fragmentation changes a spectrum: HCD and CID give different ion series and intensities, ETD gives c
and z ions, and an ion trap reads fragments at low resolution. MetaMorpheus also stores a low-resolution CID library
spectrum with its XCorr-processed intensities.

Until now nothing kept the types apart. All the chains hold Orbitrap HCD only (checked against each producing deposit's
QC report, 2026-11-24), because the ion-trap profile never had a library and low-resolution TMT searches had theirs
switched off (v0.4.34). An Orbitrap CID or ETD search would have written into the HCD chain.

## Considered Options

* A chain per fragmentation type, keyed from QC's per-file call
* Tag each spectrum with its fragmentation and filter at use
* Keep one chain and refuse non-HCD searches a library

## Decision Outcome

Chosen option: "A chain per fragmentation type". The chain is the unit a search reads and DIA loads, and a library
spectrum records no fragmentation (MSP has no field, and mzLib's MSL reader and merger never select by one).

* **The key:** QC's per-file `fragmentation` call (dissociation type and low-resolution flag) becomes a suffix of the
  chain key, after the organism and the kit. Orbitrap HCD is empty, so today's chains keep their names. Other types:
  `cid`, `etd`, `ethcd`, and for ion-trap MS2 `lowcid` or `ithcd`.
* **Kept apart by how the spectra were scored, not only by how they were acquired.** A chain's update keeps a stored
  spectrum only when it holds more fragments than the new match's score, rounded down, so every spectrum in one chain
  must have been scored the same way: a LowCID score (XCorr-like, large) would replace HCD spectra however much worse.
  QC's call is the profile's own decision of how each file is searched (a profile that searches ion-trap HCD as LowCID
  makes QC call it `LowCID`), and the search writes that call into each file's settings, so the key follows the scoring.
  Ion-trap and Orbitrap spectra never share a chain, and LowCID spectra share only with LowCID.
* **Write, then update:** a chain with no version is written by the search (`WriteSpectralLibrary`); later searches of
  that type update it (`UpdateSpectralLibrary`), as every chain does.
* **Files that disagree get no library.** A search writes one library, so a mixed deposit's spectra would mix.
* **A search QC called nothing for** keeps the profile's own chain, as before.
* **Ion-trap MS2 never has MetaMorpheus write or update its library.** Either switch makes MetaMorpheus match fragments
  at every charge (`ClassicSearchEngine`, `matchAllCharges: WriteSpectralLibrary`), which at ±0.5 Da scores decoys as
  targets (0 PSMs at 1% against 59,028 on PXD034564). With a profile's `engine.ion_trap_library`, the search reads its
  chain with neither switch set (the spectral angle is compared at the search's own product tolerance), and PXReprise
  then builds the next version from the search's PSM table by MetaMorpheus's own update rule (`LibraryFromPsms`), which
  is registered as any version is. Profile versions without the setting keep no library for ion-trap MS2.
* **Measured once, and watched from here** (the maintainer: "we should just keep an eye on this. we don't know about the
  quality of the library that was used"). A mouse ion-trap HCD library built from one deposit (53,184 spectra) was read
  by another deposit's 18-file search: 39% of its target PSMs at 1% got a spectral angle (mean 0.80); PSMs -0.2%,
  peptides -1.9% (2,666 lost, 1,919 gained), protein groups +5.3%.

### Consequences

* Good, because each library holds one kind of spectrum, the kind a DIA search of that type needs.
* Good, because the 18 existing chains are unchanged.
* Bad, because a deposit whose files mix fragmentation types gets no library at all.
