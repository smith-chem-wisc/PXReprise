---
title: "0068 Rules on master, and how a release is made"
parent: Decisions
nav_order: 68
status: accepted
date: 2026-11-13
decision-makers: the maintainer
---

# Rules on master, and how a release is made

## Context and Problem Statement

With this repository the engine's real home ([0067](0067-smith-is-the-engine-repository.md)), `master` needs rules that
keep it buildable and keep its history readable.

## Considered Options

Not recorded as options; the maintainer set each rule.

## Decision Outcome

* Pull requests only; no direct pushes to `master`.
* No approvals required for now.
* The check `build and offline tests (required)` must pass, with no bypass.
* Codecov patch coverage of 90%, with an admin bypass. It is active once an organisation admin enables Codecov.
* `v*` and `datarepo-v*` tags: admins only.
* Merge commits only; squash and rebase merges are turned off.
* **Releases:** a version bump and a dated CHANGELOG section go in by pull request
  ([0010](0010-numbered-releases.md)); then a `v<version>` tag. The release workflow builds, tests and publishes the
  release.

### Consequences

* Good, because `master` always builds and passes its offline tests, and each contribution keeps its own commits.
* Bad, because even a one-line fix needs a branch and a pull request.

## More Information

[0073](0073-one-release-for-the-engine-and-datarepo.md) makes one release for the engine and datarepo: a `v<version>`
tag publishes both programs, and the `datarepo-v*` tags end.
