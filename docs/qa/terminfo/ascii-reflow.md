# Controlled ASCII reflow effect contract (`ntilde-ascii-reflow-effect-v1`, contracts v1)

The pinned `extensions.reflow` probe (terminfo.dev@4.0.0) has no fixture, sends no resize request and reads no cells: its termless
arm consults only the declared reflow capability and always returns inconclusive (evidence "legacy"), and its terminal path returns the
same inconclusive without touching the terminal (evidence "none"). The pinned bundle declares no reflow capability for any terminal.
This contract is the separately identified measurement that the pinned probe lacks; its verdict is never attributed to the pinned probe.

## Criterion

After a parent-selected, independently verified pane resize (App `exact` + `layoutSettled`, kernel PTY geometry proved with
`/bin/stty -opost` on the same owned PTY, DA fence before every read), the stored state of the disposable main screen buffer must
equal an independent character-slicing model of the fixture at the new geometry:

- every logical hard line re-lays out by plain character slicing; hard-line boundaries stay distinct (no two hard lines share a
  physical row);
- characters are neither lost, duplicated nor reordered; unwritten row tails stay unwritten; authored single spaces and the
  bold+underline attribute probe (flags 777, stored underline style 1) survive at their positions;
- the cursor maps by its logical line and offset; exposed modes, margins, region and cursor mode stay at their bound baseline;
- displaced history rows are complete, ordered and fully read (within the 64-row scrollback budget);
- restoring the geometry re-joins every line, and growing follows the documented top-anchor policy
  (`VerticalGrow_ShouldNotPullHistory_AndAddPadding`): the first visible logical-line offset stays visible, retained history is
  not pulled back, vacated viewport rows become blank padding.

A stage that differs from the model grades **unsupported** with the stage named; a missing seed, geometry transition or complete
history read grades **inconclusive** and can never become evidence.

## Fixture

Two experiments under the single benchmark id `extensions.reflow`, seeded once each from the measured reset
(`ESC [ ? 1049 l ESC [ ! p ESC [ ? 6 l ESC [ r ESC [ 4 l ESC [ 0 m CSI 2 J CSI 3 J CSI H`):

- `reflow.width-roundtrip` (shrink 40×140 → 40×133, restore): six 210-char long hard lines (two wrapped rows at both widths, different break offsets) interleaved with short
  `RFs nnn ATTR REST` lines, one bold+underline probe (`SGR 1;4` over the first five cells, reset), the cursor parked at logical
  offset `cols + cols/3` inside the first line's wrapped continuation, six unwritten tail rows.
- `reflow.history-roundtrip` (shrink 40×140 → 36×133, restore): a full viewport of 138-char long hard lines, the cursor at the
  end of the last line; the shrink displaces complete ordered rows into history.

Long-line text is the line's own rotated alphabet (`A-Z` rotated by `7·k`) with a decimal offset marker every ten columns, so
every observed cell pins its logical line and offset. Authored spaces are single and always between position-pinning ink; no
expectation depends on a trailing authored space (the App's cell projection cannot distinguish one from an unwritten cell).
Everything is stored ASCII single-width with neutral attributes except the probe.

## Measured result (2026-11-12)

The width round trip reproduced the model exactly (redistribution, cursor +7 columns, attributes, lossless restore). The history
round trip's initial and restored stages reproduced the model exactly (top-anchor policy with 22 retained rows and bottom
padding); its shrunk stage diverged deterministically — reproduced identically in four independent owned runs: the live
pane-resize path merged hard lines in pairs — the logical line for (k, k+1) with k ∈ {0, 3, 6, 9, 12, 15} became `line k` + two
blank layout cells + `line k+1` — displacing 38 ordered rows instead of 44, losslessly. The VT engine's own `Resize` in isolation
reproduces the model for the same fixture under every tried resize sequence, isolating the divergence to the live pane-resize
path. The effect verdict is **unsupported** for this criterion as authored; the pinned verdict stays inconclusive. See
[the qualification](2026-11-12-ascii-reflow-qualification.md) for the retained evidence.
