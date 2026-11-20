# xterm pointer-color qualification (2026-11-20, #991)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc113-reset-pointer-fg` and `extensions.osc114-reset-pointer-bg`.
**No terminal behavior changed.** Ntilde has no handler for OSC 13, OSC 14, OSC 113 or OSC 114 (Unsupported by policy, #991: [`remaining-protocol-variants.md`](remaining-protocol-variants.md), the focused VT policy tests and the coverage-matrix rows): the App draws the operating system's pointer and cannot recolor it, so the four codes are ignored whole. This measures that on the real App,
through raw replies and stored terminal state.

## What the protocol is, and what the pinned probes send

In xterm the dynamic-color OSC family is a chain: `OSC 10 ; a ; b ; …` sets text foreground (10), background (11), cursor (12), **pointer foreground (13)**, **pointer background (14)**, the two Tektronix
colors (15, 16), highlight background (17), the Tektronix cursor (18) and highlight foreground (19), one value per slot. `OSC 110`–`OSC 119` reset the same slots, so **`OSC 113` resets the pointer
foreground and `OSC 114` the pointer background**. A `?` in place of a value asks for the current color. A terminal that ignores 13/14 must still *consume* their slots when a chain passes through them, or a
later slot (17, 19) shifts.

Each case is registered in the bundle as `probe(id, colorResetProbe(setCode, resetCode), terminalAsync)`:

| Path | What it does | What it can return |
|---|---|---|
| termless | `feedCapture` of the query `OSC 13 ; ? BEL` (or 14); with no complete `OSC 13 ; rgb:<r>/<g>/<b>` reply it returns `oscReplyResult(raw, null, …)`; only a complete reply makes it continue (set, query, reset, query) | inconclusive `no-response` (or `invalid-reply` when only the prefix arrived); supported only over a full readback |
| terminal | writes `ESC[1;1H ESC[2K`, `OSC 113 BEL` (or 114), asks the cursor position, returns `unverifiedEffect` | **always inconclusive** (`insufficient-evidence`, evidence `consumed`); the path the stock collector grades |

While the pointer query stays unanswered, neither pinned path can return `unsupported`: the termless function returns inconclusive without a complete reply, and the terminal path is `unverifiedEffect`. The pinned
`colorResetProbe` *can* return `unsupported` after a complete reply when a mutation succeeds but its restoration fails; that branch is not reachable in this suite, because a complete reply to the pinned query makes the replay
refuse the run (it would continue past the unsupported contract) instead of producing a report. This qualification therefore never promotes or demotes the pinned verdict, and a terminal that *did* answer would need its
own supported contract: the contract here marks such a run inconclusive, never `unsupported`.

## Result

| Case | Pinned termless (replayed byte-exactly over the journaled raw reply) | Pinned terminal (stock path) | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.osc113-reset-pointer-fg` | inconclusive (`no-response`) | inconclusive by construction (`consumed`) | **unsupported** (`ntilde-pointer-color-unsupported-effect-v1`) | inconclusive |
| `extensions.osc114-reset-pointer-bg` | inconclusive (`no-response`) | inconclusive by construction (`consumed`) | **unsupported** (`ntilde-pointer-color-unsupported-effect-v1`) | inconclusive |

Recorded separately in [report.json](2026-11-20-pointer-color/report.json) as a divergence of kind `pinned-inconclusive-effect-unsupported` for each case: the pinned verdict is inconclusive and the
independent effect contract is unsupported. They are different claims; neither overwrites the other.

The stock collector, re-run in the same owned run (`a2dc4d0460de690c9514a85e1af5a882`; stock collector run `6de46399efb311127ea110e89b478f67`), still grades both cases inconclusive
(97 supported / 1 unsupported / 157 inconclusive, `suiteComplete=false`): **no stock score, no Core point and no common-harness score is added.**

## What the App did (disposable direct-local pane, 40×140)

The suite seeds five colors with OSC 10, 11, 12, 17 and 19 (`#102030`, `#405060`, `#708090`, `#a0b0c0`, `#d0e0f0`), so a silence is not a dead channel and any leakage of a pointer code into a neighbouring
color would be visible. It then runs fourteen fenced groups per case. Every read is a paired complete screen, cursor, rendition, margin and mode snapshot taken after a completed DA fence barrier, plus the
raw replies. The grading oracle is independent: literals written from the seeded values and the xterm chain order, not read from the parser.

| Group | Role | Bytes (case 1; case 2 uses 14/114) | Demand | Observed |
|---|---|---|---|---|
| `seeded` | control | `OSC 10;? 11;? 12;? 17;? 19;?` | the five colors read back exactly as seeded | held |
| `pinned` | protocol | `OSC 13;? BEL` (the exact pinned query) | never answered | no reply |
| `query-st` | protocol | `OSC 13;? ST` | never answered | no reply |
| `set` | protocol | `OSC 13;rgb:aa/bb/cc BEL` + the five-color query | not answered; the five colors read back as seeded | held |
| `query-after-set` | protocol | `OSC 13;? BEL` | still not answered (nothing was stored) | no reply |
| `set-hex` | protocol | `OSC 13;#aabbcc ST` + the five-color query | not answered; the five colors read back as seeded | held |
| `reset` | protocol | `OSC 113 BEL` (the exact pinned reset) + the five-color query | not answered; the five colors read back as seeded | held |
| `reset-st` | protocol | `OSC 113 ST` + the five-color query | not answered; the five colors read back as seeded | held |
| `reset-argument` | protocol | `OSC 113;1 BEL` + the five-color query | not answered; the five colors read back as seeded | held |
| `colors-after-pointer` | protocol | the five-color query | colors exactly as seeded | held |
| `control-move` | control | `OSC 12;#e0a020 BEL OSC 12;? BEL` | a cursor-color change is read back (the replies follow state) | `rgb:e0e0/a0a0/2020` |
| `chain` | protocol | `OSC 12;#010203;#040506;#070809;#0a0b0c;#0d0e0f;#101112 BEL` | not answered; the pointer slots consume their values | no reply |
| `colors-after-chain` | protocol | the five-color query | 12 and 17 changed, 10, 11, 19 unchanged, 17 did not shift | held |
| `recover` | protocol | `OSC 13;? BEL OSC 12;? BEL OSC 13;? BEL` + the five-color query | the pointer query is still silent, the color read still answers and the colors are still the chained values | held |

Every mutating request carries its own five-color read, so a color a set changed cannot be restored by a later reset before anything looks. Every group of both cases held, controls included. The retained run binds the case reads to the seeded color binding and the snapshot edges to the host binding, so a run in which the seed did not
take, a palette changed or the restore failed is refused instead of graded.

## Limits

Raw replies and stored terminal state, not the operating system pointer image, the OSC 22 pointer shape, rendering or painted pixels. Direct-local macOS pane only: no remote/mux, Windows/Linux or
common-harness claim. Ignoring the codes is Ntilde's recorded policy (the platform pointer is not recolorable from inside the window), not an xterm-conformance claim; an xterm-style implementation would
need its own supported contract.

## Reproduction

`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope osc633` against the Release build of the App commit recorded in
[summary.json](2026-11-20-pointer-color/summary.json) (`appRevision` = `scriptsRevision` = `67675feec…`). `scripts/terminfo/pcl-publication.test.mjs` extracts the retained
[owned-run.zip](2026-11-20-pointer-color/owned-run.zip), reissues the complete receipt with the trusted current independent graders, rebuilds the report byte for byte from the retained journal and
worker run log, and projects the summary and report. One failed attempt (the App exited before collection because the sidecar had not been restored) is retained under
[superseded/attempt-1.zip](2026-11-20-pointer-color/superseded/attempt-1.zip) and described in [attempts.json](2026-11-20-pointer-color/attempts.json).
