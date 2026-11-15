# Xterm color-stack qualification: XTPUSHCOLORS and XTPOPCOLORS (2026-11-15)

Qualifies two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `modes.xtpushcolors` (`CSI # P`) and `modes.xtpopcolors` (`CSI # Q`). **No production code changed
in this batch**: the App's xterm palette stack came with the [device-query batch](2026-11-15-device-queries-qualification.md) (that batch measured it through the `CSI # R` depth report and the background plus
palette entry 5; this one is a separate layer that never asks for the depth report).

## What the pinned probes can say

Both pinned probes return `pass: false` and an **inconclusive** outcome on every path; neither can ever return supported or unsupported.

| Case | Pinned termless path | Pinned terminal path |
|---|---|---|
| `modes.xtpushcolors` | `feedCapture(CSI # P)` (any output is `invalid-reply`), `feedCapture(CSI c)`, `feed(CSI # Q)`; then inconclusive `insufficient-evidence` (evidence `consumed`) with the DA1 reply | `write(CSI # P)`, cursor-position query, `write(CSI # Q)`; inconclusive `insufficient-evidence`, or `no-response` without a position |
| `modes.xtpopcolors` | `feed(CSI # P)`, `feedCapture(CSI # Q)` (any output is `invalid-reply`), `feedCapture(CSI c)`; same verdict | `write(CSI # P)`, `write(CSI # Q)`, cursor-position query; same verdicts |

The pinned verdict therefore stays **inconclusive** and is the unchanged pinned code's own result over the journaled raw bytes of its own fenced exchange (the exchange's DA barrier is the pinned `CSI c`
reply) and the cursor of its read. The graded conclusion belongs to a separately identified effect contract; the difference is recorded as the divergence `pinned-inconclusive-effect-supported`, never absorbed.

## Result

| Case | Pinned (termless and terminal paths) | Effect contract (`ntilde-xterm-color-stack-effect-v1`) | Stock grade (this run) |
|---|---|---|---|
| `modes.xtpushcolors` | **inconclusive** (`insufficient-evidence`) | **supported** (divergence `pinned-inconclusive-effect-supported`) | inconclusive |
| `modes.xtpopcolors` | **inconclusive** (`insufficient-evidence`) | **supported** (divergence `pinned-inconclusive-effect-supported`) | inconclusive |

The stock collector in the same owned run (`08eb79bfd90d964a2a0f99b696dd3082`) grades 91 supported / 1 unsupported / 163 inconclusive (`suiteComplete=false`), exactly the device-query batch's counts: no stock grade
moves. The frozen stock baseline keeps its original grades. No Core point is added. Inventory: two more IDs with a supported effect contract and an inconclusive pinned verdict; no pinned-supported ID is added.

## What the effect contract decides

The oracle is written from xterm's control-sequence text (ctlseqs, patch 411): XTPUSHCOLORS "push[es] current dynamic- and ANSI-palette colors onto stack", parameters 1 through 10 "store the palette into the
stack without pushing"; XTPOPCOLORS "pop[s] stack to set dynamic- and ANSI-palette colors", parameters 1 through 10 "restore the palette from the stack without popping". Only that documented behavior decides
(`protocol` groups). The text names neither the dynamic colors, the slot count, an empty pop nor the report bytes, so those are `policy` groups: recorded, never deciding. `control` groups prove each setup took effect
(a failed control is inconclusive, never a verdict). The contract model was also checked against the C# VT on every group's exact bytes before any desktop run (0 mismatches).

Observers are fenced raw OSC replies (`OSC 10 / 11 / 12 ; ? ST`, `OSC 4 ; 5 ; ? ST`, and `OSC 4 ; 200 ; ? ST` for the policy group): each exchange retains every reply, or its explicit absence before the DA
barrier, byte for byte; a request is at most 64 bytes (the fence limit), so color sets are separate uncaptured feeds. Every exchange must leave the complete screen, cursor, rendition, margins, modes and active
screen unchanged. Distinguishable color sets A, B and C cover the foreground, background, cursor and ANSI entry 5; the host default is read first and is the restoration oracle for the pushed untouched colors.

- **`modes.xtpushcolors` (15 groups).** the exact pinned push is silent and the pinned pop after it changes nothing; the colors read back as the baseline; a push is silent and changes no current color; a pop returns the
  pushed set; a snapshot is a copy (two later changes do not alter it); numbered stores in slots 2 and 1 are silent, leave the current colors alone and are restored independently of order; policy: entry 200.
- **`modes.xtpopcolors` (16 groups).** the exact pinned push then pop is silent and changes nothing; a pop restores the pushed untouched host default (the baseline replies, not the colors in force); LIFO pops of
  sets B then A; a numbered restore of slot 1 without a prior unnumbered push; policy: a pop with nothing stored below changes no color, and entry 200.

All protocol and control groups matched, both policy groups matched Ntilde's behavior (entry 200 is saved and restored; an empty pop is ignored), and every exchange left the state unchanged. Each case ends with an
explicit colour reset (`OSC 104/110/111/112`); the start and finish screens and colour bindings are compared.

## Not claimed

Only the foreground, background, cursor and ANSI palette entry 5 decide; no other dynamic color (pointer, highlight, Tek) and no rendering or painted-pixel claim. The `CSI # R` depth report is not an observer here and
the kitty color stack (`OSC 30001/30101`) is a different spelling, not measured. The xterm slot contents and deepest counter cannot be reset without RIS: every sequence here is balanced (numbered stores use slots 1 and 2
and are restored to depth 0), so they end at depth 0. xterm's behavior beyond its text (empty pop, extended palette entries, slot bookkeeping) is not asserted. No remote/mux, persistent-session, Windows/Linux or
common-harness claim.

## Identities

App 0.14.0 (operator supplied) built from `fb6c7fd6b81b4e231afb86b500fbef26354100fd` (arm64 Release; the App sources equal the device-query batch's, executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `265bedc5075b4a56b39fe7d2a4b9ed3e35e527d4f9b207c86a240047bf0f589f`); measured scripts
`c03aab163b042abefb25abaf399282785eed2117`; source policy `readonly-owned-source-graph-v56` (374 files; manifest SHA-256 `5f2d0eb42a76f307763ded296bbd1cc0392278abd0621ae5cf7983be181f4b30`); query policy
`owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `aeee6fe94555eea7e4eb90cfad035b46bec7854f01932715d3ad3e3c8d8bdb50`; receipt schema 62; Node v24.22.2; owned run
`f2b775acf7b7faaed7c3b9e20c8ce4fc`; collected 2026-11-15T13:14Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes.

## Retained attempts ([superseded/](2026-11-15-xterm-color-stack/superseded/))

1. Collected every suite including this one (the xcs suite itself took about a second), then the whole collection hit the runner's fixed 5-minute budget (`Owned collection exceeded 5 minutes.`, at 5:00 after the
   routed-input suite) before the later stages finished. The same committed scripts and sidecar were run again unchanged.
2. Completed with both effect contracts supported and the same pinned verdicts, measured at `da8b8dccd02ce975fc140d66ec89e2e8806aa6e3` (owned run `fbfbd6e79aaf59c5178ebbf5c4a30bec`); superseded because the local Codex
   review of that head found two defects in measured files: the pinned-replay call-count check refused the pinned termless probe's valid early `invalid-reply` return, and the exchange parser threw on unexpected non-OSC
   output of a stack operation instead of retaining it. Both are fixed (and unit-tested, the early return also against the real pinned bundle) and the suite was remeasured.

The final run is [owned-run.zip](2026-11-15-xterm-color-stack/owned-run.zip); `node --test scripts/terminfo/xcs-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained
journal and compares the published projection.
