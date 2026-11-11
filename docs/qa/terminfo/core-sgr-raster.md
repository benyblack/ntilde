# Core SGR reset raster experiments

`ntilde-core-sgr-raster-v1` measures the newer App criteria of three Core rows, which are
appearance tests rather than stored-flag tests:

| ID | Attributes (subsets of) | Reset under test | Expected final crop |
|---|---|---|---|
| `sgr.selective-reset.bold` | bold, faint, italic | SGR 22 | the italic-only crop |
| `sgr.reset` | bold, italic, inverse | SGR 0 | the neutral crop |
| `reset.sgr` | bold, italic, inverse | SGR 0 | the neutral crop |

The earlier stored-state qualifications (`sgcolors-observations.json`, `svrs-observations.json`) are
separate experiments with their own fixtures and conclusions; this suite never substitutes one for
the other. `sgr.reset` and `reset.sgr` share a criterion text but each runs its own matrix with its
own captures and keeps its own conclusion.

## Experiment

Each experiment runs in the owned worker's alternate screen with the cursor hidden, at one fixed
presentation time (0 ms), scale 1 and the production offscreen draw path, then:

1. draws neutral `YY` at a fixed cell, returns to it and saves with DECSC (nothing but plain
   preparation precedes this);
2. for each of the seven nonempty subsets: moves there, sets the subset, draws `YY`
   (capture), restores with **DECRC alone** and redraws `YY` (capture);
3. sets every attribute and draws `YY`, sends the made-up SGR 99 and redraws, then sends the reset
   under test and redraws (a capture each).

Per experiment that is 18 captures. Every capture brackets one controlled render between two
identical complete state reads (text, cursor, cursor mode, stored cell attributes and overline) and
is retained as the original PNG (`sr-pixels/frame-NNN.png`). Crops are exact decoded RGBA rectangles
(the two specimen cells plus one blank cell on each side and the rows above and below) taken at
coordinates derived from the draw's own `renderDetails.grid`; an unchanged control text row is
compared across every capture to detect drift. Exact byte equality is the comparison: restoration is
never tolerance-matched.

## Decision tree (the retained catalogue text, applied literally)

* Structural controls fail (incoherent stored text/cursor, drifting control region, a blank neutral
  crop, an all-set crop that does not repeat its subset crop): **inconclusive**, no fallback.
* A subset does not change the neutral crop, a DECRC restore (pixels or stored attributes) is not
  exact, or (SGR 22 only) the italic-only crop is not distinct from neutral and every other subset:
  the raster controls do not qualify and **DECRQSS decides**. The reply to `ESC P $ q m ESC \`,
  asked in the neutral, all-set, SGR 99 and final states, is retained raw (request, reply, DA
  barrier) and decoded against specified attribute codes; the neutral, all-set and SGR 99 queries
  must report the specified pens before the final reply can decide. A valid reply with the wrong
  rendition is **unsupported**; silence, a status-0 refusal, a malformed or other-setting reply keep
  the named refusal (**inconclusive**). A fallback verdict is a *query-based* conclusion and the raster
  experiment is not claimed.
* Qualified raster controls: SGR 99 changed the crop → **invalid** (inconclusive); the final crop equals
  the expected crop → **supported**; equals neutral or another identified subset crop → **unsupported**;
  matches none → **invalid**. A qualified negative is never overridden by a DECRQSS report.

The final Core conclusion combines this with the stored-state qualification: a pass needs both
supported; either unsupported is a fail; anything else is inconclusive.

Expected behavior comes from this specification (attribute sets and the reset semantics of SGR 0 and
22), never from the production encoder or resolver. Stored attributes of the cells drawn are retained
beside every crop as an independent supporting fact; they never replace the pixels.

## Limits

Controlled offscreen appearance under the recorded font, palette, geometry and presentation phase.
No claim about physical desktop appearance, other fonts or platforms, or animation cadence. The raster
geometry is the owned pane's measured geometry, recorded explicitly and independent of any wrapping
phase.
