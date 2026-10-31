# Ntilde compatibility comparison — 2026-10-31

This refresh focuses on the gaps observed by the owned terminfo.dev collector.
The [latest runtime-color before/after evidence](../qa/terminfo/2026-10-31-color-stack.md)
identifies the measured Ntilde binaries. The [prior color-preference report](../qa/terminfo/2026-10-31-macos-owned.md)
remains available. The [VT matrix](../vt_coverage_matrix.md)
remains the implementation and test reference. The older April comparison is
superseded; it was not a controlled measurement of other terminal emulators.

## Color preference reporting

| Feature | Ntilde before PR #669 | Ntilde after PR #669 | Ghostty | Kitty | Contour |
|---|---|---|---|---|---|
| Dark/light query (`CSI ? 996 n`) | No matching response | Replies with dark/light from the effective terminal background | Documented adoption | Documented adoption | Reference implementation |
| Opt-in updates (DEC mode 2031) | Explicitly unrecognized | Queryable subscription; reports dark/light background transitions | Documented adoption | Documented adoption | Reference implementation |

Other-terminal entries are adoption statements in the protocol maintainer's
[current specification](https://contour-terminal.org/vt-extensions/color-palette-update-notifications/),
read 2026-10-31. Those binaries were not measured in this refresh. The specification
lists Ghostty since 1.0.0, Kitty since 0.38.1 and Contour since 0.4.0. This table
compares the protocol surface, not total compatibility scores.

Ntilde reports its effective profile/shared-session background preference rather
than raw OS appearance. Updates are off by default, enabling does not send an
initial report, and only a change between dark and light sends a notification.
Palette edits with unchanged classification and unrelated settings are silent. Runtime
background overrides now take precedence over host defaults for this report.
The explicit query works without subscribing. The [matrix](../vt_coverage_matrix.md)
records reset, snapshot and shared-session scope.

## Runtime colors and save/restore

| Feature | Previous Ntilde baseline | Ntilde runtime-color implementation | Verification |
|---|---|---|---|
| Foreground/background mutation | Queries only | Numeric set/query, reset and host-theme separation | Domain tests, profile updates, foreground collector mutation |
| ANSI palette and cursor query | No matching replies | Replies from effective colors | Fresh OSC 4 and OSC 12 collector queries |
| Palette changes | No runtime palette | All 256 indexed colors; existing truecolor cells stay literal | Every-slot tests and live cached-render pixels for index 200 |
| Color-stack push/pop | Two unsupported observations | Newest 16 frames; colors and palette survive attach/replay | Fresh foreground save/change/restore traces, state and live rendering tests |
| X11 names / OSC 21 | Outside implemented scope | Remain outside implemented scope | The VT matrix records numeric-only support |

The [Kitty color-control specification](https://sw.kovidgoyal.net/kitty/color-stack/)
defines the stack protocol; [xterm's control-sequence reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
defines the OSC color controls. This table reports Ntilde evidence; no other
terminal binary was measured in the follow-up.

## Remaining observations after the runtime-color follow-up

| Probe family | Observations | Measured result | What remains |
|---|---:|---|---|
| Kitty alternate keys / all keys / associated text | 3 | Requested flags are masked out | Extend keyboard encoding and routing, including text/IME/layout interactions; the [Kitty protocol](https://sw.kovidgoyal.net/kitty/keyboard-protocol/) defines flags 4, 8 and 16 |
| OSC 66 text sizing | 1 | Unsupported by the cursor-advance probe | Grid geometry, rendering and evidence for the [text-sizing protocol](https://sw.kovidgoyal.net/kitty/text-sizing-protocol/) |
| OSC 5522 clipboard | 1 | Mode explicitly unrecognized | Separate clipboard policy and fixture work |

The five unsupported observations are not five independent feature requests.
The 177 inconclusive observations include missing pixel/input evidence, absent
replies and deliberate clipboard-fixture refusal. In particular, a palette query
does not prove pixels, and a keyboard negotiation reply does not prove
press/repeat/release delivery. No overall terminal ranking follows from these
counts. The collector still has one ungraded geometry diagnostic and marks the
suite incomplete.
