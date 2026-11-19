# Kitty Unicode placeholders over a grid, measured on the desktop (2026-11-19)

Issue [#989](https://github.com/benyblack/ntilde/issues/989), the cases the [first placeholder measurement](2026-11-19-kitty-placeholders-qualification.md) left open: a grid of **more than one row**, the **inheritance of a nonzero row**, and cells that **scroll, are erased and are
inserted** with their text. It is a further measurement of the same case, `extensions.kitty-graphics.unicode-placeholders` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`), with a second suite (`ntilde-owned-kitty-placeholders-grid-v1`, suite version 6 of the
owned animation carrier), in an ordinary tab (`--scope direct-placeholder-grid`) and in a live persistent tab (`--scope persistent-placeholder-grid`, image policy 5). No production code changed.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.unicode-placeholders`, grid, ordinary tab | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-unicode-placeholder-grid-direct-effect-v1`) | none (no stock collector ran) |
| `extensions.kitty-graphics.unicode-placeholders`, grid, persistent tab | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-unicode-placeholder-grid-persistent-effect-v1`) | none (no stock collector ran) |

All 14 stages of the two cases passed in both tabs (ordinary tab: run `bf0ffaaf`, persistent tab: run `36d3e84b`; App commit `b5e6cec9`, scripts commit `499bdffd`, source policy v70), with eighteen validity controls held in each: the nine of the animation carrier, four lineage controls of the kind
of tab, and five of the grid suite (one App process and one worker throughout, the virtual placement drew nothing, the whole grid painted, and a nonzero row was inherited, not defaulted). The pinned callbacks are replayed from the retained bundle and stay inconclusive by design. No stock, Core or
common-harness number changes, and the inventory is unchanged (one id, already counted).

## The measurement

One 16x16 image in four colored quadrants (red and green above, blue and yellow below) is transmitted and given a virtual placement of a **4 x 2** grid (`a=p,U=1,p=1,c=4,r=2`); with a typical desktop font that grid is nearly square, so each of its eight cells has an interior color that
names the tile it shows. Each stage reads the whole grid by paired offscreen captures, one letter per cell (`r g b y` the quadrant color, `.` nothing). A painted cell is sampled where its quadrant lies under **every** placement of the undistorted square (the App's own placement policy is not asserted),
one pixel clear of the quadrant's edge, and must be one exact color; a cell that shows nothing must be the controlled background throughout. The cell grid itself is checked against the cells each stage left, on the rows it left them.

- **`rows`**: the grid shows `rrgg/bbyy`; the row marks, not the screen row, choose the tile (the rows swapped on screen show `bbyy/rrgg`; the lower tiles printed on the top screen row alone show `bbyy/....`); and a bare placeholder after a cell naming row 1 inherits **row 1** and the next
  column (`rrgg/bbyy`), where a terminal that defaulted a missing row to 0 would have painted the upper colors twice.
- **`scroll`**: tiles belong to the text that carries them. After the screen scrolled up one line the same grid showed one text row higher; after the lower text row was erased only the upper tiles remained (`rrgg/....`); after a line was inserted above the grid it showed one row lower.

## One observation, not a claim

The line a scroll brings in at the bottom, and the line an insertion creates, held the **default cell** (default foreground and background) in both tabs, not the blank cell with the active background that background color erase (xterm's `bce`) gives. The two look the same on screen here (the default background was set
to the controlled color), the suite does not measure the fill, and the carrier accepts either for those two stages and no other change to the lines. It is recorded as a fact about the App's scrolling, not as a result of this suite.

## Limits

One desktop (macOS), one App build, one 16x16 image, one grid size, one cell font. Not measured: a grid whose aspect differs from the image's (where the image sits in the grid is not asserted), swapped columns on a grid, ids above 24 bits (the third diacritic), relative placements (`P=`, `Q=`), resize and
reflow of placeholder cells, a palette or theme background behind the cells, text selection and copy, a second client, remote sessions, other platforms, and a restart with placeholders on screen. No stock grade, Core score or common-harness comparison changes.

## Retained evidence and the earlier attempts

`2026-11-19-kitty-placeholder-grid-direct/` (ordinary tab) and `2026-11-19-kitty-placeholder-grid/` (persistent tab) each hold the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy and the generated `summary.json` and `report.json`. The ordinary-tab directory keeps
`superseded-attempts/` (`attempts.json` records the outcome, reason and digests): a first collection stopped after four stages on unexpected input outside a capture window (cause not retained), a second passed the whole `rows` case but was refused at the scrolled stage because the scrolled-in line held the default cell
(see above; the carrier then accepted either fill), and a complete 14/14 run, published first, was superseded because review found that the oracle read a painted cell only inside its sampled rectangle (the oracle now also requires the part of each tile that every placement of the square covers to be painted, and the retained pixels satisfied that too).
The persistent-tab directory keeps that last superseded run as well. The measured App is commit `b5e6cec9` and the measured scripts `499bdffd`; later commits are publication, docs and test pins, and merges of main (which can change VT sources the run captured). `kitty-placeholder-grid-publication.test.mjs` rederives the
receipt binding, settings, measured sources, lineage and the whole carrier report from the journals for both publications and refuses a downgraded, stripped, relaunched or foreign run.

```sh
APP="/absolute/path/to/Ntilde"            # a Release build from a tree that contains PRs 1048, 1051 and 1052
RUN="artifacts/terminfo/owned-RUNID"      # the run directory the first command prints
OUT="docs/qa/terminfo/my-new-publication" # a directory that holds no publication yet
BUNDLE="/path/to/pinned-bundle.mjs.gz"    # the pinned terminfo.dev bundle archive
APP_COMMIT="40-hex App revision"
SCRIPTS_COMMIT="40-hex scripts revision"
node scripts/terminfo-owned.mjs --executable "$APP" --app-version 0.14.0 --scope direct-placeholder-grid      # or persistent-placeholder-grid
node scripts/terminfo/kitty-animation-persistent-assemble.mjs placeholder-grid-direct "$RUN" "$OUT" "$BUNDLE" "$APP_COMMIT" "$SCRIPTS_COMMIT"   # or: placeholder-grid
```

Replay of the checked-in publications (they read fixed directories and fixed measured revisions, not a fresh run):

```sh
node --test scripts/terminfo/kitty-placeholder-grid-publication.test.mjs
```
