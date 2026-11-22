# Contributing to dataRepo

**dataRepo's code now lives here, in smith-chem-wisc/PXReprise** (since #4, 2026-11-13): the projects
`src/PXReprise.*`, the tests `tests/PXReprise.Data.Tests`, the schema `schemas/datarepo/datarepo.yaml`,
and these docs. Changes come in as pull requests; datarepo releases are tagged `datarepo-v<version>`.
smith-chem-wisc/dataRepo keeps the project's design records, threads and results.

Thanks for helping. dataRepo is early, so the most useful contributions right now are schema reviews,
benchmark questions the schema can't yet answer, and producer file formats the ingester mishandles.

## Ground rules

1. **The schema YAML is the source of truth.** `schemas/datarepo/datarepo.yaml` (core) and
   `schemas/datarepo/study/*.yaml` (study layers). Everything in `docs/data/schema/` is generated from them.
2. **Keep the core generic.** If a column only makes sense for one study (age, disease model, a
   specific clock), it belongs in a study layer as a table keyed on core IDs, never as a new core column.
3. **Store, don't compute.** dataRepo doesn't define metrics, run statistics or build annotations. A
   new number needs a `definition_id` from the project that owns its meaning.
4. **Missing is not zero.** Never write 0 for "not measured".
5. **No data in this repo.** Bundles, releases and DOIs belong to an instance (decision D8).
   `schemas/datarepo/examples/`, `tests/datarepo-instance/` and `tests/PXReprise.Data.Tests/Fixtures/` hold only
   tiny fixtures.
6. **Parsing producer formats belongs to mzLib.** If the ingester reads a producer file itself, the
   reason must be recorded in `Readers` (`src/PXReprise.Ingest/Readers.cs`) and in the bundle's
   reader log, along with the request that would let the in-house code be deleted.
7. **Never invent an identifier.** An unresolved modification, an unmatched run or a protein group
   the producer did not build is reported as a finding, not filled in with a plausible guess.

## Making a schema change

```bash
# 1. edit schemas/datarepo/datarepo.yaml (or a study layer in schemas/datarepo/study/)
#    every class, column and vocabulary needs a description: agents read them

# 2. regenerate the program's table definitions and column descriptions and the schema reference
#    pages, and commit them
dotnet run --project src/PXReprise.SchemaGen            # writes src/PXReprise.Bundle/Generated/*.g.cs
                                                        # and docs/data/schema/*.md
dotnet run --project src/PXReprise.SchemaGen -- --check # exits 1 if any of them is stale

# 3. note the change in docs/data/CHANGELOG.md's unreleased section
```

`tests/PXReprise.Data.Tests` fails if `Tables.g.cs`, `SchemaDocs.g.cs` or a page in `docs/data/schema/`
no longer matches the schema.

**Changing a command's arguments or help text** changes `docs/data/cli.md`, which is generated from the
program's own `-h` output:

```bash
dotnet run --project src/PXReprise.SchemaGen -- --cli            # builds the CLI, then writes docs/data/cli.md
dotnet run --project src/PXReprise.SchemaGen -- --cli --check    # exits 1 if it is stale
```

A new command must also be placed in a group in `CliDocs.Groups` (`src/PXReprise.SchemaGen/CliDocs.cs`);
the generator refuses a command it cannot place. `tests/PXReprise.Data.Tests` fails if `cli.md` is stale.

Not yet replaced since the move: the Python tools that linted the schema (LinkML) and validated
`schemas/datarepo/examples/` stayed in smith-chem-wisc/dataRepo (no Python here: decision 0008).

## Working on the ingester

dataRepo is a C# program: the `src/PXReprise.*` data projects in `PXReprise.slnx` (its `/src/data/`
folder), with the `datarepo` command line in `src/PXReprise.DataCli`. The Python package stayed in
smith-chem-wisc/dataRepo, frozen at 0.32.0. You need the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet test tests/PXReprise.Data.Tests                                  # dataRepo's tests, including the docs (DocsTests)
dotnet test tests/PXReprise.Seam.Tests                                  # the engine and datarepo together
dotnet build src/PXReprise.DataCli -c Release -o build/datarepo         # the program, as build/datarepo/datarepo
```

`tests/PXReprise.Seam.Tests` runs both programs where they meet. The engine's search stage runs on the fixture's
recorded MetaMorpheus search (the fake CMD replays it), the engine writes the deposit's manifest entry, and the real
`datarepo` ingests it with the batch's own command and builds a catalog. What datarepo makes of it is committed in
`tests/PXReprise.Seam.Tests/Expected/PXD999999.txt`. A change on either side that moves it fails there: a manifest field
or a note the engine writes, a provenance field datarepo reads, or any row datarepo writes. If the move is meant, rewrite
the file with `PXREPRISE_SEAM_WRITE_EXPECTED=1` and say in the pull request why it moved.

[PORTING.md](PORTING.md) holds the conventions the port followed (it still names the projects
`DataRepo.*`, as they were then).

`tests/datarepo-instance/` is a miniature producing instance: a manifest with relative roots, real MetaMorpheus
`.psmtsv` rows trimmed from PXD036557, and hand-made FlashLFQ tables whose edge cases are the ones
that matter (a zero intensity, a `NotDetected` cell, a q-value of exactly zero, a decoy group, a
contaminant group). Add to it rather than mocking a reader.

Read [ingest.md](ingest.md) first; it says what each rule is for.

If a change affects which benchmark questions can be answered, update
[`design/SCHEMA_COVERAGE.md`](https://github.com/smith-chem-wisc/dataRepo/blob/master/design/SCHEMA_COVERAGE.md).

## Style

- Table names are `UpperCamelCase`; columns are `snake_case`.
- Enum values keep the field's own spelling (`DDA`, `TMT`, `MBR`), because that's what people and agents search for.
- Ontology-backed columns hold CURIEs (`NCBITaxon:9606`, `UBERON:0001134`, `UNIMOD:21`).
- Write descriptions in plain English, one sentence where possible. Say what the value *means*, not only its type.

## Asking or proposing

Open a GitHub issue. Requests between dataRepo and its sibling projects go through the thread folders
([`design/threads/`](https://github.com/smith-chem-wisc/dataRepo/tree/master/design/threads)), whose convention is described in the aging repo.
