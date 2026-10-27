# Changelog

Every release says three things before anything else: whether it needs a different MetaMorpheus, whether your machine
file needs a change, and whether it is safe to update while a batch is part-way through a queue. How to update:
section 7, "Updating", of [Getting started](https://smith-chem-wisc.github.io/PXReprise/getting-started.html).

A new version of the engine never changes a finished result. Methods live in versioned profiles
(`label-free-dda@2`), and every result's `provenance.json` records the PXReprise version that made it.

## 0.4.32 (2026-10-27)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual, and run
`library repair` for each organism in the gap (below). A question takes the new profile by naming it.

- **Spectral libraries are cleaned, and stay clean.** Every library version the engine registers, and every
  `pxreprise library repair <organism> --databases a.xml,b.xml`, leaves out two kinds of spectrum:
  - **blocked at a cut C-terminus:** the modification on the last residue blocks trypsin (citrulline, acyl-K, GG-K),
    and mzLib's `CleavageBlockingPolicy` rules out every place the sequence occurs in the organism's target proteins.
    A spectrum is kept when any occurrence is a protein C-terminus, and when the sequence occurs nowhere. This is
    cleavageRules' rule (PXR-V8), so the libraries agree with the search once a MetaMorpheus release runs with
    `RespectCleavageBlockingModifications`. Library-based DIA takes its candidates from the library itself, so these
    spectra must leave the file, not merely go unscored.
  - **a GPTMD hydroxylation on P or K:** the classes agingPTM-v5 removes (below). UniProt's own stay.
  - The version's registry record says what was dropped, by modification (`cleaning`). On rat v037: 3,344 blocked
    and 5,292 hydroxylated of 265,699 spectra.
  - **Why:** the user, 2026-10-27: "clean the libraries now and keep them clean until clean libraries are produced. i
    rather this than starting from no library".
- **New profile `label-free-dda@11`: `@10` on agingPTM-v5.** v5 is v4 without its GPTMD-added Hydroxylation on P and
  K sites (human 3,442, mouse 6,468, rat 1,033), every other byte the same; `aging_isoforms_<org>_agingPTM-v5.xml`
  are the overlays' v5 forms. 19-21% of those sites had Trp or Met within two residues, against 10-13% for every P or K
  of the same proteins: oxidised Met or Trp placed on the wrong residue. `BuildPtmDb.cs` no longer selects either
  class, so later databases stay clean until a GPTMD change makes those sites trustworthy.
- **mzLib 1.0.595** in the engine (MetaMorpheus 1.1.12 keeps 1.0.594 in its own process), for the cleaning rule.

## 0.4.31 (2026-10-27)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. A question takes
the new profile by naming it. 0.4.30 was released but never run as a batch, so this swap also brings in its
`qc-payload/2`.

- **New profile `label-free-dda@10`: GPTMD adds a site only where it improves the match's score.** It is `@9` with
  MetaMorpheus's `ImprovedScoreFilter` on the GPTMD task. Every other setting, including GPTMD's modification list,
  is `@9`'s.
  - **Why:** without a filter, GPTMD adds every placement that fits a mass difference. An entrapment test (GPTMD over
    the proteome plus a shuffled copy, where any site on the copy is false) found 12.9%, 4.2% and 7.9% of GPTMD's
    sites false on a mouse, a human and a rat deposit. With the filter: 4.3%, 1.0% and 1.5%, for 0.6%, 0.9% and 3.6%
    fewer peptides.
  - **Not done here:** modifications that block the protease at a peptide's cut C-terminus (citrulline, acyl-K) are
    to be refused by GPTMD and the search themselves, in MetaMorpheus, not by leaving classes out of the list.
- **A profile can name GPTMD's filters** (`[engine] gptmd_filters`), written into the GPTMD task's `GptmdFilters`
  and recorded in the search's provenance. MetaMorpheus ignores a filter name it does not know without a warning, so
  a profile naming one is refused when it loads.

## 0.4.30 (2026-10-25)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. Payloads already
written keep their schema.

- **The QC payload is now `qc-payload/2`** (qc's D13; qctemplates 0.3.0 renders it).
  - **The per-file `psms`, `peptides` and `protein_groups` are the whole search's 1% set restricted to the file:**
    `pxreprise:DEF-PSM-1PCT-RUN`, `DEF-PEPTIDE-1PCT-RUN` and `DEF-PROTEINGROUP-1PCT-RUN`, v1. One cutoff for every
    run, so the PSM counts sum to the dataset's.
  - Under `/1` they were MetaMorpheus's per-file lines, each from an FDR recomputed on that file alone. Those lines
    exist only when MetaMorpheus writes its individual-file results, which MetaMorpheus #2914 turns off by default
    from its next release.
  - `ms2_scans` is still MetaMorpheus's per-file line.
  - **The payload lists the databases the search read** (`dataset.search_databases`: file, sha256, kind,
    UniProt release), from the search's provenance.
  - A `/1` payload can still be built (`Build(..., schema: QcPayloadBuilder.SchemaV1)`), the proven port of aging's
    builder.
  - **On PXD051203** the 39 runs' `/2` PSM counts sum to the dataset's 34,103. The per-run medians are 644 PSMs, 345
    peptides and 177 protein groups, against 619, 393 and 216 under `/1`.

## 0.4.29 (2026-10-25)

No MetaMorpheus change. **Machine file:** optional new key `[workers] gap` (default 5). **Safe mid-batch?** Yes: swap
the pin as usual, then publish the build to the probing machine (a build-only release). A question takes the new
profiles by naming them.

- **New profile `label-free-dda@9`: two side reactions of a deposit's own chemistry are offered to GPTMD.** They are
  never searched as variable: a variable modification is for one on most peptides, as oxidised methionine is.
  - **Ring-opened NEM** (`Nethylmaleimide+water on C`, +143.058 Da, UniMod 320) where NEM blocks the cysteines.
  - **Propionamide** (`Propionamide on C`, +71.037 Da, UniMod 24) where the protocol names a gel (SDS-PAGE, in-gel
    digestion, gel bands or slices, "gel-digested").
  - Both are typed `Common Artifact` in a new `profiles/lists/PXReprise_custom_modifications_v2.txt`, with v1's NBF
    entry unchanged. GPTMD may place one where the search already holds another modification on that cysteine, so
    each sits beside variable NEM or fixed carbamidomethyl.
  - **Tested on two raw files of each kind, `@8` against `@9`.** Ring-opened NEM was on 137 matches, 7% of the
    NEM-modified cysteine matches, and 5% more cysteine-peptide matches passed. Propionamide was on 13 matches, 0.25% of
    the cysteine matches, and took over 9 of 65 methyl-on-K artifacts. PSMs were flat to +0.7%, peptides -0.2%,
    protein groups unchanged.
  - **A deposit with neither side reaction is searched exactly as under `@8`.** New profile table
    `[chemistry_artifacts]` (needs `chemistry = "deposit"` and a GPTMD task). The side reactions read are recorded in
    the chemistry record and the experiment notes under any profile.

- **New profile `label-free-dda-lowres@5`, and `lowres@4` is not recommended.** `lowres@4` (0.4.28) searched MS2
  fragmented by HCD and read in the ion trap as MetaMorpheus's LowCID. Its evidence was measured under the probe
  search's settings only.
  - **In the full search LowCID lost PSMs on every deposit searched both ways:** 41,562 to 37,052 (-11%, 19 of 22 files
    lower), 759,981 to 637,306 (-16%, all 44 files lower) and 121,034 to 119,928 (-1%). One file had 3,300 PSMs as HCD
    under the probe's settings, 15,939 in the `lowres@2` full search, and 13,043 as LowCID in the `lowres@4` one.
  - **`lowres@5` is `lowres@3` with ion-trap HCD taken as LowCID in the probe search only.** A deposit of rapid ion-trap
    HCD scans can identify nothing as HCD under the probe's settings and park as identifying nothing; as LowCID it
    passes. Its full search is `lowres@3`'s: HCD at the low-resolution tolerance, and a file mixing ion-trap CID and
    ion-trap HCD is refused again.
  - **On that deposit the full search does as well as HCD:** three of its files under the full search's settings gave
    8,627 PSMs as HCD and 8,420 as LowCID. One of them had 107 PSMs as HCD under the probe's settings and 4,862 under
    the full search's.
  - Profile key `ion_trap_hcd_probe_lowcid` (needs `ion_trap_hcd` and `probe_search`; not with `ion_trap_hcd_lowcid`).
    `lowres@4` is unchanged, since a profile version never changes; its notes here and in the docs are corrected.
- **Fixed: catch-up re-searches of deposits the queue still lists never started.** When it walked the queue, the
  batch marked every searched deposit as taken, and the catch-up then skipped all of them. Only deposits no longer in
  the queue were caught up. On 2026-10-22 the queue ran out with 81 re-searches due under profiles the question no
  longer names. Present since 0.4.1.
- **The probing machine works further down the queue (the user: "keep the worker stocked with probe sets that are five
  or 10 spots down the line").** Before, it was assigned the very next deposits, the same ones the delivering machine
  reached first. When it went quiet, the delivering machine took them back and probed them, and on 2026-10-20 one
  deposit was probed on both machines.
  - **The delivering machine keeps its next `gap` deposits to probe (default 5) for itself.** The prober's window is
    filled from the ones after them, at every look, while its heartbeat is fresh. The order is the queue's, then the
    catch-up re-searches due.
  - **A deposit the prober passed goes ahead of the queue** on the delivering machine (the user: "when it finishes a
    probe, then move that repo up the queue on the local box"). It needs no probe there, so it is fetched next, after
    any deposit already downloaded.
  - **The delivering machine no longer waits for room in the prober's window:** it probes its own deposits. It passes
    over a deposit assigned to the prober, and waits only when the prober's deposits are all that is left.
- **A take-back rewrites `accessions\assigned.tsv` at once.** Before, the file was rewritten only at the next look, so
  a prober coming back could probe a deposit the delivering machine already had.
- **A deposit whose probe failed on the prober is probed on the delivering machine,** not assigned again. PXD006815's
  probe failed there for want of memory and was assigned straight back.
- **A deposit with raw files on the delivering machine is probed there,** even when only some of them have arrived (a
  download cut by STOP, for example). Before, only a deposit with all its files counted.

## 0.4.28 (2026-10-20)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual, then publish the
build to the probing machine (a build-only release). A question takes the new profile by naming it.

- **New profile `label-free-dda-lowres@4`:** `lowres@3`, with MS2 fragmented by HCD and read in the ion trap searched as
  MetaMorpheus's LowCID.
  - **Why:** `lowres@2` and `@3` searched it as HCD at the low-res tolerance. For every type but LowCID, MetaMorpheus
    deconvolutes each MS2 spectrum into fragment masses first, and a low-resolution spectrum defeats that. On one file
    each, with the same settings and only the type changed: 107 PSMs at 1% as HCD and 2,442 as LowCID (a deposit
    `lowres@3` had parked as identifying nothing); 3,300 and 9,173 on a deposit identified at 39%.
  - A file that mixes ion-trap CID and ion-trap HCD is now one type, LowCID, and is searched instead of refused.
  - Profile key `ion_trap_hcd_lowcid` (needs `ion_trap_hcd`). Earlier profiles are unchanged.
- **PXReprise's own proteases:** `profiles/lists/PXReprise_custom_proteases_v1.tsv`, in mzLib's columns, merged into
  mzLib's protease list when the profiles load. Before a search or probe that uses one, it is added to
  `proteases_custom.tsv` in MetaMorpheus's settings folder, and the search's provenance records the file.
  - **The first is `trypsin+chymotrypsin`:** cuts after K and R, and after F, W, Y and L except before P. It is for
    the chymotrypsin fraction of a MED-FASP digest: on one such run, 651 PSMs as chymotrypsin and 7,028 with it.
  - **A question names it** in its SDRF as `NT=trypsin+chymotrypsin`, or by naming trypsin and chymotrypsin for one
    file. Before, such a file waited as `waiting_multi_protease`.

## 0.4.27 (2026-10-19)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual, then publish the
build to the probing machine (`pxreprise worker release`; a build-only release, which its supervisor takes by itself).
No profile change.

- **The probing machine now gets every probe, not only fresh deposits.** A re-search under a newer profile, a retry and
  a deferred deposit's second look go to it too, while its heartbeat is fresh.
  - **Why:** 0.4.26 assigned only deposits that had never been probed. On 2026-10-19 the ageing project's queue held one
    such deposit of about 295, so the prober sat idle while this machine probed re-searches itself.
  - A deposit whose raw files are all on this machine already (downloaded ahead) is still probed here.
  - **When the prober's window is full,** this machine waits while it has a search running, and probes the next
    deposit itself when nothing is searching. batch.log says `probed here: the probe worker has its N deposit(s) and
    nothing is searching here`.
  - An assignment is keyed on its `assigned_utc`, so a deposit handed off before is probed again when it is assigned
    again. Its attempt number goes on from the last one.
- **A search keeps a copy of the SDRF its design came from:** `04_search/design/<name>.sdrf.tsv`.
  - The provenance's `experimental_design.sdrf` now names that copy.
  - `sdrf_origin` names the file it was copied from: the question's curated SDRF, the deposit's own, or PXReprise's
    default.
  - **Why:** a question's SDRF can be edited after its search. Seven of the ageing project's were, so the bytes those
    searches used survived only in that project's git history (dataRepo's request).
  - `sdrf_sha256` is unchanged, and the copy matches it.

## 0.4.26 (2026-10-19)

No MetaMorpheus change. **Machine file:** no change, unless you add the new `[workers]` or `[worker]` table. **Safe
mid-batch?** Yes: swap the pin as usual. Without either table the batch runs exactly as in 0.4.25. No profile change.

- **A second machine can run the probes.**
  - **How it works:** the machine with `[worker]` probes the deposits the delivering machine (`[workers]`) assigns it,
    and hands each one back through a shared folder.
  - **Every full search, ingest and publish stays on the delivering machine,** so every result still comes from one
    machine, one software set and one spectral library.
  - **A probe is three raw files,** so a machine with little memory can do it.
- **The delivering machine checks every probe before using it:**
  - every file against the package's `READY.json` (size and SHA-256);
  - the PXReprise version that probed it, the profile it passed under, and the database set;
  - then it runs its own duplicate screen, which the prober cannot run.

  A damaged package, or one probed under another version, another copy of the question or another database set, is
  assigned again. After three refused packages, that deposit is probed on the delivering machine.
- **Both machines probe by the same question.** At each start the delivering machine's batch publishes the question it
  loaded (with its decisions file and designs folder) to the share. The prober runs that copy, names its own run root
  and state directory in `[worker]`, and stops between deposits when the copy changes.
- **The delivering machine never waits forever.** It probes for itself when the prober has had no heartbeat for
  `quiet_hours`, when the heartbeat says the prober stopped, or when one deposit has waited `assignment_hours`.
- **Version changes are acted on at once:**
  - `pxreprise worker release` publishes a build to the prober and raises the generation in `control\current.toml`.
  - The prober checks its own version and its databases against the release before it starts.
  - It stops between deposits as soon as the generation changes.
  - The delivering machine logs a warning at start when the release is not its own version.
- **The prober keeps no probe files.** It deletes them as soon as it has handed them off. It deletes a deposit's whole
  folder once the delivering machine has taken it, or has taken the deposit back.
- **New statuses:**
  - on the delivering machine: `assigned_to_worker`, `probed_by_worker` and `worker_fallback`;
  - on the prober: `handed_off`.
- **New command `pxreprise worker sums|check|release`** ([Commands](docs/commands.md)).
- **A profile may name a MetaMorpheus build by commit, not only by release:** `metamorpheus = "master@3a6a76632"`.
  - A release is checked by its banner, as before. A commit build (MetaMorpheus's CI zip for every push to master)
    prints "Not a release version." there, so it is checked by the commit its `--help` names instead.
  - The pin's name keys the machine file's `[metamorpheus]` table, the settings folder and the spectral library
    registry.
  - Provenance records no release for a commit build.
  - The probe checks the build too.
  - No shipped profile changes: every one names a release.
- **The manifest note names each extra database with its sha256 prefix,** for example
  `aging_isoforms_mouse.xml (sha 01e972ef51ed)`, taken from the search's own recorded inputs. A question's overlay can
  keep its name while its content changes, and the prefix tells the versions apart (the ageing project's request).
  Notes are not part of a bundle, so no bundle id moves.

## 0.4.25 (2026-10-19)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. No profile change.

- **An SDRF label that names no label no longer decides a deposit's label.**
  - **The bug:** two community-annotated SDRFs said `NT=isotope labeling;AC=PRIDE:0000310` on every row, a term that
    names no label, while PRIDE's own record said label-free (MS1 intensity, spectrum counting). 0.4.24 took the cell
    as the label and parked both deposits under a status spelled from the cell's text, with no row in
    `decisions_needed.tsv`.
  - **The fix:** a label cell naming no label we know yields to a curated SDRF, then PRIDE's quantification methods (a
    label, or label-free), PRIDE's identified PTMs and the text. With none of them, the deposit waits as
    `waiting_unknown_label`, and `decisions_needed.tsv` asks a person what the label is.
  - A label the SDRF does name (TMT, SILAC, dimethyl, ...) decides as before.
- **To give such a deposit its look:** `pxreprise batch retry <question.toml> <PXD> --reason "..."`; a `waiting_`
  status is otherwise settled.

## 0.4.24 (2026-10-18)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. No profile change.

- **A re-search now keeps what the manifest entry's owner wrote, whatever the manifest's line ends.**
  - **The bug:** with a manifest saved with Windows line ends (CRLF), 0.4.23 and earlier read no `status`, `flags`
    or `enrichment` from the old entry. A re-search then dropped the owner's flags. The ageing project's manifest is
    CRLF, and one re-search lost its `enriched` flag. The notes and the owner's own keys were kept, as before.
  - **The fix:** every reader of a manifest now parses it with LF line ends, and a write keeps the file's own line
    ends.
- **A re-search keeps the entry's `enrichment`.** It used to take the screen's reading of the deposit's text, which a
  re-search does not change, over a value the owner had curated: isolated mitochondria (`other`) became `none`. When
  the two differ, the entry keeps its value and its notes say what the screen reads (`ENRICHMENT KEPT`), so the owner
  can change it.

## 0.4.23 (2026-10-17)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. One new profile,
`label-free-dda@8`; nothing changes for a question until it names it. Its databases must be in your `database_dir`.

- **New profile `label-free-dda@8`: `@7` on the ageing project's fourth database release, agingPTM-v4.**
  - **What it is:** the same UniProt 2026_03 canonical proteomes as before, with the biological PTM sites rebuilt
    from the ageing catalog of 2026-10-17 (163 datasets). agingPTM-v3 carried the sites of the catalog of 2026-10-06
    (97 datasets).
  - **The rule is unchanged:**
    - each site is a target, at q <= 0.01 and ambiguity level 1, with at least 5 PSMs summed over the organism's
      datasets;
    - only the 21 biological modifications are taken, never deamidation or citrullination;
    - a site UniProt already annotates with the same chemistry is not added.
  - **Sites:** human 10,492 (was 6,602), mouse 15,868 (was 6,309), rat 2,667 (was 1,442).
  - **The isoform files are rebuilt with them.** The compartment isoforms now carry sites too.
  - **Measured on one run each (MetaMorpheus 1.1.12's Search task):**
    - a high-resolution mouse run found 31,536 PSMs (31,459 before), and 901 carried a biological modification
      (785 before);
    - an ion-trap run lost 4% of its PSMs to the larger search space, so the ion-trap profile stays on agingPTM-v3.
- **A site is never counted from a deposit that enriches its modification**, as before. The list of such deposits is
  now built from the batch's own record: the manifest's enrichment, the probe's measured enrichment, and a
  polyubiquitin capture named in the deposit's text. The database build adds the deposits whose runs measure phospho
  or GG enriched. 41 exclusions were used.
- Each search's `provenance.json` names its databases and their sha256, as before.

## 0.4.22 (2026-10-17)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. One new profile,
`label-free-dda-lowres@3`; nothing changes for a question until it names it.

- **New profile `label-free-dda-lowres@3`: an ion-trap search's mass windows now fit the instrument's offset.**
  - **The problem:** the `lowres` profiles run no calibration, yet `lowres@2` kept MetaMorpheus's ±5 ppm precursor
    window and FlashLFQ's 5 ppm. Both assume calibrated masses.
  - **What it cost:** on a deposit whose precursors were 8.7 ppm off, `lowres@2` identified 0.37% of the spectra. In one
    of its runs it found 292 PSMs at 1% FDR; within ±14 ppm the same run gives 19,120 PSMs, 10,212 peptides and 2,312
    protein groups, and FlashLFQ quantifies 10,935 peptides instead of 123. Seven more of the 23 `lowres@2` deposits
    in the aging batch lost identifications the same way, four of them many.
  - **Why not one wide window:** a fixed ±20 ppm costs a deposit with no offset. One run with a 0.0 ppm offset lost 21%
    of its PSMs and 17% of its peptides.
  - **What `lowres@3` does instead:** it runs the probe search, which takes precursors within ±20 ppm and measures
    each probe run's median offset. The search then sets the precursor window and FlashLFQ's to 5 ppm beyond the
    largest offset, rounded up, at most ±20 ppm. When no offset exceeds 2 ppm, MetaMorpheus's own windows are kept.
  - **What else changes:** the probe search's checks now apply to ion-trap deposits too: species, alkylation, no
    identifications, an unstated enrichment.
  - **What is recorded:** each search's `provenance.json` (`precursor_window`) records the offsets and the window,
    and `batch.log` names them after the probe.
- **A profile can set this with `[engine] precursor_window = "probe"`.** It needs `probe_search = true`, and a profile
  with a Calibration task cannot set it, because calibration sets its own windows.
- **To re-search a deposit already searched under `lowres@2`:** name `lowres@3` in the question ahead of `lowres@2`
  (a deposit goes to the first ion-trap profile in the question's order), then `batch research` it. If you remove
  `lowres@2` from the question instead, the catch-up re-searches all of them.

## 0.4.21 (2026-10-17)

No MetaMorpheus change. **Machine file:** no change needed; two optional settings are new. **Safe mid-batch?** Yes:
swap the pin as usual. No profile version changes. A deposit is searched again only by the catch-up below, which your
machine file already turns on or off with `catch_up_every`.

- **Your machine file can set how big a deposit is searched whole.** A new `[deposit_limits]` table takes the
  profiles' four size gates: `max_files`, `max_file_mb`, `max_est_ms2` and `defer_if_median_file_mb`. Each key you set
  replaces the profile's value, and a question's `[exceptions]` still wins for its own deposit. These gates choose
  which deposits are searched, never how, so changing one needs no new profile version. Like `search_timeout_h`, they
  describe what the machine and its network can take. See `machines/example.toml`.
- **A running batch takes up changed limits without stopping.** The driver reads `[deposit_limits]` and
  `probe_search_minutes` from its machine file again, at most every 30 seconds, and `batch.log` says `limits changed`.
  A file that no longer loads leaves the limits as they were, and the log says that too. Every other setting is still
  read once, at start.
- **A deferred deposit gets one more look when the limits change.** A deposit deferred by a size gate is looked at
  again, once, when the machine's limits change and now fit what its deferral measured: the median file, the estimated
  MS2 scans or the file count. If it still does not fit, it settles again. A raised gate takes up exactly the deposits
  it now covers, with no `batch retry`.
- **`probe_search_minutes` in the machine file** replaces the profile's limit for the short probe search, which takes
  longer on larger files.
- **The catch-up re-searches go on when the queue runs out.** Before, a deposit searched under a profile the question
  no longer names was re-searched only at every `catch_up_every`-th start. When no new deposit was left, the driver
  exited with re-searches still due: 91 of them on 2026-10-17. Now every start is a catch-up re-search until none is due.
- Each search's `provenance.json` records the machine's `deposit_limits` when it sets any, and `batch.log` names them
  at start.
- **The probe search takes precursors within ±20 ppm, not MetaMorpheus's default ±5 ppm.** The probe runs no
  calibration. On an instrument whose precursor masses were off by +7.6 ppm, it found 12 to 31 PSMs a run and parked the
  deposit as `waiting_no_identifications`, while the calibrated search of the same files had found 24,449. The probe only
  decides whether and how a deposit is searched; the search itself is unchanged. A deposit the probe parked that way can be
  taken up again with `batch retry`.

## 0.4.20 (2026-10-16)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. No profile version
changes and nothing is searched again. If a search under 0.4.19 or earlier added a spectrum like the ones below to a
spectral library, run `pxreprise library repair <organism>` with no batch running before the next batch starts.

- **A spectral library no longer takes in spectra that mzLib cannot read back.** mzLib's library reader takes any
  spectrum whose name holds a count in parentheses for a crosslink. Unimod names an isotope label that way:
  `NEM:2H(5)`, `Label:13C(6)`, `Dimethyl:2H(4)`. MetaMorpheus wrote such spectra into the libraries it updated, but
  reading that library back then fails. The next search that used the mouse library failed after almost 2 hours with
  "Bad spectral library formatting or indexing", and the batch switched the library off for the rest of its run.
  Those spectra are now left out of the version the batch registers, and the registry records how many
  (`dropped_unreadable`). MetaMorpheus's own file stays whole in the search's folder.
- **`pxreprise library list` and `pxreprise library repair <organism>`.** `repair` writes a new current version
  without such spectra (mode `repair`), keeps every earlier version, and changes nothing when there are none. `list`
  shows each organism's current version. See the Commands page.

## 0.4.19 (2026-10-15)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. No profile version
changes and nothing is searched again. Add a `species` exception to a question only once its batch runs 0.4.19: 0.4.18
refuses the key, and a batch that cannot read its question does not start.

- **A deposit digested with pepsin or proteinase K waits instead of being searched for nothing.** The chemistry reader
  maps both to MetaMorpheus's `non-specific` protease. A Classic search with that protease makes no peptide as long as
  the profile's minimum, so the probe search of a pepsin deposit found nothing in 25 seconds, and the deposit was
  parked `waiting_no_identifications`, as if a person had to work out what it was. It now waits
  `waiting_nonspecific_cleavage` before any download, as an immunopeptidome does, until a profile searches
  non-specific cleavage. A deposit parked the old way is moved by `batch retry`.
- **A deposit whose PRIDE record names an organism outside the question waits for a person.** A record named mouse
  and the killifish *Nothobranchius furzeri*; the queue took mouse, and 34 killifish runs were searched as mouse beside
  20 mouse runs. Before any download, the record's organisms are now compared with the question's `organisms`. One
  outside the list parks the deposit `waiting_other_organism`, listed in `decisions_needed.tsv`. An `[exceptions]`
  entry that selects the runs (`include_files` / `exclude_files`) or names the `species` is the answer, and the deposit
  then gets one more look by itself. A question with no `organisms` list is not checked. Deposits already searched are
  checked when they are searched again.
- **`[exceptions] species`: the organism a deposit is searched as.** A deposit filed under the wrong organism in PRIDE
  (rat filed as mouse) is searched as the organism the question names: a database key of its profiles, such as
  `"rat"`. The database, the species the probe expects, the spectral library and the delivered taxon all follow it,
  and the experiment record says where it came from. `validate` and `batch run` refuse a key that no profile of the
  question has a database for.
- **The question file's `[exceptions]` table is now documented** (Questions page).

## 0.4.18 (2026-10-15)

No MetaMorpheus change. **Machine file:** no change; `min_free_gb` now also governs every download. **Safe mid-batch?**
Yes: swap the pin as usual. No profile version changes and nothing is searched again. Searches delivered before 0.4.18
keep their per-file outputs; delete those by hand if you need the space.

- **Downloads stop at the disk floor.** The floor (`min_free_gb`) was checked only before a deposit started. A 98-file
  deposit (95 GB) began downloading with 125 GB free while another deposit's search and a publish were writing to the
  same disk, and the disk filled: the search failed while writing its results and the publish failed while building the
  catalog. A fetch now checks first that all its files fit above the floor, and checks again as each file starts. A
  deposit that does not fit is left `fetch_paused_disk` with the files it already has. The driver starts no further
  deposit and stops once the searches under way finish. The next driver checks the files already on disk against
  PRIDE's checksums, so they are not downloaded again.
- **After delivery, search outputs that nothing reads are deleted.** MetaMorpheus writes every result table a second
  time per raw file ("Individual File Results": PSMs, peptides, protein groups, quantified peaks, mzIdentML), about 7 GB
  per deposit. The engine also kept the Percolator input table and its own copy of the spectral library it had just
  registered. Once a deposit's ingest succeeds, these are deleted and each path is recorded in `09b_prune`. The library
  copy is deleted only when its bytes match the version in the library registry. The combined tables, `results.txt`,
  the protein groups, logs and provenance are kept: they are what ingest, the QC payload and store corrections read.
  MetaMorpheus still writes the per-file outputs during the search: `results.txt` has its per-file counts only when it
  does, and the QC payload reads them.
- **A failed search that never reached `state.json` is recorded, and `batch retry` takes a failed search.** When the
  disk filled, a search failed and the same full disk refused the driver's record of it. The deposit read `searching`
  next to a failed search on disk, and no later pass would take it up. A new driver now records such a deposit as
  `search_failed`. `batch retry` on a deposit whose search failed moves `04_search` aside (`04_search.failed-<time>`, never
  deleted) and requeues it. Its raw files stay, and they are checked rather than downloaded again. A successful search is
  still refused: use `batch research` for that.

## 0.4.17 (2026-10-14)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. A deposit already
delivered keeps the enrichment it was screened with; correct one in the manifest by hand. A deposit parked
`probe_search_failed` by a leftover design is taken up again by the next driver on its own.

- **A probe search no longer reads a design left beside the spectra.** Every full search writes
  `ExperimentalDesign.tsv` beside the spectra, naming all of its files, and MetaMorpheus reads a design there on every
  run, whether it quantifies or not. When a reboot cut off a 61-file search, the deposit's re-probe of 3 files read that
  design and MetaMorpheus stopped (`Condition "all" biorep 2 is missing!`). A retry or re-search of a searched deposit
  could meet the same leftover. The probe now removes a leftover design before it runs; the full search writes its own afresh,
  as before. An interrupted search's design is moved aside with its output. A `TmtDesign.txt` there stops the probe or
  a search with a design: PXReprise writes none, so one was put there by hand.
- **"Lectin" inside another word no longer makes a deposit glyco-enriched.** A FLAG/HA immunoprecipitation was screened
  as glyco because its protocol said "collecting the FLAG peptide eluate", and glyco is checked before
  immunoprecipitation. "Selecting" and "reflecting" did the same. Lectin now counts only as a word ("lectin affinity
  enrichment", "lectins").
- **An enriched deposit's log line and manifest note quote the words that made it enriched.** They quoted the text
  behind the acquisition and labelling calls instead, which is empty for nearly every enriched deposit, so the note read
  "the deposit's own text says '......'". The screen now keeps the enrichment's own words (`enrichment_evidence` in the
  batch state, and the `enrichment` fact of the experiment record).

## 0.4.16 (2026-10-13)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. A deposit settled
`excluded_duplicate` stays settled until someone runs `batch retry` on it. A deposit already searched as label-free is
not screened again; hold it back by hand if its label is found later.

- **A dimethyl label stated only in PRIDE's list of identified modifications is read.** A deposit whose description
  names no label and whose protocols are "Not available" listed "4x(2)H labeled dimethylated residue" among its
  modifications. It was searched as label-free and identified 0.2% of its spectra. Its profile ran no probe search
  that could have caught it. An isotope-labelled dimethyl in that list is now a label, and the deposit waits on a
  labelling capability. A plain "dimethylated residue" is the natural modification and is not a label.
- **A copy the question's manifest excludes is no longer an original for the deposit it copies.** One deposit had been
  searched, then deposited again under a second accession, and the copy was searched too before the duplicate screen
  existed. The manifest excludes the copy (`status: exclude`, `reason: duplicate_of: <original>`). A later re-search of
  the original was refused as a duplicate of its own copy. The screen now leaves out such copies and logs that it did.

## 0.4.15 (2026-10-13)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. A question that
uses the new `alkylation` key cannot be read by an older version: add it just before the swap. A deposit parked
`waiting_species` or `waiting_alkylation_check` stays parked until someone runs `batch retry` on it.

- **A probe run's species is judged only from 500 species-specific PSMs (was 50).** In a gel band with little protein,
  human skin proteins from handling that the contaminant panel lacks can outnumber the sample's own: one mouse band
  had 55 human PSMs, mostly epidermal proteins, against 49 mouse, and parked its deposit while the other probe runs
  had thousands of mouse PSMs. A weaker run is now noted in the experiment record and not judged. Every run of the
  probe's calibration set has at least 983, so its calls are unchanged, the deposit whose runs really were another
  species included.
- **A question can state that a deposit's cysteines were never alkylated.** `alkylation = "none"` in the deposit's
  `[exceptions]` block searches it with no cysteine modification, whatever its text or PRIDE's list says. It is the
  only value the key takes; another alkylant is named in a curated SDRF. The chemistry record gives the source as
  `question_exception`, and when such a search is flagged `cysteine_poor`, the manifest note says the low share is
  expected: only free cysteines can be identified.

## 0.4.14 (2026-10-13)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual. A deposit already
searched is not looked at again, except by a catch-up re-search (D39), which screens it anew.

- **Dimethyl labelling is recognised in more of its wordings.** The screen matched only "dimethyl label", and the
  chemistry reader two wordings more. A deposit saying "labeled using dimethyl isotope labeling" passed as label-free,
  and its probe found no peptides at all, since every peptide carries the label. "Dimethyl isotope", "dimethylation"
  and "isotope dimethyl" wordings now count, and such a deposit waits on a labelling capability, as SILAC does.
- **A probe search that identifies nothing parks the deposit.** Before, a probe run with no PSMs was not judged, and
  the deposit went on to its whole download and search on no evidence. Now, when no probe run reaches 50 PSMs at 1% FDR,
  the deposit waits (`waiting_no_identifications`), and the decision is listed in `decisions_needed.tsv`.

## 0.4.13 (2026-10-13)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual (STOP, let it drain,
start the new pin). A deposit that 0.4.12 parked `waiting_species` stays parked until someone runs `batch retry` on it.

- **Contaminants no longer decide a run's species.** The probe search counts each run's PSMs specific to one species. A
  protein that is both in the contaminant panel and in a species database, such as a human skin keratin, is kept as that
  species' protein. So keratins from handling counted as human. In gel bands of a mouse deposit with little protein,
  they outnumbered the mouse PSMs, and the deposit waited as another species. A PSM matching any panel protein now
  counts for no species. The experiment record gives each run's count of them (`contaminant_panel_psms`). On that
  deposit's own probe search, every run is mouse once they are left out.

## 0.4.12 (2026-10-13)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual (STOP, let it drain,
start the new pin). A deposit whose probe search timed out under 0.4.11 is taken up again by the new driver by itself.

- **The probe reads its own files only.** The probe's QC and its search (`label-free-dda@7`) took every raw file in the
  deposit's download folder, which holds only the probe files, except when 0.4.10 had downloaded the whole deposit
  ahead under STOP. Then the probe checked and searched the whole deposit and hit its 60-minute limit. That happened
  to the first such deposit, 20 files and about 1.2 million MS2. They now read the three files the probe chose, and
  the log says how many more are on disk.
- **A deposit already downloaded is searched first,** as 0.4.10 said it would be. The driver took the queue in order,
  so after a pin swap it downloaded the next deposit (about 2 hours for 57 files of 2.2 GB) while three downloaded
  deposits waited and both search slots stood idle. A deposit whose selected files are all on disk now goes ahead of
  the queue order, another species before one already searching.

## 0.4.11 (2026-10-12)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual (STOP, let it drain,
start the new pin). To use the probe search, the question names `label-free-dda@7` in place of `@6`; deposits already
searched keep the profile that searched them.

- **New profile `label-free-dda@7`: `@6` with the probe search on.** The probe search was built in 0.4.0 but no
  shipped profile turned it on, so no deposit was ever checked by it. Before the whole deposit is downloaded, its probe
  files are now searched briefly (3-19 minutes each in our trial) against every species database the profile has.
  A run of another species, a run of mixed species, and a whole proteome expected to be carbamidomethylated with under
  2% cysteine-containing peptides each wait for a person (`waiting_species`, `waiting_mixed_species`,
  `waiting_alkylation_check`) instead of being searched. A measured enrichment the text did not state is recorded and
  flagged. Two deposits searched under `@6` had 0.5% and 1.2% cysteine peptides (alkylated lysates run 11-28%); their
  manifest entries carry the after-search `cysteine_poor` flag, but nothing stopped them.
- **Iodoethanol is a known alkylant.** A deposit whose protocol, SDRF or PRIDE record names iodoethanol (S-hydroxyethyl
  cysteine, UniMod 278 `Ethanolyl`) is searched with it in place of carbamidomethyl. This applies under every profile
  that reads each deposit's chemistry (`@3` onward), from this version on. One deposit searched with
  carbamidomethyl had 0.5% cysteine peptides for this reason.

## 0.4.10 (2026-10-12)

No MetaMorpheus change. **Machine file:** no change. **Safe mid-batch?** Yes: swap the pin as usual (STOP, let it drain,
start the new pin). The new drain applies from the first STOP the new version itself receives.

- **STOP no longer stops the downloads.** A STOP lets the searches under way finish, as before, but the driver now goes
  on downloading the next deposits in the queue meanwhile (screen, probe and whole fetch, as usual) and leaves them
  `fetched`. The next driver checks those files against PRIDE's checksums instead of downloading them again, and
  searches them first. When the last search ends, the download in flight is cut (the next driver takes that deposit up
  again) and the driver exits, so a drain lasts no longer than before. Before, a pin swap left the downloads idle for
  the whole drain: about 6 h on 2026-10-12.
- **A deposit that finishes downloading after STOP is no longer searched by the stopping driver.** It is left `fetched`
  for the next one. On 2026-10-12 a deposit finished downloading 85 minutes after STOP, and its 6 h search held the pin
  swap until it ended.
- **Correction to 0.4.9: the Globus host damages copies too.** In pride's later rounds it returned 2 damaged copies of
  15, against 6 of 10 from `ftp.pride.ebi.ac.uk`. It stays the HTTPS host (damaged less often, and about twice as fast),
  but it is not a fix: checking every file is the defence. Damage grew with how long a copy took (every copy slower than
  about 0.2 MB/s was damaged), so on a slow line a lower `parallel_downloads` may help.

## 0.4.9 (2026-10-12)

No MetaMorpheus change. **Machine file:** two optional keys, `aspera_attempts` and `pride_https` (below); without them
you get the new download routes. **Safe mid-batch?** Yes: swap the pin as usual (STOP, let it drain, start the new pin).
Deposits already delivered are not re-screened.

- **Raw files over HTTPS now come from EMBL-EBI's Globus host, and a damaged Aspera copy switches to it at once.**
  EBI's helpdesk answered our report of damaged downloads with "try Globus". In a side-by-side trial on 2026-10-12, the
  same five files came back damaged 2 of 5 times from `ftp.pride.ebi.ac.uk` and 0 of 5 from the Globus host (0 of 15
  over all its runs), while Aspera had damaged 24% of transfers since 2026-10-08.
  - HTTPS downloads use `https://g-a8b222.dd271.03c0.data.globus.org/pride/data/archive/...`, the same paths as PRIDE's
    FTP site: anonymous, with no Globus software or login. mzLib needs no change.
  - Aspera stays first (about twice as fast per file when clean), but the first damaged Aspera copy sends that file's
    remaining attempts to HTTPS; before, three Aspera attempts came first whatever happened.
  - A Globus attempt that fails to deliver is followed by one from `ftp.pride.ebi.ac.uk`, then Globus again.
  - Every transfer is logged with its route, size and time (`... arrived over Globus HTTPS (attempt 2): 1315 MB in 840
    s`), so the routes can be compared from batch.log.
  - The Globus host cannot resume a dropped download; it restarts from byte 0. Every file is still checked.
  - `aspera_attempts` (default 3) sets how many attempts go to Aspera; 0 sends every attempt to HTTPS. `pride_https`
    is `"globus"` (default) or `"ftp"`, the old host.
- **Acetyl-lysine and succinyl-lysine captures are recognised as enrichments.** Two deposits holding anti-acetyl-lysine
  captures beside their whole-proteome runs were stored as unenriched (PXD038328, PXD061654), because the screen had no
  acetyl or succinyl terms although dataRepo's vocabulary has both.
  - **Text:** phrases that name the capture ("acetyl-lysine enrichment", "enriched for acetylated peptides",
    "anti-acetyllysine", "acetylome") now give `acetyl`, and their succinyl forms `succinyl`. "Acetyl (Protein N-term)"
    as a variable modification, or a GO enrichment of acetylated proteins, does not.
  - **File names:** `acetylom(e)`, `succinylom(e)`, `phosphoproteom(e)` and `proteom` now count, so a deposit of
    `_acetylom_` and `_proteom_` runs is flagged `possibly_mixed_enrichment`.
  - **Probe search:** acetyl-K is a variable modification of the probe, counted as MetaMorpheus's `Acetylation on K` or
    a UniProt `N6-acetyllysine` site. A probe run at 5% or more is a measured enrichment (whole proteomes run 0.1-0.5%,
    these captures 18-51%).
  - **Manifest:** a measured enrichment the text did not state used to stop at state.json. The entry now says so in a
    note and flags `possibly_mixed_enrichment`, so the enrichment is curated before pooling.

## 0.4.8 (2026-10-12)

No MetaMorpheus change. **Machine file:** nothing. **Safe mid-batch?** Yes: state.json, the manifest and the machine
file need no change. Swap the pin as usual (STOP, let it drain, start the new pin). A deposit that stopped as
`fetch_failed` on leftover files needs `batch retry` once.

- **A file left out of the search no longer makes qc reject the QC report.** The payload marked such a file
  `"excluded": true`; qc's schema wants the reason, a non-empty string (qc QC-Q20). Each excluded file now carries the
  QC report's own reason (`"qc: too_few_ms2"`), or else the search's `excluded_files.reason`. PXD014274 (2026-10-12),
  the first deposit with excluded files since the payload began marking them, was delivered with its QC report
  rejected and unrendered; its data were unaffected.
- **A deposit fetched again under a new file selection no longer fails on the files the old one left.** QC and the
  search read every raw file in `02_fetch/spectra`. PXD057407 had five phospho runs from an earlier attempt beside the
  ten global runs its question now selects, so the fetch counted "15 of 10" and stopped as `fetch_failed`; had the
  count matched, the extra runs would have been searched too.
  - A whole-deposit fetch now moves every raw file its selection does not name, with its `.partial` files, into
    `02_fetch/not_selected/`, and says so in batch.log and state.json (`set_aside`).
  - Cleanup deletes those files after the search, and now also any stale `.raw.partial` left beside the raw files.

## 0.4.7 (2026-10-11)

No MetaMorpheus change (1.1.12 and 1.1.11, as in 0.4.6). **Machine file:** nothing. **Safe mid-batch?** No: stop the
batch, let it drain, update, then start it again. **Question file:** two optional `[publish]` keys, below; a question
without them gets the new defaults.

- **Publishing is scheduled, no longer run after each ingest.** A large catalog takes long to build: on 2026-10-11 the
  ageing project's took about an hour, and the fixed 1-hour limit killed four publishes in a row, so nothing new was
  served for six hours.
  - The question's `[publish]` command now runs at most every `every_h` hours (default 6), only when something was
    delivered since the last successful publish. The first one after a start runs at once, and a driver that runs out of
    queue publishes what waits before it exits. `every_h = 0` restores a publish after each ingest.
  - `timeout_h` (default 4) is how long a publish may run before it is killed; it was a fixed hour.
  - One publish serves every deposit delivered since the last success. Each one's `publish_rc` is written when it is
    served; after a failure they all wait for the next publish. state.json's new `publish` record keeps the last start,
    end, exit code and log.
  - A driver stopped by STOP lets a publish under way finish, starts no new one, and says how many deposits wait for the
    next driver.
- **A killed process keeps what it wrote.** A publish or ingest killed at its limit used to leave only "TIMEOUT after
  N s"; its output up to the kill is now in its log (G38's file) too.

## 0.4.6 (2026-10-11)

**Upgrading from 0.3.11, the last public release.** 0.4.0 to 0.4.5 were not released on their own; this release carries
all of them, and their sections below say what each added. Taken together:
- **MetaMorpheus 1.1.12** (mzLib 1.0.594) for `label-free-dda@4`, `@5`, `@6` and both `label-free-dda-lowres` profiles;
  1.1.11 stays for `@1` to `@3`. In the machine file:
  - add `"1.1.12" = "<path to 1.1.12's CMD.exe>"` under `[metamorpheus]`;
  - before the first ingest of a 1.1.12 search, give `metamorpheus_settings_root` a `1.1.12` folder (a copy of the
    install), or datarepo resolves no modification;
  - keep `fetch_stall_minutes` at 5 or more (0.4.0).
- **Machine file, optional keys:** `search_slots` and `catch_up_every` (0.4.1); `aspera_ascp` and `aspera_key`, together
  (0.4.4). Recommended: datarepo 1.3.2 (below).
- **Databases:** `@5`, `@6` and the `lowres` profiles name UniProt's canonical proteomes with the ageing project's sites
  (`*_all_genecentric_2026_03_agingPTM-v3.xml`) under `database_dir` (0.4.2).
- **Safe mid-batch?** No: stop the batch, let it drain, update, then start it again.

**From 0.4.5:** no MetaMorpheus change (1.1.12). **Machine file:** nothing required. Recommended: point `datarepo` at
datarepo 1.3.2, whose catalog build no longer crashes while something reads the catalog; no re-ingest, and the aging
box moves to it with this release. **Safe mid-batch?** No: stop the batch, let it drain, update, then start it again.
**New profiles:** `label-free-dda@6` and `label-free-dda-lowres@2`. Nothing changes for a question until it names them;
profiles already named keep searching exactly as before.

- **Profile `label-free-dda-lowres@2`: MS2 fragmented by HCD and read in the ion trap is searched** (a Fusion, Lumos or
  Eclipse "HCD-IT" method; PXD057407).
  - `@1` refuses such a file as "not a fragmentation MetaMorpheus can be told per file". `@2` searches it as HCD at the
    profile's low-resolution product tolerance (±0.5 Da), written into the file's own settings, as `@1` does for ion-trap
    CID.
  - A file that mixes ion-trap and Orbitrap MS2, or ion-trap CID and ion-trap HCD, is still refused: a file gets one
    fragmentation.
  - The new profile key behind it is `engine.ion_trap_hcd`, which needs `fragmentation = "per_file"`.
- **Profile `label-free-dda@6`: a thiol reagent with no UniMod entry can be searched with a modification the profile
  ships.** The first is NBF-Cl (4-chloro-7-nitrobenzofurazan; PXD058611).
  - **Before:** such a deposit waited as `waiting_unknown_alkylant`. Under `@6` it is searched with "NBF on C"
    (C6HN3O3, +163.00179 Da) as a VARIABLE modification and no fixed cysteine modification.
  - **Why variable:** a probe search found NBF on 76-86% of the identified cysteines, and the rest unmodified
    (`results/nbf-probe_2026-10-11`).
  - **Two new profile keys:**
    - `engine.custom_modifications` names a MetaMorpheus modification file shipped under `profiles/lists/`, here
      `PXReprise_custom_modifications_v1.txt`. It is read at load with mzLib's own reader, so a malformed entry stops
      the profile from loading.
    - `[thiol_reagents]` maps a reagent the chemistry reads to one of that file's modifications on C.
  - **Before a search or probe search that uses one, PXReprise copies the file into the MetaMorpheus settings folder
    the run reads** (`metamorpheus_settings_root`, `Mods`).
    - When that folder does not exist yet, MetaMorpheus is asked to create it first (`-g --mmsettings`). It is never
      created by hand.
    - A different file already there under the same name is refused, never replaced: a changed list ships under a new
      name.
  - **Everything else in `@6` is `@5`:** the same databases, tasks and settings. A deposit that names no such reagent is
    searched exactly as under `@5`.
  - **In provenance,** a custom modification has `"unimod": null` and its MetaMorpheus id (`PXReprise\tNBF on C`), and
    the installed file is recorded as an input.
- **Every ingest's and publish's whole output is kept.** It goes to `<state dir>/outputs/<accession>_<step>_<time>.log`.
  `batch.log` and `state.json` keep the 1,200-character tail, as before, plus the file's path (`ingest_log`,
  `publish_log`). The tail alone lost a .NET exception's type and message twice (PXR-R14).
- **A search whose label-free quantification MetaMorpheus skipped is still never delivered.** MetaMorpheus 1.1.12 can
  drop quantification with only a warning: in Calibrate, GPTMD, Search, when a file fails calibration and its name is
  inside another file's (A1 and A10; QuantProject 049, fix MetaMorpheus #2784, not merged).
  - PXReprise already fails such a search: its "Skipping quantification" warning, or a missing
    `AllQuantifiedPeptides.tsv`, makes the search unsuccessful (D48). A test now pins this exact case.
  - None of the 117 searches on the aging box was affected (checked 2026-10-11).
- **datarepo 1.3.2's exit code 70** (an unexpected error) is handled like any failed ingest: not delivered, ingested
  again at the next start. Its last-line message is now kept in full (G38).
- **Docs:** the profile table in Concepts lists every shipped profile (it stopped at `@3`), with the note that all but
  `@2` need the ageing project's databases. It also says which profiles take an experimental design, and how a profile
  models a thiol reagent.
- **Checked live before release (2026-10-11),** through the engine's own search code on scratch folders:
  - `@6` on PXD058611 run 199: the settings folder was seeded and the file installed; NBF variable in all three tasks;
    no "Unrecognized mod"; 12,989 target PSMs at 1%; NBF on 86% of cysteine PSMs.
  - `lowres@2` on PXD052631's `..._SL_F8_...raw` (389 MB, every MS2 ion-trap HCD): QC decided "HCD, low resolution"; the
    file's own settings were HCD at ±0.5 Da; no warnings; 1,431 target PSMs at 1% of 16,051 MS2. Matched fragment errors
    reached 0.499 Da (median 0.07), so the low-resolution tolerance was in force. (PXD057407's 4 GB files never arrived
    whole from EBI that day: 6 of 6 copies carried its error text.)

## 0.4.5 (not released on its own; in 0.4.6)

**Updating:** no MetaMorpheus change, no machine-file change. **Safe mid-batch?** No: stop the batch, let it drain,
update, then start it again.

- **Deposits searched under `label-free-dda-lowres@1` now ingest.** That profile runs MetaMorpheus's Search task alone,
  so its results are in `Task1SearchTask`. The manifest entry always named `Task3SearchTask`, so `datarepo ingest`
  refused every deposit searched under it (PXD059520, 2026-10-08). The entry now names the folder from the Search
  task's place in the profile's task list. Three-task profiles still give `Task3SearchTask`.
  - **An entry written before 0.4.5 is not rewritten:** the batch never edits an existing entry. Change its
    `search_results` to `04_search/mm/Task1SearchTask` by hand. The next start re-ingests it with no new search.

## 0.4.4 (not released on its own; in 0.4.6)

**Updating:** no MetaMorpheus change. **Machine file:** two optional keys, `aspera_ascp` and `aspera_key`, go together.
- `aspera_ascp`: the path of IBM Aspera's `ascp.exe`, version 4.4.4 or newer. IBM Aspera for Desktop installs one at
  `C:\Program Files\IBM Aspera\transferd\bin\ascp.exe`; Aspera Connect's 4.4.3 is too old.
- `aspera_key`: EBI's public key, published in EMBL-EBI KB0011565.

With neither, nothing changes. **Not for mid-batch.**

- **Raw files arrive over IBM Aspera first, HTTPS after.**
  - **Speed:** EBI serves PRIDE over Aspera at about five times the HTTPS rate measured on the aging box (4 sessions
    about 10 MB/s, HTTPS about 2 MB/s).
  - **Retries:** a file gets 3 Aspera attempts. Each is a fresh download into a private folder, never resumed, because a
    resume gave corrupted files. Sessions start 10 seconds apart. After 3 failures, HTTPS takes over.
  - **Checks:** every file is checked as in 0.4.3, whichever way it came.
- **PRIDE's MD5 is used where it has no SHA-1.** Newer deposits leave the file list's SHA-1 empty; their MD5s come from
  PRIDE's `files/checksum/{accession}`. A file is checked against whichever PRIDE gives, and always for EBI's error text.

## 0.4.3 (not released on its own; in 0.4.6)

**Updating:** no MetaMorpheus change, no machine-file change. **Safe mid-batch?** No: stop the batch, let it drain,
update, then start it again.

- **Every downloaded raw file is checked against PRIDE before it is searched.** On 2026-10-08, EBI's storage wrote its own
  error response (`<Error><Code>ConnectionClosedException…`, about 200 bytes) into the data of files it served, over HTTPS
  and over Aspera alike, and each file kept exactly PRIDE's size. 8 of about 25 PXD012307 transfers carried it. One of
  them was a file the batch had already fetched and would have searched.
  - The fetch now hashes each file (SHA-1) against PRIDE's own checksum, read from PRIDE's v3 file list, and scans it
    for that error text.
  - A file that fails is deleted and fetched again, as a dropped transfer is. Files already on disk from an earlier
    fetch are checked the same way.
  - The check also runs where PRIDE gives no checksum; the error text alone condemns a file then.
- **Deposits searched before 0.4.3 were checked by size only.** Their raw files were deleted after searching, so they
  cannot be checked now. The catch-up re-searches (D39) fetch them again, with this check.

## 0.4.2 (not released on its own; in 0.4.6)

**Updating:** no MetaMorpheus change (1.1.12). **Machine file:** nothing required. The aging box turns on
`catch_up_every = 4` (D39). **Databases:** profile `label-free-dda@5` names three new files under `database_dir`:
`UP000005640_all_genecentric_2026_03_agingPTM-v3.xml`, `UP000000589_...` and `UP000002494_...`. They are built by
`tools/ptm-db/BuildPtmDb.cs` from UniProt's canonical proteomes (see `data/ptm-db/agingPTM-v3/`).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again.

- **A new profile, `label-free-dda@5`:** `@4` on UniProt's **canonical** reference proteomes (D38). That is one protein
  per gene, Swiss-Prot plus TrEMBL for genes Swiss-Prot lacks: human 20,652, mouse 21,860, rat 21,471 entries, against
  20,416, 17,277 and 8,228 reviewed. agingPTM-v3 carries v2's sites. The deposit caps rise to 120 raw files and 3,000,000
  estimated MS2 (were 60 and 1,500,000), because MetaMorpheus 1.1.12 and two search slots search much faster.
- **A new profile for MS2 read in the ion trap, `label-free-dda-lowres@1`.** It runs the Search task only: no
  calibration until low-res calibration is merged in MetaMorpheus, and no GPTMD (the user). It searches against the same
  canonical agingPTM-v3 XML, with fragmentation written per file (ion-trap CID as LowCID, product tolerance
  ±0.5000 Absolute), and keeps no spectral library. The screen never routes a deposit to it. A probe whose files fail
  only `low_res_ms2` under the question's earlier profile is re-checked under it, when the question names it. 34 aging
  deposits were skipped as `low_res_ms2` before this profile existed.
- **`BuildPtmDb.cs` reads either UniProt namespace.** It used to read only `http://uniprot.org/uniprot`, which UniProt's
  REST files use; UniProt's FTP files declare `https://` and were read as empty. It now refuses to write a database
  that matches fewer than half of the sites, instead of writing it with zero sites.

## 0.4.1 (superseded by 0.4.2 before it ran; in 0.4.6)

**Updating:** no MetaMorpheus change (1.1.12 as in 0.4.0). **Machine file:** two optional keys, both off by default:
- `search_slots` (default 1): how many searches run at once;
- `catch_up_every` (default 0 = off): every Nth search started is a re-search of a deposit searched under a profile
  the question no longer names (D39).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again.

- **Searches run side by side** (`search_slots = 2` on the aging box; the user: "start a second search immediately
  following any complete download"). A deposit starts searching as soon as it is downloaded and a slot is free, and the
  next one is screened and fetched meanwhile. One slot keeps the old rhythm.
  - **Another species goes first:** while a search runs, the next deposit is the first queued one of another species,
    if any. Two searches of one species cannot both extend its spectral library: the second to finish is refused
    registration (no spectrum is lost; that run's library stays in its folder) and the search itself still succeeds.
  - **Shared steps stay one at a time:** delivery (manifest, ingest, publish) and spectral-library registration.
- **Catch-up re-searches** (D39, off until the canonical-proteome profile): every Nth start re-searches an old deposit.
  The earlier search is moved aside as `batch research` does. Order by gain: rat first, then deposits the question has
  a curated design for, then the oldest, preferring a species no running search has.
- **Every database GPTMD wrote is recorded** with its sha256 (G33). Before, only the first one found was recorded,
  which was the contaminant file, never the proteome the final search loaded.

## 0.4.0 (not released on its own; in 0.4.6)

**Updating:** **MetaMorpheus 1.1.12** (mzLib 1.0.594) for the new profile `label-free-dda@4`; 1.1.11 stays for the
older profiles. **Machine file changes:**
- add `"1.1.12" = "<path to 1.1.12's CMD.exe>"` under `[metamorpheus]`;
- before the first ingest of a 1.1.12 search, `metamorpheus_settings_root` must hold a `1.1.12` folder (a copy of the
  install), or datarepo resolves no modification and says so only in its findings;
- keep `fetch_stall_minutes` at 5 or more (below).

**Not for mid-batch:** stop the batch, let it drain, update, then start it again. Results already searched keep the
version that made them.

- **A new profile, `label-free-dda@4`:** `@3` (each deposit's own chemistry) on MetaMorpheus 1.1.12, with three more
  changes:
  - **an experimental design from SDRF** (`[quant] design = "sdrf"`). The question's curated SDRF comes first, then the
    deposit's own, through mzLib's `SdrfLabelFreeDesign`. With neither, PXReprise writes its own default SDRF
    (`pxreprise_sdrf/<PXD>.pxreprise_default.sdrf.tsv`: one condition, `all`, and each file its own biological
    replicate) and builds the design from that, so no search runs without one. MetaMorpheus quantifies with MBR from the
    design. A fractionated deposit needs a curated SDRF: under the default, each fraction counts as a sample;
  - **the search writes its own SDRF**, `experiment.sdrf.tsv`, stamped with the deposit's ProteomeXchange accession
    (`[engine] write_sdrf = true`; MetaMorpheus #2816, #2870);
  - **new PTM-annotated databases**, `*_agingPTM-v2.xml`;
  - **Run-level QC and contamination read protein-group columns through the design.** With a design, FlashLFQ names
    protein-group columns by sample (`aged_1`), not by file. Each label is now mapped to its one file through the
    search's `ExperimentalDesign.tsv`. A sample spanning several files (fractions, re-injections) gets no run-level value,
    and the payload's notes say so. Before this, the 1.1.12 trial reported `pg_missing_frac_msms = 1` for every run and
    lost `contaminant_intensity_frac`.
  - **Fragmentation per file, from the MS2 headers (new profile option, unused by any shipped profile):** with
    `[engine] fragmentation = "per_file"`:
    - QC decides each file's `DissociationType` from its own scans: HCD, CID, ETD or EThcD; LowCID for CID read in an
      ion trap; Autodetect only for a high-resolution mix of supported types;
    - QC fails an unsupported or mixed-analyzer file (`unsupported_fragmentation`);
    - the search writes the type into the file's `<raw>.toml`, beside its protease.
  - **A probe search before the full download (new profile option, unused by any shipped profile):** with
    `[engine] probe_search = true`:
    - the probe files are searched briefly against every species database the profile has, with variable phospho and
      GG;
    - each run's species, cysteine-peptide share and phospho/GG shares are measured and kept in the experiment record;
    - a run of another species parks `waiting_species`; a run mixing species parks `waiting_mixed_species`; a deposit
      expected to be carbamidomethylated but with almost no cysteine peptides parks `waiting_alkylation_check`. Each
      park is announced;
    - a measured enrichment is recorded beside the screen's call.
  - **Every deposit gets an experiment record, and every park that needs a person is announced.**
    - At the probe stage each deposit gets `01_experiment/experiment.json` and `experiment_notes.md`: every fact with
      its source and evidence, guesses marked, each probe run's measured fragmentation and reporter ions.
    - A park that needs a person's decision is listed once in the run root's `decisions_needed.tsv`, with the
      evidence and the choices: an unknown protease, modification or alkylant; several proteases; mixed labelling;
      too many files for a whole search; unsupported fragmentation.
  - **An unmodelled thiol reagent parks the deposit.** NBF-Cl named in a protocol, with no modelled alkylant beside it,
    now waits as `waiting_unknown_alkylant` instead of being searched as carbamidomethyl.
  - **MetaMorpheus's contaminant panel without the UPS spike-in standard**, used as it is
    (`MetaMorpheusContaminants_NoUPS.xml`, 216 entries; MetaMorpheus #2858). It ships under `profiles/lists/` because
    the MetaMorpheus 1.1.12 install does not have it yet. All 48 UPS proteins are out, including the 7 human ones
    (albumin, transferrins, lactalbumin, lysozyme, haemoglobins) that the older `contaminant_panel_exclude_v1.tsv`
    kept. In mouse and rat searches, fewer native proteins are therefore labelled contaminant. A profile now names a
    shipped panel with `[contaminants] file = "..."`, or MetaMorpheus's own with `panel = "..."`.

  Known result changes from 1.1.11 to 1.1.12, so do not pool or diff across the boundary:
  - protein N-terminal occupancy now counts Met-removed forms (mzLib #1337);
  - PEP is trained iteratively (MetaMorpheus #2844);
  - four modifications gain Unimod accessions;
  - PEP is faster (#2841).
- **PTM databases are rebuilt for every new pipeline build** (`tools/ptm-db/BuildPtmDb.cs`):
  - from a copy of the live catalog, never the file a publish may be writing;
  - **a site is never added from a deposit that enriches that PTM**, whether the enrichment is declared or measured
    (any run with at least 5% phospho or 2% GG among its 1% target PSMs);
  - the builder now reads the catalog with DuckDB 1.5.5, the version dataRepo writes with.
- **Per-deposit exceptions, as question data** (`[exceptions.PXD…]`):
  - a deposit can have its own `max_files`, `max_file_mb`, `max_est_ms2` and `defer_if_median_file_mb`;
  - it can choose its own runs with `include_files` / `exclude_files` (case-insensitive regular expressions on raw file
    names);
  - a reason is required;
  - a deposit deferred earlier gets one more look when an exception is added;
  - every use is logged, kept in fetch provenance (`subset_by_question`) and noted in the manifest
    (`question_exception`).
- **A search fails when MetaMorpheus ignores its design.** MetaMorpheus can drop an invalid design after one warning
  and quantify without it. The protein table must now carry one sample column per design sample.
- **New flags:**
  - `cysteine_poor`: under 5% of 1% target peptides contain cysteine (whole proteomes run 12-26%), which usually means
    the lab used an alkylant the search did not. Written to the manifest for non-enriched deposits only;
  - `output_sdrf_failed`: MetaMorpheus's SDRF writer crashed; the results stand.
- **Chemistry:**
  - NEM named as the only alkylant, even in a lysis buffer, is searched as variable NEM on C;
  - MMTS blocking is read as Methylthio;
  - two resolutions of one UNIMOD id must agree on mass, or the deposit waits (`waiting_unknown_modification`).
- **Fetch (mzLib 1.0.594: retry and resume inside PRIDE's client):**
  - an empty FTP listing, which mzLib now throws, is retried as before;
  - SDRF downloads are retried and size-checked like raw files;
  - an SDRF present only in PRIDE's FTP tree is flagged `sdrf_ftp_only`;
  - provenance says why `pride_checksum` is empty (PRIDE's REST list never fills it).
- **Census:** a `factor value[biological replicate]` column is no longer read as an experimental factor.
- **Manifest:**
  - `no_design_file` and `no_output_sdrf` now come from what the search did;
  - a re-search keeps every key it does not write (an owner's `excluded_runs`, `restrictions`, ...).
- **QC payload:** a file left out of the search is listed with `"excluded": true` (qctemplates 0.2.0).
- **`pxreprise batch research <question.toml> <PXD> --reason "..." [--profile key@n]`** searches a finished deposit again under another
  profile. The earlier output is moved aside, and the manifest entry is replaced once, keeping what its owner wrote.

## 0.3.11 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. The existing profiles search exactly as before; the new chemistry handling is a new profile a question opts into.

- **A new profile, `label-free-dda@3`, searches each deposit with its own chemistry.** See
  [Concepts](https://smith-chem-wisc.github.io/PXReprise/concepts.html).
  - **What it reads, before any download:** the protease, the cysteine alkylation and any label, from the question's
    SDRF, the deposit's SDRF, PRIDE's identified modifications and quantification method, and the protocol text.
  - **What it searches with:**
    - another protease than trypsin;
    - a different protease per raw file, through MetaMorpheus's per-file settings;
    - another alkylant than iodoacetamide;
    - a light/heavy pair such as d0/d5-NEM, as two variable modifications.
  - **Where names come from:** protease names and modification names come from mzLib, so they match what MetaMorpheus
    reads.
  - **What it records:** each choice and its source go into the search's `provenance.json`. A choice read only from
    protocol text is flagged `chemistry_guessed` in the manifest.
  - **When a deposit waits:** if it cannot be searched as read (`waiting_multi_protease`, `waiting_unknown_protease`,
    `waiting_unknown_modification`, `waiting_<label>_labelling`).
  - Otherwise `@3` is `@1`.
- **A search fails when MetaMorpheus ignores a setting.** MetaMorpheus only warns about a modification it does not know
  ("Unrecognized mod") or a per-file settings file it cannot read, then searches on without it. Either now fails the
  search. No search made so far logged either warning.
- **Top-down and DiLeu deposits wait** (`waiting_top_down`, `waiting_dileu_labelling`) instead of being searched as
  label-free bottom-up.
  - Top-down is read from the title, description and protocols only, because PRIDE's "Top-down proteomics" keyword is
    often set on bottom-up deposits.
  - DiLeu is never sent to the isobaric profile, which searches TMT masses.
- **Two new kinds of discovery keyword in a question's `[discover]`.** See
  [Writing a question](https://smith-chem-wisc.github.io/PXReprise/questions.html).
  - `disease_keywords`: a deposit found only by these is queued only if its PRIDE text names a reference group.
  - `watch_keywords`: deposits are found and screened, never queued.
  - The census writes `watch.tsv` and counts how many deposits each keyword would queue.
- **`pxreprise rank`** orders a census's deposits biggest first, within a file-count and size limit, from PRIDE file
  listings; no spectra are downloaded.
- **`pxreprise chemistry`** reports each deposit's protease, alkylation and label with their sources, and where they
  differ from MetaMorpheus's defaults.

## 0.3.10 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no search result changes.

- **Immunopeptidomes and peptidomes are no longer searched as tryptic.** Their peptides are not made by a protease, so
  a tryptic search finds almost nothing (two aging deposits: an identification rate of about 0.0015).
  - **What the screen reads:** the PRIDE record's text (immunopeptidome, ligandome, peptidomics, MHC or HLA peptides or
    ligands, an unspecified or absent enzyme) and its project tags (hupo-hipp).
  - **Where such a deposit goes:** it waits as `waiting_nonspecific_cleavage` until a profile that searches it exists.
    A profile opts in with `nonspecific_cleavage = true` under `[accepts]`; none does yet.
- **QC-excluded raw files are recorded for dataRepo again.** The search's `provenance.json` carries `excluded_files`
  (`files` and `reason`), which dataRepo reads to leave a blank or failed injection out of the deposit's runs. Since
  the move to C#, such a file was ingested as a run.
- **dataRepo 1.0.0's JSON result is read.** At batch start PXReprise runs `datarepo --version`. From 1.0.0 on it calls
  `ingest --json`, and a deposit counts as delivered only when the exit code is 0 and its status is `ingested` or
  `unchanged`. The state records the status, bundle id and dataRepo's reasons. An older dataRepo is judged by its exit
  code, as before.
- **`pxreprise validate` checks `[designs]`.** It refuses a `dir` that does not exist and lists the deposits it holds
  a design for.

## 0.3.9 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no search result changes.

- **Each raw file's acquisition start time and instrument are recorded.** `qc_report.json` gains `start_time`,
  `instrument_model`, `instrument_model_accession` and `instrument_serial` for each file. These facts exist only in
  the raw file, which is deleted after the search.
  - Each key is written only when the reader gives the value, and is never an empty string.
  - A Thermo RAW start time is the instrument's local clock with no time zone. It is written as read, never converted.
  - Reading these values never fails a file's QC.
- The release notes now keep nested list items on their own lines.

## 0.3.8 (2026-10-05)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no result changes.

- **A batch survives a reboot or a crash.** Run the same `batch run` again and it finishes what was cut off:
  - **A search cut off part-way is searched again.** Before, its half-written output made the next start mark it
    `search_failed` and never try again. The partial output is moved aside, and the deposit is `search_interrupted`.
    A fourth cut-off on one deposit settles it.
  - **A delivery cut off part-way is delivered again.** Before, an ingest that never finished looked delivered.
  - **A stale `driver.pid` no longer blocks the start.** After a reboot its number can belong to another program;
    the driver's start time now tells them apart.
- **`driver.last.json`** in the state directory records the running batch's command line, working directory, pid and
  start time, so a script or scheduled task can start the batch again after a reboot.
- **`batch run` no longer deletes the STOP file of a batch that is still running.** A second start is refused, as
  before, but it used to remove STOP first.
- **The manifest is never seen half-written.** Each new entry is written to a file beside it, which then replaces it.

## 0.3.7 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. No shipped profile changes, so no result changes.

- **A duplicate of the deposit being searched is now caught.** The batch fetches the next deposit while the current one
  searches, and the duplicate check compared only with deposits whose search had finished. PXD042302 (PXD042301
  deposited again: same title, same paper, 30 of 31 raw files byte-identical) was checked 7 seconds after PXD042301's
  search began, and was fetched and searched in full. The deposit being searched now counts too, and the log says
  `..., being searched now`.
- **Possibly mixed samples are flagged.** When every raw file's name says what kind of sample it is (`Lysate_`, `IP_`,
  `EV_`, `phospho`, ...) and there is more than one kind, the manifest entry gets the flag `possibly_mixed_enrichment`
  and a note, and `batch.log` says `POSSIBLY MIXED SAMPLES`. Which run is which enrichment stays yours to curate; the
  flag does not change the ingest. On the 96 searched aging deposits it flags one, PXD077298 (lysate, IP and
  extracellular vesicles).
- **A state entry's `detail` belongs to its status.** When a later status replaces it (a download that failed once,
  then succeeded), the old text moves to `earlier_detail`. Before, a searched deposit could still show the error of an
  earlier pass.
- **Experimental designs (not used by any shipped profile yet).** A profile can set `[quant] design = "sdrf"`. Before
  the search, PXReprise writes MetaMorpheus's `ExperimentalDesign.tsv` from the question's own SDRF for the deposit
  (`[designs]` in `question.toml`), or else from the deposit's, after mzLib checks it the way MetaMorpheus reads it. A
  question's design that fails the check stops that deposit's search, with the reasons. `pxreprise search` takes
  `--design` and `--condition-columns`. See [Writing a question](https://smith-chem-wisc.github.io/PXReprise/questions.html).
- Every stage's `provenance.json` now says `"definitions": "pxreprise"`, so dataRepo can cite PXReprise's own
  definitions of its numbers (`DEFINITIONS.md`). dataRepo 0.32.0 ignores the field.

## 0.3.6 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: new optional `fetch_stall_minutes`. Safe mid-batch: stop
the batch, update, run it again.

From a first-run report on a slow, unreliable link (one 4.5 GB deposit over two days):

- **A stalled download can no longer hang the batch.** Two downloads sat on frozen `.partial` files for 6 hours with no
  error and no log line. The batch now watches each download's file. When it receives no data for
  `fetch_stall_minutes` (default 5), the download is abandoned and retried, even if the network read does not respond
  to cancellation. It counts as a dropped transfer: retried, then left for a later pass.
- **A file is complete only at PRIDE's listed size.** A file already in `spectra/` at the wrong size (cut short by a
  crash, or downloaded by hand) is deleted and downloaded again, and a download that arrives short is retried. Before,
  any file present by name was taken as complete. Across 2,222 aging downloads, PRIDE's size and the downloaded size
  never differed.
- **Downloads are logged as they happen:** `FETCH 7 of 15 done: <file> 305 MB in 212 s`, and each retry with its error
  type, for example `FETCH retry <file> attempt 2 of 8: TimeoutException: ...`. Before, a fetch was silent for hours.
- **Failures name the exception type** in `batch.log` and in the state entry's `detail`.
- **New `pxreprise batch retry <question.toml> <PXD> --reason "..."`** puts a settled deposit back in the queue, so
  undoing a `fetch_failed` no longer means editing `state.json`. It keeps the old status, time, user and reason, and
  refuses while a batch is running.
- The TLS drop in that report ("Received an unexpected EOF or 0 bytes from the transport stream") was already fixed in
  0.3.3. That run used 0.3.1.

## 0.3.5 (2026-10-04)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again.

- **mzLib 1.0.593.** PXReprise now uses mzLib 1.0.593, one release ahead of the 1.0.592 inside MetaMorpheus 1.1.11.
  That is safe because MetaMorpheus runs as its own process. Searches are unchanged: MetaMorpheus does them with its
  own mzLib.
- What changes for PXReprise: PRIDE now reports every dropped or timed-out download as `HttpRequestException`, the
  error the batch already retries, so dropped transfers no longer depend on PXReprise's own workaround from 0.3.3
  (kept as a safety net). Download error messages name the file and host, never the full URL.
- Every record's `tools.mzlib` now reads `1.0.593+…`.

## 0.3.4 (2026-10-02)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: new optional `dotnet_root` (a private .NET for
MetaMorpheus). Safe mid-batch: stop the batch, update, run it again.

- **TMT and iTRAQ are recognised from the spectra.** Some deposits never mention their labels in the text, so the
  text screen cannot see them (one TMT six-plex was searched as label-free). QC now looks for reporter ions in every
  MS2 and MS3 spectrum. It uses mzLib's reporter masses for TMT, iTRAQ and DiLeu, matched within 3 mDa. A file
  counts as labelled when at least a quarter of its spectra carry three or more reporters of one kit. On real data,
  label-free files show 0 to 0.01% and TMT files 50 to 79%.
- If every file is labelled, the deposit waits for the isobaric profile (`waiting_tmt_dda_1`), as if the text had said
  so. If only some files are, it becomes `deferred_mixed_labelling`: searching only the label-free files is a design
  decision, not one to make automatically. Only a profile that does not take isobaric labels looks for reporters.
- **A search that fails because .NET changed under it is searched again.** An automatic .NET update replaced the
  runtime that a running MetaMorpheus was using, and the search failed 2 h 21 m in. The batch now records the
  installed runtimes before each search. If a search fails and that set has changed, the deposit becomes
  `search_interrupted` and is searched again on a later pass, at most twice. Every search's provenance records the
  runtime it used.
- **Optional `dotnet_root` in the machine file** runs MetaMorpheus on a private copy of .NET that no updater manages,
  through `DOTNET_ROOT`. `machines/example.toml` says how to make one.

## 0.3.3 (2026-10-02)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. Deposits already settled keep their status; the new screens apply to deposits not yet probed.

- **A dropped TLS connection is retried, not fatal.** When EBI closes a connection mid-download, .NET can throw a plain
  `IOException` ("Received an unexpected EOF or 0 bytes from the transport stream") instead of an `HttpIOException`.
  The batch treated it as permanent and dropped the deposit after one failure. It is now retried like any other
  dropped transfer. A local disk error still fails at once.
- **Duplicate deposits are refused before any download.** If every raw file of a deposit (by name and size, from
  PRIDE's file list) is already in a searched deposit, the deposit gets the status `excluded_duplicate`, and its state
  entry's `duplicate_of` names the original. A deposit that shares only some files is searched, and the shared files
  are logged. On the aging corpus, the only duplicate among 78 searched deposits is PXD012985, which repeats PXD011740.
- **Crosslinking (XL-MS) deposits are screened out** as `waiting_crosslinking`. The screen looks for names that mean
  only crosslinking MS (`XL-MS`, `iqPIR`, `DSSO`, `DSBU`, `BS3`, "cross-linking mass spectrometry"). `DSS` and `PIR`
  count only beside a crosslinking word: DSS also names a colitis model, and PIR a protein database. "Crosslinked"
  alone never counts, because hydrogels and ChIP samples are crosslinked too. A profile can opt in with
  `crosslinking = true` under `[accepts]`; none does.
- **18O-labelled deposits are screened out** as `waiting_o18_labelling`, found by `18O`, `O18`, `16O/18O` and similar
  terms.
- Over the 259 queued aging deposits with a PRIDE record, the two new screens flag PXD062841 and PXD028282, the two
  that were searched by mistake, plus two that other screens had already excluded.

## 0.3.2 (2026-09-30)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: yes. Only `census` changes;
a running batch reads the queue it already has.

- **The census takes organisms from PRIDE's project record, not its search index.** PRIDE's search index sometimes
  stores SDRF column headers as metadata terms. For example, `Characteristics[organism]` appears as an organism, and in
  one deposit it was the first organism listed. For every relevant deposit, the census now reads the project record.
  It uses that record's organisms both for the `[discover] organisms` filter and to choose the queue's database.
  The search index is still used to find deposits.
- `census.tsv` gains `organisms_of_record` and `organism_source` (`project`, `search`, or `search_fallback`), and
  `queue.json` entries gain `organism_source`. If PRIDE cannot serve a record, the deposit is kept. It uses the search
  index's organisms with the SDRF headers removed, and it is marked `search_fallback`.
- A census now fetches one project record per relevant deposit, so it takes longer.

## 0.3.1 (2026-09-30)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged. Safe mid-batch: stop the batch, update, run it
again. On that restart, any deposit whose ingest had failed is ingested again.

- **A failed ingest is retried at the next start.** The batch writes a deposit's manifest entry just before it runs
  `datarepo ingest`, and it treated "in the manifest" as delivered. So a deposit that dataRepo refused was never
  ingested again, even after dataRepo was fixed. A deposit now counts as delivered only when its recorded ingest
  succeeded. When the batch starts again, it re-runs ingest and publish from the finished search, so nothing is
  downloaded or searched again.
## 0.3.0 (2026-09-29)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file: recommended new line `datarepo` (the dataRepo download);
new optional `search_timeout_h`, `search_stall_minutes`. Safe mid-batch: stop the batch, update, run it again.

- **The whole pipeline, with no Python.** Each searched deposit now goes into a [dataRepo](https://github.com/smith-chem-wisc/dataRepo)
  repository and a website, using dataRepo's ready-to-run download (0.31.0; Windows, Linux, macOS). `examples/first-run`
  ends with `work/site/index.html`. Getting started, step 3b, installs it.
- **A new question's manifest is created for it,** with roots relative to the manifest; an entry's `run` is taken
  against the manifest's own `work_root`. A `[publish]` command may use `{datarepo}` and `{manifest_dir}`.
- **A busy machine no longer kills searches.** A search's time limit was the profile's `timeout_h` (6 h), so
  MetaMorpheus 1.1.11's long, silent PEP step could be killed on a loaded box while still working. The limits now live
  in the machine file: `search_timeout_h` (default 48 h) is a ceiling, and `search_stall_minutes` (default 90) kills a
  search only when MetaMorpheus has used no CPU for that long, i.e. when it is hung. Profiles' `timeout_h` is read but
  no longer used. Provenance records the limits used, and says which one killed a search.
- **Provenance points at the public repository.** Each stage's `provenance.json` now names
  `https://github.com/smith-chem-wisc/PXReprise` as `pipeline.repo`, instead of a development repository no reader
  could open.
- **The census record is renamed** from `pxreprise-provenance/1` to `pxreprise-run/1`. `pxreprise-provenance/1` is
  reserved for the per-stage record dataRepo ingests.

## 0.2.0 (2026-09-29)

**Updating:** MetaMorpheus unchanged (1.1.11). Machine file unchanged; two optional settings are new. Safe mid-batch:
stop the batch, update, run it again.

- **A PRIDE outage no longer loses deposits.** A deposit whose downloads fail through every retry was settled for good
  as `fetch_failed`. It is now `fetch_unavailable`, not settled. The batch finishes the queue, waits, and tries it
  again, up to three passes in all, before settling it. A checksum mismatch or a missing file still settles at once.
- **New optional machine settings:** `fetch_passes` (default 3) and `pass_wait_minutes` (default 30).
- **A server timeout counts as an outage.** An HTTP timeout from PRIDE used to escape the retry handling; it is now
  retried like any other outage.
- **Docs:** how to update an installation (Getting started, section 7; `AGENTS.md`, "Updating").

## 0.1.0 (2026-09-29)

First public release. The batch, census and question tooling; profiles `label-free-dda@1` and `@2` (MetaMorpheus
1.1.11); the `first-run`, `t2d` and `muscle-ageing` examples; the documentation site and tutorial.
