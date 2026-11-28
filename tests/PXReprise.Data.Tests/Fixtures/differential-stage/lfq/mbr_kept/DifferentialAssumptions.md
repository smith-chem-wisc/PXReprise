**Assumptions of this differential analysis** (QuantProject, 2026-11-21; replaced by the report's own Assumptions and
References sections in a later mzLib release)

1. **Missing values.** A peptide with no value in a sample is left out of that sample's fit and treated as missing at
   random. A value below the detection limit is not modelled. Where a protein is absent because it is scarce, the
   estimated change can be smaller than the true one.
2. **Grouping.** Conditions are the search's experimental design, exactly. Samples whose condition is `not available`
   were left out and are listed in the provenance.
3. **Match-between-runs.** Rows of `quant_basis` with transfers include transferred values. A file named in the
   provenance as having lost every transfer has no transferred values, though its sibling files do. In those rows its
   samples look absent, not low.
4. **Independence.** Samples of different individuals are independent. Repeated samples of one individual share a random
   intercept, where the design names the individual.
5. **One search.** Every number here comes from one search of one deposit. Results from searches under different
   identification filters are compared only with that difference stated.
