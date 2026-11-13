# go fixture files

Copied unchanged from go's pre-release delivery (go thread 016, 2026-10-02, GO-D1),
`F:\ClaudeTestBuilds\go-data\prerelease-c5b16451\`, replacing the `prerelease-282b480d` copies of go 010:

| file | sha256 |
|---|---|
| `fixture1347_go_annotation.tsv` | `43ebad3d3dd76fc6df0e524758e9819dcb50636e25f2afe5061190edf04e1d92` |
| `fixture1347_go_category_smoke.tsv` | `76d336cf12846ebbd58c2b663e7f76733b7fd52fb64cf75f1bda09dd804818a0` |

Written by mzLib at `c5b16451` (mzLib#1366, after #1353 merged) from mzLib #1347's six-row
MetaMorpheus 1.1.11 protein-group fixture: 5 groups, 563 rows, one group a contaminant. The table has
go's 19 columns (D33's `accession_direct`, `accession_inherited`, `evidence_by_member`; D36's
`entrapment_members`). Their headers say `#!mzlib_release none`, so the reader refuses them unless a
test passes `allow_prerelease=True`. The category file uses go's four-row `smoke` map, which exists to
fill the format: do not test against its categories.

What these files do not show (go 015 section 5): no member is an isoform or a variant, no run used
entrapment, and the run was strict. So `accession_inherited` and `entrapment_members` are empty,
`inherited` is `false` on every row, and `#!unresolved_go_ids` is absent; the tests add the last two
by editing a copy.
