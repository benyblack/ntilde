# Scrolling and history qualification: nine pinned scrollback cases (2026-11-09)

Qualifies exactly nine pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`scrollback.accumulate`, `scrollback.total-lines`, `scrollback.scroll-up`, `scrollback.reverse-index`, `scrollback.scroll-down`,
`scrollback.set-region`, `scrollback.alt-screen`, `scrollback.decstbm` and `scrollback.decstbm-reset`. Resize/reflow, Unicode or
wide-cell scrolling, other scrolling operations and the Ntilde-owned `history.*` suite are different work and are not substituted.

## Pinned definitions

| Id | Pinned bytes (one feed each) | Pinned termless verdict |
|---|---|---|
| `accumulate` | `line N\r\n` for N in 0..rows+9 | supported iff measured `totalLines` grows |
| `total-lines` | the same bytes | supported iff measured `totalLines` grows |
| `scroll-up` | `ESC[2J`, A..E on rows 1-5, `ESC[2;4r`, `ESC[S`, `ESC[r` | needs empty history first; supported iff rows read A,C,D,blank,E with history unchanged |
| `reverse-index` | `A\r\nB\r\nC`, `ESC[H ESC M` | supported iff cell (0,0) was A and is now blank |
| `scroll-down` | `LINE1\r\nLINE2\r\nLINE3`, `ESC[T` | supported iff cell (0,0) was L and is now blank |
| `set-region` | `ESC[5;10r`, `ESC[r` | **always inconclusive**: reads only the cursor |
| `alt-screen` | `NORMAL`, `ESC[?1049h` | **always inconclusive**: reads only the mode flag |
| `decstbm` | `FIXED_TOP\r\n`, `ESC[3;10r`, `ESC[3;1HINNER`, `ESC[10;1HZ\r\n`, `ESC[r` | supported iff INNER scrolled away and FIXED_TOP stayed |
| `decstbm-reset` | `ESC[5;10r`, `ESC[r`, `ESC[H`, then rows+10 lines `line-N\r\n` | **always inconclusive**: growth does not prove the region was active |

The pinned `totalLines` is screen rows plus retained history lines. The three null probes say so in their own notes; stock grades stay
inconclusive for all nine (stock counts unchanged: 86 supported, 1 unsupported, 168 inconclusive).

## Method

Seventeenth digest-checked collector extension `ntilde-owned-scrollback-grading-v1` (derived over the charsets one; it refuses
derivation unless the verbatim pinned scrollback section, pinned by one SHA-256, and the `parserStateResult`/`isBlank` helpers are present
once), contracts v1, suite `ntilde-owned-scrollback-graded-v1`, prefix `scrl`, nonce `NTILDE_SB_`. The suite runs on the **main screen**
(history is only observable there) after the charsets suite, starting from a pristine main screen and restoring it exactly. Each plan
begins with a measured reset (`?1049l`, DECSCNM/DECOM/margins/IRM/SGR off, `2J`, `3J`, home), feeds the pinned bytes one write each, and
journals complete state reads: viewport text, **actual `readScrollback` lines and count**, margins, active screen, origin/insert/auto-wrap
and pending wrap, cursor, and that no cell stores a non-default attribute (attribute runs and stored details must agree). Every expected
state comes from an independent byte-interpreting model (`scrollback-model.mjs`) run over the fed bytes, which refuses any unmodeled
sequence. The pinned code runs live over the journaled states and the parent re-executes a port; the contract grader must agree wherever
the pinned probe decides. For the three null probes the contract supplies the evidence their notes say is missing and the report records
`contract-measures-beyond-pinned`.

Controls (each a separate journaled read): empty-history baseline and the state just before the first overflow; region-confined SU/SD/RI
with every row outside the region (`ROW-N` markers) and the history intact; wrong-direction, no-op and whole-screen alternatives (labeled
diagnoses); RI below the top margin (cursor only); full-screen SD discarding the bottom row without history; margins observed after the
set and after the reset; confinement and a following full-screen scroll that does enter history; an unreset region (`decstbm-reset` control:
history stays empty, rows 1-4 fixed); a seeded three-line main history before the alternate screen, alternate-screen overflow that must not
reach it, and exact main restoration. Unsupported needs valid controls and an observed mismatch; a missing control, stale history or wrong
geometry is inconclusive.

## Measurement

Owned run on a disposable sidecar built from this checkout, receipt schema 30, source policy v24 (inventory v31, 199 files), default theme,
40x140. All nine cases graded **supported** by the contract. Six pinned verdicts agree (supported); three are inconclusive by construction.
Observed: 50 lines overflow into exactly lines 0-10 of history (total lines 40 to 51) with `line 11`..`line 49` in rows 0-38; SU confined to
rows 2-4 gave A,C,D,blank,E with no history; RI/SD inserted a blank top row; DECSTBM `5;10` observed as margins 4..9 (0-based) and restored to
0..39 by the reset; `decstbm` kept FIXED_TOP and every row outside 3..10 with no history; the reset-plus-overflow fixture put exactly
`line-0..10` in history; an unreset region left history empty. **Alternate screen:** the App reports the main history (3 lines) while the
alternate screen is active and homes the cursor on entry (observed, not required); alternate overflow added nothing to it and exit restored
the main viewport, cursor `(39,6)` and history exactly.

**Preserved attempts:** (1) `289de99d…` (scripts `152509f3`): the first measurement over main `4e32f6d9` (receipt 26, policy v20, 179 files), superseded when main merged the character-editing layer; (2) the post-integration run over that layer (receipt 27, policy v21, 184 files, scripts `57efb32e`), superseded when main merged the erase-in-display layer; (3) the post-integration run over that layer (receipt 28, policy v22, 189 files), superseded when main merged the line-editing layer; (4) the post-integration run over that layer (receipt 29, policy v23, 194 files), superseded when Codex review found the geometry guard did not bound the column count (an evidence-validation change), so the guard was corrected and the run repeated on the same base. This layer was restacked each time (now the seventeenth extension, receipt 30, policy v24, 199 files) and re-measured; all five runs graded all nine supported. The final run measured the scripts of the commit recorded in the summary; App source is unchanged since `c16997c2`.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (inventory derived from the merged contracts
by the publication test); 10/10 truecolor-literal finding; 3/3 underline-color resolution.

## Limits

ASCII fixtures at 40x140 on the macOS owned sidecar and default theme; stored state only. No wrapped-line, reflow, resize, Unicode,
wide-cell or pixel claim.

## Evidence

[`2026-11-09-scrollback/`](2026-11-09-scrollback/): summary, report and run log (header plus one file per case), common frames and one file
per read frame. `scripts/terminfo/scrollback-publication.test.mjs` re-derives the report from the retained journal.
