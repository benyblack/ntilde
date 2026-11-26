# Native Sixel raster fidelity

The fix for [#851](https://github.com/benyblack/ntilde/issues/851) preserves the square-pixel raster at the presenter’s device-pixel extent. The current cell grid supplies the anchor. The placement still reserves whole cells for terminal layout.

| Case | Pinned termless callback | Pinned terminal callback | Owned raster effect | Stock grade |
| --- | --- | --- | --- | --- |
| `extensions.sixel` | inconclusive | inconclusive | supported | inconclusive |

The unchanged terminfo.dev 4.0.0 callbacks report that a capability declaration cannot establish pixels and that the terminal callback has no pixel readback. The stock run returned the same terminal result, with 97 supported, 157 inconclusive, and one unsupported graded case overall. The [earlier unsupported raster result](../2026-11-16-static-images-qualification.md) remains evidence for its measured revision. The [DA1 and geometry assessment from #1113](../2026-11-26-rich-sixel-reports/report.md) measures child PTY replies and a resize.

## Fixed-metric reproduction

On unchanged main `3c4d91088ecd0839824572076b233be4c4e95040`, both decoder and cell-reservation controls passed. Both raster checks failed. The fixture declares and paints 16×16 source pixels. The decoder produced a 16×18 canvas with two transparent bottom rows before and after the fix.

| Parse cell pixels | Capture cell pixels | Reservation | Expected painted extent | Main before fix | Fixed |
| --- | --- | --- | --- | --- | --- |
| 10×20 | 10×20 | 2×1 | 16×16 | 20×18 | 16×16 |
| 20×40 | 10×20 | 1×1 | 8×8 | 10×18 | 8×8 |

Both cases anchored at `(34,60)`. The [original reproducer and failure output](before-fixed-metrics.zip) can be run by putting the archived C# test in `tests/Ntilde.App.Tests/RenderTests` in an isolated checkout of that main revision. The same wrapper command runs the regression here.

## Owned measurement

A disposable direct-local macOS arm64 pane used a 40×140 grid and a 1.1× offscreen capture. The snapped capture grid measured 9×20 pixels per cell. The 16×18 decoder canvas reserved 1×1 cell. The independent calibration projected the painted region at roughly 9×8.89 capture pixels. The observed region was 9×9 at `(31,80)`.

Blank, show, and clear all passed. The fixed one-pixel edge band left 2, 8, 3, and 12 safe color pixels and 1,340 safe background pixels. All matched. The whole-cell projection gave 55 mismatches. The paired raw fences bound each write to the captures, which retained an unchanged surrounding and verified clearing. The earlier 1× attempt painted 8×8 in its capture but had no gradeable pixels in two color regions outside the fixed edge band. Its result remains inconclusive. The attempt record lists the superseded captures and links their previously published archives. [1× run](inconclusive-owned-run.zip) · [Attempt record](prior-attempts.json)

- App build `f9b0da195bce62cf2626d5ca65d9f57c826ca744`. The version label was operator-supplied. The receipt binds the sidecar executable and assembly digests.
- Collector `f9b0da195bce62cf2626d5ca65d9f57c826ca744`, source graph `readonly-owned-source-graph-v80`. Owned run `c0e02b05af46e65e61c64782bb920edd`, stock run `6c7dc83522a9b65ecef4bf872d05475b`.
- `--scope static-images` runs the existing OSC 633 carrier scope and requests 1.1× solely for the paired static-image captures. The collector retains the scale, original PNGs, grid, cell and image facts, raw writes and replies, and cleanup proof in the [owned archive](owned-run.zip). See the [summary](summary.json), [machine-readable verdicts](report.json), and [manifest](manifest.json).

## Production path and limits

The parser keeps the existing ceiling-based reservation and retains a versioned source canvas and crop. The renderer uses the current anchor and the presenter density for raster sizing. A capture applies its own scale to that extent. State transfer, native v3 replays, and upgraded mux peers keep the intent and ordered stream metrics. Policy-zero states and streams retain whole-cell sizing. Changing a pane’s stream policy preserves its local wheel and cursor defaults. Native anchors stay with their rows as the hidden main screen redistributes history or reflows a row that holds the image. Under raster budget pressure, a retained source preserves native reservation and cursor progression. State transfer can restore that placement’s layout and source without allocating a raster. A packet above the source-retention limit still advances the cursor without adding a raster placement. An isolated capture that requires unavailable pixels reports that failure.

This owned result covers one square-pixel packet, one local pane, one OS and stable metrics. Font and DPI changes, clipping, replay, state transfer, and mux have regression tests. Monitor pixels, remote or cross-platform output, Pan/Pad, P2 compositing, and broader decoder extents are outside the capture scope. The stock baseline, Core, and common-harness scores are outside this measurement.

## Reproduce

```sh
scripts/build.sh test tests/Ntilde.App.Tests --blame-hang-timeout 5m \
  --filter 'Lane=PlatformBoot&FullyQualifiedName~SixelNativePixelReproductionTests'
node scripts/terminfo/sixel-native-publication.mjs --check
```

The check reissues the complete receipt, regrades the paired renders and controls, and replays both pinned callbacks from the retained bundle. A fresh run uses Node 24 and the [pinned collector preparation](../../QA_TERMINFO.md). Build the measured App revision into a private sidecar and run from the collector revision above. The collector acquires and releases the shared desktop lock.

```sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false \
  -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde \
  --app-version 0.14.0+f9b0da195bce62cf2626d5ca65d9f57c826ca744 --scope static-images
```
