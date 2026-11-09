# Rectangle-editing qualification: DECFRA, DECERA, DECCRA (2026-11-09)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `editing.decfra`, `editing.decera` and
`editing.deccra`. `editing.decsera` (selective erasure), `editing.deccara` and `editing.decrara` (attribute rectangles), DECSACE, rectangle checksums, Unicode and
wide-cell editing, the inline-image plane, rendered appearance and any platform other than the macOS owned sidecar are different cases and are not substituted.
The implementation under test is the stored-text DECFRA/DECCRA of [PR 908](rectangle-copy-fill.md), as amended by PR 907 (character protection: filled cells take the live DECSCA pen, a copy carries protection); DECERA is the bounded stored-text implementation of
[PR 875](decera-text-erase.md), unchanged.

## Pinned definitions

All three termless probes read the geometry, feed **one seed string**, read the cells, feed `ESC[1;1H`, read the cursor (must be x 0, y 0), feed the edit and read the
cells again. A seed that did not read back, or a wrong setup cursor, is inconclusive in every case.

| Id | Needs | Seed (one feed) | Edit | Supported iff |
|---|---|---|---|---|
| `editing.decfra` | 3 rows, 6 columns | `ESC[1;1HaaaaaZ ESC[2;1HbbbbbY ESC[3;1HcccccW` | `ESC[88;1;1;3;5$x` | column 6 of every row unchanged and columns 1-5 read `XXXXX` |
| `editing.decera` | 3 rows, 6 columns | `aaaaaZ / bbbbbY / cccccX` | `ESC[1;1;3;5$z` | column 6 unchanged and columns 1-5 blank (`""` or `" "`) |
| `editing.deccra` | 6 rows, 14 columns | six 14-column rows `ABCDE.........`, `FGHIJ.........`, two dot rows, `.........12345`, `.........67890` | `ESC[1;1;2;5;1;5;10$v` | rows 1-4 and columns 1-9 of rows 5-6 unchanged; row 5 and row 6 columns 10-14 read `ABCDE` and `FGHIJ` |

The pinned probes never read the cursor after the edit, any cell outside the seeded block, attributes, margins, origin mode or the pen. The terminal pixel path of every
registration (cursor position report, then `unmeasuredCellResult`) never carries a verdict; the derivation pins all three registrations verbatim with their terminal paths and the
helper bodies, so an upstream change that gave one a verdict would be refused. In the stock run all three cases stay `inconclusive` (`insufficient-evidence`, evidence `query`).

## Method

Twenty-fourth digest-checked collector extension `ntilde-owned-rectangle-editing-grading-v1` (derived over the selective-erasure one, `derivedFromAdapterId` `ntilde-owned-selective-erase-grading-v1`, which is itself layered over the erase-character/saved-lines, cursor, attribute-rectangle and erase attributes/margins ones), contracts v1, suite
`ntilde-owned-rectangle-editing-graded-v1`, prefix `rect`, nonce `NTILDE_RC_`, minimum geometry 12x16 (the measured screen is 40x140). The pinned bytes are fed byte-exactly, one write each, on a
measured reset screen; the pinned probe runs over the parent-journaled screens and the parent re-executes it. The independent contract grader compares every cell of the whole
screen with a rectangle model written for this suite and never grades more permissively than the pinned probe (recorded divergences: none).

Every case journals the pinned groups (initial blank screen, the pinned seed, the cursor setup, the edit) and then one fenced write per labeled control, each preceded by a measured
seed read: a seven-row block of distinct characters (`blockBefore`) and an attributed seed (`attrBefore`: plain, bold, red-on-blue, plain). A control's write begins by clearing
margins, origin mode, insert mode, SGR and DECSCA protection, so no state leaks between groups. Margins, origin mode and insert mode are read from the App's stored buffer state in every read;
a read whose state differs from the contracted setup is inconclusive.

- **DECFRA** (17 reads): single cell, inclusive borders, omitted and zero borders (the whole page), oversized borders (clipped), a combined no-op group (character codes 31, 127, 0 and 256, reversed borders, an extra parameter), the rectangle under DECOM, across scroll margins (margins never clip), with insert mode on, and the live pen: the filled cells carry the stored tuple of a reference cell printed afterwards under the same pen (so the pen, the cursor and the fill's attributes are all checked), over attributed cells that outside the rectangle keep their tuples, and under a literal-RGB pen.
- **DECERA** (16 reads): the same bounds, default, clip, no-op (reversed borders, extra parameter), origin and margin controls; erased cells read blank with no stored attribute but a background (the pen background fill is observed, not required); unrelated attributed cells keep their tuples; and ordinary erasure over DECSCA-marked text, which stays erased. DECERA is the unconditional erase; DECSERA is a different case and is not exercised.
- **DECCRA** (20 reads): a non-overlapping copy with the source intact; overlap right, left, down, up and diagonal (an incremental copy smears and is graded as a mismatch); the page copied onto itself and zero defaults; a destination origin clamped to the last row and to the last column (clipping); a no-op group (reversed source borders, an extra parameter); origin and margin controls; attributed cells arriving with their stored tuples while a different pen stays active and is preserved.

**Unsupported pages are an Ntilde policy, not a criterion.** DECCRA's `Pps` and `Ppd` name pages Ntilde does not have. Ntilde accepts 0 and 1 and ignores the command for any larger page rather than treating it as the
active page (the DEC page says a page past the arrangement means the last page). The page control is therefore recorded and observed, never graded; no other terminal is judged against it.

Unsupported needs valid controls and an observed mismatch. An invalid or missing seed, reverse video not proven off, a geometry below the contract minimum, a stale palette binding, contradictory stored projections,
missing buffer state, a margin or mode the App did not confirm, an unmeasured pen reference or incomplete restoration is inconclusive or refused, never conclusive.

## Measurement

Scripts (adapter) revision `3f43fa0ac0cc50ba50dab1c18463c599f4a71aaa`; App source revision `4a2c55ce305c2e3aa91a484ecb169c5aed018f57` (the last commit that touched `src/`, the agent-host protection flag of PR 915; PR 907's DECSCA protection (filled cells take the live DECSCA pen, a copy carries protection), PR 908's DECFRA/DECCRA, PR 913's reverse-wraparound and the attribute-rectangle work are inside it); the Release sidecar
was built from this merged checkout with the repository wrappers. Owned run `fe38a66e3836aa36359af7487a413029`, receipt schema 37, source policy v31 (inventory v38, 236 files), default theme, 40x140, App version
0.13.0 (operator supplied; not independently verified), disposable sidecar `artifacts/sidecar-rect-edit-24`, stock collector run `ff25a08119a7dbb18679d113514f303e`, measured 2026-11-09T21:26Z on macOS with Node 24.
One owned collection was started on the final scripts and it completed; the earlier attempts are listed under Preserved attempts.

| Case | Pinned (parent replay + live pinned code) | Contract | Stock grade |
|---|---|---|---|
| `editing.decfra` | supported | supported | inconclusive (`query`) |
| `editing.decera` | supported | supported | inconclusive (`query`) |
| `editing.deccra` | supported | supported | inconclusive (`query`) |

**DECFRA.** The pinned 3x5 area read `XXXXX` on every row with `Z`, `Y`, `W` untouched and the cursor still at (0,0). All nine controls matched the model over the whole 40x140 screen:
omitted and zero borders filled all 5600 cells, the clipped fill stopped at the page edge, the combined no-op group left all 70 seed cells unchanged, DECOM added the top margin to the rows, the scroll margins did not clip the
rectangle, and insert mode did not shift anything. Filled cells carried the stored tuple of a reference cell printed under the same pen (italic, palette 3 on palette 2), so the pen was neither lost nor drifted, and the cursor ended where it was
parked plus the reference character. The literal-RGB pen reproduced the same way.

**DECERA.** The pinned area read blank (only `Z`, `Y`, `X` remain) with the cursor unchanged. Omitted and zero borders blanked the whole page (no ink), the no-op group changed nothing, origin and margin controls matched the model,
and the DECSCA-marked seed was erased like any other. Erased cells stored no attribute other than a background; observed (not required) that they carried the pen's background color, and that cells outside the rectangle kept their stored tuples.

**DECCRA.** The pinned 2x5 source was copied to row 5, column 10 with the source intact. All five overlap controls matched the read-first model (none smeared), the page-onto-itself and zero-default copies changed nothing,
the clamped destination row copied only the first source row to the last row (`ABC`), the clamped destination column copied only the first source column to the last column (`A`, `K`), and the no-op group (reversed borders, extra parameter)
changed nothing. Attributed cells arrived with their stored tuples while the different pen stayed active. **Observed, not graded:** both page-2 spellings (source and destination) were ignored.

**Preserved attempts:** (1) run `faee7daf7e602fd9ce7e837f1b174a74` (scripts `e39a53f7`, App source `5210c58d`, receipt 33, policy v27, stock run `6340ec6fd8ba4522704e4374b2f02745`, sidecar `artifacts/sidecar-rect-edit`), complete, all three cases supported, as the twentieth
extension; superseded because the attribute-rectangle, reverse-wrap and cursor work merged first and the layer was restacked on the new base, so that measurement is not reused; (2) run
`3412e154c7ecf4aa22c2ab57c8f3b00c` (policy v29, receipt 35, as the twenty-second extension over the cursor layer), complete, all three cases supported, discarded because #910 (erase-character/saved-lines)
merged on the same policy and schema and changed the scripts and the collector chain before this branch merged, so the layer was restacked as the twenty-third extension and measured again;
(3) run `51e52a34af07a480d49b4791e5f37b91` (scripts `042587c2`), aborted during cell collection before any suite with the collector refusal `Unexpected input during cell collection`
(retained under `artifacts/terminfo/owned-9Na9v7`, no evidence used); the cause is not understood (the shared desktop lock was held, the App log is empty), and it was retried once with identical inputs;
(4) run `bf9252644d2532d7f0d603fc0c5bec73` (scripts `042587c2`, App source `359417dd`, sidecar `artifacts/sidecar-rect-edit-22`, stock run `ae997cb82d2fd95be0ee28461ee9bf9d`), the retry of (3), complete, all three cases supported (stock inconclusive, query), superseded
because #907 (DECSCA protection) then changed `src/`, including the DECFRA/DECCRA files, and the scripts were refactored for SonarCloud, so the measurement was repeated on the merged tree and the final scripts. (5) run `b057258dc450021de905ce44d00e4ae3` (scripts `0f9c1c28`, App source `80425d73`, sidecar `artifacts/sidecar-rect-edit-23`, stock run `49958a9f34000d2f3742ae12d7319034`), the first collection on the merged tree after the Sonar refactor, complete, all three cases supported (stock inconclusive, query), superseded
because the dispatch predicate and fixture were then changed again after the Sonar re-review and #915 (selective erasure) merged on the same policy and schema, so the layer was restacked as the twenty-fourth extension and measured again. Only (3), (4), (5) and the published run are recorded in `summary.json` `attempts`; (1) and (2) are recorded here because
they were measured on bases that no longer exist.

The consolidated inventory's reviewability cap on the generated JSON was first raised on this branch from 160 KiB to 176 KiB (the 77-id inventory was about 164 KB), and then superseded: #915 landed a larger LF-measured cap (192 KiB, `qualification-inventory.test.mjs`), so this branch no longer changes that test and the current 79-id inventory fits under main's cap.

## Boundaries

Stored ASCII state only: no Unicode, wide-cell, scaled-text, image-plane, protected-cell, rendered-appearance or platform claim. Checksums are not graded here; DECSERA is graded by the selective-erasure batch and DECCARA, DECRARA and DECSACE by the attribute-rectangle batch. History is observed only
(the reads run on the alternate screen). The pages behavior is an Ntilde policy. The pinned terminal paths never carry a verdict and are not qualified; stock grades are unchanged.
