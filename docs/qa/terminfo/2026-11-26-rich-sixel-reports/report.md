# Rich TUI Sixel DA1 and geometry reports

One owned local Ntilde tab on current main measured `extensions.sixel-da1` and `extensions.sixel-geometry-report`. Both already show as supported in the [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json).

## Historical replies

The unchanged pinned callbacks replayed the exact archived writes and child replies from the [November 4 reset archive](../2026-11-04-reset-observations-evidence.zip) and the [November 16 static-image archive](../2026-11-16-static-images/owned-run.zip). DA1 advertised attribute `4` in both. The November 4 geometry exchange returned only its DA1 fence and replayed as `inconclusive`. The November 16 exchange returned `CSI ?2;0;2000;1480S` before its fence and replayed as `supported`.

## Pinned child PTY replies

The exact requests were `CSI c` and `CSI ? 2;1;0 S`. The geometry query included the pinned DA1 sentinel. Each exchange also retained a separate pair of raw ordering replies. The table shows the case reply and its pinned terminfo.dev 4.0.0 verdict.

| Stage | DA1 reply | DA1 verdict | Geometry reply | Geometry verdict |
| --- | --- | --- | --- | --- |
| initial | `CSI ?62;4;22c` | supported | `CSI ?2;0;2000;1480S` | supported |
| resized | `CSI ?62;4;22c` | supported | `CSI ?2;0;1428;962S` | supported |
| restored | `CSI ?62;4;22c` | supported | `CSI ?2;0;2000;1480S` | supported |

## Owned resize comparison

| Stage | Grid (rows × columns) | Text area (width × height, px) | Sixel report (width × height, px) | Check |
| --- | --- | --- | --- | --- |
| initial | 40 × 140 | 2380 × 1480 | 2000 × 1480 | passed |
| resized | 26 × 84 | 1428 × 962 | 1428 × 962 | passed |
| restored | 40 × 140 | 2380 × 1480 | 2000 × 1480 | passed |

The parent resized only the nonce-bound pane and independently checked its kernel grid. App cell metrics and device scaling give the text-area dimensions, corroborated by `CSI 14 t`. The Sixel report matched those dimensions with a 2000-pixel cap per axis. The initial width crossed that cap, and the resized width fell below it. Returning to the original grid reproduced the original report.

## Raster evidence

These cases measure device reports. The [separate native Sixel raster qualification](../2026-11-16-static-images-qualification.md) records an unsupported raster-fidelity effect. Its pixel evidence and outcome are unchanged.

## Reproduce

The [owned archive](owned-run.zip) retains the ordered raw PTY replies, fences, App cell metrics, resize journal, settings, captured sources and cleanup proof. The [machine-readable report](report.json) retains pinned outcomes separately from the geometry comparison. Run `4462d1c03136aa903680ab975e92b0bc` used macOS 27.0.1 (26A434), arm64. App revision `cbc33bf4cc62622343f27206bb3fba927c50892a`, collector revision `04f93456e49e2406e6c4d40559885e0994b75801`. Cleanup restored the original pane and kernel PTY mode, removed the fixture and released the desktop lease.

Build the App revision with `scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`. From the measured collector revision run `node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+cbc33bf4cc62622343f27206bb3fba927c50892a --scope sixel-reports`.

Run `node scripts/terminfo/sixel-reports-publication.mjs --check` to replay the retained and new pinned replies, independently verify the geometry comparison, and rederive the receipt. Stock and inventory totals are unchanged.
