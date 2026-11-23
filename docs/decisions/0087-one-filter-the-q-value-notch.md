---
title: "0087 One identification filter, the q-value notch at 1%, set in the search only"
parent: Decisions
nav_order: 87
status: accepted
date: 2026-11-23
decision-makers: the maintainer
---

# One identification filter, the q-value notch at 1%, set in the search only

## Context and Problem Statement

[0082](0082-pep-q-value-from-the-next-metamorpheus-version.md) chose the PEP q-value at 1% for the profiles made for the
next MetaMorpheus version. That version is 1.1.15. Searched with its profiles, the PEP q-value failed at the protein
level on some deposits:
- MetaMorpheus 1.1.15 ranks protein groups for their FDR by the best peptide's PEP. Where many decoy matches get a PEP of
  exactly 0, decoy groups rank among the first targets, and the protein FDR collapses.
- An isobaric ion-trap deposit (PXD019690) kept 106 target protein groups at 1% under the PEP q-value and 4,590 under
  the q-value notch.
- A label-free ion-trap deposit (PXD031710) kept 907 against 5,838.
- Other deposits did not fail (PXD031782, PXD007683). So whether it fails depends on the deposit, and cannot be
  predicted.

The steps after the search had also grown rules of their own for choosing a 1% set. datarepo picked the PEP q-value
wherever PEP was trained, and so did the PTM statistics' quantified-PSM tier. So a search filtered one way could be read
another way.

## Considered Options

* One filter, the q-value notch at 1%, set in every task of every profile, which every later step reads
* The PEP q-value where it works, and the q-value notch where it fails (per profile, or per deposit)
* Keep 0082: the PEP q-value everywhere

## Decision Outcome

Chosen option: "One filter, the q-value notch at 1%, set in every task of every profile". It replaces 0082.

* **Every profile made for MetaMorpheus 1.1.15 sets it in every task:** `IdentificationFilter = "QValueNotch"` and
  `FilterThreshold = 0.01`. That covers label-free-dda@15, label-free-dda-lowres@9 and tmt-dda@6. A profile never leans
  on MetaMorpheus's default, which in 1.1.15 is the PEP q-value for bottom-up searches.
* **The probe search takes its profile's filter,** and its measures read only what that filter passed. They no longer cut
  at a q-value of their own.
* **Everything downstream reads the search's own filter**, the one `results.txt` names, and never adds a second rule. That
  covers datarepo's 1% set and the PTM statistics' quantified-PSM tier, each cut as the search cut it (at or below the
  threshold).
* **Searches made under earlier profiles keep their filter,** which their provenance records. Up to MetaMorpheus 1.1.14
  that is the q-value and the q-value notch at 1%.
* **A later profile moves to the PEP q-value only by a new decision,** once MetaMorpheus ranks protein groups correctly
  under it.

### Consequences

* Good, because one rule decides what a 1% identification is, from the search to every table built on it.
* Good, because no deposit's protein FDR depends on whether PEP happens to fail on it.
* Bad, because the PEP q-value usually keeps more PSMs and peptides where it does not fail, and that gain is given up.
* Bad, because the PTM statistics' quantified-PSM set shrinks where it used the PEP q-value: about 18% fewer target PSMs
  over the 215 deposits served on 2026-11-23.
