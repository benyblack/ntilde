# DECSCLM (smooth scrolling) qualification (2026-11-25, #1001)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `modes.decsclm`.
**No terminal behavior changed.** Ntilde ignores DEC private mode 4 by policy ([`DecsclmPolicyTests`](../../../tests/Ntilde.VT.Tests/DecsclmPolicyTests.cs), [coverage matrix](../../vt_coverage_matrix.md)): `CSI ? 4 h/l` changes nothing,
`CSI ? 4 $ p` answers "not recognized", and output scrolls at full speed. That policy said "no scroll timing has been measured"; this measures it on the real App, as elapsed-time scroll captures with a mode-off
comparison, a stationary control and an instrument control. ANSI mode 4 is insert mode (IRM), and the wheel-scroll `SmoothScrolling` setting is a different feature; neither is measured or changed here.

## What the protocol is, and what the pinned probe sends

DEC's DECSCLM (`CSI ? 4 h`) selects smooth scrolling: terminal output that scrolls the screen is paced at about six lines per second; `CSI ? 4 l` is jump scrolling, as fast as the output arrives.
The case is registered in the bundle as `decrpmModeProbe("modes.decsclm", 4, termlessFn)`:

| Path | What it does | What it can return |
|---|---|---|
| termless | feeds `CSI ? 4 h`, captures the `CSI ? 4 $ p` reply, feeds `CSI ? 4 l`, then always returns `parserStateResult(null, …, { response })` | **always inconclusive** (`insufficient-evidence`, evidence `parser-state`: "DECRPM state does not measure scroll timing") |
| terminal | `decrpmResult(await ctx.queryMode(4), 4)` over the DA-sentinel query `CSI ? 4 $ p` | supported only for a recognised set/reset state; **inconclusive** for Pm 0 ("not recognized; mode behavior unmeasured") or no reply |

Neither path measures any timing: a DECRPM acknowledgement alone would score without behavior. This qualification never promotes or demotes the pinned verdict; a terminal answering `Pm 1/2` would make the pinned terminal path
pass without measuring anything, which the owned replay refuses to serve and the effect contract marks inconclusive.

## Result

| Case | Pinned termless (replayed byte-exactly over the journaled exchange) | Pinned terminal (stock path) | Effect contract | Stock grade |
|---|---|---|---|---|
| `modes.decsclm` | inconclusive (`insufficient-evidence`, `parser-state`) | inconclusive (`insufficient-evidence`, `query`: mode 4 not recognized) | **unsupported** (`ntilde-decsclm-unsupported-effect-v1`) | inconclusive |

Recorded separately in [report.json](2026-11-25-decsclm/report.json) as a divergence of kind `pinned-inconclusive-effect-unsupported`; neither claim overwrites the other. The stock collector, re-run in the same owned run
(`2f1fd55bc310836bb65d0fa51f0bc450`; stock collector run `9283eacf407b365f75fb92d84c8a30a3`), still grades the case inconclusive (97 supported / 1 unsupported / 157 inconclusive, `suiteComplete=false`):
**no stock score, no Core point and no common-harness score is added.**

## What the App did (disposable direct-local pane, 40×140)

Every group starts from the same fixture: the margin band (rows 2–6) seeded with attributed cells, the cursor away from home over a plain witness character. A *burst* is the cursor on the bottom margin row followed by
forty line feeds, each scrolling the band by one line. Every exchange ends by rewriting the witness at the cursor, so a pen, truecolor or the ANSI insert mode left changed shows in the stored cell. Every read is a paired
complete screen, cursor, rendition, margin and mode snapshot taken after a completed DA fence barrier, with the raw replies and the **parent's own timing**: whole milliseconds on the parent's monotonic clock from the end of
the previous journaled observation to the arrival of this read request. That interval spans the fenced exchange and the reseed before it, so it is an upper bound of what the terminal needed; the worker cannot shorten it.
The grading oracle is independent: literals written from DEC's contract and Ntilde's DECRPM answers, not read from the parser.

| Group | Role | Bytes (then the witness `W CUB`) | Demand | Elapsed |
|---|---|---|---|---|
| `recognized` | control | `?7$p ?2$p` | DECAWM and DECANM answered set, so "not recognized" is a real answer | 6 ms |
| `pinned` | protocol | `?4h ?4$p ?4l` (the exact pinned termless exchange) | `?4;0$y`; nothing else changes | 6 ms |
| `pinned-query` | protocol | `?4$p` (the exact pinned terminal-path query) | `?4;0$y` | 6 ms |
| `set` / `reset` | protocol | `?4h ?4$p` / `?4l ?4$p` | stationary: no screen, cursor, rendition, margin or mode change; `?4;0$y` | 6 / 5 ms |
| `burst-off` | control | `CUP 6;1`, `?4l`, 40 × LF, `?4$p` | the reference jump-scroll burst: band blank but the witness on the bottom margin row, cursor there, `?4;0$y` | 6 ms |
| `burst-set` | protocol | the same with `?4h` | the identical state, inside the bound | 7 ms |
| `burst-held` | protocol | the same without any mode bytes, after `burst-set` (no reseed between, so the mode is still set) | the identical state, inside the bound | 6 ms |
| `burst-reset` | protocol | the same with `?4l` | the identical state, inside the bound | 5 ms |
| `restored` | protocol | `?4l ?4$p` | nothing changed | 8 ms |
| `paced` | instrument control | the `?4h` burst, then the worker holds 2.5 s | the same timing field must reach the hold (it exceeds the bound) | 2508 ms |
| `control-clear` | control | `ED 2`, `CUP 1;1` | the state reader sees the clear and the home | 6 ms |

The bound is 2 s: forty line feeds at DEC's six lines per second need 6.7 s, so pacing at or below that rate cannot hide inside it. Every ordinary exchange took 5–8 ms, and the instrument control read 2508 ms for its 2500 ms
hold, so the same field sees a slow exchange. Every group held, controls included. The retained run binds the seed to the host binding and the snapshot edges to it, so a run in which the seed did not take, a palette changed or
the restore failed is refused instead of graded.

## Limits

- **What the timing proves.** The effect claim is that the *processing* of scrolling output is not paced: the fenced exchange (which includes the DA reply after the burst) finished in milliseconds with the mode set, held and reset, and
  the stored state read afterwards is the final scrolled state. It does not prove that nothing animates: a terminal that applied the state at once but animated its drawing separately would still pass, and so would the wheel
  `SmoothScrolling` animation, which this does not measure. No rendered frame, repaint cadence or painted pixels were read.
- Wall-clock timing is noisy: a run whose ordinary exchanges miss the bound is graded inconclusive, never unsupported, and is measured again. This run is its first attempt; none is superseded.
- The instrument control holds the worker, not the terminal; it shows the reader can see a slow interval, not that a paced terminal would produce one.
- Raw replies, stored terminal state and parent timing only. Direct-local macOS pane only: no remote/mux, Windows/Linux or common-harness claim. Ignoring the mode is Ntilde's recorded policy (#1001: a byte stream does not
  throttle the terminal), not a DEC-conformance claim; an implementation would need an output-rate contract with a cap and a user override and its own supported contract.
- **Source graph.** This suite extends the integrated source graph (policy v80, 494 files, receipt schema 82). The earlier focused Modern measurements (blink, viewport, OSC titles, OSC 7 cwd, child environment; policies
  v75–v79) are frozen on the v74 graph: their published evidence is unaffected, but they cannot be re-collected from a tree that contains this suite without new fork policies.

## Reproduction

`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope osc633` against the Release build of commit `7394fe6d7…` (`appRevision` and `scriptsRevision` in [summary.json](2026-11-25-decsclm/summary.json)).
`scripts/terminfo/dsl-publication.test.mjs` extracts the retained [owned-run.zip](2026-11-25-decsclm/owned-run.zip), reissues the complete receipt with the trusted current independent graders, rebuilds the report byte for byte from
the retained journal and worker run log, and projects the summary and report; it also refuses copies whose timing or effect verdict was changed. Receipt schema 82 and source policy v80 bind it.
