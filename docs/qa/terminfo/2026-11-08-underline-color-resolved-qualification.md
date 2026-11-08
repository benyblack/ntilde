# App-observed underline-color resolution (2026-11-08)

Follow-up to the [SGR underline-color qualification](2026-11-08-sgr-underline-color-qualification.md) (PR #895, merge
`11b0fb0d`). That batch graded the three pinned cases from stored state and derived the "resolved" underline RGB
**parent-side** from the stored index and the App-reported palette; the App reported no resolved underline color.
This follow-up adds an opt-in App projection and qualifies it with fresh owned evidence. **No benchmark case is added
or changed**: `sgr.underline-color-indexed`, `-rgb` and `-reset` keep their narrower evidence, and the overlapping
aggregate `sgr.underline.color` stays unqualified. Rendered pixels are not claimed.

## Actual underline resolution (draw path)

Traced in `TerminalDrawOperation` (the run loop that strokes the underline):

1. **No underline** (`IsUnderline` false) → nothing is stroked. An underline *color* without a style is never stored.
2. **Explicit SGR 58 color** (`CellUnderline.HasColor`): a palette index resolves through the theme palette
   (`ResolvePaletteIndex`, the same entry a foreground index uses); a literal is its stored RGB. The stroke color is set
   from `ResolveUnderlineColor` and is **untouched by inverse, faint, minimum contrast and the foreground override**.
3. **Default marker** (index -1, no literal; the state after SGR 59 or never set): the stroke keeps the run's text
   color. That is the cell's resolved foreground, or its resolved **background under inverse video**
   (`bg = IsInverse ? cf : cb; fg = IsInverse ? cb : cf`), then adjusted by minimum contrast, the faint blend and the
   foreground override.
4. DECSCNM is already folded into the theme defaults handed to the resolver.

`ResolveCellUnderline` implements steps 1–4 once; the draw path's explicit-color branch uses the same
`ResolveUnderlineColor`, and both the draw loop and the projection take the presented text color from the shared
`PresentedTextColor`. There is no second implementation.

## Stage exposed

`readScreen { includeResolvedColors: true, includeResolvedUnderlineColors: true }` (the second flag is only effective
with the first, mirroring `includeOverline`/`includeCellDetails`). Each resolved-color run gains `ul`
(`none | inherit | palette | literal`, `index`, `inheritsFrom`, opaque `argb`) and the binding gains
`underlineStage: "underline-resolution"` plus a note. It is captured from the same coherent render snapshot as the
foreground/background runs, under the same budgets, resize refusal, access checks and windowless refusal (a windowless
session is refused with `screenDetailsUnavailable`; the flag alone is ignored). Without the flag the response bytes
are unchanged.

For `inherit` the reported value is the presented text color **at the theme-resolution stage**. **Excluded**: minimum
contrast, the faint blend, the foreground override, whether the stroke is painted (hidden, blink phase, selection or
cursor overlays), opacity compositing and rasterisation. Rendered appearance is not claimed.

## Suite (Ntilde extension controls, not benchmark criteria)

Suite `ntilde-owned-underline-color-resolved-v1`, contracts v1, prefix `sgur`, nonce `NTILDE_UR_`. Every check is an
**Ntilde-specific extension contract** (no pinned probe, no collector extension, no terminfo.dev grade). Each case runs
start → OSC 4 mutation of entries 4 and 5 → OSC 104 restoration and reads the whole screen after each step.

| Case | Stored fixture | Claim |
|---|---|---|
| `ntilde.underline-color-resolved.indexed` | default, `58;5;5`, `58;5;4`, SGR 59, SGR 24 | indexed resolves to the palette entry, follows the mutation and returns; default and absent are unaffected |
| `ntilde.underline-color-resolved.literal` | default, `58;2;255;0;128`, `58;2;1;2;3`, `58;2;205;0;205` (= default entry 5), SGR 59 | literals resolve to their bytes and never follow the mutation, including the aliasing literal |
| `ntilde.underline-color-resolved.default-inheritance` | default over default/palette/literal fg, inverse over default/palette bg, explicit palette under inverse, SGR 59 | default inherits fg (bg under inverse) and follows that channel's palette entry; explicit is untouched |

Four facts must agree per cell and read: the **stored representation** (parent-journaled cell details), the **captured
binding** (per-read palette/theme, validated for all 256 entries and the override list), an **independent expectation**
(computed from stored state, the run's start binding and the contracted OSC 4 constants, never from the App's `ul`) and
the **App-observed** resolution. Reports retain `appObserved` and `parentExpected` as separate fields; a parent
calculation is never labelled an App observation. Unsupported needs valid controls plus an observed mismatch; an
unprepared setup, a drifting palette or theme, a missing or incomplete mutation, DECSCNM on or stored cells changing
across reads are inconclusive. Opt-in is validated both ways (requested ⇒ present and coherent with the stored flags,
index, bytes and inverse state; not requested ⇒ absent), so App values cannot be smuggled into frozen-shape evidence.

## Measurement

Committed source `dcdcc28b` (scripts and App source coincide). Owned run `owned-ka4SX9`, run id
`e3f14e99bd11bc5776436d5ebe8ffb36`, receipt completed 2026-11-08T20:01:59Z, receipt schema 22, source policy v16
(inventory v23, 158 files), default theme (palette 4 = 0,0,238; palette 5 = 205,0,205; default foreground 211,211,211).
First and only collection; no failed attempt.

| Case | Outcome | Layers (stored / App-resolved) |
|---|---|---|
| indexed | supported | supported / supported |
| literal | supported | supported / supported |
| default-inheritance | supported | supported / supported |

- **Indexed:** X palette 5 → `ffcd00cd`, Y palette 4 → `ff0000ee`; during OSC 4 they became `ff445566` / `ff112233`
  with stored indices unchanged; restored to the originals.
- **Literal:** X `ffff0080`, Y `ff010203` and P `ffcd00cd` (aliasing literal) were identical in all three reads.
- **Inheritance:** a default underline over foreground palette 5 reported `ffcd00cd` then `ff445566` (it follows the
  channel); over a literal foreground `ff0a141e` throughout; under inverse it inherited the background (default
  `ff000000`, palette-5 background `ffcd00cd` → `ff445566`); an explicit palette-4 underline under inverse was untouched.
- **Differences between App observations and parent-derived expectations: none.** All cells agreed in every read.

Predecessors rederive in the same run: the 29 unique benchmark qualifications (layers ascii-sgr 4, colors 5, rgb 2,
attrs 5, palette 6, truecolor 2, underline 4, underline-color 3 — 31 layer entries, 29 unique ids) and the separate
10/10 truecolor-literal finding; stock counts unchanged (86 supported, 1 unsupported, 168 inconclusive).

## Limits

Resolved at the theme-resolution stage only; the minimum-contrast, faint and override adjustments of a default
underline and whether it is painted are excluded; no pixel or glyph claim. Qualification is for the default theme of the
owned sidecar on macOS, not for other platforms.

## Evidence

[`2026-11-08-underline-color-resolved/`](2026-11-08-underline-color-resolved/): summary, per-case reports, run log and
journals. `sgr-underline-color-resolved-publication.test.mjs` re-derives the report from the retained journal.
