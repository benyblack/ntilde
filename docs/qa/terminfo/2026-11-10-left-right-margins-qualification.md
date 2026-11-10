# Left/right-margin qualification: DECLRMM and DECSLRM (2026-11-10)

Qualifies one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `modes.left-right-margin`. The implementation under
test is the DECLRMM/DECSLRM of PR 925 ([contract](left-right-margins.md)); before it `?69` was untracked and `CSI s` was always SCOSC. CPR, wide glyphs, side
tables, the image plane, rendered appearance and any platform other than the macOS owned sidecar are not qualified here.

## Pinned definition: inconclusive by design

The termless probe feeds `ESC[?69h`, reads the mode metadata (`getMode("leftRightMargin")`), feeds `ESC[?69l` and returns
`parserStateResult(null, …)`: inconclusive whatever it read ("Mode metadata does not measure margins"). The terminal path returns inconclusive without touching
the terminal. The pinned verdict is therefore preserved as the conclusion. The derivation pins the registration and the helper verbatim, so an upstream change
that gave either path a verdict would be refused.

## Effect contract

The separately identified effect contract `ntilde-left-right-margins-effect-v1` grades the margins live, from DEC's DECLRMM/DECSLRM as xterm implements them
([contract](left-right-margins.md)). An independent margins model written for this suite interprets each control's operations over a measured block seed (ten
characters at the left edge and ten at the right edge of seven rows) and must reproduce the App's whole screen, the cursor, and the App-stored region, left/right
margins, DECLRMM, origin and insert mode after every control. Before measurement the model was run against the VT implementation on every control's exact bytes
and agreed on all of them.

## Method

Twenty-seventh digest-checked collector extension `ntilde-owned-left-right-margins-grading-v1` (derived over the rectangle-checksum one), contracts v1, suite
`ntilde-owned-left-right-margins-graded-v1`, prefix `lrmm`, nonce `NTILDE_LM_`, minimum geometry 13x48 (the measured screen is 40x140). The pinned bytes are fed
byte-exactly on a measured reset screen, and the pinned code runs over the App's journaled mode (the parent re-executes it). The shared observer keeps the App's
stored horizontal margins and mode only for this suite (`includeHorizontalMargins`). Sixteen controls, each one fenced write (clear, block seed, operations) and
one read; 20 reads and 23 captures in all:

- **mode:** `CSI 5;14 s` without DECLRMM saves the cursor and sets no margins; DECSLRM 5;14 homes the cursor; an equal and an inverted pair are ignored;
  DECSTR restores full width and keeps DECLRMM; resetting `?69` restores full width.
- **printing:** text wraps at the right margin back to the left margin; insert mode pushes text only to the right margin.
- **movement:** CUF/CUB stop at the margins from inside and at the page edges from outside; CR returns to the left margin from inside and to the first column
  from outside; HT from column 11 stops at the right margin (13) instead of the tab stop at 16.
- **scrolling:** LF at the bottom margin scrolls only the band between the margins and, from outside them, neither scrolls nor moves; RI at the top margin;
  SU/SD wherever the cursor is; IL/DL; ICH/DCH, which do nothing outside the margins.
- **columns:** DECIC/DECDC/SL/SR move only the columns between the margins.
- **origin:** DECOM homes to the top-left margin corner, CUP columns count from the left margin and clamp to the right margin, and a DECERA rectangle's columns
  are offset by the left margin.

## Measurement

Scripts (adapter) revision `9d309a2e` (full SHA in `summary.json`); App source revision `c8fd98de` (the last commit that touched `src/`: PR 925 with its review
fixes; main `dba5fa56` has the same tree); the Release sidecar was built from that tree with the repository wrappers. Owned run
`3694bbec88a9170324cc2a238a7e36bf`, receipt schema 40, source policy v34 (inventory v41, 253 files), default theme, 40x140, App version 0.13.0 (operator
supplied; not independently verified), disposable sidecar `artifacts/sidecar-declrmm`, stock collector run `e48a6d34bf450e845c9a4bbcf8e2ae47`, measured
2026-11-10T20:44Z on macOS with Node 24, under the shared desktop-collection lock.

| Case | Pinned (parent replay + live pinned code) | Effect contract | Stock grade |
|---|---|---|---|
| `modes.left-right-margin` | inconclusive (by design; the App reported DECLRMM set) | supported | inconclusive (`none`) |

Every control matched the model: the margins read 4-13 after DECSLRM with the cursor home; SCOSC restored row 2, column 3 without DECLRMM; the refused pairs
left 4-13; text wrapped from column 13 to column 4; HT stopped at 13; the band scrolls moved only columns 4-13 and the outside-margin LF changed nothing;
DECSTR and the `?69` reset restored 0-139.

**Predecessors and findings.** Every merged predecessor suite (twenty-five, including rectangle checksum) and both finding suites reconfirm in the same run with
the same outcomes as their own publications; the exact-query suite is unchanged (33 matched, 7 mismatched); the receipt re-derives all of them.

**Attempts:** the published run is the only attempt.

## Layout

`summary.json`, `run-log.json`, `journal-common.json`, `case-modes.left-right-margin.json` and one `journal-modes.left-right-margin-read<n>.json` per read frame.
`lr-margins-publication.test.mjs` reassembles the journal and re-derives the report byte for byte.

## Boundaries

Stored ASCII cells, the cursor and the stored margins and modes only. CPR, wide glyphs and side tables in a band scroll, the image plane, DECRQSS for the margins,
DECBI/DECFI and ED/EL/ECH bounding are not claimed. The pinned paths never carry a verdict; the stock grade is unchanged.
