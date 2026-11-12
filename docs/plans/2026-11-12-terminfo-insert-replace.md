# Insert/replace qualification working plan

Target only `modes.insert-replace`. The pinned `terminfo.dev@4.0.0` upstream
bundle has SHA-256 `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`
and probe hash `16a690f34c0e`. The inspected owned-adapter derivation had
SHA-256 `7a10de804b419036c5108f3df01008c265842a0b12e9ec04b5f8bf0ef7896415`;
its byte-identical probe registration has SHA-256
`a9e9c28bc23dd45070e6f372c214a9cfff9adeb8491a4c71ea173f90c4d363b1`.
This plan is not measurement evidence.

The termless path requires at least 1 row and 4 columns. In order, its feeds
are `ESC[4l ESC[1;1H ESC[2K ABC`, `ESC[1;1H`, `ESC[4h`, `X`, and, in
`finally`, `ESC[4l`. It reads the first four cells after the seed, the cursor
after the home, and those cells after X. `XABC` is supported; `XBC` followed
by a blank is unsupported; another result or an invalid seed is inconclusive.
The enable and print are separate feeds and must remain so in the replay.

The terminal path is different: at least 1x5, it writes `ESC[1;1H ESC[2K`,
`ABCD`, `ESC[1;2H`, `ESC[4h`, `X`, and `ESC[4l` in a `finally`, then queries
the cursor. A response is inconclusive because cursor advance does not prove
cell displacement; no response is also inconclusive. Preserve this path and
the stock outcome separately from termless and the additional effect contract.

The effect experiment must independently observe a prepared multirow ASCII
screen, full stored cell tuples, cursor, geometry, margins, origin, wrap and
IRM state. Compare a transition through insert and replace with a replacement
control from the same established seed. Include multiple printed characters,
right-edge clipping with wrap explicitly controlled, a reference printed with
the same pen, and shifted cells with distinguishable stored attributes. Gate
conclusions on valid seeds, geometry, identity and state; compare unaffected
cells and rows, and restore the initial IRM state and screen. The separate
effect conclusion cannot upgrade a pinned inconclusive result.

Use the shared owned collector and lock, with an independent oracle and exact
feed validation before collection. Bind the receipt to scripts, source, binary,
geometry and pane identity without asserting a stronger binary/source link
than the runner proves. Derive reports from retained observations, require a
completed restoration receipt, and rederive applicable predecessor suites.
Versions and layered registration must be chosen after the concurrent
title/icon batch lands; remeasure if integration changes measured inputs.
