---
title: Decisions
nav_order: 9
has_children: true
---

# Decisions

These pages record the decisions that shape the PXReprise engine: what was decided, why, and what it replaced. Each follows
the minimal template of [MADR](https://adr.github.io/madr/) 4.0.

**Before you change how the engine behaves, read the records that touch your change.** A change that reverses a record
needs a new record that supersedes it.

## Numbering

A record's number is its number in the maintainers' decision log. That log also holds decisions about running the lab's
own batch, about one question's data, and about other projects. Those are not recorded here, so the numbers have gaps.
A record may also cover a neighbouring entry of the log; it says so.

The records dated before 2026-11-14 were backfilled from the log on that day. Their context is shorter than a record
written at the time, and they list the options the maintainer weighed only where the log names them.

Records marked *provisional* are good guesses, made fast on purpose. Each says when to re-examine it.

## Proposing a decision

1. Copy [the template](adr-template.md) to `docs/decisions/NNNN-short-title.md`, with the next number above the highest
   here. The maintainer may renumber it at merge to keep it in step with the log.
2. Keep the front matter: `title` (the number and the decision), `parent: Decisions`, `nav_order` (the number),
   `status: proposed`, `date`, `decision-makers`.
3. Fill in the sections and open a pull request. The discussion happens there.
4. When the maintainer accepts it, its status becomes `accepted` before the merge.
5. Never rewrite an accepted decision. Write a new record, and set the old one's status to
   `superseded by [NNNN](NNNN-short-title.md)`.

## Records

| Number | Decision | Status | Date |
|---|---|---|---|
| [0001](0001-csharp-orchestration-only.md) | C#, orchestration only: mzLib computes, MetaMorpheus searches | accepted | 2026-09-27 |
| [0002](0002-profiles-questions-corpus.md) | Profiles, questions and one shared corpus | accepted | 2026-09-27 |
| [0004](0004-mzlib-conventions.md) | Follow mzLib's conventions for failures, async, the CLI and tests | accepted | 2026-09-28 |
| [0007](0007-profile-versions-never-edited.md) | A profile version is never edited | accepted | 2026-09-29 |
| [0008](0008-no-python.md) | No Python | accepted | 2026-09-29 |
| [0009](0009-limits-belong-to-the-machine.md) | Time and size limits belong to the machine, not the profile | accepted | 2026-09-29 |
| [0010](0010-numbered-releases.md) | Every user-visible change is a numbered release | accepted (amended by 0073) | 2026-09-29 |
| [0011](0011-pipeline-number-definitions.md) | PXReprise owns the definitions of its pipeline numbers | accepted | 2026-09-29 |
| [0013](0013-pride-index-for-discovery-only.md) | PRIDE's search index is for discovery only | accepted | 2026-09-30 |
| [0014](0014-new-engines-join-through-metamorpheus.md) | A new search engine joins as a MetaMorpheus task and a profile | accepted | 2026-09-30 |
| [0015](0015-rank-bigger-first.md) | Rank new deposits bigger first, within limits (provisional) | accepted | 2026-10-05 |
| [0016](0016-disease-and-watch-keywords.md) | Disease keywords need a stated reference group; watch keywords never queue (provisional) | accepted | 2026-10-05 |
| [0018](0018-unsupported-data-waits-named.md) | Data no profile searches correctly waits under a named status (provisional) | accepted | 2026-10-05 |
| [0021](0021-pxreprise-decides-search-chemistry.md) | PXReprise decides each deposit's search chemistry | accepted | 2026-10-05 |
| [0027](0027-metamorpheus-facts-the-search-relies-on.md) | How the engine maps chemistry onto MetaMorpheus | accepted | 2026-10-05 |
| [0029](0029-rebuild-ptm-databases-each-build.md) | Rebuild the PTM-annotated databases at every pipeline build; never add enriched-for PTMs | accepted | 2026-10-07 |
| [0030](0030-pxreprise-determines-the-experiment.md) | PXReprise determines what experiment each deposit is | accepted | 2026-10-07 |
| [0031](0031-quantify-inside-metamorpheus.md) | Quantify inside MetaMorpheus, with match-between-runs and a design, on every search | accepted (default design superseded by 0066) | 2026-10-07 |
| [0032](0032-settings-from-deposit-facts.md) | Decide search settings at download, from the deposit's facts | accepted | 2026-10-07 |
| [0033](0033-split-mixed-deposits.md) | Split mixed deposits into separate searches | accepted | 2026-10-07 |
| [0034](0034-when-evidence-disagrees.md) | When evidence disagrees: spectra first, and tell a person | accepted (amended by 0065) | 2026-10-07 |
| [0035](0035-species-and-databases.md) | Species, databases, spike-ins and the contaminant panel | accepted | 2026-10-07 |
| [0036](0036-one-identification-filter.md) | One identification filter, used everywhere | accepted | 2026-10-07 |
| [0037](0037-whole-search-identifications.md) | Count identifications from the whole search, not per-file FDR | accepted | 2026-10-07 |
| [0038](0038-canonical-proteomes.md) | Search UniProt's canonical proteomes | accepted | 2026-10-07 |
| [0039](0039-catch-up-re-search.md) | Catch up out-of-date searches, one search in four | accepted | 2026-10-07 |
| [0041](0041-public-data-architecture.md) | Public data architecture: add-only bundles, one corpus, a catalog of views | accepted | 2026-10-11 |
| [0043](0043-precursor-window-from-the-probe.md) | An uncalibrated search's mass windows come from the probe's measured offset | accepted | 2026-10-17 |
| [0047](0047-measure-under-full-search-settings.md) | Measure a profile change under the full search's settings before re-searching | accepted | 2026-10-21 |
| [0051](0051-clean-spectral-libraries.md) | Clean the spectral libraries at every registration | accepted | 2026-10-27 |
| [0059](0059-delete-raws-once-calibrated.md) | Delete raw files as soon as their calibration succeeds | accepted | 2026-11-02 |
| [0061](0061-ptms-mimic-the-sample.md) | PTMs in the databases and libraries must mimic the sample | accepted | 2026-11-04 |
| [0064](0064-each-question-builds-its-own.md) | A new question builds its own databases, profiles and libraries | accepted | 2026-11-06 |
| [0065](0065-sdrf-for-every-deposit.md) | PXReprise makes every deposit's SDRF | accepted | 2026-11-07 |
| [0066](0066-sdrf-makes-the-design.md) | PXReprise's SDRF makes the design; the default design retires | accepted | 2026-11-12 |
| [0067](0067-smith-is-the-engine-repository.md) | This repository is the engine's real home | accepted | 2026-11-13 |
| [0068](0068-rules-on-master.md) | Rules on master, and how a release is made | accepted (amended by 0073) | 2026-11-13 |
| [0069](0069-datarepo-code-in-pxreprise.md) | The data repository's code lives here, with its history | accepted (amended by 0073) | 2026-11-13 |
| [0070](0070-record-engine-decisions-as-madr.md) | Record engine decisions as MADR records in this repository | accepted | 2026-11-14 |
| [0072](0072-dia-search-mode.md) | A DIA search mode keyed on the DIA task's own tables | accepted | 2026-11-16 |
| [0073](0073-one-release-for-the-engine-and-datarepo.md) | One release for the engine and datarepo | accepted | 2026-11-22 |
| [0082](0082-pep-q-value-from-the-next-metamorpheus-version.md) | The PEP q-value at 1% from the next MetaMorpheus version | superseded by [0087](0087-one-filter-the-q-value-notch.md) | 2026-11-23 |
| [0087](0087-one-filter-the-q-value-notch.md) | One identification filter, the q-value notch at 1%, set in the search only | accepted (amended by 0099) | 2026-11-23 |
| [0089](0089-mbr-transfers-at-five-percent.md) | Match-between-runs transfers kept below a q-value of 5% | accepted | 2026-11-23 |
| [0091](0091-ptm-databases-are-cumulative.md) | PTM databases are cumulative | accepted | 2026-11-24 |
| [0092](0092-one-spectral-library-per-fragmentation-type.md) | One spectral library per fragmentation type | accepted | 2026-11-24 |
| [0099](0099-pxreprise-1-0-0-profiles-take-the-pep-q-value.md) | PXReprise 1.0.0's profiles take the PEP q-value at 1% | accepted | 2026-11-26 |
