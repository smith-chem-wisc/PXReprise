---
title: "Tutorial: your first question"
nav_order: 5
---

# Tutorial: your first question

This builds a real question, skeletal muscle ageing, from a first draft to a finished rule set. It takes four censuses
and about fifteen minutes. The numbers are from real censuses of PRIDE on 29 September 2026; yours will differ a little
as PRIDE grows. The finished question is
[`examples/muscle-ageing`](https://github.com/smith-chem-wisc/PXReprise/tree/master/examples/muscle-ageing).

The skill here is a loop. Nobody writes good rules first time; you get there by reading what each census did:

1. write rules;
2. run `pxreprise census question.toml`, with no downloads, in a minute or two;
3. read the census's **`review.md`**;
4. fix what it shows, and go back to 2.

Stop when `review.md`'s samples look right. You never need to read all of `census.tsv`.

## 1. A first draft

Start broad. It is easier to cut false positives you can see than to find deposits you never saw.

```toml
question = "muscle-ageing"
description = "Skeletal muscle proteome across age"
profiles = ["label-free-dda@2", "tmt-dda@1"]

[discover]
keywords = ["sarcopenia", "muscle aging", "muscle ageing", "aged muscle", "skeletal muscle"]
organisms = ["Homo sapiens (human)", "Mus musculus (mouse)", "Rattus norvegicus (rat)"]

[relevance]
require_any = ["muscle.{0,60}(aging|ageing|aged|old|elderly)", "(aging|ageing|aged|old|elderly).{0,60}muscle"]
```

The rule says: *muscle* within 60 characters of an ageing word, either way round. Listing `tmt-dda@1` costs nothing:
TMT deposits are counted, not searched, so the census says what that profile would add.

```
pxreprise census question.toml
```

`review.md` opens with the outcome:

| | deposits |
|---|---:|
| found by the keywords | 616 |
| **relevant** | **81** |
| not relevant: no `require_any` rule matched | 522 |

and 32 of the 81 would be searched. Now read on.

### Judge a keyword by what it finds that is relevant

| keyword | found | relevant | found only by this keyword |
|---|---:|---:|---:|
| sarcopenia | 40 | 13 | 10 |
| skeletal muscle | 605 | 79 | 568 |

`skeletal muscle` looks like noise, 605 hits, but it finds 79 of the 81 relevant deposits. Keep it. A keyword is noise
only when its *relevant* count is small; the rules deal with the rest.

### Read the relevant sample

Each row quotes the text that decided it. Among them:

| accession | title | decided by |
|---|---|---|
| PXD054406 | TMT analysis of proteomic changes in the gastrocnemius ... of WT and Bmal1-KO mice | ...threshold value of **fold** change... |
| PXD023050 | High levels of TFAM repress in vivo transcription of mitochondrial DNA | ...4.5-**fold** increase of TFAM ... in heart and skeletal muscle... |
| PXD001641 | Single muscle fiber proteomics reveals unexpected mitochondrial specialization | ...CD1 strain, 3 months **old**... |

Three lessons:

- **`old` is inside *fold* and *threshold*; `aged` is inside *damaged*.** Put `\b` (a word boundary) around words:
  `\bold\b`. (In TOML, write `\\b`.)
- **An age is not an ageing study.** "3 months old" says how old the mice were, not that age was studied. Match
  ageing words: *aging*, *aged*, *elderly*, *old mice*, *older adults*, *young vs old*.
- **`sarcopenia` is the one word that means muscle ageing by itself**, yet the draft rules needed an ageing word
  near *muscle* as well. Give it its own rule.

## 2. Word boundaries and ageing words

```toml
require_any = [
    "\\bsarcopeni",
    "\\b(muscle|myofib\\w*|satellite cells?)\\b.{0,80}\\b(aging|ageing|aged|elderly|old(er)? (mice|rats|adults|animals|individuals|subjects|people|men|women))\\b",
    "\\b(aging|ageing|aged|elderly|old(er)? (mice|rats|adults|animals|individuals|subjects|people|men|women))\\b.{0,80}\\b(muscle|myofib\\w*)",
]
exclude_if_any = ["\\b(heart|cardiac|cardiomyocytes?|myocardi\\w*)\\b"]
unless_any = ["\\bskeletal muscle", "\\bsarcopeni"]
```

The exclusion is for heart muscle, which is muscle too; `unless_any` keeps a deposit that also names skeletal muscle
or sarcopenia. Result: **68 relevant**, 28 searchable. The *fold* and *3 months old* deposits are gone. But:

### Read the Rules table

| rule | pattern | deposits it matched |
|---|---|---:|
| exclude_if_any | heart, cardiac, ... | 0 excluded |
| unless_any | `\bskeletal muscle` | 0 kept despite an exclusion |
| unless_any | `\bsarcopeni` | 1 kept despite an exclusion |

One heart deposit was kept, PXD040488, "The atrial and ventricular myocardial proteome of end-stage lamin heart
disease", because its results mention sarcopenia. **An `unless_any` that is too broad undoes the exclusion.** Drop
`sarcopeni` from it.

### Words used loosely

The relevant sample also shows deposits about the **liver** (PXD019755, "Lkb1 suppresses amino acid-driven
gluconeogenesis in the liver") and **cancer cachexia** (PXD036752), which mention sarcopenia as a side effect. A rule
against *liver* would lose real multi-tissue ageing studies. When a few specific deposits are wrong, **decide them by
hand** instead of writing a rule (step 3).

### Look for what was missed

The section *Found by a keyword, but no rule matched* is a sample. To look further, filter `census.tsv` (it opens in
Excel) for `relevance` = `not_relevant` and an ageing word in the title. Here that turns up:

- PXD006840, "**Age-dependent** modulation of the human muscle proteome": the rules have no *age-dependent*.
- PXD035171, "Natural **aging** and ovariectomy induces parallel phosphoproteomic alterations in skeletal **muscle**":
  a little over 80 characters lie between *aging* and *muscle*, and the window allowed 80. **Proximity windows need
  slack.**

## 3. Wider window, more phrasings, and hand decisions

Add `age-related`, `age-dependent`, `age-associated` and `young vs old`; widen the window to 150; drop `sarcopeni`
from `unless_any`; and add a decisions file:

```toml
decisions = "decisions.tsv"
```

```
accession	verdict	reason
PXD013478	exclude	liver gluconeogenesis (LKB1); sarcopenia named only as a downstream consequence
PXD019755	exclude	liver gluconeogenesis (Lkb1); sarcopenia named only as a downstream consequence
PXD019757	exclude	liver gluconeogenesis (Lkb1); sarcopenia named only as a downstream consequence
PXD047574	exclude	liver tissue of PolgA mutator mice; no muscle samples
PXD036752	exclude	pancreatic cancer cachexia model, not ageing
```

Tab-separated, one reason each. A decision nobody can explain later cannot be reviewed.

Result: **72 relevant**, 2 excluded, 5 decided. Both missed deposits are in.

### Check every exclusion

`review.md` lists **all** excluded deposits, because each is a deposit lost:

| accession | title | decided by |
|---|---|---|
| PXD040488 | The atrial and ventricular myocardial proteome of end-stage lamin heart disease | ...myocardial... |
| PXD030350 | Small extracellular vesicles from young plasma reverse age-related functional declines ... | ...various tissues (hippocampus, muscle, heart, testis...)... |

The first is right. The second is a multi-organ ageing study; the word *heart* excluded it. The deposit's PRIDE page
lists skeletal muscle among the eight organs measured, so it belongs. Add:

```
PXD030350	include	multi-organ ageing study (young-plasma vesicles in aged mice); skeletal muscle is one of the eight organs measured
```

## 4. Done

| | draft | step 2 | step 3 | final |
|---|---:|---:|---:|---:|
| relevant or included | 81 | 68 | 72 | **73** |
| of which `label-free-dda@2` would search | 32 | 28 | 29 | **29** |

The draft's 81 looked like more, but it counted *fold change* and *3 months old*. The final 73 are deposits you have
read reasons for.

Of the 73, 29 can be searched today. The rest are counted by what they wait on: 18 on TMT, 17 on DIA, and a few on
timsTOF, metabolic labelling or other instruments. PXD030350, which you just included, is one of them: it is iTRAQ,
and no available profile searches that yet. **Relevance is your call; whether it can be searched is the profile's.**

Now write the queue and run it:

```
pxreprise census question.toml --queue
pxreprise batch run question.toml --machine machines/my-machine.toml
```

## Rules of thumb

- Start broad; narrow by reading. Judge keywords by their *relevant* count.
- `\b` around words, always. Match ageing (or disease) words, not incidental mentions.
- Give proximity windows slack: 150 characters rather than 60.
- `unless_any` rescues from **every** exclusion; keep it narrow.
- A handful of wrong calls: decisions, with reasons. A pattern of wrong calls: a rule.
- Read every exclusion. They are where good deposits are lost.
- Keep the question and its decisions under version control, next to your analysis.
