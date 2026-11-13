# Selection-color effect contract (OSC 17, 19, 117, 119)

`ntilde-owned-selection-colors-graded-v1` is the effect contract of the selection-color qualification
([report](2026-11-12-selection-colors-qualification.md)). It qualifies four pinned terminfo.dev@4.0.0 cases and is only
ever stricter than the pinned criteria.

## The pinned definitions

The set cases are one registration each, `oscColorQueryProbe(id, code)` — `queryOnly(probe(...))` with
`termlessObservationEvidence: "query"`:

| Case | Query (exact) | Pinned termless criterion |
|---|---|---|
| `extensions.osc17-highlight-bg` | `ESC]17;?BEL` | supported iff a complete `ESC]17;rgb:{1-4}/{1-4}/{1-4}` reply (BEL or ST terminated) is present; inconclusive (`invalid-reply`) when the raw capture carries the `ESC]17;` prefix without a complete reply; inconclusive (`no-response`) otherwise |
| `extensions.osc19-highlight-fg` | `ESC]19;?BEL` | the same for OSC 19 |

The pinned termless code issues exactly one `feedCapture`; the pinned terminal path re-asks the same query behind a
sentinel and grades by the same `oscReplyResult`. Neither decides anything about setting or resetting a selection
color: the fixture is a query only.

The reset cases pin their whole registration object verbatim:

| Case | Termless fixture (exact) | Pinned termless result | Pinned terminal path |
|---|---|---|---|
| `extensions.osc117-reset-highlight-bg` | `ESC]117 BEL X` then `getCell(0,0)` | `notTestedResult` (`notTested.reason: "no-semantic-observable"`), whatever the cell holds | writes `ESC[1;1H ESC[2K` and `ESC]117 BEL`, queries the cursor, returns `unverifiedEffect` (inconclusive, evidence `consumed`) |
| `extensions.osc119-reset-highlight-fg` | `ESC]119 BEL X` then `getCell(0,0)` | the same for the highlight foreground | the same with `ESC]119 BEL` |

Both reset paths are inconclusive by construction. The derivation pins the four registrations, the
`oscColorQueryProbe` factory body and the helper bodies their verdicts run through (`queryOnly`, `oscReplyResult`,
`notTestedResult`, `unverifiedEffect`) verbatim; a change to any of them is refused.

**State under the pinned set-case query.** The stock collector runs the OSC 17 and OSC 19 probes right after the
OSC 117 and OSC 119 probes have explicitly reset both selection colors, so the queried state is the unset
default. Ntilde's default theme defines no explicit selection colors (`TerminalTheme.SelectionBackground` and
`SelectionForeground` are null), and an unset selection source intentionally produces no concrete OSC reply —
the pinned criterion is therefore inconclusive (`no-response`) in the default state, and that inconclusive
verdict is preserved. The absence is valid evidence only because (a) each exchange completes behind its DA
ordering barrier, so a settled null reply is not a timeout or a broken fence, (b) the same case's other
experiments contain a qualified positive override-query control (an exchange with a concrete expected color
arrived and matched, proving the reply path works), and (c) the null marker is never substituted with black,
white or any other RGB value in any layer.

## The effect contract

An independent selection-color model (`selection-colors-model.mjs`, no code shared with the VT) turns the exact
bytes written so far into the overrides each stage must show: OSC 17/19 set the selection background/foreground
overrides, OSC 117/119 clear exactly one of them, OSC 4/104 and 10/11/12/110/111/112 touch only their own
channels, queries change nothing. Expected restoration is the **measured baseline** with the model's overrides
laid over it, never conventional palette values; a selection source with no override keeps its measured
baseline value, which is `null` here.

Every stage is read from four separately retained layers, and each must match the model:

1. **Query replies**: the exact `ESC]…;rgb:rrrr/gggg/bbbb ESC\` reply (or its explicit absence) fenced by a DA
   barrier; sixteen-bit channels are the eight-bit value times 257. An unset selection source must reply
   nothing; a set source must reply with its exact color.
2. **App-resolved colors**: the `readScreen` color binding (default colors, the 256-entry palette, override
   flags, override slot list) and every explicit cell's resolved color. Selection overrides paint the render,
   never the cells.
3. **Render theme sources**: the controlled render theme's sources (foreground, background, cursor, the
   nullable `selectionBackgroundArgb`/`selectionForegroundArgb`, palette) and its override flags, compared with
   the derived selection paints and cursor text excluded; when no override remains, the complete theme
   including every derived value must equal the baseline exactly.
4. **Controlled render**: the offscreen render at a fixed synthetic phase (clock 0, cursor hidden, scale 1)
   with the established render-only synthetic selection `(2,4)..(4,10)` and its original PNG. From the PNG
   itself: the untouched default-background cell, the unselected explicit-RGB control row (byte-identical to
   the baseline's in every stage), the selected blank row (a single solid color equal to the active selection
   background override when one is set, byte-equal to the baseline's region otherwise) and the selected text
   ink (its dominant non-fill color equal to the baseline's when no foreground override is set, carrying the
   active override value in force when one is, and never the other distinguishable value). Whole-image digests
   add that a stage which changes what the scene draws differs from the previous stage's render, that a stage
   leaving an override the scene draws differs from the baseline render, and that a final stage with nothing
   drawn overridden equals the baseline render byte for byte.

Each experiment is `baseline → override(s) → reset…`. The baseline and the override stages are **controls**: no
runtime override in any layer, the two App layers agreeing, the scene as written, the intended override present
in every layer and distinct from its baseline, the render different from the baseline render. Invalid controls,
missing replies, stale observations or identity drift make the experiment **inconclusive**; with valid controls
any mismatched reset stage makes it **unsupported**; otherwise it is **supported**. A case is unsupported if any
experiment is, else inconclusive if any is, else supported — and a case qualifies only when a qualified
positive override-query control passed somewhere in it.

| Case | Experiments |
|---|---|
| OSC 17 | `pinned` (the single BEL-spelled default-state query); `set-query` (ST spelling: baseline null → override A `rgb:aa/bb/cc` in every layer → override B `rgb:12/34/56` rejecting a stale value → reset with OSC 117 restoring the unset default); `bel-spelling` (a labeled control: the BEL spellings of the same cycle, state layers only) |
| OSC 19 | the same three for the selection foreground |
| OSC 117 | `pinned` (the exact `ESC]117 BEL X` fixture on the default state, cell (0,0) = `X`, the selection query asked beside it); `cycle` (set OSC 17 A → reset with the exact pinned BEL spelling; the unset default returns in every layer and the render returns to the baseline bytes); `independence` (background A, foreground B and an unrelated palette slot set together; reset only the background (ST) — the foreground override and the slot survive; then reset the foreground (BEL) — only the slot survives; both reset orders) |
| OSC 119 | the mirror: the pinned `ESC]119 BEL X` fixture, the foreground cycle, and the independence experiment resetting the foreground first (ST) and the background second (BEL) |

The coupled controls across the two reset cases cover setting both overrides together, resetting only one with
the other surviving (each direction), exercising both reset orders, and leaving the unrelated palette slot,
the ordinary dynamic colors (OSC 10/11/12 queried directly), stored cells, the cursor and the rest of the
theme untouched throughout.

Extra spellings (BEL versus ST, the second value) are labeled controls or stale-rejection stages, never extra
benchmark cases: the suite claims exactly the four pinned ids.

## What is and is not claimed

Runtime selection colors survive alternate-screen exit and DECSTR, so neither is evidence of a reset; the suite
clears them explicitly in its finish write and requires the original screen, theme and PNG back. The pinned
terminal paths never carry a verdict and the stock grades are unchanged. Not claimed: pointer colors, ordinary
foreground/background/cursor resets, clipboard behavior, physical mouse selection, native desktop appearance,
real-time rendering cadence, other themes or platforms, remote delivery, and query replies as rendered
appearance (the render is a separate layer at a fixed synthetic phase). A selected-region render establishes
that rendering path only.
