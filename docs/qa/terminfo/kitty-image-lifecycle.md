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
while an animation with a placement is running; a retired view arms none. A clock that jumps far ahead skips whole cycles in one step instead of replaying them, a gapless frame is passed through and never shown (loading mode parked on one keeps the last visible frame), and loading mode resumes from the moment a new frame arrives.

Deliberate bounds, each with its own negative reply: 64 frames per image including the root; a
frame is charged at 8 bytes per pixel (decoded pixels plus the RGBA kept for later composition)
against the same 64 MiB screen quota and is refused with ENOSPC rather than evicting everything;
the root frame's pixels are not retained, so `c=1`/`r=1` return ENOTSUP; `f=100`, an absent `f`
and `a=c` composition return ENOTSUP, and `a=q,e=c` answers ERR. The root frame's gap is 0 by the
protocol; the playhead shows it for the 40 ms default (an implementation policy, not a protocol
statement). Frames are released with their image (replace, hard delete, reset); they are not carried
by snapshots or persistent-session replay, which keep the root frame only.

`KittyAnimationTests` (VT) pin the frame store, composition arithmetic, error replies, quotas, the
playhead on an injected clock and frame retirement; `KittyAnimationRenderTests` check the real view
paints the frame the clock selects. These do not establish wall-clock playback on the desktop; that
belongs to the owned measurement, which has not been collected. The pinned
`extensions.kitty-graphics.animation` probe can only return inconclusive whatever Ntilde does.
