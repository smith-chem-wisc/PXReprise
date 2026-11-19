# go's view fixture (`GoCatalogTests`)

go's own fixture for the catalog's GO join, copied byte for byte from go's repository (`data/fixtures/go-view-v1-a83d1c63/`,
go commit `52e7bf3`, 2026-10-25). go's DEFINITIONS.md ("Computing `DEF-GROUP-GO-ANNOTATION v1` rows from the stored
tables") names it as the test: the join of the four tables must give `expected_go_annotation.tsv`, mzLib's
`GoGroupAnnotator` output, row for row, so the group definition keeps its id.

| file | what | sha256 |
|---|---|---|
| `go_accession_terms.tsv` | (a): one database's direct terms per entry (`go_accession_terms_format 1`) | `3b79200169998674769edd477ace4cbc36c3a370ed17f204bcfa8a7da32decf1` |
| `member_resolution.tsv` | (c): each group member's entry (`member_resolution_format 1`) | `c8a954fce27169244edee9a0c784911387d578751fdc98dc1fae2ac9989b1e66` |
| `go_terms.tsv` | the release's terms (`go_terms_format 1`) | `cea99af8a11e92aa4745a1eb3c8085c3d7d3559c78de6d2129532da0fd524bfe` |
| `go_closure.tsv` | the release's is_a + part_of closure (`go_closure_format 1`) | `37ff6e2a909a644de1853012eb64e4bfbee7bb4d033bef70a9857082ea9a845a` |
| `protein_groups.tsv` | the 18 groups: name, `;`-joined members, contaminant mark, q-value | `eb97d0fa61fec0724c1fd3c7ad319db0fe160495339ab39ddeed3f983d69f569` |
| `expected_go_annotation.tsv` | the annotator's 165 rows (`go_annotation_format 1`) | `45100cc78eafa8dc9bd9b77e465f64b91fa81a61360187b30a183bfd53854130` |

All were written by mzLib `a83d1c63` (PR #1497, pre-release: `#!mzlib_release none`), from mzLib's
`TestGoPerDatabaseTables.Fixture()`: synthetic rows, for building and testing a reader only (GO-A1). The ontology is
`../go-engine/go-trimmed.obo` (the same file, sha256 `01c11e95...`). The fixture reaches every rule of the definition and
all four statuses; its database is pre-#1541, so the entrapment entry `X11111` still carries GO.
