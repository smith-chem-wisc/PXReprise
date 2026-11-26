# Released schemas, frozen

Each folder here holds a released core schema version exactly as it was released, and is **never edited**. Bundles record only
their `schema_version`. This is where to read the schema text behind a version, without digging through git history.

- `<version>/datarepo.yaml` is the core schema at that version.
- `<version>/study/aging.yaml` is the ageing study layer as it stood beside it. It has its own `version:` key.
- `<version>/SHA256SUMS` holds the checksum of every file in the folder. `FrozenSchemaTests` fails if a frozen file changes,
  if a file is added or removed, or if a folder's `datarepo.yaml` does not carry its folder's version.

The working schema, `../datarepo.yaml`, is always the next version. When a version is released, copy the core and study
files here under the new version, with their `SHA256SUMS`, and add the version to `FrozenSchemaTests.Released`.

## Which versions are here, and why

A version is here when a datarepo release carried it:
- 0.0.2 and 0.0.3 were carried by the first releases (0.2.x and 0.3.x, 2026-09-19). The ageing project's corpus release
  v0.1 records schema 0.0.3.
- 0.0.4 to 0.0.16 were each carried by at least one dated release in `docs/data/CHANGELOG.md` (0.2.0 to 1.4.5).

Each file is the text at the last commit that carried that version number. Where a release tag exists, that text is byte for
byte the text at the version's last tag: v0.32.0 for 0.0.13, v1.0.0 for 0.0.14, v1.3.6 for 0.0.15, v1.4.5 for 0.0.16.

Left out:
- 0.0.1 was never released;
- 0.0.17, released with PXReprise 1.0.0 (2026-11-26), is still the working version. `FrozenSchemaTests` refuses a frozen
  copy of the working version, so 0.0.17 is frozen here, from its text at the `v1.0.0` tag, by the first change that moves
  the working schema to 0.0.18.

| version | from commit | study layer version |
|---|---|---|
| 0.0.2 | `5389e10` | 0.0.1 |
| 0.0.3 | `6ee5bce` | 0.0.1 |
| 0.0.4 | `415e372` | 0.0.1 |
| 0.0.5 | `b45ba08` | 0.1.0 |
| 0.0.6 | `fbeb965` | 0.1.0 |
| 0.0.7 | `f13a80b` | 0.1.0 |
| 0.0.8 | `c6ec6db` | 0.3.0 |
| 0.0.9 | `13cb148` | 0.3.0 |
| 0.0.10 | `f7856bd` | 0.3.0 |
| 0.0.11 | `4a56c90` | 0.4.0 |
| 0.0.12 | `45180df` | 0.4.0 |
| 0.0.13 | `36d8417` | 0.4.0 |
| 0.0.14 | `1977a07` | 0.5.0 |
| 0.0.15 | `233d687` | 0.5.0 |
| 0.0.16 | `1b16b7b` | 0.6.0 |
