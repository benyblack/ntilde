# Owned SGR flags and palette indices — 2026-11-02

All **17 supplemental SGR experiments passed** in a fresh disposable macOS
instance, alongside all eight ASCII experiments from the preceding slice.
The stock collector still records **78 supported / 0 unsupported / 177
inconclusive** observations, `suiteComplete=false`, and its existing ungraded
reverse-wrap geometry diagnostic. Historical grades remain unchanged.

The [machine-readable evidence](2026-11-02-sgr-indices.json) contains all 44 SGR
snapshots, complete cell flags/indices in lossless run-length form, expected
states, assertions, source/input hashes and the completed cleanup receipt.
The companion ASCII report and original context/raw/ownership files remain
local; their recorded hashes support checking those originals.

| Measurement | Value |
|---|---|
| Operator-supplied app version | `0.13.0+a4dd764ec1bc85cb358e670ef0a0a8b40f0a8da4` |
| App assembly SHA-256 | `55573ea14b9d4a0a1c3a17214ffc226b27a9d18d4406f9d2adb29b557f35b9d6` |
| Runtime / collector | Node `v24.22.1` / pinned `terminfo.dev@4.0.0` |
| Local evidence | `artifacts/terminfo/owned-jEXXfL` |
| Started / cleanup completed | `2026-11-02T16:40:02.674Z` / `2026-11-02T16:40:10.741Z` |
| Viewport | 41 rows × 140 columns |
| SGR report SHA-256 | `e06e029f51ef2092388b9065c1ccc8c623846478cb6993898e0e94a34c7cc602` |
| Stock raw SHA-256 | `957f39eeb8ae0ba7f0afd8a1ee90a5c12312037f35e93add7b6834ec2bc4932c` |

The operator supplied the version argument using local build metadata. The
runner does not read or independently verify the assembly's informational
version. Raw collector identity stays `unverified`; process/parent/PTY ownership
and the exact executable are checked separately. Only the private fixture
enables Observe access; Act access is off.

## Observed state

Every experiment establishes a blank default-attribute control with `KEEP!` on
the next row. Writes are fenced by DA replies, but actual parent-owned
`readScreen` cell snapshots determine every assertion. Both text and attributes
of all unaffected cells must remain unchanged.

| Experiments | Measured result |
|---|---|
| Bold, italic, underline-enabled, inverse | The corresponding flag is set on the written `A` |
| Selective resets 22, 23, 24, 27 | Written `B` drops only the selected flag from a combined-style fixture; other flags, both palette indices and prior `A` survive |
| SGR 0 | Written `B` returns the measured style flags and foreground/background markers to defaults; prior `A` survives |
| Standard foreground/background | All eight indices 0–7 are recorded independently |
| Bright foreground/background | All eight indices 8–15 are recorded independently |
| `38;5` / `48;5` | Indices 0, 1, 15, 16, 231, 232 and 255 are sampled on each channel |
| Defaults 39 / 49 | Only the chosen channel returns to its default marker/index; unrelated styles and the other color survive |

Flags use the existing `TerminalCellFlags` projection: bold `0x0001`, inverse
`0x0002`, italic `0x0004`, underline `0x0008`, default foreground/background
`0x0100`/`0x0200`, and palette foreground/background `0x4000`/`0x8000`.
The App omits its internal Dirty bit. The parent decodes each full attribute row
and compresses adjacent identical cells across the row-major viewport. Counts
sum exactly to rows × columns; decoding preserves every observed cell.

The SGR observer must use the ASCII suite's pane ID and a new nonce. Geometry
and pane identity surround each capture. Initial and restored main-screen
snapshots match in text, cursor, visibility and all recorded attributes.
One completion receipt binds both report hashes only after worker/app exit and
private fixture deletion.

## Verification and limits

The local suite passed 153 tests with zero skips on an awake macOS desktop,
including real collection/cleanup, archived sources and receipt hashes, repeated
signals and a SIGTERM-resistant worker. New tests use independent literal VT
sequences and flag/index samples, reject malformed/incomplete runs, distinguish
bad controls from failed assertions, catch changes to unaffected cells and
unrelated styles during reset, and refuse text-only restoration. Portable tests
run in CI without a desktop or collector installation. The Release build
succeeded with three existing native Rust warning lines and no errors.

This establishes recorded ASCII cell flags and palette indices. It does not
establish RGB values, rendered colors, bold brightness, underline style/color,
glyph appearance or blink timing. SGR 0 was measured for the selected flags and
colors above; other rendition fields were not exercised. No wide/combining
text, scrollback, SSH, mux, clipboard or other emulator was measured. Viewport,
response and sample bounds remain those of the owned observer.

These are 17 local experiments, not 17 regraded stock observations. The
underline experiment records its enable flag; it does not qualify all stock
underline-style probes. No VT/parser, rendering, capability contract or App
API changed. Remaining Q02 work includes RGB/underline metadata, controlled
pixel captures and runtime-color reset observations.
