# Rich TUI text-area and cell pixel reports

One disposable direct-local Ntilde pane measured `device.xtwinops-14` and `device.xtwinops-16` on macOS. Both historical stock grades are supported. The [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json) and its inventory admission rules are unchanged.

## Pinned child PTY replies

The exact writes were `CSI 14 t` and `CSI 16 t`, each followed by the pinned DA1 sentinel. A separate pair of raw ordering replies fenced every exchange. The unchanged terminfo.dev 4.0.0 terminal callbacks require a complete reply with two positive dimensions in wire order (height, width). The JSON also records the pinned termless callbacks replayed over the isolated PTY case replies.

| Stage | CSI 14 t frame | Pinned verdict | CSI 16 t frame | Pinned verdict |
| --- | --- | --- | --- | --- |
| initial | `CSI 4;1480;2380t` | supported | `CSI 6;37;17t` | supported |
| resized | `CSI 4;962;1428t` | supported | `CSI 6;37;17t` | supported |
| font-changed | `CSI 4;960;1422t` | supported | `CSI 6;40;18t` | supported |
| restored | `CSI 4;1480;2380t` | supported | `CSI 6;37;17t` | supported |

## Owned exact geometry

| Stage | App and kernel grid (rows × cols) | Font (DIP) | Text area (px) | Cell (px) | Native outer window (px) | Sixel report (px) | Exact checks (14 / 16 / Sixel) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| initial | 40 × 140 | 14 | 2380 × 1480 | 17 × 37 | 2400 × 1600 | 2000 × 1480 | passed / passed / passed |
| resized | 26 × 84 | 14 | 1428 × 962 | 17 × 37 | 1446 × 1098 | 1428 × 962 | passed / passed / passed |
| font-changed | 24 × 79 | 15 | 1422 × 960 | 18 × 40 | 1446 × 1098 | 1422 × 960 | passed / passed / passed |
| restored | 40 × 140 | 14 | 2380 × 1480 | 17 × 37 | 2398 × 1616 | 2000 × 1480 | passed / passed / passed |

Pixel dimensions in this table are width × height. The parent read cell metrics and render scale from the App, took the independent kernel grid from `/bin/stty`, and recorded the native window bounds through Core Graphics. Each cell axis was converted to single-precision device pixels and clamped to one pixel before rounding. For the text area, the unrounded cell dimensions were multiplied by columns and rows in single precision before rounding. Both replies use nearest-even rounding. The Sixel geometry query supplied the 2000-pixel cap control.

The parent verified the same pane, screen, cursor, buffer and presentation metadata, native window, and kernel grid around each query. The resize held font size and scale fixed. The font shortcut changed cell metrics in the same resized window at the same scale. Restoration recovered the original pane grid, exact cell metrics and pixel reports. The native window bounds were recorded independently at every stage. Restoring the grid produced an outer window of 2398 × 1616 px versus the initial 2400 × 1600 px.

## Font-change fix

The original App dispatched 80 columns to the buffer and kernel after the font change, while the view had room for 79. The font path divided the full view width by the new cell width. It now uses the same padded grid calculation as ordinary layout. The focused App regression failed with a one-column overdispatch on the original code and passed with the fix.

- [Pre-fix attempt 0f03fca1bfa4b9b4eb418895a289c3b4](pre-fix-metadata-attempt.zip) recorded `Invalid or incomplete owned pane metadata.` and verified lease release.
- [Pre-fix attempt 5971cc49358758b78201f00a40c819c5](pre-fix-grid-attempt.zip) recorded `Cell snapshot budget exhausted.` and verified lease release.

## Reproduce

The [owned archive](owned-run.zip) retains the raw fenced replies, paired App observations, kernel geometry, resize actions, App and collector identities, source hashes, and cleanup proof. The [machine-readable report](report.json) records the independent oracle and the pinned verdicts. Run `6ef5b839b27c220ef603cd2843e93c25` used macOS 27.0.1 (26A434), arm64, App `8ce13633b18218121a24948672fb9987afc26914` and collector `4cc1e37877a0310c4fa289e8906cefe38bfd713b`. Cleanup restored the original kernel PTY mode, removed the disposable fixture and released the desktop lease.

Build the recorded App revision with Node 24+ available for the collector. Run the collector from the recorded scripts revision.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+8ce13633b18218121a24948672fb9987afc26914 --scope xtwinops-pixels
~~~

At the publication revision, rederive the retained evidence and verify the frozen Rich TUI map.

~~~sh
node scripts/terminfo/xtwinops-pixels-publication.mjs --check
python3 scripts/terminfo-rich-baseline.py --check
~~~

Scope covers bare `CSI 14 t` and `CSI 16 t` in the measured direct-local macOS pane. `CSI 14;2 t`, other XTWINOPS operations and cross-platform parity remain outside this measurement.
