# Erase with attributes and erase with margins qualification: EL over a background, ED under a scroll region (2026-11-09)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`erase.el-with-attrs` and `erase.ed-scroll-region`. `erase.line.*` (EL 0/1/2) and `erase.screen.*` (ED 0/1/2) are already qualified and are
not re-graded; `erase.character` (ECH), `erase.screen.scrollback` (CSI 3J), selective erasure (`erase.selective`, DECSED/DECSEL), rectangles,
Unicode editing, rendered appearance and any platform other than the macOS owned sidecar are different cases and are not substituted.

## Pinned definitions

Both termless probes feed a fixed string, read stored cells, feed **one erase with the omitted parameter** (`ESC[K`, `ESC[J`), read again, and end in a
`finally` feed. Neither depends on the measured geometry for its bytes.

| Id | Setup (one feed) | Erase | `finally` | What the pinned verdict reads |
|---|---|---|---|---|
| `erase.el-with-attrs` | `\x1b[42m\x1b[1;1HXXXXX\x1b[1;1H` | `\x1b[K` | `\x1b[0m` | cell (0,0) twice. Inconclusive if the X is not `X` with a non-null background ("Colored X setup was not measured") or the erased cell has no background ("Erased cell background metadata unavailable"); otherwise **supported iff the erased cell is blank and its resolved background r/g/b equals the X's** |
| `erase.ed-scroll-region` | `\x1b[1;1HKEEP!\x1b[3;1HERASE\x1b[3;10r\x1b[3;1H` | `\x1b[J` | `\x1b[r` | needs a measured 10x10 screen; rows 0 and 2 (first five cells) and the cursor before the erase. Inconclusive if the seed or the cursor (x 0, y 2) is wrong ("Erase region seed or cursor setup failed") or row 0 no longer reads `KEEP!` ("Preceding row or cell metadata was not preserved"); otherwise **supported iff the five target cells are blank** |

Neither probe reads the cursor after the erase, any cell past column 4, the storage representation, the margins or any mode. The terminal paths of both
registrations (cursor position report, then `unmeasuredCellResult`) never carry a verdict: the derivation pins both registrations verbatim, with their terminal
paths and the helper bodies, so an upstream change that gave a terminal path a verdict would be refused. In the stock run both cases stay `inconclusive`
(`insufficient-evidence`, evidence `query`). Stored-state qualification **does not establish rendered appearance**.

**Spellings.** Both fixtures use the omitted parameter. The explicit-0 spelling (`ESC[0K`, `ESC[0J`) runs as a separate, labeled observation, never a grade.

**Resolved color is not stored color.** The pinned criterion compares the cell's exposed resolved `{r,g,b}`. The contract therefore requires resolved-RGB
equality for cell (0,0) and only observes the stored representation (palette index versus literal ARGB), the rest of the erased row and which pen the fill
follows. No requirement that an erased cell keeps any other previous style is invented.

**ED is not assumed to be clipped to the margins.** The pinned criterion only needs the preceding row and the target cells. A full-screen marker fill under the
same margins and cursor records where ED stopped as a labeled observation (no standards claim, no grade).

## Method

Nineteenth digest-checked collector extension `ntilde-owned-erase-attrs-region-grading-v1` (derived over the saved-state/reset one; it refuses derivation unless both
registrations and the `isBlank`, `rowCells`, `parserStateResult` and `unmeasuredCellResult` bodies are present verbatim once), contracts v1, suite
`ntilde-owned-erase-attrs-region-graded-v1`, prefix `erar`, nonce `NTILDE_EA_`, geometry 40x140 (any other geometry is inconclusive). The pinned bytes are fed
byte-exactly, one write each, on a measured reset screen; the pinned probe runs over the parent-journaled screens and the parent re-executes it. The pinned code
may take three feed paths (full fixture, the early return that still runs `finally`, or no feed on a screen below the floor); the recording context accepts exactly those.
The independent contract grader agrees with the pinned probe wherever semantics coincide (recorded divergences: none).

`erase.el-with-attrs` (11 journaled reads):

- initial blank screen; the **colored-X seed proof** (five X, stored palette index 2, no other attribute, App-resolved green equal to the binding's palette entry 2, cursor (0,0));
- erased read: row 0 holds only blanks and cell (0,0)'s resolved background equals the X's (required); cursor, every mode flag, margins and the saved-line count unchanged;
- **restoration witness:** after the pinned `\x1b[0m` a `W` written at an explicitly positioned (0,0) (`\x1b[1;1HW`, so an erase that moved the cursor cannot move the witness) must carry neutral storage, else the grade is inconclusive;
- **full-screen control** (graded): rows 1-39 carry a distinct marker each and must survive (accidental whole-screen clearing), row 0 holds only blanks;
- observed, not graded: the explicit-0 spelling; the **pen controls** (X written under SGR 42, then SGR 44 or SGR 0 active when EL runs), because the pinned fixture erases under the
  pen that wrote the X and cannot tell a current-pen fill from a preserved cell background.

`erase.ed-scroll-region` (7 journaled reads):

- initial blank screen with full-screen margins; setup read: `KEEP!` on row 0, `ERASE` on row 2, margins read from the App as 2..9 (zero-based), origin mode off, cursor (2,0);
- erased read: the whole screen holds exactly `KEEP!`, cursor, margins and every mode flag unchanged, no saved line added (stricter than the pinned rows 0 and 2);
- **restoration:** after the pinned `\x1b[r` the margins must read as the full screen and the stored text must equal the erased screen's (else inconclusive; a wrong or missing erase stays the verdict's business, never masked as a restoration failure); the cursor after it is observed, not required;
- observed, not graded: the explicit-0 spelling and the marker-fill boundary observation.

Unsupported needs valid controls and an observed mismatch. An invalid or missing seed, reverse video not proven off, another geometry, a stale palette binding,
contradictory stored projections, missing buffer state or history, incomplete restoration or an unseeded control is inconclusive or refused. A cleared-to-default
background is a measured mismatch the pinned probe cannot see (it grades it inconclusive): the contract grades it unsupported and records the divergence.

## Measurement

Scripts `1d7a161c4ccfabb0c4abfb0b749589b3419bb475` (committed source); App source unchanged since `c16997c2` (the Release sidecar was built from this checkout, no App
change; recorded separately from the scripts revision). Owned run `9417a60257946f1528864f1448132196`, receipt schema 32, source policy v26 (inventory v33, 209
files), default theme, 40x140, disposable sidecar, stock collector run `d400cb6d9ed7870da74725fe3d61f4fe`.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `erase.el-with-attrs` | supported | supported | stored cells, App-resolved background, cursor, buffer state |
| `erase.ed-scroll-region` | supported | supported | stored cells, cursor, margins, mode flags, buffer state |

**EL, resolved color and storage.** Before the erase, the five `X` cells (row 0, columns 0-4) stored palette index 2 (`bgIndex` 2, `backgroundArgb` null), which the App
resolved to `(0,205,0)`, equal to the binding's palette entry 2. After `ESC[K` all 140 cells of row 0 read blank and carry the resolved background `(0,205,0)` (140 of 140), but stored as a
**literal ARGB `4278242560` (0xFF00CD00), not index 2**: the same resolved color in another representation, which satisfies the pinned criterion and is recorded, not required.
The cursor stayed at (0,0), every mode flag was unchanged and rows 1-39 kept their markers (no whole-screen clearing). The pen controls show the erase follows the pen active at
erase time: with SGR 44 active the erased cells resolved to `(0,0,238)` (literal `4278190318`), not the green they replaced; with SGR 0 active they carried the default background (no
painted cell). After the pinned `ESC[0m` the witness `W` (written at an explicitly positioned (0,0)) was stored neutral. The explicit-0 spelling produced the identical result.

**ED, cursor, margins and preserved contents.** The setup read showed `KEEP!` (row 0), `ERASE` (row 2), cursor (2,0), margins 2..9 and origin mode off. After `ESC[J` the whole
screen held exactly `KEEP!`, the cursor stayed at (2,0), margins stayed 2..9, every mode flag and the saved-line count were unchanged. After the pinned `ESC[r` the margins read 0..39 and
the cursor was at (0,0) (observed). The explicit-0 spelling produced the identical result. **Boundary observation (not graded, no standards claim):** with the same margins and the cursor at
(2,0) over a marker on every row, ED 0 kept rows 0-1 and erased every row from 2 down, including rows 10-39 below the bottom margin, in this measurement.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (63 unique cases across the 17 earlier suites; the saved-state/reset suite keeps its two
pinned-inconclusive cases with their separately identified effect contracts supported; counts are derived from the merged contracts by the publication test); the separate 10/10 truecolor-literal
finding; the separate 3/3 App-observed underline-color resolution; stock collector counts unchanged (86 supported, 1 unsupported, 168 inconclusive).

**Preserved attempts** (both in the summary, with run ids and script revisions): (1) `a0ccee04…`, complete but superseded: local Codex review found that the ED restoration check compared the restored
screen with the expected KEEP!-only screen (a no-op or wrong erase would have been masked as an inconclusive restoration failure) and that the EL pen witness was written wherever the erase left the
cursor; the contract and the witness feed (now `ESC[1;1HW`) changed, so the suite was re-measured (both cases graded identically); (2) `9417a602…`, complete, receipt finalized; the final measurement. The
final receipt was independently re-derived from a copy of the retained run directory and is identical to the retained one.

## Limits

Stored state and App-resolved color only: no glyph, pixel or rendered-appearance claim; the pinned terminal cursor-query paths are not qualified. Whether ED must be clipped to the
margins is not claimed (the marker-fill observation is data, not a requirement). Selective erasure, ECH, CSI 3J and rectangular operations are not graded. macOS owned sidecar, default theme, 40x140
geometry only. The stock collector was not re-run in a separate interactive Ntilde tab; its grades for these two ids come from the stock pass of the same owned run.

## Evidence

[`2026-11-09-erase-attrs-region/`](2026-11-09-erase-attrs-region/): summary, run log, common frames with the report header, one file per case and one file per read frame.
`scripts/terminfo/erase-attrs-region-publication.test.mjs` re-derives the report from the retained journal.
