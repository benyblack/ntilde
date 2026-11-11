# OSC 133 qualification: prompt start, command-input start, command execution start, command completion (2026-11-11)

Qualifies four pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `extensions.osc133-a`, `extensions.osc133-b`,
`extensions.osc133-c` and `extensions.osc133-d`. The contract, the oracle and the evidence boundaries are in [osc133.md](osc133.md). The cases sit outside the
frozen Core catalogue, so **no Core percentage changes**. This is a qualification of existing behavior: **no production code changed**.

## Result

| Case | Role | Pinned (live code + parent replay) | Effect contract | Experiments (reads) | Stock grade |
|---|---|---|---|---|---|
| `extensions.osc133-a` | prompt start | inconclusive by design | **supported** | 6 (8) | inconclusive |
| `extensions.osc133-b` | command-input start | inconclusive by design | **supported** | 8 (11) | inconclusive |
| `extensions.osc133-c` | command execution start | inconclusive by design | **supported** | 10 (15) | inconclusive |
| `extensions.osc133-d` | command completion | inconclusive by design | **supported** | 8 (12) | inconclusive |

All four effect contracts are `ntilde-osc133-shell-lifecycle-effect-v1`, separately identified and never attributed to the pinned probes; the four divergences are
recorded as `pinned-inconclusive-by-design`. Forty-six reads were journaled as 49 paired frames (start, bind, 46 reads, finish); none needed settling retries. The stock
collector, re-run in the same owned run, still grades all four ids inconclusive (167 inconclusive / 87 supported / 1 unsupported, as before), so this qualification
**does not raise the stock compatibility score**.

## Pinned definitions

None of the pinned probes can decide. Each feeds `ESC ] 133;<marker> BEL X` (the terminal path first writes `ESC[1;1H ESC[2K`), checks that `X` landed in cell (0,0)
(termless) or that the cursor sits in column 2 (terminal), and returns `promptConsumptionResult`: always `pass: false`, outcome inconclusive, "Marker was consumed;
shell prompt integration was not observed". The retained code (termless **and** terminal functions) ran through a digest-checked derivation over the left/right-margin
extension and was replayed over the journaled stock-context reads: `X` in (0,0), cursor column index 1, on both paths for all four markers. The parent re-executed the
termless probes from the journal and agreed with the live results.

## What the App did (disposable direct-local pane, 40×140)

Each experiment starts from a completed `D;0` then `A` baseline (precise prompt state, no anchors, prepared title and cwd, a run nonce on row 9, a runtime palette
override). Reads are paired complete screen + pane-metadata + session-status captures; the oracle predicts the whole screen, the cursor and five lifecycle facts.

**A, prompt start.** From a measured running command (`echo a-run`, anchors (2,8) and (3,0)) the unmarked control left the state unchanged; the exact pinned fixture
returned the session to the prompt with no current command. Gates: B and C from the same prior state left it running. D from that state also returns the session to its
prompt, so the status observable is *shared* with D and is disclosed, never required. Implementation policy (recorded, not graded): A does not clear the window or the
anchors of an unterminated command (they stayed at (2,8) and (3,0)). The ST-terminated marker behaved identically.

**B, command-input start.** On a prompt, the unmarked control opened nothing; the pinned fixture opened the input window with the command-start anchor at the cursor,
(0,0), with the typed `X` after it. In the boundary experiment a prompt `ntilde$ ` then `B` then `echo b-input` produced anchor (3,8): the prompt text is exactly the
eight cells left of the anchor and the input text exactly what follows, with no command exposed (status still prompt, current command null). A, C and D from the
same state opened no window. A second cycle moved the anchor to (6,8); nothing of the first cycle survived.

**C, command execution start.** From a measured input phase (window open, anchor (2,8)) the unmarked control changed nothing; the pinned C (no payload) put the session
running with the window closed and no command identity. The payload experiment typed `echo c-typed` but sent `C;<base64 "echo c-payload">`: the session reported
`echo c-payload` (never the typed text), closed the window, kept the command-start anchor (2,8) and set the output anchor to (3,0), where the first output row
`c-output` landed. A, B and D from the input phase produced no running session. A second cycle named `echo c-two` with anchors (6,8) and (7,0). Recorded observations:
an `aid=7` attribute payload names no command, a plain-text payload (`echo c-plain`) is passed through, and the ST terminator behaves identically. After the pinned
fixture homed the cursor above the input line, the output anchor was (1,0), above the command line; this is policy and not graded.

**D, command completion.** From a measured running command the unmarked control changed nothing; the pinned D (`D;0`) returned the session to the prompt, dropped both
anchors, closed the window and cleared the command. A left the anchors; B and C left the command running. Two consecutive cycles ended with clean state each time.

**Exit status (separate observation).** `D;7` produced the same state as `D;0`. The session status's `exitCode` is the child **process** exit code and stayed null;
the snapshot and metadata expose no command exit status, so the payload's interpretation is **not observed** here (the criterion does not require it). The
`commandFinished` event stream is not read and PR #709 is neither modified nor depended on.

## Identities

App 0.13.0 (operator supplied) built from `45f0e09c` (arm64 Release, executable SHA-256 `f5814e51…9bf8`, assembly `bc9ff8b9…6f9825`); scripts at the same
revision; source policy v39 (281 files, manifest `96ffe9a0…14b0`); receipt schema 45; Node v24.22.2; owned run `650b9b82…4395`, stock run `7e393c09…9ea247`,
measured 2026-11-11T12:01Z on macOS 27.0.1; private settings digest `ab4811db…6469`. Predecessor suites reconfirmed in the same run through the established runner:
every earlier suite's cases are supported (or their effect contracts supported), the query suite reproduces its frozen 33 matched / 7 mismatched, and the two findings
reconfirm. Three earlier measurements are retained, each with all four effect contracts supported: the first, uncommitted one (26 experiments; `superseded/attempt-1-superseded.zip`); the committed-script run `58f5357e…007a` (`superseded/attempt-2-superseded.zip`), superseded by a Codex review finding (the worker and the parent both created the typed unavailable marker, aborting a run whose first observation is unavailable; the worker is a measured file, so the fix required a fresh measurement); and the run `dd0203ec…3ba8` on the fixed worker (`superseded/attempt-3-superseded.zip`), superseded because the text/cursor batch (PR 937) merged first and took policy v38 / receipt 44, so this layer was restacked to policy v39 / receipt 45 and measured again.

## Evidence

[summary](2026-11-11-osc133/summary.json), [run log](2026-11-11-osc133/run-log.json), [journal-common](2026-11-11-osc133/journal-common.json), one
[case file](2026-11-11-osc133/) per id and one `journal-<case>-<experiment>.json` per experiment (its paired frames). Reproduce:

```bash
node --test scripts/terminfo/osc133-publication.test.mjs
```

The test reassembles the journal, re-derives the report, the receipt digests and the pinned replay. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.13.0` on Node 24.

## Limits

Protocol handling in a disposable direct-local App only. Not qualified: any real shell's emission (Bash, Zsh, Fish, PowerShell), SSH or mux delivery, Windows/Linux,
OSC 133 P, OSC 633, `extensions.semantic-prompts`, command-assist behavior, the mouse/focus reset that A performs, hidden panes, and the commandFinished event
payload. Restoration is the prepared title/cwd baseline, the initial cells and the original palette; the original null title/cwd is not observable and not claimed.
