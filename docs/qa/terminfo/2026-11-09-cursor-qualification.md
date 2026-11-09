# Cursor qualification (2026-11-09)

Qualifies exactly four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`cursor.cup-scroll-region`, `cursor.hide`, `cursor.shape` and `cursor.reverse-wrap`. Other cursor cases, general rendering, real-time blink,
platform qualification and unrelated terminal features are not part of this batch.

## Pinned definitions and verdict semantics

| Id | Pinned fixture bytes | What the pinned code can decide |
|---|---|---|
| `cursor.cup-scroll-region` | termless: `ESC[5;15r`, `ESC[?6h`, `ESC[1;1H`, cleanup `ESC[?6l`, `ESC[r`; app: the same, then CPR, `ESC[2;1H`, CPR, `ESC[999B`, CPR | termless: physical cursor `(4,0)` after the three feeds (supported/unsupported). App: **always inconclusive** ("the physical row 5 and the margins still need an independent observable"); it records only region-relative CPR replies |
| `cursor.hide` | termless: `ESC[?25h`, `ESC[?25l`, restore; app: `ESC[0m ESC[2J ESC[H`, `ESC[1;1HCURSOR ESC[2;1HL  R ESC[2;3H ESC[2 q ESC[?25h`, capture, `ESC[?25l`, capture, cleanup | termless: stored visibility shown → hidden → restored. App: **always inconclusive** (capture-only; "control must visibly show the cursor") |
| `cursor.shape` | termless: `ESC[6 q`; app: capture, `ESC[5 q`, capture, `ESC[0 q` | termless: stored style is a beam after CSI 6 SP q. App: **always inconclusive** ("shape difference requires review") |
| `cursor.reverse-wrap` | termless and app: `ESC[?45h`, `ESC[?7h`, `ESC[H`, `A` × cols + `B`, `BS BS`, cleanup `ESC[?45l` | termless: physical cursor `(0, cols-1)`. App: CPR `1;1`, `2;2`, then `1;cols` decides |

## Collector defects, reproduced and repaired

The pinned `cursor.reverse-wrap` probe record lacks `termNeedsGeometry: true`. Its app callback reads `ctx.rows` and `ctx.cols` first, the shared
`createTermContext` getters throw `UndeclaredTerminalGeometry` for an undeclared probe, and the callback fails **before its first byte**; the stock
collector leaves that as an ungraded error (`suiteComplete=false`). It is not a terminal verdict. This batch found the **same latent defect in
`cursor.shape`**: its app callback reads the geometry only after its two captures, so the stock run (which supplies no capture adapter, so it ends
inconclusive "No cursor pixel readback for shape") never reaches it, but any capturing run does.

`ntilde-owned-cursor-geometry-repair-v1` is the explicitly versioned, digest-checked repair, layered inside the twenty-first extension
(`ntilde-owned-cursor-qualification-grading-v1`). Derivation refuses unless the retained attribute-rectangle extension still contains the exact pinned
cursor section (SHA-256 `bcac625f…fbb9`), the exact reverse-wrap and shape registrations and the exact `createTermContext` source, each once. The repair
changes exactly one piece of metadata, at run time, on the probe **record** handed to the real `createTermContext`: `{ ...original, termNeedsGeometry: true }`.
The termless and app callbacks are the original function objects (retained SHA-256 of both sources), the bundle text is never rewritten, and the original
execution is re-run for every report so the failures stay retained: reverse-wrap throws with no bytes sent; shape sends `ESC[5 q` and its `ESC[0 q`
cleanup, then throws. Original bundle, probe hash and stock receipt are untouched. Geometry is proven from the parent's own snapshots before and after the
callback (40×140 in every frame) and matches what the callback read.

## Method

Twenty-first digest-checked extension, contracts v1, suite `ntilde-owned-cursor-qualification-graded-v1`, prefix `curq`, nonce `NTILDE_CQ_`. Each group starts from a measured
reset on the alternate screen (SGR 0, DECSCNM/DECOM off, auto-wrap on, ?45 off, margins reset, IRM off, cleared, home, visible, host-default shape). Pinned bytes
are fed exactly through the owned fence; the pinned termless and app callbacks run over the parent-journaled observations (live in the worker, ported in the
parent, and re-run against the derived bundle during retention). Reads are plain state, **CPR** (the fence adds a bounded CPR query; the raw reply is retained
next to the physical cursor and cross-checked against it, region-relative under DECOM) or **capture** (a controlled offscreen render at presentation time 0 ms or
530 ms, bracketed by two state reads that must be identical, bound to the actual stored cursor mode and phase). Expectations are written from the protocol in the
contracts, never from readback; unsupported needs valid controls and a measured mismatch, and an invalid setup, missing observation or incomplete restoration is inconclusive.

- **CUP with DECOM:** physical rows `4`, `5`, `14` with margins `[4,14]` and origin set; CPR `1;1`, `2;1`, `11;1`. Controls: DECOM off (CUP `1;1`/`15;1`/`3;3` absolute, CPR absolute; every control row fits the 15-row minimum geometry), DECOM on boundaries (`11;1`, clamp at `12;1`, CUU stops at the top margin), restoration (CUP `3;5` absolute again).
- **Hide:** stored visibility and four pixel checks: the control paints the cursor cell, the hidden frame paints exactly the background, nothing else changes, and the control re-rendered after restoration is pixel-identical.
- **Shape:** stored mode after `CSI 6 SP q` (steady bar) and `CSI 5 SP q` (blinking bar, `isDefault=false`) against the host default (`style 2, blinking, isDefault=true`, kept separate and restored by `CSI 0 q`); pixels: steady block/underline/bar/hidden references classified from the painted ink, the requested bar equals the steady bar in the visible half-period and the background in the hidden one, the default differs, nothing else changes. Synthetic phases; no desktop blink cadence is measured.
- **Reverse-wrap:** physical cursor and CPR across the wrap boundary with a valid home and wrap control, restoration, and a `?45`-off control with the same bytes that must stay on row 2.

The historical `CSI 0 q` negatives that expected `isDefault=false` are superseded, not changed: the contract expects host-default intent after `CSI 0 q`, as the current App stores it.

## Measurement

Scripts `de1a63d1` (committed; main with attribute-rectangle and the reverse-wraparound fix, plus three Codex-review fixes), App from main `6b26a158` (sidecar `sidecar-cursor-qual-final`), run `ce7cf5bb…` (stock run `f31a740c…`), receipt schema 34, source policy v28 (inventory v35, 221 files), default theme, 40×140.

| Case | Pinned termless | Pinned app (repaired where marked) | Effect contract | Divergence |
|---|---|---|---|---|
| `cursor.cup-scroll-region` | supported | inconclusive (by design) | supported | `contract-measures-beyond-pinned` (app) |
| `cursor.hide` | supported | inconclusive (by design) | supported | `contract-measures-beyond-pinned` (app) |
| `cursor.shape` | supported | inconclusive (by design, repaired) | supported | `contract-measures-beyond-pinned` (app) |
| `cursor.reverse-wrap` | supported | **supported** (repaired) | supported | none |

Counts: 4 supported. Stock grades are unchanged (86 supported, 1 unsupported, 168 inconclusive; `cursor.reverse-wrap` remains the ungraded `UndeclaredTerminalGeometry` diagnostic, `suiteComplete=false`).
Observed: CUP physical rows 4/5/14 with CPR `1;1`/`2;1`/`11;1`; hide painted 171 cursor pixels and then exactly the background; the bar paints one 1-px column, the underline two rows, the block the whole 9×19 cell; reverse-wrap moved `(1,1)` → `(0,139)` with CPR `2;2` → `1;140`, and `2;1` at `(1,0)` without `?45`.

**Preserved attempts** (in the summary): the first complete run on the fixed App (`fee69703…`, scripts `a9ec1e30`, also four supported), superseded when Codex review changed three controls/checks and the measurement was repeated; an aborted launch (display asleep; Avalonia render timer error, nothing collected), and the **before-fix run** (`0e972801…`, App `bd802f67`, scripts `159ceecd`): the three other cases graded identically, but `cursor.reverse-wrap` was **unsupported with valid controls** (CPR `1;1`, `2;2`, `2;1`; termless fixture ended at `(1,0)`; the mode-off control behaved identically): Ntilde ignored DECSET 45. That was a genuine terminal gap, fixed with a failing regression first in [#913](https://github.com/benyblack/ntilde/pull/913) (reverse-wraparound for BS and CUB, xterm semantics; merged as `6b26a158`), then re-measured above. Its case file is retained in `before-fix/`.

Same-run reconfirmation (kept separate): every merged predecessor qualification suite supported in the same run (the receipt re-derives each, including the attribute-rectangle suite), 10/10 truecolor-literal finding, 3/3 App-observed underline-colour resolution.

## Limits

Stored state, physical cursor, retained CPR replies and synthetic offscreen render phases of ASCII fixtures on macOS with the owned sidecar and default theme. No desktop blink cadence, focus, compositor timing, Unicode, wide-cell, reflow, resize or platform claim; the pinned app callbacks that cannot decide remain inconclusive.

## Evidence

[`2026-11-09-cursor/`](2026-11-09-cursor/): summary, report header, one file per case, run log, common frames, one file per read frame, ten controlled PNGs in `cursor-pixels/`, and the before-fix case. `scripts/terminfo/cursor-qual-publication.test.mjs` re-derives the report, including every pixel comparison, from the retained bytes.
