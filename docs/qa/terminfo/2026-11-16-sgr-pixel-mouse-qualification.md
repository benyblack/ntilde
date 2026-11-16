# SGR-Pixels mouse coordinates (1016) (2026-11-16)

Qualifies one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `input.pixel-mouse`.
`?1016` did not exist in Ntilde before this batch (documented Unsupported), so the batch pairs the production change (the mode in the VT mode state, DECRQM, RIS/DECSTR, XTSAVE, state transfer,
the App's mouse encoder with a device-pixel pointer position, and a bounded pixel declaration for generated pointer input) with the qualification layer `pxm`. This page is the design, the evidence contract
and the measured result: the pinned verdict stays **inconclusive** and the effect contract is **supported** (a recorded divergence).

## Pinned criterion (read from the unchanged bundle)

| Case | Pinned probe | What it can decide |
|---|---|---|
| `input.pixel-mouse` | `mouseInputProbe(…, "pixelMouse")`: reads `ctx.getMode("pixelMouse")`, feeds nothing | Always `inconclusive / insufficient-evidence` (no generated event and encoded report were validated) on both the termless (evidence `legacy`) and the terminal (evidence `none`) path |

It has the same shape as `input.urxvt-mouse` and `input.x10-mouse`. The layer replays the unchanged pinned code over the one value the child itself received: the DECRQM state of `?1016` in the restored
capture (reset), with the set state proved by the selector-only plan event kept beside it. The conclusion keeps the pinned inconclusive verdict; the separately identified effect contract
`ntilde-sgr-pixel-mouse-1016-effect-v1` carries the graded outcome and the difference is a recorded divergence, never an absorbed one. No stock grade can move.

## Effect contract and independent oracle

The oracle (`scripts/terminfo/pxm-contracts.mjs`) is written from xterm's `ctlseqs` ("SGR-Pixels (1016): the same mouse response format as the 1006 control, but report position in pixels rather than
character cells") and `button.c` (`EditorButton`: in this mode `row = event->y - OriginY`, `col = event->x - OriginX`; `EmitMousePosition` prints value + 1; `EmitButtonCode` drops the 32 offset; a release keeps the button and
ends in `m`). It never calls the App's encoder. The plan carries a literal byte string for every event and the oracle must agree with each literal, including the stateful rule below.

* **Report**: `CSI < Cb ; Px ; Py M|m`, Cb the SGR code (left 0, middle 1, right 2, shift +4, alt/meta +8, control +16, motion +32, wheel 64/65), Px/Py the zero-based pixel offset inside the pane text area plus one, no
  coordinate ceiling, release `m` with the released button.
* **Unit**: the App reports device pixels, the unit of its `CSI 14 t` / `CSI 16 t` replies and of Sixel/Kitty placement (view position × the view's effective render scaling, clamped to the text area, whose extent is what
  `CSI 14 t` rounds to). The generated events declare exact device pixels (`pixelX`/`pixelY`, zero-based); the App routes the framework event at the centre of that pixel and reports back the cell it derived.
* **Motion** under 1002/1003 is reported whenever the pixel position changes (xterm compares the pixel offsets it just computed): an adjacent pixel in the same cell reports, the same pixel again sends nothing.
* **Single-slot selection** (xterm `extend_coords`): DECSET 1016 replaces 1005/1006/1015 and they replace it; DECRST of 1016 clears it only when it is the active encoding. Every dispatch is preceded by a child
  DECRQM proof of the encoding and tracking state (`?1005`, `?1006`, `?1015`, `?1016` and the tracking selectors).
* Tracking selectors stay independent of the encoding: X10 (`?9`) press-only tracking never carries modifier bits.

Documented policy (never deciding): pixels are clamped to the text area (xterm does not clamp); the mux text client never mirrors `?1016` outward and withholds mouse tracking while it is selected (the
session's `CSI 14 t`/`16 t` metrics are not the outer terminal's pixels; with several attached clients of different metrics the daemon's replies follow the latest client while each App reports its own view's pixels).

## Plan (input plan v5)

v5 keeps every v4 event verbatim (their modes only gain `mousePixel:false`), so the routed-input, modifyOtherKeys and extended-mouse layers keep re-deriving from the same journal, and appends 32 events: a selector-only
event (the pinned state), legacy and SGR cell-coordinate controls, then 30 required experiments: presses at five pixel positions (the first pixel, a small one, a mid-cell one, one beyond 223 and the
last admitted pixel), each button, shift/alt/control/all modifiers, two releases, wheel up/down, held-button motion (same pixel position, the adjacent pixel in the same cell, the next pixel row), any-event hover, the unchanged
pixel under unchanged any-event tracking (no bytes), X10 press-only with all modifiers, and the single-slot transitions (1005/1006/1015 then 1016, 1016 then 1006/1015/1005, reset of an inactive and of the active
encoding). Resets use `ESC [ ? 1;9;1000;1002;1003;1006;1005;1015;1016 l`. The plan needs a grid of at least 97 columns and 24 rows and assumes at least 4 × 8 device pixels per cell (the largest planned pixel is 387, 191);
a smaller grid is refused as typed unavailability, a smaller cell would abort the run when the App refuses an out-of-area pixel. Budgets: 196 events, 394 raw captures (budget 512), per-reader snapshots 199 (budget 256); the later
query suite owes 424 earlier challenges.

## Result

| Case | Pinned (unchanged terminfo.dev@4.0.0 code) | Effect contract | Stock grade (this run) |
|---|---|---|---|
| `input.pixel-mouse` | **inconclusive** (`insufficient-evidence`, evidence `legacy`) | `ntilde-sgr-pixel-mouse-1016-effect-v1`: **supported** (divergence `pinned-inconclusive-by-design`) | inconclusive |

The stock collector in the same owned run (`a3651fd70aacde5e31ec2707420d7589`) grades **93 supported / 1 unsupported / 161 inconclusive** (`suiteComplete=false`), unchanged from the preceding batch: `mouseInputProbe`
validates no generated event on either path, so the production change cannot move a stock grade. Inventory: one more ID with a supported effect contract and an inconclusive pinned verdict (no pinned-supported ID and no Core point is added).

## What the effect contract decides

One owned input plan (v5, 196 generated events, all `passed`; 32 are this layer's) ran in one disposable direct-local pane of 140×40 cells at 2× render scaling (device cells of about 17 × 37 pixels). The child's raw stdin is read by an
independent reader and each report is compared with a literal expectation that equals the independent oracle (`oracleHex === expectedHex === actualHex` for all 32 experiments). All events are Avalonia-routed
(`appFacts.boundary = avalonia-routed`, `eventDispatched = true`).

- **`input.pixel-mouse` (32 experiments: 30 required, 2 controls; all matched).**
  A lone `?1016h` key-up proves the state and sends nothing. Controls: legacy tracking at cell 96,3 is `1b 5b 4d 20 80 23` and SGR at the same cell is `ESC [ < 0 ; 96 ; 3 M`. With 1016 on, a left press at pixel (0,0) is
  `ESC [ < 0 ; 1 ; 1 M`, at (3,11) `0;4;12`, at (143,57) `0;144;58` (middle `1`, right `2`, shift `4`, alt `8`, control `16`, all three `28`), at (361,171) `0;362;172` and at (387,191) `0;388;192`, past the 223 limit of the
  legacy form. Release is `0;144;58m` (right `2;…m`); wheel up/down are `64` and `65`; held-button motion (1002) at (143,57) is `32;144;58`, at the adjacent pixel (144,57), still the same cell, `32;145;58`, and one row down
  `32;145;59`; hover (1003) at the first pixel is `35;1;1` and the same pixel again produced no bytes; X10 press-only tracking with modifiers 7 is `0;144;58`. Single slot: `?1005h`, `?1006h` or `?1015h` followed by `?1016h`
  reports pixels (the earlier encoding was replaced), `?1016h` followed by `?1006h`, `?1015h` or `?1005h` reports that encoding at cell 96,3 (`<0;96;3M`, `32;96;3M`, `ESC [ M 20 c2 80 23`), a `?1016l` while SGR is active leaves
  SGR in force, and a `?1016l` while pixels are active returns to legacy bytes.
  The App also reported the cell it derived for each pixel (retained per experiment as `pointer`): pixel (143,57) is cell 9,2, (361,171) is 22,5 and (387,191) is 23,6.

## Divergences and policy that never decided

- Pinned versus effect: the pinned code can only return inconclusive on either path; the effect contract's `supported` is recorded as the divergence `pinned-inconclusive-by-design`.
- Clamping: production clamps a pointer position to the text area; xterm reports whatever the pointer position is. Covered by unit tests, not measured (a generated pixel outside the text area is refused).
- The App's pixel unit is device pixels, exactly as `CSI 14 t`/`CSI 16 t` (a float cell size rounded to an integer), so far from the origin a cell index computed from the rounded `CSI 16 t` size can differ from the App's own by a
  fraction of a cell. The run does not measure another terminal's pixel geometry.
- DECRQM answers `1`/`2`, RIS clears the encoding, DECSTR keeps it, XTSAVE/XTRESTORE and state transfer carry it. These are covered by VT/App unit tests (see [remaining-protocol-variants.md](remaining-protocol-variants.md));
  this run does not reset the terminal mid-suite, so RIS/DECSTR are not measured here.

## Not claimed

Generated Avalonia events are not physical pointer input; the declared pixels are the App's device-pixel unit and not a check against any other terminal; App admission is not child receipt; delivery in a direct-local pane does not
qualify SSH, mux or Windows (the mux text client deliberately does not mirror `?1016`); one 140×40 grid at one scale only; no Windows/Linux; highlight tracking (1001) is out of scope; no stock, Core or common-harness score changes.
The 196-event routed-input journal also re-verifies the earlier input layers.

## Identities

App 0.14.0 (operator supplied) built from `9972a6fcf7649ac66140da7354b82020bc10b131` (arm64 Release on macOS; sidecar `artifacts/sidecar-pxm`; executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`,
assembly SHA-256 `18116b0879528073a9b4256374ef890406217a37dad32b22f43cfd3bff5f84ce`); measured scripts `9972a6fcf7649ac66140da7354b82020bc10b131`; source policy `readonly-owned-source-graph-v61` (407 files; manifest SHA-256
`3b8aed244f24f3e81d5c21f19c37e5fbef0afa38974a39fba4c01a44c72f5265`); query policy `owned-exact-query-paired-raw-state-v5`; receipt schema 67; Node v24.22.2; owned run `b45702ad11ea1f0ea46eeb4908206155`; collected 2026-11-16T10:33Z to 10:38Z.
The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the retained bytes.

The final run is [owned-run.zip](2026-11-16-sgr-pixel-mouse/owned-run.zip) (the first measurement on the final scripts and App; no attempt was superseded);
`node --test scripts/terminfo/pxm-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained input journal, action log, ownership seal and worker run log, and compares the published projection.

## Reproduce

Source policy v61 (407 captured files), receipt schema 67. The collection is `node scripts/terminfo-owned.mjs --executable <sidecar>/Ntilde --app-version <version>` under the shared desktop lock
(see [QA_TERMINFO.md](../QA_TERMINFO.md)); rederive with `completeOwnedReceipt` over a copy of the retained run minus `cell-receipt.json`.
