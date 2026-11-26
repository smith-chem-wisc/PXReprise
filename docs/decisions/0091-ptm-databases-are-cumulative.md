---
title: "0091 PTM databases are cumulative"
parent: Decisions
nav_order: 91
status: accepted
date: 2026-11-24
decision-makers: the maintainer
---

# PTM databases are cumulative

## Context and Problem Statement

Each agingPTM database adds to a UniProt proteome the biological PTM sites that GPTMD found in the deposits PXReprise
searched (decision 0029): a site goes in when the deposits that identify it at 1% FDR, unambiguously localized, sum to
at least 5 PSMs.

Until now each version read its sites from the question's current catalog, and a catalog serves only the deposits the
question includes today. When a question later excludes a deposit, for example because it turns out to have no aging
factor, the deposit leaves the catalog. Its bundle stays in the store, but its sites left the next database with it.

agingPTM-v9 lost 3,250 of agingPTM-v8's sites, and 3,236 of them only because 56 deposits had been excluded as out of
scope. Since agingPTM-v3, about 4,260 biological sites were lost that way. Nothing was wrong with those searches.

## Considered Options

* Build each database from every kept bundle, not only the catalog's (cumulative evidence)
* Carry the previous version's site list forward and add the new sites
* Keep building from the catalog alone

## Decision Outcome

Chosen option: "Build each database from every kept bundle". A deposit's evidence is re-read from its own data at every
build, so the rules always apply to all of it.

* **Evidence:** the question's catalog, plus the newest kept bundle of every deposit in the store that the catalog no
  longer serves. `tools/ptm-db/EvidenceCatalog.cs` writes a copy of the catalog's views with those bundles added, in the
  catalog's own form, and `BuildPtmDb.cs` reads it unchanged.
* **A deposit's evidence is its newest bundle,** so a re-search replaces it.
* **The selection rules are unchanged:** the biological classes, at least 5 PSMs summed, the residue on the current
  sequence, UniProt's own sites, and no sites of a class from a deposit enriched for it.
* **Evidence leaves only by a stated rule:** a deposit listed in `data/ptm-db/evidence_dropped.tsv`, with the quality
  problem that removes it. The first list holds 7: a deposit with mouse runs searched as human, ancient bone, a
  dimethyl-labelled deposit searched label-free, and four TMT deposits held because their profile does not fit their
  chemistry.
* **Not added:** a deposit split into served parts (its parts are served).
* First used for agingPTM-v10.

### Consequences

* Good, because a site stays once found, unless a stated rule removes it, and the reason is on record.
* Good, because the evidence is re-read from the bundles at every build, not copied from an earlier output, so a change
  of rule applies to every site.
* Bad, because a build now depends on the store keeping its bundles. The store is add-only, so it does.
* Neutral: a question's own scope still decides what its catalog serves. Only the PTM databases read past it.
