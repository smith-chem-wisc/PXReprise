# The public site

`datarepo site` writes a static website from one built catalog. The site needs no server, no
database and no API: it is plain HTML and a few text files that any web host can serve, and it still
works if every other service is down (FRAMEWORK section 5, D16). dataRepo ships the generator, and
the instance owner (`aging`) publishes the output, because data-derived files do not belong in this
repository (D8).

```
datarepo site <catalog.duckdb> --out <dir> [--base-url URL] [--data-url URL] [--title TEXT] [--notice TEXT] [--about FILE]
```

## What it writes

| File | For | Written when |
|---|---|---|
| `index.html` | people: every dataset, its headline counts, and the catalog's id | always |
| `datasets/<id>.html` | people and search engines: one page per dataset, with a [Bioschemas Dataset](https://bioschemas.org/profiles/Dataset/1.0-RELEASE) block | always |
| `llms.txt` | agents: what the repository is, the rules for reading it, and every dataset in one line ([llmstxt.org](https://llmstxt.org)) | always |
| `datasets.json` | programs: the same facts as the pages | always |
| `style.css` | the pages, in light and dark mode | always |
| `croissant.json` | ML loaders: every Parquet file with its SHA-256, one record set per dataset and table ([Croissant 1.0](https://docs.mlcommons.org/croissant/docs/croissant-spec.html)) | with `--data-url` |
| `sitemap.xml`, `robots.txt` | search engines | with `--base-url` |

**The two URLs.** `--base-url` is where the site itself will be served. Without it, links are
relative and the structured data carries no `url`. `--data-url` is where the bundle store is served,
laid out as `<data-url>/<dataset>/<bundle>/<table>.parquet`, which is the layout the store already
has. Without it, the pages say the Parquet files are not published yet. They do not link to anything,
and no Croissant file is written, because a Croissant file describes files someone can download.

**Why one record set per dataset and table.** A Croissant `FileSet` would give one record set per
table across every dataset, but it must sit inside something a loader can list: a git repository,
an archive or a local folder. A directory served over HTTP is none of those. That form passes
`mlcroissant validate` and then loads **zero records** with no error, which we found by loading it
rather than by validating it. Each file is hashed from the store, and its fields come from the
file's own Parquet schema. A file the store cannot supply is left out, and the run warns about it.

**`--about FILE`** is a short Markdown file with the front page's overview of the project behind
the instance. It is the instance owner's text, not dataRepo's, so it lives with them. Only a safe
subset is read: `## ` headings, `- ` lists, paragraphs, bold, italic, code and http(s) links. Any
HTML in it is shown as text.

**The front page's figures of merit** are sums and counts over the catalog: datasets, raw files,
spectra searched, PSMs at 1% FDR, proteins identified (decoys and contaminants excluded) and PTM
sites. Each tile says what it counts. A figure some datasets don't report says how many did,
instead of passing off a partial sum as a total.

**`--notice`** puts a banner at the top of every page and of `llms.txt`, for example that the
site is a preview and its data will be regenerated.

## What a dataset page says, and where it comes from

Every number on a page is read from the catalog named in its footer, and nothing else:

- **The generated summary** is a template filled from catalog fields (D17). It is labelled
  "generated", and it cannot say anything the catalog does not hold. So it says nothing about what
  a study found, only what was measured and how much of it passed 1% FDR.
- **Open findings** are the dataset's `findings` rows with `status = 'open'`, errors and warnings
  first. They come before the counts on purpose, because a reader who sees a count without its
  caveats will use it.
- **What was found** are the `dataset_overview` columns, plus the producer's identification rate
  and contaminant share from `metrics`, each shown with its definition id.
- **Most frequent modifications** counts `ptm_sites` on target proteins by the search engine's own
  name.
- **Reconciliation** says whether the bundle's counts agreed with the producer's. When they did
  not, the page names the check that failed.
- **Licence and credit** are read from the bundle's own `bundle.json`, through the path the catalog
  recorded. If that file cannot be read, the page says "not recorded" and the structured data
  omits the licence. It never falls back to the value D3 decided, because the licence a download
  states is the one that binds.

## Regenerating

The output directory holds a `.datarepo-site.json` marker listing every file the last run wrote.
A new run deletes exactly those files before writing, so a dataset that left the catalog loses its
page. The generator refuses a non-empty directory without the marker rather than adopt files it
cannot account for. The same catalog always gives byte-identical output, so a regenerated site
can be diffed against the published one.

Regenerating the site moves no bundle id and no catalog id: nothing on it reaches a written row.

## Not yet

- **A model-written summary (D17).** The template is the grounded-by-construction floor. A model
  may later write the prose from the same fields, under the same "generated" label.
- **A Zenodo DOI per release.** It belongs to the instance owner's release step (D11), and the page
  will carry it once `releases` has rows.
- **Hosting.** Any static host works, GitHub Pages included. Which one is the instance owner's
  choice, and the only open decision on this step.
