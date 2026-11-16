# VS Code OSC 633 lifecycle markers A–D, implemented and remeasured (2026-11-16)

Issue #982. This implements the documented [VS Code prompt-start, prompt-end, pre-execution and execution-finished markers](https://code.visualstudio.com/docs/terminal/shell-integration#_supported-escape-sequences)
and remeasures the four exact `terminfo.dev@4.0.0` cases `extensions.osc633-a` through `extensions.osc633-d` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`,
probe hash `16a690f34c0e`). The cases sit outside the frozen Core catalogue, so **no Core percentage changes**. The contract, oracle and evidence boundaries are unchanged and are in
[osc633.md](osc633.md); the earlier measurement of the App *before* this change is retained, unmodified, in [2026-11-16-osc633-qualification.md](2026-11-16-osc633-qualification.md).

## What changed in the App

`OSC 633;A`, `B`, `C` and `D[;exit]` now go through the same handler as `OSC 133;A`–`D`, so they drive the same prompt-ready edge, command-input window, command/output anchors and shell events
(one code path, not a copy). The dialect differs from OSC 133 only where its documented grammar does:

- `C` has no parameters, so nothing after the letter is read as command text (OSC 133;C's base64, plain-text and `cmdline_url` payloads are not applied). The command line is the separate `E` record.
- `D` reads an optional exit code only; OSC 133's trailing duration field is not part of OSC 633.
- `E`, `P` and every other 633 record are ignored: no lifecycle effect, no state.

Owning regressions: `tests/Ntilde.VT.Tests/Osc633LifecycleTests.cs` (events, input window and anchor state, 633-versus-133 equivalence, payload and duration non-reading, ignored records, ST and split
sequences, unchanged OSC 133 behavior) and a pane-level case in `tests/Ntilde.App.Tests/Controls/PaneCommandOutputCaptureTests.cs`.

## Result

| Case | Role | Pinned (live code + parent replay) | Effect contract | Before the change (retained) | Stock grade |
|---|---|---|---|---|---|
| `extensions.osc633-a` | prompt start | inconclusive by design | **supported** | unsupported | inconclusive |
| `extensions.osc633-b` | prompt end, input starts | inconclusive by design | **supported** | unsupported | inconclusive |
| `extensions.osc633-c` | pre-execution | inconclusive by design | **supported** | unsupported | inconclusive |
| `extensions.osc633-d` | execution finished | inconclusive by design | **supported** | unsupported | inconclusive |

The effect contract is the unchanged `ntilde-osc633-shell-lifecycle-effect-v1`, separately identified and never attributed to the pinned probes; the four divergences are recorded as
`pinned-inconclusive-by-design`. The unchanged pinned probes can only establish marker consumption and still return inconclusive on both paths. The stock collector, re-run in the same owned run, still
grades all four inconclusive (161 inconclusive / 93 supported / 1 unsupported, `suiteComplete=false`), so this **does not raise the stock compatibility score**. The 63 paired screen, pane-metadata and
session-status frames passed every validity control (stock screen, prior state, unmarked control, OSC 133 positive control, applicable wrong-marker gates).

## What the App did (disposable direct-local pane, same fixtures as the baseline)

Each case starts from a measured prior state built with OSC 133 (the observer's positive control); OSC 633 is only ever the target.

- **A.** From a running command (anchors (2,8) and (3,0)) the unmarked control stayed running; `633;A` returned the session to the prompt, as `133;A` does. Gates: `B` opened the input window and `C` moved the output anchor,
  both leaving the session running. `D` from that state also returns to the prompt, so that observable is *shared* with A and disclosed, never required. The ST-terminated marker behaved identically.
- **B.** On a prompt the unmarked control opened nothing; `633;B` opened the input window with the command-start anchor at the cursor, (0,0). `A` and `D` opened no window, and `C` put the session running instead (a different transition, with an output anchor).
- **C.** From a measured input phase (window open, anchor (2,8)) the unmarked control changed nothing; `633;C` put the session running, closed the window and set the output anchor to (1,0), with no command identity (C has no payload).
  `A` changed nothing, `B` only moved the start anchor, and `D` closed the window and cleared both anchors.
- **D.** From a measured running command the unmarked control changed nothing; `633;D;0` returned the session to the prompt and dropped both anchors. `B` and `C` left the command running, and `A` (a shared observable) returned to the
  prompt while keeping the anchors, unlike D.

**Exit status (separate observation, not graded).** `D` with no exit code and `D;7` produced the same state as `D;0`. The session status's `exitCode` is the child **process** exit code and stayed null in these experiments; the App's
snapshot and metadata expose no command exit status, so the payload's interpretation is **not observed here**. The `commandFinished` event stream is not read, and PR #709 is neither modified nor depended on. The parser's
exit-code handling for `D;N` is unit-tested (the VT suite above), not desktop-measured.

## Identities

- App: 0.14.0 (operator supplied), Release sidecar built from `9c814b13215b6abf9186b0c4c784e7702b3e1828` (arm64; host stub SHA-256 `f5814e51…9bf8`, assembly SHA-256 `a2e858f0…4fa4`, which differs from the baseline's `e0b149a6…caa9`).
- Scripts: the same revision. The measured scripts are unchanged from the merged baseline; only publication-side projection and inventory code changed afterwards. Source policy v62 (manifest `bd58ab31…0bfc`), receipt schema 69.
- Owned run `323882a7d7b0e3c86ce4152fa54e6aa3`, stock run `cb002bb51eb2bf8e80e1e5bfd31b48e3`, measured 2026-11-16T14:03Z on macOS 27.0.1, Node v24.22.2, private settings digest `03bb9072…ccc4`, scope
  `owned-osc633-without-notification-suites-v2` (omits only the OSC 9 and OSC 777/99 suites, whose evidence stays in the independently revalidated pinned predecessor run `b45702ad…`; this is combined historical and
  fresh evidence, not a fresh full-suite receipt).
- A first launch of this measurement aborted before any App launch because the local collector cache did not match the checked-in pin; it produced no run, receipt or observation and nothing is retained for it.

## Evidence

[manifest](2026-11-16-osc633-implementation/manifest.json), [owned run archive](2026-11-16-osc633-implementation/owned-run.zip), [summary](2026-11-16-osc633-implementation/summary.json) and
[report](2026-11-16-osc633-implementation/report.json). Reproduce the rederivation:

```bash
node --test scripts/terminfo/osc633-implementation-publication.test.mjs
```

The test reissues the complete receipt from the retained bytes, rebuilds the target report from the paired journal, checks the published projection, and binds the result to a changed App and the same pinned bundle and effect
contract as the retained unsupported baseline. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.14.0 --scope osc633` on Node 24.

## Limits

Protocol handling in a disposable direct-local App on macOS only. Not qualified: any real shell's emission (VS Code's own integration scripts, Bash, Zsh, Fish, PowerShell), SSH or mux delivery, Windows/Linux,
OSC 633 E (command line) or P (properties), command-assist behavior, the `commandFinished` payload, or a command exit status. A shell that emitted both OSC 133 and OSC 633 for one prompt would raise each edge twice;
no such shell integration is shipped or measured. This is not an aggregate `extensions.osc-633-vscode` or `extensions.semantic-prompts` claim.
