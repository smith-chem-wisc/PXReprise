# Changelog

Every release says three things before anything else: whether it needs a different MetaMorpheus, whether your machine
file needs a change, and whether it is safe to update while a batch is part-way through a queue. How to update:
section 7, "Updating", of [Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

A new version of the engine never changes a finished result. Methods live in versioned profiles
(`label-free-dda@2`), and every result's `provenance.json` records the PXReprise version that made it.

## 0.2.0 (2026-09-29)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged; two optional settings are new. Safe mid-batch:
stop the batch, update, run it again.

- **A PRIDE outage no longer loses deposits.** A deposit whose downloads fail through every retry was settled for good
  as `fetch_failed`. It is now `fetch_unavailable`, not settled. The batch finishes the queue, waits, and tries it
  again, up to three passes in all, before settling it. A checksum mismatch or a missing file still settles at once.
- **New optional machine settings:** `fetch_passes` (default 3) and `pass_wait_minutes` (default 30).
- **A server timeout counts as an outage.** An HTTP timeout from PRIDE used to escape the retry handling; it is now
  retried like any other outage.
- **Docs:** how to update an installation (Getting started, section 7; `AGENTS.md`, "Updating").

## 0.1.0 (2026-09-29)

First public release. The batch, census and question tooling; profiles `label-free-dda@1` and `@2` (MetaMorpheus
1.1.11); the `first-run`, `t2d` and `muscle-ageing` examples; the documentation site and tutorial.
