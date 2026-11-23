# DECCOLM (80/132 columns) qualification (2026-11-23, #1000, option A)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `modes.deccolm`.
**No terminal behavior changed.** Ntilde ignores DEC private modes 3 (DECCOLM) and 40 (allow 80/132 switching): the App has no program-initiated resize path (pane size comes from layout), so `CSI ? 3 h/l` changes no width,
clears nothing and homes nothing, and DECRQM answers "not recognized" for both modes ([`remaining-protocol-variants.md`](remaining-protocol-variants.md) and the coverage-matrix row). This measures that on the real App,
through raw replies, stored terminal state and **two independent grid readers**. Implementing the width switch is a separate decision (option B of #1000) that this batch does not preclude.

## What the protocol is, and what the pinned probe sends

xterm's DECCOLM (`CSI ? 3 h` for 132 columns, `CSI ? 3 l` for 80) resizes the screen, clears it, homes the cursor and resets the margins, and is honoured only while switching is allowed (`CSI ? 40 h`).
The case is registered in the bundle as `decrpmModeProbe("modes.deccolm", 3, termlessFn)`:

| Path | What it does | What it can return |
|---|---|---|
| termless | feeds `CSI ? 3 h`, captures the `CSI ? 3 $ p` reply, feeds `CSI ? 3 l`; with `$y` in the reply it returns `parserStateResult(null, …, { response, cols })` (else it feeds `X` and reads the text) | **always inconclusive** (`insufficient-evidence`, evidence `parser-state`: "DECRPM state does not establish a width transition") |
| terminal | `decrpmResult(await ctx.queryMode(3), 3)` over the DA-sentinel query `CSI ? 3 $ p` | supported only for a recognised set/reset state; **inconclusive** for Pm 0 ("not recognized; mode behavior unmeasured") or no reply |

Neither path measures a width: a DECRPM acknowledgement alone would score without behavior, which is exactly what teaching DECRQM about mode 3 without a width transition would do. This qualification therefore never
promotes or demotes the pinned verdict. A terminal that answered `Pm 1/2` for mode 3 would make the pinned terminal path pass without measuring anything; the owned replay refuses to serve that, and the effect contract marks such
a run inconclusive, never unsupported.

## Result

| Case | Pinned termless (replayed byte-exactly over the journaled exchange) | Pinned terminal (stock path) | Effect contract | Stock grade |
|---|---|---|---|---|
| `modes.deccolm` | inconclusive (`insufficient-evidence`, `parser-state`) | inconclusive (`insufficient-evidence`, `query`: mode 3 not recognized) | **unsupported** (`ntilde-deccolm-unsupported-effect-v1`) | inconclusive |

Recorded separately in [report.json](2026-11-23-deccolm/report.json) as a divergence of kind `pinned-inconclusive-effect-unsupported`: the pinned verdict is inconclusive and the independent effect contract is unsupported.
They are different claims; neither overwrites the other.

The stock collector, re-run in the same owned run (`31af6e012dd85f4e939799d78fbb20da`; stock collector run `d125d1e3f9cd2c627733e589db5ea225`), still grades the case inconclusive
(97 supported / 1 unsupported / 157 inconclusive, `suiteComplete=false`): **no stock score, no Core point and no common-harness score is added.**

## What the App did (disposable direct-local pane, 40×140)

The seed places the cursor away from home (row 3, column 6) over a plain witness character, inside the margins (rows 2–6) beside an attributed seed. The baseline width (140) is neither 80 nor 132, so a transition in either
direction would be visible. Every exchange ends by rewriting the witness at the cursor and stepping back onto it, so a pen or insert mode left changed by a mode sequence shows in the stored cell. Every read is a paired
complete screen, cursor, rendition, margin and mode snapshot taken after a completed DA fence barrier, the raw replies, and the kernel PTY grid read by the parent with the system `/bin/stty` against the proved private PTY.
The grading oracle is independent: literals written from xterm's contract and Ntilde's DECRPM answers, not read from the parser.

| Group | Role | Bytes (then the witness `W CUB`) | Demand | Observed |
|---|---|---|---|---|
| `recognized` | control | `?7$p ?2$p` | DECAWM and DECANM answered set, so "not recognized" is a real answer | `?7;1`, `?2;1` |
| `pinned` | protocol | `?3h ?3$p ?3l` (the exact pinned termless exchange) | `?3;0$y`; nothing else changes | held |
| `pinned-query` | protocol | `?3$p` (the exact pinned terminal-path query) | `?3;0$y` | held |
| `set` | protocol | `?3h ?3$p` | no grid, screen, cursor, margin or mode change; `?3;0$y` | held |
| `reset` | protocol | `?3l ?3$p` | the same | held |
| `allow` | protocol | `?40h ?3$p ?40$p` | `?3;0$y`, `?40;0$y` | held |
| `allow-set` | protocol | `?40h ?3h ?3$p` | the same as `set`, with switching allowed | held |
| `allow-reset` | protocol | `?40h ?3l ?3$p` | the same as `reset`, with switching allowed | held |
| `allow-cycle` | protocol | `?40h ?3h ?3l ?3h ?3l ?3$p` | the same after a full cycle | held |
| `disallow-set` | protocol | `?40l ?3h ?3$p` | the same with switching not allowed | held |
| `restored` | protocol | `?3l ?40l ?3$p` | the same after everything | held |
| `control-clear` | control | `ED 2`, `CUP 1;1` | the state reader sees the clear and the home (ink gone, cursor from row 3 to home) | held |

At every group the App grid and the kernel grid were both 40×140, equal to each other and to the baseline. Every group held, controls included. The retained run binds the seed to the host binding and the snapshot edges to the host
binding, so a run in which the seed did not take, a palette changed or the restore failed is refused instead of graded.

## Limits

- **Geometry sensitivity is shown by a live control of the same run.** The DECCOLM groups carry no resize of their own, but the owned run that measured them (`31af6e012dd85f4e939799d78fbb20da`) also ran the owned
  resize suite ([2026-11-03-resize.md](2026-11-03-resize.md)): six parent-selected App resizes, each followed by the App's settled grid and the kernel PTY grid read by the same system `/bin/stty` against the same proved PTY that
  the DECCOLM groups use. In the width round trip both readers moved together from 40×140 to 40×133 and both returned to 40×140 (the height and combined round trips likewise), and the receipt of the run rederives all three
  comparisons as passed. The publication projects those six actions into [summary.json](2026-11-23-deccolm/summary.json) (`geometryControl`) and `dcl-publication.test.mjs` refuses a copy in which a reader disagrees with its
  target, a width never moves, or a restore misses the DECCOLM baseline. So an unchanged grid in the DECCOLM groups is an observation these readers demonstrably can make, on this App build, in this run. The control is the
  resize suite's, not a step inside the DECCOLM plan: the DECCOLM contract header (a measured source, so left as measured) still describes the equivalence to the 2026-11-03 measurement. An App grid that did change would not even be
  gradable: the observer's fixed-size guard and the journal refuse a frame of another grid, so the run would be refused rather than judged. The kernel grid changing alone is a graded disagreement.
- A transition completed after a barrier would still be caught: every later group re-reads both grids, and the last group runs after all of them.
- Raw replies, stored terminal state and PTY geometry only: not a window-manager size, rendering or painted pixels. Direct-local macOS pane only: no remote/mux, Windows/Linux or common-harness claim. Ignoring the modes is Ntilde's
  recorded policy (the App cannot resize its own pane from a program), not an xterm-conformance claim; an xterm-style implementation would need its own supported contract.

## Reproduction

`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope osc633` against the Release build of App commit `cf5d07ed3…` (`appRevision` in [summary.json](2026-11-23-deccolm/summary.json); the scripts measured are
`scriptsRevision` `3ec330061…`). `scripts/terminfo/dcl-publication.test.mjs` extracts the retained [owned-run.zip](2026-11-23-deccolm/owned-run.zip), reissues the complete receipt with the trusted current independent graders,
rebuilds the report byte for byte from the retained journal and worker run log, and projects the summary and report. The first attempt passed, so no run is superseded. Receipt schema 79 and source policy v72 (483 files) bind it. The same-run resize control was added to the publication afterwards, without a new measurement.
