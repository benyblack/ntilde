# DECIC, DECDC, SL and SR stored-text contracts

The four column operations act on the active text page only. They do not move the
cursor and they preserve the pending-wrap state, the pen, scrollback and the image
plane. Each is an exact identity: a private leader, a different or extra
intermediate, a colon subparameter or a second parameter is ignored whole. An
omitted or zero count is one. The catalogue keeps all four **Partial**.

| Sequence | Name | Start column | Effect on each region row |
|---|---|---|---|
| `CSI Pn ' }` | DECIC | cursor column | insert `Pn` blank columns; columns pushed past the right edge are lost |
| `CSI Pn ' ~` | DECDC | cursor column | delete `Pn` columns; blank columns enter at the right edge |
| `CSI Pn SP @` | SL | left margin | delete `Pn` columns at the left margin, so the row shifts left |
| `CSI Pn SP A` | SR | left margin | insert `Pn` blank columns at the left margin, so the row shifts right |

## Region and cursor

The operations change every row of the top/bottom scrolling region (the whole
page when DECSTBM is not set). They change nothing while the cursor row is outside
that region. [DECIC](https://vt100.net/docs/vt510-rm/DECIC.html) and
[DECDC](https://vt100.net/docs/vt510-rm/DECDC.html) define the scrolling-region
scope and the no-effect rule. ECMA-48 SL/SR know nothing of DEC margins. Ntilde
follows xterm (`xtermColScroll`): it applies them to the same region under the
same cursor-row rule, from the left margin and regardless of the cursor column.
Counts larger than the space between the start column and the right edge clamp to
that space. With pending wrap the cursor still occupies the last column, so
DECIC/DECDC act there. Origin mode does not change any of this.

Left/right margins (DECLRMM/DECSLRM) are **not implemented**, so the horizontal
margins are the page edges. When they are implemented, these operations must be
bounded by them; no left/right-margin behavior is claimed here.

## Cells

Introduced blank columns use the same cell as ICH/DCH: a space with the live
foreground/background colors (literal, palette index or default) and no visual
attribute. Shifted cells move with their character, flags and colors and with
every row side table: extended text, hyperlink, underline decoration, sized-text
records and DECSCA protection. Columns pushed off the edge or deleted drop their
side-table entries.

A shift never leaves half a wide glyph behind. When a cut boundary (the start
column, or the first column pushed off the edge on insert or kept after the
deleted run on delete) splits a wide glyph, both halves are first replaced by the
erase blank. Sized-text blocks the shift would split, or that span several rows,
are retired as ICH/DCH retire them.

## Evidence

[`ColumnEditingTests`](../../../tests/Ntilde.VT.Tests/ColumnEditingTests.cs) covers:

- the four pinned terminfo.dev fixtures;
- omitted, zero and oversized counts;
- region-only rows and the cursor-outside-region no-op;
- SL/SR independence from the cursor column, and page-edge columns;
- origin mode, and pending wrap;
- blank-cell colors and attributes, and the live pen;
- moved attributes and side tables, including protection, and side tables dropped at the edge;
- wide glyphs at every cut;
- identity rejection and the bare finals' ICH/CUU meanings;
- the alternate screen, untouched scrollback, full-state round trip, and chunked input.

Image-plane shifting, left/right margins and rendered appearance remain unqualified.
