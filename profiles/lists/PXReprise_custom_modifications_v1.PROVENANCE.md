# PXReprise_custom_modifications_v1.txt

- **What:** MetaMorpheus modification file (mzLib's ptmlist format) with the modifications PXReprise profiles model
  that no UniMod entry describes (G40).
- **NBF on C:** NBF-Cl (4-chloro-7-nitrobenzofurazan, C6H2ClN3O3) reacting with a thiol adds C6HN3O3 (loses HCl):
  monoisotopic +163.00179 Da. Chemistry from aging 072 (PXR-A69; the reagent's Sigma-Aldrich 163260 entry).
- **Evidence it is there:** the PXD058611 probe search, `results/nbf-probe_2026-10-11/` (NBF on 76% and 86% of
  identified cysteines in a lysate and a persulfide run). NBF on K and on the N-terminus was rare (under 1% of lysines,
  under 0.5% of PSMs), so this list models only NBF on C.
- **Used by:** `label-free-dda@6` `[thiol_reagents] "NBF-Cl"`, as a VARIABLE modification.
- **Rule:** never edit this file in place; searches ran against it. A changed list ships as `_v2.txt`.
