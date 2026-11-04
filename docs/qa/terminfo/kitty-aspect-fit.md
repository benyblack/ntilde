# Kitty placement aspect fit (#770)

Kitty images now retain the decoded source aspect ratio inside the existing
placement cell rectangle. This is a focused rendering correction for the
[primary c/r contract](https://github.com/kovidgoyal/kitty/blob/3dc4f8d5aa0092b77c839145aff5602ca8664cd4/docs/graphics-protocol.rst#L680),
which requires undistorted fit when both cell dimensions are supplied. It does
not redefine the reserved cells or native source dimensions.

Previously a 4×2 source in a 44×48 pixel cell envelope was drawn as 44×48, instead
of a 44×22 undistorted image. Real offscreen regressions reproduce both wide and
tall distortion at 1×, 1.5× and 2× rendering scales, plus anonymous Kitty images.
All seven fail against the previous production source while iTerm explicit
stretch, SIXEL and generic-image controls pass. These are deterministic local
parser/decoder/render tests, not owned desktop or cross-platform measurements.

## Ownership and geometry

VT already distinguishes Kitty placements with `KittyImageId`, including zero
for anonymous images. Its render snapshot now copies an aspect-preservation
semantic flag. The flag is a value in that snapshot and cannot change when a
later placement/source identity changes. It adds no UI reference or I/O to VT.

App fits the actual bitmap within the original cell envelope using the draw's
snapped pixel grid. The envelope is still determined by VT cell placement and the
presenting host's metrics/DPI. Source pixel width/height remain bitmap facts;
cell count, cursor reservation and source storage are unchanged. The image keeps
the existing top-left anchor. The tests require containment and source aspect;
they do not claim that the protocol requires a common centered-padding policy.

Fit is calculated before existing viewport and row/column clipping, using the
full original placement rectangle. Clipping therefore removes pixels from that
fitted image instead of resizing its remaining visible portion. Main-screen
history translation retains the same ratio and source sampling. Images remain
in the existing image pass after text/cache rendering; each full envelope is
cleared as before, so cached replacement cannot retain old letterbox pixels.

SIXEL, iTerm2 and generic images keep their existing drawing policy. In
particular iTerm `preserveAspectRatio=0` remains a distinct stretch contract;
the subsequent [iTerm aspect correction](iterm-aspect-fit.md) covers its default
preserve behavior independently. SIXEL native-pixel sizing remains unqualified.
Other Kitty source crops, offsets, default native-pixel sizing and extended layout
remain separately unqualified.

## Executable evidence

- [KittyAspectRenderTests](../../../tests/Ntilde.App.Tests/RenderTests/KittyAspectRenderTests.cs)
  routes actual Kitty bytes through the production Skia decoder and offscreen
  draw path. It measures colored image bounds, checks asymmetric color interiors
  and compares every non-target pixel against an independently blank reference.
  A colored control outside the target must survive. Integer/fractional DPI,
  anonymous ids, non-Kitty controls, clipping, history and cached replacement
  have dedicated cases.
- [KittyAspectSnapshotTests](../../../tests/Ntilde.VT.Tests/KittyAspectSnapshotTests.cs)
  checks anonymous/named Kitty and non-Kitty projection, unchanged handles,
  cells/clipping and snapshot stability across later identity mutation.
- The existing lifecycle render test samples the actual square source interior
  and also checks the lower reserved-cell padding. Its old point lay in the
  stretched part of that cell rectangle.

Cached replacement retains an actual snapshot session while the old source is
replaced. Its bitmap remains readable until that registered session ends, then
normal retirement/drain disposal applies. No handle ownership, cleanup grace,
retained-asset serialization or replay transfer policy changes. Existing image
lifecycle, clipping and decoder suites remain integration guards.

The capability row stays **Partial**, with updated executable evidence and a
regenerated report. No new catalog-owned escape spelling is introduced; the
capability catalogue remains unchanged. The separate prototype static-image
Node model is not imported or mixed into this correction. Animation, Unicode
placeholders, actual owned pixels and Windows/Linux runtime qualification remain
open in #770/#775. Historical evidence and stock grades remain untouched.
