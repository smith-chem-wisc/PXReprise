#!/bin/bash
# agingPTM-v4 (PXReprise D44, 2026-10-17): the three canonical proteomes and the nine isoform overlays, from a COPY of
# aging's catalog, with the enriched-for exclusions from the batch's own record. Run from tools/ptm-db.
set -e
V=F:/pxreprise_ptmdb_v4
DB=F:/aging_data/db
X=$V/enriched_ptm_exclusions_2026-10-17.tsv
cd /e/CodeReview/PXReprise/tools/ptm-db
echo "$(date -u +%H:%M:%S) proteomes"
dotnet run BuildPtmDb.cs -- --catalog $V/catalog_copy.duckdb --out $V/out --tag agingPTM-v4 --sites-out $V/sites --exclude-enriched $X \
  --xml human=$DB/UP000005640_all_genecentric_2026_03.xml --xml mouse=$DB/UP000000589_all_genecentric_2026_03.xml --xml rat=$DB/UP000002494_all_genecentric_2026_03.xml
for tier in tier1-2 tier3_compartment peroxisome; do
  echo "$(date -u +%H:%M:%S) isoforms $tier"
  dotnet run BuildPtmDb.cs -- --mode isoforms --catalog $V/catalog_copy.duckdb --out $V/out_iso_$tier --tag agingPTM-v4 --sites-out $V/sites_iso_$tier --exclude-enriched $X \
    --xml human=$DB/aging_isoforms_${tier}_human.xml --xml mouse=$DB/aging_isoforms_${tier}_mouse.xml --xml rat=$DB/aging_isoforms_${tier}_rat.xml
done
echo "$(date -u +%H:%M:%S) done"
