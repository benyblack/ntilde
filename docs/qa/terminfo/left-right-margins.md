# DECLRMM and DECSLRM left/right margin contract

`CSI ? 69 h` sets DECLRMM (left/right margin mode); `CSI ? 69 l` resets it. While it is set, `CSI Pl ; Pr s` is DECSLRM (set left
and right margins); while it is reset, `CSI s` stays SCOSC (save the cursor), exactly as before. DECRQM (`CSI ? 69 $ p`) reports
1 or 2. The catalogue keeps both **Partial**.

## Setting the margins

DECSLRM takes one-based columns: an omitted or zero `Pl` is 1; an omitted, zero or oversized `Pr` is the last column. A pair whose
left margin is not left of its right margin is ignored, and so is the whole request while DECLRMM is reset. Applied margins home
the cursor: to the first row and column, or to the top-left corner of the margins in origin mode (DECOM). Margins are page-wide
state shared by both screens, like the top/bottom margins.

Resetting DECLRMM, RIS, DECSTR, DECALN and a resize restore full-width margins; RIS also resets DECLRMM, while DECSTR keeps it.
State transfer carries the mode and the margins; a payload with full-width margins and the mode reset is unchanged.

## What the margins bound

The rules follow xterm (a VT420-compatible implementation of DEC's left/right margins).

| Operation | With margins |
|---|---|
| Printing and autowrap | The right edge is the right margin while the cursor is at or left of it (the last column once it is right of it). A wrap returns to the left margin of the next line and is not marked as a soft wrap of the whole row. Without autowrap the last character overwrites the edge. |
| Insert mode (IRM) | Characters are pushed only up to the right margin. |
| CR, NEL, CNL, CPL | Return to the left margin when the cursor is at or right of it, or in origin mode; otherwise to the first column. |
| CUF, HPR, HT | Stop at the right margin when the cursor is at or left of it; otherwise at the last column. |
| CUB, BS | Stop at the left margin when the cursor is at or right of it; otherwise at the first column. Reverse wraparound reaches the right margin. |
| CUP, HVP, CHA, HPA in origin mode | Columns count from the left margin and are clamped to the right margin. CPR reports the column relative to the left margin. CBT stops at the left margin. |
| LF, IND, NEL at the bottom margin; RI at the top margin | Scroll only the columns between the margins, and only while the cursor column is between them; outside them nothing scrolls and the cursor stays. |
| SU, SD | Scroll only the columns between the margins, wherever the cursor is. |
| IL, DL | Act only while the cursor is between the margins, and move only the columns between them. |
| ICH, DCH | Act only while the cursor is between the margins; cells shift only up to the right margin, and DCH's blanks enter there. |
| DECIC, DECDC, SL, SR | Act only while the cursor column is between the margins; only the columns between them move. |
| DECERA, DECSERA, DECFRA, DECCRA, DECCARA, DECRARA, DECRQCRA | In origin mode columns are offset by the left margin (rows by the top margin, as before). |

A partial-width scroll never feeds scrollback. Cells keep their character, attributes, colors and side tables (extended text,
hyperlinks, underline decoration, DECSCA protection). A wide glyph cut by a margin is retired whole, sized-text blocks touching
the moved band are retired, and sticky images overlapping the band are removed. Scrolled-in blanks take the colors the
corresponding full-width operation uses.

## Not bounded or not implemented

ED, EL and ECH stay full-width, as in xterm. DECBI and DECFI, DECRQSS for the margins (`DCS $ q s ST` stays a failure reply) and
the image plane under a partial-width scroll (images are removed, not moved) are not implemented.

## Evidence

- [LeftRightMarginTests](../../../tests/Ntilde.VT.Tests/LeftRightMarginTests.cs): the mode and SCOSC fallback, DECSLRM defaults,
  clamps and refusals, printing and wrap, CR, CUF/CUB/BS, HT, origin-mode addressing and CPR, LF/IND/RI/NEL/SU/SD band scrolls,
  IL/DL, ICH/DCH, insert mode, column edits, origin-mode rectangles, wide glyphs and hyperlinks in a band scroll, resets, resize
  and state transfer.
- [VtCapabilityContractTests](../../../tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs): the catalogue's executable contracts.
- Every existing VT test still passes with full-width margins, the behavior of every earlier operation.
