# Owned charset restoration and cursor modes, revision 2 — 2026-11-02

All **16 rendition/charset and nine cursor-mode experiments passed** in a fresh
private macOS instance. The preceding ASCII 8/8, SGR 17/17 and RGB/underline
20/20 suites passed too. Stock counts remain **78 supported / 0 unsupported /
177 inconclusive**, `suiteComplete=false`, with the existing ungraded reverse-wrap
diagnostic. These local measurements do not regrade historical stock probes.

The [machine-readable evidence](2026-11-02-rendition-cursor-restoration.json) retains all 40
rendition and 22 cursor frames, complete physical-cell details, expected states,
assertions, source/input hashes and the shared final cleanup receipt. Original
raw/context/ownership files and the three companion reports stay local; their
recorded hashes support checking those originals.

| Measurement | Value |
|---|---|
| App build source | `9cbd4c3a1e62d8544fb00606df3136d2643135d9` |
| Operator-supplied version | `0.13.0+9cbd4c3a1e62d8544fb00606df3136d2643135d9` |
| App assembly SHA-256 | `0293e202edd94d857040f3fd1537da788b2ba9f0f3c3de0f275c2849c30003c6` |
| Runtime / collector | Node `v24.22.1` / pinned `terminfo.dev@4.0.0` |
| Local evidence | `artifacts/terminfo/owned-YU9rw6` |
| Started / cleanup completed | `2026-11-02T22:24:08.143Z` / `2026-11-02T22:24:16.692Z` |
| Viewport | 41 rows × 140 columns |
| Rendition report SHA-256 | `9662898a1aa75f88b837c7f1783cddeb50221e534e9dd8be31b632e03753a199` |
| Cursor report SHA-256 | `849340ab079ab13d8dcb3b8af90852587fce625c911f8957afedcd97008b1691` |
| Stock raw SHA-256 | `d43035803efaa4448fdf819add902fa70bbe2783de88a5d3df613b712646e60e` |

Version provenance is operator-supplied from local build metadata; the runner
does not read or independently verify the assembly informational version. Raw
collector identity stays `unverified`; executable and process/parent/PTY
ownership are verified separately. Only the private fixture enables Observe;
Act and clipboard writes stay off.

## Recorded experiments

| Group | Observations |
|---|---|
| Faint, hidden, strikethrough, blink | Each corresponding cell flag is set; hidden text remains recorded content |
| Selective 22/28/29/25 resets | The new `B` drops only the selected rendition; 22 also clears bold. Other flags, literal foreground/background, underline style/color and earlier `A` survive |
| SGR 0 | The new cell returns the tested flags/colors/underline metadata to defaults while earlier cells survive |
| Unicode | Exact BMP, combining, wide and supplementary content, physical continuation/extended flags and independently expected cursor widths |
| DEC charsets | Line drawing and special symbols; G0/G1 SI/SO mapping, with lowercase qx probes after G0/G1 ASCII restoration |
| Cursor visibility | DECTCEM hide/show changes the recorded visibility without changing cells, cursor position or style |
| DECSCUSR 0..6 | Exact stored block/bar/underline style and blinking/steady mode, with explicit style overriding the host-default marker |
| Invalid DECSCUSR 7 | The normalized stored mode remains unchanged |

Every control and target checks the entire viewport, including the unaffected
`KEEP!` row and blank cells. DA replies order writes and reads; they are not
feature evidence. A bad control stays inconclusive; a qualified mismatch fails
a local assertion. Each suite retains its own nonce, ordering and 64-frame bound.

`readScreen(includeCursorMode=true)` adds optional stored `style`, `blink` and
`isDefault` fields under the same buffer read lock as text, geometry, visibility
and requested cell details. Style values are 0 block, 1 bar and 2 underline.
When `isDefault` is true the presenting host may use its configured appearance;
this is not a resolved UI style or a measured blink phase. Default reads omit
this object. Windowless import is covered by API tests, not this desktop run.

The cursor suite accepts only a home-position, visible, empty default-attribute
private main screen with stored default underline/blinking modes. Controls use
explicit steady block. After the last sample, alternate-screen exit and DECSTR
restore the recorded default cursor modes without clearing main-screen text.
Initial/restored snapshots match including cursor modes. Full saved state,
scrollback, reset-family support and other unobserved modes are separate work.
The one cleanup receipt binds all five reports after verified worker/app exit,
socket shutdown and fixture removal. Initial typed projection-budget refusals
record unavailable markers and let later suites/stock continue; other or later
errors still fail closed.

## Validation and remaining scope

Before this follow-up, all 218 Node tests passed with zero skips, including actual
collection/cleanup and oversized-viewport refusal regressions; 206 AgentHost and
180 architecture tests passed. Portable tests use independent literal sequences
and state samples, reject malformed modes/evidence, detect wrong widths and
unaffected-cell changes, and require exact restoration. Published-data tests
also re-derive both retained reports and check their receipt hashes. Separate
App test lanes and current-head CI are required before merge.

This qualifies stored state and decoded characters for the tested variants.
It does not qualify painted faint/hidden/strike/blink effects, glyph appearance,
cursor pixels, blink timing, other emulators, SSH or live Windows/Linux coverage.
Overline currently has no VT state or rendering path. It remains open under
#762 for a separate implementation and observation; no fake constant or input
consumption hint stands in for evidence. VT parsing/rendering, the capability
catalog, stock collector definitions and historical triage are unchanged.

This revision adds lowercase `qx` after G0/G1 ASCII restoration. The production
receipt finalizer also rehashes every required report/marker, checks its run
binding, refuses unfinished cleanup or remaining fixture entries, and writes
the shared receipt exclusively. Fourteen portable tests exercise those checks
in CI without a desktop or a prepared collector. The five-minute collection
deadline remains a resource bound; this qualified run finished in seconds.

The complete suite passed 235 Node tests with zero skips, including both
retained measurement revisions, all portable receipt checks, real success and
oversized-budget cleanup, and repeated-signal/worker-exit regressions.
