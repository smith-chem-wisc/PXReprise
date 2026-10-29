#!/bin/bash
# agingPTM-v6 (PXReprise D29; the user, 2026-10-28: "a good time to clean and update the databases and the libraries and
# to add any new high quality PTMs to the databases per our prior requirements"), for the first profile on the
# MetaMorpheus release of 2026-10-29 (master 315e02b5e, mzLib 1.0.596). The three canonical proteomes, from aging's
# catalog b978714d36f3878a (datarepo 1.3.6, catalog version 13, 178 datasets; read in place: since datarepo 1.3.5 a
# catalog is views over the store and no file a reader holds is replaced). Enriched-for exclusions: the batch's own
# record (EnrichedExclusions.cs, with 10-17's list kept) plus BuildPtmDb's measured rows, read from each served
# bundle's psms.parquet (version 13 has no psms_1pct). Hydroxylation on P and K is not selected (v0.4.32). The isoform
# overlays are annotated separately, from aging's raw union of its five sources (aging 175). Run from tools/ptm-db.
set -e
V=F:/pxreprise_ptmdb_v6
DB=F:/aging_data/db
CAT=F:/aging_data/repo/catalogs/catalog-b978714d36f3878a.duckdb
X=/e/CodeReview/PXReprise/data/ptm-db/enriched_ptm_exclusions_2026-10-28.tsv
mkdir -p $V
cd /e/CodeReview/PXReprise/tools/ptm-db
echo "$(date -u +%H:%M:%S) proteomes"
dotnet run BuildPtmDb.cs -- --catalog $CAT --out $V/out --tag agingPTM-v6 --sites-out $V/sites --exclude-enriched $X \
  --xml human=$DB/UP000005640_all_genecentric_2026_03.xml --xml mouse=$DB/UP000000589_all_genecentric_2026_03.xml --xml rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) done"
