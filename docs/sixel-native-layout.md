# Native SIXEL layout and transfer (#851)

Direct App panes use presenter-native policy 1. Mux panes use the daemon
session's chosen policy, with 0 for sessions on older peers. Native recording
headers use v3. Legacy placements and v1/v2 recordings keep their original layout.

## Ownership and values

VT owns immutable `SixelRasterIntent` version 1 (decoded canvas and source crop),
the buffer's ordered canonical SIXEL cell pair, integer reservation/cursor
behavior and irreversible domain loss. QA placement provenance supplies none of
these production values. The optional placement intent and canonical pair are
omitted in legacy state. Native imports require a target with the same policy.

The host supplies explicit initial cell metrics before startup bytes. A missing pair refuses
nonempty parser input and buffer/parser export before effects. A valid incoming
native state can initialize an empty target from its explicit validated pair.
Native pairs must be finite and at least the existing one-device-pixel effective
cell floor, preventing unbounded reservation/cursor
loops from tiny positive metrics. Parser and buffer use one buffer-owned pair.
RIS and DECSTR retain it and the immutable session policy. A metric update alone
never changes a placement's stored crop.

Rendering owns the bounded deferred bitmap. Decoder-null native parsing instead
uses a sealed BCL source owner: it reserves conservatively, releases unused
decoder scratch, and never claims materialized pixels. Both owners keep the
existing 256-placement / 64 MiB lifetime authority. Draining or requesting a frame
does not return credit. The headless consumer disposes only the sealed source
owner after leaving VT locks; it never takes a renderer's opaque handle.
Snapshot release and explicit clear also revisit those queued sources. Session
disposal clears native sources after parsing has stopped.

## Drawing and culling

App receives an immutable presenter density **P** from its production context.
Requested capture density **S** is separate:

```text
source crop [l,t,r,b] -> current cell anchor + [l/P,t/P,r/P,b/P] DIPs
live S=P             -> one source pixel per output device pixel
capture S            -> source extent multiplied by S/P
```

The current snapped cell grid moves the anchor; it never stretches the raster.
Missing or unrepresentable P/S, unavailable materialization and bitmap/canvas
disagreement refuse native capture instead of returning a blank successful
result. Detached test contexts are explicit virtual presentation inputs, not a
claim about an observed monitor.

VT receives a pure BCL projection derived from that exact draw grid and P/S.
A placement is selected if its surviving native raster or clearing region
intersects the viewport; reservation height alone is insufficient. Text-only
consumers explicitly omit images. No UI, Skia, native call, clock or file access
is introduced into native geometry.

The legacy whole-cell clearing envelope remains separate from the bitmap crop.
Native drawing applies viewport and source-pixel clips without retaining the
legacy integer-row clip around the raster. iTerm/Kitty layout and null-intent
SIXEL use their existing paths. Neither renderDetails v1 nor imageDetails v1
grows new fields.

## Canonical loss and transfer limits

Only actual ordered domain loss narrows crop. Regional scroll/page edges,
history eviction/ED3 and applicable resize/reflow loss intersect source bounds
using the canonical pair. Left/top never decrease; right/bottom never increase.
Fractional and empty crops are explicit. Reverse scroll, widening, import or
presentation changes cannot restore discarded pixels. A fully clipped integer
reservation may still retain native pixels.

Clear/reset and existing whole-image treatment of deleted/intersected anchors
can retire the entire image; a rectangular crop does not invent a discontinuous
row splice. Coverage retirement requires both the old raster and clearing region
to be contained in the new clear region in canonical stream space. Otherwise it
retains conservatively under the admission ceiling. This shared-stream loss
policy does not claim identical physical occlusion for arbitrarily different
client fonts.

A full state export preserves stored native crop, including latent overhang.
An explicit scrollback cap may add irreversible top loss to the exported copy;
it does not mutate the live placement or infer bottom/right loss from a newer
font/grid. If an image retained by that cut has lost its encoded source, native
export refuses the **whole** state. It never silently omits or downgrades the
image. A materialized bitmap remains usable and charged; export refusal does
not retire it. Later activation consumers must expose this unavailable-state
boundary rather than treating a partial snapshot as success. Legacy omission
behavior is unchanged.

## Import boundary

Native import validates every entry and its source canvas before any resize,
clear, source commit or host decode. It stages private placement facts and
pre-admitted owners from the destination's existing budget; old retired/pinned
owners retain their charges. If the complete source/work set does not fit,
import refuses without changing the destination. Direct native buffer import
requires matching geometry; whole Restore stages first, then resizes.

A host decoder is a synchronous precommit yield. After the last callback,
Restore revalidates the full buffer/parser envelope and canonical agreement,
refreshes decoded validated cell bytes, and checks every staged native fact.
Invalid Modes/Rows/Parser and changed placement/crop/source/pair cannot be mixed
with prior owners. Ordinary concurrent mutation of caller-owned DTOs remains
outside the existing DTO ownership contract.

Rollback disposes known provisional owners outside VT locks. Failed disposal
is queued through the original lifetime authority, still charged and
snapshot-gated; notification failure does not replace the original refusal.
Unknown decoder ownership failure retains bounded credit rather than claiming
unproved native disposal. A prepared native import belongs to one buffer and
one commit; it cannot migrate, be reused or revive after disposal.

## Validation and compatibility

Deterministic tests cover real parser→Skia pixels, P = 1/2 and S = 1/2, wide/tall fonts,
nonzero anchors, non-target pixels, overhang/fully clipped reservations,
fractional and empty crop, history/ED3, rollback, malformed state, synchronous
callback mutation, actual disposal and encoded-source expiry. These are
offscreen/domain witnesses, not an owned desktop qualification.

Mux clients and the daemon intersect their maxima at hello. The selected
policy is immutable for a session and must match at attach and readScreen.
Old peers can continue to create and use policy-zero streams. Ordered cell
metrics precede the output that uses them. Both recording writers preserve
legacy v2 headers and use a required policy marker in v3 for native streams.
Old binaries that predate the header gate may still resume bytes without
recognizing v3.

Animation, placeholders, general Pan/Pad, P2 and different-cell-background
compositing are outside this measurement.
