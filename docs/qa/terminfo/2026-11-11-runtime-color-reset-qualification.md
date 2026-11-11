# Runtime-color reset qualification: OSC 104, 110, 111, 112 (2026-11-11)

Qualifies four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `extensions.osc104-reset-palette`, `extensions.osc110-reset-fg`, `extensions.osc111-reset-bg`, `extensions.osc112-reset-cursor`.
They were outside the frozen Core catalogue. The implementation under test is the existing VT/App runtime-color path (no production change in this batch):
OSC 4/10/11/12 set and query, OSC 104/110/111/112 reset. Real-time cursor cadence, desktop appearance, selection and pointer colors, Kitty named colors, other themes
and other platforms are not qualified here. The [effect contract](runtime-color-reset.md) and the [owned-colors foundation](owned-colors.md) have the details.

## Pinned definitions

Each case is one `colorResetProbe(setCode, resetCode[, index])` registration. Its **termless path** feeds the BEL-spelled query, the BEL-spelled set of `rgb:aa/bb/cc`
(`rgb:12/34/56` if the original already is that color), the query, the BEL-spelled reset (`ESC]104;0 BEL`, `ESC]110 BEL`, `ESC]111 BEL`, `ESC]112 BEL`) and the
query again; supported iff the changed reply equals the requested color and the restored reply equals the original. Its **terminal path** is
`unmeasuredStateEffect`: it writes nothing and is inconclusive by design (evidence `none`). The derivation pins the four registrations, `colorResetProbe` and its helpers
verbatim, so a change to either path would be refused. The pinned terminal verdict is preserved beside the termless verdict and the effect conclusion, never replaced.

## Method

Twenty-eighth digest-checked collector extension `ntilde-owned-runtime-color-reset-grading-v1` (derived over the left/right-margin one), contracts v1, suite
`ntilde-owned-runtime-color-reset-graded-v1`, prefix `crst`, nonce `NTILDE_CR_`. The pinned bytes are replayed on a measured reset screen inside a pristine disposable instance; the pinned
termless code runs live in the worker over the journaled replies and again in the parent (it must agree). Each case has three experiments (four for OSC 104), each `baseline → override → reset…`,
every stage read as four separately retained layers (exact fenced replies; the App-resolved colors and override metadata; stored cells and cursor; the controlled offscreen render
with its original PNG). 44 reads and 47 captures in all:

- **OSC 104:** `pinned` (pinned): supported, 18/18 controls and 8/8 assertions; `individual-slots` (control): supported, 18/18 controls and 19/19 assertions; `isolation` (control): supported, 17/17 controls and 10/10 assertions; `whole-palette` (control): supported, 18/18 controls and 8/8 assertions. Slot 0 is the pinned fixture; five slots (base 1, bright 9, cube 16, extended 200, gray 255) are overridden and reset one, two and then the rest with the others still overridden; all 256 slots are overridden and reset with `ESC]104 BEL`, and the complete palette and override list must return.
- **OSC 110:** `pinned` (pinned): supported, 18/18 controls and 8/8 assertions; `isolation` (control): supported, 18/18 controls and 6/6 assertions; `sgr-default` (control): supported, 17/17 controls and 11/11 assertions. Background and cursor overrides must survive; SGR 39 changes the pen, not the runtime default, and OSC 110 does not change the pen.
- **OSC 111:** `pinned` (pinned): supported, 18/18 controls and 8/8 assertions; `isolation` (control): supported, 18/18 controls and 6/6 assertions; `sgr-default` (control): supported, 17/17 controls and 11/11 assertions. The same for the background and SGR 49.
- **OSC 112:** `pinned` (pinned): supported, 18/18 controls and 8/8 assertions; `isolation` (control): supported, 18/18 controls and 6/6 assertions; `hidden-beam` (control): supported, 17/17 controls and 6/6 assertions. Foreground and background overrides survive; shape and visibility never move; a hidden steady-beam cursor stays hidden.

Expected restoration is the baseline measured before any mutation with the model's overrides laid over it; an independent model parses the exact bytes written (it shares no code with the VT).
Invalid controls, missing replies or identity drift make an experiment inconclusive; only valid controls let a mismatch establish unsupported. Runtime colors survive alternate-screen exit and
DECSTR, so neither is evidence of a reset: the finish write clears them explicitly and the original screen, theme and PNG must return exactly.

## Measurement

Scripts (adapter) revision `6332de8b` (full SHA in `summary.json`); App source revision `1c3400f4` (the last commit that touched `src/`); the Release sidecar was built from that tree with the repository wrappers.
Owned run `746456cc05243c56088e09fc218583a7`, receipt schema 42, source policy v36 (inventory v43, 267 files), theme `Default`, 40x140, App version 0.13.0 (operator supplied;
not independently verified), disposable sidecar `artifacts/sidecar-colorreset`, stock collector run `071705f2e1cba6d79456315c52a506f7`, measured 2026-11-11T09:52Z on macOS with Node 24.22.2, under the shared desktop-collection lock.

| Case | Pinned termless (parent replay + live pinned code) | Pinned terminal path | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.osc104-reset-palette` | supported | inconclusive (by design, evidence none) | supported | inconclusive (`none`) |
| `extensions.osc110-reset-fg` | supported | inconclusive (by design, evidence none) | supported | inconclusive (`none`) |
| `extensions.osc111-reset-bg` | supported | inconclusive (by design, evidence none) | supported | inconclusive (`none`) |
| `extensions.osc112-reset-cursor` | supported | inconclusive (by design, evidence none) | supported | inconclusive (`none`) |

Pinned and effect verdicts agree in every case; no divergence is recorded. The pinned replies (BEL query, ST reply, `rrrr/gggg/bbbb`):

| Case | Baseline | After the set | After the reset |
|---|---|---|---|
| OSC 104 slot 0 | 0000/0000/0000 | aaaa/bbbb/cccc | 0000/0000/0000 |
| OSC 110 foreground | d3d3/d3d3/d3d3 | aaaa/bbbb/cccc | d3d3/d3d3/d3d3 |
| OSC 111 background | 0000/0000/0000 | aaaa/bbbb/cccc | 0000/0000/0000 |
| OSC 112 cursor | ffff/ffff/ffff | aaaa/bbbb/cccc | ffff/ffff/ffff |

The measured baselines are the configured theme's: slot 0 black, foreground #d3d3d3, background #000000, cursor #ffffff; every reset returned exactly those values
(all layers, derived cursor text and selection paint included) and the original PNG, and the pinned overrides took effect in every layer first.
Individual slot resets (`ESC]104;1`, `ESC]104;9;200`, `ESC]104;16;255`):

- baseline: 1=cdcd00000000 9=ffff00000000 16=000000000000 200=ffff0000d7d7 255=eeeeeeeeeeee (App override list [])
- override: 1=30309090f2f2 9=585868688a8a 16=5b5be5e52f2f 200=f3f34d4dd7d7 255=e6e6dada0c0c (App override list [1,9,16,200,255])
- reset-one: 1=cdcd00000000 9=585868688a8a 16=5b5be5e52f2f 200=f3f34d4dd7d7 255=e6e6dada0c0c (App override list [9,16,200,255])
- reset-two: 1=cdcd00000000 9=ffff00000000 16=5b5be5e52f2f 200=ffff0000d7d7 255=e6e6dada0c0c (App override list [16,255])
- reset-rest: 1=cdcd00000000 9=ffff00000000 16=000000000000 200=ffff0000d7d7 255=eeeeeeeeeeee (App override list [])

The whole-palette control overrode all 256 slots (0 → 256 → 0 overridden slots after baseline, override, reset).
SGR defaults: `osc110`: baseline: override false; override: override true; sgr: override true; reset: override false; `osc111`: baseline: override false; override: override true; sgr: override true; reset: override false.

**Predecessors and findings.** Every merged predecessor suite (26: ascii-sgr (4), colors (5), rgb (2), attrs (5), palette (6), truecolor (2), underline (4), underlineColor (3), reset (2), closure (3), erase-line (3), char-edit (3), erase-display (3), line-editing (2), charsets (4), scrollback (9), saved-reset (5), erase-attrs-region (2), attr-rect (3), cursor (4), erase-chars-history (2), selective-erase (2), rect-edit (3), col-edit (4), rect-checksum (1), lr-margins (1)) and both finding suites reconfirm in the same run with the same outcomes as their own publications; the exact-query suite is unchanged (33 matched, 7 mismatched); the receipt re-derives all of them.
Stock collection was rerun in this run: 87 supported / 1 unsupported / 167 inconclusive, `suiteComplete=false` (measured, not carried forward); the four cases stay inconclusive and nothing is promoted.

**Attempts.**
- Attempt 1 (superseded): run `dcaec14c5c8751cebd4654992b42d086`, scripts `1eadace1`, receipt completed; complete, receipt schema 41, source policy v35: all four cases supported on the pre-review scripts (not published) Reason it was superseded: the Codex review then found two grader gaps (an absent reset reply graded unsupported instead of inconclusive; a reset the renderer never applied could pass where other overrides remain), a cross-target isolation experiment was added, and main moved to source policy v35 (Core gaps), so the scripts, the source inventory and the receipt scope all changed and the measurement was repeated.
- Attempt 2 (published): run `746456cc05243c56088e09fc218583a7`, scripts `6332de8b`, receipt completed; complete, receipt schema 42, source policy v36 (inventory v43, 267 files): all four cases pinned termless supported, effect contract supported, terminal paths inconclusive by design, on App source 1c3400f4 (the published run)

## Layout

`summary.json`, `run-log.json`, `journal-common.json`, one `case-<id>.json` per case, one `journal-<id>-read<n>.json` per read frame and `crst-pixels/` (the 47 original PNGs).
`color-reset-publication.test.mjs` reassembles the journal, re-verifies every PNG and re-derives the report byte for byte.

## Boundaries

Newly qualified existing behavior: all four cases. Newly fixed terminal behavior: none. Query replies are not rendered appearance; the render is a separate layer at a fixed synthetic phase.
The pinned terminal paths never carry a verdict; the stock grades are unchanged.
