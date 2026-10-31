# Runtime colors and color stacks — 2026-10-31

A fresh owned before/after collection measured the same pinned suite in private
140-column × 41-row fixtures. The pre-change sidecar is the preserved `aa640ee`
build from the previous report. Its runtime sources match merged main `e91eb816`
exactly across App, VT, Pty, Mux, Rendering and Mux.Contracts (verified with Git);
its embedded version still identifies `aa640ee`, not the later merge commit.
The feature sidecar was rebuilt from committed source `ff9abd15` in a separate
uninstalled output directory. Neither run used installed application profiles.
An initial `b40702a1` feature collection had the same 73/5/177 outcomes before
batched publication, reply hardening, cache and CLI-rendering follow-ups. Its
metadata remains in the comparison as an intermediate build; the final after
measurement identifies `ff9abd15` and includes those fixes.

| Evidence | Before | After |
|---|---|---|
| Source | `aa640ee4384b963ddd9e156b78e0f8e0d532ee0f` | `ff9abd159d1ac53cddaea17d7bcfb71fa823ef50` |
| Collected at UTC | `2026-10-31T22:11:29.621Z` | `2026-10-31T22:46:07.760Z` |
| Retained local directory | `artifacts/terminfo/owned-qmH85E` | `artifacts/terminfo/owned-WH1tzu` |
| Raw SHA-256 | `5887e6ecf3be2ded8a253d8eab9478e0b9311255ab22beacb287f8bbc168f645` | `bf51f1315aa111ded1fe905b0b825f61545b88e2d68f372b772b7a4b0c0dcfb7` |
| Assembly SHA-256 | `91ef82f942999f4acad9da89194cbc9112c99f836518ab8a9536c980003e7777` | `ea22370a497fd5f65b3918408842435b9376de9c5dd2ce46c2dfc3ba3e1c5a44` |
| Supported observations | 69 | 73 |
| Unsupported observations | 7 | 5 |
| Inconclusive observations | 179 | 177 |

Both runs use terminfo.dev 4.0.0, suite/probe `16a690f34c0e`, Node v24.22.1,
collector source `cf7d8759c7e637aa8dba1e429f3dddd1c0124158` and adapter
`ntilde-owned-macos-v1`. The derived adapter bundle hash remains
`10f5db68f76ecdcdda27c7cd9ffc0b9b51bdbb71888d6e8fede333dea34be5ce`.
The [derived comparison JSON](2026-10-31-color-stack-comparison.json) records the
same collector/source/dependency pins, the raw hashes and exact four changed traces.
Original JSON, source archives and receipts remain local and Git ignored.

## Observed changes

Exactly four observations changed; the other 251 retain their outcome and reason.

| Probe | Before | After | Evidence scope |
|---|---|---|---|
| OSC 4 palette query | No response; inconclusive | Replies for palette index 0 | Query only; does not prove palette mutation or pixels |
| OSC 12 cursor query | No response; inconclusive | Replies with the host cursor color | Query only; does not prove cursor pixels |
| OSC 30001 color-stack push | Foreground mutation/restoration not observed; unsupported | Foreground changes and pop restores its prior value | One foreground save/change/restore cycle |
| OSC 30101 color-stack pop | Foreground mutation/restoration not observed; unsupported | Foreground changes and pop restores its prior value | Same behavior family, not a second independent implementation |

For each stack probe, foreground replies change from `rgb:d3d3/d3d3/d3d3` to
`rgb:aaaa/bbbb/cccc`, then return to `rgb:d3d3/d3d3/d3d3` after pop. The collector
restores its initial foreground afterward. Independent tests establish the broader
state, shared-session and rendering claims below.

## Implemented scope and verification

The [Kitty color-stack specification](https://sw.kovidgoyal.net/kitty/color-stack/)
and [xterm OSC reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
are the protocol references. The implementation adds numeric OSC 4 palette set/query, OSC 10/11/12/17/19 dynamic
colors, OSC 104 and 110/111/112/117/119 resets, and OSC 30001/30101 save/restore.
All 256 palette slots and foreground/background/cursor/selection overrides are
session state, separate from saved host themes. The newest 16 stack frames are
retained; an empty pop is silent. Unset entries follow the current receiving host
theme even after a host theme change while a frame is saved. DECSTR preserves
colors; RIS clears overrides and stack. Dark/light reporting uses the resulting
effective background.

Supported values are #RGB, #RRGGBB, #RRRGGGBBB, #RRRRGGGGBBBB, rgb: with 1–4 hex
digits/channel, and finite rgbi: intensities in [0,1]. Values resolve to Ntilde's
8-bit channels. X11 named colors, other dynamic slots and OSC 21 remain outside
this change. Color requests are bounded before splitting; malformed values leave
prior state intact and replies contain only validated numeric colors/indices.

The original regression set failed 11 cases before implementation while six
malformed-input guards passed. Final local validation: VT 1,189 passed, mux
1,076 passed / 20 expected platform skips, MCP 223 passed, architecture 177 passed,
the complete App main lane after local reply hardening passed 7,133 / 51 expected
skips. Seven focused queue/pane/allocation tests passed and the separate
PlatformBoot lane passed 75 / 4 expected font skips. Latest-head full
App lanes are also required in CI before merge. The focused App/profile/factory group passed 41 cases.
The 74-row conformance report has zero errors or warnings; scoped formatting and
whitespace checks pass.

Domain tests cover every palette slot, nesting, stack overflow/underflow, resets,
input splits and terminators, background notifications, deep copies, legacy state
and malformed snapshot rejection before resize. Serialized replay tests resume a
partial SGR and restore palette/stack state. Mux tests exercise startup JSON-wire
colors and attach continuation; local/remote factory tests verify forwarding.
Real profile updates preserve overrides and pop returns to the current host theme.
The CLI text client resolves overridden default/indexed cells and underline colors
into SGR without forwarding palette OSC. A round-trip regression verifies changed
colors, stable frames, untouched outer palette/default semantics, and restoration.
Two near-limit palette regressions fail against the reviewed pre-batching source;
the fixed implementation stays below a 4 MiB allocation ceiling and publishes once
per OSC while retaining ordered query replies. Internally shared saved frames are
immutable; snapshot import/export still deep-copy them. Two additional regressions cover
in-place host-theme edits after cache warmup, with and without application overrides;
the existing cursor-render Boot regression also passes after the cache fix.

Four live Skia cases reuse row caches: palette index 200, default colors and
cursor color visibly change, unchanged frames remain stable, and pop restores
identical pixels. The foreground-only case keeps the canvas background unchanged,
so it specifically verifies text foreground rendering. Existing truecolor cells
keep their literal values.

Palette queries send at most 256 replies per OSC, while subsequent mutations still
apply. A near-limit query regression verifies the cap. The local PTY and OpenSSH
wrapper reply path uses a nonblocking best-effort queue operation, reserving one
quarter of its 1,024 slots from reply floods. Deterministic tests fill it without a
consumer, verify keyboard room and drain/recovery, and verify pane routing never
falls back to blocking input after a rejected device reply. User-input
backpressure remains unchanged.

Native SSH uses a separate best-effort reply command with a 64 KiB admission
budget, including a 256-byte surcharge per command. Its reservation stays live
through a stalled channel write and releases on completion, failure or teardown.
Older native binaries decline the new entry point without using the unbounded
keyboard path. All 180 native Rust library tests and 39 focused managed SSH tests pass,
including saturation, held reservations, empty packets, receiver teardown and
keyboard independence. The full managed Platform suite passes 711 tests after merging the recording-privacy changes from main, with 43
expected skips. Both standalone and shared-shell regressions exhaust a real SSH
server receive window, close without replenishing it, and verify bounded shutdown
and reservation release. Disabling write cancellation makes both regressions fail.
Post-loop network cleanup is bounded for standalone and shared connections. A
virtual-time regression saturates a ten-slot protocol queue and verifies the
shared-shell finish sweep and reservation release despite a stuck disconnect;
disabling the cleanup timeout makes that regression fail.
A third real-window case closes the shared connection with its shell still stalled.
EOF and resize operations share the same close-cancellable operation helper as
data writes. A saturated-queue regression checks cancellation, success and error
propagation. Managed interop tests cover rejected handles/payloads and call the
real export with a native session paused during SSH establishment: the first
32 KiB reply is admitted and a second is rejected by the native budget. Rebinding
to the ordinary-input export makes that test fail. Pane routing asserts the exact
OSC reply bytes. Both ownership documents explicitly permit only the two
immutable, volatile-published scalar color getters to avoid taking the buffer lock.
The new raw-pointer FFI documents and declares its Rust safety boundary; the
pre-existing Clippy raw-pointer baseline remains at 14 rather than growing. The final direct-desktop collector artifact remains the
ff9abd15 build: it does not exercise native SSH or the CLI writer. The subsequent
SSH hardening and ANSI-writer maintainability cleanup are covered separately by
these tests and the full VT/mux suites; the collector evidence is not relabeled.

## Limits and cleanup

The remaining five unsupported observations are three advanced Kitty keyboard
flags, OSC 66 text sizing, and OSC 5522 clipboard. They are not five independent
feature projects. Of the 177 inconclusive observations, 152 lack semantic/input/
pixel evidence, 22 have no matching response, and three OSC 52 checks were refused
by the unchanged clipboard policy. No clipboard fixture was added.

Both runs retain `suiteComplete: false`: the unchanged `cursor.reverse-wrap`
callback accesses undeclared geometry and produces an ungraded collector diagnostic.
These measurements are a local adapter extension, not an upstream-approved score
or an overall terminal ranking; upstream identity remains unverified. Other
terminal binaries were not measured. See the [comparison](../../ghostty-gaps/ntilde_vs_ghostty_status.md).

Both app/worker pairs exited and cleanup completed. Their proofs record cleanup
at `2026-10-31T22:11:29.675Z` and `2026-10-31T22:46:07.786Z`;
private app-data roots and sockets were removed. The prior baseline report remains
available in the [macOS owned baseline report](2026-10-31-macos-owned.md).
