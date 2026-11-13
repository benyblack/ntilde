# Controlled ASCII resize/reflow qualification: extensions.reflow (2026-11-12)

Qualifies the one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`) named `extensions.reflow` through a separately identified
effect contract (`ntilde-ascii-reflow-effect-v1`, contracts v1) measured in a disposable direct-local App pane. The pinned probe has **no fixture, sends no resize request
and reads no cells**: its termless arm consults only the declared reflow capability (`ctx.capabilities.reflow === true`), returns `pass: false` and observation
inconclusive/insufficient-evidence (evidence "legacy") under the note "No controlled resize and cell-content readback to measure reflow", and its terminal path returns the
same inconclusive without touching the terminal (evidence "none"). The pinned bundle declares no reflow capability for any terminal, so **the pinned verdict is inconclusive
by design and stays inconclusive**; the effect verdict is separately identified and never attributed to the pinned probe. The historical three-case ASCII resize publication
([owned-resize.md](owned-resize.md), [2026-11-03-resize.md](2026-11-03-resize.md)) and its outcomes are preserved untouched; this suite reuses that machinery (the
parent-owned resize action, the paired full-state observers, the `/bin/stty` kernel proof) and reuses none of its results as fresh evidence.

## Result

| Case | Pinned (live code + parent replay) | Effect contract | Experiments (reads) | Stock grade |
|---|---|---|---|---|
| `extensions.reflow` | **inconclusive by design** | **unsupported** (width round trip model-exact; history round trip diverges in the live pane-resize path) | 2 (6) | inconclusive |

The stock collector, re-run in the same owned run, still grades `extensions.reflow` inconclusive (167 inconclusive / 87 supported / 1 unsupported, unchanged), so this
qualification **does not raise the stock compatibility score**. The recorded divergence is `pinned-inconclusive-by-design`: the pinned probe never decides, so every decided
effect verdict is a recorded difference, never an absorbed one.

## What was measured (disposable direct-local pane, 40×140)

The suite runs two experiments under the single benchmark id, each as one fenced sequence: seed once at the original grid, paired-read, parent-selected shrink action,
paired-read, restore action, paired-read — with **no content writes between a capture and its resize** (the only feeds are the two seeds and the final cleanup). Every
action is parent-selected from the source plan (the worker requests a stage, never dimensions), re-proves the App/worker/executable/uid/TTY relation before and after,
requires the App's `exact` + `layoutSettled` result at the planned grid, and then independently verifies the kernel PTY geometry with `/bin/stty` on the same owned TTY
(`-opost` throughout). A DA ordering fence precedes every read. Four actions, nine paired frames (start, nonce bind on the alternate screen as a bounded control, six reads,
finish), the nonce bind and final cleanup held to the pristine baseline.

- **`reflow.width-roundtrip`** (shrink 40×140 → 40×133, then restore): six 210-char long hard lines (two wrapped rows at both 140 and 133 columns, with different break offsets) interleaved with
  short hard lines, one authored-single-space line and a bold+underline attribute probe. **All three stages matched the independent character-slicing model exactly**:
  the wrapped lines redistributed at the smaller width with the break offsets moved, the short lines, authored spaces and attribute probe stayed intact (probe flags 777,
  underline style 1, through all three states), the cursor parked inside the first line's wrapped continuation moved from [1, 46] to [1, 53] (+ the removed 7 columns) and
  back, and the restored grid was the original layout. No history was displaced at either width; the six unwritten tail rows stayed unwritten.
- **`reflow.history-roundtrip`** (shrink 40×140 → 36×133, then restore): a full viewport of 138-char hard lines; the cursor parked at the end of the last line. The
  **initial and restored stages matched the model exactly** (restored = documented top-anchor policy: 22 retained history rows, viewport from line 22 with bottom blank
  padding, cursor [17, 138]). **The shrunk stage diverged** (below).

## The divergence (deterministic, measured twice)

At the smaller grid the model expects every hard line to rewrap 138 → 133 + 5, displacing 44 complete ordered rows into history (≤ the 64-row read budget) and putting the
cursor at [35, 5]. The App displaced **38** rows and put the cursor at [35, 5]. The exact decode of the retained 74-row layout: hard lines 0–16 reflowed as **six merged
pairs** — the logical line for each pair (k, k+1) with k ∈ {0, 3, 6, 9, 12, 15} is `line k` + two blank layout cells + `line k+1` (278 chars → 133 + 133 + 12 rows) —
while lines 17–39 rewrapped individually, exactly as modeled. Every character of every line is present exactly once (lossless), the 2 inserted cells are default blanks,
the cursor mapping still matches the logical-content model, and the restore re-wraps the merged lines losslessly back into the original projection (the inserted blanks
become trailing row padding the cell projection cannot distinguish from unwritten cells — the authored-space limit stated in the contract).

The divergence is **deterministic**: six independent owned runs produced byte-identical layouts for all six fixture
reads (the only differing frame is the nonce bind, which embeds the runId), identical action targets and identical App-reported grids. The VT engine in isolation does
**not** reproduce it: driving the same `Ntilde.VT` build with the same fixture, a one-shot `Resize(133, 36)`, one-column stepped resizes, height-first order, simultaneous
steps, and randomized non-monotonic walk sequences all reproduce the model exactly (44 rows, cursor [35, 5]). The divergence is therefore specific to the live pane-resize
path (window layout → view resize dispatch → session), not to the documented reflow algorithm; its exact trigger inside that path is not yet root-caused. It is **not**
a violation of the documented invariant ([MODULE_OWNERSHIP.md](../../../MODULE_OWNERSHIP.md): "Lossless reflow — resize never silently drops ordinary text content"):
no content was dropped. It **does** violate this contract's stricter criterion — that the stored layout after a parent-selected resize equals the independent
character-slicing model, with hard-line boundaries distinct on one physical row each — so the effect verdict for the history experiment is **unsupported**, honestly
retained with the stage-level note. Unaffected-state checks held everywhere: modes, margins, region, cursor mode and unrelated cells stayed at their bound baseline in
every stage of both experiments.

Not measured, deliberately: Unicode, wide-cell or grapheme reflow, history attributes, alternate-screen reflow (the alternate screen is observed once as the nonce-bind
control), images, rendered appearance, saved-bank behavior beyond the bounded main-screen history, remote/mux sessions, other platforms, and shell reflow behavior.

## Identities

App 0.13.0 (operator supplied) built from `d8679e11` (arm64 Release, executable SHA-256 `f5814e51…9bf8`); scripts at commit `d8679e11`; source policy
`readonly-owned-source-graph-v52` (348 files — main's 342 through the OSC 9 layer plus these six modules); receipt schema 58; Node v24.22.2; owned run
`e8d8ebdb…48c7`, measured on macOS 27.0.1 arm64; pane and settings bound in [summary](2026-11-12-ascii-reflow/summary.json); the stock collector re-ran in the same run
(not carried forward). The insert/replace (PR 956), XTGETTCAP (PR 957), modifyOtherKeys and OSC 9 (PR 960) layers merged ahead of this batch (policy v48–v51, receipt schemas 54–57 for their own
scopes), so this layer was restacked above all of them (policy v52 / captured v59 / receipt 58) and **re-measured**: their suites are reconfirmed inside the
published run's receipt (insert/replace `supported: 1`, XTGETTCAP `inconclusive: 1`, plus the modifyOtherKeys and OSC 9 suites). The full
context: worker, PTY, output-processing proof, per-action parent/kernel geometry
records and the completed cleanup receipt. Predecessor suites were reconfirmed in the same run through the established runner: the receipt re-derived every earlier layer,
including the resize/history suites this batch reuses, and the stock counts are re-collected in that run and unchanged.

Attempts, all retained with their actual reasons (archives in [superseded/](2026-11-12-ascii-reflow/superseded/)): attempt 1 (`owned-Z3WQlA`, no receipt) aborted before
collection when the first launch's context named a bundle digest that did not exist yet; attempt 2 (`owned-DD0VT3`, no receipt) failed at parent bootstrap on the same
naming mismatch; attempt 3 (`owned-7gsSlr`, no receipt) failed launching the worker on the renamed binding; attempt 4 (`owned-dNbnet`, no receipt) **completed collection
with identical grading** but could not finalize: the metadata receipt's source-policy map lacked the then-current v48 entry (fixed in the measured scripts); attempt 5
(`owned-Q0DShd`, no receipt) aborted when the disposable App did not respond at launch (desktop idle), retained unchanged. Runs `owned-NITRsC` (attempt 6), `owned-sWZqnR` (attempt 7), `owned-KeiEHw` (attempt 8), `owned-4VAYK1` (attempt 9), `owned-iUlKFK` (attempt 10,
aborted: the XTGETTCAP merge removed this suite's context bindings from the runner mid-flight) and `owned-FoQxOl` (attempt 11, the previously published run `4b6916b3…609b` at policy v50 / receipt 56, superseded by the modifyOtherKeys and OSC 9 merges) and `owned-jR4FIO` (attempt 12, run `481a15b5…40c5` at policy v52 / receipt 58, superseded by Codex's final-head P2: the runner now creates `reflow-actions.jsonl` before any request so a typed refusal can finalize) `owned-eTCUl0` (attempt 13, run `9aee574f…31aa`, superseded by the SonarCloud fixes in the measured scripts: nested ternaries, complexity, optional chains, one unused import and one re-export, with no behavior change) and `owned-qabkq9` (attempt 14, run `b19d1f52…aa20`, superseded by Codex's unavailable-receipt-shape P2 and one more SonarCloud finding). `owned-pp78n8` (attempt 15, no receipt) aborted with an observer timeout because a full local test suite was running concurrently on the same machine (load average ≈ 48), so it is retained as an environmental failure and not a result **completed and graded identically (width model-exact,
history shrunk diverged at 38 displaced rows, restored exact) and reproduced byte-identically by each other and by the final run** — ten independent owned
runs, same divergence — but are superseded by the serial restacks (insert/replace took v48/54, XTGETTCAP v49/55, modifyOtherKeys v50/56 and OSC 9 v51/57, so this layer was restacked to
v52/58 above all of them; their retained receipts bind pre-restack scopes) and by the review fixes to the measured scripts (Codex: the unavailable marker's
experiment count and the unavailable receipt's completion-artifact rejection; CodeRabbit: proof-marker admission, adapter-identity validation). The
published run is the sixteenth.

## Newly fixed versus newly measured

No production code changed: every observed behavior already existed, and the oracle predicted the width experiment's nine comparisons of the real App before any App
adjustment (there were none). Newly measured existing behavior: the width round trip (model-exact, including the wrapped-row cursor mapping, authored spaces, the
attribute probe and lossless restoration) and the top-anchor restoration of the history experiment. Newly measured divergence: the deterministic live-path pair merge
described above. The measurement tooling itself (contracts, observer, collector, grader, layered pinned replay, receipt suffix) is new and is the only code this PR adds.

## Evidence

[summary](2026-11-12-ascii-reflow/summary.json), [report](2026-11-12-ascii-reflow/report.json), [manifest](2026-11-12-ascii-reflow/manifest.json),
[owned-run.zip](2026-11-12-ascii-reflow/owned-run.zip) (the complete retained run: context, ownership proof, settings, journals, action records, observations, receipt,
retained sources) and the superseded attempt archives. Reproduce:

```bash
node --test scripts/terminfo/ascii-reflow-publication.test.mjs
```

The test verifies the archive digests, extracts the retained run, re-derives the complete receipt (schema 55) from the retained bytes, re-derives both experiments and
their grading from the journals, and compares the published projection byte for byte. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.13.0+<revision>` on Node 24.

## Limits

These observations qualify the stated ASCII stored-state reflow criterion in the disposable direct-local macOS App only. They do not qualify Unicode, wide-cell or
grapheme reflow, history attributes, alternate-screen reflow, images, pixels, rendered appearance, saved banks, remote delivery, other platforms, desktop timing, or the
stock grade. No common-harness or Core score inference is made: the pinned verdict stays inconclusive, the stock grade stays inconclusive, and the effect verdict is
**unsupported** for the history-crossing criterion as authored. The divergence's root cause inside the live pane-resize path is identified as a measured, deterministic
behavior of that path but is not yet attributed to a specific code site; attributing and (if confirmed against a documented contract) fixing it is separate work.
