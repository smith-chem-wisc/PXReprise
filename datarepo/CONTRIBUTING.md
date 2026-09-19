# Contributing to dataRepo

Thanks for helping. dataRepo is early, so the most useful contributions right now are schema reviews,
benchmark questions the schema can't yet answer, and producer file formats the ingester mishandles.

## Ground rules

1. **The schema YAML is the source of truth.** `schema/datarepo.yaml` (core) and
   `schema/study/*.yaml` (study layers). Everything in `docs/schema/` is generated from them.
2. **Keep the core generic.** If a column only makes sense for one study (age, disease model, a
   specific clock), it belongs in a study layer as a table keyed on core IDs, never as a new core column.
3. **Store, don't compute.** dataRepo doesn't define metrics, run statistics or build annotations. A
   new number needs a `definition_id` from the project that owns its meaning.
4. **Missing is not zero.** Never write 0 for "not measured".
5. **No data in this repo.** Bundles, releases and DOIs belong to an instance (decision D8).
   `examples/` and `tests/data/` hold only tiny fixtures.
6. **Parsing producer formats belongs to pyMzLib.** If the ingester reads a producer file itself, the
   reason must be recorded in `src/datarepo/readers.py` and in the bundle's reader log, along with the
   request that would let the in-house code be deleted.
7. **Never invent an identifier.** An unresolved modification, an unmatched run or a protein group
   the producer did not build is reported as a finding, not filled in with a plausible guess.

## Making a schema change

```bash
pip install -r requirements-dev.txt

# 1. edit schema/datarepo.yaml (or a study layer)
#    every class, column and vocabulary needs a description: agents read them

# 2. check
linkml-lint --config .linkmllint.yaml schema/datarepo.yaml
linkml-lint --config .linkmllint.yaml schema/study/aging.yaml
linkml-validate -s schema/datarepo.yaml -C Bundle examples/minimal_bundle.yaml

# 3. regenerate the reference docs AND the ingester's Arrow schemas, and commit them
python tools/build_docs.py
python tools/build_tables.py

# 4. if the change affects what the ingester writes, regenerate the example bundle (needs pyMzLib)
python tools/build_example_bundle.py

# 5. note the change under "Unreleased" in CHANGELOG.md
```

CI runs the same checks. It also confirms that `examples/invalid/` still **fails** validation, that
`docs/schema/` and `src/datarepo/_tables.py` match the schema, and that real ingester output
(`examples/ingested_bundle.yaml`) validates.

## Working on the ingester

```bash
pip install -e . -r requirements-dev.txt
pip install pymzlib          # parses .psmtsv; `datarepo doctor` says whether its bridge is built
pytest -q -rs                # tests that parse .psmtsv skip without the bridge
```

`tests/data/` is a miniature producing instance: a manifest with relative roots, real MetaMorpheus
`.psmtsv` rows trimmed from PXD036557, and hand-made FlashLFQ tables whose edge cases are the ones
that matter (a zero intensity, a `NotDetected` cell, a q-value of exactly zero, a decoy group, a
contaminant group). Add to it rather than mocking a reader.

Read [docs/ingest.md](docs/ingest.md) first; it says what each rule is for.

If a change affects which benchmark questions can be answered, update
[`design/SCHEMA_COVERAGE.md`](design/SCHEMA_COVERAGE.md).

## Style

- Table names are `UpperCamelCase`; columns are `snake_case`.
- Enum values keep the field's own spelling (`DDA`, `TMT`, `MBR`), because that's what people and agents search for.
- Ontology-backed columns hold CURIEs (`NCBITaxon:9606`, `UBERON:0001134`, `UNIMOD:21`).
- Write descriptions in plain English, one sentence where possible. Say what the value *means*, not only its type.

## Asking or proposing

Open a GitHub issue. Requests between dataRepo and its sibling projects go through the thread folders
([`design/threads/`](design/threads/)), whose convention is described in the aging repo.
