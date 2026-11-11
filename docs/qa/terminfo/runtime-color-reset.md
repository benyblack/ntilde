# Runtime-color reset effect contract (OSC 104, 110, 111, 112)

`ntilde-runtime-color-reset-effect-v1` is the effect contract of the runtime-color reset qualification
([report](2026-11-11-runtime-color-reset-qualification.md)). It qualifies four pinned terminfo.dev@4.0.0 cases and is only ever
stricter than the pinned termless criterion.

## The pinned definitions

Each case is one registration `probe(id, colorResetProbe(setCode, resetCode[, index]), () => Promise.resolve(unmeasuredStateEffect(...)))`.

| Case | Query | Set (exact) | Reset (exact) |
|---|---|---|---|
| `extensions.osc104-reset-palette` | `ESC]4;0;?BEL` | `ESC]4;0;rgb:aa/bb/cc BEL` | `ESC]104;0 BEL` |
| `extensions.osc110-reset-fg` | `ESC]10;?BEL` | `ESC]10;rgb:aa/bb/cc BEL` | `ESC]110 BEL` |
| `extensions.osc111-reset-bg` | `ESC]11;?BEL` | `ESC]11;rgb:aa/bb/cc BEL` | `ESC]111 BEL` |
| `extensions.osc112-reset-cursor` | `ESC]12;?BEL` | `ESC]12;rgb:aa/bb/cc BEL` | `ESC]112 BEL` |

**Termless path.** Capture the query, feed the set (`rgb:12/34/56` when the original already is `rgb:aa/bb/cc`), capture the query, feed the
reset, capture the query. Supported iff the changed reply equals the requested color and the restored reply equals the original; unsupported
when the mutation read back but the original did not return; inconclusive when the mutation or a readback was not established. It reads only
the three replies.

**Terminal path.** `unmeasuredStateEffect`: it writes nothing, reads nothing and is inconclusive by design (evidence `none`). The stock grade
of every case stays that; the extension evidence never promotes it.

## The effect contract

An independent runtime-color model (`color-reset-model.mjs`, no code shared with the VT) turns the exact bytes written so far into the
overrides each stage must show: OSC 4 sets palette slots, OSC 10/11/12 set the dynamic colors, OSC 104 resets the named slots (all when it has
no parameter), OSC 110/111/112 reset one dynamic color each, queries change nothing. The expected colors of a stage are the **measured
baseline** with the model's overrides laid over them, never conventional palette values.

Every stage is read from four separately retained layers, and each must match the model:

1. **Query replies**: the exact `ESC]…;rgb:rrrr/gggg/bbbb ESC\` reply (or its explicit absence) fenced by a DA barrier; sixteen-bit channels
   are the eight-bit value times 257.
2. **App-resolved colors**: the `readScreen` color binding (default colors, the 256-entry palette, override flags, override slot list) and every
   explicit cell's resolved color.
3. **Stored state**: default markers, palette-indexed and literal-RGB cells, text, cursor position, stored cursor mode and visibility.
4. **Controlled render**: the offscreen render theme (sources, override flags, derived cursor text and selection paint), the presentation phase
   (clock 0, cursor phase fixed visible, scale 1) and the original PNG, bound to the observed cursor state. Three samples are read from the PNG itself and compared
   with the model's colors, independently of the theme the App reported: an untouched default-background cell (one solid color), the dominant ink color of the
   default-colored text row, and the cursor cell of a visible steady block (one solid color). Whole-image digests add that a reset render differs from the previous
   stage's render, differs from the baseline render where an override the scene draws remains, and equals it byte for byte where none remains.

Each experiment is `baseline → override → reset…`. The baseline and the override are **controls**: no runtime override in any layer, two App
layers agreeing, the scene as written, the intended override present in every layer, each overridden target distinct from its baseline, the
render different from the baseline render. Invalid controls, missing replies, stale observations or identity drift make the experiment
**inconclusive**; with valid controls any mismatched reset stage makes it **unsupported**; otherwise it is **supported**. A case is unsupported
if any experiment is, else inconclusive if any is, else supported.

| Case | Experiments |
|---|---|
| OSC 104 | `pinned` (slot 0, BEL); `individual-slots` (slots 1, 9, 16, 200, 255 overridden; reset 1 alone, then 9 and 200, then 16 and 255, the unrelated slots staying overridden each time); `isolation` (slots 5 and 6 overridden beside the foreground, background and cursor; reset slot 5 alone, then the whole palette: the three dynamic colors stay overridden); `whole-palette` (all 256 slots overridden; `ESC]104 BEL` must restore all 256 and clear the override list; a control, the pinned fixture resets one slot) |
| OSC 110 | `pinned`; `isolation` (background, cursor and an unrelated palette slot stay overridden through an `ESC]110 ESC\`); `sgr-default` (SGR 39 changes the pen, not the runtime default; OSC 110 does not change the pen; explicit palette and literal cells never move) |
| OSC 111 | the same three for the background and SGR 49 |
| OSC 112 | `pinned` (steady visible block); `isolation` (foreground and background overrides survive; shape and visibility unchanged); `hidden-beam` (a hidden steady-beam cursor stays hidden and beam) |

The raw configured cursor color (`cursorArgb`) is a source; the cursor text color and selection paint are derived and are compared only when
no override is left, where the whole theme must equal the baseline exactly.

## What is and is not claimed

Runtime colors survive alternate-screen exit and DECSTR, so neither is evidence of a reset; the suite clears them explicitly in its finish
write and requires the original screen, theme and PNG back. Not claimed: real-time cursor blink cadence or physical desktop appearance
(the render phase is synthetic), selection and pointer colors, Kitty named colors, color stacks, other themes or platforms, and query replies
as rendered appearance (the render is a separate layer).
