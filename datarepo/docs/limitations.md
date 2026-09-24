# What this repository cannot tell you

Read this before publishing a number from a dataRepo catalog, and give it to any agent that will
query one.

Most wrong answers from this system are not wrong queries. They are **right queries against a
question the data cannot answer**, returning an empty result or a NULL that reads as a measured
absence. This page lists every such place we currently know about, with the measurement that
establishes it and the gap id tracking the fix.

Everything below was measured on catalog **`f8fc910cce116fbe`** (9 datasets, schema 0.0.7,
`INGESTER_VERSION` 0.9.0, built 2026-09-22). Re-measure before citing — the producing instance adds
datasets continuously.

> **The governing rule.** `required: true` is a claim that a true value always exists. When that
> claim is false, the column does not crash — it lies, and the tools support the lie. `Protein.organism`
> was `required: true`, so all 339 contaminant entries in an earlier catalog read `NCBITaxon:9606`:
> porcine trypsin, bovine albumin and *E. coli* lacZ, at q = 0, in every dataset. *"No non-human
> proteins were identified"* became a falsehood with full tool support. Every entry on this page is
> that same failure caught earlier.

---

## 1 · There is almost no sample metadata, and **no ages at all**

This is the largest limitation and it is structural, not a backlog item.

```
datasets with a deposited SDRF          1 of 9      (PXD036557 only)
sdrf_status = absent                    8 of 9
```

Across all **162 samples** in the catalog:

| column | filled |
|---|---|
| `source_name` | 162 / 162 |
| `organism` | 162 / 162 |
| `biological_replicate` | 18 / 162 |
| `sex` | **0** |
| `organism_part` | **0** |
| `cell_type` | **0** |
| `disease` | **0** |
| `condition` | **0** |
| `material_type` | **0** |
| `cell_line` | **0** |
| `individual_id` | **0** |
| `timepoint` | **0** |

`organism` reads 162/162 only because the **producer supplies it from the manifest**, not because an
SDRF said so. It is not evidence of sample annotation.

**No donor age exists anywhere in this catalog.** `sample_ages` holds zero rows, and this is not a
loading problem: the upstream repair path fills a cell only where PRIDE's project record holds one
value for a whole deposit, and an age is a property of a donor, not a deposit. It therefore yields
**exactly zero ages**, permanently. The only mechanical recovery is a lookup against a curated
corpus, whose ceiling is **153 accessions of 1,203 (12.7%)** — and the one dataset here that *has* an
SDRF is not in that corpus.

**PRIDE's own metadata will not fill it either** (pride thread 002, measured 2026-09-23 on PRIDE
Archive v3). No PRIDE route is per sample; `sampleAttributes` only repeats the project's own lists;
and age appears only as free text, mostly "age-matched", with a number in 4 of 500 projects and even
then per group, not per donor. The only per-sample source PRIDE serves is a deposited SDRF with a
real `characteristics[age]` column. On this corpus, every deposited SDRF is a PRIDE
community-annotated file, and mzLib's informativeness gate rates the ten of them 1 informative,
8 partial and 1 skeleton; none varies with age.

**Consequences.** Any question stratified by age, sex, tissue, disease or donor is unanswerable.
Of the producer's 168 benchmark questions, **46 require an age effect** and 42 are blocked by
nothing else. Do not present an empty result to such a question as "no effect found".

*Tracked as G38, G45; discussed with the `sdrf` project in `design/threads/sdrf/`.*

## 2 · `samples` cannot distinguish "answered `not available`" from "never asked"

PXD036557's SDRF **does** carry `characteristics[organism part]` and `characteristics[disease]`.
Both hold `not available` in all 18 rows. `samples.organism_part` is NULL for those 18 samples —
and NULL for the 144 samples in the eight datasets that were never asked at all.

**The two are indistinguishable in the catalog.** A deposit that was asked and declined to answer
looks exactly like a deposit that was never asked, which removes it from the denominator of any
measurement of how bad the coverage problem is.

Until this is fixed, **do not compute a coverage or completeness statistic from `samples`.** Use
`sample_characteristics`, which carries the producer's verbatim cell.

*Tracked as G42.*

## 3 · The annotation tables are empty

```
protein_localizations   0 rows
protein_annotations     0 rows
age_effects             0 rows
age_effect_meta         0 rows
age_mappings            0 rows
ptm_stoichiometry       0 rows
feature_sets            0 rows
```

These tables exist, are validated, and have **no producer yet**. An organelle question returns
nothing — not because no protein is mitochondrial, but because the organelle map has never been
loaded.

**`empty` and `unknown` are different answers.** The MCP server reports an empty table distinctly
for exactly this reason; if you are querying SQL directly, check row counts before interpreting a
zero.

## 4 · `Protein.gene` is currently **unvalidated** — do not key on it

Use `protein_accession` as the identifier. The `gene` column is the producer's display symbol,
stored verbatim, and it has three known problems:

**It is not a function of the accession.** Over 20,022 distinct non-decoy accessions:

```
carry exactly one gene name everywhere          19,874
carry NO gene name in any dataset                  143
carry CONFLICTING gene names across datasets         5
carry a gene in one dataset and NULL in another     60
```

**The mechanism.** The producer `|`-joins `Gene Name` alongside `Accession`. Of 23,284
multi-accession non-decoy peptide rows, 188 have the gene collapsed to a single shared value
(correct, and broadcast) and **182 are *ragged*** — typically `n_gene = n_acc − 1`, because a protein
with no symbol leaves no hole in the join. The ingester refuses to guess on a ragged row and writes
NULL, which is right; but an accession's stored value then depends on **which row claims it first**,
and a ragged row claims it as firmly as a clean one.

**And one stored value we cannot reproduce.** Tracing `P63135` through our own reader by hand yields
NULL for PXD032040, while the catalog stores `ERVK-6`. Until that is explained, treat the column as
unverified rather than merely incomplete.

Note PXD036557 has **zero** ragged rows, which is why an earlier investigation of the same column
family — conducted on that dataset — never saw this.

*Tracked as G48.*

## 5 · There is no leading / razor protein, and list position is not rank

`protein_groups.protein_accessions` is a list. It is **alphabetically sorted by the producer** —
159 of 159 multi-accession rows across two datasets, zero deviations — and the producer's
protein-group file has 26 columns, **none of which names a razor, leading, representative or
principal protein**.

So `protein_accessions[1]` is the alphabetically first accession and nothing else. A column derived
from it and named `leading_protein` would report alphabetical rank under a biological name, and
**a reader cannot detect the error**: a human accession on bovine albumin eventually looks odd,
alphabetical order never does.

This has already produced a wrong published measurement. A cross-dataset count of proteins that
"lead in some datasets and not others" was computed from list position and reached two other
projects before anyone checked; reproduced here over nine datasets it gives 4,354 / 4,123 / 171 / 60,
and it is `min(group)` by string comparison.

**What is real, and is measurable:** group *composition* genuinely varies across datasets. Of 4,354
accessions identified in ≥2 of 9 datasets, **354 change group composition and 316 are alone in one
dataset and grouped in another** — ARF1, RAB1A/RAB1B, SAR1A, H3C1. That is recoverable from
`protein_accessions` directly, needs no new column, and is the honest form of the question.

*Tracked as G43.*

## 6 · Decoys carry a species name, inconsistently

`Protein.organism` is correctly NULL for decoys and contaminants — a reversed sequence is no
organism's protein, and a contaminant panel is multi-species by design. `Protein.organism_name` did
**not** get the same treatment:

```
decoys missing organism_name, PXD023381        149 of  7,157
decoys missing organism_name, PXD024803     11,804 of 11,804
```

So in some datasets a reversed sequence carries `Homo sapiens` and in others it carries nothing.
Always exclude decoys explicitly (`protein_accession NOT LIKE 'DECOY_%'`) rather than relying on a
species filter to do it.

*Tracked as G44.*

## 7 · The corpus is narrower than the schema

| | schema supports | present today |
|---|---|---|
| organism | human + rodent | **human only**, all 9 datasets |
| acquisition | DDA + DIA | **DDA only** |
| quant method | label-free + TMT | **label-free only** |
| search engine | any | **MetaMorpheus 1.1.11 only** |
| database | any | **one UniProt XML**, identical sha256 in all 9 |

A query that returns nothing for DIA is describing this corpus, not the world. And because a single
search engine and a single database produced everything, **cross-dataset agreement here is not
evidence of method robustness** — the methods are identical.

There is also no cross-species key: `age_effects` (31 columns) and `age_effect_meta` (24 columns)
have **no organism column**, and `age_effect_meta` stratifies on tissue, acquisition and quant
method but not species. Nothing currently prevents a human and a mouse effect pooling into one
meta-estimate. Both tables hold zero rows, so this is a design gap rather than a corrupted result.

*Tracked as G40, G46.*

## 8 · Depth varies ~14× across datasets

```
PXD027318   378,168 accepted PSMs
PXD036557    26,582 accepted PSMs
```

"Protein X appears in more PSMs in dataset A" is usually a statement about sequencing depth.
Normalise, or compare within a dataset.

## 9 · The bundles say what is wrong with themselves — read it

```sql
SELECT code, severity, status, count(*) AS n FROM findings GROUP BY 1,2,3 ORDER BY 4 DESC;
```

```
                    code severity status  n
          no_design_file  warning   open  9
          no_output_sdrf     info   open  9
                 no_sdrf  warning   open  8
          count_mismatch  warning   open  8
unresolved_modifications  warning   open  5
collapsed_duplicate_rows     info   open  5
      high_contamination  warning   open  5
             low_id_rate  warning   open  1
```

`count_mismatch` on 8 of 9 datasets means our totals and the producer's disagree somewhere; each
finding's `message` says where. `high_contamination` on 5 is a flag on the sample, not on the
processing.

A finding is never silently resolved. If a number matters, check whether its dataset has an open
finding that touches it.

---

## How to ask honestly

- **Quote the `catalog_id`.** A number without it is not reproducible; the corpus grows weekly.
- **Check row counts before interpreting a zero.** Empty table, empty result and "no such effect"
  are three different statements.
- **Use the acceptance views** (`psms_1pct`, …), not the raw tables.
- **Key on accessions**, not gene symbols (§4), and never on list position (§5).
- **Exclude decoys explicitly**, and remember contaminants are real identifications of real
  non-human proteins (§6).
- **Say what you could not check.** Every entry on this page exists because someone wrote down a
  thing they had not verified, and someone else found it later.

## Reporting a new one

If you find a place where this catalog returns a confident answer it should not, that is a finding
worth more than a fix. Open an issue at
[github.com/trishorts/dataRepo/issues](https://github.com/trishorts/dataRepo/issues) with the query,
the `catalog_id`, and what you expected — the `catalog_id` is what makes it reproducible.

See also: [querying.md §4](querying.md), *queries that look right and are wrong*.
