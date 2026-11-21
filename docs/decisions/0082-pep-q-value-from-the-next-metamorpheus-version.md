---
title: "0082 The PEP q-value at 1% from the next MetaMorpheus version"
parent: Decisions
nav_order: 82
status: accepted
date: 2026-11-20
decision-makers: the maintainer
---

# The PEP q-value at 1% from the next MetaMorpheus version

## Context and Problem Statement

[0036](0036-one-identification-filter.md) chose one identification filter for every step: the PEP q-value below 0.01,
with a fallback where PEP cannot be computed. MetaMorpheus 1.1.14 lets a task name its filter (MetaMorpheus #2982), and an
open MetaMorpheus pull request (#2989) gives that filter one threshold. The profiles still search with the q-value notch at
0.01, MetaMorpheus's old default. 0036 did not say when to switch.

## Considered Options

* Switch at the next MetaMorpheus version the engine adopts
* Switch now, on MetaMorpheus 1.1.14
* Keep the q-value notch

## Decision Outcome

Chosen option: "Switch at the next MetaMorpheus version the engine adopts". The filter then changes once, with the version
change that already brings new profile versions.

* **Every profile version made for that MetaMorpheus version names the filter in every task it writes:** the PEP q-value
  at 0.01. It never leans on MetaMorpheus's default.
* **The keys follow the version.** Once #2989 is released, they are `IdentificationFilter = "PepQValue"` and
  `FilterThreshold = 0.01`. Otherwise they are 1.1.14's keys, as its Search window saves them:
  `IdentificationFilter = "PepQValue"`, `PepQValueThreshold = 0.01` and `QValueThreshold = 1.0`.
* **Each search's provenance records its filter and threshold,** as 0036 requires. It also records MetaMorpheus's
  fallback where PEP was not trained, which judges those matches by the q-value notch and says so in `results.txt`.
* **Searches made before the switch keep the q-value notch,** and their provenance says so. Whether to search them again
  is a separate decision.

### Consequences

* Good, because counts and quantities then follow 0036, and one version change carries both changes.
* Bad, because `results.txt`'s count lines then say "pep q-value", so everything that reads them must change with the same
  version.
* Bad, because the store then holds searches made under two filters until the older ones are searched again. Their
  provenance tells them apart.
