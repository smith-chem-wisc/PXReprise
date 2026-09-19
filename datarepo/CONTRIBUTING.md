# Contributing to dataRepo

Thanks for helping. dataRepo is in its design phase, so the most useful contributions right now are
schema reviews and benchmark questions the schema can't yet answer.

## Ground rules

1. **The schema YAML is the source of truth.** `schema/datarepo.yaml` (core) and
   `schema/study/*.yaml` (study layers). Everything in `docs/schema/` is generated from them.
2. **Keep the core generic.** If a column only makes sense for one study (age, disease model, a
   specific clock), it belongs in a study layer as a table keyed on core IDs, never as a new core column.
3. **Store, don't compute.** dataRepo doesn't define metrics, run statistics or build annotations. A
   new number needs a `definition_id` from the project that owns its meaning.
4. **Missing is not zero.** Never write 0 for "not measured".
5. **No data in this repo.** Bundles, releases and DOIs belong to an instance (decision D8).
   `examples/` holds only tiny, illustrative fixtures.

## Making a schema change

```bash
pip install -r requirements-dev.txt

# 1. edit schema/datarepo.yaml (or a study layer)
#    every class, column and vocabulary needs a description: agents read them

# 2. check
linkml-lint --config .linkmllint.yaml schema/datarepo.yaml
linkml-lint --config .linkmllint.yaml schema/study/aging.yaml
linkml-validate -s schema/datarepo.yaml -C Bundle examples/minimal_bundle.yaml

# 3. regenerate the reference docs and commit them with the change
python tools/build_docs.py

# 4. note the change under "Unreleased" in CHANGELOG.md
```

CI runs the same checks. It also confirms that `examples/invalid/` still **fails** validation, and that
`docs/schema/` matches the schema.

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
