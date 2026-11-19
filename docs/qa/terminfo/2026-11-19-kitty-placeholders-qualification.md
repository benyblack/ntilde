# Kitty Unicode placeholders, measured on the desktop (2026-11-19)

Issue [#989](https://github.com/benyblack/ntilde/issues/989), step C. [PR 1048](https://github.com/benyblack/ntilde/pull/1048) made `U=1` virtual placements and `U+10EEEE` cells draw images in ordinary tabs and
[PR 1051](https://github.com/benyblack/ntilde/pull/1051) carried them in persistent sessions (image policy 5); deterministic tests pinned both. This note measures the behaviour on a real desktop with the owned image
machinery, in an ordinary tab and in a live persistent tab, with one new suite (`ntilde-owned-kitty-placeholders-v1`, suite version 5 of the owned animation carrier). The case is
`extensions.kitty-graphics.unicode-placeholders` (bundle SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`). No production code changed in this step.

## Result

| Case | Pinned termless | Pinned terminal | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.kitty-graphics.unicode-placeholders`, ordinary tab | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-unicode-placeholders-direct-effect-v1`) | none (no stock collector ran in this run) |
| `extensions.kitty-graphics.unicode-placeholders`, persistent tab | inconclusive by design | inconclusive by design | **supported** (`ntilde-kitty-unicode-placeholders-persistent-effect-v1`) | none (no stock collector ran in this run) |

All 19 stages of the three cases passed in both tabs (ordinary tab: run `efccf352`, persistent tab: run `c1a30dea`; App commit `8a2eb295`, scripts commit `c46e42c7`, source policy v70), with seventeen validity controls held in each: the nine of the animation carrier, four lineage controls of the
kind of tab (the same four as the composition publications), and four of the placeholder suite (one App process throughout, one worker throughout, the virtual placement alone drew nothing, and the valid cells painted after the negatives).
The pinned callbacks are replayed from the retained bundle (they read the declaration and nothing else) and stay inconclusive; the divergence is recorded as `pinned-inconclusive-by-design`. No stock, Core or common-harness number changes.

## The measurement

One 16x16 image (left half red, right half blue, raw RGBA, `f=32`) is transmitted, given a virtual placement of a 2 x 1 grid (`a=p,U=1,p=1,c=2,r=1`), and shown by cells printed at a controlled anchor. The foreground
colour is a 24-bit image id, the underline colour (SGR 58) is the placement, and the row and column marks are the first entries of kitty's diacritics table; the commands are written out in the suite from the protocol,
not taken from the App. Each stage reads **one row of two cells** by paired offscreen captures, independently classifying each cell as nothing, a red band or a blue band, a painted band being as wide as its cell and
as tall as the undistorted fit of a square image into the whole grid, with the cell grid itself checked to hold exactly the cells the stage printed (placeholders are cells, so unlike every other image carrier the screen changes).

- **`tiles`**: the virtual placement alone painted nothing (the transmit and the placement answered `OK`, the target stayed blank); the cells then showed **red, blue**; erasing the screen removed the image.
- **`cell-mapping`**: the marks, not the position, chose the tile. Swapped columns showed **blue, red**; a lone cell naming column 0, printed in the *second* cell, showed red there and nothing in the first;
  a bare placeholder after a cell with marks (row 0, column 0) continued in row 0 at the next column (**red, blue**). The grid has one row, so the inheritance of a nonzero row is not measured.
- **`unbound`**: cells naming an image that was never transmitted, a placement that does not exist, or a tile outside the 2 x 1 grid painted **nothing**; the valid cells (the positive control) then painted
  red and blue; `a=d,d=i` made those same cells paint nothing again while still on screen.

## Limits

One desktop (macOS), one App build, one 16x16 image, one grid size, one cell font. Not measured: a grid whose aspect differs from the image's (where the image sits inside the grid is not asserted: only that
every painted tile of a row agrees), image ids above 24 bits (the third diacritic), the inheritance of a nonzero row, relative placements (`P=`, `Q=`), scrolling, history and reflow of placeholder cells, a palette or theme background behind the cells,
text selection and copy, a second client, remote sessions, other platforms, and a restart or reattach with placeholders on screen (persistent tab). Unit tests pin several of those (`KittyUnicodePlaceholderTests`,
`PersistentKittyPlaceholderTests`); they are not desktop measurements. No stock grade, Core score or common-harness comparison changes.

The cases listed above as not measured that concern **a grid of more than one row, the inheritance of a nonzero row, and scrolling, erasing and inserting** were measured afterwards: see the [placeholder grid qualification](2026-11-19-kitty-placeholder-grid-qualification.md).

## Retained evidence and the earlier attempt

The measured App is commit `8a2eb295` and the measured scripts are `c46e42c7`; later commits are publication, docs and test pins, and merges of main. A merge of main can change VT sources the run captured (the double-size line re-wrap, for one, touched
`TerminalBuffer.WritePath.cs`), so the tree these files ship in is not the measured App: the claim is about the measured commits.

`2026-11-19-kitty-placeholders-direct/` (ordinary tab) and `2026-11-19-kitty-placeholders/` (persistent tab) each hold the whole owned run (`owned-run.zip`, SHA-256 pinned in `manifest.json`), the pinned bundle copy and the
generated `summary.json` and `report.json`. The ordinary-tab directory also keeps `superseded-attempts/`: its first collection failed after two stages because the image carrier required the cell grid to stay the blank bound screen,
which is false for placeholders; the carrier was extended so a suite declares the screen each stage must leave (built from the stage's own command text), and the suite was collected again. `kitty-placeholders-publication.test.mjs`
rederives the receipt binding, settings, measured sources, lineage and the whole carrier report from the journals for both publications, and refuses a downgraded, stripped, relaunched or foreign run.

```sh
APP="/absolute/path/to/Ntilde"            # a Release build from a tree that contains PRs 1048 and 1051
RUN="artifacts/terminfo/owned-RUNID"      # the run directory the first command prints
OUT="docs/qa/terminfo/my-new-publication" # a directory that holds no publication yet
BUNDLE="/path/to/pinned-bundle.mjs.gz"    # the pinned terminfo.dev bundle archive
APP_COMMIT="40-hex App revision"
SCRIPTS_COMMIT="40-hex scripts revision"
node scripts/terminfo-owned.mjs --executable "$APP" --app-version 0.14.0 --scope direct-placeholders      # or persistent-placeholders
node scripts/terminfo/kitty-animation-persistent-assemble.mjs placeholders-direct "$RUN" "$OUT" "$BUNDLE" "$APP_COMMIT" "$SCRIPTS_COMMIT"   # or: placeholders
```

Replay of the checked-in publications (they read fixed directories and fixed measured revisions, not a fresh run):

```sh
node --test scripts/terminfo/kitty-placeholders-publication.test.mjs
```
