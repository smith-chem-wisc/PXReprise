# site fixture

Written by the Python site generator, datarepo **0.32.0** (`src/datarepo/site.py` at `efcbb76`, DuckDB 1.5.5),
on 2026-10-04. `SiteTests.cs` compares the C# generator's output with these files byte for byte; the only
normalisation is the generator's own version, in the marker's `generator` field and in every page's footer
(`SiteParity`).

- `catalog.duckdb.gz`: `gzip -9 -n` of the catalog Python built from the `python-0.32.0` bundle, run from
  `Fixtures/` so the catalog records the bundle's path relative to it (`python-0.32.0/PXD999999/aeb10630abbcaf72`):

      cd dotnet/tests/DataRepo.Tests/Fixtures
      python -m datarepo.cli build ../../../../tests/data/manifest.yaml PXD999999 --store python-0.32.0 --out <scratch>/catalog.duckdb

  catalog id `153cf55067d58441`. Every page states the catalog's `built_utc`, so the expected pages below belong
  to this file: regenerate them together or not at all.
- `python-plain/`: `python -m datarepo.cli site <scratch>/catalog.duckdb --out <dir>`, from `Fixtures/`.
- `python-full/`: every option, from `Fixtures/`:

      python -m datarepo.cli site <scratch>/catalog.duckdb --out <dir> \
        --title "Test <repo> & 'co'" --about site/about.md \
        --base-url https://example.org/repo/ --data-url https://example.org/store/ \
        --purpose "  how organelle proteomes change with age.  " --keyword aging --keyword " organelle " --keyword "  " \
        --notice "Preview: regenerated & <checked> \"soon\"."

- `about.md`: written for this fixture, to exercise the Markdown subset (headings, lists, inline markup, an
  unsafe link, raw HTML).
- `formats.json`: Python's own `_compact`, `f"{x:.1%}"`, `_shared_threshold` and `about_html` on edge inputs
  (a midpoint `112.5` rounds to even, `999,999` compacts to `1e+03K`), from a script that imports `datarepo.site`
  and writes `json.dumps(..., indent=1, ensure_ascii=False)` as UTF-8.

## Real data (`[Category("RealData")]`)

`OnTheRealCorpusEveryShardIsUnderTheCapAndIndexed` reads aging's serving catalog
(`F:/aging_data/repo/catalog.duckdb`, read only) and needs nothing else. `OnTheRealCatalogTheSiteIsPythons`
also needs `DATAREPO_SITE_PARITY_DIR`, a folder holding `real_about.md` (a copy of aging's
`instance/site_about.md`) and `real-py/`, Python's site from the same catalog:

    python -m datarepo.cli site F:/aging_data/repo/catalog.duckdb --out <dir>/real-py \
      --title "Aging proteomics repository" --about <dir>/real_about.md \
      --base-url https://trishorts.github.io/aging-pipeline/ --data-url https://example.org/store \
      --purpose "how organelle proteomes change with age" --keyword aging \
      --notice "Preview: this data will be regenerated & <checked>."

It is inconclusive, not failed, when the catalog has been rebuilt since. On 2026-10-04 (catalog
`7304c18d9da964d2`, 85 datasets, built by 0.32.0) all 1,321 files, 41,172,148 bytes, were identical.
