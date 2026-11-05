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
  header/resize callbacks, replay bytes or resume seeks. A recognized recording
  type cannot be hidden by another `type` key: all occurrences are inspected and
  ambiguous recognized headers are rejected before legacy fallback. Ordinary v2 writers and
  legacy placement/header bytes stay unchanged. No v3 writer is added.
  App uses the same validation before indexing snapshots and keeps that reader
  open through indexing. For seek/playback, Replay opens and validates once, then
  invokes a cancellation-aware host callback before resize/header/data callbacks
  or resume seeks. App restores either the cached checkpoint or the initial state
  there; the same selected stream remains open through restoration and consumption.
  Cancellation, declined preparation and callback failure dispose the reader.
  Queued data, resize and metric updates carry the same run token to UI dispatch;
  final position/error cleanup explicitly opts out while retaining its session gate.
  Path replacement cannot switch the stream between validation/restoration/replay.
  Existing live append (`FileShare.ReadWrite`) remains supported. This is not a
  frozen-byte guarantee against arbitrary concurrent in-place file modification.
  Current readers now also reject recognized recording headers with missing,
  malformed, duplicate or unsupported versions before indexing, preparation,
  seeks or data callbacks. Recognized v3/future headers cannot fall through to
  the v1 event reader, even without a required-policy marker. A complete recognized
  root type token in a valid JSON prefix also rejects later syntax errors before
  byte resume/preparation; nested properties and quoted lookalike text are not
  classified as headers. Historical v1
  event streams and recognized v2 policy-zero recordings remain supported.
  This does not repair old binaries: pre-foundation byte-offset resume readers
  may bypass a v3 header. Native recordings will require upgraded readers.

## Session policy admission

The buffer's creation policy is immutable and selected before startup bytes.
`TerminalBuffer(cols, rows)` remains legacy; the explicit policy constructor
currently rejects every nonzero policy. RIS and DECSTR cannot change it. The
omitted-default `required_sixel_raster` snapshot marker is validated before
resize, import or native decode, and must match the destination generation.
A historical recording selects legacy when its host buffer is created/replaced,
never by downgrading an already-running native generation. No setter toggles
policy from terminal input or presentation changes.

Mux hello/welcome carries an omitted-zero maximum. Spawn/attach/readScreen carry
an omitted-zero requirement; spawn replies and snapshots retain the requirement.
Absent peer capability means zero. Explicit unsupported requests fail before
factory creation, subscription, geometry changes, process probes or snapshots.
The shared attach/readScreen client decoder checks the negotiated ceiling before
delivery, including native entries a later source budget would discard; an opened client session retains its immutable required policy across
reattach. Reconnect starts a fresh negotiation, not a silent policy downgrade.
Current native ceilings remain zero. `SupportsSixelScrolling` and mux protocol 2
are not native policy capability. Contracts remains a zero-reference leaf.

Snapshot, replay and mux source-generated readers reject duplicate properties, preventing
a requirement from being hidden by last-key-wins. Valid absent/default/null-intent
legacy serialization stays byte-identical; no writer version or schema is bumped.
No successful native session/state transfer is claimed by these negative gates.

The finite remaining implementation path is: complete native admission/lifetime,
persistent crop/culling/coverage and P/S transform together while public creation
stays disabled; then enable the coherent path with upgraded mux parity and v3
reading/writing only for native recordings. Both ordinary and flight recorders
must retain v2 for legacy policy. New source/pixel evidence accompanies activation;
the preserved historical negative is not rewritten.

## Bounded decoder and lifetime implementation (still inactive)

The next implementation slice is exercised only by an internal test buffer factory.
Public creation, import, mux advertisement and replay admission still have ceiling
zero; no terminal escape sequence can select this test path. Native intent creation,
persistent crop and physical-pixel sizing remain the next focused slice. The
original two negative fixed-metric renderer controls and frozen measurement stay
unchanged. Native recordings and capability activation follow only when the complete
path is coherent; this is operational admission code, not a native-pixel grade.

A native owner reserves one identity plus peak pixel storage **before** decoder
invocation or cursor effects. Its session budget permits at most 256 admitted
identities/placements and 64 MiB across live, deferred, retired and snapshot-pinned
owners. A budget binds once to one buffer generation and cannot be shared or
migrated; handle retirement and snapshot gates stay under that same owner. Shared opaque handles add placement charges without charging their bitmap
twice. These conservative alias charges remain until the shared owner is disposed.
An admission refusal has no decoder, source-retention or cursor side effect. Hosts
without the bounded decoder contract cannot fall back to the legacy decoder.

The native decoder uses one fixed ushort register plane, a fixed 4096-entry uint
palette matching the existing register cap, and an exact-row-stride BGRA bitmap.
It parses spans without growing bands, input-sized command lists or substrings.
Reservation is `6 × W × H + 4096 × 4` bytes, with dimensions capped by
`SixelGeometry.DefaultMaxPixelDimension`. The existing retained-source character
budget independently limits input. Deferred owners retain the whole reservation,
so materialization never waits for scratch promised to another deferred owner.
After decoding returns, scratch credit is released; `4 × W × H` remains until
successful actual bitmap disposal. This bounds owned pixel storage and decoder
work, not whole-process RSS, allocator overhead or GPU caches.

Native decode state is published without taking the native decode lock. Source
pruning under the VT write lock observes one state value, preserves in-flight
owners, and retries skipped expired sources after completion (including failure)
even without another source eviction. Native decode captures its immutable input
before publishing in-flight state; metadata reads never wait for that work.

The existing image-retirement registry and snapshot sessions protect native owners.
Removing a placement, evicting its source, ending a snapshot or draining a retired
handle does **not** release pixel credit. `NativeSixelImage.Dispose` releases only
after its owned bitmap and source are disposed. No native disposal runs under the
buffer or budget gate. Failed disposal retains credit; the actual view requeues
that native owner in its original buffer for a later disposal attempt. Handoff
stays irreversible, retry generations cannot duplicate pending work, and a retry
never readmits the handle. Final native retirement is irreversible even before
handoff: callers need a fresh owner after the final placement/asset retires. Active
native aliases remain supported; nonnative handles retain their historical pending-
retirement cancellation behavior. An undecoded native source stays live while another
placement or retained asset still references its owner. Unknown decoder
exceptions or foreign reservation/source responses fail closed without crediting
unproven disposal or destroying a possibly borrowed host handle. These internal
contracts constrain the built-in decoder; they do not attest arbitrary host code.
Legacy decoding, placement geometry, serialization and the raw v1 capture schemas
retain their existing behavior.

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
