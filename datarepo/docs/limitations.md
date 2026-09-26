# What this repository cannot tell you

Read this before publishing a number from a dataRepo catalog, and give it to any agent that will
query one.

Most wrong answers from this system are not wrong queries. They are **right queries against a
question the data cannot answer**, returning an empty result or a NULL that reads as a measured
absence. This page lists every such place we currently know about, with the measurement that
establishes it and the gap id tracking the fix.

Everything below was measured on aging's catalog **`deddfb23a567c7c7`** (34 datasets, schema
0.0.11, every bundle built by datarepo 0.21.0, catalog built 2026-09-26). The page was first written
on a 9-dataset catalog on 2026-09-22, and every number was re-run for this version rather than
edited. Re-measure before citing: the producing instance adds datasets continuously.

> **The governing rule.** `required: true` is a claim that a true value always exists. When that
> claim is false, the column does not crash — it lies, and the tools support the lie. `Protein.organism`
> was `required: true`, so all 339 contaminant entries in an earlier catalog read `NCBITaxon:9606`:
> porcine trypsin, bovine albumin and *E. coli* lacZ, at q = 0, in every dataset. *"No non-human
> proteins were identified"* became a falsehood with full tool support. Every entry on this page is
> that same failure caught earlier.

---

## 1 · There is almost no sample metadata, and ages only where someone curated them

```
datasets with an SDRF (sdrf_status trusted)    5 of 34
sdrf_status = absent                          29 of 34
samples                                      811
```

| `samples` column | filled |
|---|---|
| `source_name`, `organism` | 811 / 811 |
| `biological_replicate` | 69 / 811 |
| `sex`, `organism_part`, `cell_type`, `disease`, `condition`, `material_type`, `cell_line`, `individual_id`, `timepoint` | **0** |

`organism` reads 811/811 only because the **producer supplies it from the manifest**, not because an
SDRF said so. It is not evidence of sample annotation.

**The zeros are not all "not recorded" (see §2).** `organism_part` holds an UBERON *term*, and the
SDRFs here name tissues without one. So it is NULL for 51 samples whose SDRF says `heart`, `Urine`,
`Blood` or `Blood serum` (PXD026608, PXD034432, PXD011314, PXD010115). From catalog format 8
(datarepo 0.25.0) the name is in `samples.organism_part_name`, filled for exactly those 51, and
likewise `sex_name`, `cell_type_name` and `disease_name` (none filled on this corpus).

**Ages: `sample_ages` holds 236 rows, one per sample, in 7 datasets, all hand-curated by aging**
from each deposit's PRIDE record and paper (PXD047289, PXD047292, PXD051203, PXD051644, PXD056433,
PXD056458, PXD058248; 0.25 to 2.0 years, all rodent). None came from an SDRF. From study layer
0.4.0 on, `age_source` says which kind a row is. A dataset outside those seven has no age, and that
is NA, not "young".

**Upstream, the SDRF route yields a usable age for 132 of 151 age-bearing accessions** (sdrf 016:
86% of real age cells parse exactly; the refusals are mostly bare numbers with no unit). Of the
producer's 168 benchmark questions, 46 require an age effect, and `age_effects` holds zero rows.
Do not present an empty result to such a question as "no effect found".

*Tracked as G38, G45; discussed with `sdrf` in `design/threads/sdrf/`.*

## 2 · A NULL in `samples` has three meanings

1. **Never asked.** 29 of 34 datasets have no SDRF at all.
2. **Asked, and the answer was `not available`.** Stored as NULL, like case 1 (G42).
3. **Answered with a name and no ontology term.** `sex`, `organism_part`, `cell_type` and `disease`
   hold terms only, so a named tissue is NULL there. **Resolved from catalog format 8:** the name is
   in the `<column>_name` beside each (§1).

**Cases 1 and 2 are still indistinguishable**, in the term columns and in the `_name` columns alike,
because the ingester stores no row for a `not available` cell (G42). Do not compute a coverage
statistic from `samples`, and do not conclude from a NULL that a sample was never asked.

## 3 · Several tables are empty

```
protein_localizations        0      age_effects         0
protein_annotations          0      age_effect_meta     0
feature_sets                 0      age_mappings        0
organelle_term_categories    0      trait_effects       0
glycopeptides                0      ptm_pairs           0
proteoform_inferences        0
```

These tables exist, are validated, and have **no delivery yet**. An organelle question returns
nothing — not because no protein is mitochondrial, but because the organelle map has not been
loaded.

Filled since the first version of this page: `ptm_stoichiometry` (475,212 rows: 385,625
quantified, 65,352 floor, 24,235 count-only), `sample_ages` (236), `gene_resolutions` (47,573) and
`protein_genes` (350,205).

**`empty` and `unknown` are different answers.** The MCP server reports an empty table distinctly
for exactly this reason. If you are querying SQL directly, check row counts before interpreting a
zero.

## 4 · `Protein.gene` is the producer's display symbol — key on accessions

Use `protein_accession` as the identifier. Over 44,881 distinct non-decoy accessions:

```
carry exactly one gene name everywhere          44,227
carry NO gene name in any dataset                  483
carry CONFLICTING gene names across datasets         1
carry a gene in one dataset and NULL in another    170
```

**The mechanism.** The producer `|`-joins `Gene Name` alongside `Accession`, and a protein with no
symbol leaves no hole in the join, so the two lists can be ragged. The ingester refuses to guess on
a ragged row and writes NULL, and an accession's stored value then depends on which row claims it
first. For a gene-level question use `protein_genes` / `gene_resolutions` (logs' resolution against
the primary assembly), not this column. **48.6% of rat accessions have no primary-assembly gene** in
that resolution, so a rat gene-level summary loses about half the proteins.

*Tracked as G48.*

## 5 · There is no leading / razor protein, and list position is not rank

`protein_groups.protein_accessions` is a list, and it is **alphabetically sorted by the producer**:
all 2,435 multi-accession groups in this catalog, zero deviations. The producer's protein-group
file has 26 columns, **none of which names a razor, leading, representative or principal protein**.

So `protein_accessions[1]` is the alphabetically first accession and nothing else. A column derived
from it and named `leading_protein` would report alphabetical rank under a biological name, and
**a reader cannot detect the error**. `protein_group_id` is built from the same sorted list, so its
first accession is not a leader either.

**What is real:** group *composition* varies across datasets, and that is recoverable from
`protein_accessions` directly.

*Tracked as G43.*

## 6 · Contaminant is a per-dataset label

MetaMorpheus labels an accession a contaminant by the database it was read from, per search. P02768
(human albumin) is a **target in the human datasets and a contaminant in the rodent ones**; human
MAPT (tau) is in the shipped contaminant panel, so rodent searches label it a contaminant too.

Never filter or count on a corpus-wide contaminant flag. Use `proteins.is_contaminant` or
`protein_datasets.is_contaminant` for one dataset; `protein_index.n_datasets_contaminant` counts the
datasets that label it one (catalog version 7 on). Decoys carry no `organism` and no
`organism_name` anywhere (0 of 321,808), but exclude them explicitly
(`protein_accession NOT LIKE 'DECOY_%'`) all the same.

## 7 · `pep` is not comparable across datasets

MetaMorpheus trains its PEP model afresh on every search, on that search's own targets and decoys,
so `psms.pep`, `peptidoforms.best_pep` and `psms.pep_q_value` are on a scale set by the search that
wrote them (pep 002, definition `pep:DEF-PEP`). The per-dataset median `pep` over all PSMs ranges
from 0.0 to 1.0 in this catalog, which describes nothing. Rank or threshold within one dataset;
across datasets compare counts at a threshold, and across releases use `q_value`. The MCP `sql`
tool says this whenever a query reads one of the three (datarepo 0.22.0 on).

## 8 · The corpus is narrower than the schema

| | schema supports | present today |
|---|---|---|
| organism | any | human 19, rat 10, mouse 5 |
| acquisition | DDA + DIA | **DDA only** |
| quant method | label-free + TMT | **label-free only** |
| search engine | any | **MetaMorpheus 1.1.11 only** |
| database | any | **three UniProt XMLs**, one per species, one sha256 per species |
| enrichment | any | none 12, affinity purification 8, other 5, immunoprecipitation 3, proximity labelling 3, chemical probe 2, phospho 1 |

A query that returns nothing for DIA is describing this corpus, not the world. Because one search
engine and one database per species produced everything, **cross-dataset agreement here is not
evidence of method robustness**. And **22 of 34 datasets are enriched** (pull-downs, probes, IPs):
a protein's absence from one of those says nothing about the proteome. Read `datasets.enrichment`,
and `runs.enrichment`, which can differ run by run, before comparing.

`datasets.organisms` is the organism of the **searched database**, not a statement about the
sample.

*Tracked as G40, G46.*

## 9 · Depth varies ~170× across datasets

```
PXD032044   680,855 accepted PSMs
PXD011314     3,994 accepted PSMs
```

"Protein X appears in more PSMs in dataset A" is usually a statement about sequencing depth.
Normalise, or compare within a dataset.

## 10 · The bundles say what is wrong with themselves — read it

```sql
SELECT code, severity, count(*) AS n FROM findings GROUP BY 1, 2 ORDER BY 3 DESC;
```

```
                       code severity   n
             no_output_sdrf     info  34
             no_design_file  warning  34
         unplaced_ptm_sites  warning  34
     occupancy_decoy_groups     info  33
                    no_sdrf  warning  29
   unresolved_modifications  warning  28
upstream_provenance_changed  warning  25
             count_mismatch  warning  23   (19 datasets)
         high_contamination  warning  23
   collapsed_duplicate_rows     info  14
                low_id_rate  warning  11
               sdrf_uncoded  warning   4
       excluded_from_search     info   2
       occupancy_not_stored  warning   2
sdrf_skeleton, calibration_failed, mixed_enrichment: 1 each
```

Every dataset carries several findings, and most are about what the deposit lacks (no SDRF, no
design) rather than about the processing. `count_mismatch` means our totals and the producer's
disagree somewhere, and each finding's `message` says where. `high_contamination` is a flag on the
sample, not on the processing. A finding is never silently resolved: if a number matters, check
whether its dataset has an open finding that touches it.

---

## How to ask honestly

- **Quote the `catalog_id`.** A number without it is not reproducible; the corpus grows weekly.
- **Check row counts before interpreting a zero.** Empty table, empty result and "no such effect"
  are three different statements.
- **Use the acceptance views** (`psms_1pct`, …), not the raw tables.
- **Key on accessions**, not gene symbols (§4), and never on list position (§5).
- **Exclude decoys explicitly**, and read the contaminant label per dataset (§6).
- **Never compare raw `pep` across datasets** (§7).
- **Say what you could not check.** Every entry on this page exists because someone wrote down a
  thing they had not verified, and someone else found it later.

## Reporting a new one

If you find a place where this catalog returns a confident answer it should not, that is a finding
worth more than a fix. Open an issue at
[github.com/trishorts/dataRepo/issues](https://github.com/trishorts/dataRepo/issues) with the query,
the `catalog_id`, and what you expected — the `catalog_id` is what makes it reproducible.

See also: [querying.md §4](querying.md), *queries that look right and are wrong*.
