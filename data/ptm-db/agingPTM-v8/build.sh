#!/bin/bash
# agingPTM-v8 (PXReprise D29; the user, 2026-11-07: "move to mm 1.1.14"), for tmt-dda@4 (MetaMorpheus 1.1.14).
# The same build as agingPTM-v7 (D62: --uniprot-sites regular; GPTMD-found sites as v6, no Malonylation on K), on the
# newest catalog: e56b5ac87dcf5187 (datarepo 1.4.3, catalog version 15, read in place). Enriched-for exclusions: the
# batch's own record today (EnrichedExclusions.cs) with 11-04's list kept, plus BuildPtmDb's measured rows. The isoform
# overlays are annotated from aging's v6 union sources (aging 175), as v7's. Run from tools/ptm-db.
set -e
V=F:/pxreprise_ptmdb_v8
DB=F:/aging_data/db
CAT=F:/aging_data/repo/catalogs/catalog-e56b5ac87dcf5187.duckdb
X=/e/CodeReview/PXReprise/data/ptm-db/enriched_ptm_exclusions_2026-11-07.tsv
ISO=F:/aging_data/db/isoform_db_staging/v6_union_2026-10-28
mkdir -p $V
cd /e/CodeReview/PXReprise/tools/ptm-db
echo "$(date -u +%H:%M:%S) exclusions"
dotnet run EnrichedExclusions.cs -- --manifest F:/aging_data/batch/manifest.yaml --state F:/aging_data/batch/state.json \
  --also /e/CodeReview/PXReprise/data/ptm-db/enriched_ptm_exclusions_2026-11-04.tsv --out $X
echo "$(date -u +%H:%M:%S) proteomes"
dotnet run BuildPtmDb.cs -- --catalog $CAT --out $V/out --tag agingPTM-v8 --sites-out $V/sites --exclude-enriched $X \
  --uniprot-sites regular \
  --xml human=$DB/UP000005640_all_genecentric_2026_03.xml --xml mouse=$DB/UP000000589_all_genecentric_2026_03.xml --xml rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) isoforms"
dotnet run BuildPtmDb.cs -- --mode isoforms --catalog $CAT --out $V/out_iso --tag agingPTM-v8 --sites-out $V/sites_iso --exclude-enriched $X \
  --xml human=$ISO/aging_isoforms_union_human.xml --xml mouse=$ISO/aging_isoforms_union_mouse.xml --xml rat=$ISO/aging_isoforms_union_rat.xml \
  --proteome human=$DB/UP000005640_all_genecentric_2026_03.xml --proteome mouse=$DB/UP000000589_all_genecentric_2026_03.xml --proteome rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) done"
