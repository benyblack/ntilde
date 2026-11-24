# Ntilde Modern assessment for SGR 5 blink

**52 pass / 0 fail / 17 inconclusive across 69 frozen requirements.**

The [Modern modes assessment](../2026-11-24-modern-modes-results/report.md) had 51 pass / 0 fail / 18 inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen criteria and upstream attribution.

**sgr.blink: pass.** styled crop changed and returned with a stable unstyled control.

The sidecar wrote SGR 5 followed by X on row 4 and left an unstyled X on row 7. It hid the caret and parked it at row 12, column 1. It made no terminal writes during the measured series. Both crops came from the separate plain-X calibration. All 20 control crops matched that calibration.

The sampler targeted 350 ms intervals. Each native screenshot falls within its recorded command interval. The no-change rule uses the shortest possible overall span and the longest possible interframe gap.

The measured series spans at least 6527.30 ms with gaps no greater than 529.31 ms.

The styled crop changed and returned across frames 0 → 2 → 3 (zero-based). The live compositor used the App’s usual clock. The stored blink flag (true) and DECRQSS reply (`"\u001bP1$r0;5m\u001b\\"`) corroborate the setup.

Native window 100725 held at (680, 167, 1200, 800) in macOS points. The original PNGs are 2400 × 1600 pixels. The styled crop is (126, 189, 19, 25) and the control is (126, 300, 19, 25), in window-local pixels (x, y, width, height).

| Frame | Capture start (monotonic ms) | Capture end (monotonic ms) | Styled appearance |
| --- | ---: | ---: | --- |
| 0 | 3126.56 | 3238.93 | A |
| 1 | 3465.43 | 3584.20 | A |
| 2 | 3826.33 | 3939.30 | B |
| 3 | 4167.47 | 4284.76 | A |
| 4 | 4526.60 | 4634.19 | A |
| 5 | 4867.02 | 4984.92 | B |
| 6 | 5276.71 | 5396.33 | A |
| 7 | 5576.09 | 5681.15 | A |
| 8 | 5927.27 | 6051.43 | B |
| 9 | 6284.72 | 6402.24 | A |
| 10 | 6630.41 | 6752.64 | A |
| 11 | 6987.61 | 7101.37 | B |
| 12 | 7333.50 | 7452.13 | A |
| 13 | 7667.36 | 7800.17 | A |
| 14 | 8034.00 | 8163.40 | B |
| 15 | 8366.36 | 8481.13 | A |
| 16 | 8718.34 | 8832.52 | A |
| 17 | 9064.65 | 9184.75 | B |
| 18 | 9416.98 | 9535.03 | A |
| 19 | 9766.23 | 9880.48 | A |

## Evidence and reproduction

[Owned run](owned-run.zip) `fc2234cff3baa61dec82a6bb5cc117b5` started 2026-11-24T14:41:48.224Z on macOS 27.0.1 (26A434), arm64, with Node v24.22.2. App 0.14.0 was built from `fb83fbcdc6fa1026cc5ce89ca4f410725e074ce5`. The measured scripts were `0086ec934ca084796ba53676a41a3df2be63b135`. The [manifest](manifest.json) names source policy `readonly-owned-source-graph-v75`, receipt schema 1, and the archive digests.

The archive retains the original PNGs, pane snapshots, exact fixture bytes and DA fences, native window bounds and timestamps, App executable and assembly hashes, captured source graph, PTY restoration, cleanup receipt, and desktop lease release events. The [JSON report](report.json) contains all 69 rows and the derived crop digests.

[Retained attempt a6a13657fc2b6b509e0bc60ad185e468](attempt-1.zip). The native window moved and resized during the blank calibration capture. The calibration geometry check refused the run after verified cleanup.

Check the archived evidence and rederive the reports offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --blink
```

For a fresh capture, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop with Screen Recording permission. The launcher acquires and releases the desktop lease.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-blink
```
