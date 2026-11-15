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

The stock collector in the same owned run (`8cb42b4de5030a9383471a6a9b5f11eb`) grades 91 supported / 1 unsupported / 163 inconclusive (`suiteComplete=false`), exactly the device-query batch's counts: no stock grade
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

App 0.14.0 (operator supplied) built from `7ba6fff36c36c1e4b805ad252c805835cfb0c42e` (arm64 Release, main after PR 966; the last commit that changed the App sources; executable SHA-256
`f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256 `3b1ccd56d690d9123a5a6b875788b6d48f0972fb0a26546f3e667537dcb72673`); measured scripts
`74bf9d5e00b41a71a72bced25fe85952ea1fc5b4`; source policy `readonly-owned-source-graph-v56` (374 files; manifest SHA-256 `c60af4e766d1b1577b3a91133301de25139bc5f5c1e7992c869cbd65729e3e4a`); query policy
`owned-exact-query-paired-raw-state-v4`; initial settings SHA-256 `7c8bd312715f015215a3909fecbce74bfa8b943d3abb6cd0b4d5560439fb7fdb`; receipt schema 62; Node v24.22.2; owned run
`e9d9cfc12b3bb5e163128fe7f792cc95`; collected 2026-11-15T13:37Z on macOS. The build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the
retained bytes.

## Retained attempts ([superseded/](2026-11-15-xterm-color-stack/superseded/))

1. Collected every suite including this one (the xcs suite itself took about a second), then the whole collection hit the runner's fixed 5-minute budget (`Owned collection exceeded 5 minutes.`, at 5:00 after the
   routed-input suite) before the later stages finished. The same committed scripts and sidecar were run again unchanged.
2. Completed with both effect contracts supported and the same pinned verdicts, measured at `da8b8dccd02ce975fc140d66ec89e2e8806aa6e3` (owned run `fbfbd6e79aaf59c5178ebbf5c4a30bec`); superseded because the local Codex
   review of that head found two defects in measured files: the pinned-replay call-count check refused the pinned termless probe's valid early `invalid-reply` return, and the exchange parser threw on unexpected non-OSC
   output of a stack operation instead of retaining it. Both were fixed (unit-tested, the early return also against the real pinned bundle).
3. Completed with the same verdicts, measured at `c03aab163b042abefb25abaf399282785eed2117` (owned run `f2b775acf7b7faaed7c3b9e20c8ce4fc`); superseded because the next Codex review found two more defects in measured files:
   a BEL-terminated (or never terminated) OSC string before the barrier aborted the parser instead of being retained, and the grader scanned every control before any protocol group, so a protocol failure could be masked
   as a failed setup control by a later control it had damaged. It now accepts BEL, retains an unterminated string as unexpected bytes, and grades the first failure in execution order.
4. Completed with the same verdicts, measured at `fe332981a1b69000eac4d597d99bcafeef00fb59` (owned run `1a46bb891009fc5ea451569741d08afa`) on the device-query batch's head, before PR 966 merged; superseded because the third Codex review
   found two more defects in measured files (a barrier split across reads after an unterminated OSC string aborted the parser instead of waiting, and the empty-pop policy probe could change colors that the next numbered-slot
   control assumed, letting a non-deciding group influence the verdict), and because main then moved: PR 966 merged with Sonar fixes that changed the App sources (the explicit-zero deviation for push/pop) and the device-query
   scripts, so the App was rebuilt and everything remeasured on the merged tree.
5. Failed on the merged tree: `Unexpected input during cell collection.` (the collector's guard against input reaching the disposable App), before the xcs suite finished. No code changed; the same committed scripts and sidecar were run again.

The final run is [owned-run.zip](2026-11-15-xterm-color-stack/owned-run.zip); `node --test scripts/terminfo/xcs-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the retained
journal and compares the published projection.
