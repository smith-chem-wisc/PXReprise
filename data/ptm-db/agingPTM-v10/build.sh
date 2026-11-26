#!/bin/bash
# agingPTM-v10 (PXReprise D29, D91, G130). The first CUMULATIVE build (D91; the maintainer, 2026-11-24: "yes build it the
# cumulative way. and note that this should be the policy moving forward"), and the first on UniProt release 2026_04
# (the maintainer, same day: "It would be good for the next release to have an updated set of protein databases").
#   - Evidence: EvidenceCatalog.cs over the newest catalog, 7ecfc9763064b181 (catalog version 16, builder 1.4.5), plus the
#     newest kept bundle of every deposit it no longer serves, minus data/ptm-db/evidence_dropped.tsv.
#   - Proteomes: UniProt 2026_04, the gene-centric sets from REST (proteome_canonical, mzLib #1554), identical in
#     membership to the FTP files (results/ptmdb-v10_2026-11-24 in the project; F:/aging_data/db/canonical_2026_04).
#   - Rules: v9's (D62: --uniprot-sites regular; GPTMD-found sites as v6, no Malonylation on K). Enriched-for exclusions:
#     the batch's own record today (EnrichedExclusions.cs) with 11-21's list kept, plus BuildPtmDb's measured rows.
#   - The isoform overlays are annotated from aging's v6 union sources (aging 175), as v7's to v9's.
#   - A site on an accession 2026_04 no longer has (580 mouse entries changed) moves to its gene's 2026_04 entry where
#     the 2026_03 sequence around it is found (--previous-proteome; the maintainer, same day: "yes").
# Run from tools/ptm-db.
set -e
V=F:/pxreprise_ptmdb_v10
DB=F:/aging_data/db
LIVE=F:/aging_data/repo/catalogs/catalog-7ecfc9763064b181.duckdb
CAT=$V/evidence_v10.duckdb
HERE=$(cd "$(dirname "$0")" && pwd)
X=$HERE/../enriched_ptm_exclusions_2026-11-24.tsv
ISO=F:/aging_data/db/isoform_db_staging/v6_union_2026-10-28
HU=$DB/UP000005640_all_genecentric_2026_04.xml
MO=$DB/UP000000589_all_genecentric_2026_04.xml
RA=$DB/UP000002494_all_genecentric_2026_04.xml
mkdir -p $V
cd "$HERE/../../../tools/ptm-db"
echo "$(date -u +%H:%M:%S) evidence"
[ -e "$CAT" ] || dotnet run EvidenceCatalog.cs -- --catalog $LIVE --out $CAT --drop "$HERE/../evidence_dropped.tsv" \
  --manifest F:/aging_data/batch/manifest.yaml
echo "$(date -u +%H:%M:%S) exclusions"
dotnet run EnrichedExclusions.cs -- --manifest F:/aging_data/batch/manifest.yaml --state F:/aging_data/batch/state.json \
  --also "$HERE/../enriched_ptm_exclusions_2026-11-21.tsv" --out "$X"
echo "$(date -u +%H:%M:%S) proteomes"
dotnet run BuildPtmDb.cs -- --catalog $CAT --out $V/out --tag agingPTM-v10 --sites-out $V/sites --exclude-enriched "$X" \
  --uniprot-sites regular --xml human=$HU --xml mouse=$MO --xml rat=$RA \
  --previous-proteome human=$DB/UP000005640_all_genecentric_2026_03.xml --previous-proteome mouse=$DB/UP000000589_all_genecentric_2026_03.xml \
  --previous-proteome rat=$DB/UP000002494_all_genecentric_2026_03.xml
echo "$(date -u +%H:%M:%S) isoforms"
dotnet run BuildPtmDb.cs -- --mode isoforms --catalog $CAT --out $V/out_iso --tag agingPTM-v10 --sites-out $V/sites_iso --exclude-enriched "$X" \
  --xml human=$ISO/aging_isoforms_union_human.xml --xml mouse=$ISO/aging_isoforms_union_mouse.xml --xml rat=$ISO/aging_isoforms_union_rat.xml \
  --proteome human=$HU --proteome mouse=$MO --proteome rat=$RA
echo "$(date -u +%H:%M:%S) done"
