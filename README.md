# PXReprise

Reprocess public ProteomeXchange/PRIDE proteomics data at scale, for any biological question.

PXReprise is orchestration: it finds deposits, decides which can be searched and how, searches them with MetaMorpheus
and records what it did. Chemistry, file readers, SDRF, quantification and statistics come from
[mzLib](https://github.com/smith-chem-wisc/mzLib); search is [MetaMorpheus](https://github.com/smith-chem-wisc/MetaMorpheus).

## The idea

| | what it is | where it lives |
|---|---|---|
| **Profile** | how one kind of data is searched: accepted acquisition, MetaMorpheus version and tasks, databases, contaminants, quantification, deposit rules, QC gates | `profiles/` in this repo, versioned (`label-free-dda@1`) |
| **Question** | what is studied: PRIDE keywords, organisms, relevance rules plus hand decisions, allowed profiles, traits, holds | a `question.toml` in the question's own project folder |
| **Shared corpus** | search results keyed by (accession, profile version, engine version) | machine configuration; reused by every question |

Routing sends each deposit to the first of the question's profiles that accepts its acquisition. A deposit no
available profile takes is recorded as waiting on a capability (for example `dia`, `timstof`, or a `pending` profile
such as `tmt-dda@1`), so a census says what each capability would unlock. A new question is a new file, never an
engine change.

## Use

```
pxreprise version                          # tools, mzLib pin, verbs
pxreprise profiles                         # the profiles this build ships
pxreprise validate <question.toml>         # check a question against its schema and the profiles
pxreprise census <question.toml> [--out D] # discovery + routing, no downloads: census.tsv, summary.json, provenance.json
```

Every command prints one JSON envelope on stdout (`{"ok":true,"data":...}` or `{"ok":false,"error":{"type","message"}}`)
and exits 0 (ok), 1 (handled failure; `ServiceUnavailable` means retry later) or 2 (usage).

## Build and test

```
dotnet build
dotnet test --filter "Category!=ExternalService"   # offline, required
dotnet test --filter "Category=ExternalService"    # live PRIDE canaries; skipped when PRIDE is down
```

.NET 10. mzLib 1.0.592 from NuGet, the same version MetaMorpheus 1.1.11 uses.

## Status

Phase PLAN. Working: profiles, questions, acquisition classification, relevance, routing and `census`. Next:
running one known dataset end to end, and matching the aging pipeline's counts (`design/DESIGN.md` section 7).
