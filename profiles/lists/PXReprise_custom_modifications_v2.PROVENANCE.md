# PXReprise_custom_modifications_v2.txt

- **What:** MetaMorpheus modification file (mzLib's ptmlist format): v1's NBF on C, plus two side reactions typed
  `Common Artifact` for GPTMD (v0.4.29; ptmQtl 020, PXR-P18 and PXR-P19).
- **NBF on C:** as in v1 (`PXReprise_custom_modifications_v1.PROVENANCE.md`); same entry, same bytes.
- **Nethylmaleimide+water on C:** N-ethylmaleimide on a thiol (C6H7NO2, +125.04768 Da) with its succinimide ring opened by
  water: C6H9NO3, monoisotopic +143.05824 Da, UNIMOD:320. Searched with closed NEM only, the McDonagh deposits
  (PXD001054, PXD005014, PXD010091) and the ten Jarome deposits left +86.037 Da (143.058 - 57.021) that GPTMD placed as
  Hydroxybutyrylation on K: 65 of 96 and 82 of 91 such sites were on cysteine peptides (ptmQtl 020). Light NEM only:
  ring-opened d5-NEM (+148.0896) has no UniMod entry.
- **Propionamide on C:** acrylamide (C3H5NO) added to a free cysteine in a polyacrylamide gel: +71.03711 Da, UNIMOD:24.
  Searched with carbamidomethyl fixed, the +14.016 Da left over became methyl on K or R in seven deposits whose
  protocols name a gel (ptmQtl 020: PXD043779, PXD009212, PXD018999, PXD007999, PXD004916, PXD007188, PXD006898).
- **Why Common Artifact and not UniMod's own entries:** the user (2026-10-25): "it would be an artifact mod
  (modification type)". GPTMD can take either type; MetaMorpheus files UniMod's under the type `Unimod`.
- **Why GPTMD and not variable:** the user (2026-10-25): variable only when most peptides are expected to carry the
  modification, as with oxidised methionine. Neither is expected on most cysteines. GPTMD places a candidate where the
  search holds another modification on the same residue (MetaMorpheus `GptmdEngine.GetPossibleMods`), so these sit
  beside variable NEM and fixed carbamidomethyl.
- **Used by:** `label-free-dda@9` `[thiol_reagents]` (NBF, variable) and `[chemistry_artifacts]` (the two, GPTMD).
- **Rule:** never edit this file in place; searches ran against it. A changed list ships as `_v3.txt`.
