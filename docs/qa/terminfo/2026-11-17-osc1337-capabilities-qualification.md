# iTerm2 feature-reporting qualification: OSC 1337 RequestCapabilities (2026-11-17)

Qualifies one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc1337-capabilities`, whose exact request is
`OSC 1337 ; RequestCapabilities BEL`. Like the [rxvt special-color batch](2026-11-17-osc5-special-colors-qualification.md), **this batch adds production code**: main did not answer the request (the
[#1003 policy audit](2026-11-17-term-features-policy.md) recorded the undeclaration and its owning regressions), so a qualification had nothing to measure. The VT now answers the iTerm2
feature-reporting query with an evidence-backed declaration ([contract](iterm-feature-reporting.md)), and the batch measures it. The batch is restacked on the merged OSC 5, OSC cell-size,
OSC 3008, OSC 720, OSC 22, Kitty-animation and font-query batches (live source policy v70, receipt schema 77; every earlier publication remains retained).

## What the pinned probe can say

The pinned probe is a **query-only** probe registered through terminfo.dev's `oscSimpleQueryProbe` factory. It sends `OSC 1337 ; RequestCapabilities BEL` and is the one case of this batch whose
verdict the unchanged pinned code can decide:

- termless path: `feedCapture(OSC 1337 ; RequestCapabilities BEL)` over `oscReplyResult`; terminal path: `queryWithSentinelOutcome` with the pattern `ESC ] 1337 ; Capabilities = <characters without
  BEL or ESC> (BEL | ST)`.
- **supported** when the reply is one complete `OSC 1337 ; Capabilities = …` frame ended by BEL or ST; **inconclusive** (`invalid-reply`) when the capture holds the `Capabilities=` prefix without a
  complete frame; **inconclusive** (`no-response`) when nothing arrives. It can never return unsupported — **and its valid pattern accepts any feature string**, so the pinned verdict alone never
  establishes which capabilities are honestly advertised. The declaration's truthfulness is the effect contract's question.

Both pinned paths are replayed byte-exactly over the journaled raw reply of the case's own fenced exchange. The pinned verdict is also the stock collector's grade in the same owned run and is
corroborated by the exact-query witness `q07.extensions.osc1337-capabilities` under **query policy v7**, which owes the declared string byte for byte.

## Result

| Case | Pinned termless / terminal paths | Stock grade (this run) | Exact-query witness | Effect contract (`ntilde-osc1337-capabilities-effect-v1`) |
|---|---|---|---|---|
| `extensions.osc1337-capabilities` | **supported** / **supported** | **supported** (was inconclusive: no reply) | matched the exact declared reply, byte for byte | **supported** (4 protocol groups matched; 4 policy groups silent as recorded) |

The effect contract demands the specification-framed reply exactly: `ESC ] 1337 ; Capabilities = T3CwLrMSc7UTs3BFGsGoSyHNoP ST`, transcribed from the [specification's code table](iterm-feature-reporting.md)
over this terminal's implemented, tested capabilities — an independent literal, never read from the parser's constants. The `Cw` code is declared because the measured run's direct pane accepts OSC 52
writes under its default initial settings; a host that cannot answer omits it (policy, unit-tested, not measured here). Every exchange left the screen, cursor, rendition, margins, modes and active
screen unchanged, and the silent forms (an unsolicited `Capabilities=…` assignment, a bare or query `Capabilities`, a suffixed request, both wrong-case spellings) stayed silent with the exact request
still answered after them.

The stock collector in owned run `96792c065a93b2b1def8fce8b2e6d9ab` grades 97 supported / 1 unsupported / 157 inconclusive (`suiteComplete=false`), 255 graded cases — the two font-query ids the merged #1033 batch added are supported in the same run. `extensions.osc1337-capabilities`
is the one case this batch newly moves from inconclusive to supported; the frozen November 9 baseline and every other stock grade stay unchanged. No Core point is added.

## Production

The VT answers only the exact payload `1337;RequestCapabilities` ([parser](../../../src/Ntilde.VT/AnsiParser.CapabilityReport.cs), [unit tests](../../../tests/Ntilde.VT.Tests/ITerm2CapabilitiesReplyTests.cs),
[catalog contract](../../../tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs)): one ST-terminated reply, no state change. The declared string is a fixed literal per host-acceptance state — there is no
generic reply machinery to satisfy a collector. Unsolicited `Capabilities` forms keep their [#1003](2026-11-17-term-features-policy.md) silence regression; the image (`1337;File=`, multipart) and
cell-size (`1337;ReportCellSize`) forms keep their own handlers. The environment-variable half of the protocol stays undeclared: the native PTY layer still never writes `TERM_FEATURES` (the same
source-scan regression holds), so the `device.term-features` disposition is unchanged.

## What was measured (disposable direct-local pane, alternate screen)

The case runs on the shared device-query seed (a nonce row, bold and indexed-red text, a curly-underlined cell, margins 2..6) at 40×140. Each group is one fenced exchange: the exact request bytes,
then every raw reply (or its explicit absence) retained byte for byte before the DA barrier, journaled by the parent beside a complete state read. Groups: the pinned request; the same request ended by
ST; two requests answered by two identical replies in request order; the four silence groups; and the exact request answered again after them.

## Not claimed

The declaration advertises implemented capabilities; advertisement is not a per-invocation behavioral guarantee — each code's behavior continues to be owned by its own sequence implementations and
tests ([iterm-feature-reporting.md](iterm-feature-reporting.md)). `Aw`, `Uw` and `Sx` stay undeclared (no evidence; measured unsupported). No rendering, painted-pixel, remote/mux, persistent-session,
Windows/Linux or common-harness claim. The pinned probe alone never establishes the declaration's truthfulness — that separation is the effect contract's, and it is why the batch did not stop at the
pinned verdict.

## Superseded attempts

1. Aborted at `caps-start`: the parent's owner-request allowlist did not carry the `caps` prefix (`Unknown owner request`). Fixed; retained at `artifacts/terminfo/owned-WDiLPM` (session-local, not
   committed).
2. Aborted at the first silent-forms exchange: the owned fence refuses exchange requests over 64 bytes and the combined payloads exceeded it. The contract now self-checks the real bound and carries
   four bounded policy groups; retained at `artifacts/terminfo/owned-E8agzr`.
3. Completed every suite (stock 95/159/1, all caps groups matched) but receipt finalization refused: the metadata snapshot policy list, the per-suite retained-source policy lists and the query
   receipt's report rederivation did not carry the new policy/marker. All fixed; retained at `artifacts/terminfo/owned-TUkIzc`. The fixes are checked by the offline receipt fixtures; the retained
   evidence was not repackaged as the publication.
4. Completed every suite on the pre-restack graph (stock 95/159/1, caps supported, witness matched; live source policy v67, schema 74, 452 inputs) and was packaged, then superseded when upstream
   main merged OSC 720 and OSC 22 and the live graph moved to v69/463 inputs; retained at `artifacts/terminfo/owned-wKLg39` (session-local, not committed).
5. Aborted at receipt finalization: resolving the restack conflicts dropped the caps suite's wiring from the owned runner (undefined bundle path); retained at `artifacts/terminfo/owned-iC8o0v`
   (session-local, not committed).
6. Aborted mid-collection: the fence refused stray bytes injected into the PTY from outside the run (`Unexpected input … Bytes: d8a7`); retained at `artifacts/terminfo/owned-jduaQZ` (session-local,
   not committed).
7. Completed every suite but receipt finalization refused: the restacked metadata receipt did not import the new source-graph table (`capturedV76SourceFiles`); retained at
   `artifacts/terminfo/owned-MLbzEo` (session-local, not committed).
8. Completed every suite (caps supported, all groups matched) but the exact-query witness recorded unexpected bytes: resolving the restack conflicts dropped the `capabilitiesReport` flag, so the
   witness demanded silence while the App answered the declared reply. Fixed; retained at `artifacts/terminfo/owned-Gv81Xb` (session-local, not committed).
9. Completed every suite on the pre-font-restack graph (stock 95/159/1, caps supported, witness matched; live source policy v69, schema 76, 463 inputs) and was packaged, then superseded when the
   merged font-query batch (#1033) claimed the same v69/76 slots; the caps batch renumbered to v70/77/469 inputs; retained at `artifacts/terminfo/owned-ZER5Ix` (session-local, not committed).
10. Completed every suite but receipt finalization refused: the per-suite checked-in policy lists did not yet admit the v70 source policy (the static-image receipt refused first). Fixed by the
    same mechanical admission bump every earlier batch applied; retained at `artifacts/terminfo/owned-ivCBrs` (session-local, not committed).

## Identities

App 0.14.0 (operator supplied) built from `6d9cac77500c…` (arm64 Release, executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, app host; the Ntilde.dll payload carries the merged font-query and feature-reporting implementations); measured scripts `6d9cac77500c…`;
source policy `readonly-owned-source-graph-v70`; query policy `owned-exact-query-paired-raw-state-v7`; initial settings include `AllowOsc52ClipboardWrite: true` (the `Cw` gate), `AllowTitleReport: on`
for the title-report suites and `AllowRectangleChecksumReport: on`; receipt schema 77; owned run `96792c065a93b2b1def8fce8b2e6d9ab`; collected 2026-11-18T17:35Z on macOS. The build is not
binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the retained bytes.

## Retained run and reproduction

The retained [archive](2026-11-17-osc1337-capabilities/owned-run.zip) ([manifest](2026-11-17-osc1337-capabilities/manifest.json)), [summary](2026-11-17-osc1337-capabilities/summary.json) and
[report](2026-11-17-osc1337-capabilities/report.json) rederive without the collector:

```sh
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/caps-publication.test.mjs
node scripts/terminfo-inventory.mjs --check
```

The first command extracts the archive, re-issues the receipt, rebuilds the caps report from the retained journal and compares the published projection; the second re-derives the whole registered
inventory (150 ids / 152 claims; 147 effect-supported, 97 pinned-and-effect-supported) and compares it to the committed JSON.
