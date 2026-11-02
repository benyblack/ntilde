# Owned remaining rendition, charsets and cursor modes — 2026-11-02

All **16 rendition/charset and nine cursor-mode experiments passed** in a fresh
private macOS instance. The preceding ASCII 8/8, SGR 17/17 and RGB/underline
20/20 suites passed too. Stock counts remain **78 supported / 0 unsupported /
177 inconclusive**, `suiteComplete=false`, with the existing ungraded reverse-wrap
diagnostic. These local measurements do not regrade historical stock probes.

The [machine-readable evidence](2026-11-02-rendition-cursor.json) retains all 40
rendition and 22 cursor frames, complete physical-cell details, expected states,
assertions, source/input hashes and the shared final cleanup receipt. Original
raw/context/ownership files and the three companion reports stay local; their
recorded hashes support checking those originals.

| Measurement | Value |
|---|---|
| App build source | `d133f9ba189dc671eb8b4651f211fd756de10365` |
| Operator-supplied version | `0.13.0+d133f9ba189dc671eb8b4651f211fd756de10365` |
| App assembly SHA-256 | `5199c82408dd3b050b01e7a41464aca3df8de691af3490a9d5a6a0a1b173ebe6` |
| Runtime / collector | Node `v24.22.1` / pinned `terminfo.dev@4.0.0` |
| Local evidence | `artifacts/terminfo/owned-S9dxAp` |
| Started / cleanup completed | `2026-11-02T22:09:13.944Z` / `2026-11-02T22:09:23.995Z` |
| Viewport | 41 rows × 140 columns |
| Rendition report SHA-256 | `345582359702a6022a8ebe75f79ed4332437332b13e91b112e6b19f3ace27a26` |
| Cursor report SHA-256 | `440928a12defe8b0ce27ac93f6d179bfb46deeb6e79a0ecbb3303428f1544cae` |
| Stock raw SHA-256 | `c134eb30d4e1cdf8c65fe80253071196c61e337ed7d10aeacd97bbf725151aa8` |

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
| DEC charsets | Line drawing and special symbols; G0/G1 SI/SO mapping with ASCII designation/shift restored before subsequent fixtures |
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

Before publication, all 215 Node tests passed with zero skips, including actual
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

After adding the two published-data checks and the unaffected-visibility
regression, the complete Node suite passed 218 tests with zero skips.
The main App lane passed 7,776 tests with 56 platform-specific skips; the
separate PlatformBoot lane passed 75 with four platform-specific skips.
