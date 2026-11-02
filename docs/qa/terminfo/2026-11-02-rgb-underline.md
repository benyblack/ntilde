# Owned stored RGB and underline metadata — 2026-11-02

All **20 supplemental RGB/underline experiments passed** in a disposable macOS
instance, alongside all eight ASCII and seventeen SGR experiments. The stock
collector remains **78 supported / 0 unsupported / 177 inconclusive**, with
`suiteComplete=false` and the original ungraded reverse-wrap diagnostic.

The [machine-readable evidence](2026-11-02-rgb-underline.json) retains all 48
RGB/underline frames, complete row-major cell-detail runs, expected states,
assertions, source/input hashes and the final shared cleanup receipt. Companion
ASCII/SGR reports and original context, raw and ownership files remain local;
recorded hashes support checking those originals.

| Measurement | Value |
|---|---|
| App build source | `98efa35d87d9c53d8af7d3bb6c38a8814750a010` |
| Operator-supplied version | `0.13.0+98efa35d87d9c53d8af7d3bb6c38a8814750a010` |
| App assembly SHA-256 | `a932cbcf2043f0b68dcbe9e2e62d5eace269c1c5d510b694df60a19fe5e4ab54` |
| Runtime / collector | Node `v24.22.1` / pinned `terminfo.dev@4.0.0` |
| Local evidence | `artifacts/terminfo/owned-fFcZhe` |
| Started / cleanup completed | `2026-11-02T17:37:37.780Z` / `2026-11-02T17:37:47.369Z` |
| Viewport | 41 rows × 140 columns |
| RGB/underline report SHA-256 | `c13767bee0538f54aa9564a054e6e6baf43be528c1cc1d46e4a40c5cf3e19ceb` |
| Stock raw SHA-256 | `9e05f1575062a335ad6e1b5641c6813fdf81b115a8c05809875d808d42bf74c5` |

The operator supplied the version from local build metadata. The runner does
not independently read or verify the assembly informational version. Raw
collector identity remains `unverified`; exact executable and process/parent/PTY
ownership are checked separately. Observe access is enabled only in the private
fixture; Act remains off.

## Observed state

Each experiment establishes a blank default-attribute control with `KEEP!` on
the next row. Every target checks text, cursor and stored details across the
whole viewport. DA replies provide ordering only; parent-owned `readScreen`
snapshots determine assertions. Bad controls remain inconclusive and qualified
mismatches fail local assertions without regrading stock probes.

| Experiments | Observed result |
|---|---|
| Foreground and background RGB | Semicolon, colon and colon with an omitted colorspace store exact ARGB for (1,128,255), (254,2,0), black and white |
| SGR 39 / 49 | Only the chosen channel returns to its default marker; other literal color, underline metadata and preceding cell survive |
| SGR 4:1 through 4:5 | Written cell records single, double, curly, dotted and dashed styles respectively |
| SGR 21 | Written cell records double underline |
| SGR 58 | Semicolon/colon RGB and indexed color 123 record independent underline colors |
| SGR 59 | Underline color returns to inherited text color while its style, text colors and preceding cell survive |
| SGR 24 | Underline is disabled for the new cell, preserving both text colors and preceding cell |
| SGR 0 | The tested text colors and underline metadata return to defaults for the new cell; preceding cell survives |

The opt-in API returns unsigned packed ARGB values, with alpha in the high byte,
only for literal colors. Default/palette flags and indices remain distinct;
these paths omit literal ARGB rather than resolving a theme. Underline style 0
means disabled and 1–5 select the styles above. Index -1 with no underline ARGB
means inherited text color. The transient Dirty flag is excluded. Runs include
physical continuation cells, although these owned runtime experiments use only
ASCII. Separate API tests cover wide continuation positions.

The App captures details, text, geometry and cursor state under one buffer read
lock. Requests above 8,192 cells or 1,024 runs fail explicitly; default reads
keep their existing projection. The new observer requires the preceding SGR
suite's pane, a fresh nonce, coherent identity/geometry around every capture,
and identical initial/restored main-screen state including all requested details.
A single receipt finalizes all three reports after verified worker exit, app
shutdown, socket shutdown and private fixture deletion.

## Verification and limits

The local Node suite passed 194 tests with zero skips on an awake desktop,
including live collection/cleanup, archived source hashes, repeated signals and
a SIGTERM-resistant worker. API tests passed 199 cases, covering default/palette
versus literal colors, all underline styles/colors, resets, windowless import,
wide continuation cells, detached snapshots and exact cell/run budget boundaries.
The separate App lanes passed 7,769 and 75 tests (56 and 4 platform-specific skips);
all 180 architecture tests passed. Final focused tests were repeated after the
projection helper was simplified. Portable decoder and experiment tests run in
CI on Windows and Linux without installing a collector or launching a desktop.

This qualifies stored buffer metadata in the tested variants. It does not
qualify rendered RGB, theme/palette resolution, inverse/faint appearance,
selection, underline pixels, glyphs, blink timing, SSH, mux runtime, clipboard
or other emulators. Windowless import is covered by API tests, not this desktop
run. No VT/parser or renderer implementation changed. These 20 local experiments
are not 20 regraded stock observations; the frozen triage remains intact.

Review follow-up: an initial `screenDetailsUnavailable` refusal now records a
separate, hashed unavailable marker and allows stock collection to continue.
A real private viewport above 8,192 cells exercised this path, retained stock
raw results and finalized cleanup without starting an RGB fixture. Refusals
after fixture entry and unrelated errors remain fatal. This does not rewrite
the qualified observation above or its recorded source hashes.
After the review fixes, all 199 Node tests passed with zero skips, and the
199 focused AgentHost tests passed again. The strengthened canonical-run test
also checks that uniform rows merge into a single cross-row run.
