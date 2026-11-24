# Ntilde Modern assessment for holding the viewport on output

**53 pass / 0 fail / 16 inconclusive across 69 frozen requirements.**

The [blink assessment](../2026-11-24-modern-blink-results/report.md) had 52 pass / 0 fail / 17 inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen catalogue and criteria.

**`scrollback.viewport-hold-output` on the macOS App: pass.** The manual criterion requires the same old rows at the same visible positions after both plain and synchronized output. The separate Linux XTEST wheel and pixel-capture path remains unmeasured by this run.

## Fixed App

| Phase | Verdict | Live bottom | Old anchor | After output | Restored bottom |
| --- | --- | --- | --- | --- | --- |
| plain | pass | 0082–0120 (0/81) | 0040–0079 (42/81) | 0040–0079 (43/82) | 0083–MARKER (0/82) |
| synchronized | pass | 0082–0120 (0/81) | 0040–0079 (42/81) | 0040–0079 (43/82) | 0083–MARKER (0/82) |

The table shows the numbered row IDs and the distance from the live bottom over the maximum offset. The restored view ends with a blank row below its marker.

- plain control marker at the restored bottom: `V-3bc714f153f4ac4a62b01e2c9cc1b3e4-P-MARKER`.
- synchronized control marker at the restored bottom: `V-3bc714f153f4ac4a62b01e2c9cc1b3e4-S-MARKER`.

Each phase started from a fresh seed of 120 numbered lines. The pane held a 40-row by 140-column grid in a 1200 × 800 DIP window. Both phases retained every seeded row with zero evictions. Smooth scrolling was disabled for settled wheel steps. The default wheel step was three rows per notch and the default MaxHistory was 10,000. Session persistence was off.

The collector used the OSC 720 generated App wheel route. Paired readbacks combined the App viewport offset, the retained history and the live rows to identify every visible row. The child wrote the plain marker with CRLF. For the synchronized phase it wrote the marker and CRLF in one write between `ESC [ ? 2026 h` and `ESC [ ? 2026 l`. The collector sent no keyboard input or PTY queries between the anchor and post-output snapshots.

## Baseline and fix

| Phase | Verdict | Live bottom | Old anchor | After output | Restored bottom |
| --- | --- | --- | --- | --- | --- |
| plain | inconclusive | 0082–0120 (0/81) | 0040–0079 (42/81) | 0041–0080 (42/82) | 0083–MARKER (0/82) |
| synchronized | inconclusive | 0082–0120 (0/81) | 0040–0079 (42/81) | 0041–0080 (42/82) | 0083–MARKER (0/82) |

The [baseline run](baseline-run.zip) delivered both markers and drifted one row in each phase. Both baseline phases are inconclusive under the frozen criterion. The App had kept a constant distance from the growing bottom. The fix tracks the history tail and advances the offset with appended rows so the visible anchor stays in place. App regressions cover plain and synchronized output, a smooth wheel gesture, a return to the live bottom during continuous output, eviction of an older page and a cleared history generation.

## Evidence and reproduction

[Fixed App run](owned-run.zip) `3bc714f153f4ac4a62b01e2c9cc1b3e4` started 2026-11-24T15:44:09.302Z on macOS 27.0.1 (26A434), arm64 with Node v24.22.2. App 0.14.0 came from `351150271ee064bb5b9cd06538085f61af382b76`. The measured scripts were `351150271ee064bb5b9cd06538085f61af382b76`. Baseline App `2decf1f1392322a4a0b60ec28cd14edd5ec2ee67` and scripts `6778948c0c3dd2f295364ddc73a6897500b83bf0` produced run `9d5cb718f7dc5abae880324e9e792298`.

The [manifest](manifest.json) pins the archives. Source policy `readonly-owned-source-graph-v76` and receipt schema 1 bind the raw snapshots, wheel actions, exact PTY bytes, settings and geometry to the owned App and child. The runs verified restoration to the blank main pane with no history, the original PTY mode, fixture shutdown and desktop lease release. The [JSON report](report.json) retains the full visible row vectors and all 69 Modern rows.

[Retained attempt cf1bc5d09c17f80f18bc787cfee4ef99](attempt-1.zip). Both phases passed on this App. Codex flagged a smooth-scroll convergence defect under continuous output, which this capture preceded.

Rederive the reports from the retained archives with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --viewport
```

For a fresh measurement, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop. The launcher acquires and releases the repository desktop lease.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-viewport
```
