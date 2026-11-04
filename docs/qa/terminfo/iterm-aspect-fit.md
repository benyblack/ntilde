# iTerm2 inline-image aspect policy (#770)

OSC 1337 `File=inline=1` now preserves the decoded source aspect ratio by default
and with `preserveAspectRatio=1`. Explicit `preserveAspectRatio=0` keeps stretching
to the requested cell envelope. The [pinned primary specification](https://github.com/gnachman/iterm2-website/blob/d6249e4f778822ac2ec89cc099607a3bc5ef4e3f/source/_includes/3.7/documentation-images.md#L64)
requires this distinction: zero permits distortion, other values preserve aspect,
and the default is one. Unsuffixed requested width/height remain cell counts.

Previously all three forms stretched. The regression sends real asymmetric wide
and tall PNGs through the production parser and Skia decoder into the actual
offscreen App draw path. At 1x, 1.5x and 2x scale, all twelve default/explicit-one
cases fail against the previous production source while all six explicit-zero
controls pass. Each checks the independently blank target, retained colored
controls, every non-target pixel, measured image bounds and interior source colors.
These are deterministic local rendering tests, not owned desktop or cross-platform
runtime qualification. Historical source archives, measurements and grades are
unchanged.

## Ownership and compatibility

VT records the requested policy on each admitted iTerm placement, initialized
independently for every image. The detached render snapshot copies the policy by
value, using the same boolean already projected for Kitty. App reuses its existing
fit path within the original snapped cell envelope, before existing row/column
and viewport clipping. Source dimensions remain bitmap facts; requested cells,
cursor advancement, source handles and lifetime ownership are unchanged. The
existing top-left anchor is retained; no common centered-padding rule is claimed.

Legacy constructors, SIXEL and restored SIXEL keep their existing drawing policy.
Explicit iTerm stretch and Kitty fit remain separate controls. The fit is based on
the entire placement, so clipping removes pixels without resizing the surviving
fragment. Main-screen history translation preserves the policy and source colors.
Clear removes rendered pixels, while an actual registered snapshot session keeps
the same native source bitmap alive until the session ends and retirement drains.

Complete mux/replay state still omits opaque iTerm/Kitty images; it gains no image
handle, layout or schema field. Existing pending OSC state retains the raw argument
and resumes with the same policy after serialization/restore. This change does not
add retained-image replay transport.

## Executable evidence and limits

- [ITermAspectRenderTests](../../../tests/Ntilde.App.Tests/RenderTests/ITermAspectRenderTests.cs)
  covers the eighteen wide/tall/DPI default/one/zero controls, three clipping
  directions, preserved/stretched history translation and snapshot-pinned clear.
- [ITermAspectIntentTests](../../../tests/Ntilde.VT.Tests/ITermAspectIntentTests.cs)
  covers detached projection, per-image defaults, zero/nonzero and invariant-case
  arguments, pending OSC state transfer, legacy/SIXEL defaults and unchanged opaque
  image-transfer exclusions.
- Existing Kitty aspect/lifecycle, SIXEL scrolling and inline decoder tests guard
  the unaffected protocols and handle ownership.

The OSC 1337 matrix row retains its existing **Supported** grade with this bounded
regression evidence. No additional protocol spelling enters the capability
catalogue. `name=`/`size=` enforcement, native-pixel/default sizing, other OSC 1337
variants and owned runtime image measurements remain outside this correction.
Animation, Unicode placeholders and Windows/Linux runtime image qualification
remain open in #770/#775. The separate static-image qualification work is not
imported here, and no parser-only result is presented as rendered support.
