# MetaMorpheusContaminants_NoUPS.xml

- **What:** MetaMorpheus's contaminant panel without the 48 proteins of the Sigma UPS1/UPS2 spike-in standard (216
  entries).
- **Source:** smith-chem-wisc/MetaMorpheus, `MetaMorpheus/EngineLayer/Contaminants/Subsets/MetaMorpheusContaminants_NoUPS.xml`
  at commit `a6447cd8b8787ccfbb119e6ddd0503ce13712b69` (PR #2858, merged 2026-10-06). Copied byte for byte with
  `git show`.
- **sha256:** `5b6e2059abf229f3d52b894f6abe4c949d6c37dc78667f19f447eab8205004a9`
- **Why it is here:** #2858 was merged after the MetaMorpheus 1.1.12 tag, so the 1.1.12 install does not have the file.
  Drop this copy and name the file through `panel` once a pinned MetaMorpheus release ships it.
- **Decision:** PXReprise D35 (the user, 2026-10-07): use it as it is. Add the UPS file only where a study declares a
  UPS spike-in.
- **Licence:** MetaMorpheus is MIT. The entries are UniProt data (CC BY 4.0).
