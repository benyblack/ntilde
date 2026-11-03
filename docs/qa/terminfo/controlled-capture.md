# Controlled offscreen captures

`captureScreen` accepts optional controls for renderer qualification.
The MCP tool `ntilde.capture_screen` exposes the clock and details controls;
synthetic selection is currently an AgentHost-only qualification input.

| Input | Meaning |
| --- | --- |
| `presentationTimeMs` | Integer synthetic time from 0 through 60,000 ms, starting with a visible blink phase. Omit it to keep the existing fixed-visible capture behavior. |
| `includeRenderDetails` | Include the actual resolved primary font, pixel grid, palette, color overrides and presentation facts from the draw. Defaults to false. |
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
and never sampled from the UI selection. When details are requested, their
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
- Nullable `selectionBackgroundArgb`/`selectionForegroundArgb` are the effective theme values used for OSC queries, before App color derivation. Explicit null is meaningful: an unset query default may have no concrete reply. `effectiveSelectionBackgroundArgb` is the actual paint tint, which may be host-derived and translucent. Nullable `effectiveSelectionForegroundArgb` means selected cells retain their own foreground when no contrast override is needed. These are distinct facts; a painted selection tint does not imply a concrete OSC 17/19 reply.
- Actual synthetic time, text phase, effective cursor shape/blink/visibility and cursor position; ligature/shaping and minimum-contrast settings.

Windowless captures use their imported terminal snapshot and the font metrics/preferences borrowed from a measured pane. They remain render-only and do not prove that the same session was displayed on a desktop.

Offscreen capture canvases include the complete snapped cell grid and its pixel origin.
Sizing and the allocation budget use the same snapping as the draw; multiplying
fractional DIP metrics by the column/row count alone can clip the final cells.
`maxWidth` may still explicitly resample the complete image afterward.

Compare controlled fixtures within recorded platform, renderer, font and geometry preconditions. Font hashes do not make rasterization identical across OS or renderer versions. Keep the original PNG, exact input, returned metadata, executable/source hashes and restoration/cleanup evidence with each observation. A state flag or cursor movement alone is not pixel evidence.

These additive color facts and synthetic-selection controls prepare runtime-color
reset qualification under #764. They do not by themselves supply owned OSC-reset
measurements; that suite and its retained evidence remain the next slice.

The [owned pixel suite](owned-pixels.md) invokes this API in the disposable runner and retains its PNGs, complete state brackets and metadata. It does not change the frozen stock collector grades (78 supported, 0 unsupported, 177 inconclusive), historical reports or `suiteComplete: false`.
