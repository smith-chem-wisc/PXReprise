---
title: Writing a question
nav_order: 6
---

# Writing a question

New to this? Start with the [tutorial](tutorial.md), which builds a question step by step; this page is the reference.

A question is one [TOML](https://toml.io) file. Keep it in a folder of your own, not inside the PXReprise download.
Start from [`examples/muscle-ageing/question.toml`](https://github.com/smith-chem-wisc/PXReprise/blob/master/examples/muscle-ageing/question.toml),
the question the tutorial builds, and check your edits with `pxreprise validate question.toml`. PXReprise rejects
unknown or misspelled settings rather than ignoring them.

## A complete example

```toml
question = "muscle-ageing"
description = "Skeletal muscle proteome across age"
profiles = ["label-free-dda@2", "tmt-dda@1"]

[discover]
keywords = ["sarcopenia", "muscle aging", "muscle ageing", "aged muscle", "skeletal muscle"]
organisms = ["Homo sapiens (human)", "Mus musculus (mouse)", "Rattus norvegicus (rat)"]

[relevance]
require_any = [
    "\\bsarcopeni",
    "\\b(muscle|myofib\\w*|satellite cells?)\\b.{0,150}\\b(aging|ageing|aged|elderly|age[- ](related|dependent|associated)|old(er)? (mice|rats|adults|animals|individuals|subjects|people|men|women)|young (and|vs\\.?|versus) old)\\b",
    "\\b(aging|ageing|aged|elderly|age[- ](related|dependent|associated)|old(er)? (mice|rats|adults|animals|individuals|subjects|people|men|women)|young (and|vs\\.?|versus) old)\\b.{0,150}\\b(muscle|myofib\\w*)",
]
exclude_if_any = ["\\b(heart|cardiac|cardiomyocytes?|myocardi\\w*)\\b"]
unless_any = ["\\bskeletal muscle"]
decisions = "decisions.tsv"

[batch]
run_root = "work/runs"
state_dir = "work/state"
queue = "work/queue.json"
```

The [tutorial](tutorial.md) explains why each rule is written the way it is.

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
| `[publish]` | Delivery into a [dataRepo](https://github.com/smith-chem-wisc/dataRepo) repository; needs `datarepo` in the machine file. `manifest`: the dataRepo manifest the batch appends each searched deposit to (created on first use). `command`: run after each ingest, e.g. `["{datarepo}", "publish", "{manifest}", "--site", "{manifest_dir}/site"]` to rebuild a website; `{datarepo}`, `{manifest}` and `{manifest_dir}` are filled in, so the question names no machine path. See `examples/first-run`. Not needed to run a question. |

## Tips

- Run `pxreprise census` after every change to the rules, and read its `review.md`: how much each keyword and rule
  did, a sample of each outcome, and every exclusion, each quoting the text that decided it. `census.tsv` has every
  deposit, for filtering in Excel.
- Put `\b` around words (`\\b` in TOML), so `old` cannot match *fold* and `aged` cannot match *damaged*; give
  proximity windows about 150 characters.
- Prefer rules that are too broad plus a few `exclude` decisions over rules too narrow to find what you need.
- A question is data: put it under version control next to your analysis.
