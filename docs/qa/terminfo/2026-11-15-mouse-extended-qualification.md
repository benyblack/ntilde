# Extended mouse coordinate encodings: UTF-8 (1005) and urxvt (1015) (2026-11-15)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `modes.utf8-mouse-1005` and `input.urxvt-mouse`.
Neither mode existed in Ntilde before this batch (both were documented Unsupported), so the batch pairs the production change (modes 1005 and 1015 in
the VT mode state, DECRPM, RIS/DECSTR, state transfer and the App's mouse encoder) with the qualification layer `mex`. This page is the design and the
evidence contract. **Results are pending**: no measurement has been retained yet, so no verdict, count or revision is claimed below.

## Pinned criteria (read from the unchanged bundle)

| Case | Pinned probe | What it can decide |
|---|---|---|
| `modes.utf8-mouse-1005` | `decrpmModeProbe(…, 1005, termless)`: feeds `ESC [ ? 1005 h`, captures the reply to `ESC [ ? 1005 $ p`, feeds `ESC [ ? 1005 l`, returns `notTestedResult("encoded UTF-8 mouse events")` | The termless function never decides (no semantic observable): **inconclusive by design**. The *stock terminal path* is `decrpmResult(queryMode(1005))`, which is **supported** once DECRPM recognises the mode (a stock-collector effect of the production change, kept separate from the pinned termless verdict and from the effect contract) |
| `input.urxvt-mouse` | `mouseInputProbe(…, "mouseTracking")` — byte-identical to `input.x10-mouse`: reads `ctx.getMode("mouseTracking")`, feeds nothing | Always `inconclusive / insufficient-evidence` (no generated event and encoded report were validated) on both paths |

The layer replays the unchanged pinned code over the values the child itself received: the DECRPM reply to `?1005$p` after the pinned `?1005h` feed
(a dedicated plan event whose setup is exactly that feed) and the restored tracking state. Conclusions keep the pinned inconclusive verdicts; a separately
identified effect contract per case (`ntilde-utf8-mouse-1005-effect-v1`, `ntilde-urxvt-mouse-effect-v1`) carries the graded outcome and any difference is a recorded
divergence, never an absorbed one.

## Effect contract and independent oracle

The oracle (`scripts/terminfo/mex-contracts.mjs`) is written from xterm's `ctlseqs` ("Extended coordinates") and `button.c` (`EmitMousePosition`, `EditorButton`)
and never calls the App's encoder. The plan carries a literal byte string for every event and the oracle must agree with each literal.

* **UTF-8 (1005)**: `CSI M Cb Cx Cy`, each value printed as the UTF-8 encoding of (value + 32): one octet through position 95, two octets from 96 (so column 96 is `C2 80`
  where legacy X10 sends `80`), at most position 2015. Releases carry code 3 plus modifier bits, exactly as legacy tracking.
* **urxvt (1015)**: `CSI Cb ; Cx ; Cy M` in decimal with the X10 +32 offset on Cb (left press at 96,3 is `CSI 32;96;3 M`), a release is code 3 (`CSI 35;…M`, final `M`, never `m`),
  wheel codes 96/97, no coordinate limit.
* **Single-slot selection** (xterm `extend_coords`): DECSET 1005, 1006 or 1015 replaces the previous encoding; DECRST of one clears it only when it is the active encoding.
  Every dispatch is preceded by a child DECRQM proof of the state (`?1005`, `?1006`, `?1015` and the tracking selectors), so a replaced encoding is observed, not assumed.
* Tracking selectors (9, 1000, 1002, 1003) stay independent of the encoding: X10 tracking never carries modifier bits, hover needs 1003, held-button motion 1002.

Documented policy (never deciding): positions above 2015 are clamped, xterm's NUL-at-limit marker is not emulated; the rightmost-grid-column events are *labels*
(their literal is computed from the code-point rule for the observed grid, and they never grade).

## Plan (input plan v4)

v4 keeps every v3 event verbatim (their modes only gain `mouseUtf8:false`, `mouseUrxvt:false`), so the routed-input and modifyOtherKeys layers keep re-deriving from the
same journal, and appends 46 events: a proved all-off baseline, the two pinned feeds, legacy and SGR controls shared by both cases, then 21 `modes.utf8-mouse-1005` (including the two pinned feeds) and
22 `input.urxvt-mouse` events (presses of each button at columns 95/96/97 and 1,1, modifiers, release, wheel, held motion, hover, X10 press-only, the five single-slot cases and a
labeled rightmost-column press). Motion events use cells that differ from the preceding reported motion (the App reports motion once per cell). Resets use
`ESC [ ? 1;9;1000;1002;1003;1006;1005;1015 l`; the plan needs a grid of at least 97 columns and refuses a narrower one as typed unavailability.
Budgets: 164 events, 330 raw captures (budget 384), per-reader snapshots 167 (budget 192); the later query suite owes 360 earlier challenges.

## Limits

Generated Avalonia events are not physical pointer input; App admission is not child receipt; delivery in a direct-local pane does not qualify SSH, mux or Windows;
pixel coordinates (1016) and highlight tracking (1001) are out of scope; the stock grade of `modes.utf8-mouse-1005` is a DECRQM recognition check, not an encoding measurement;
no Core or common-harness score changes.

## Reproduce

Source policy v57 (380 captured files), receipt schema 63. The collection is `node scripts/terminfo-owned.mjs --executable <sidecar>/Ntilde --app-version <version>` under the shared desktop lock
(see [QA_TERMINFO.md](../QA_TERMINFO.md)); rederive with `completeOwnedReceipt` over a copy of the retained run minus `cell-receipt.json`.
