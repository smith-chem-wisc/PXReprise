---
title: "0096 The batch runs ptmQtl's engines"
parent: Decisions
nav_order: 96
status: accepted
date: 2026-11-25
decision-makers: the maintainer
---

# The batch runs ptmQtl's engines

## Context and Problem Statement

ptmQtl's engines (`ptmqtl.site_pairs`, `ptmqtl.pool_pairs`, `ptmqtl.site_traits`) run on stored bundles through
`datarepo run`. Until now an operator ran them by hand. The catalog already serves any ptmQtl artefacts the store holds,
but the batch never ran one, so a deposit delivered got no ptmQtl results unless someone remembered. The maintainer
wanted them on by default, and the per-trait fit in the default too, built now rather than held for a later release.

## Considered Options

* The batch runs the engines at the two points where their inputs are ready: a deposit's delivery, and each publish
* A separate scheduled job beside the batch
* Keep running them by hand

## Decision Outcome

Chosen option: "The batch runs the engines". The inputs are ready exactly there: a bundle exists once its ingest
succeeds, and a pool or a catalog needs every paired deposit, which a publish already waits for.

* **Switched on by the machine file's `[ptmqtl]`**, which names ptmQtl's lists. They are ptmQtl's until the run labels
  (decision 0094) reach the bundle.
* **`site_pairs` after each label-free delivery,** on the deposit's newest bundle (the one the catalog serves). An
  isobaric bundle is never run: it has no occupancy table.
* **Before each publish:**
  * a bounded catch-up of label-free deposits delivered before the engines ran;
  * then `pool_pairs` over every paired deposit, which writes one pooled artefact per species and nothing when its
    members are unchanged.
* **A failure is recorded and logged, never fatal.** The delivery stands, and the publish goes on with the artefacts the
  store holds.
* **The catalog takes the newest pool of a species.** The batch pools again as deposits arrive, so the store keeps each
  earlier pool. A pool whose members all sit in a larger one on the catalog's datasets is that pool's earlier version.
  Pools that do not nest are still refused, as two candidates for one slot always are.
* **`site_traits` comes next,** with its traits writer. It needs ptmQtl's engine change (one-form runs kept, covariates)
  and the mzLib release that change rests on.

### Consequences

* Good, because every label-free deposit gets its ptmQtl results as it is delivered, with no one remembering to.
* Good, because a failed engine run is visible in the deposit's state and its whole output is kept.
* Bad, because a change to ptmQtl's lists gives each bundle a second `site_pairs` artefact, which the catalog refuses
  until the old ones are moved out of the store's `_engine` folder. The labels (0094) end the lists.
