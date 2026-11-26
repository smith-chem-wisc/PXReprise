---
title: "0073 One release for the engine and datarepo"
parent: Decisions
nav_order: 73
status: accepted
date: 2026-11-22
decision-makers: the maintainer
---

# One release for the engine and datarepo

## Context and Problem Statement

Since the data repository's code moved here ([0069](0069-datarepo-code-in-pxreprise.md)), one repository has built two
programs, each released on its own:

* **Two workflows.** `release.yml` released the engine on a `v<version>` tag: release notes only, no programs.
  `datarepo-binaries.yml` built `datarepo` for four platforms on a `datarepo-v<version>` tag, into a draft release.
* **Two version lines.** The engine's version is `<Version>` in `src/PXReprise/PXReprise.csproj` (0.4.50).
  datarepo's was its own (`src/Data.Build.props`, stamped from its tag): its last numbers were 1.4.5 and 1.5.0.

A machine that delivers runs both: the engine searches, then calls `datarepo ingest`. Nothing tied the two together. An
engine release's notes had to say which datarepo it needed, and nothing stopped a machine from running any pair.

## Considered Options

* One release and one version for both programs
* Keep two releases, each with its own tag and version line

## Decision Outcome

Chosen option: "One release and one version for both programs", because a machine then installs one thing, and the
version a result records names both programs.

* **One tag and one version.** A release is a `v<version>` tag on `master`. `<version>` is the csproj's `<Version>`,
  bumped by the release's pull request; datarepo has no version of its own. The first joint release is 1.0.0.
* **Both programs on four platforms, in one release.** The release workflow publishes the engine and datarepo
  self-contained for win-x64, linux-x64, osx-arm64 and osx-x64, stamped with the version (`-p:Version`) and the commit
  (`-p:SourceRevisionId`). It runs each with no .NET on `PATH`: the engine's `version` verb must report the version,
  `datarepo --version` must say `datarepo <version>`, and datarepo runs the fixture instance end to end. One archive per
  platform, `pxreprise-<version>-<rid>`, holds a `pxreprise/` and a `datarepo/` folder. The release, "PXReprise
  <version>", carries the four archives and the CHANGELOG section as its notes, after the MetaMorpheus output check and
  the offline tests pass.
* **Every release says what adopting it costs.** Each CHANGELOG section leads with four things: the MetaMorpheus version,
  any change to the machine file, whether the release is safe mid-batch, and **Adopting:** a swap only, a swap and a
  catalog rebuild, or a full re-ingest. The release workflow refuses a section whose lead lines do not say
  `**Adopting:**`.
* **The engine refuses a datarepo from another release.** At a batch's start, the engine reads `datarepo --version`. Any
  version but its own (its `+commit` aside) stops the batch before any deposit, naming both versions.
  `batch status --machine` makes the same check, so a new install is checked before a running batch is stopped.

### Consequences

* Good, because one install, one version and one changelog section describe everything a machine runs, and a delivered
  bundle's engine and datarepo are always one release.
* Good, because each platform's programs are verified by running them before the release is published, as datarepo's
  were.
* Bad, because the `datarepo-v*` tags end. datarepo's last own numbers were 1.4.5 and 1.5.0. Its version now restarts
  at 1.0.0, below them, so across the change a higher number no longer means a newer datarepo.
* Bad, because the catalog id hashes datarepo's version, and datarepo's version is now every release's. Every release,
  even one that changes only the engine, moves the catalog id, so adopting any release costs at least a swap and a
  catalog rebuild.
* Bad, because a machine can no longer move the engine and datarepo separately: a new datarepo needs a release, and a
  new engine needs its datarepo.

## More Information

This record amends three others. [0010](0010-numbered-releases.md): a CHANGELOG section leads with four things, not
three. [0068](0068-rules-on-master.md): there are no `datarepo-v*` tags any more, and a release publishes both programs.
[0069](0069-datarepo-code-in-pxreprise.md): the binaries workflow it brought no longer releases; it checks, on pushes to
`master`, that both programs still build and run on every platform.
