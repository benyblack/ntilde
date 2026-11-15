# Extended mouse coordinate encodings: UTF-8 (1005) and urxvt (1015) (2026-11-15)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `modes.utf8-mouse-1005` and `input.urxvt-mouse`.
Neither mode existed in Ntilde before this batch (both were documented Unsupported), so the batch pairs the production change (modes 1005 and 1015 in
the VT mode state, DECRPM, RIS/DECSTR, state transfer and the App's mouse encoder) with the qualification layer `mex`. This page is the design, the
evidence contract and the measured result: both pinned verdicts stay **inconclusive** and both effect contracts are **supported** (a recorded divergence).

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

## Result

| Case | Pinned (unchanged terminfo.dev@4.0.0 code) | Effect contract | Stock grade (this run) |
|---|---|---|---|
| `modes.utf8-mouse-1005` | **inconclusive** by design (termless: `notTestedResult`, reason `no-semantic-observable`) | `ntilde-utf8-mouse-1005-effect-v1`: **supported** (divergence `pinned-inconclusive-by-design`) | **supported** (`decrpmResult`, evidence `query`; see below) |
| `input.urxvt-mouse` | **inconclusive** (`insufficient-evidence`, evidence `legacy`) | `ntilde-urxvt-mouse-effect-v1`: **supported** (divergence `pinned-inconclusive-by-design`) | inconclusive |

The stock collector in the same owned run (`6fe436ab09e4e2dd5d1e16bde51ab193`) grades **92 supported / 1 unsupported / 162 inconclusive** (`suiteComplete=false`), up from 91/1/163 in the
xterm color-stack batch. The one stock grade that moved is `modes.utf8-mouse-1005`, and it moved for a reason that is **not** an effect-contract upgrade: the pinned *terminal* path of that probe is
`decrpmResult(queryMode(1005))`, which grades supported as soon as the child's DECRQM query is answered with a recognised state, and the production commit now answers `CSI ? 1005 $ p` with `1`
(set) or `2` (reset) instead of `0`. That is a stock effect of the production change, a DECRQM recognition check; it measures no encoded event, and the pinned termless verdict and the effect contract
are separate results that it does not change. `input.urxvt-mouse` has no such path (`mouseInputProbe` validates no generated event on either path), so its stock grade stays inconclusive. The frozen
stock baseline keeps its original grades and no Core point is added. Inventory: two more IDs with a supported effect contract and an inconclusive pinned verdict (no pinned-supported ID is added).

## What the effect contract decides

Both cases ran the same owned input plan (v4, 164 generated events, all `passed`; 46 of them are this layer's) in one disposable direct-local pane of 140x40 cells. Every dispatch is preceded by a
child DECRQM proof of the encoding and tracking state, the child's raw stdin is read by an independent reader, and each report is compared with a literal expectation that equals the independent oracle
(`oracleHex === expectedHex === actualHex` for all 49 experiments, labels included). Mapped events are Avalonia-routed (`appFacts.boundary = avalonia-routed`, `eventDispatched = true`).

- **`modes.utf8-mouse-1005` (24 experiments: 20 required, 3 controls, 1 label; all matched).**
  The two pinned feeds ran as key-up plan events whose only content is the pinned `?1005h` / `?1005l` setup: the child received the DECRPM reply `CSI ? 1005 ; 1 $ y` after the set (and the restored
  state afterwards), which is exactly the input the unchanged pinned termless code consumes. Controls: legacy tracking at column 96 is `1b 5b 4d 20 80 23` (the single octet 80), SGR at the same cell is
  `ESC [ < 0 ; 96 ; 3 M`. With 1005 on, button presses at column 96 row 3 are `ESC [ M 20 c2 80 23` (left; 21 middle, 22 right), column 95 is still one octet (`... 20 7f 23`), column 97 is `c2 81`, and
  column 1 row 1 is `21 21`. Modifiers 7 (shift+ctrl+alt) add 28 to the code (`3c`); release is code 3 (`23`); wheel up/down are `60` and `61`; held-button motion under 1002 (column 97 row 4) is `40 c2 81 24`
  and hover under 1003 (column 96 row 4) is `43 c2 80 24`; X10 press-only tracking carries no modifier bits even with modifiers 7 (`20 c2 80 23`). Single slot: `?1005h` then `?1006h` reports SGR
  (the SGR set replaced UTF-8), `?1006h` then `?1005h` reports UTF-8, `?1015h` then `?1005h` reports UTF-8 (the urxvt encoding was replaced), a `?1005l` while SGR is active leaves SGR in force, and a
  `?1005l` while UTF-8 is active returns to legacy bytes. Label (never graded): the rightmost grid column (140) printed as `c2 ac`, the code-point rule for value 140 + 32 = 172.
- **`input.urxvt-mouse` (25 experiments: 21 required, 3 controls, 1 label; all matched).**
  A lone `?1015h` key-up plan event proves the state and sends nothing. With 1015 on, a left press at column 96 row 3 is `ESC [ 32 ; 96 ; 3 M`, middle `33`, right `34`, column 1 row 1 `32;1;1`, modifiers 7
  `60`, shift `36`, alt `40`, ctrl `48`; release is code 35 with the final `M` (never `m`); wheel up/down are `96` and `97`; held-button motion (1002) is `64;97;4`, hover (1003) `67;96;4`; X10 press-only
  tracking is `32;96;3` with and without modifiers. Single slot: `?1006h` then `?1015h` reports urxvt (the SGR selection was replaced), `?1015h` then `?1006h` reports SGR, `?1005h` then `?1015h` reports urxvt (UTF-8 was replaced), a `?1015l` while
  SGR is active leaves SGR in force, and a `?1015l` while urxvt is active returns to legacy bytes.
  Label (never graded): the rightmost column is `32;140;3`.

The plan events that need no tracking selector (the alone-set and pinned set/reset events) are key-ups because the App refuses a generated pointer event while no tracking selector is active (see the
retained attempt below).

## Divergences and policy that never decided

- Pinned versus effect: both cases keep the pinned `inconclusive` verdict (the unchanged pinned code can only return inconclusive on either path) and record the effect contract's `supported` as the divergence
  `pinned-inconclusive-by-design`; neither is collapsed into the other.
- Coordinate ceiling: the production UTF-8 encoder clamps positions above 2015 (the largest two-octet code point), where xterm emits a NUL at its limit logic. The clamp is a documented deviation; it is
  covered by unit tests, not measured here, because the generated-input protocol accepts columns 1-256 and rows 1-128 only (`GeneratePaneInput` range check), so no generated event can exceed 256.
  urxvt has no ceiling in production.
- Single-slot semantics (xterm `extend_coords`) are measured for the transitions the plan declares (1005/1006/1015 set after each other and a reset of an active versus inactive encoding).
- DECRQM answers `1`/`2` for `?1005` and `?1015`; RIS clears the selection, DECSTR keeps it, and state transfer, clone and the mux outer-terminal mirror carry it. These are covered by VT/App unit tests
  (see [remaining-protocol-variants.md](remaining-protocol-variants.md)); this run does not reset the terminal mid-suite, so RIS/DECSTR are not measured here.

## Not claimed

Generated Avalonia events are not physical pointer input; App admission is not child receipt; delivery in a direct-local pane does not qualify SSH, mux or Windows; one 140x40 grid only (the plan needs at
least 97 columns and the run did not vary the geometry); no Windows/Linux, no remote or mux transport; pixel coordinates (1016) and highlight tracking (1001) are out of scope; the stock grade of
`modes.utf8-mouse-1005` is a DECRQM recognition check, not an encoding measurement; no Core or common-harness score changes. The 164-event routed-input journal also re-verifies the earlier input layers.

## Identities

App 0.14.0 (operator supplied) built from `7578595b09d149b17bb23a9db4ab74d18b827e22` (arm64 Release on macOS 27.0.1, the last commit that changed the App sources; sidecar `artifacts/sidecar-mex`; executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `baf44bef14b761ec33097989dadc545854df8fe731f3198c0761932a2d66a7a7`); measured scripts
`2a6e4b586d5841b9e625ce6096b0c6c820592e60`; source policy `readonly-owned-source-graph-v57` (380 files; manifest SHA-256 `31ba8ef01264502ca2f85310bb2ad2cefa30930d1065fc546d78b7cd926169ff`); query policy
`owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `f7afe1bec7b6d310ec334ddcb3ff74d49fa87f2e3fb872ab88475b49a1b4fd64`; receipt schema 63; Node v24.22.2; owned run
`a25f8db7d77fc3d00295ab5daa52ba2d`; collected 2026-11-15T16:11Z to 16:16Z. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes.

## Retained attempts ([superseded/](2026-11-15-mouse-extended/superseded/))

1. Failed at the owned input suite with `Owned generated-input action failed.` (`failure.json` and `input-failed-observation.json` name the plan event `input.mex.u.pinned.set`), run
   `b54d70a870db2929e199d5ce469bb41f`, collected on the scripts of `93231919758b95d7b553ee88b4ef37955991b173` (the same App build and sidecar). The App refuses a generated pointer event while no
   tracking selector is active, and that plan carried the no-tracking state cases (the pinned `?1005h`/`?1005l` events and the lone `?1015h` event) as pointer events. Commit `2a6e4b58` moved those events
   to key-ups (the measured scripts of the final run); the same sidecar was run again.

The final run is [owned-run.zip](2026-11-15-mouse-extended/owned-run.zip); `node --test scripts/terminfo/mex-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained
input journal, action log, ownership seal and worker run log, and compares the published projection.

## Reproduce

Source policy v57 (380 captured files), receipt schema 63. The collection is `node scripts/terminfo-owned.mjs --executable <sidecar>/Ntilde --app-version <version>` under the shared desktop lock
(see [QA_TERMINFO.md](../QA_TERMINFO.md)); rederive with `completeOwnedReceipt` over a copy of the retained run minus `cell-receipt.json`.
