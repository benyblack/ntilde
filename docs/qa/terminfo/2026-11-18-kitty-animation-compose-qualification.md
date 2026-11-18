# Kitty animation frame composition (a=c), measured on the desktop in a persistent tab (2026-11-18)

Issue [#1027](https://github.com/benyblack/ntilde/issues/1027), step 4. [PR 1034](https://github.com/benyblack/ntilde/pull/1034) implemented `a=c` (a rectangle of one frame composed onto another, the result replacing the destination frame) in ordinary tabs and,
under persistent image policy 4, in persistent sessions; deterministic tests pinned it. This note measures it on a real desktop with the owned animation machinery, in a live persistent tab (a multiplexer
daemon, the App's pane as one of its clients). The case is `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed in this measurement, no Core percentage changes.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract for composition | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-persistent-compose-effect-v1`) | none (no stock collector ran in this run) |

All 10 stages of the two compose cases passed (run `744414ca`, App commit `8fed8e5f`, scripts commit `786faf96`), with sixteen validity controls held: the nine of the direct-tab publication, the four persistent-lineage controls of
[#1025](2026-11-17-kitty-animation-persistent-qualification.md) and three composition controls (one App process throughout, the same worker under the same daemon, the three refusals answered with exactly the named codes).
The pinned callbacks are replayed from the retained bundle and stay inconclusive; the divergence is recorded as `pinned-inconclusive-by-design`.

## What the composed panes showed

- **Replace** (`compose-replace-blend.replace`): after `a=c,r=3,c=2,C=1` frame 2 shows frame 3's pixels (blue, where it had been green).
- **Blend** (`compose-replace-blend.blend`): a half-transparent green PNG frame built on no base (so it keeps its alpha), blended by default (`a=c,r=4,c=1`) onto the opaque red root, shows the protocol's blend of the two; the root, a composition *destination*, was
  read back from its retained source, as the persistent policy requires (no decoder).
- **Refusals change nothing** (`compose-refused`): a missing source frame answered `ENOENT`, a rectangle wider than the image `EINVAL`, one frame onto itself with overlapping rectangles `EINVAL`, in that order and nothing else; the displayed frame stayed the root, and frame 2,
  selected afterwards, was still its own green.

Every stage is a static frame chosen by an explicit command, so no elapsed-time window is involved (the wall-clock validity control then holds only if the suite really has no timed stage).

## Limits

One desktop (macOS), one App build, one 16x16 image. The measured tab is a **persistent** one (policy 4): an ordinary (non-persistent) tab, where the root is read back through the decoder, is covered by unit tests only. Only **whole-frame** compositions are observed,
because the classifier names one uniform color per capture: rectangle offsets and sizes smaller than the image are pinned by `KittyAnimationTests` and the persistent parity tests, not here. A composition whose source or destination is a PNG-encoded root is not measured
(the root image is raw RGBA, `f=32`; only the added frame is PNG), nor a second client, remote persistent sessions, other platforms, or a restart or reattach after composing. A persistent session created before policy 4 shipped keeps refusing `a=c`
(measured by nothing here; unit tests cover it). No stock grade, Core score or common-harness comparison changes.

## Retained evidence

`2026-11-18-kitty-animation-compose/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy and the generated `summary.json` and `report.json`. The published run is `744414caccad5b6868bab0f353b24c67`. This was the first
collection of the suite and it passed on the first run, so there are no superseded attempts. `kitty-animation-compose-publication.test.mjs` rederives the receipt binding, settings, measured sources, lineage and the whole carrier report from its journals and refuses
a downgraded, stripped, relaunched or foreign run.

Fresh measurement and assembly, into a new publication directory of your own (the assembler refuses one that already holds a publication, and the revisions must be the measured constants of `kitty-animation-compose-qualification.mjs`). Set the variables first:

```sh
APP="/absolute/path/to/Ntilde"            # a Release build from a tree that contains PR 1034 (policy 4)
RUN="artifacts/terminfo/owned-RUNID"      # the run directory the first command prints
OUT="docs/qa/terminfo/my-new-publication" # a directory that holds no publication yet
BUNDLE="/path/to/pinned-bundle.mjs.gz"    # the pinned terminfo.dev bundle archive
APP_COMMIT="40-hex App revision"
SCRIPTS_COMMIT="40-hex scripts revision"
node scripts/terminfo-owned.mjs --executable "$APP" --app-version 0.14.0 --scope persistent-compose
node scripts/terminfo/kitty-animation-persistent-assemble.mjs compose "$RUN" "$OUT" "$BUNDLE" "$APP_COMMIT" "$SCRIPTS_COMMIT"
```

Replay of the checked-in publication (it reads that fixed directory and the fixed measured revisions, not a fresh run):

```sh
node --test scripts/terminfo/kitty-animation-compose-publication.test.mjs
```
