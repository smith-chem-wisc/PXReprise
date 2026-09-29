---
title: Writing a question
nav_order: 5
---

# Writing a question

A question is one [TOML](https://toml.io) file. Keep it in a folder of your own, not inside the PXReprise download.
Start from [`examples/t2d/question.toml`](https://github.com/smith-chem-wisc/PXReprise/blob/master/examples/t2d/question.toml)
and check your edits with `pxreprise validate question.toml`. PXReprise rejects unknown or misspelled settings rather
than ignoring them.

## A complete example

```toml
question = "muscle-ageing"
description = "Skeletal muscle proteome across age"
profiles = ["label-free-dda@2"]

[discover]
keywords = ["sarcopenia", "muscle aging", "skeletal muscle ageing"]
organisms = ["Homo sapiens (human)", "Mus musculus (mouse)"]

[relevance]
require_any = ["\\bsarcopeni", "(muscle|myofib).{0,40}(ag(e|ing)|old|elderly)"]
exclude_if_any = ["cardiac", "\\bheart\\b"]
unless_any = ["skeletal"]

[batch]
run_root = "work/runs"
state_dir = "work/state"
queue = "work/queue.json"
```

## The settings

### Top level

| Setting | Required | Meaning |
|---|---|---|
| `question` | yes | Short name: lower-case words joined by `-`. |
| `description` | no | One sentence, for people. |
| `profiles` | yes | The search methods this question accepts, in order of preference: see [profiles](concepts.md#profiles). Use `["label-free-dda@2"]`. Adding `"tmt-dda@1"` counts TMT deposits without searching them. |

### `[discover]`: finding deposits

| Setting | Required | Meaning |
|---|---|---|
| `keywords` | yes | PRIDE search terms. Each is searched separately and the results are merged. Be broad; the relevance rules narrow it. |
| `organisms` | no | Exact PRIDE spellings, e.g. `"Homo sapiens (human)"`. Leave out, or `[]`, for all organisms. `label-free-dda@2` has databases for human, mouse and rat; deposits of other organisms are counted but not searched. |

### `[relevance]`: which deposits count

Each rule is a list of [regular expressions](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-language-quick-reference),
case-insensitive, matched against the deposit's title, description, sample and data processing protocols, keywords,
experiment types and quantification methods. In TOML, a backslash is written twice: `"\\bt2d\\b"`.

| Setting | Meaning |
|---|---|
| `require_any` | A deposit is relevant only if at least one matches. Leave it out to accept every keyword hit. |
| `exclude_if_any` | A relevant deposit is excluded if any matches... |
| `unless_any` | ...unless one of these also matches. (Only allowed together with `exclude_if_any`.) |
| `decisions` | Optional: a file of hand decisions that override the rules, e.g. `"decisions.tsv"`. |

A decisions file is tab-separated, with exactly this header, and a reason on every line:

```
accession	verdict	reason
PXD012345	exclude	streptozotocin model of type 1, not type 2
PXD067890	include	T2D cohort; the abstract never says "diabetes"
```

`verdict` is `include` or `exclude`. Lines starting with `#` are ignored.

### `[batch]`: where the work goes

Needed for `census --queue` and `batch run`. Relative paths are relative to the question file.

| Setting | Meaning |
|---|---|
| `run_root` | One folder per deposit, holding its results. |
| `state_dir` | The batch's log (`batch.log`), its per-deposit status (`state.json`) and its stop file. |
| `queue` | The list of deposits to process, written by `pxreprise census --queue`. |

### Optional

| Table | Meaning |
|---|---|
| `[holds]` | Deposits to leave alone for now, with a reason: `PXD012345 = "waiting for the authors' sample key"`. |
| `[overlays.<organism>]` | `extra_xml = ["file.xml", ...]`: extra protein databases searched alongside the profile's, for this question only. A plain file name is looked up in the machine's `database_dir`; an absolute path is used as is. |
| `[publish]` | Hooks into a results store. Not needed to run a question. |

## Tips

- Run `pxreprise census` after every change to the rules, and read `census.tsv`. Each deposit's `relevance_evidence`
  column quotes the text that matched, so you can see why each one is in or out.
- Prefer rules that are too broad plus a few `exclude` decisions over rules too narrow to find what you need.
- A question is data: put it under version control next to your analysis.
