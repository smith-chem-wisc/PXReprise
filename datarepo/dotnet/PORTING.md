# Porting a Python module to C# (D41)

The plan is `design/CSHARP_PORT.md`. Phase 2 ports the ingest path (`src/datarepo/ingest.py` and what it
calls) into `DataRepo.Ingest`. The bar is **the same rows from the same inputs**, checked against what the
Python 0.32.0 actually produces, never against what we expect it to produce.

## Conventions

- **One C# file per Python module**, namespace `DataRepo.Ingest` (sources in `DataRepo.Ingest.Sources`).
  A module's functions become static methods of a static class named after the module
  (`sources/runs.py` -> `Sources/Runs.cs`, `static class Runs`), PascalCase of the Python name
  (`excluded_files` -> `ExcludedFiles`). Private helpers keep their meaning; names may be C#-shaped.
- **Values cross as Python's would.** A row is `DataRepo.Bundle.Row` (a `Dictionary<string, object?>`).
  `None` -> `null`, `str` -> `string`, `int` -> `long`, `float` -> `double`, `bool` -> `bool`, `list`/`tuple`
  -> `List<object?>` (or a typed list when no row sees it), `dict` -> `Dictionary<string, object?>` keeping
  insertion order. JSON is read with `System.Text.Json` into those same plain types (a JSON integer is a
  `long`, any other number a `double`).
- **Python's text forms matter.** `str(x)` is `PyFormat.Str(x)`, `json.dumps` is `PyFormat.Json`, float
  `repr` is `PyFormat.FloatRepr`. Anything that reaches a row or a hash must reproduce Python's bytes.
- **Messages are kept word for word** (findings, refusals, notes): they are rows or operator-facing text.
  Docstrings become `<summary>`/`<remarks>`, keeping the reasons; that prose is the project's memory.
- **Errors**: `IngestError` -> `IngestException`, `UnsupportedProvenance` -> `UnsupportedProvenanceException`,
  `ManifestError` -> `ManifestException`, `ReaderUnavailable` -> `ReaderUnavailableException`,
  `CatalogError` -> `CatalogException`, `QueryRefused` -> `QueryRefusedException`, `QueryTimeout` ->
  `QueryTimeoutException` (all in `DataRepo.Bundle`); the MCP server's `ToolError` -> `ToolException`
  (`DataRepo.Mcp`). Where a Python name reaches an agent (the MCP error payload's `error`), it is the Python
  name (`Mcp.ErrorName`).
- **Producer files are read with mzLib** (NuGet `mzLib` 1.0.593), never with a new in-house parser for a
  format mzLib reads. Where the Python read a file through pyMzLib, the C# reads it through the same mzLib
  type and projects it the way pyMzLib's bridge did, so the module above it sees the same values. The
  bridge source at v0.4.0 (built on mzLib 1.0.593) is the reference.
- **No new Python in the product.** Python may be run once to produce a test's expected output (a JSON
  fixture under `tests/DataRepo.Tests/Fixtures/<module>/` with a `PROVENANCE.md` saying how), never at test
  time and never shipped.
- **Tests are NUnit**, one file per module, comparing against those Python-produced fixtures. A test that
  only checks what the C# was written to do proves nothing about parity.
- Line endings: never write C# through a shell heredoc or Python `write_text` on Windows (it mangles `\r\n`
  escapes and writes CRLF). Use the editor tools.
