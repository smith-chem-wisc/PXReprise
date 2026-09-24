# go fixture files

Copied unchanged from go's pre-release delivery (go thread 010, 2026-09-24),
`F:\ClaudeTestBuilds\go-data\prerelease-282b480d\`:

| file | sha256 |
|---|---|
| `fixture1347_go_annotation.tsv` | `98a9341c89aa88171921c994cf2202b3c3219ab405de05a304a2861bb6dcf33d` |
| `fixture1347_go_category_smoke.tsv` | `97538ce700c16cfd93feb979f1fe9c378659efba51ef428c75491f531d110bb4` |

Written by mzLib `trishorts:feat/gene-ontology-dag` at `282b480d` (draft smith-chem-wisc/mzLib#1353)
from mzLib #1347's six-row MetaMorpheus 1.1.11 protein-group fixture: 5 groups, one of them a
contaminant. Their headers say `#!mzlib_release none`, so the reader refuses them unless a test
passes `allow_prerelease=True`. The category file uses go's four-row `smoke` map, which exists to fill
the format: do not test against its categories.
