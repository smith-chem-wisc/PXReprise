# Querying a catalog

A cookbook. **Every query on this page was run against a real catalog and every result shown is what
it actually returned** — no invented numbers, no illustrative placeholders.

```
catalog_id       f8fc910cce116fbe
schema_version   0.0.7
builder_version  0.13.0
n_datasets       9
built_utc        2026-09-22T14:59:41+00:00
```

Your catalog will differ. That is the point of §1.

---

## 0 · Two things before any query

### The acceptance views, not the raw tables

`psms`, `peptidoforms` and `protein_groups` hold **everything the producer wrote**, including rows
above the FDR threshold and decoys. The views `psms_1pct`, `peptidoforms_1pct` and
`protein_groups_1pct` apply the producing search engine's own acceptance rule, once, in one place.

```sql
SELECT 'psms' AS t, (SELECT count(*) FROM psms) AS raw,
                    (SELECT count(*) FROM psms_1pct) AS accepted
UNION ALL SELECT 'peptidoforms',   (SELECT count(*) FROM peptidoforms),
                                   (SELECT count(*) FROM peptidoforms_1pct)
UNION ALL SELECT 'protein_groups', (SELECT count(*) FROM protein_groups),
                                   (SELECT count(*) FROM protein_groups_1pct);
```

```
             t      raw  accepted
          psms  2707851   1361409
  peptidoforms   911187    198444
protein_groups    31318     19246
```

**Half the PSMs and four-fifths of the peptidoforms are below the bar.** Query `psms` instead of
`psms_1pct` and you will roughly double your answer. Unless you are specifically studying the
decoy distribution or the score tail, use the view.

The views are not a filter applied at write time — the rows are all there, and a consumer who wants
a different threshold can have one. That is rule 4 in the [docs index](README.md): emit the data,
let the consumer filter.

### The catalog identifies itself, and you should quote it

```sql
SELECT catalog_id, schema_version, builder_version, n_datasets, built_utc FROM catalog_meta;
SELECT dataset_id, bundle_id FROM catalog_bundles ORDER BY dataset_id;
```

`catalog_id` is a hash of the exact set of bundles plus the schema and builder versions. Two
catalogs with the same `catalog_id` contain the same rows; two with different ids do not, even if
they came from "the same" instance an hour apart. **A number from this catalog without its
`catalog_id` is not reproducible**, because the producing instance adds datasets continuously.

---

## 1 · Orientation: what is actually in here?

Always start here, especially as an agent. The corpus is not fixed.

```sql
SELECT dataset_id, acquisition, quant_method, n_runs, n_samples,
       n_psms_1pct, n_protein_groups_1pct
FROM dataset_overview ORDER BY dataset_id;
```

```
dataset_id acquisition quant_method  n_runs  n_samples  n_psms_1pct  n_protein_groups_1pct
 PXD023381         DDA   label_free      18         18        38862                   1207
 PXD024803         DDA   label_free      18         18       238159                   1751
 PXD027318         DDA   label_free      18         18       378168                   4478
 PXD028852         DDA   label_free      18         18        77292                   2686
 PXD032040         DDA   label_free      15         15       118909                   2538
 PXD032202         DDA   label_free      21         21       183023                   1925
 PXD036557         DDA   label_free      18         18        26582                   1652
 PXD049018         DDA   label_free      18         18        98615                   1534
 PXD050351         DDA   label_free      18         18       201799                   1475
```

Note the spread: PXD027318 has **14×** the accepted PSMs of PXD036557. Any cross-dataset count that
does not normalise is measuring depth, not biology.

`dataset_overview` also carries `title`, `organisms`, `labelling`, `enrichment`,
`instrument_vendor`, `search_engine`, `search_engine_version`, `n_ptm_sites`, `n_quant_values` and
`n_open_findings`.

### Which search, against which database?

```sql
SELECT dataset_id, search_engine, search_engine_version,
       search_database, substr(search_database_sha256, 1, 16) AS sha16
FROM datasets ORDER BY dataset_id;
```

In this catalog every dataset returns the same file **and the same checksum** —
`uniprotkb_proteome_UP000005640_AND_revi_2026_09_18.xml`, `760984e8d402ade6…`, MetaMorpheus 1.1.11.
That is a strong statement: the searches are comparable because they were run against identical
bytes. **Check it rather than assuming it** — a catalog spanning two database versions is a
different analysis.

---

## 2 · Identifications

### A protein across every dataset that found it

```sql
SELECT dataset_id, protein_group_id, round(q_value, 5) AS q,
       unique_peptides, round(sequence_coverage, 1) AS cov
FROM protein_groups_1pct
WHERE list_contains(protein_accessions, 'P60709')   -- ACTB
ORDER BY dataset_id;
```

```
dataset_id protein_group_id   q  unique_peptides  cov
 PXD024803 PXD024803:P60709 0.0                0  0.7
 PXD027318 PXD027318:P60709 0.0                0  0.9
 PXD028852 PXD028852:P60709 0.0                0  0.7
 PXD032040 PXD032040:P60709 0.0                0  1.0
 PXD032202 PXD032202:P60709 0.0                0  0.9
 PXD036557 PXD036557:P60709 0.0                0  0.9
 PXD049018 PXD049018:P60709 0.0                0  0.7
 PXD050351 PXD050351:P60709 0.0                1  0.8
```

Two things worth reading carefully. **ACTB is in 8 of 9 datasets, not 9** — absence from PXD023381
is a real result, not a query bug. And **`unique_peptides` is 0 almost everywhere**: beta-actin
shares nearly all its tryptic peptides with the other actins, so it is identified confidently
(`q = 0`) on shared evidence. A filter like `WHERE unique_peptides > 0` would silently delete one of
the most abundant proteins in the sample.

`protein_accessions` is a `VARCHAR[]`, so use `list_contains`, not `LIKE`.

### Proteins seen in every dataset

```sql
SELECT protein_accession, gene, n_datasets_1pct
FROM protein_index
WHERE n_datasets_1pct = 9 AND NOT is_contaminant
ORDER BY protein_accession LIMIT 6;
```

```
protein_accession     gene  n_datasets_1pct
           A8MWD9 SNRPGP15                9
           E9PAV3     NACA                9
           O00571    DDX3X                9
           O15371    EIF3D                9
           O43143    DHX15                9
           O43175    PHGDH                9
```

`protein_index` and `peptide_index` are cross-dataset convenience tables built by `build`. They
carry `n_datasets`, `n_datasets_1pct`, `dataset_ids`, `dataset_ids_1pct` and `best_q_value`, so a
"how reproducible is this?" question needs no join.

⚠ **`gene` here is currently unvalidated** — see [limitations.md §4](limitations.md). Use the
accession as the key.

### PTM sites by chemistry

```sql
SELECT * FROM ptm_sites_by_chemistry LIMIT 8;
```

The view groups modifications by chemical identity rather than by name, which matters because the
same mass shift carries several names across sources. Columns: `dataset_id`, `protein_accession`,
`position`, `residue`, `modification`, `chemistry_key`, `n_names`, `modification_names`, `n_psms`,
`best_q_value`, `best_ambiguity_level`, `target_decoy`. **`n_names > 1` means the same chemistry
arrived under several spellings** — do not group on `modification` alone.

---

## 3 · Trust: what the bundle itself says is wrong

Every bundle reconciles its own counts against the producer's totals and records disagreements as
**findings**, rather than silently resolving them.

```sql
SELECT code, severity, status, count(*) AS n
FROM findings GROUP BY 1, 2, 3 ORDER BY 4 DESC;
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

Read that table before trusting a cross-dataset claim. `no_sdrf` on **8 of 9** datasets is the
single most consequential row in this catalog and it is the subject of
[limitations.md §1](limitations.md). `count_mismatch` on 8 means our totals and the producer's
differ somewhere — each finding's `message` says where.

### Every number should be able to name its definition

```sql
SELECT count(*) AS n, count(DISTINCT definition_id) AS distinct_defs FROM definitions;
-- 135 rows, 15 distinct definitions
```

`Metric` rows carry a `definition_id` pointing at the owning project's published definition, so two
conflicting numbers can sit side by side, each labelled, and neither is silently preferred.
Definition ids are namespaced `<owner>:<ID>`; a number with no published definition uses
`PROVISIONAL:<NAME>`, which is a flag, not a name.

---

## 4 · Queries that look right and are wrong

This section is the reason the page exists.

### Querying the raw table instead of the acceptance view

```sql
SELECT count(*) FROM psms;          -- 2,707,851   includes decoys and everything above 1% FDR
SELECT count(*) FROM psms_1pct;     -- 1,361,409   the number you meant
```

### Treating a NULL as a measured absence

`Protein.organism` is NULL for every contaminant and every decoy **on purpose** — a reversed
sequence is no organism's protein, and a contaminant panel is bovine, porcine and bacterial by
design. So:

```sql
-- WRONG: reads "no non-human proteins" off a column that is null by design
SELECT count(*) FROM proteins WHERE organism <> 'NCBITaxon:9606';
```

Use `organism_name` (the producer's verbatim species string) and exclude decoys explicitly. In this
catalog **25 distinct species names** appear among non-decoy entries, and **986 entries are
contaminant-panel proteins**.

### Assuming position in a list means rank

`protein_groups.protein_accessions` is a list, and it is **alphabetically sorted by the producer**.
Element 0 is *not* the leading or razor protein — MetaMorpheus does not record one. A query like
`protein_accessions[1] AS leading_protein` returns alphabetical rank wearing a biological name.
See [limitations.md §5](limitations.md).

### Counting identifications without normalising for depth

PXD027318 has 378,168 accepted PSMs and PXD036557 has 26,582. "Protein X was seen in more PSMs in
dataset A" is usually a statement about how deep dataset A was sequenced.

### Naming a CTE after a real table

```sql
-- Reads ZERO catalog bytes. Returns a number.
WITH protein_groups_1pct AS (SELECT 99999 AS n) SELECT * FROM protein_groups_1pct;
```

This is not hypothetical — it is why provenance in this system is a fact reported by the server
about itself, never inferred from a query or its output (rule 5 in the [index](README.md)).

---

## 5 · Running queries

```bash
# one-off, read-only, from the CLI
datarepo query /path/to/catalog.duckdb "SELECT * FROM dataset_overview"

# what went into this catalog?
datarepo catalog /path/to/catalog.duckdb

# serve it to an agent
datarepo mcp --catalog /path/to/catalog.duckdb --install
```

Or open it directly — it is an ordinary DuckDB file:

```python
import duckdb
con = duckdb.connect("catalog.duckdb", read_only=True)
con.execute("SELECT catalog_id FROM catalog_meta").fetchone()
```

**Open it `read_only=True`.** `read_only` alone is not a sandbox — DuckDB will still
`read_csv_auto` anything on disk — which is why the MCP server adds
`enable_external_access=false`, row and character caps, and a timeout. See
[mcp.md](mcp.md#what-the-sandbox-does-and-what-it-does-not).

## Where to go next

- [**limitations.md**](limitations.md) — what this catalog cannot tell you, with measured numbers.
  Read it before publishing anything.
- [**schema/core.md**](schema/core.md) — every table and column, generated from the schema.
- [**mcp.md**](mcp.md) — the agent-facing tools and their provenance contract.
- [**build.md**](build.md) — how the catalog is assembled and what a failed check means.
