# Truecolor-literal finding — 2026-11-08

**This is an Ntilde finding, not a terminfo.dev case.** It has its own suite
(`ntilde-owned-truecolor-literal-finding-v1`, case ids `finding.truecolor-literal.*`), no
pinned probe, no collector extension and no stock grade. It does not rewrite any historical
terminfo.dev grade or publication, and it does not add to the stock collector's counts.

## The behavior

`AnsiParser.Sgr.cs` rewrote five exact direct-RGB requests (`38/48;2;r;g;b`) into palette
indices before storing them:

| Requested RGB | Stored as |
|---|---|
| `0,55,218` | palette 4 |
| `58,150,221` | palette 4 |
| `12,12,12` | palette 0 |
| `204,204,204` | palette 7 |
| `242,242,242` | palette 15 |

Verified in source and on the real App (below), on both foreground and background.

## Rationale and dependents (investigated)

The remap arrived in the Feb 2026 theme work (`7c3040ce`, "Add themes") with the comment
"Snapping for Campbell Blue/Black": Windows Terminal's Campbell scheme uses these exact
values for its black, blue, cyan and white entries, so mapping them to indices made output
that spelled the scheme in RGB follow the active theme. It was never documented, and the
July 2026 deep review already recorded it as a defect ("hard-coded Campbell snapping RGB
remap corrupts legitimate truecolor that hits those exact values"). The mapping was also
internally inconsistent: `58,150,221` is Campbell's *cyan* but was mapped to blue (4).

Nothing depended on it: no test, replay corpus, parity corpus or documentation referenced
the five values; the only reader of the stored index is the generic palette path (render
resolution, DECRQSS, state transfer and replay all treat an index like any explicit
`38;5;n`). Explicit indexed colors (`30-37`, `90-97`, `38;5;n`, `38:5:n`) were never routed
through the remap and are unchanged.

## Observable consequences (before the correction)

A direct-RGB request is a literal, so a program that asked for `204,204,204` expects exactly
that color. Measured on the real App with the default theme and an explicit indexed positive
control (the suite's "before" run):

- the cell was **stored as the palette index** (no literal bytes) in the semicolon,
  colon-omitted-colorspace and colon-explicit-colorspace forms;
- its **initial resolved color differed** from the request (`0,55,218` resolved to the theme's
  blue `0,0,238`; `12,12,12` to black `0,0,0`; `204,204,204` to `229,229,229`; `242,242,242`
  to `255,255,255`; `58,150,221` to `0,0,238`);
- an **OSC 4 change of the aliased entry changed the explicit RGB cell** (to the new color),
  while a genuine literal would not move;
- DECRQSS reported a standard or bright color code (`30`-`37`, `90`-`97`, `40`-`47`, `100`-`107`) instead of `38;2;r;g;b`, so a replayed
  report no longer reproduced the pen.

Internal storage choice versus observable bug: the first bullet alone is a storage choice;
the second and third are observable violations of the truecolor contract on the default
theme (under a Campbell theme the initial color happened to match, which is the original
intent, but the OSC 4 and theme-change behavior was wrong everywhere).

## The correction

The parser no longer remaps direct RGB: a `38/48;2;r;g;b` request is always stored as literal
bytes with the palette index and default marker cleared, for every value, on both channels,
in the semicolon and both colon forms. The change is the removal of the five-value remap
(two copies) in `AnsiParser.Sgr.cs`; explicit indexed behavior is untouched, VT gains no
rendering or UI dependency and no hot path changes.

Compatibility: output that spelled the Campbell scheme in RGB now shows exactly those RGB
values and no longer follows theme or OSC 4 changes (which is what a literal request
means). Programs that want theme-following colors should use SGR 30-37/90-97 or `38;5;n`.

## Regressions

`tests/Ntilde.VT.Tests/TruecolorLiteralStorageTests.cs` (173 cases) was written first and
failed on the unmodified parser with **120 failures** (30 per test across storage, resolved
color before/after OSC 4, the indexed positive control and the DECRQSS report) while its 53
control cases (explicit indexed storage on both channels, near-miss values, literal
replacement) passed. After the correction all pass, as does the full VT project (2,812),
the Rendering (175) and Architecture (191) projects.

## Forms actually tested

Semicolon `38;2;r;g;b` / `48;2;r;g;b`; colon with the omitted colorspace id
`38:2::r:g:b`; colon with an explicit colorspace id `38:2:0:r:g:b` — each separately, per
value and channel. Nothing else is claimed.

## Owned evidence (before and after)

Suite `ntilde-owned-truecolor-literal-finding-v1` (contracts v1), ten cases (five values ×
fg/bg), receipt schema 19, source policy v13 (inventory v20, 144 files). Per case, one
fixture carries a neutral baseline, an explicit indexed positive control (`38/48;5;n`) and
the three literal forms; the plan then applies an OSC 4 change to the aliased entry
(`ESC]4;n;rgb:ff/00/ff BEL`), reads again, restores it (`ESC]104;n BEL`) and reads a third
time. Every frame carries the App's recorded palette/theme binding, so the mutation and the
restoration are themselves observed; the run begins and ends on the same recorded palette.

Conclusions need valid controls: DECSCNM off, neutral baseline, an indexed control that
follows the mutation and restores, a mutation that changed exactly the aliased entry to the
contracted color and nothing else, and the unaffected channel and unrelated attributes
clear. Missing or invalid controls are inconclusive, never unsupported.

| Run | App source | Result |
|---|---|---|
| before | main `86546641` | 10/10 **unsupported** (stored as palette 4/4/0/7/15; initial resolved color differs; changed by OSC 4) |
| after | this change `34b8e7ec` | 10/10 **supported** (literal bytes; initial resolved equals the request; unchanged by OSC 4) |

Both runs use the same scripts revision `34b8e7ec` and complete their cleanup receipts.
Layers stay separate: stored literal bytes and palette indices, resolved RGB at the
theme-resolution stage, and **no rendered-appearance claim**. In each run the 22 prior
unique qualifications (ASCII text/bold/italic, SGR resets and stored indices, resolved
standard colors, the five attributes, the six palette cases, the two truecolor cases) are
reconfirmed in the same run and every report and the completed receipt rederive from
retained bytes. The stock collector keeps its own grades in both runs (86 supported /
1 unsupported / 168 inconclusive, `suiteComplete=false`).

| Measure | before | after |
|---|---|---|
| Run | `763ceb3ab05a9b51ffd4b11f03f2bda4` | `68334378d2fda6c04ea7ae0b826347fa` |
| Receipt completed | 2026-11-08T14:12:53Z | 2026-11-08T14:13:31Z |
| Local evidence | `artifacts/terminfo/owned-WzBTKL` | `artifacts/terminfo/owned-huDNo0` |
| App revision | `86546641` (main) | `34b8e7ec` |

Publication: [`summary.json`](2026-11-08-truecolor-literal-finding/summary.json) and, per run
(`before/`, `after/`), the context and receipt, the run log, a common report, one report and
one journal file per case (each ≤ 510 lines). The publication test reassembles both runs and
re-derives the full reports.

## Preserved attempts

Kept unmodified locally and listed in the summary: the first before/after measurement pair
(`owned-8Z05an` 10/10 unsupported, `owned-gHDHuj` 10/10 supported; scripts `31696fd4`),
superseded because CodeRabbit found that unrelated palette entries were compared only
between reads and not with the run's start palette — an evidence-validation change, so both
runs were repeated — and the Codex-review-sandbox run `owned-cvj6ym`, which failed before
collection. Later Codex-sandbox failure directories, if any, hold no evidence.

## Limits

Rendered glyph appearance (inverse swap, contrast adjustment, compositing) is not claimed.
Only the default theme was measured; the contract also holds for any theme because the
literal is stored independent of it. No other value was found to be aliased: the parser had
exactly these five remaps.

## Reproduce

```sh
node --test scripts/terminfo/truecolor-literal-finding-*.test.mjs
```
