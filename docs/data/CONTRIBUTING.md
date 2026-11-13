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
6. **Parsing producer formats belongs to mzLib.** If the ingester reads a producer file itself, the
   reason must be recorded in `Readers` (`dotnet/src/DataRepo.Ingest/Readers.cs`) and in the bundle's
   reader log, along with the request that would let the in-house code be deleted.
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

# 3. regenerate the reference docs AND the program's table definitions, and commit them
python tools/build_docs.py
dotnet run --project dotnet/src/DataRepo.SchemaGen

# 4. note the change under "Unreleased" in CHANGELOG.md
```

CI runs the same checks. It also confirms that `examples/invalid/` still **fails** validation, that
`docs/schema/` and `dotnet/src/DataRepo.Bundle/Generated/` match the schema, and that the ingester
output in `examples/ingested_bundle.yaml` (written by the Python release) validates.

## Working on the ingester

dataRepo is a C# program (`dotnet/`, solution `DataRepo.slnx`); the Python package under `src/` is
frozen at 0.32.0 and only tested at that tag. You need the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet test dotnet/DataRepo.slnx                     # every test, including the docs (DocsTests)
dotnet build dotnet/src/DataRepo.Cli -c Release -o build/cs   # the program, as build/cs/datarepo
python tools/build_cli_docs.py                       # after changing a command's arguments or help
```

`dotnet/PORTING.md` holds the conventions the port follows.

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
