# go engine fixtures (`GoEngineTests`)

Inputs for the go engine's tests (`go.annotate_groups`, G86). Nothing here was produced by Python: the engine
calls mzLib, and the tests check its output against go's own reader (`Sources/Go.cs`), which `GoTests` checks
against the Python.

| file | what | from |
|---|---|---|
| `go-trimmed.obo` | 29 GO terms, `data-version: releases/2026-07-26` | copied unchanged from mzLib's test data, `mzLib/Test/DataFiles/Ontologies/go-trimmed.obo` at mzLib `0a808fec3` (the 1.0.593 release notes commit; the file came in with #1353, `57d393690`). sha256 `01c11e95ec7068eb02b2aac689258d8b6bd2e6b93d5c28cfbc0bb220bf4d60d6`. mzLib's own PROVENANCE.md says how it was cut from the full go.obo (the is_a + part_of closure of four seeds, so ancestors are the same as in the full file). GO is CC BY 4.0. |
| `fixture_map.tsv` | a three-row category map (`nucleus`, `mitochondrion`, `mitochondrion:inner_membrane`) in mzLib's `GoCategoryMap` format 1 | written for these tests. Every anchor is a live term of `go-trimmed.obo`. |

The searched database with GO in it is not a file here: each test copies `tests/data`, adds GO references to
three entries of `test_human.xml` (`GoEngineTests.GoReferences`), and updates the search provenance's sha256 for
that file, so the ingest reads it as the database the search used.
