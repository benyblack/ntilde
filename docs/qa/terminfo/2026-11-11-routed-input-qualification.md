# Routed-input qualification: X10 tracking, button-event tracking and application keypad (2026-11-11)

Qualifies three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `input.x10-mouse`, `input.button-event-mouse`
and `modes.application-keypad`. The contract, the plan and the evidence boundaries are in [routed-input.md](routed-input.md). Windows, Linux, SSH,
mux, physical keyboard and pointer input, host NumLock and key translation, mouse modes 1005/1006-as-tracking/1015/1016, modifyOtherKeys and Kitty
keyboard expansion are not qualified here.

## Result

| Case | Pinned (live code + parent replay) | Effect contract | Experiments | Stock grade |
|---|---|---|---|---|
| `input.x10-mouse` | inconclusive by design | **supported** (`ntilde-x10-mouse-tracking-effect-v1`) | 21: 16 required, 5 controls | inconclusive |
| `input.button-event-mouse` | inconclusive by design | **supported** (`ntilde-button-event-mouse-tracking-effect-v1`) | 11: 6 required, 2 controls, 3 SGR labels | inconclusive |
| `modes.application-keypad` | inconclusive by design | **supported** (`ntilde-application-keypad-effect-v1`) | 30: 25 required, 5 controls | inconclusive |

All 60 declared events produced exactly the child bytes their literal protocol expectation and the independent oracle require. The pinned verdicts
are preserved as the conclusions and the effect verdicts are separate fields; the three divergences are recorded as `pinned-inconclusive-by-design`.
The stock collector, re-run in the same owned run, still grades all three ids inconclusive (167 inconclusive / 87 supported / 1 unsupported, the same
as the preceding publication), so no stock grade changed and **this extension qualification does not raise the stock compatibility score**.

## Pinned definitions

None of the pinned probes can decide. `mouseInputProbe` (both mouse ids) only reads `getMode("mouseTracking")` and returns inconclusive; the
application-keypad probe feeds `ESC =` and `ESC >`, reads the mode flag twice and returns `notTestedResult("keypad input")`; every terminal path
returns inconclusive without touching the terminal. The retained code runs through the twenty-eighth derivation
(`ntilde-owned-routed-input-grading-v1`) over mode values the child itself received as DECRQM replies: the keypad probe's feeds are the exact
setups of plan cases `input.keypad.application.0` (`ESC =`) and `input.keypad.restore.deckpnm.enter` (`ESC >`), and the child reported DECKPAM set
and then reset (`{"on":true,"off":true}`); the mouse probes read the restored defaults (`{"currentMode":false}`). The parent re-executed the probes
from the journal and agreed with the live results. Details and the pinned/effect separation: [routed-input.md](routed-input.md).

## What the child received

Direct-local disposable pane, 40×140, default theme; each event was a declared Avalonia routed event, preceded by an independent DECRQM proof of the
expected modes and followed by a fresh pair of FIFO ordering replies, so silence is a completed capture and not a lost delivery.

**X10 (DECSET 9).** Presses of left, middle and right sent `1b 5b 4d 20|21|22 80 23` at column 96, row 3, with the column byte `0x80` as one octet
(never `c2 80`). Shift, control, alt, meta, all four, and the all-modifier press all produced the same modifier-free frame; the same press under
normal tracking carried the modifier bits (`<`). Release, held-button motion, hover and both wheel directions produced no bytes, while the identical
events under normal and button-event tracking were reported (release `23`, wheel up `60`, held motion `40`), so the suppression is the protocol's and
not a dropped event. SGR coordinates (`1006`) under mode 9 gave `ESC [ < 0 ; 96 ; 3 M`, and resetting only `1006` returned the legacy frame with
mode 9 still selected, so tracking selection and coordinate encoding are independent. The DECRQM replies showed 9 set and 1000/1002/1003 reset
for every X10 case.

**Button-event (DECSET 1002).** Left and right presses `20|22`, held left motion `40`, held middle motion with shift+alt `4d` (column 97, byte
`0x81`), release `23`; hover with no button produced nothing behind a completed barrier while the same hover under any-event tracking sent
`1b 5b 4d 43 7e 23` and held motion under normal tracking sent nothing. SGR press, motion and release (`0;96;3M`, `32;96;3M`, `0;96;3m`) are
labeled observations, not part of the verdict.

**Application keypad.** In numeric mode key-down and key-up sent nothing, text `7` sent `37`, keypad Enter sent `0d`, and a key-down carrying its
backend text sent `37` once. After `ESC =` all 17 physical keypad key-downs sent their SS3 frames (`ESC O p`…`ESC O y`, `n l k m j o`, Enter `M`);
a key-down carrying its backend TextInput (digit `7` and `+`) sent only `ESC O w` / `ESC O k` with the App reporting both events handled (duplicate
suppression); key-up sent nothing; a standalone text `7` after that sent `37`; `ESC >` restored numeric Enter (`0d`); DECNKM (`?66h` then `?66l`)
selected and restored the same way. The App's `handled`/`inputSubmitted`/`textHandled` facts are retained beside, never instead of, these bytes.

## Implementation changes

- **Newly implemented:** a `keyDown` declared event may carry one keypad character (`text`). The App raises that TextInput right after the key-down in
  the same dispatch and reports `textHandled`. Before this, every generated keyboard event cleared the one-shot text correlation when it returned, so
  no declared call could exercise the handled-key duplicate guard. Tests: `MainWindowGeneratedInputTests` (application mode, numeric mode, pending
  human correlation) and `AgentHostGeneratedInputProtocolTests` (validation). No encoder, mouse routing or mode behavior changed.
- **Newly qualified existing behavior:** DECSET 9 press-only reporting, byte-preserved legacy coordinates, mode 1002 button-event reporting, DECKPAM
  and DECNKM selection and restoration, and the physical keypad table. These shipped earlier (#769 foundation); this is their first measurement
  against an independent contract.
- Scripts: input plan v2 beside the frozen v1, the layered derivation, collector, grading, receipt, source policy v36 (inventory v43, 267 files),
  receipt schema 42, and the query/image suites' earlier-challenge limits generalized from 110/80 to the plan's 152/122.

## Measurement

Scripts revision `91f0f63a` (full SHA in `summary.json`); App source revision `de5d921e` (the last commit that touched `src/`, the merged Core-gap change), Release sidecar built
from that tree with the repository wrappers (`artifacts/sidecar-routed-input`). Owned run `aee54a96a4efae23d4e9189b11259e35`, receipt schema 42,
source policy v36, App version 0.13.0 (operator supplied; not independently verified), executable SHA-256 `f5814e51…`, Node `v24.22.2`, macOS
arm64, stock collector run `5f9325e8e35e29392bc23e20a204ee3e`, measured 2026-11-11T10:05Z under the shared desktop-collection lock. Every one of the
26 earlier suites in the same-run table, both finding suites and the exact-query suite (33 matched, 7 mismatched, unchanged) reconfirmed in the same run
with the outcomes of their own publications; the receipt re-derives all of them.

**Attempts** (all but the last are superseded; their reasons are actual):
1. Run `7d982ad3`, scripts `eaaae0a6`: complete, 56 of 58 experiments matched. `button-event` was unsupported because the held middle motion
   targeted the cell the previous held motion had reported, which the App coalesces by design (one 1002 report per cell change); the keypad was
   unsupported because the duplicate-text experiment sent a standalone text after an earlier key-down, which a generated event cannot correlate. Both
   were defects of the plan and the declared-event API, not of the encoder; the report is retained in `superseded/`.
2. Run `51947880`: aborted two seconds in with "Unexpected input during cell collection." (no grade, cause unknown, retained in `superseded/`); rerun unchanged.
3. Run `6eb8e771`, scripts `906684d6`: complete, all 60 matched. Re-measured because review then changed measured scripts (a 97-column minimum and
   a required `textHandled` fact for paired events); its evidence stays in the ignored local run directory.
4. Run `c1fc366b`, scripts `a09622ef`, source policy v35 / receipt 41: complete, all 60 matched, all three supported. Superseded because the Core-gap batch (PR 932)
   merged first and claimed v35 / receipt 41; this work was restacked onto it and re-measured as v36 / receipt 42 (evidence in the ignored local run directory).
5. Run `b92a7063`, scripts `f50245da`, policy v36 / receipt 42: complete, all 60 matched. Re-measured because the final review found that a grid below the plan's
   97 columns turned the earlier typed input-unavailable fallback into a failed run; the layer now records a typed unavailable marker (receipt checks and tests added).
6. The published run.

## Layout and re-derivation

`summary.json` (context, receipt, same-run outcomes of every predecessor, stock probes, attempts), `run-log.json` (the worker's pinned-probe results),
`journal-common.json` (report header, restoration seal, start/bind/finish frames), `case-<id>.json` ×3, `input-<index>-<experiment>.json` ×60 (the App
action record, the mode-control frame and the read frame of each event), `superseded/`. `routed-input-publication.test.mjs` reassembles the 123-frame
journal, re-derives the owned input report (its SHA-256 equals the receipt's), the graded report byte for byte and the pinned replay.

## Remaining limitations

Generated App events are not hardware input and say nothing about the host keyboard layout or NumLock; direct-local delivery does not qualify SSH,
mux or Windows. Main-row digit key-downs are not a declared event, so ordinary digits are covered only as text. The first-attempt finding that the
App reports button motion once per cell change is documented App behavior (issue #269), exercised here with distinct cells. Pixel, UTF-8 and urxvt
mouse coordinates, modifyOtherKeys and Kitty keyboard expansion remain unqualified.
