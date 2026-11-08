# SGR underline-color qualification (2026-11-08)

Qualifies exactly three terminfo.dev@4.0.0 cases through independently observed stored state:
`sgr.underline-color-indexed`, `sgr.underline-color-rgb` and `sgr.underline-color-reset`.
All three are **supported**. Rendered underline appearance is **not claimed**.

## Verified pins (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`)

All three run the termless path over cells carrying `underline`, `underlineColor` (RGB, `null` = default) and `fg`.

| Case | Fixture (exact bytes, cells read) | Pinned acceptance |
|---|---|---|
| `sgr.underline-color-indexed` | `ESC[4;38;5;4m ESC[59m A ESC[58;5;5m X ESC[38;5;5m ESC[59m B ESC[58;5;4m Y` (A,X,B,Y) | X's underline color equals B's resolved foreground (palette 5) and Y's equals A's (palette 4); controls must resolve to distinct colors |
| `sgr.underline-color-rgb` | `ESC[4m ESC[59m ESC[38;2;0;0;255m A ESC[38;2;0;255;0m B ESC[58;2;255;0;128m X` (A,B,X) | X's underline color is rgb(255,0,128) and B's default underline is not that color |
| `sgr.underline-color-reset` | `ESC[4m ESC[59m C ESC[58;2;255;0;128m X ESC[59m Y` (C,X,Y) | X is rgb(255,0,128); SGR 59 returns Y to C's measured default representation |

- **Overlapping aggregate, not qualified:** `sgr.underline.color` is registered through the same `rgbUnderlineProbe`
  body (identical fixture and criterion) under a different id. It is neither graded nor substituted; the stock run
  keeps it inconclusive. `sgr.underline-color-rgb` is the case this batch qualifies.
- Qualified syntax is exactly what each fixture exercises (semicolon `58;5;n`, `58;2;r;g;b`, `59`). The colon forms
  `58:5:5`, `58:2::255:0:128` and `58:2:0:255:0:128` appear only as labeled controls and receive no grade.
- The pinned probes never return unsupported from an unexposed default; the stock run leaves all three
  inconclusive ("Cursor advance does not verify SGR styling").

## Layers kept distinct

| Layer | What is observed | Source |
|---|---|---|
| Stored | underline style, underline color as palette index, literal ARGB bytes, or the default marker | parent-journaled `readScreen` cell details |
| Resolved | the RGB the pinned cell shape reports | **derived parent-side, not reported by the App** |
| Rendered | stroke appearance | never claimed |

**Known limit (departure from "add the missing App projection").** The App's resolved-color projection covers
foreground and background only; it reports no resolved underline color. This batch did not add one (that would
change the AgentHost wire contract and every frozen resolved-color publication). The resolved underline RGB is
therefore computed by the parent: a palette index resolves through the palette in the recorded theme binding (the
draw path's `ResolveUnderlineColor` uses the same palette entry a foreground index uses), literal bytes are
themselves, and a default reports `null`. The indexed case's "resolved" verdict thus rests on the stored index plus
the App-reported palette, not on the App's drawing code, and does not prove the App paints that color. Reporting
the final underline color from the App is a separate follow-up (done in
[the App-observed underline-color resolution](2026-11-08-underline-color-resolved-qualification.md)).

**Default underline color.** A default underline is a stored marker (index -1, no literal), not a fixed RGB. It
inherits the cell's text color at draw time (the draw path keeps the run's foreground paint when no explicit color
is stored). The pinned shape therefore reports `underlineColor: null`, and the reset case compares representations:
C and Y are both default. Y keeps underline style 1, foreground and background.

## Contracts and extension identity

- Contracts `ntilde-owned-sgr-underline-color-graded-v1` v1; extension `ntilde-owned-sgr-underline-color-grading-v1`
  (ninth layer over the underline-style one; seams pin each probe body verbatim, once, plus the registrations,
  `requestedUnderlineColor`, helpers and previous exports; the aggregate registration is deliberately not a seam).
- Receipt schema 21, source policy v15 (inventory v22, 154 files). Policies v1–v14 and their publications stay frozen.
- Controls: neutral setup, unrelated attribute/overline/foreground/background drift and the exact underline style;
  DECSCNM off; literal bytes versus palette 67 with equal resolved RGB; explicit index 5 with foreground 5 versus the
  default; foreground equal to the requested color with a default underline (substitution); a neutral cell; SGR 59
  from an indexed color on a curly underline with explicit foreground/background, and from a literal color on a
  single underline with default foreground/background, preserving these attributes. The indexed case
  also mutates palette entries 4, 5 and 67 with OSC 4 and restores them with OSC 104, comparing **all 256 entries** and
  the override list at start, mutated and restored reads; stored cells must be identical across the three states and
  every palette-sourced color must follow its own read's palette.
- Grading: supported needs the exact stored fields and the pinned resolved relations; unsupported needs every control
  valid plus an observed mismatch (the layers record which of stored/resolved failed); an unprepared setup, a cleared
  underline style, indistinguishable controls or invalid mutation/restoration are inconclusive. The contract is never
  more permissive than the pinned probe; no divergence occurred.

## Measurement

Fresh committed build: scripts and App source at `be0a490e` (App source unchanged from main `5d0923ee`, so App and
adapter revisions coincide). Owned run `owned-TTpuQg`, run id `9f14001275edd10fcd83601626a2e68d`, receipt completed
2026-11-08T17:33:14Z, default theme (palette 4 = 0,0,238; palette 5 = 205,0,205).

| Case | Pinned | Contract | Layers (stored / resolved) |
|---|---|---|---|
| `sgr.underline-color-indexed` | supported | supported | supported / supported |
| `sgr.underline-color-rgb` | supported | supported | supported / supported |
| `sgr.underline-color-reset` | supported | supported | supported / not evaluated |

Observed: X stored palette 5 and Y palette 4 (no literal bytes), A and B default; RGB X stored literal `0xff0080`
with index -1; after SGR 59 Y is the default marker with style, foreground and background preserved. Under the OSC 4
mutation the stored indices did not change and X/Y/Q resolved to the mutated entries while the literal control did
not; after restoration all palette entries matched the start and the override list was empty. The colon-form
controls stored the same literal/index values (ungraded).

The same run reconfirmed the prior 26 unique qualifications and the separate 10/10 truecolor-literal finding
(its own ids, never counted as qualifications). Every report and the completed receipt rederive from retained bytes.
The stock collector is unchanged: 86 supported, 1 unsupported, 168 inconclusive.

## Preserved attempts

- `owned-0xvKXe` (scripts `145a5ce9`): a complete 3/3 run, superseded. The Codex review showed that palette stability
  was enforced only inside the indexed case; the fix (every non-mutation frame must equal the start palette) changes
  evidence validation, so the measurement was rerun. Retained unmodified locally.
- `owned-syzaXK` (scripts `8c34e4af`): a second complete 3/3 run, superseded. The CodeRabbit review fixes changed two
  inventoried scripts (a control label and symlink rejection in the receipt check), so the measured source no longer
  equalled the committed source and the measurement was rerun. Retained unmodified locally.
- `owned-ijwh1a`, `owned-ktHIu6`: the disposable app aborted in Avalonia native startup (display asleep) before any
  collection. The next attempt woke the display (`caffeinate -u`) and ran with `caffeinate -d`.

## Evidence

[summary](2026-11-08-sgr-underline-color/summary.json), [report](2026-11-08-sgr-underline-color/report.json),
[run log](2026-11-08-sgr-underline-color/run-log.json) and one journal per case under `2026-11-08-sgr-underline-color/`.
`sgr-underline-color-publication.test.mjs` reassembles and rederives them.

## Limits

Stored state and parent-derived resolution only; no App-reported or rendered underline color. Default theme only.
Only the three pinned spellings are qualified; colon forms are controls. Underline styles are outside this batch.
