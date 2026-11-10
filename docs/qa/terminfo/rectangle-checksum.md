# DECRQCRA rectangle checksum contract

`CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y` asks the terminal for a checksum of a rectangle of the active page. Ntilde answers it only
when the host turns on the `AllowRectangleChecksumReport` setting, which is **off by default**. The catalogue keeps it
**Partial**.

## Why it is off by default

The checksum of a one-cell rectangle identifies the character in that cell. A program that asks cell by cell can read back
everything on the screen, including output it did not write, such as a token an earlier command printed. xterm gates the
report the same way (`AllowWindowOps(xw, ewGetChecksum)`, off by default). While the setting is off, Ntilde consumes the
request and sends nothing, so a program sees the same silence as before the report existed.

The setting is read by direct panes only. A persistent (mux) session's replies come from the daemon's parser, which never
answers the request, whatever the setting says.

## Reply

`DCS Pi ! ~ XXXX ST`, where `XXXX` is the checksum as exactly four uppercase hexadecimal digits. `Pi` is echoed as a decimal
number; an omitted `Pi` is `0`. The reply framing and the request-id correlation follow
[DECRQCRA](https://vt100.net/docs/vt510-rm/DECRQCRA.html) and xterm.

## Rectangle

`Pg` (the page) is not interpreted: Ntilde has one page per screen, and the active page (the alternate screen while it is
shown) is the one summed. `Pt;Pl;Pb;Pr` follow DECERA exactly: an omitted or zero border takes the page default (top and left
1, bottom and right the last row and column), DECOM offsets the rows by the top margin, and every border is clipped to the page,
not to the margins. A rectangle whose top is below its bottom, or whose left is right of its right, sums nothing and replies
`0000`.

## Checksum

The checksum is the negated sum, modulo 2^16, of each cell's value:

| Cell | Value |
|---|---|
| A character | its Unicode scalar value (a supplementary character counts its full value) |
| A blank (never written or erased) | a space, 0x20 |
| A wide glyph | its scalar at the lead cell; the continuation cell adds nothing |
| Combining marks | nothing; only the base character counts |
| Bold, blink, reverse video, underline (any style) | 0x80, 0x40, 0x20 and 0x10 added to the cell's value |

Colors, faint, italic, hidden, strikethrough, overline, hyperlinks and DECSCA protection add nothing. An attribute weight
applies to any cell that carries it, a written space included.

The attribute weights are xterm's. The arithmetic is Ntilde's own contract: agreement with a particular xterm
`checksumExtension` setting or a DEC terminal is not claimed.

## Identity and state

Only the exact unprefixed identity is admitted: no leader, the single `*` intermediate, at most six plain parameters and no
colon subparameter. Any other shape is ignored and gets no reply. The request never changes the screen, the cursor, the pen,
the pending-wrap state, the modes or the margins.

## Evidence

- [RectangleChecksumTests](../../../tests/Ntilde.VT.Tests/RectangleChecksumTests.cs): the gate, the pinned terminfo.dev spellings,
  blanks, attribute weights, wide, supplementary and combining characters, defaults and clipping, the page and request-id
  parameters, inverted rectangles, origin mode, the alternate screen, non-admitted identities, unchanged state and 16-bit
  wrapping.
- [VtCapabilityContractTests](../../../tests/Ntilde.VT.Tests/VtCapabilityContractTests.cs): the catalogue's executable contract.
- [QueryVariantAuditTests](../../../tests/Ntilde.VT.Tests/QueryVariantAuditTests.cs): the default parser stays silent.
- [PaneParserWiringTests](../../../tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs): the App setting reaches a direct
  pane's parser, live, and never a mux-backed one.
