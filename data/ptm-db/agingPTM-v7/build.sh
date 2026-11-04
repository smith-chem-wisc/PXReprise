#!/bin/bash
# agingPTM-v7 (PXReprise D29, D61, D62; the user, 2026-11-04: "let's stick with my principle"), for label-free-dda@13.
# The first REGULAR databases: UniProt's own modified residues are kept only where a deposit not enriched for the site's
# class identifies them (BuildPtmDb.cs --uniprot-sites regular; tested leave-one-out in results/regular-db-trial_2026-11-04).
# The GPTMD-found sites are selected as for v6, without Malonylation on K (results/acyl-gptmd_2026-11-03). Catalog
# abfeaa1bf5c73d4d (datarepo 1.4.1, catalog version 15, read in place). Enriched-for exclusions: the batch's own record
# (EnrichedExclusions.cs, 2026-11-04, with 10-28's list kept) plus BuildPtmDb's measured rows. The isoform overlays are
# annotated from aging's v6 union sources (aging 175) with the same catalog; their few UniProt features (29 in all) are
# kept as they are (--mode isoforms has no regular rule). Run from tools/ptm-db.
set -e
V=F:/pxreprise_ptmdb_v7
DB=F:/aging_data/db
CAT=F:/aging_data/repo/catalogs/catalog-abfeaa1bf5c73d4d.duckdb
X=/e/CodeReview/PXReprise/data/ptm-db/enriched_ptm_exclusions_2026-11-04.tsv
ISO=F:/aging_data/db/isoform_db_staging/v6_union_2026-10-28
mkdir -p $V
cd /e/CodeReview/PXReprise/tools/ptm-db
echo "$(date -u +%H:%M:%S) proteomes"
dotnet run BuildPtmDb.cs -- --catalog $CAT --out $V/out --tag agingPTM-v7 --sites-out $V/sites --exclude-enriched $X \
  --uniprot-sites regular \
  --xml human=$DB/UP000005640_all_genecentric_2026_03.xml --xml mouse=$DB/UP000000589_all_genecentric_2026_03.xml --xml rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) isoforms"
dotnet run BuildPtmDb.cs -- --mode isoforms --catalog $CAT --out $V/out_iso --tag agingPTM-v7 --sites-out $V/sites_iso --exclude-enriched $X \
  --xml human=$ISO/aging_isoforms_union_human.xml --xml mouse=$ISO/aging_isoforms_union_mouse.xml --xml rat=$ISO/aging_isoforms_union_rat.xml \
  --proteome human=$DB/UP000005640_all_genecentric_2026_03.xml --proteome mouse=$DB/UP000000589_all_genecentric_2026_03.xml --proteome rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) done"
