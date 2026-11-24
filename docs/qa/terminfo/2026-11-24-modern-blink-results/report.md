# Ntilde Modern assessment for SGR 5 blink

**52 pass / 0 fail / 17 inconclusive across 69 frozen requirements.**

The [Modern modes assessment](../2026-11-24-modern-modes-results/report.md) had 51 pass / 0 fail / 18 inconclusive. The [original Modern publication](../2026-11-15-modern-results/report.md) retains the frozen criteria and upstream attribution.

**sgr.blink: pass.** styled crop changed and returned with a stable unstyled control.

The sidecar wrote SGR 5 followed by X on row 4 and left an unstyled X on row 7. It hid the caret and parked it at row 12, column 1. It made no terminal writes during the measured series. Both crops came from the separate plain-X calibration. All 20 control crops matched that calibration.

The sampler targeted 350 ms intervals. Each native screenshot falls within its recorded command interval. The no-change rule uses the shortest possible overall span and the longest possible interframe gap.

The measured series spans at least 6567.26 ms with gaps no greater than 520.90 ms.

The styled crop changed and returned across frames 0 → 2 → 3 (zero-based). The live compositor used the App’s usual clock. The stored blink flag (true) and DECRQSS reply (`"\u001bP1$r0;5m\u001b\\"`) corroborate the setup.

Native window 100911 held at (-1880, 183, 1200, 800) in macOS points. The original PNGs are 2400 × 1600 pixels. The styled crop is (126, 189, 19, 25) and the control is (126, 300, 19, 25), in window-local pixels (x, y, width, height).

| Frame | Capture start (monotonic ms) | Capture end (monotonic ms) | Styled appearance |
| --- | ---: | ---: | --- |
| 0 | 3230.14 | 3329.72 | A |
| 1 | 3580.67 | 3686.79 | A |
| 2 | 3932.48 | 4037.21 | B |
| 3 | 4278.71 | 4378.56 | A |
| 4 | 4630.05 | 4728.84 | A |
| 5 | 4992.74 | 5099.81 | B |
| 6 | 5328.59 | 5432.94 | A |
| 7 | 5678.26 | 5783.18 | A |
| 8 | 6030.05 | 6128.94 | B |
| 9 | 6378.35 | 6495.19 | A |
| 10 | 6727.30 | 6829.13 | A |
| 11 | 7081.95 | 7201.30 | B |
| 12 | 7429.71 | 7533.33 | A |
| 13 | 7779.67 | 7884.00 | A |
| 14 | 8127.97 | 8250.42 | B |
| 15 | 8479.11 | 8600.59 | A |
| 16 | 8871.43 | 9000.01 | A |
| 17 | 9179.72 | 9280.07 | B |
| 18 | 9566.77 | 9699.93 | A |
| 19 | 9896.98 | 10016.89 | A |

## Evidence and reproduction

[Owned run](owned-run.zip) `f7a0ab9debab67953875e4a23a91da52` started 2026-11-24T14:54:04.194Z on macOS 27.0.1 (26A434), arm64, with Node v24.22.2. App 0.14.0 was built from `fb83fbcdc6fa1026cc5ce89ca4f410725e074ce5`. The measured scripts were `a45860f8d4a29427ff5365ad87b3cfcdc0ea5a0a`. The [manifest](manifest.json) names source policy `readonly-owned-source-graph-v75`, receipt schema 1, and the archive digests.

The archive retains the original PNGs, pane snapshots, exact fixture bytes and DA fences, native window bounds and timestamps, App executable and assembly hashes, captured source graph, PTY restoration, cleanup receipt, and desktop lease release events. The [JSON report](report.json) contains all 69 rows and the derived crop digests.

[Retained attempt a6a13657fc2b6b509e0bc60ad185e468](attempt-1.zip). The native window moved and resized during the blank calibration capture. The calibration geometry check refused the run after verified cleanup.

[Retained attempt fc2234cff3baa61dec82a6bb5cc117b5](attempt-2.zip). The live styled crop returned and the unstyled control stayed stable. The SonarCloud cleanup and dispatch fixes changed measured scripts and superseded this run.

Check the archived evidence and rederive the reports offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --blink
```

For a fresh capture, build the recorded App revision and run the collector from the measured scripts revision on an awake macOS desktop with Screen Recording permission. The launcher acquires and releases the desktop lease.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope modern-blink
```
