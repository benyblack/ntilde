# SGR truecolor qualification — 2026-11-08

Two pinned `terminfo.dev@4.0.0` cases — `sgr.fg.truecolor` and
`sgr.bg.truecolor` — measured against a fresh disposable macOS desktop instance,
each **supported** by executing the pinned collector's own `truecolorProbe` code
over independently observed cells, with the pinned result, an independent contract
grader and a parent-side re-derivation agreeing, beside a completed schema-18
cleanup receipt. The twenty earlier unique qualifications (the ASCII, SGR-colors,
SGR-RGB, SGR-attributes and SGR-palette batches) are reconfirmed in the same run and
their frozen publications still rederive.

This is a **local collector-extension measurement**, not stock CLI output. The same
instance's owned-adapter collector run keeps its recorded identity (86 supported /
1 unsupported / 168 inconclusive, `suiteComplete=false`) and grades both cases
inconclusive with `consumed` evidence ("Cursor advance does not verify SGR
styling"). No combined stock score is manufactured.

## Three layers, kept distinct

| Layer | What it is | Claimed here |
|---|---|---|
| **Stored** | per-cell literal ARGB bytes, palette index and default marker (App `readScreen` attribute runs + cell details) | yes — exact literal bytes with index and marker cleared |
| **Resolved** | the App's storage-to-theme RGB for the same cell, bound to **one recorded palette/theme state** (resolution lives in App/Rendering, never in VT) | yes — at the theme-resolution stage only |
| **Rendered** | glyph/pixel appearance after inverse swap, contrast adjustment, compositing and rasterisation | **no** — no pinned criterion requires it; no pixel evidence was used |

Every report carries the layer verdicts beside the outcome
(`conclusion.layers = { stored, resolved }`).

## Pinned definitions, naming and syntax

Read from the pinned bundle (SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`); the
layered adapter refuses derivation when any definition, body or note changes.

| Case | Pinned fixture (one feed) | Pinned criterion |
|---|---|---|
| `sgr.fg.truecolor` | `C ESC[31m A ESC[34m B ESC[38;2;255;128;0m X ESC[38;2;17;97;201m Y` | two distinct direct-RGB samples are exposed at their exact requested values |
| `sgr.bg.truecolor` | `C ESC[41m A ESC[44m B ESC[48;2;0;255;128m X ESC[48;2;17;97;201m Y` | same, bg |

**Naming differences.** The pinned bundle also registers `extensions.truecolor`
(foreground only, no background twin, a different group, criterion text and
`The preceding color can mimic an ignored RGB request` note). It is a *related
variant*, is not part of this batch, and is neither graded nor substituted.

**Syntax scope.** Only the semicolon form `38/48;2;r;g;b` that the pinned fixture
exercises is qualified. Colon forms, optional colorspace ids and combined
single-sequence fg+bg forms are separate variants; they are neither fed nor claimed.

**Verdict semantics.** The pinned probe may grade `unsupported` (requested values
differ while the two ordinary-color controls are distinguishable), `supported`, or
`inconclusive` (cells not exposed, readback absent, the preceding color could mimic
an ignored first request, or the controls are not calibrated). The contract grader
keeps those inconclusives and grades unsupported only after valid controls plus an
observed stored or resolved mismatch. It may be stricter than the pinned probe
(`contract-stricter-than-pinned`: e.g. a palette entry resolving to the requested
RGB; `contract-measured-mismatch`: e.g. a target left on the default marker, which
the pinned probe reports as absent readback) or more cautious
(`contract-more-cautious-than-pinned`), never more permissive. **No divergence
occurred in this run.**

## What each conclusion establishes

Measured with the sidecar's default geometry, theme `Default`, DECSCNM off in every
frame, no palette overrides, minimum contrast 1. Baseline flags `768` = default fg+bg.

| Case | Observed target cells | Controls | Conclusion |
|---|---|---|---|
| `sgr.fg.truecolor` | `X` stored **literal** `255,128,0`, `Y` literal `17,97,201`, palette index −1, not the default marker; resolved equal to the requested RGB; `A`/`B` palette 1/4 (red `205,0,0`, blue `0,0,238`); bg default | literal `95,135,175` (`P`) and palette 67 (`Q`) resolve **alike** yet store differently; literal black `0,0,0` and white `255,255,255` are literal storage; a literal fg beside an explicit bg palette keeps that bg (`U`); `SGR 0` restores neutral | **supported** (stored supported, resolved supported) |
| `sgr.bg.truecolor` | `X` literal `0,255,128`, `Y` literal `17,97,201` on bg; fg default | same, bg | **supported** |

Unrelated attributes (including overline and underline color) and the unaffected
channel were checked on every target cell; attribute-run flags, cell-detail flags and
indices, and resolved channels are cross-checked per cell, and resolved RGB must
cohere with the one recorded palette/theme binding. Rendered appearance is **not
claimed**.

## Known limit: values the parser snaps to palette colors

`AnsiParser.Sgr.cs` recognises five exact `38/48;2;r;g;b` values taken from Windows
terminal themes and stores them as **palette indices**, not literal bytes:
`0,55,218` and `58,150,221` become index 4, `12,12,12` index 0, `204,204,204`
index 7 and `242,242,242` index 15. A program that sends one of these gets a theme
color back, which follows later theme changes.

This qualification does **not** cover those five values: no pinned fixture or control
uses them (the boundary controls use `0,0,0` and `255,255,255`, which are not
snapped). Read "supported" as "the contracted fixture values, black and white are
stored as exact literal bytes", not "every truecolor value is stored exactly". A
control for the snapped values is a separate finding:
[truecolor-literal finding](2026-11-08-truecolor-literal-finding.md). It confirmed the
snapping on the real App, and the parser was corrected afterwards; this document's
original measurement and verdicts are unchanged and were taken against the App as it
was (before the correction).

## Remaining inconclusive criteria

None among the two. Open, outside this batch: rendered appearance of the colors
(would need a controlled-pixel suite), other themes/palettes, the colon and
colorspace syntax variants, `extensions.truecolor`, and the stock collector's own
`consumed` grades.

## Collector extension identity

| Measure | Value |
|---|---|
| Layered adapter | `ntilde-owned-sgr-truecolor-grading-v1` (contracts v1), suite `ntilde-owned-sgr-truecolor-graded-v1` |
| Derived from | `ntilde-owned-sgr-palette-grading-v1` → `…sgr-attributes…` → `…sgr-rgb…` → `…sgr-colors…` → `…ascii-sgr…` → `ntilde-owned-macos-v1` over pinned `terminfo.dev@4.0.0` |
| Pinned bundle SHA-256 | `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6` |
| Layered bundle SHA-256 | `37bfb1faf4766e7bc893f8c8a440ece041cd2572e5fb3d998e8d797cfcd22b6c` |
| Receipt schema / source policy | 18 / `readonly-owned-source-graph-v12` (inventory v19, 140 files); policies v1–v11 keep their frozen maps |

## Measurement

| Measure | Value |
|---|---|
| Run / receipt completed | run `a44537686a17710c484e2bfcbf3194d9`, receipt 2026-11-08T11:18:42Z |
| App | `0.13.0`; App source identical to main `2218cb74` (no `src/` change on this branch); sidecar built fresh from committed `d0595efd` (App source unchanged since) |
| Adapter/scripts source | commit `e6c88b89` (`adapterRevision` in the [summary](2026-11-08-sgr-truecolor/summary.json)), pinned byte-for-byte by the run's policy-v12 inventory |
| Same-run predecessors | grading 4/4, SGR-colors 5/5, SGR-RGB 2/2, SGR-attributes 5/5, SGR-palette 6/6 supported — twenty unique qualifications, all rederive |
| Local evidence | `artifacts/terminfo/owned-qNsCmN`; digests in the summary |

## Publication layout

[`summary.json`](2026-11-08-sgr-truecolor/summary.json),
[`report.json`](2026-11-08-sgr-truecolor/report.json),
[`run-log.json`](2026-11-08-sgr-truecolor/run-log.json), one journal file per case and
[`journal-common.json`](2026-11-08-sgr-truecolor/journal-common.json); each ≤ 1,100
lines. The publication test reassembles the exact journal order and re-derives the
whole report.

## Preserved attempts

See `attempts` in the summary, all retained unmodified locally: the first
collector-cache install failed (gitignored lockfile absent; fixed from the retained
lockfile, digest verified); `owned-uTMzpm` (adapter `d0595efd`) and `owned-5ydlyP`
(adapter `3529132f`) are complete 2/2 measurements superseded because review
findings changed measured source (the SonarCloud duplication gate led to a
shared-core refactor; CodeRabbit found a receipt-admission gap); and the
Codex-review-sandbox runs (`owned-yomWuQ`, `owned-mpzO8J`, `owned-DFEk1i`,
`owned-rDKQr5`) failed before collection.

## Reproduce

```sh
node --test scripts/terminfo/sgr-truecolor-*.test.mjs
```
