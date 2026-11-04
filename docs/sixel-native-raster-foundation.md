# Native SIXEL raster foundation (#851)

This is an inactive compatibility foundation, not a native-pixel support claim.
Current SIXELs still use whole-cell drawing. The confirmed fixed-metric reproduction
in [#851](https://github.com/benyblack/ntilde/issues/851) remains a separate rendering
defect: a 16×18 decoded canvas containing 16×16 painted pixels reserves 2×1 cells
at 10×20 creation metrics and currently paints 20×18 rather than 16×16.

The intended production policy is presenter-native pixels: source pixels retain
their physical extent when fonts change. The cell anchor may move. DEC raster
attributes and cursor reservation do not justify scaling the raster to the rounded
cell envelope. See [DEC graphics chapter 14](https://vt100.net/docs/vt3xx-gp/chapter14.html)
and the pinned xterm [source-pixel draw loop](https://github.com/ThomasDickey/xterm-snapshots/blob/9489b2056ee51fa9dd6a7087483b9b8f85d6a0c4/graphics.c#L943-L1008).
This does not qualify arbitrary Pan/Pad, P2 transparency, animation or placeholders.

## What this stage establishes

- `SixelRasterIntent` version 1 holds immutable bounded decoded canvas dimensions
  and immutable source-pixel crop. Crop coordinates can be fractional or empty,
  must be finite/ordered/nonnegative and must fit that canvas. Dimension ceilings
  reuse `SixelGeometry.DefaultMaxPixelDimension`.
- A placement's optional `native_raster` DTO field is omitted when null. There is
  no active production placement/snapshot field yet: no parser creates native
  intent, and import rejects every non-null native DTO before resizing, changing
  the buffer/parser or decoding. Rejection also covers budget-discarded entries.
  Null/absent state keeps existing legacy sizing. QA provenance never supplies
  production state or reconstructs lost intent.
- `SixelRasterPolicy.EnabledVersion` is zero. The separately named version 1 is a
  schema identifier, not a supported capability. It is never advertised by mux.
- App's detached presenter context carries device pixels per view DIP (**P**) from
  the actual attached view. It is published with measured font/grid values and
  withdrawn on detach. Requested offscreen scale (**S**) remains independent;
  detached/never-attached views have no known P. Captures carry the value internally
  without using it for sizing or extending `renderDetails`/`imageDetails` v1.
- Replay's `required_sixel_raster` header field is omitted at zero. Readers reject
  every nonzero, malformed or duplicate requirement on recognized headers before
  header/resize callbacks, replay bytes or resume seeks. Ordinary v2 writers and
  legacy placement/header bytes stay unchanged. No v3 writer is added.

## Gates before enabling version 1

The later live transform must use W/P by H/P DIPs, so a live surface with S=P
paints W×H device pixels and offscreen captures paint W·S/P by H·S/P. Missing P
must refuse native capture rather than infer it from capture scale, current parser
metrics or QA metadata. Bitmap dimensions must agree with production intent.
Font/interface/monitor changes and capture-scale changes need independent tests.

Native raster overhang must participate in culling independently of integer cell
reservation. Monotonic source crop must preserve irreversible scroll/margin/page/
resize loss and prevent resurrection after widening or scrolling back. Canonical
ordered stream cell metrics define that shared clipping policy; clients may have
different local fonts. Presentation alone must not shrink or expand crop. Existing
whole-envelope clearing, legacy/SIXEL reservation and cursor movement stay explicit
separate contracts. Coverage retirement must account for native extent.

Admission must reserve bounded peak decoder work before decode, and account for
live, retired and snapshot-pinned bytes/identities until actual disposal. Reuse the
existing handle/source lifetime owners and snapshot sessions; invoke no arbitrary
dispose callbacks under buffer/private locks. Parser→Skia→offscreen regressions,
overhang/crop/non-resurrection controls and disposal/lease tests precede activation.

Mux must negotiate version 1 before native session creation/attach and apply it
before catch-up bytes. Absent capability remains legacy; old clients must fail
before receiving native session state. Upgraded text clients may understand state
without rendering images. Replay v3 should be written only when version 1 semantics
are required, with explicit required-policy validation before data; ordinary legacy
recordings continue writing v2 and exact historical v1/v2 remain readable. An
optional v2 marker alone cannot protect an old reader that ignores unknown fields.

Any future QA exposure needs a separately versioned opt-in schema and new owned
measurements. Synthetic foundation tests neither qualify pixels nor alter grades.
