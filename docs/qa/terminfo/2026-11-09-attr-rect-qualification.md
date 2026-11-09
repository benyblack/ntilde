# Attribute-rectangle qualification: DECCARA, DECRARA and DECSACE (2026-11-09)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`editing.deccara`, `editing.decrara` and `editing.decsace`. Selective erasure, fill/copy/erase rectangles, rectangle checksums, SGR and platform
qualification are different cases and are not substituted.

## Pinned definitions and verdict semantics

Only the termless paths carry a verdict; the pinned `term` paths of DECCARA/DECRARA end in a cursor-position query (`unmeasuredCellResult`) and DECSACE's is a not-attempted placeholder, so none of them is qualified.

| Id | Pinned fixture bytes | Pinned verdict possible |
|---|---|---|
| `editing.deccara` | `ESC[1;1HaaaaaZ ESC[2;1HbbbbbY ESC[3;1HcccccX`, `ESC[1;1H`, `ESC[1;1;3;5;7$r` (selector 7, no extent selected) | control: every character unchanged **and column 6 not inverse**, else inconclusive; supported iff columns 1..5 of all three rows are inverse |
| `editing.decrara` | the same shape with `ESC[7m aaaaa ESC[0m Z` per row, `ESC[1;1H`, `ESC[1;1;3;5;7$t` | same control; supported iff columns 1..5 are no longer inverse |
| `editing.decsace` | `ESC[1;1H ESC[2*x`, then reads the text | **always inconclusive** ("Sequence consumption does not measure attribute-change extent"); never reads a cell or applies DECCARA/DECRARA |

## The default extent (primary protocol)

The VT510 reference (DECSACE, DECCARA, DECRARA) makes the default extent the **stream**: Ps 0 or 1 select the stream from the first named position to the second, Ps 2 the
rectangle; DECCARA/DECRARA are not affected by page margins, DECOM offsets the coordinates, they change no cursor, no SGR pen and no character. With Pt=1, Pl=1, Pb=3, Pr=5 the stream
covers row 1 through its last occupied column (the `Z`), all of row 2 and row 3 through column 5. A conforming terminal therefore changes the right-flank cells the pinned control requires to
stay untouched: **the pinned DECCARA and DECRARA verdicts are inconclusive by the pinned code's own control, and are preserved, never promoted.** The effect contract grades what the terminal does
against the definition. The same pinned code over the same pinned bytes with the rectangle extent selected first (`ESC[2*x`, restored with `ESC[0*x`) is a **labeled experiment**, recorded beside the
pinned result and never a pinned grade. `editing.decsace` is inconclusive by design; its effect contract (stream versus rectangle cells across the first, intermediate and last rows) is separately identified.
Implementing the rectangle as the default would make the pinned probes pass but contradict the reference, so it was not done.

## Implementation (existing versus new)

Before this batch DECCARA (`$ r`), DECRARA (`$ t`) and DECSACE (`* x`) were unhandled (`CSI $ r` was kept distinct from DECSTBM by the hardening sweep). New: exact-identity parsing (no leader, no subparameters),
page coordinates with defaults/clamping/DECOM row offset (margins never clip), a stream/rectangle extent kept as stream state (RIS restores the stream, DECSTR keeps the selection, state transfer carries it, independent of
margins, origin, protection and the pen), and attribute changes on bold, underline, blink and inverse only. Operands: DECCARA 0, 1, 4, 5, 7, 22, 24, 25, 27 (set or clear, in order; 0 or an omitted list clears the four);
DECRARA 0, 1, 4, 5, 7 (toggle; 0 or omitted toggles the four; a repeated selector cancels; 22/24/25/27 ignored). **Documented as unsupported:** xterm's 8/28 extension and every other selector (ignored with the rest of the list still
applied), DECRQSS for the extent, protected-cell storage (DECSCA), OSC 66 block atomicity. Clearing underline drops its style and color but keeps overline; a wide glyph changes as one unit; text, hyperlinks, extended text,
colors, the cursor and the pen are untouched. In the stream extent cells past a row's written extent are left alone, while the rectangle changes and occupies them (the DEC STD 070 note xterm cites; it is not stated by the VT510 pages and is observed, not graded).

## Method

This is the twentieth digest-checked extension (derived over the erase-attrs/region one), contracts v1, suite `ntilde-owned-attr-rect-graded-v1`, prefix `atrc`, nonce `NTILDE_AR_`.
Every group is a separate parent-journaled read on a measured reset screen; the pinned probe runs over the journaled screens, the independent oracle grades the effect contract, and the parent re-executes the pinned code.
The pinned groups (seed, cursor, edited) carry the exact pinned bytes at the genuine default extent; every rectangle control ends by restoring `ESC[0*x`, and each case ends with a stream restoration witness. **The DECSACE setting is not readable
(no API reports it), so it is inferred from the cells a DECCARA/DECRARA changes: that unobserved-state limit is stated, and a failed witness is inconclusive.**

Controls (required unless labeled): the pinned fixture under the stream and the rectangle; a plain 4x6 text seed with inclusive-bounds rectangle (rows 2..3, columns 2..5); set versus replace (a mixed pre-attributed row keeps bold, italic, colors, underline,
blink and inverse not named); selectors 22;24 and 0 clearing only what they name while italic and the color survive; unknown selectors (99, 8) ignored around defined ones; a pen/cursor control (the pen is bold on a blue background, a character written after the operation must carry
it and land where the cursor was); origin mode with margins 2..4 (rows offset by the top margin, the bottom margin does not clip); DECRARA toggle with a second-application restoration and the operand table (selector 1, 0, omitted, 22/24/25/27, 1;1, 4;5 over cells that also carry italic and a color, so a
blind XOR of internal flags fails); DECSACE 0/1/omitted/2 on a four-row eight-column fixture across first, intermediate and last rows for both DECCARA and DECRARA; independence from margins, origin mode, cursor and pen; an undefined value (3) after the rectangle is **observed, not graded**.
Unsupported needs valid controls and a measured mismatch; a missing or invalid seed, cursor, geometry, DECSCNM, blank screen or unrestored extent is inconclusive.

## Measurement

Scripts `9ee16b381337241c2fa2575eae8c63b76a50f932` (committed source: the merge of main with the erase-attrs/region and DECFRA/DECCRA changes, plus the two Codex-review VT fixes); App source revision `9ee16b381337241c2fa2575eae8c63b76a50f932` (the Release sidecar `~/.ntilde-qa-sidecars/attr-rect`, built from this checkout; App and scripts revisions are the same commit because this batch changes
App source). Owned run `6af895fbcdedae19c5682fee92717d57`, receipt schema 33, source policy v27 (inventory v34, 214 files), default theme, 40x140, taken through the shared desktop lock.

| Case | Pinned (parent replay + live pinned code) | Effect contract | Divergence | Rectangle experiment (labeled) |
|---|---|---|---|---|
| `editing.deccara` | inconclusive (flank control) | supported | `pinned-fixture-assumes-rectangle-extent` | pinned code supported |
| `editing.decrara` | inconclusive (flank control) | supported | `pinned-fixture-assumes-rectangle-extent` | pinned code supported |
| `editing.decsace` | inconclusive (by design) | supported | `pinned-inconclusive-by-design` | n/a |

Counts: conclusions 3 inconclusive (all pinned verdicts preserved); effect contracts 3 supported (kept separate). No inconclusive pinned result was promoted.

Observed (stored cells, 40x140): the pinned DECCARA at the default extent marked `IIIIII / IIIIII / IIIII.` (rows 1 and 2 through the flank, row 3 through column 5), text and cursor (0,0) unchanged; after `ESC[2*x` it marked
`IIIII. ×3`. DECRARA at the default left the flank `.....I / .....I / ......`; the rectangle cleared exactly columns 1..5. The four-row fixture gave `..IIIIII / IIIIIIII / IIIIIIII / IIIIII..` for the stream (default, 0, 1, omitted, and the final witness)
and `..IIII..` on every row for the rectangle, for both DECCARA and DECRARA. Under DECOM with margins 2..4 the rectangle covered absolute rows 3..6 (offset by the top margin, past the bottom margin). The character written after DECCARA carried the unchanged pen (bold) and landed at the
pre-operation cursor. An undefined DECSACE value left the rectangle in force (observed).

**Preserved attempts** (all in the summary): (1) `e9933919…` (scripts `35380b41`, receipt 32, policy v26), complete and graded identically, superseded because main merged the erase-attrs/region layer (PR #909) and the DECFRA/DECCRA VT change, so this layer was restacked as the twentieth extension and the suite re-measured; (2) `7865160b…` (scripts `2e3fdacb`, receipt 33, policy v27), complete and graded identically, superseded because Codex review found two VT defects (a rectangle that changed no attribute did not occupy its cells; ordered selectors such as `24;4` kept the underline style and color), so the VT was fixed and the suite re-measured; (3) the run above. **Two later relaunches on head `2bd4b0c0`** (the legacy-replay-checkpoint fix found by Codex, which touches no measured path: it changes `ReplayModels`, snapshot apply and `PtyRecorder` only) aborted before any observation (Avalonia.Native could not start the RenderTimer, error -6661, while the desktop session was idle and locked); they are preserved in the summary and are not evidence. The published measurement is therefore on `9ee16b38`; the head differs from it only by that replay-checkpoint change, and a repeat measurement on the final head is an open item for an unlocked desktop. CodeRabbit explicitly reported rate limiting on PR #911; the final-head Codex review is the review basis.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (inventory derived from the merged contracts; saved-reset keeps its pinned-inconclusive cases with supported effect contracts), 10/10 truecolor-literal finding, 3/3 App-observed
underline-color resolution. **The stock collector was not re-run** (it needs an interactive Ntilde tab): the pinned bundle and probeHash are unchanged and the three pinned `term` paths never carry a verdict (cursor query, cursor query, not-attempted), so the carried counts
(168 inconclusive, 86 supported, 1 unsupported, baseline run `02be5c60…`) are labeled as carried.

## Limits

Stored state only: no resolved-color, rendered-appearance or pixel claim; the extent setting is inferred, not read; protected cells, wide-cell atomicity (unit-tested only), OSC 66 blocks, xterm's 8/28 selectors and DECRQSS for the extent are not claimed. macOS owned sidecar, default theme.

## Evidence

[`2026-11-09-attr-rect/`](2026-11-09-attr-rect/): summary, report header, one file per case, run log, common frames and one file per read frame.
`scripts/terminfo/attr-rect-publication.test.mjs` re-derives the report from the retained journal.
