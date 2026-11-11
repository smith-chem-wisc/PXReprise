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
profiles = ["label-free-dda-uniprot@1", "tmt-dda@1"]

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
| `profiles` | yes | The search methods this question accepts, in order of preference: see [profiles](concepts.md#profiles). On a new installation use `["label-free-dda-uniprot@1"]` (since 0.4.41; `label-free-dda@2` is the older method); adding `"tmt-dda@1"` counts TMT deposits without searching them. Where the ageing project's databases exist, name the newest of each kind first: `["label-free-dda@14", "label-free-dda-lowres@8", "tmt-dda@4"]`. When a newer version comes out, put it first and keep the old ones listed, or the deposits searched under them become due for a catch-up re-search (where the machine's `catch_up_every` is on). |

### `[discover]`: finding deposits

| Setting | Required | Meaning |
|---|---|---|
| `keywords` | yes | PRIDE search terms. Each is searched separately and the results are merged. Be broad; the relevance rules narrow it. |
| `disease_keywords` | no | More search terms, for diseases rather than the question's own subject (e.g. `["Alzheimer", "Parkinson"]`). A deposit found **only** by these is queued only if its PRIDE text names a reference group (control, healthy, untreated, vehicle, sham, wild type, placebo, age-matched, young, ...); otherwise it goes to the watch list as `no_reference_group_found`. The check is a guess from text: a cell-line control counts. A deposit also found by `keywords` keeps the normal screen. |
| `watch_keywords` | no | Search terms whose deposits are found, screened and listed in the census's `watch.tsv`, but **never queued**: a place to look for a second dataset without searching it. |
| `organisms` | no | Exact PRIDE spellings, e.g. `"Homo sapiens (human)"`. PRIDE can spell one species several ways, so list each: `"Mus musculus (mouse)"` and `"Mus musculus"`, `"Rattus norvegicus (rat)"` and `"Rattus norvegicus"`. Leave out, or `[]`, for all organisms. The profiles have databases for human, mouse and rat; deposits of other organisms are counted but not searched. |

A keyword may appear in only one of `keywords`, `disease_keywords` and `watch_keywords`.

### `[relevance]`: which deposits count

Each rule is a list of [regular expressions](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-language-quick-reference),
case-insensitive, matched against the deposit's title, description, sample and data processing protocols, keywords,
experiment types and quantification methods. In TOML, a backslash is written twice: `"\\bsarcopeni"`.

| Setting | Meaning |
|---|---|
| `require_any` | A deposit is relevant only if at least one matches. Leave it out to accept every keyword hit. |
| `exclude_if_any` | A relevant deposit is excluded if any matches... |
| `unless_any` | ...unless one of these also matches. (Only allowed together with `exclude_if_any`.) |
| `decisions` | Optional: a file of hand decisions that override the rules, e.g. `"decisions.tsv"`. |

A decisions file is tab-separated, with exactly this header, and a reason on every line:

```
accession	verdict	reason
PXD012345	exclude	liver study; sarcopenia named only as a downstream consequence
PXD067890	include	multi-organ ageing study; skeletal muscle is one of the organs measured
```

`verdict` is `include` or `exclude`. Lines starting with `#` are ignored.

### `[batch]`: where the work goes

Needed for `census --queue` and `batch run`. Relative paths are relative to the question file.

| Setting | Meaning |
|---|---|
| `run_root` | One folder per deposit, holding its results, and `decisions_needed.tsv`: the deposits waiting on a person. |
| `state_dir` | The batch's log (`batch.log`), its per-deposit status (`state.json`) and its stop file. |
| `queue` | The list of deposits to process, written by `pxreprise census --queue`. |

### Optional

| Table | Meaning |
|---|---|
| `[holds]` | Deposits to leave alone for now, with a reason: `PXD012345 = "waiting for the authors' sample key"`. |
| `[exceptions.PXD012345]` | One deposit's own rules, recorded in its provenance and manifest note. `reason` is required, and at least one of these: `include_files` / `exclude_files` (case-insensitive regular expressions on raw file names; the files left out are never downloaded); `max_files`, `max_file_mb`, `max_est_ms2`, `defer_if_median_file_mb` (the profile's size caps for this deposit); `alkylation = "none"` (its cysteines were never alkylated, so no cysteine modification is searched); `species` (the organism it is searched as, a database key of the profile such as `"rat"`, when PRIDE's record names the wrong one); `replace_files`, one sub-table per raw file name (`[exceptions.PXD012345.replace_files."x.raw"]` with `path`, `pride_sha1`, `sha256` and `note`), a local copy used instead of PRIDE's file, such as a repair of a file damaged at the source: it is never downloaded, PRIDE must still list that SHA-1 for the file and the copy must have that SHA-256, and the delivery's note names each replaced file; `min_identification_rate` (since 0.4.44: the deposit's own identification floor, a share of its MS2 spectra from 0 to 1; `0` delivers a search held as `waiting_low_identifications` whatever it found, for a sample that holds few peptides, and its delivery's note quotes the exception). A deposit deferred for its size, or waiting because its record names an organism outside the question, gets one more look when its exception is added or changed. |
| `[splits.PXD012345]` | Since 0.4.43: one deposit searched as several, for instance a deposit holding both human and mouse runs. `reason` and `by` are required. `by` says what the split separates: `species`, `enrichment`, `label` or `chemistry`. Then give one sub-table per group, `[splits.PXD012345.mouse]`, named with lower-case letters and digits. Each group takes the keys `[exceptions]` takes, and must say which raw files are its own (`include_files` or `exclude_files`). Under `by = "species"`, each group also names its own `species`, and no two groups name the same one. Each group is a search of its own, with its own folder, probe, provenance and delivery, keyed `PXD012345-mouse`. A raw file belongs to one group only: two groups that take the same file wait as `waiting_split_overlap` until their selections are fixed. A deposit cannot have both `[exceptions]` and `[splits]`. Once a deposit is split, it is no longer searched or caught up whole. Its earlier search stays on disk, and its manifest entry is retired (`status: superseded`) when the first group is delivered. Groups are delivered only when the machine file says the repository serves splits (`deliver_split_deposits`); until then they are searched and wait. |
| `[gate]` | Since 0.4.46: rules that every deposit the census would queue must pass, read from **the deposit's own text only**: its title, description, sample processing protocol, keywords, PRIDE's organism parts and diseases, and the titles of its references. Never its submitters, lab heads, affiliations, countries, instruments or software, so a lab named after the question's subject does not bring its deposits in. When PRIDE's project record cannot be had, the text is ProteomeXchange's record of the deposit, else PRIDE's search index. Each term is a phrase or a regular expression, matched case-insensitively as a whole word, with any dash read as a hyphen and runs of spaces as one. `strong`: a match queues the deposit (at least one term is required). `weak`: a match alone sends it to the watch list. `other_sense`: a strong term used in another sense (`"(graft\|implant) longevity"`) sends it to the watch list. `watch_if_any`: any match sends it to the watch list (a cancer model). `watch_without_strong`: a match with no strong term sends it to the watch list (a later disease). `reference_group`: `"required"` (the default) applies the `disease_keywords` reference-group rule; `"off"` does not. Outcomes, decided in this order: `not_queued` (no strong, weak or `watch_without_strong` term), `watch` (any watch condition, or only weak terms), `queue`. A deposit your `[relevance] decisions` file includes has been read by a person, so it is queued without the gate (its row in `gate.tsv` says so); one it excludes is never queued. Every decision is written to the census's `gate.tsv` with the matched terms, the field and the text around each; watched deposits also go to `watch.tsv`. `pxreprise gate <question> --list <accessions or queue.json>` gives the same rows for any list of deposits, changing nothing. |
| `[overlays.<organism>]` | `extra_xml = ["file.xml", ...]`: extra protein databases searched alongside the profile's, for this question only. A plain file name is looked up in the machine's `database_dir`; an absolute path is used as is. |
| `[designs]` | Your own experimental designs, for a profile with `[quant] design = "sdrf"`. `dir`: a folder holding one SDRF per deposit, named `PXD012345.sdrf.tsv`. `condition_columns`: the `factor value[...]` columns the condition is built from, when a design has more than one (e.g. `["factor value[age]"]`). It is used ahead of the deposit's own SDRF and checked the way MetaMorpheus reads it; a design that fails the check stops that deposit's search with the reasons, rather than searching without it. Deposited SDRFs rarely carry the experiment's factors (age, treatment), so for most deposits a design is written by hand. `pxreprise validate` refuses a `dir` that does not exist and lists the deposits it holds a design for. |
| `[publish]` | Delivery into a [dataRepo](https://github.com/smith-chem-wisc/dataRepo) repository; needs `datarepo` in the machine file. `manifest`: the dataRepo manifest the batch appends each searched deposit to (created on first use). `command`: publishes what was ingested, e.g. `["{datarepo}", "publish", "{manifest}", "--site", "{manifest_dir}/site"]` to rebuild a website; `{datarepo}`, `{manifest}` and `{manifest_dir}` are filled in, so the question names no machine path. The batch runs it on a schedule, not after each ingest: at most every `every_h` hours (default 6; 0 = after each ingest), only when something was delivered since the last successful publish, and at once when the queue runs out. `timeout_h` (default 4) is how long it may run before it is killed. A driver stopped by STOP finishes a publish under way and leaves the rest to the next driver. `study_layer` names the dataRepo study layer your project delivers to, as a record; the batch does not act on it. See `examples/first-run`. Not needed to run a question. |
| `[restrictions.PXD012345]` | Since 0.4.46: uses a whole delivered dataset is unfit for, each with its reason, as `use = "reason"`: `any_analysis = "an immunopeptidome searched as tryptic"`. The uses are dataRepo's fixed list: `protein_statistics`, `quantitation`, `age_analysis`, `any_analysis`, `occupancy` and `cysteine`; any other key, or an empty reason, is refused when the question is loaded, because dataRepo refuses the whole manifest over it. A split search is restricted by its own key (`[restrictions.PXD012345-mouse]`). The batch writes the manifest entry's `restrictions` from these and from one rule of its own: an entry whose `enrichment` (or `run_enrichment`) names anything but `none` is restricted for `occupancy`, since the material was selected on purpose and a site's occupancy in it is not the proteome's. A use you declare replaces the rule's reason for that use. They are written when a deposit is delivered and brought up to date at every start of the batch, so a change here takes effect at the next start, and the repository needs only a rebuild, no re-ingest. Never write `restrictions` into the manifest by hand: the next start replaces them. `pxreprise batch restrictions <question>` shows what a start would write, changing nothing; `--manifest <copy>` points it at a copy and `--write` writes. Needs `[publish] manifest`. |
| `[traits]` | The sample facts your analysis needs: `required` and `optional` lists, and `source`, the folder of your curated tables. A record for people and for your own analysis; no step of the batch reads it. |

## Tips

- Run `pxreprise census` after every change to the rules, and read its `review.md`: how much each keyword and rule
  did, a sample of each outcome, and every exclusion, each quoting the text that decided it. `census.tsv` has every
  deposit, for filtering in Excel.
- Put `\b` around words (`\\b` in TOML), so `old` cannot match *fold* and `aged` cannot match *damaged*; give
  proximity windows about 150 characters.
- Prefer rules that are too broad plus a few `exclude` decisions over rules too narrow to find what you need.
- A question is data: put it under version control next to your analysis.
- A running batch read the question when it started. An edit takes effect at its next start.
- `census --queue` queues whatever the question says at that moment. Read every keyword list before you run it.
