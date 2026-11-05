# DECERA stored-text contract

The exact `CSI Pt ; Pl ; Pb ; Pr $ z` spelling erases an inclusive rectangle
on the active text page. The [DEC reference](https://vt100.net/docs/vt510-rm/DECERA.html)
defines omitted/zero borders, clipping to the page, origin-mode coordinates,
and preservation of the cursor. The catalogue remains **Partial** because
this contract does not qualify erasure of the separate inline-image plane.

Top and left default to one; bottom and right default to the page dimensions.
DECOM adds the top scrolling margin to row coordinates before page clipping.
Scrolling margins do not themselves clip the rectangle. Reversed borders after
clipping, extra parameters, colon subparameters, private leaders and other
intermediates are ignored. Horizontal-margin state is not implemented.

Erased text becomes spaces with normal cell rendition and the existing Ntilde
erase foreground/background colors. The live pen, cursor position, pending-wrap
state and row continuation flags remain intact. Extended text, hyperlinks and
underline side metadata are removed from erased cells.

The existing wide/OSC66 text-unit policy applies: intersecting either half of a
wide glyph clears the whole glyph; touching a scaled block clears the whole
block. The same erase colors, default-color flags and side-table cleanup apply
throughout the whole erased unit. This can clear cells outside the requested rectangle, including
its retained history anchor when the same block spans history and the viewport.
History rows and unrelated cells remain present. Image handles and their
ownership are unchanged; protected-cell storage and DECSERA remain unsupported.

`RectangularEraseTests` covers both screen banks, colors/attributes and side
metadata, borders/defaults, origin/margins, invalid identities, input splits,
wide/scaled units, row wrapping, full version1 state/parser continuation and
actual recording snapshot/tail replay. Recorder checkpoints intentionally omit
scrollback; the full-state continuation tests separately preserve it. The
catalogue also has an executable partial contract in `VtCapabilityContractTests`.

Eight old-parser failures and eight passing refusal controls are retained in
the isolated development proof. No frozen terminfo observation or grade is
changed by these unit contracts; an owned App measurement remains separate.
Eight further wide-glyph color regressions failed on the initial implementation
and pass with complete whole-unit erase blanks in both screen banks.
