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
* **Write, then update:** a chain with no version is written by the search (`WriteSpectralLibrary`); later searches of
  that type update it (`UpdateSpectralLibrary`), as every chain does.
* **Files that disagree get no library.** A search writes one library, so a mixed deposit's spectra would mix.
* **A search QC called nothing for** keeps the profile's own chain, as before.
* **Ion-trap MS2 still gets no library from MetaMorpheus.** Writing or updating one makes MetaMorpheus match fragments at
  every charge (`ClassicSearchEngine`, `matchAllCharges: WriteSpectralLibrary`), which at ±0.5 Da scores decoys as
  targets (0 PSMs at 1% against 59,028 on PXD034564). Its chains are built by PXReprise from each search's 1% PSMs, and a
  search reads but never writes them; that is a following change, measured on one ion-trap deposit first.

### Consequences

* Good, because each library holds one kind of spectrum, the kind a DIA search of that type needs.
* Good, because the 18 existing chains are unchanged.
* Bad, because a deposit whose files mix fragmentation types gets no library at all.
