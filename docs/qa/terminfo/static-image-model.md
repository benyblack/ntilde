# Owned static image model, driver and oracle (#770)

The three pure input/case/oracle modules are integrated as the fifteenth owned
suite, after the independently captured v9 routed-input phase. Portable tests
use synthetic state, byte streams and PNGs; they are not runtime qualification.
No static-image owned measurement has been published by this change.

The live source graph is the fixed67-file `readonly-owned-source-graph-v3`
scope, producing schema10 receipts. The original51/52-file v8 and60-file v9
maps and builders remain independent of future live inventory growth. The
published v9 archive is reproduced through the full current finalizer, including
tests with a future active source policy and inventory. Historical data and
stock78/0/177 grades remain unchanged.

## Owned controller and retained evidence

The existing private owner controller admits each of30 bounded writes after
checking the same executable, direct App/worker/PTY lineage and disabled output
processing. It captures31 complete before/after cell, history, cursor and buffer
states with two original full-grid offscreen PNGs apiece. Each read-only reader
uses31 of its64 allowed reads; the62 images together must fit the existing
64-million decoded-pixel cap. No new App action surface is used.

The worker creates one exclusive undecoded raw reader after input has been
sealed. Every admitted write includes independent source-selected default-mode
queries (including reverse-video mode5 reset), then a fresh unpredictable paired DECRPM fence selected after the
write. Image pairs cannot reuse any of input's80 pairs. Raw payloads remain
bounded to512 bytes, with a4.5-second capture deadline and no retry. Requests,
replies, fences, admission/observation lineage facts, PNGs and seven immutable
PNG/RGBA/SIXEL source inputs are retained and hash-bound. Source images are
validated against fixed SHA-256 pins before use and during finalization.

The fixture pins both the VT runtime OSC11 background and stored cell background
to `#101010`. Every admitted write independently queries OSC11 and requires its
exact retained RGB reply; every capture must agree on the literal background and
active override. This qualifies matching controlled theme/cell backgrounds only.
Compositing over differing cell/theme backgrounds remains unqualified. Original
non-overridden theme, full render context and PNG must all return at finish.
Font/grid/foreground/palette/other stored theme facts remain fixed throughout.
Only the controlled background and unpainted derived cursor-text/effective
selection colors may differ across phases; those full facts must still match
exactly within each paired capture. Cursor is hidden during image observations,
and synthetic selection is forbidden.

The23 logical image stages retain ACK results separately from pixel assertions.
A well-framed correlated error or missing expected reply is negative evidence;
neither an ACK nor a consumed stream proves displayed pixels. If an allocation provides no usable image ID, dependent lifecycle stages are
explicitly inconclusive and emit no guessed request. Number-reuse stages require
both distinct successful allocations; a silent delete of an empty scene cannot
qualify a lifecycle that was never established. Malformed/unmatched replies, changed
ownership/state/context, failed raw seals or restoration prevent completion.
Once independent fixture controls pass, outside-envelope image bleed is a
semantic failure. Completed collection does not mean every stage passed.

Stage qualification also retains causal witness references. A clear/delete needs
the preceding visible-image witness; redisplay and missing-image stages need the
corresponding prior removal witness. A failed prerequisite makes dependent
qualification inconclusive with its exact reason, while preserving the actual
request, reply, PNGs and cleanup attempts. The first failure remains failed and
the separate five-case summary remains negative; later blank pixels cannot turn
an unestablished lifecycle into support. Storage attempts remain separately
observed against the independent blank control.

Each case records its deletion/clear outcome before independent RIS cleanup,
then proves return to the bound blank state and original PNG. Final RIS must
restore the original main-screen cells/modes and PNG before the raw reader is
sealed. Existing owner teardown must verify worker/App exit and fixture/source
removal before the full15-suite receipt can be written. Only an initial typed
screen-detail or geometry/pixel-budget refusal, before any image fixture/files,
can produce an explicit unavailable binding. A started failure cannot downgrade.

## Primary protocol contracts

- [DEC VT300 sixel chapter](https://vt100.net/docs/vt3xx-gp/chapter14.html):
  use a seven-bit DCS with explicit raster aspect `1;1`, declared 16×16 raster,
  RGB-percent palette and six-row bands. P2=1 preserves unpainted pixels. This
  fixture paints every declared pixel, so its image is opaque and is independent
  of unspecified palette/background defaults. Native source size and the rounded
  cell envelope remain distinct.
- [iTerm2 primary documentation, pinned source](https://github.com/gnachman/iterm2-website/blob/d6249e4f778822ac2ec89cc099607a3bc5ef4e3f/source/_includes/3.7/documentation-images.md#L64):
  `inline=1` displays the image; unsuffixed width/height are cell counts.
  The default preserves aspect within the requested rectangle; explicit
  `preserveAspectRatio=0` permits distortion. These are separate cases.
- [Kitty primary placement specification, pinned source](https://github.com/kovidgoyal/kitty/blob/3dc4f8d5aa0092b77c839145aff5602ca8664cd4/docs/graphics-protocol.rst#L680):
  explicit `c/r` sets a cell envelope with undistorted image fit. It does not
  license stretching the source to every cell edge. `C=1` keeps the cursor.
- [Kitty numbering](https://github.com/kovidgoyal/kitty/blob/3dc4f8d5aa0092b77c839145aff5602ca8664cd4/docs/graphics-protocol.rst#L970)
  requires a distinct allocated id for a new image using a reused number; later
  commands using that number select its newest image. Returned ids must come
  from raw correlated responses, not a guessed counter.
- [Kitty deletion](https://github.com/kovidgoyal/kitty/blob/3dc4f8d5aa0092b77c839145aff5602ca8664cd4/docs/graphics-protocol.rst#L907):
  lowercase selection removes placements while allowing redisplay of retained
  data; uppercase selection also removes data when no other reference remains.
  This fixture uses a fresh private alternate screen, no scrollback references,
  no virtual placements and only its own bounded resources.

The primary sources were inspected before generating fixtures. No placement
alignment shared by all protocols is invented: aspect cases qualify contained
square size/aspect inside the explicit cell envelope, without claiming that
unspecified letterbox padding is centered. Native Sixel requires the original
16×16 content rectangle at the controlled anchor; iTerm explicit stretch
requires the complete requested envelope.

## Immutable inputs and planned cases

[Inputs](../../../scripts/terminfo/static-images-inputs.mjs) retain fixed PNG,
RGBA and Sixel bytes with SHA-256 pins. The 16×16 source has unequal quadrant
splits at x=5 and y=7, with red/green/blue/yellow in a fixed spatial order.
PNG/RGBA sources add a 2×2 fully transparent interior hole over an independently
known opaque `[16,16,16,255]` background. A second source reverses the quadrant
colors, making newest-number lookup distinguishable from an old image.
Functions return fresh bytes; mutating one invocation cannot alter later inputs.
There is no half-alpha or color-profile interpretation in this bounded phase.

[Cases](../../../scripts/terminfo/static-images-cases.mjs) describe five variants
and 23 logical stages, before outer binding/restoration:

| Case | Pair/stage contract |
|---|---|
| Sixel native raster | Blank → exact 1:1 raster → cleared target; unchanged non-target pixels. |
| iTerm explicit stretch | Inline PNG with width=4, height=2, preserveAspectRatio=0 → full-envelope pixels → cleared target. |
| iTerm default aspect | Same PNG/cell envelope with preservation omitted → contained undistorted fit → cleared target. |
| Kitty numbered lifecycle | `a=t` stores without display; `a=p` displays; soft `d=n` disappears; placement redisplays retained bytes; hard `d=N` disappears; another placement reports ENOENT and remains blank. |
| Kitty number reuse | Two no-display stores with the same I allocate distinct ids; number placement shows the second source; hard-delete newest; explicit first-id placement still shows the first source; hard-delete old id. |

The initial anchor is column 3/row 4 in zero-based coordinates and the requested
cell envelope is 4×2. The driver must independently confirm sufficient geometry,
set/verify the known background, hide the cursor and keep the disposable pane
free of text in the target. It must position the cursor at the requested anchor
before image requests. The command builder does not own that setup or restoration.
Generic clear commands in the plan are local effect probes, not a complete
pristine-state restoration policy.

## Independent comparison interface

[The pure oracle](../../../scripts/terminfo/static-images-oracle.mjs) accepts
`baseline`/`observed` decoded original full-grid RGBA images, the actual captured
`grid`, the anchor/cell envelope, geometry policy, source id and desired visibility.
It requires an independently blank target reference, checks every non-target
pixel against that reference, measures the non-background content bounds,
checks protocol geometry, then checks fixed asymmetric color/alpha interiors.
It returns separate controls, assertions and measurements, never a support grade.
It does not call a renderer or use a stored placement rectangle as pixel proof.

For disappearance/no-display, compare to the retained matching blank reference.
After a mode/cursor or scene change, the driver must obtain a new independently
validated reference rather than silently accepting pixel differences. The
reference cannot be replaced with the expected output of the renderer under test.
Full-grid originals, cell/mode/geometry reads and source bytes must be retained;
the sparse color/alpha interior checks do not claim exhaustive raster fidelity.

The bounded reply parser verifies exact I/i/p fields, unsigned limits, duplicate
keys, success/ENOENT status and distinct allocation ids. It accepts one separately
framed reply, not batches or trailing controls. It does not establish freshness:
the central worker must begin each request under the current direct-local proof,
retain raw request/reply bytes and use a fresh ordered challenge/fence. Repeated
placement replies have identical identities; syntax alone cannot reject a stale
reply. An ACK never makes a pixel assertion pass.

## Integration and evidence boundary

Only new uniquely named image modules/tests and this document are added here.
Do not import them from the current worker or modify the existing inventory,
receipt, CI or generated capability report until the QA owner coordinates the
next captured source-policy version. Preserve the immutable 51/52-source v8 and
60-source v9 graphs and their archived import closure. Runtime integration must
bind every executed image module and input, original capture PNG, complete
paired state reads, exact request/reply, per-case restoration and final cleanup.
Never finalize a measurement before verified worker/App exit, endpoint shutdown,
fixture removal and archive/hash checks.

The deterministic tests reject stretched aspect geometry even when all color
samples match, incorrect native size/anchor, substituted source/colors, lost
alpha, residual pixels, changed non-target regions, malformed context, wrong
correlation and reused allocation ids. They exercise a model with synthetic
pixels only. The separate runtime leading-G and aspect-fit corrections are not
qualified by these tests; a fresh announced owned collection is still required.
Animation, Unicode placeholders, live desktop cadence and Windows/Linux owned
qualification remain separate work. Historical grades and evidence are untouched;
#770 stays open.
