# Owned runtime color observations

`ntilde-owned-runtime-colors-v1` runs after the five stored-state suites and the
fixed-render pixel suite in the disposable macOS runner. It uses the same
verified worker, PTY, private endpoint and pane, with Observe enabled only in
that fixture. It writes no bytes to an existing user pane.

Ten experiments cover OSC 104 (individual base/extended slots and all slots),
110, 111, 112, 117 and 119. They query OSC 4, 10, 11, 12, 17 and 19 after every
baseline, override and reset. Both orders of independent selection foreground
and background reset are measured. Queries run serially: one optional exact
`rgb:rrrr/gggg/bbbb` ST reply followed by a complete DA reply establishes each
sample. DA is an ordering barrier. An explicitly unset selection default may
have no concrete reply; it is recorded as null, never graded unsupported.
Malformed, mismatched, duplicate, late, oversized and timed-out replies are
fatal. Sixteen-bit channels must equal the captured eight-bit channels × 257.

The parent brackets each of 35 offscreen PNGs with complete cell/cursor reads,
checks the known stage plan, and retains the original bytes and draw metadata.
Selection experiments use the render-only synthetic viewport selection
`(2,4)..(4,10)`; the real UI selection is untouched. Explicit RGB glyphs on row 6
remain unselected controls. The fixture uses clock 0, scale 1, minimum contrast
1 and disabled ligatures. Font fingerprint, grid and settings stay fixed.
Runtime override flags and palette slot masks must match the input. Raw theme
sources are compared independently from derived selection paint and cursor
text colors; all derived values must return exactly to baseline on reset.

The report compares changed target pixels, coverage of the requested RGB,
unchanged control pixels, fenced replies and reset equivalence. Coupled
selection cases also require the other override to survive the first reset.
A failed positive control is inconclusive; a qualified mismatch fails the local
comparison. Neither changes a stock collector grade. No live desktop cadence,
interactive selection behavior, fallback equivalence or cross-platform runtime
claim follows from these offscreen captures.

Runtime colors survive alternate-screen exit and DECSTR. The fixture therefore
requires a pristine private baseline before writing, then explicitly clears
OSC 104/110/111/112/117/119 in its finally block before exiting the alternate
screen. Original complete state, whole theme and PNG hash must match exactly.
If query input becomes fatal, a separately bounded best-effort write still emits
the color resets and alternate-screen exit. It accepts no reply, cannot revive
the fence, and cannot qualify a finish snapshot or completion receipt. The
failed run then requires termination of the disposable app and retains failure
evidence. A write error or timeout is fatal too. A malformed or missing capture
fact fails before fixture writes; only an initial typed `screenDetailsUnavailable` can produce an unavailable marker.

Evidence consists of `color-snapshots.jsonl`, `color-observations.json` and
`color-pixels/frame-NNN.png`. The runner archives query, scene, observer and
report sources. Receipt v3 retains all preceding bindings and adds color
evidence, rehashes PNGs, re-derives the report and compares its journal only
after verified worker exit, app shutdown and fixture deletion. Historical
receipt v1/v2 and fixed-render reports remain reproducible.
