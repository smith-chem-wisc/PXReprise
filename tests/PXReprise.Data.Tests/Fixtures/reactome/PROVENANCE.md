# Reactome test fixtures

Two files cut from **Reactome release 96**, copied byte for byte from mzLib 1.0.598's own test fixtures
(`mzLib/Test/DataFiles/Pathways/`, commit `aad4426be2df0e53ada2732d6f4f3a3e97d4ed77`), so `kegg.reactome_release` and
the catalog's Reactome tables are tested offline on real release lines. They are test fixtures, not production data:
`datarepo run kegg.reactome_release` reads the release's real files, and only a release listed in
`ReactomeEngines.Releases`. The tests pass these as a release of their own.

| File | Upstream | Rows | Bytes | sha256 (as committed, LF) |
|---|---|---:|---:|---|
| `ReactomePathways-96-trimmed.txt` | `https://download.reactome.org/96/ReactomePathways.txt` (sha256 `eac3ac07...`) | 25 pathways | 1,365 | `7273f1ca3324ece67f7c6eb97742a2fa6a2ef17af484ed7876dd2d3e9e8756df` |
| `ReactomePathwaysRelation-96-trimmed.txt` | `https://download.reactome.org/96/ReactomePathwaysRelation.txt` (sha256 `72e2da17...`) | 19 pairs | 501 | `bb938866a859265afcd5fa7ae5e80c29d0d2e5fff9bc325ffa0c5331e0d2489c` |

mzLib's rule, unchanged: every pathway in the ancestor closure of six seeds (`R-HSA-77289`, `R-MMU-77289` and
`R-RNO-77289`, Mitochondrial Fatty Acid Beta-Oxidation under Metabolism in three species; `R-HSA-5602358`, under
Disease; `R-HSA-354192`, Integrin signaling, with two parents under two roots; `R-GGA-351470`, a chicken pathway with no
human twin), and every relation line whose parent and child are both kept. 7 roots.

**Licence.** Reactome's data are CC0 (https://reactome.org/license, section 1c). Citation, requested: "ReactomePathways.txt"
and "ReactomePathwaysRelation.txt", Reactome, release 96, https://reactome.org/download-data/ (downloaded 2026-10-25).

`.gitattributes` marks this folder `-text`, so a Windows checkout keeps the bytes the sha256s above name.
