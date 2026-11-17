# Kitty graphics animation playback, implemented and qualified (2026-11-16)

Issue [#988](https://github.com/benyblack/ntilde/issues/988), parent [#770](https://github.com/benyblack/ntilde/issues/770). This covers the one exact `terminfo.dev@4.0.0`
case `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probe hash `16a690f34c0e`).
The audit found Ntilde had **no** Kitty animation (`a=f`, `a=a` and `a=c` answered `ENOTSUP`), so this change implements a bounded frame lifecycle
([contract](kitty-image-lifecycle.md)) and then measures it with a new owned suite ([model, oracle and timing](kitty-animation-model.md)) on a real desktop.
The case sits outside the frozen Core catalogue, so **no Core percentage changes**, and the stock grade stays inconclusive.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-playback-effect-v1`) | inconclusive |

The four result kinds stay separate. Both pinned callbacks are replayed from the retained pinned bundle (two source spans pinned by SHA-256) and return a
constant inconclusive observation whatever the App declares: the termless path says "Declared Kitty graphics capability does not establish timed animation
playback", the terminal path "No timed frame playback or pixel readback for Kitty animation". The divergence is recorded as `pinned-inconclusive-by-design`.
The stock collector, re-run in the same owned run, still grades the case inconclusive (161 inconclusive / 93 supported / 1 unsupported, `suiteComplete=false`),
so this **does not raise the stock compatibility score**. A missing pinned verdict was never read as a failure: the unchanged callbacks cannot emit
supported or unsupported.

## Effect contract

`ntilde-kitty-animation-playback-effect-v1` composes three carrier cases of `ntilde-owned-kitty-animation-v1`, all required:

- `kitty.animation.stopped-hold`: a retained 16x16 image with two further `a=f` frames (5000 ms gaps, root gap 5000 ms) stays on its root frame past that gap while stopped.
- `kitty.animation.playback`: after `a=a,s=3` the image shows the second frame, then the third, then the root again, each read inside its planned 5 s window.
- `kitty.animation.stop-freeze`: after `a=a,s=1` the image stays on the frame it showed past a hold a running animation could not have survived.

All 18 stages passed (blank before, correlated `OK` replies for every write, source-derived frame colors at the controlled anchor with unchanged surroundings, blank
again after each clear), and every validity control held: receipt binds the report, raw reader healthy, state restored, matching controlled background, fresh paired
fences never reused from the input or static-image phases, blank-before controls, acknowledgement separate from pixels, and every timed read inside its window.

## What the wall-clock evidence shows

Frames advance with the App's own monotonic clock; the suite does not synthesize presentation time. The collector waited so each paired capture would land mid-window.
Measured against the bracket in which `a=a,s=3` could have taken effect (about 50 ms wide), the playback captures covered 7.41-7.58 s (second frame window 5-10 s),
12.40-12.58 s (third, 10-15 s) and 17.40-17.58 s (root again, 15-20 s). In `stop-freeze` the stop took effect at 6.33 s, inside the second frame, and a capture
12.5-12.7 s after the start still showed the second frame, where a running animation would have shown the third. The stationary control's capture began more than one
root gap after the frame was built and still showed the root.

## Limits

This is playback by elapsed time on one App build (App and scripts revision `efed4bf8`), one desktop, one image size and one gap length. It is
not a frame-rate or display-refresh claim, and says nothing about PNG frames, building on or editing the root frame, `a=c` composition, persistent sessions (animation is
refused there, `ENOTSUP`, because their snapshots keep the root frame only), remote/mux, other platforms or the common-harness comparison. Those production limits are listed in the
[lifecycle contract](kitty-image-lifecycle.md), and `a=q,e=c` answers `ERR`.

## Retained evidence and superseded attempts

`2026-11-16-kitty-animation/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy, and the generated
`summary.json` and `report.json`. The run is `a3bc9c2e7381e740e3a9dc8d946e889e` (stock collector run `81ca521fd14ab5200ef937ac9f93fe8b`), source policy v63,
receipt schema 70, collected with the v3 `osc633` scope whose predecessor archive is the existing pinned full run. `kitty-animation-publication.test.mjs` rederives the complete
receipt and the projection from the archive and refuses an altered binding, journal line, pixel, input or marker.

Eight earlier runs are retained under `superseded-attempts/` with their real outcomes. Four ended without a grade: the display was unavailable twice, so the App aborted
before collecting (Avalonia RenderTimer error -6661); the observer's prior-challenge allowance covered the input phase only; and the expected `a=T` reply
shape omitted the placement id the App correctly echoes (`i=<id>,p=1;OK`). Four complete 18/18 runs, on the Apps built from `479a5465`, `b4bcba0d`, `be8add66` and `a706d8a1`,
are kept as superseded rather than failed: five rounds of review of the playhead, the view and the measuring scripts found real defects (finite-loop counting, catch-up after a
long gap, loading resume and gapless display, wake-ups on retire and for an animation that cannot move, settling before gap changes and refused frames,
boundary-straddling capture pairs, challenge uniqueness, eviction ordering, rejected frame handles, bleed checks on both pair members, snapshot parity in persistent sessions), so each fix
set rebuilt the App and repeated the whole collection, as did the CI and SonarCloud fixes after the pull request opened. The published run is therefore a fresh collection on the final App and scripts, and none of the fixes changed a measured verdict.
