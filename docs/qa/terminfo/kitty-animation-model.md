# Owned Kitty animation suite: model, oracle and timing (#988)

Suite `ntilde-owned-kitty-animation-v1`, policy `owned-kitty-animation-paired-raw-render-v1`, run after the static-image suite
in the same owned collection (source policy v63, receipt schema 70). The measured behavior is the Kitty graphics animation
playback added for #988 ([contract](kitty-image-lifecycle.md)); this document defines what the measurement asserts.

## What is measured

Three bounded cases over one 16x16 image placed at the controlled anchor (4x2 cells, column 3, row 4), whose frames are solid
red (root), green and blue. Every frame is held 5000 ms (`z=5000`, and `a=a,r=1,z=5000` for the root), so a capture of about a
second sits well inside one hold.

| Case | Stages | Claim |
|---|---|---|
| `kitty.animation.stopped-hold` | blank, build, hold, clear | Frames added with `a=f` and an unstarted animation stay on the root frame after the root's gap has elapsed (stationary control). |
| `kitty.animation.playback` | blank, build, start, second, third, loop, clear | After `a=a,s=3` elapsed time selects root, then green, then blue, then the root again. |
| `kitty.animation.stop-freeze` | blank, build, start, second, stop, hold, clear | After `a=a,s=1` the displayed frame stays frozen past a hold that would have advanced it. |

Each stage is one admitted write followed by a paired, original offscreen capture of the live view plus complete cell/mode reads
and a freshly fenced raw reply read, exactly as in the static-image carrier. `build` is one `a=T` plus two `a=f` plus one
`a=a`; all four replies must be `OK` and correlated to the parent-chosen image id.

## Oracle

The expected frame is derived from the protocol and the retained wall-clock facts, never from the App:

- **Pixels.** The non-background content in the target envelope must be the undistorted 32x32 fit of the source, and its interior
  (clear of the antialiased edge) must be exactly one palette color. Blank stages must have no content; nothing outside the
  envelope may change from the independently blank baseline.
- **Timeline.** `framesPossibleAt` gives every frame a looping animation could show for any start time inside the bracket in
  which `a=a,s=3` could have taken effect (write admission to the read request, which follows the raw reply) and any instant of
  the paired capture span. A running read counts only when that set is exactly the stage's planned frame; a straddled boundary
  or a late read makes the stage **inconclusive**, never failed. A stopped hold counts only if the stop landed inside the
  planned window and a running animation could not still show that frame at the capture. `stopped-hold` requires the capture to
  begin at least one root gap after the build took effect.
- **Grading.** Stage results are passed, failed or inconclusive. A negative or missing reply, and bleed outside the envelope,
  fail regardless of timing; a pixel mismatch fails only with valid timing and passed witnesses. A case fails if any stage
  fails and is inconclusive if any stage is.

## Collection

The collector sleeps before admitting a read: a hold waits out its gap after the previous read; a running read aims its
capture centre at its target offset (1.0, 7.5, 12.5, 17.5 s, or 6.2 s before the stop) from the `a=a,s=3` admission, assuming the
centre trails admission by 60% of the last measured read. A parent admission after the wait brackets the command's effect, so
waiting never widens the start uncertainty. The planned reads are centred in their holds, so about 2.5 s of latency either way
is tolerated. Presentation time is not synthesized: frames advance with the App's monotonic clock.

## Limits

One image size, three solid frames and a 5 s gap. This establishes playback by elapsed time in this App build and run; it is
not a frame-rate, display-refresh, cross-platform, persistent-session or `a=c` composition claim. The pinned
`extensions.kitty-graphics.animation` callbacks always return inconclusive and are replayed unchanged by the publication.
