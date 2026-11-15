# Xterm SGR-stack qualification: XTPUSHSGR and XTPOPSGR (2026-11-15)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `modes.xtpushsgr` (`CSI # {`) and `modes.xtpopsgr` (`CSI # }`). Unlike the
[color-stack batch](2026-11-15-xterm-color-stack-qualification.md), **this batch adds production code**: main answered neither sequence (both were listed as unsupported in
[remaining-protocol-variants.md](remaining-protocol-variants.md)), so a qualification had nothing to measure. The VT now implements xterm's ten-level video-attribute stack, and the batch measures it.

## What the pinned probes can say

Both pinned probes return `pass: false` and an **inconclusive** outcome on every path; neither can ever return supported or unsupported.

| Case | Pinned termless path | Pinned terminal path |
|---|---|---|
| `modes.xtpushsgr` | `feedCapture(CSI # {)` (any output is `invalid-reply`), `feedCapture(CSI c)`, `feed(CSI # })`; then inconclusive `insufficient-evidence` (evidence `consumed`) with the DA1 reply | `write(CSI # {)`, cursor-position query, `write(CSI # })`; inconclusive `insufficient-evidence` with the position, or `no-response` without one |
| `modes.xtpopsgr` | `feed(CSI # {)`, `feedCapture(CSI # })` (any output is `invalid-reply`), `feedCapture(CSI c)`; same verdict | `write(CSI # {)`, `write(CSI # })`, cursor-position query; same verdicts |

The pinned verdict therefore stays **inconclusive** and is the unchanged pinned code's own result over the journaled raw bytes of its own fenced exchange (the exchange's DA barrier is the pinned `CSI c`
reply) and the cursor of its read. The graded conclusion belongs to a separately identified effect contract; the difference is recorded as the divergence `pinned-inconclusive-effect-supported`, never absorbed.

## Result

| Case | Pinned (termless and terminal paths) | Effect contract (`ntilde-xterm-sgr-stack-effect-v1`) | Stock grade (this run) |
|---|---|---|---|
| `modes.xtpushsgr` | **inconclusive** (`insufficient-evidence`) | **supported** (divergence `pinned-inconclusive-effect-supported`) | inconclusive |
| `modes.xtpopsgr` | **inconclusive** (`insufficient-evidence`) | **supported** (divergence `pinned-inconclusive-effect-supported`) | inconclusive |

The stock collector in the same owned run (`3878cad8139eca384f5e6d24eeb92559`) grades 91 supported / 1 unsupported / 163 inconclusive (`suiteComplete=false`), exactly the color-stack batch's counts: no stock
grade moves. The frozen stock baseline keeps its original grades. No Core point is added. Inventory: two more IDs with a supported effect contract and an inconclusive pinned verdict; no pinned-supported ID is added.

## Production

`CSI Pm # {` (XTPUSHSGR) saves video attributes of the current pen onto a stack of ten levels and `CSI # }` (XTPOPSGR) restores them ([parser](../../../src/Ntilde.VT/AnsiParser.SgrStack.cs),
[stack](../../../src/Ntilde.VT/TerminalBuffer.SgrStack.cs), [unit tests](../../../tests/Ntilde.VT.Tests/SgrStackTests.cs)). It follows xterm's control-sequence text (ctlseqs, patch 411):

- the parameters select what a level saves: 1 bold, 2 faint, 3 italic, 4 underlined, 5 blink, 7 inverse, 8 invisible, 9 crossed-out, 21 doubly-underlined, 30 foreground color, 31 background color; with no
  parameter all of them are saved, and "the stack is limited to 10 levels";
- a pop "restores the video-attributes which were saved using XTPUSHSGR to their previous state": exactly the attributes the level selected come back, every other attribute keeps its current value;
- both sequences are silent. Only the exact spelling is admitted (no leader, the single `#` intermediate, plain parameters; a pop takes none).

Choices the text does not make, taken here and recorded as non-deciding policy: a push onto a full stack is ignored (so an unbalanced program cannot lose its outermost level); a pop with nothing saved is ignored; a
parameter the text does not list selects nothing but still saves a level; the overline, the underline color and DECSCA protection are not part of the stack; the stack is shared by the main and the alternate screen,
RIS clears it, DECSTR leaves it, and state transfer carries it (`sgr_stack`, absent when empty, validated before any state change). The pen has one underline whose style says which of xterm's two flags it
stands for, so parameters 4 (any style but double) and 21 (double) are saved and restored independently; xterm holding both flags at once cannot be represented and resolves to the double underline. The
`CSI Pm # p` and `CSI # q` aliases (spelled for languages that cannot write `#{`) and XTREPORTSGR (`CSI # |`) are different spellings and are not implemented. Two local Codex reviews of the production code found
two defects (a push with more than 32 parameters ignored later selectors; restoring one underline flag also restored the other), both fixed with regression tests before the final measurement.

## What the effect contract decides

The oracle is written from the same text. After each stack operation a probe character is written (the request is the control sequence followed by the character, at most 64 bytes, on its own cell of row 8,
below the scroll region) and the **stored attributes of that cell** are read back from the App: bold, faint, italic, underline style, blink, inverse, hidden, strikethrough, and the stored foreground and
background (default, palette index or literal RGB). `protocol` groups decide a mismatch (unsupported), `control` groups prove each setup took effect (a failed control is inconclusive, never a verdict) and
`policy` groups record a choice above (never deciding). Every exchange must be silent and leave the screen outside the probe row, the cursor position, the cursor visibility and stored mode, the margins, the
wrap/insert modes and the active screen unchanged; probe cells written earlier must survive each later operation exactly as they were observed. Each case ends by draining the stack (ten pops) and resetting the
pen, begins by erasing the probe row, and the whole suite stays inside the owner observer's capture budget (46 frames of 64).

- **`modes.xtpushsgr` (28 groups).** the exact pinned push is silent and the pinned pop after it changes nothing; the baseline cell is neutral before and after; a push changes no attribute of the pen; an
  argument-less push saves every attribute (pen A: bold, faint, italic, single underline, blink, inverse, hidden, strikethrough, palette foreground 196, literal background) and a pop restores all of them after
  pen B (nothing set, literal foreground, palette background) replaced it; a snapshot is a copy (two later pens do not alter it); **each of the eleven parameters alone** restores only its attribute and leaves every
  other one as pen B has it; parameter 4 alone leaves a double underline set since in place and parameter 21 alone leaves a single one; ten pushes all hold (the first pop restores the tenth level and, after
  eight more, the next restores the first level, the bottom of the stack). Policy: two underline-flags-at-once cases, the eleventh push, an unlisted value (99) and the overline and underline color.
- **`modes.xtpopsgr` (13 groups).** the exact pinned push then pop is silent and changes nothing; a pop restores the pushed untouched baseline (no attribute, default colors), not the pen in force; LIFO pops of
  pens B then A; a pop restores only what the push saved (pen C is pushed with parameter 1, pen B is set, and the pop restores C's bold and leaves italic and both colors as B has them). Policy: a pop with
  nothing saved.

All protocol and control groups matched, all six policy groups matched Ntilde's choices, and every exchange was silent and left the state unchanged. The contract's expected cells were also written by hand as a
second oracle in the portable test fixture (the model and the hand-written cells agree for all 41 groups), and the exact bytes of every plan were replayed through the C# VT before any desktop run (0
mismatches in cursor, silence, attributes and colors).

## Not claimed

Stored cell attributes only: no rendering, painted-pixel or host-presentation claim (a hidden, blinking or inverse cell is not shown to be drawn so), no XTREPORTSGR claim, no `CSI Pm # p` / `CSI # q` alias claim, no
claim about the overline, the underline color or DECSCA protection, and no other dynamic state: the kitty color stack and the xterm color stack are different spellings measured by other suites. xterm's behavior beyond
its text (the eleventh push, an empty pop, an unlisted value, RIS or DECSTR) is not asserted. Remote/mux delivery, Windows/Linux and common-harness runs are not covered.

## Identities

App 0.14.0 (operator supplied) built from `d02149e458f9a6dde0adac9211d4fa3984e9a2e8` (arm64 Release; the last commit that changed the App sources; executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `8b3e0cf49c071a57b889e69881fc8ab4453c156dd9535800bceb708925b3ad63`); measured scripts
`52375795351698a609d13c71f173ecc11bab8dbf`; source policy `readonly-owned-source-graph-v57` (381 files; manifest SHA-256 `cec0657a44cae3835fe34e62d8c86c410f1d570ef30042aa0ec0fd4d8953aa16`); query policy
`owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `cdbcf980fa9822ff3ea5016b5b007be0054c2e6d2e3490ae35a3a296f3f56fd6`; receipt schema 63; Node v24.22.2; owned run
`acc8d6b242fbf0c85a8083498888866b`; collected 2026-11-15T16:10Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes.

## Retained attempts ([superseded/](2026-11-15-xterm-sgr-stack/superseded/))

1. Aborted early with `xssRecordingContext is not defined`: the collector re-exported the color-stack suite's recording context without binding the name its own driver calls, and the driver had no unit test. The
   import is fixed and a driver test now replays both plans through a fake fence and owner.
2. Collected every suite including this one (the xss suite itself took about a second), then the whole collection hit the runner's fixed 5-minute budget (`Owned collection exceeded 5 minutes.`, at 5:00 after the
   routed-input suite) before the later stages finished. The same committed scripts and sidecar were run again unchanged.
3. Completed, but the pop case began on a probe row the push case had filled: its seed was (rightly) not proven and its effect contract came out inconclusive, with the push case supported. The per-case reseed now
   erases the probe row, a unit test pins that, and a second test shows stale probe cells leave the seed unproven. Before the next measurement a local Codex review found the two production defects above (and
   nothing in the scripts), the oracle gained four underline groups, and the eleventh-push policy group now drains the stack afterwards so no later demand depends on it.

A fourth start was refused before any App launched because another session held the shared desktop lock; it waited for the release and no lock was touched.

The final run is [owned-run.zip](2026-11-15-xterm-sgr-stack/owned-run.zip); `node --test scripts/terminfo/xss-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained
journal and compares the published projection.
