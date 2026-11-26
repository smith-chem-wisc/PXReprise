---
title: "0099 PXReprise 1.0.0's profiles take the PEP q-value at 1%"
parent: Decisions
nav_order: 99
status: accepted
date: 2026-11-26
decision-makers: the maintainer
---

# PXReprise 1.0.0's profiles take the PEP q-value at 1%

## Context and Problem Statement

[0087](0087-one-filter-the-q-value-notch.md) set one identification filter, in the search only, and chose the q-value
notch. The PEP q-value had collapsed protein FDR on MetaMorpheus 1.1.15:
- PXD019690-astro kept 114 target protein groups at 1% under it, against 4,590 under the notch;
- PXD031710 kept 907.

Under the PEP filter, 1.1.15 ranked protein groups by raw PEP. Some decoys get a PEP of exactly 0, as the best targets
do, so decoy groups sorted among the first targets.

MetaMorpheus 1.1.16 (released 2026-11-26) carries #2983. Under the PEP filter it ranks protein groups by their best
peptide's PEP q-value, then by score. Which filter should PXReprise 1.0.0's profiles (`label-free-dda@17`,
`label-free-dda-lowres@11`, `tmt-dda@8`) take?

## Considered Options

* Keep the q-value notch (0087)
* The PEP q-value at 1%

## Decision Outcome

Chosen option: "The PEP q-value at 1%", in every task, set in the search only as 0087 requires. The maintainer, 2026-11-26:
"yes. move to the pep filter".
- **The collapse is gone.** A build of #2983 gave these protein groups at 1%:
  - PXD019690-astro: 4,720 (the notch: 4,590);
  - PXD031710: 6,005;
  - decoy groups at 1.0% of the targets on both.
- **PEP's top end is unchanged** (some decoys still get a PEP of exactly 0), but the PEP q-value tolerates those ties.
- 0087's rule stands: one filter, set in the search, used by every stage. Only the filter it names changes, for these
  versions. Earlier versions keep the notch.

### Consequences

* Good, because PSMs and peptides at 1% are counted by the trained model, as MetaMorpheus 1.1.15's own default intended.
  On PXD058082 (MetaMorpheus 1.1.15's release notes) it gave 52,778 target PSMs at 1%, against 42,392 under the
  notch. Its entrapment estimate at 1%, an upper bound, was 0.57% for PSMs and 1.06% for peptides.
* Good, because the probe takes its profile's filter too, so probe and search judge matches alike.
* Bad, because the counts move against the notch versions. Each 1.0.0 profile is trialled under both filters before its
  PR merges (the 1.1.16 trial), and the peers that read the counts are told before the swap.
* Bad, because a deposit where the PEP model cannot be trained is judged by the notch for those matches, as MetaMorpheus
  does, and `results.txt` says how many.
