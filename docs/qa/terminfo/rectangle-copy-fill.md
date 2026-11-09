# DECFRA and DECCRA stored-text contracts

Both sequences act on the active text page only, preserve the cursor, pending-wrap
state, pen, row continuation flags and the image plane, and are exact identities:
a private leader, a different intermediate, a colon subparameter or too many
parameters is ignored whole. The catalogue keeps both **Partial**.

Coordinates follow the same rules as [DECERA](decera-text-erase.md): one-based,
inclusive, omitted/zero meaning the default, values past the page treated as the
page extent, rows offset by the top scrolling margin in origin mode, scrolling
margins not clipping. A rectangle whose clipped borders are reversed is ignored.
See the [DECFRA](https://vt100.net/docs/vt510-rm/DECFRA.html) and
[DECCRA](https://vt100.net/docs/vt510-rm/DECCRA.html) definitions.

## DECFRA — `CSI Pch ; Pt ; Pl ; Pb ; Pr $ x`

`Pch` must be 32..126 (looked up in the GL set in use, so DEC special graphics maps
like printed text) or 160..255 (the Latin-1 right half; Ntilde has no other GR
set); any other or missing code ignores the whole command. Every cell of the
rectangle receives that character with the live SGR pen: attributes, literal,
palette-index or default colors, and underline decoration. An open hyperlink is
not an SGR rendition, so filled cells carry none; extended text is cleared. An edge
that splits a wide glyph or touches a scaled block retires the whole unit with the
erase blank, as DECERA does, before the fill is written.

## DECCRA — `CSI Pts ; Pls ; Pbs ; Prs ; Pps ; Ptd ; Pld ; Ppd $ v`

The clipped source rectangle is copied to the destination origin, clipped to the
page, as if the whole source were read before any destination cell is written, so
overlapping copies in any direction cannot smear. Characters, flags, colors,
extended text, hyperlinks and underline decoration travel with the cells; the live
pen is not applied. The destination origin clamps like any other coordinate.

Only page 1 exists. A source or destination page of 0 or 1 (or omitted) is
accepted; any larger explicit page ignores the command rather than being treated
as the active page. The DEC page text says a page past the arrangement means the
last page; with one page that would be page 1, but Ntilde refuses instead because
it cannot distinguish "unsupported page" from "the active page" and the contract
must not silently alias them.

A wide glyph cut by the source rectangle or the page edge copies as a blank that
keeps its colors and attributes. A destination edge splitting an existing wide
glyph retires the orphaned half with the erase blank. Scaled-text (OSC 66)
metadata is not copied; destination scaled blocks are retired whole.

The DECCRA page lists its source constraint as `Pts <= Pbs` and `Pls <= Prs` but
its notes state the reverse; Ntilde follows the parameter rule and ignores a
reversed source.

## Not claimed

Image-plane fill or copy, protected-cell (DECSCA) storage, attribute rectangles,
DECSACE and rectangle checksums are separate contracts. `RectangleCopyFillTests`
pins every rule above; the pinned terminfo.dev fixtures are replayed byte-exactly
in the owned qualification suite.
