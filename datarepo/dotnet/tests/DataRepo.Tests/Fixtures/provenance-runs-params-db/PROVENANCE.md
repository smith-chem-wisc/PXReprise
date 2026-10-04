# provenance-runs-params-db fixture

Expected outputs for `Sources/Provenance.cs`, `Runs.cs`, `SearchParams.cs` and `ProteinDb.cs`, written by the
Python on 2026-10-04 (Python 3.13.11, lxml 6.1.0, Windows). Nothing here was written by hand except the
inputs under `inputs/`.

## Which Python

- **datarepo 0.32.0** as installed from master (`ae186d9`), and
- the same package with `wip/d37-g81-python` (`3e68781`) laid over it: D37's `definitions_namespace()`,
  `namespace=` on `metric_rows` / `contamination_metric_rows`, `defs.cite()`, and G81's `pep_iterative()`.

The generator ran under both. Every output the two runs share (3,425 leaves: all aging-namespace rows,
schema layouts, runs, search parameters, protein databases) was compared and is identical, which is D37's
requirement that the default namespace changes nothing. `cases.json` is the wip run's output, so it also
has the `pxreprise` rows, `definitions_namespace` and `pep_iterative`.

`Runs.Build`'s `ns` parameter has no Python counterpart: `build/pxreprise` is master's `runs.build` output
with each metric's `definition_id` passed through the wip `defs.cite(defs.BY_ID[id], "pxreprise")`, which is
how D37 says a pipeline count is cited.

## Files

- `cases.json` (CI): synthetic cases plus the repository fixture `tests/data/work_root`. Each value is
  either `{"value": <json.dumps-able result>}` or `{"error": <Python exception name>, "message": <str(e)>}`.
  Paths in messages and in `protein_db.load()` file lists have the repository root replaced by `<repo>`
  and `\` by `/`; the tests normalise the C# output the same way.
  - `provenance`: each document through `schema_version`, `definitions_namespace`, `record_row`
    (`stage_dir_name="04_search"`, `bundle_path="sources/provenance_04_search.json"`, `sha256="ab"*32`),
    `finding_rows`, `metric_rows` at layouts 2 and 3 in both namespaces, and `contamination_metric_rows`
    with `run_names=None` and with `RunNameMap(("run_a", "run_b", "run_c"))`.
  - `runs`: `excluded_files`, then `build(dataset_id, fetch, qc, run_facts, excluded)`. The work_root
    case's `run_facts` are `sdrf.parse(...).run_facts` of its SDRF (read through pyMzLib), stored as input.
  - `assign_enrichment`: eleven rule cases on three runs (`a.raw`, `b.raw`, and `c.mzML`/`c.raw`, which
    share a stem).
  - `search_params`: eighteen sets of task files (`inputs/tasks/*.toml` and the work_root tasks) through
    `modification_rows` (registry: `ModRegistry.from_metamorpheus(tests/data/work_root/mm_settings/1.1.11)`;
    `unimod` records what its `lookup(name).unimod_curie` gave, which is what the C# test passes in),
    `pep_regime`, `tc_ambiguity`, and `pep_iterative` for ten release strings.
  - `provenance_inputs`: `searched_database` and `task_names`.
  - `protein_db`: the `(accession, sequence)` pairs `_iter_uniprot_xml` / `_iter_fasta` yield for
    `inputs/db/edge_cases.xml`, `inputs/db/crap_panel.fasta` and the two work_root databases, the
    `ProteinSequences` state after `read_database` of all four, refusals, `is_contaminant_database`,
    `occurrences`, and `load()` of four provenance documents against `tests/data/work_root`.
  - `enrichment_vocabulary`: `_schema_docs.ENUMS["Enrichment"]["values"]`.
- `real_data.json` (`[Category("RealData")]`, skipped without `F:/aging_data`): ten search folders of
  aging's, read only: `run_2026-09-18/PXD036557/04_search_dll` (`/2`, contamination block),
  `run_2026-09-18/PXD036557/04_search` and `PXD048658/04_search` (`/1`, refused; PXD048658 is TMT data
  searched label-free; MetaMorpheus 1.1.10 and 1.1.9), `PXD036557_n18` (`/2`), `run_2026-09-19/PXD027318`
  (`/3`, human), `run_2026-09-21/PXD047293` (two excluded files, rat, isoform database),
  `PXD051644` (one excluded file), `PXD008934` (no contamination block), `PXD007188` (human, agingPTM
  proteome, tier 3 isoforms) and `PXD018315` (mouse, the proteome with B/Z residues). Per folder: record
  rows of its `02_fetch`, `02b_qc` and search provenance, findings, runs (with the SDRF's `run_facts` as
  input), enrichment from aging's `instance/manifest.yaml` entry, metrics and contamination rows in both
  namespaces, search parameters (task files as `ingest._task_files` finds them; registry from
  `F:/aging_data/mm_settings/<metamorpheus_version>`), and for four folders a digest of
  `protein_db.load()`: entry counts, file sha256s, and sha256 over `acc\tstatus\tseq|seq\n` in
  insertion order. `databases` holds, for every database under `F:/aging_data/db` plus MetaMorpheus
  1.1.11's own contaminant XML, the file's sha256 and the reader's entry count, ordered-pair sha256
  (`acc\tseq\n`), distinct pairs and distinct accessions. No sequence data is stored.

## How

The generator scripts (`gen_fixtures.py`, `gen_real.py`, `db_digest.py`) lived in the session scratchpad
and were not committed (PORTING.md: no new Python in the repository). They import the modules above and
call each public function on the inputs listed, round-tripping every input through `json` first so both
languages see the same values, and write `json.dumps(..., ensure_ascii=False)`. `real_data.json` was
re-dumped compactly with `build/pxreprise` reduced to its metrics (its runs are `build/aging`'s, checked).

Beyond these ten folders, the same comparison was run once over every search folder aging has
(`run_2026-09-1*` and `run_2026-09-2*`, with every searched database loaded) and is reported in the
commit message, not stored.

## mzLib was measured and not used for the protein database

mzLib 1.0.593 `ProteinDbLoader.LoadProteinXML(path, true, DecoyType.None, [], isContaminant, [], out _)`
over the same 18 real files: with the default `maxHeterozygousVariants = 4` it adds variant proteins (264
contaminant entries become 579, 49 human isoform entries 53); with 0 it matches the Python's ordered pairs
exactly on 14 of 18 (same count, order and accessions everywhere) but rewrites `B` and `Z` to `X`
(`SanitizeAminoAcidSequence`) in the two mouse proteome files (6 proteins: `Q70FJ1`, `P01643`, `P01658`, ...)
and the two rat ones (1 protein, `P01681`). So `ProteinDb.cs` ports the Python reader.

Regenerate only on purpose, and say why here.
