---
title: "0010 Every user-visible change is a numbered release"
parent: Decisions
nav_order: 10
status: accepted
date: 2026-09-29
decision-makers: the maintainer
---

# Every user-visible change is a numbered release

## Context and Problem Statement

People run PXReprise in long batches that last days or weeks. They need to know what changed, and whether they can
update in the middle of a batch.

## Considered Options

* Users follow `master`
* Numbered releases, each with a changelog section

## Decision Outcome

Chosen option: "Numbered releases", because each running batch can then name the exact engine it runs.

* A user-visible change bumps `<Version>` in `src/PXReprise/PXReprise.csproj` and adds a `CHANGELOG.md` section.
* Each section leads with three things: the MetaMorpheus version, any change to the machine file, and whether the
  release is safe to install in the middle of a batch.
* Publishing refuses a version with no dated section.
* How to update is documented (`docs/getting-started.md`, `AGENTS.md`).

### Consequences

* Good, because a batch's provenance names the release that made each result.
* Bad, because even a small fix needs a version bump and a changelog line.

## More Information

Releases were first published by a script that copied a snapshot into this repository. Since this repository holds the
real history, a release is a version bump by pull request and then a `v<version>` tag
([0067](0067-smith-is-the-engine-repository.md), [0068](0068-rules-on-master.md)).

[0073](0073-one-release-for-the-engine-and-datarepo.md) amends the lead: each section now leads with four things, the
three above and **Adopting:** (a swap only, a swap and a catalog rebuild, or a full re-ingest), and the release workflow
refuses a section whose lead does not say it.
