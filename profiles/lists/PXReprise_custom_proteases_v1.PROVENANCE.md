# PXReprise_custom_proteases_v1.tsv

- **What:** digestion agents mzLib's `proteases.tsv` does not have, in the same columns. PXReprise merges them into
  mzLib's `ProteaseDictionary` when the profiles load, and installs the ones a search uses into MetaMorpheus's
  settings folder as `proteases_custom.tsv`. MetaMorpheus 1.1.12 reads that file at start
  (`GlobalVariables.LoadDigestionAgents`), and a per-file `DigestionAgent` resolves through the same dictionary
  (`FileSpecificParameters`).
- **`trypsin+chymotrypsin`:** cuts after K and R, and after F, W, Y and L except before P.
  - **For:** the chymotrypsin fraction of a MED-FASP digest (Lys-C, then trypsin, then chymotrypsin on what was left),
    first PXD006815 (aging PXR-A115, PXR-A130).
  - **Evidence:** one CT run, `20170508_QEp7_JRW_SA_CT_Z69.raw` (61,995 MS2), 2026-10-20, in
    `F:\aging_batch\nonspecific-PXD006815`.
    - A non-specific search identifies 8,139 PSMs at 1% FDR. Of the peptides with both ends inside a protein, 34%
      have a K/R cut at both ends, 16% an F/Y/W/L/M cut at both, and 29% one of each.
    - So the fraction holds both kinds of cut, not only chymotryptic ones.
    - Searched with `chymotrypsin|P` the run gives 651 PSMs. With this motif it gives 7,028 PSMs (5,779 peptides);
      without L, 7,066 PSMs (5,674 peptides). L is kept, as in mzLib's `chymotrypsin|P`.
  - **Proline:** a cut after K or R is followed by P in 3.7 to 4.5% of peptides (P is 6.1% of residues), so the
    trypsin part has no proline rule, as the plain `trypsin` the deposit's trypsin fraction is searched with. After F,
    W, Y or L it is followed by P in 0 to 0.6%, so the chymotrypsin part keeps `[P]`.
- **Named by:** a question's SDRF, either `NT=trypsin+chymotrypsin` or both trypsin and chymotrypsin in one file's
  `comment[cleavage agent details]` columns.
- **Rule:** never edit an entry in place; searches ran against it. A changed list ships as `_v2.tsv`.
