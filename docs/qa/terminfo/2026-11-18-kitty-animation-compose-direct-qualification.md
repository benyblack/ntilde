# Kitty animation frame composition (a=c), measured on the desktop in an ordinary tab (2026-11-18)

Issue [#1027](https://github.com/benyblack/ntilde/issues/1027). The [persistent-tab measurement](2026-11-18-kitty-animation-compose-qualification.md) left one gap: an ordinary (non-persistent) tab, where `a=c` runs on the App's own parser and the root's pixels are read back through
the **host decoder** (a persistent session reads retained sources instead and never asks a decoder). This note measures that route with the same two-case suite (`ntilde-owned-kitty-animation-compose-v1`), collected with `--scope direct-compose`: no multiplexer daemon, `SessionPersistence Off`, the
worker the App's own child, as in a full collection. The case is `extensions.kitty-graphics.animation` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed, no Core percentage changes.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract for composition in an ordinary tab | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.animation` | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-animation-direct-compose-effect-v1`) | none (no stock collector ran in this run) |

All 10 stages of the two cases passed (run `5e359d6e`, App commit `91b111e0`, scripts commit `86783304`, source policy v68), with sixteen validity controls held: the nine of the direct-tab publication, four ordinary-tab lineage controls (the tab was configured `SessionPersistence Off`, every observation proved the
worker a direct child of the App with no daemon in its lineage, the proof records no daemon, cleanup verified) and the same three composition controls as the persistent publication. The pinned callbacks are replayed from the retained bundle and stay inconclusive.

## What the panes showed

The same observations as in the persistent tab, through the other route:

- **Replace** (`replace`): a half-transparent green PNG frame 4 replaces the opaque blue frame 3 with `a=c,r=4,c=3,C=1`; the pane shows the source's pixels, alpha included, over the terminal background (a blend would have shown green over blue).
- **Blend** (`blend`): the same frame blended by default onto the opaque red root shows the protocol's blend; here the root, a composition *destination*, was read back **through the host decoder**.
- **Refusals change nothing** (`refuse`, `intact`): `ENOENT`, `EINVAL`, `EINVAL` in that order and nothing else, the displayed frame stayed the root, and frame 2 was still its own green afterwards.

## Limits

One desktop (macOS), one App build, one 16x16 image. Only **whole-frame** compositions are observed (the classifier names one uniform color per capture); rectangle offsets and sizes smaller than the image are pinned by `KittyAnimationTests`. A composition whose source or destination is a PNG-encoded root is not measured
(the root is raw RGBA, `f=32`; only the added frame is PNG), nor a second client, remote sessions or other platforms. No stock grade, Core score or common-harness comparison changes.

## Retained evidence and the one earlier attempt

`2026-11-18-kitty-animation-compose-direct/` holds the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy and the generated `summary.json` and `report.json`. The published run is `5e359d6e1de924b26360301b7ba62ce8`. `superseded-attempts/` keeps one earlier attempt
(`attempts.json` records its scripts commit, outcome, reason and the SHA-256 and size of each retained file): the first ordinary-tab collection passed all ten stages but the runner then failed to finish the receipt (the receipt completion read the run's scope before it had read the run's context), so it produced no receipt;
the runner was fixed and the suite collected again. `kitty-animation-compose-publication.test.mjs` runs the same checks over both compose publications: it rederives the receipt binding, settings, measured sources, lineage and the whole carrier report from the journals and refuses a downgraded, stripped,
relaunched or foreign run (a foreign App pid included), and re-checks every retained attempt file.

```sh
APP="/absolute/path/to/Ntilde"            # a Release build from a tree that contains PR 1034 (a=c)
RUN="artifacts/terminfo/owned-RUNID"      # the run directory the first command prints
OUT="docs/qa/terminfo/my-new-publication" # a directory that holds no publication yet
BUNDLE="/path/to/pinned-bundle.mjs.gz"    # the pinned terminfo.dev bundle archive
APP_COMMIT="40-hex App revision"
SCRIPTS_COMMIT="40-hex scripts revision"
node scripts/terminfo-owned.mjs --executable "$APP" --app-version 0.14.0 --scope direct-compose
node scripts/terminfo/kitty-animation-persistent-assemble.mjs compose-direct "$RUN" "$OUT" "$BUNDLE" "$APP_COMMIT" "$SCRIPTS_COMMIT"
```

Replay of the checked-in publications (they read fixed directories and fixed measured revisions, not a fresh run):

```sh
node --test scripts/terminfo/kitty-animation-compose-publication.test.mjs
```
