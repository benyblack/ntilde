# Owned offscreen pixel observations

`ntilde-owned-render-pixels-v1` runs after the five stored-state suites in
`scripts/terminfo-owned.mjs`. It uses the same proved worker/PTY, private app-data
endpoint and singleton local pane. Observe is enabled only in the disposable
profile; Act, clipboard writes and session persistence remain disabled.

The worker enters its own alternate screen, binds `NTILDE_PX_<runId>`, writes
24 explicit scenes and obtains 40 PNG frames, including start/bind/restoration.
It never discovers or attaches to a user's existing terminal. Each write is
ordered with a complete DA reply. The parent reads complete physical cell runs
(including overline), cursor modes and text before and after every capture;
pane identity, geometry and both state hashes must agree. These reads establish
coherence and ownership; the pixel comparisons establish appearance.

Captures use only `captureScreen(mode=render, inline=true, maxWidth=0, scale=1,
includeRenderDetails=true, presentationTimeMs=...)`. The normal read-only RPC
allowlist is unchanged. The dedicated fixed capture transport is bounded at
5 MiB; PNGs are limited to 3 MiB, four million pixels and 8192 pixels per side.
The whole suite has a 64-million-decoded-pixel budget. Its static PNG subset is
8-bit RGB/RGBA, non-interlaced; CRC, chunk ordering, exact bounded inflate length
and all five row filters are verified. Unsupported forms, APNG, malformed data,
downscaling, missing inline bytes or clock/context substitutions fail the run.

The returned primary font stream SHA-256, TTC face index/style, configured and
resolved families, fallback names, size, platform/architecture, actual pixel
grid, effective 256-color palette and settings must remain stable. Missing font
fingerprints cannot qualify this suite. Ligatures are disabled, minimum contrast
is 1, and the effective background/palette is opaque. Fallback names do not
attest font bytes or per-glyph fallback choices. The fixture uses ASCII glyphs
and full-block primitives; results are local to the recorded renderer/font/OS.

| Comparison | Pixel control |
|---|---|
| 16 palette foreground/backgrounds and literal RGB | Interior colors of background patches and full blocks, against the actual palette |
| Bold, italic, faint, hidden and reverse | Same-coordinate normal glyph/blank references plus an unchanged control row |
| Strike and overline | Painted changes on both glyphs and blank cells |
| Five underline styles and independent color | Green stroke coverage on blanks, accounting for antialiasing against the known background; five distinct masks |
| Text blink at 0/529/530/1059/1060 ms | Identical input, visible-phase equality, absent glyph/decorations in the hidden phase, backgrounds and non-target row retained |
| Host-default and DECSCUSR 0–6, plus hidden cursor | Visible/off-phase masks against hidden-cursor pixels; effective blink metadata; distinct steady block/underline/beam masks |

Comparisons are `passed`, `failed` or `inconclusive` when positive controls do
not establish the precondition. A failed comparison remains evidence; it is not
silently promoted to support. These are **synthetic offscreen phases**, not
measurements of desktop blink cadence, focus, compositor timing or platform
runtime parity. Shared-renderer/cache behavior is also covered by the focused
App tests added with the blink implementation; this adapter does not benchmark
cache performance.

The run retains `pixel-snapshots.jsonl`, `pixel-observations.json` and
`pixels/frame-NNN.png`. The report contains exact scene inputs, their hashes,
all state brackets, render metadata, PNG hashes and assertions. The runner also
archives the executed adapter bundle and every relevant source/input file.
It restores the original main screen with alternate-screen exit and DECSTR,
requiring exact initial/final state **and fixed-time PNG hash** equality.
A version-2 `cell-receipt.json` is finalized only after worker/app/socket shutdown
and fixture removal; it re-derives the report, checks its journal and rehashes
all PNGs. Version-1 historical reports/receipts retain their original format.

Only a preceding initial typed `screenDetailsUnavailable` refusal may produce
`pixel-unavailable.json` with `fixtureStarted=false` and no pixel outcomes.
Other missing evidence is fatal. No fixture writes occur for that unavailable
case. A run without verified cleanup has no final receipt.

The stock results remain **78 supported / 0 unsupported / 177 inconclusive**,
`suiteComplete=false`, including the existing ungraded reverse-wrap geometry
diagnostic. Supplemental pixels never regrade the stock collector.
