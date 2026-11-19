# Kitty Unicode placeholders (#989)

Status: design, 2026-11-18. Three PRs: A (VT + renderer), B (persistent sessions), C (owned measurement).

## What the protocol asks for

An image is transmitted as usual. A client then creates a **virtual placement**
(`a=p` or `a=T` with `U=1`, plus `c`/`r` for the grid size, optional `p`). A virtual
placement is invisible and never moves the cursor. The image appears where the client
prints the placeholder character `U+10EEEE`:

- the cell's **foreground colour** carries the low 24 bits of the image id (`38;5;n`
  gives an 8-bit id, `38;2;r;g;b` a 24-bit one);
- the **underline colour** (SGR 58) carries the placement id, `0` meaning any virtual
  placement of that image;
- up to three **combining diacritics** after the placeholder give the row, the column
  and the high byte of the image id, indexed through the 297-entry table in kitty's
  `gen/rowcolumn-diacritics.txt`;
- omitted diacritics are inherited from the placeholder cell to the left under the three
  rules in the kitty documentation (same fg and underline colour; row same; column + 1);
- the cell background shows through transparent pixels;
- the image is fitted, aspect ratio kept, into the `c` x `r` grid, and each cell shows
  its tile of that fit.

Virtual placements can only be deleted with `d=i,I,n,N` (and `r,R`, which Ntilde does not
implement); `a,A,c,p,q,x,y,z` never touch them. `a=q,e=T,U=1` must answer with `U=1`
present.

## Why cells, not placements

Ntilde's real placements are objects anchored at a cell with a size. Placeholder images
must move with *text*, so they are derived from the cells at render time:

- scrolling, scrollback, erase, reflow, insert/delete and copy already move or drop the
  cells, so the image follows without any placement bookkeeping;
- nothing about a visible tile is stored. Erasing the cell removes the tile;
- the only new retained state is the virtual placement (grid size) per image.

## PR A: VT + renderer

- `KittyImageAsset.VirtualPlacements`: placement id -> grid (columns, rows), at most 64
  per image, oldest evicted. A re-transmitted id is a new asset and so has none.
- Parser: `U=1` on `a=p` and `a=T` stores a virtual placement and does nothing else
  (no cursor motion, no real placement). Grid size is `c`/`r`, else the image's size in
  cells (as for real placements). `d=i/I/n/N` remove virtual placements.
- `a=q,e=T,U=1` answers `i=..,e=T[,C=1],U=1;OK`.
- `KittyPlaceholders`: the diacritic table, per-cell decoding and the left-to-right
  inheritance rules. Pure functions of (grapheme text, fg, underline, previous cell).
- `GetVisibleImagesSnapshot` appends one `RenderImageSnapshot` per horizontal run of
  resolved placeholder cells (same image, placement, row; consecutive columns). Each
  carries the virtual grid origin and size. It is skipped entirely, with no row scan,
  while no asset holds a virtual placement.
- `TerminalDrawOperation` draws a run by fitting the bitmap into the grid rectangle
  (derived from the run rectangle and the grid origin) and clipping to the run, over the
  cells' own background, so a transparent pixel shows the background colour.

Not in PR A: persistent sessions. Under a persistent-session policy that retains images
(`RetainImageSources`), `U=1` answers `ENOTSUP` and the feature query `ERR` until PR B,
because a snapshot cannot yet carry virtual placements.

## PR B: persistent sessions (implemented)

Image policy 5 (`PlaceholderImagesVersion`): the daemon and every client parse the same
bytes, so each holds the same virtual placements; snapshots add them next to the retained
assets so reattach restores them. Older peers are refused the session, never downgraded.

## PR C: measurement (implemented)

An owned desktop run ([qualification](../qa/terminfo/2026-11-19-kitty-placeholders-qualification.md)):
one 16x16 image (left half red, right half blue), a 2x1 virtual placement and a row of two
placeholder cells, with controls (blank before, tiles after, erase clears, swapped columns
swap the colors, a lone cell shows its own tile, a bare placeholder continues in row 0, an
unbound id, a stale placement id and a tile outside the grid paint nothing, and `a=d,d=i`
stops the same cells painting). A 2x2 grid, the inheritance of a nonzero row and a wrong
row that moves a tile (the grid has one row) were not measured. The pinned
`extensions.kitty-graphics.unicode-placeholders` callbacks cannot return supported or
unsupported (they report `insufficient-evidence`), so the verdict is an independently
specified effect, kept apart from the pinned result, as for animation.

## Open questions

- Whether `U+10EEEE` is one cell wide in `UnicodeWidth` (tested first).
- Palette background cells: the tile uses the cell's RGB background or the theme
  background; resolving a palette background through the theme is not attempted.
