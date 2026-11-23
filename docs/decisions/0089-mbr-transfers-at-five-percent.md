---
title: "0089 Match-between-runs transfers kept below a q-value of 5%"
parent: Decisions
nav_order: 89
status: proposed
date: 2026-11-23
decision-makers: the maintainer
---

# Match-between-runs transfers kept below a q-value of 5%

## Context and Problem Statement

FlashLFQ's match-between-runs borrows a peptide's signal into a file where the peptide was not identified. Its error
control (PIP-ECHO, mzLib #802) judges each file's transfers against that file's own decoys:
- it ranks the file's candidate transfers by PEP;
- at each depth it estimates the error rate as (1 + decoys so far) / candidates so far;
- it keeps a transfer whose q-value is below the threshold.

MetaMorpheus passes FlashLFQ a threshold of 1%. At 1%, a file whose decoys run a little over 1 in 100 at every depth
keeps no transfer at all, and so does a file with 100 candidates or fewer.

On PXD000279 (143 files), 27 files kept no transfer at 1%. Estimated over all files together, those 27 carry about twice
the decoy rate of the rest: worse, but not without good transfers. mzLib's own FlashLFQ default is 5%.

## Considered Options

* Keep transfers below a q-value of 5% in the label-free profiles
* Keep 1%
* Estimate the transfer error rate over all files together, instead of file by file (an mzLib change)

## Decision Outcome

Chosen option: "Keep transfers below a q-value of 5%". It is one search setting, with no code change in mzLib.

* **label-free-dda@15 and label-free-dda-lowres@9 set `MbrFdrThreshold = 0.05` in the Search task.** tmt-dda@6 runs no
  match-between-runs and is unchanged.
* **Our counts of kept transfers follow the search's threshold:** the provenance's `mbr` block (`mbr_kept`,
  `mbr_fdr_threshold`) and the QC payload's per-file `mbr_kept`. datarepo already reads `mbr_fdr_threshold` from the
  provenance.
* **Measured on PXD000279:**
  - files with no transfer fell from 27 to 2, and those 2 carry about 22% decoys among their transfers;
  - kept transfers rose from 103,406 to 253,952.

  **On PXD031782 (12 files):** files with no transfer fell from 3 to 0.

### Consequences

* Good, because a file is refused all its transfers only when its transfers are poor, not when its decoys fall just over
  1 in 100.
* Bad, because about 1 kept transfer in 20 is estimated to sit on the wrong peak, against about 1 in 100 at 1%. The
  peptides themselves are still identified at 1% in the files they were identified in, so the extra error is in the
  borrowed quantities. Whether it suits the differential statistics is the quantification owners' to judge; this
  decision is revisited if they object.
* Neutral: a transfer rule that is stricter with weaker files without cutting them off is still open, as an mzLib change.
