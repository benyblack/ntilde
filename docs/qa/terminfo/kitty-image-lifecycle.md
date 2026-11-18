# Kitty image identity and lifetime foundation (#770)

This slice implements the basic retained-image lifecycle from the [Kitty graphics
protocol](https://sw.kovidgoyal.net/kitty/graphics-protocol/): `a=t` retains decoded
data, `a=T` also displays it, and `a=p` displays a retained image at the current
cursor. Image ids (`i`) and terminal-allocated image numbers (`I`) are distinct.
Reusing a number allocates another id; subsequent number lookup selects its
newest retained asset. Retransmitting an explicit id replaces that screen's old
data and placements. Placement ids (`p`) replace just that placement.

Successful transmit/display acknowledgements contain the actual numeric id,
original number when supplied and placement id when supplied. Strict unsigned
32-bit parsing excludes signs, whitespace, overflow and controls. Specifying both
`i` and `I` produces a bounded EINVAL reply. Quiet value 1 suppresses success;
value 2 suppresses all replies. Missing stored images produce ENOENT. Unsupported
animation actions, formats and compression have negative replies. Successful
deletion sends no acknowledgement.

## Resource ownership

The VT buffer owns retained opaque handles. Placements borrow them, so soft
`i`/`n` deletion and ordinary screen erase do not schedule disposal while data is
stored. Transmit-only replacement and quota eviction notify the live view when
placements disappear. Weak per-handle records distinguish pending retirement
from disposer ownership: re-admission cancels the old generation, and an already
handed-off handle is refused. An obsolete entry never supplies the timestamp for
a newer retirement. A short private gate coordinates drain and admission without
waiting on the parser lock or running Dispose. The same lifetime state tracks
opaque iTerm2/SIXEL/legacy handles, so earlier non-Kitty retirement cannot bypass
Kitty admission. Generic grace is preserved; fresh handles avoid history scans,
and bulk cleanup unlinks each placement before considering remaining aliases.
Removed SIXEL source text retires independently of shared decoded handles, so
source-budget eviction still favors retired text. Unstored-handle rejection checks
ownership and queues retirement together under the buffer write lock. Rejected
SIXEL admission releases newly retained source text without throwing, moving the
cursor or arming post-image newline suppression. Decoding borrows a provisional
source backed by the bounded parser payload; only admitted placements commit it
to the persistent budget. Rejected frames cannot evict live text, even when the
character or source-count allowance is full. Successful admission publishes the
new handle owner before commit may evict/prune an older placement.
Basic uppercase `I`/`N` deletion releases data once no relevant placement
references it. `a`/`A` deletion affects visible placements; offscreen references
keep their data. A delete command aborts an incomplete upload. Full clear/reset
and complete-state import release hidden assets too. Import does not restore
Kitty assets from snapshots; native Kitty resources remain outside that transfer
contract. Main and alternate screens have independent asset namespaces.

Each screen has a live allowance of 128 assets, 512 placements and 64 MiB charged
at four bytes per decoded pixel. Quota pressure evicts oldest unused assets first,
then oldest live assets. Placements have their own bounded count. This is a live
storage bound, not a claim that snapshot-pinned retired resources disappear early:
they remain subject to the existing snapshot-session disposal gate. Chunked
control metadata is bounded to single ASCII-letter keys and 64 characters per
value; payload and inflation retain their existing bounds.

## Evidence and limits

`KittyImageLifecycleTests` pin exact frames, number allocation/reuse, unsupported
and malformed requests, quiet policy, quotas, reset/import cleanup and concurrent
snapshot protection. Of the first 19 cases, 18 fail against the old implementation.
`KittyImageLifecyclePixelTests` use the production Skia decoder and inspect real
RGBA pixels after placement and soft deletion, and while a hard-deleted image
remains pinned by a snapshot. `KittyImageLifecycleRenderTests` uses the production
offscreen capture path to check painted and neighboring pixels through transmit,
place, soft delete, redisplay and hard delete. These are deterministic local tests,
not a frozen owned desktop measurement or remote child acknowledgement.

Two existing decoder/placement fixtures now request `a=T` explicitly: their prior
omitted action / `a=t` accidentally depended on the old transmit-also-displays
behavior. Existing legacy layout handling remains partial. In particular accepted
extra keys such as `U=1` do not establish virtual placements; animation, Unicode
placeholder rendering, extended source crops/layout/deletion, fresh owned Sixel
and iTerm2 pixels, timed playback, and cross-platform runtime evidence remain in
#770/#775. #770 stays open. Historical stock grades and published evidence remain
unchanged.

## Animation frames and playback (#988)

`a=f` adds a frame to a retained image and `a=a` controls it. Frames are raw `f=24`/`f=32` only
(`o=z` allowed); the rectangle is `x`,`y`,`s`,`v`, composed over a solid `Y` color or base frame
`c`, or over the frame being edited with `r`, alpha-blended or replaced with `X=1`. `z` is the gap
in milliseconds: unspecified or 0 is 40 ms for a new frame, negative is gapless and never shown.
`a=a` sets run state `s` (1 stop, 2 run and wait at the last frame, 3 run and loop), loop count `v`
(0 ignored, 1 infinite, n plays the animation n-1 times in total, reset by stop), a frame's gap (`r` with `z`) and the
displayed frame (`c`). An animation starts stopped; the playhead only moves after `s=2` or `s=3`.
Time comes from `TerminalBuffer.KittyAnimationClock` (a monotonic millisecond clock); the live
`TerminalView` advances the buffer on each frame and arms one wake-up for the next change, only
while an animation with a placement is running; a retired view arms none, and a replay window holds every animation (`TerminalBuffer.KittyAnimationPaused`): commands and explicit `a=a,c=` frame choices still apply, but nothing advances by time, because the playheads would follow a wall clock rather than the recording's time. The view arms a wake-up only for a placement in the rows on screen, so an animated image scrolled into history costs no redraws. A clock that jumps far ahead skips whole cycles in one step instead of replaying them, a gapless frame is passed through and never shown (loading mode parked on one keeps the last visible frame), and loading mode resumes from the moment a new frame arrives.

Deliberate bounds, each with its own negative reply: 64 frames per image including the root; a
frame is charged at 8 bytes per pixel (decoded pixels plus the RGBA kept for later composition)
against the same 64 MiB screen quota and is refused with ENOSPC rather than evicting everything;
a frame is raw `f=24`/`f=32` or a container (`f=100`, PNG): a container frame's size is read from its header first (a frame that cannot fit its place is refused before
anything is decoded) and the host decoder reads the decoded pixels back through `IImageDecoder.TryReadPixels` (a decoder without it, or no decoder, answers
ENOTSUP). The root frame keeps no pixels of its own: `c=1` reads them back from the root's decoded handle when a frame is built on it, and `r=1` replaces the root with
the edited pixels (the new root keeps its RGBA, charged 4 bytes per pixel since its decoded pixels were already the asset's, and its gap); where the root cannot be read back
(an undecoded root, or a decoder without `TryReadPixels`) `c=1`/`r=1` answer ENOTSUP. An absent `f` returns ENOTSUP. The root frame's gap is 0 by the
protocol; the playhead shows it for the 40 ms default (an implementation policy, not a protocol
statement). Frames are released with their image (replace, hard delete, reset).

**Frame composition, `a=c` (#1027).** `a=c,i=,r=<source frame>,c=<destination frame>` composes a rectangle of one frame onto another, the way the protocol
specifies (`w`,`h` size, default the whole image; `X`,`Y` source offset; `x`,`y` destination offset; `C=1` replaces pixels, alpha included, instead of the default alpha blend).
The result **replaces the destination frame in place** (the root included, which then counts as edited), so the frame count, the destination's gap and the playhead position do not
change, and it is charged exactly as an `a=f` edit of that frame: a replacement releases the pixels it replaces, but the first composition into an unedited root adds that root's kept RGBA
(4 bytes per pixel, as the first `r=1` edit does), and in a persistent session the replacement's packed source (`KittyFrameCodec`) can be longer or shorter than the one it replaces. Validation order: an unknown image or a missing
frame (an absent, zero or too-large `r`/`c`) is `ENOENT`; an invalid control or `C` is `EINVAL`; a source or destination rectangle that leaves the image, or one frame composed onto
itself with overlapping rectangles, is `EINVAL`; quota pressure is `ENOSPC`, decided before any pixel is read; pixels that cannot be read (an evicted source, or a root without a
decoder in an ordinary tab) are `ENOTSUP`. `a=q,e=c` answers OK. A persistent session needs persistent image policy 4 (`PersistentImagePolicy.CompositionImagesVersion`,
`RetainedComposition`): under policy 3 `a=c` answers `ENOTSUP` and `a=q,e=c` answers `ERR`, because a peer that predates composition would keep a different frame, so the
policy is fixed at spawn like the others: a persistent session created before an update keeps the policy it was spawned with (3) and keeps answering `ENOTSUP` to `a=c` until it is closed and a
new one is started, while ordinary tabs compose at once. A composed frame is an ordinary frame, so snapshots carry nothing new and a policy-3 snapshot restores under policy 4.

**Persistent sessions (#1017).** Where images are retained for snapshots (`RetainImageSources`) animation needs persistent image policy 3
(`PersistentImagePolicy.AnimationImagesVersion`, `RetainedAnimation`); under policy 1 or 2 `a=f` and `a=a` answer `ENOTSUP` and `a=q,e=f`/`e=a` answer `ERR`, as before, because a
peer that predates frames would show a still image and could not restore a snapshot that carries them, so a session's policy is fixed at spawn, every attach must name it, and an older
peer is refused rather than downgraded. A persistent session's daemon has no image decoder while its clients do, and all of them must accept, refuse and charge a frame from the same
bytes, so nothing here asks a decoder for pixels (`IImageDecoder.TryReadPixels` is the ordinary tab's route): a container frame is read by `PngPixelReader` (a decoder-independent PNG
reader; any other container answers `ENOTSUP`), the root's pixels come from the bytes it was sent as (raw directly, a PNG through the same reader), and each frame's pixels are kept only as
their packed form (`KittyFrameCodec`, whole-pixel runs, deliberately not a runtime compressor: its length is charged to the shared image-source budget and must be identical on every
side) as an entry of that budget. Snapshots therefore stay inside one mux frame, a source the budget evicts leaves its frame unavailable as a base (`ENOTSUP`, on every side alike) and
carried without pixels, and an edited root's pixels are its own packed source (the image's original bytes are not used in their place). A snapshot carries each frame (gap, packed pixels,
whether the root was edited), the playback state (state, current and displayed frame, loops left, whether it is parked) and the time the current frame had been up, from which the restoring
side resumes its own clock; a restore charges every frame exactly as the source did (8 bytes per pixel, 4 for an edited root), validates the packings and indices without inflating them,
and decodes the frames when a decoder exists (the displayed placement stays undecoded until then). A snapshot with frames is refused by a session whose policy is below 3 (composition, policy 4, adds no snapshot content).
A live persistent tab (policy 3, the pane a client of the multiplexer daemon) is measured on the desktop by the owned animation suite ([#1025](2026-11-17-kitty-animation-persistent-qualification.md));
restoring a tab from a snapshot after the App is killed and started again is measured the same way ([#1030](2026-11-17-kitty-animation-restore-qualification.md)), and `a=c` composition in a live policy-4 persistent tab too ([#1027](2026-11-18-kitty-animation-compose-qualification.md)). Why the PNG-only and frame-budget limits stay: [#1026](kitty-persistent-animation-limits.md).

`KittyAnimationTests` (VT) pin the frame store, composition arithmetic, error replies, quotas, the
playhead on an injected clock and frame retirement; `KittyAnimationRenderTests` check the real view
paints the frame the clock selects. These do not establish wall-clock playback on the desktop; that
belongs to the owned measurement (`kitty-animation-model.md`). The pinned
`extensions.kitty-graphics.animation` probe can only return inconclusive whatever Ntilde does.
