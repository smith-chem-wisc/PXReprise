---
title: "0069 The data repository's code lives here, with its history"
parent: Decisions
nav_order: 69
status: accepted
date: 2026-11-13
decision-makers: the maintainer
---

# The data repository's code lives here, with its history

## Context and Problem Statement

The data repository (dataRepo: ingest, catalog, site, MCP) is one generic component of the pipeline
([0041](0041-public-data-architecture.md)). Its code lived in its own repository, `smith-chem-wisc/dataRepo`, beside
its Python, design records, results and manuscript. A contributor proposed moving its code into this repository (#3).
How should it move?

## Considered Options

* Copy the code across as one new commit
* Move the code with its history, as was done for the engine ([0067](0067-smith-is-the-engine-repository.md))

## Decision Outcome

Chosen option: "Move the code with its history", because its history explains its code.

* Pull request #4 (merged) took the contributor's move (#3) and added dataRepo's filtered history (179 commits) as the
  second parent of the move commit, plus its binaries workflow (with its own `datarepo-v*` release tags), its
  `CONTRIBUTING.md` and its contaminant FASTA.
* Python, design records, results, the manuscript and presentations stay in `smith-chem-wisc/dataRepo`, which becomes
  dataRepo's project repository. No Python came across ([0008](0008-no-python.md)).
* dataRepo's work happens on branches named `datarepo/<topic>`.

### Consequences

* Good, because the engine and the data repository change together in one pull request when they must.
* Bad, because dataRepo's design records stay in its own repository, apart from its code.

## More Information

[0073](0073-one-release-for-the-engine-and-datarepo.md) ends the `datarepo-v*` release tags: datarepo is released with the
engine, under one version, and the binaries workflow checks on pushes to `master` that both programs build and run.
