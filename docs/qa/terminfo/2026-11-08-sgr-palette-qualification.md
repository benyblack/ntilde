# SGR palette-color qualification — 2026-11-08

Six pinned `terminfo.dev@4.0.0` cases — `sgr.fg.bright`, `sgr.bg.bright`,
`sgr.fg.256`, `sgr.bg.256`, `sgr.fg.default` and `sgr.bg.default` — measured
against a fresh disposable macOS desktop instance, each **supported** by
executing the pinned collector's own probe code over independently observed
cells, with the pinned result, an independent contract grader and a parent-side
re-derivation agreeing, beside a completed schema-17 cleanup receipt. The
fourteen earlier unique qualifications (ASCII text/bold/italic, SGR resets and
stored indices, resolved standard-color RGB, the five attributes) are
reconfirmed in the same run and their frozen publications still rederive.

This is a **local collector-extension measurement**, not stock CLI output. The
same instance's owned-adapter collector run keeps its recorded identity
(86 supported / 1 unsupported / 168 inconclusive, `suiteComplete=false`) and
grades all six cases inconclusive with `consumed` evidence ("Cursor advance
does not verify SGR styling"). No combined stock score is manufactured.

## Three layers, kept distinct

| Layer | What it is | Claimed here |
|---|---|---|
| **Stored** | per-cell palette index, default marker, or literal ARGB bytes (App `readScreen` attribute runs + cell details) | yes — exact indices, markers and bytes |
| **Resolved** | the App's storage-to-theme RGB for the same cell, bound to **one recorded palette/theme state** (resolution lives in App/Rendering, never in VT) | yes — at the theme-resolution stage only |
| **Rendered** | glyph/pixel appearance after inverse swap, contrast adjustment, compositing and rasterisation | **no** — no pinned criterion requires it; no pixel evidence was used |

Every report carries the layer verdicts beside the outcome
(`conclusion.layers = { stored, resolved }`).

## Pinned definitions and naming

The IDs, fixture bytes and acceptance criteria below were read from the pinned
bundle (SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`); the layered adapter
refuses derivation when any of these definitions, bodies or notes changes. No
related variant is substituted. The pinned probes read App-exposed per-cell
colors, where a default channel is `null` and a palette or literal channel is
resolved `{r,g,b}`.

| Case | Pinned fixture (one feed) | Pinned criterion |
|---|---|---|
| `sgr.fg.bright` | `C ESC[91m X ESC[94m Y` | the two named colors select distinct exposed fg RGB values (distinct from the neutral baseline and each other) |
| `sgr.bg.bright` | `C ESC[101m X ESC[104m Y` | same, bg |
| `sgr.fg.256` | `C ESC[38;2;95;135;175m A ESC[38;2;215;135;95m B ESC[38;5;67m X ESC[38;5;173m Y` | indices 67/173 resolve to the two independently observed direct-RGB cube references |
| `sgr.bg.256` | same with `48` | same, bg |
| `sgr.fg.default` | `C ESC[31m X ESC[39m R` | after a distinct colored X, `R` has the measured baseline fg |
| `sgr.bg.default` | `C ESC[42m X ESC[49m R` | same, bg |

Verdict semantics differ from the attribute batch: the pinned named and indexed
probes grade supported or inconclusive only, but the pinned **default** probe
can also grade unsupported (reset color differs from the baseline). The contract
grader may grade unsupported only after valid controls show an observed stored
mismatch; missing cells, invalid controls, DECSCNM, aliased/overridden palette
entries and indistinguishable colors stay inconclusive. It may be stricter than
the pinned probe (recorded as a `divergence`: `contract-stricter-than-pinned`,
`contract-measured-mismatch`, or `contract-more-cautious-than-pinned`) but never
more permissive. **No divergence occurred in this run.**

## What each conclusion establishes

Measured with the sidecar's default geometry, theme `Default`, DECSCNM off in
every frame, no palette overrides, minimum contrast 1. Flags are the App's
stored `TerminalCellFlags` (`768` = default fg+bg).

| Case | Observed target cells | Controls | Conclusion |
|---|---|---|---|
| `sgr.fg.bright` | `X` stored palette **9** (red `255,0,0`), `Y` palette **12**; bold false; bg default | neutral baseline `C`; bold+`SGR 31` stores the **standard** index 1 with the bold flag (bold is not bright); a repeat of `SGR 91` stores what `X` stored; `SGR 0` restores neutral | **supported** (stored 9/12, resolved distinct) |
| `sgr.bg.bright` | `X` palette 9, `Y` palette 12 on bg; fg default | same, bg | **supported** |
| `sgr.fg.256` | `A`,`B` stored **literal** `95,135,175` / `215,135,95`; `X` palette **67**, `Y` palette **173**, no literal bytes; `X`/`Y` resolve to the `A`/`B` values | palette index **0** and **255** are palette storage (not the default marker); `SGR 0` restores neutral | **supported** (stored 67/173 beside literal references, resolved equal to the canonical cube) |
| `sgr.bg.256` | same on bg | same, bg | **supported** |
| `sgr.fg.default` | `X` explicit palette **1** (resolves to `205,0,0`, distinct from the theme foreground), then `R` on the default marker (no index, no literal bytes) | both channels set (`31;42`), `SGR 39` resets **only** fg (bg stays palette 2); `SGR 0` restores neutral | **supported** (explicit nondefault setup, then the stored default marker) |
| `sgr.bg.default` | `X` explicit palette **2**, then `R` on the default marker | both set, `SGR 49` resets only bg | **supported** |

For `sgr.*.default`, a cell that merely started and stayed default proves
nothing: the setup `X` must be the contracted explicit palette color,
resolve distinctly from the theme default, and the reset must return the stored
default marker with no palette or literal representation.

Unrelated attributes (including overline and underline color) and the unaffected
channel were checked on every target cell; attribute-run flags are
cross-checked against the stored cell details of the same snapshot cell, and
resolved RGB must cohere with the one recorded palette/theme binding (stale or
fabricated RGB cannot validate). Rendered appearance is **not claimed**.

## Remaining inconclusive criteria

None among the six. Open, outside this batch: rendered appearance of the colors
(inverse swap, contrast adjustment, compositing — would need a controlled-pixel
suite), palette/theme variants beyond the measured `Default` theme, and the
stock collector's own `consumed` grades.

## Collector extension identity

| Measure | Value |
|---|---|
| Layered adapter | `ntilde-owned-sgr-palette-grading-v1` (contracts v1), suite `ntilde-owned-sgr-palette-graded-v1` |
| Derived from | `ntilde-owned-sgr-attributes-grading-v1` → `ntilde-owned-sgr-rgb-grading-v1` → `ntilde-owned-sgr-colors-grading-v1` → `ntilde-owned-ascii-sgr-grading-v1` → `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Layered bundle SHA-256 | `fdabe03cffe454ba68ced014824a8ecdf8f1ca97d50df5165f81e5a86be52488` |
| Receipt schema / source policy | 17 / `readonly-owned-source-graph-v11` (inventory v18, 133 files); policies v1–v10 keep their frozen maps |

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `1814f55c3d61995209db7f2141f16eaf`, receipt 2026-11-08T09:31:26.549Z |
| App | `0.13.0`; **App source identical to main `d29f56ff` (#889)** — `git diff d29f56ff HEAD -- src` is empty; sidecar built fresh from this branch |
| Adapter/scripts source | commit recorded in the [summary](2026-11-08-sgr-palette/summary.json) (`adapterRevision`), pinned byte-for-byte by the run's policy-v11 inventory |
| Same-run predecessors | grading 4/4, SGR-colors 5/5 (contracts v2), SGR-RGB 2/2, SGR-attributes 5/5 supported — fourteen unique qualifications |
| Local evidence | `artifacts/terminfo/owned-BhLFns`; digests in the summary |

## Publication layout

The evidence is split so every file is reviewable in one piece (each ≤ 1,200
lines): [`summary.json`](2026-11-08-sgr-palette/summary.json) (context, receipt,
same-run predecessors, stock grades, retained digests, attempts),
[`report.json`](2026-11-08-sgr-palette/report.json) (the graded report without
frames), [`run-log.json`](2026-11-08-sgr-palette/run-log.json) (the worker run
log) and one parent-journal file per case plus
[`journal-common.json`](2026-11-08-sgr-palette/journal-common.json) (start, bind,
finish). The publication test reassembles the exact journal order and re-derives
the whole report.

## Preserved attempts

See `attempts` in the summary; failed and superseded measurements are kept
unmodified.

## Reproduce

```sh
node --test scripts/terminfo/sgr-palette-*.test.mjs
```

The publication test re-derives the whole report from the committed journal and
run log; the receipt test re-derives the six-layer adapter chain and rejects
tampered bytes, identities, counts, predecessors and downgrades.
