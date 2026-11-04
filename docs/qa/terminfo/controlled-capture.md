# Controlled offscreen captures

`captureScreen` accepts optional controls for renderer qualification.
The MCP tool `ntilde.capture_screen` exposes the clock and details controls;
synthetic selection is currently an AgentHost-only qualification input.

| Input | Meaning |
| --- | --- |
| `presentationTimeMs` | Integer synthetic time from 0 through 60,000 ms, starting with a visible blink phase. Omit it to keep the existing fixed-visible capture behavior. |
| `includeRenderDetails` | Include the actual resolved primary font, pixel grid, palette, color overrides and presentation facts from the draw. Defaults to false. |
| `includeImageDetails` | AgentHost only: include detached image placement, canvas and optional SIXEL creation facts from this draw. Requires `includeRenderDetails=true`; defaults to false. |
| `syntheticSelection` | Optional `{startRow,startCol,endRow,endCol}` inclusive zero-based viewport span to paint in the offscreen image. It never changes the live selection. AgentHost only. |

These controls require `mode: "render"`. A request combining any with `mode: "live"` is malformed; the endpoint does not silently replace a desktop photograph with a controlled render. Invalid inputs are refused before marking the pane as observed or writing a PNG. Valid captures retain the normal observe permission and indicator behavior. If the draw grid differs from the grid used to size the bitmap, captures return retryable `captureUnavailable`, including default captures without metadata. The lightweight grid facts do not require font fingerprinting or provenance allocation.

For example, against the observe endpoint of a disposable owned fixture:

```json
{
  "v": 1,
  "id": 1,
  "method": "captureScreen",
  "params": {
    "paneId": "00000000-0000-0000-0000-000000000001",
    "mode": "render",
    "inline": true,
    "scale": 2,
    "presentationTimeMs": 530,
    "includeRenderDetails": true
  }
}
```

Replace the example pane id with the fixture's verified pane id. Ordinary agent captures do not establish ownership of the fixture by themselves.

The synthetic clock selects text and cursor presentation phases. Its half-period is 530 ms: 0–529 ms is visible, 530–1059 ms is hidden, and 1060 ms is visible again. Steady cursors remain visible; application cursor overrides take precedence over the host preference, and default cursor modes use the presenting host's preference. VT cursor hiding still applies. Blink-off hides text and decorations while retaining backgrounds. No request samples, advances or resets the live view's animation clock, focuses a pane or changes its terminal state. These images do **not** measure desktop timing.

The result echoes `presentationTimeMs` and `syntheticSelection` when supplied. A selection
span must be ordered, nonnegative and fit the actual local or imported windowless
grid; malformed spans are rejected before attention/export. A concurrent resize
returns retryable unavailability. Selection rows are resolved against the actual
captured viewport, including scrollback or a scroll between preparation and draw,
and never sampled from the UI selection. Windowless bounds require reading the
remote screen first; that actual read is journaled even when its span is refused,
without exporting a PNG or lighting the read-success indicator. When details are requested, their
`syntheticSelection` comes from the actual selected snapshot rows. Qualification
callers must confirm the requested echoes rather than accepting an older endpoint that
silently ignores a new control.

The MCP client refuses an unconfirmed requested clock or missing requested details.
Default requests and responses omit the new optional controls and echoes.

`renderDetails` schema version 1 contains:

- The app process's platform and architecture.
- Configured and resolved primary font families, size, weight/width/slant, and SHA-256 of the resolved face's complete font stream with its collection index. Fingerprinting runs only for requested details and reads at most 64 MiB. An absent hash means no usable fingerprint was obtained.
- Resolved fallback family names. Their bytes and per-glyph selection are **not** attested. A primary-font hash alone does not qualify fallback glyphs.
- Cell dimensions in DIPs and the actual snapped pixel origin, cell dimensions, baseline, underline and strike offsets. Pixel coordinates and `renderWidthPx`/`renderHeightPx` describe the original draw **before** optional `maxWidth` resampling. Qualification callers should reject `downscaled: true` when asserting original pixel coordinates.
- Snapshot foreground/background/cursor colors, cursor text color and all 256 effective palette entries. Stream override flags distinguish foreground, background, cursor and both selection colors; `paletteOverrideIndices` lists the overridden slots even if their colors equal the host defaults. The captured palette and value metadata are detached from later buffer/theme edits. Background fill uses that same draw snapshot.
- Nullable `selectionBackgroundArgb`/`selectionForegroundArgb` are the effective theme values used for OSC queries, before App color derivation. Explicit null is meaningful: an unset query default may have no concrete reply. `effectiveSelectionBackgroundArgb` is the actual paint tint, which may be host-derived and translucent. Nullable `effectiveSelectionForegroundArgb` means there is no shared selection foreground override; selected cells use their own foreground, subject to per-cell minimum-contrast adjustment. These are distinct facts; a painted selection tint does not imply a concrete OSC 17/19 reply.
- Actual synthetic time, text phase, effective cursor shape/blink/visibility and cursor position; ligature/shaping and minimum-contrast settings.

Windowless captures use their imported terminal snapshot and the font metrics/preferences borrowed from a measured pane. Theme-derived values describe the receiving host's borrowed theme and imported overrides, not independent remote query policy. They remain render-only and do not prove that the same session was displayed on a desktop.

Offscreen capture canvases include the complete snapped cell grid and its pixel origin.
Sizing and the allocation budget use the same snapping as the draw; multiplying
fractional DIP metrics by the column/row count alone can clip the final cells.
`maxWidth` may still explicitly resample the complete image afterward.

Compare controlled fixtures within recorded platform, renderer, font and geometry preconditions. Font hashes do not make rasterization identical across OS or renderer versions. Keep the original PNG, exact input, returned metadata, executable/source hashes and restoration/cleanup evidence with each observation. A state flag or cursor movement alone is not pixel evidence.

These additive color facts and synthetic-selection controls are used by the
[owned runtime color suite](owned-colors.md) for #764. Its
[retained measurement](2026-11-03-colors.md) combines fenced replies, actual theme
sources and selected/unselected PNGs; API metadata alone is not a measurement.

The [owned pixel suite](owned-pixels.md) invokes this API in the disposable runner and retains its PNGs, complete state brackets and metadata. It does not change the frozen stock collector grades (78 supported, 0 unsupported, 177 inconclusive), historical reports or `suiteComplete: false`.

## Opt-in image projection metadata

Raw `captureScreen` can additionally request `includeImageDetails: true` with
`mode: "render"` and `includeRenderDetails: true`. Missing render details or live
mode is malformed before attention/export. The separate result `imageDetails`
has schema version 1; existing `renderDetails` stays version 1 with its existing
shape. Default requests/results omit the new flag/result and produce the same
PNG. This adds no MCP tool, setting, acting permission or native input.

`imageDetails` contains `complete`, `totalPlacements` and `placements` in the
exact draw snapshot's order. At most 512 entries are returned. Overflow returns
`complete: false`, the actual count and an empty array. A complete empty array
means this draw had no active-viewport placement candidates; it says nothing
about retained hidden assets, inactive screens or a remote session's images
omitted from its imported viewport. Qualification callers must reject missing,
incomplete or unknown-version metadata and resampled (`downscaled`) images.

Each placement contains `index`, its current in-process `placementId`, `col`,
`viewportRow`, `widthCells`, `heightCells`, `clipTopRows`, `clipBottomRows`,
`clipRightColumns`, `preserveAspectRatio`, nullable `bitmapWidthPx`/
`bitmapHeightPx`, and nullable `sixelPlacement`. The ID is copied from the same
leased placement, not a process/ownership attestation, and may change on import.
The bitmap dimensions describe the canvas actually submitted during this draw,
not its colored/opaque bounds. Both are null if no bitmap was submitted. Metadata
performs no extra decode, pixel scan or visible-bounds fitting. Cells, clipping,
identity and creation facts come from the same immutable render snapshot; no
later buffer lookup substitutes another placement. All coordinates use the
original draw grid, before optional `maxWidth` resampling.

For a newly parsed SIXEL placement, `sixelPlacement` contains:

- `reservationWidthPx`/`reservationHeightPx`: final positive canvas dimensions
  actually used for cell reservation, after the existing decoder-report clamp.
  They are distinct from the source's declared raster or painted extent.
- `dimensionOrigin`: `sixel-geometry` when supplied by the initial geometry
  measurement, or `image-decoder` when a non-null decoder handle supplied its
  reported dimensions. Deferred decoders may report measured dimensions before
  materializing pixels; this field does not claim decoding already occurred.
- `cellWidthPx`/`cellHeightPx`: exact sanitized float32 denominators selected at
  creation, widened to double before JSON serialization to preserve their values.
- `cellWidthOrigin`/`cellHeightOrigin`, independently: `sixel-override`,
  `parser-metric`, or `default-fallback`. Values include the existing minimum-one
  clamp and default 10/20 fallback; later DPI/parser changes do not replace them.
- `arithmetic: "float32-ceiling-v1"`: reserve cells with ceiling after float32
  division. In JavaScript, independently calculate
  `Math.ceil(Math.fround(canvasDimension / recordedDenominator))`; using only
  float64 division can choose a different cell at an integer boundary.

Creation provenance is immutable BCL-only VT value data, copied into the leased
render snapshot. Generic, directly constructed and imported/legacy placements
have explicit null provenance. Existing state, replay and mux formats do not
grow and cannot invent historical metrics on restore. Such native SIXEL geometry
remains unknown/unqualified even when the image renders successfully.

An independent oracle must first derive its expected canvas from its fixed raw
source and pinned decoder policy, compare that with reservation and actual bitmap
dimensions, recompute the reservation from creation-time denominators, and compare
the resulting cells with the observed placement. It can then project its known
source pixels through those cells using the recorded capture grid and clipping.
For example, three six-pixel bands can produce an 18-row canvas despite only 16
painted rows and a declared raster height of 16. A whole-cell render of that canvas
need not paint 16 capture pixels. Current CSI 16 t or monitor scaling cannot
substitute for these creation facts.

These facts expose existing reservation/projection behavior; they do **not**
promise native-pixel fidelity or change whole-cell stretching. A calibrated
source-to-grid check may still find a real geometry defect. No renderer fix or
runtime qualification is implied by this metadata API, and a completed owned
experiment still requires independent process/PTY proof, fenced pixels and
verified restoration/cleanup.
