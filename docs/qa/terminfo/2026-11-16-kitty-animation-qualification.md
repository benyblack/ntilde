# Kitty graphics animation playback, implemented and qualified (2026-11-16, extended 2026-11-17)

Issue [#988](https://github.com/benyblack/ntilde/issues/988) (playback) and [#1015](https://github.com/benyblack/ntilde/issues/1015) (PNG frames and the root frame as a base or edit target), parent [#770](https://github.com/benyblack/ntilde/issues/770). This covers the one exact `terminfo.dev@4.0.0`
case `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probe hash `16a690f34c0e`).
The audit found Ntilde had **no** Kitty animation (`a=f`, `a=a` and `a=c` answered `ENOTSUP`), so this change implements a bounded frame lifecycle
([contract](kitty-image-lifecycle.md)) and then measures it with a new owned suite ([model, oracle and timing](kitty-animation-model.md)) on a real desktop.
The case sits outside the frozen Core catalogue, so **no Core percentage changes**, and the stock grade stays inconclusive.

#1015 lifted two of the production limits #988 accepted: a frame can be a PNG (`f=100`, decoded through `IImageDecoder.TryReadPixels`), and the root frame can be a base (`c=1`) or an edit target (`r=1`). The owned suite gained a
fourth case measuring them, so the suite, policy, qualification and effect contract are v2 and the previous run (#988, three cases) is retained as superseded. Persistent sessions still refuse animation ([#1017](https://github.com/benyblack/ntilde/issues/1017)).

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-playback-effect-v2`) | inconclusive |

The four result kinds stay separate. Both pinned callbacks are replayed from the retained pinned bundle (two source spans pinned by SHA-256) and return a
constant inconclusive observation whatever the App declares: the termless path says "Declared Kitty graphics capability does not establish timed animation
playback", the terminal path "No timed frame playback or pixel readback for Kitty animation". The divergence is recorded as `pinned-inconclusive-by-design`.
The stock collector, re-run in the same owned run, still grades the case inconclusive (161 inconclusive / 93 supported / 1 unsupported, `suiteComplete=false`),
so this **does not raise the stock compatibility score**. A missing pinned verdict was never read as a failure: the unchanged callbacks cannot emit
supported or unsupported.

## Effect contract

`ntilde-kitty-animation-playback-effect-v2` composes four carrier cases of `ntilde-owned-kitty-animation-v2`, all required:

- `kitty.animation.stopped-hold`: a retained 16x16 image with two further `a=f` frames (5000 ms gaps, root gap 5000 ms) stays on its root frame past that gap while stopped.
- `kitty.animation.playback`: after `a=a,s=3` the image shows the second frame, then the third, then the root again, each read inside its planned 5 s window.
- `kitty.animation.stop-freeze`: after `a=a,s=1` the image stays on the frame it showed past a hold a running animation could not have survived.
- `kitty.animation.png-and-root-frames`: a half-transparent green PNG frame built over the red root (`c=1`) shows `(127,128,0)` when chosen with `a=a,c=2`; after the root is edited blue (`a=f,r=1`) and shown again, a half-transparent red PNG built over it shows `(128,0,127)`, not the red-over-red an unedited base would give. The expected colors are the protocol's straight-alpha blend of the independently encoded PNG inputs, within a 2-per-channel rounding step; each frame is chosen explicitly, so the case involves no elapsed time.

All 24 stages passed (blank before, correlated `OK` replies for every write, source-derived frame colors at the controlled anchor with unchanged surroundings, blank
again after each clear), and every validity control held: receipt binds the report, raw reader healthy, state restored, matching controlled background, fresh paired
fences never reused from the input or static-image phases, blank-before controls, acknowledgement separate from pixels, and every timed read inside its window.

## What the wall-clock evidence shows

Frames advance with the App's own monotonic clock; the suite does not synthesize presentation time. The collector waited so each paired capture would land mid-window.
Measured against the bracket in which `a=a,s=3` could have taken effect (about 50 ms wide), the playback captures covered 7.48-7.55 s (second frame window 5-10 s),
12.45-12.52 s (third, 10-15 s) and 17.47-17.55 s (root again, 15-20 s). In `stop-freeze` the stop took effect at 6.25-6.27 s, inside the second frame, and a capture
12.4 s after the start still showed the second frame, where a running animation would have shown the third. The stationary control's capture began more than one
root gap after the frame was built and still showed the root.

## Limits

This is playback by elapsed time on one App build (App and scripts revision `cf59628f`, main merged in), one desktop, one image size, one gap length and one PNG encoding (8-bit RGBA, non-interlaced). It is
not a frame-rate or display-refresh claim, and says nothing about other container formats or PNG encodings, `a=c` composition, persistent sessions (animation is
refused there, `ENOTSUP`, because their snapshots keep the root frame only), remote/mux, other platforms or the common-harness comparison. Those production limits are listed in the
[lifecycle contract](kitty-image-lifecycle.md), and `a=q,e=c` answers `ERR`.

## Retained evidence and superseded attempts

`2026-11-16-kitty-animation/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy, and the generated
`summary.json` and `report.json`. The run is `99d639356e6c79215fd29c5cd655b4b8` (stock collector run `b0d928355c08946d7180c3f77bacfcc9`), source policy v64,
receipt schema 71, collected with the v4 `osc633` scope whose predecessor archive is the existing pinned full run. `kitty-animation-publication.test.mjs` rederives the complete
receipt and the projection from the archive and refuses an altered binding, journal line, pixel, input or marker.

Thirteen earlier runs are retained under `superseded-attempts/` with their real outcomes. Four ended without a grade: the display was unavailable twice, so the App aborted
before collecting (Avalonia RenderTimer error -6661); the observer's prior-challenge allowance covered the input phase only; and the expected `a=T` reply
shape omitted the placement id the App correctly echoes (`i=<id>,p=1;OK`). Eight complete 18/18 runs of the three-case suite, on the Apps built from `479a5465`, `b4bcba0d`, `be8add66`, `a706d8a1`, `efed4bf8`, `78ab1d2b`, `de376515` and `9bc1fd9c` (the run #1013 merged),
are kept as superseded rather than failed: five rounds of review of the playhead, the view and the measuring scripts found real defects (finite-loop counting, catch-up after a
long gap, loading resume and gapless display, wake-ups on retire and for an animation that cannot move, settling before gap changes and refused frames,
boundary-straddling capture pairs, challenge uniqueness, eviction ordering, rejected frame handles, bleed checks on both pair members, snapshot parity in persistent sessions), so each fix
set rebuilt the App and repeated the whole collection, as did the CI and SonarCloud fixes after the pull request opened. The published run is therefore a fresh collection on the final App and scripts, and none of the fixes changed a measured verdict. Two more complete runs are superseded for #1015 rather than because anything failed: the three-case run #1013 merged (`9bc1fd9c`), and the first four-case run (`4f4b294e`), repeated because merging main brought in the integrated OSC cell-size run, whose v1 animation evidence made the suite versioned (v1 stays rederivable beside v2), and because the final review and SonarCloud follow-ups changed measured scripts and the frame parser.
