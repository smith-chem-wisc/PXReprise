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

`peptidoforms` and `protein_groups` hold **everything the producer wrote**, including rows above the
FDR threshold and decoys. The views `peptidoforms_1pct` and `protein_groups_1pct` apply a 1% rule,
once, in one place. `protein_groups_1pct` is the search engine's own q-value rule. From catalog format 17,
`peptidoforms_1pct` is each search's own confidence rule (`datasets.confidence_rule`): the PEP q-value
where the search trained PEP, otherwise its notch q-value, strictly below 0.01. It is not the engine's own
peptide count, which is in `metrics` (`name = 'peptides'`, `scope = 'dataset'`), and two datasets on
different rules rest on different q-values.

```sql
SELECT 'peptidoforms' AS t, (SELECT count(*) FROM peptidoforms) AS raw,
                            (SELECT count(*) FROM peptidoforms_1pct) AS accepted
UNION ALL SELECT 'protein_groups', (SELECT count(*) FROM protein_groups),
                                   (SELECT count(*) FROM protein_groups_1pct);
```

```
             t      raw  accepted
  peptidoforms   911187    198444
protein_groups    31318     19246
```

**Four-fifths of the peptidoforms are below the bar.** Query `peptidoforms` instead of
`peptidoforms_1pct` and you will roughly quintuple your answer. Unless you are specifically studying the
decoy distribution or the score tail, use the view.

**PSMs are not in the catalog** (catalog format 13). Each dataset's counts are in `dataset_overview`:
`n_psms_1pct` at the same acceptance rule, and `n_psms_all` for every row. On the catalog above that is
1,361,409 accepted of 2,707,851, half of them below the bar. The PSMs themselves are each bundle's
`psms.parquet`, which DuckDB reads directly: `catalog_bundles.path` is each dataset's bundle folder.

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

`unique_peptides` is MetaMorpheus's own count for the group. It is not `peptidoforms.is_unique`,
which from datarepo 1.0.0 means one gene in the searched sequences; see
[limitations.md §6](limitations.md#6--unique-means-one-gene-in-the-searched-sequences).

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
"how reproducible is this?" question needs no join. The counts are of **deposits**: a deposit searched
in parts (one dataset per part, keyed `<accession>-<group>`) counts once, and `dataset_ids` lists each
part. `deposits` lists every deposit with its datasets.

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

### Every ortholog of a protein, with what the repository saw of each

From catalog format 17, `protein_orthologs` joins every target accession that has a gene resolution to its
orthologs in each other species of the registered orthology snapshot (Ensembl Compara, through logs), and to
the accessions that resolve to each ortholog gene:

```sql
-- Every link logs wrote, both of its views together (no view filter).
SELECT target_species, status, resolution_source, resolution_xref_agreement, relationship_type,
       partner_gene_name, partner_accession, partner_resolution_source, partner_xref_agreement,
       partner_n_datasets_1pct
FROM protein_orthologs WHERE protein_accession = 'P14136'   -- human GFAP
ORDER BY target_species, partner_accession;
```

```
target_species     status        resolution_source            resolution_xref_agreement  relationship_type  partner_gene_name  partner_accession  partner_resolution_source    partner_xref_agreement  partner_n_datasets_1pct
-----------------  ------------  ---------------------------  -------------------------  -----------------  -----------------  -----------------  ---------------------------  ----------------------  -----------------------
mus_musculus       has_ortholog  search_database_dbreference  agrees                     ortholog_one2one   Gfap               P03995             search_database_dbreference  agrees                  59
rattus_norvegicus  has_ortholog  search_database_dbreference  agrees                     ortholog_one2one   Gfap               P47819             search_database_dbreference  agrees                  25
```

That ran on a 1.5.0 folder catalog of aging's 192 re-ingested deposits, where `protein_orthologs` holds 180,930 rows
for 64,198 accessions; it answers in about 0.1 s.

**Each row is one gene link logs wrote, and the table holds both of its views together.** To read one view, filter
on the view columns, the accession's and the partner's:

- the search database's view: `resolution_source = 'search_database_dbreference'` (and `partner_resolution_source`);
- logs' v2 default view, aging's: `resolution_xref_agreement IN ('agrees', 'silent')` (and `partner_xref_agreement`).
  It leaves out the links Ensembl contradicts. A v1 row (`gene_resolution_definition_id`) has only
  `resolution_xref_agrees`, where false covers both `silent` and `disagrees`.

A view can leave an accession with no gene (one only Ensembl's xref links, `resolution_source = 'ensembl_xref'`, has
none in the search database's view) and an ortholog with no accession, so keep the `gene_id IS NULL` rows and read
the unfiltered rows before calling anything absent.

**A view applied as a plain `WHERE` drops rows; it does not turn them into refusals.** On aging's store (logs 024):
- under v2's default view, P60484-2 (a PTEN isoform) loses its only gene. Its own xref links only a gene outside the
  gene set, so its PTEN link reads `disagrees`;
- 138 (accession, ortholog gene) pairs lose every partner accession, so the ortholog itself disappears from the answer.

To read one view and still see what it leaves out, filter the partner columns inside a `LEFT JOIN` rather than in the
`WHERE`, and report an accession whose rows a view removes as "no gene in this view", not as absent. The status is
Compara's own: human PTEN, for example, is `no_edge_in_shared_tree` towards mouse and rat in Compara 116 itself.

The evidence for each ortholog is one join away:

```sql
-- Every link logs wrote, both views together; add a view's filter to `asked` to read one.
WITH asked AS (
    SELECT * FROM protein_orthologs WHERE protein_accession = 'P14136'   -- human GFAP
),
groups AS (
    SELECT a.partner_accession, max(g.sequence_coverage) AS best_group_coverage
    FROM asked a
    JOIN protein_groups_1pct g ON list_contains(g.protein_accessions, a.partner_accession)
    GROUP BY a.partner_accession
),
peptides AS (
    SELECT a.partner_accession,
           count(DISTINCT p.base_sequence) AS n_sequences_1pct,
           count(DISTINCT p.dataset_id)    AS n_datasets_peptides
    FROM asked a
    JOIN peptidoforms_1pct p ON list_contains(p.protein_accessions, a.partner_accession)
    GROUP BY a.partner_accession
),
sites AS (
    SELECT a.partner_accession,
           count(DISTINCT s.position || ':' || s.modification_name) AS n_ptm_sites,
           list_sort(list(DISTINCT s.modification_name))            AS modifications
    FROM asked a
    JOIN ptm_sites s ON s.protein_accession = a.partner_accession
    WHERE s.target_decoy = 'target' AND s.best_q_value <= 0.01
    GROUP BY a.partner_accession
)
SELECT a.target_species, a.status, a.relationship_type, a.partner_gene_name, a.partner_accession,
       a.partner_n_datasets_1pct, round(g.best_group_coverage, 2) AS best_group_coverage,
       p.n_sequences_1pct, p.n_datasets_peptides, s.n_ptm_sites, s.modifications
FROM asked a
LEFT JOIN groups g ON g.partner_accession = a.partner_accession
LEFT JOIN peptides p ON p.partner_accession = a.partner_accession
LEFT JOIN sites s ON s.partner_accession = a.partner_accession
ORDER BY a.target_species, a.partner_gene, a.partner_accession;
```

**Read `status` before reading an empty column as "not detected".** Every accession has at least one row per
other species. A status other than `has_ortholog` says why there is no ortholog: `no_gene` (no gene of its species
is linked to the accession at all, in either view), `no_edge_in_shared_tree`, `tree_lacks_target_species` or
`not_in_any_tree`. A `has_ortholog` row with no `partner_accession` is an ortholog in Compara with no accession of
it in any search here. Only a row with a `partner_accession` and no evidence means the repository searched for that
protein and accepted nothing. **No rows at all** means the accession is not one the repository resolved to a gene (it is
not in `protein_genes`), or that the catalog serves no snapshot (`orthology_species` is empty), never that it has
no orthologs. One-to-many orthologs give a row each
(`relationship_type`), and isoform accessions of a partner gene each have their own row. The site rule here
(target, `best_q_value` at most 0.01) is the reader's: `ptm_sites` holds every site, fixed modifications included.
`best_group_coverage` is MetaMorpheus's coverage (0 to 1) of an accepted group holding the accession. On the
catalog above it gives mouse P03995 94 sequences in 56 datasets and 286 sites, and rat P47819 71 sequences in 25
datasets and 140 sites, in 10 to 20 s: nearly all of it is the scan of `peptidoforms_1pct` (8.4 million of 82
million peptidoforms) that `list_contains` needs, since a list column takes no index.

### The GO terms of a protein, and the proteins of a GO term

From catalog format 17 GO is reference data. `go_accession_terms` holds each searched database file's entries and the
terms each is annotated to directly (all three aspects), once per file; `go_closure` holds every term's ancestors; and
`protein_localizations` is go's group annotation for each search, cellular component only, derived at build. Read them
in that order: the database says what an entry is annotated to, `protein_localizations` says which accession of
which accepted group carried the term in which search.

**The GO terms of one protein, as its database states them**, with each direct term's ancestors:

```sql
SELECT a.accession, c.ancestor_go_id AS go_id, t.go_name, t.aspect,
       bool_or(c.ancestor_go_id = a.go_id) AS direct,
       list_sort(list(DISTINCT a.evidence) FILTER (WHERE c.ancestor_go_id = a.go_id)) AS evidence,
       count(DISTINCT a.database_sha256) AS n_files
FROM go_accession_terms a
JOIN go_closure c ON c.go_id = a.go_id
JOIN go_terms t ON t.go_id = c.ancestor_go_id
WHERE a.accession = 'P04406' AND NOT a.inherited          -- human GAPDH; its own terms
GROUP BY ALL
ORDER BY t.aspect, direct DESC, go_id;
```

An entry with no GO has one row with `go_id` NULL, which this join drops: check `go_accession_terms` for the accession
before reading no rows as "no GO". An isoform with no GO of its own takes its entry's terms (go D37); query the entry
(`isoform_of`), since the file's own `inherited` rows exist only when the isoform and its entry are in one file.

**The same protein in each search that held it**, with go's qualifiers for that accession in that group:

```sql
SELECT l.dataset_id, l.compartment, t.go_name, l.protein_group, l.n_with, l.n_members,
       l.propagated, l.inherited, l.evidence, l.q_value
FROM protein_localizations l
JOIN go_terms t ON t.go_id = l.compartment
WHERE l.protein_accession = 'P04406'
ORDER BY l.dataset_id, l.propagated, l.compartment;
```

`n_with < n_members` means the term is true of some members of the group, not of the whole group. `propagated` means
the accession carries the term only through a more specific one, `inherited` that it took the term from its entry
(an isoform or a sequence variant). No row for a dataset that searched the protein means it was in no group, the group
has no CC term (`go_group_status.n_cc_terms = 0`), or the search is not served GO (`catalog_checks` of kind
`engine-coverage`).

**The proteins of a GO term across datasets**, counted once per deposit, from accepted groups only, contaminant and
entrapment groups left out (go D3):

```sql
SELECT l.protein_accession,
       count(DISTINCT split_part(l.dataset_id, '-', 1))          AS n_deposits,
       bool_or(NOT l.propagated)                                AS annotated_to_the_term_itself,
       list_sort(list(DISTINCT l.dataset_id))                   AS datasets
FROM protein_localizations l
JOIN go_group_status s ON s.bundle_id = l.bundle_id AND s.protein_group = l.protein_group
WHERE l.compartment = 'GO:0005739'          -- mitochondrion: its descendants' proteins included, by propagation
  AND l.q_value <= 0.01                     -- an accepted protein group
  AND NOT s.is_contaminant AND len(s.entrapment_members) = 0
GROUP BY l.protein_accession
ORDER BY n_deposits DESC, l.protein_accession;
```

`protein_localizations` holds every non-decoy group whatever its q-value, so the `q_value` filter is the reader's. For a
biological process or molecular function term, join the members to the database instead (each file's own rows, so an
isoform held in another file than its entry is missing here; for CC, `protein_localizations` has it):

```sql
WITH members AS (
    SELECT DISTINCT dataset_id, bundle_id, unnest(protein_accessions) AS accession FROM protein_groups_1pct
)
SELECT m.accession, count(DISTINCT split_part(m.dataset_id, '-', 1)) AS n_deposits
FROM members m
JOIN go_group_members r ON r.bundle_id = m.bundle_id AND r.accession = m.accession
JOIN dataset_databases d ON d.bundle_id = m.bundle_id AND d.role = 'target'
JOIN go_accession_terms a ON a.database_sha256 = d.sha256 AND a.accession = r.annotated_as
JOIN go_closure c ON c.go_id = a.go_id
WHERE c.ancestor_go_id = 'GO:0006096'       -- glycolytic process, and every term below it
GROUP BY m.accession ORDER BY n_deposits DESC;
```

**By gene.** `go_accession_terms.database_sha256` is `gene_resolutions.search_database_sha256`, so a database entry's
terms join logs' gene resolution of the same file. Keep an accession with no gene as a row (logs 023: a no-gene row
counts only when the accession has no gene at all in that file):

```sql
WITH genes AS (
    SELECT search_database_sha256, accession, gene_id, gene_symbol, outcome
    FROM gene_resolutions
    QUALIFY gene_id IS NOT NULL OR NOT bool_or(gene_id IS NOT NULL) OVER (PARTITION BY search_database_sha256, accession)
)
SELECT a.accession, g.outcome, g.gene_id, g.gene_symbol, a.go_id, t.go_name, a.aspect, a.evidence
FROM go_accession_terms a
LEFT JOIN genes g ON g.search_database_sha256 = a.database_sha256 AND g.accession = a.accession
LEFT JOIN go_terms t ON t.go_id = a.go_id
WHERE a.go_id = 'GO:0005739'
ORDER BY g.gene_symbol NULLS FIRST, a.accession;
```

A NULL `outcome` means logs has not resolved that file (`catalog_checks` of kind `engine-coverage`), not that the
accession has no gene.

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
SELECT count(*) FROM peptidoforms;        -- 911,187   includes decoys and everything above 1% FDR
SELECT count(*) FROM peptidoforms_1pct;   -- 198,444   the number you meant
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
