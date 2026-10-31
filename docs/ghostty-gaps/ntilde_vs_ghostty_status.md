# Ntilde compatibility comparison — 2026-10-31

This refresh focuses on the gaps observed by the owned terminfo.dev collector.
The [new baseline and before/after evidence](../qa/terminfo/2026-10-31-macos-owned.md)
identify the measured Ntilde binaries. The [VT matrix](../vt_coverage_matrix.md)
remains the implementation and test reference. The older April comparison is
superseded; it was not a controlled measurement of other terminal emulators.

## Color preference reporting

| Feature | Ntilde on main before this change | Ntilde implementation in this change | Ghostty | Kitty | Contour |
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
Palette edits with unchanged classification and unrelated settings are silent.
The explicit query works without subscribing. The [matrix](../vt_coverage_matrix.md)
records reset, snapshot and shared-session scope.

## Remaining observations on the pre-change main build

| Probe family | Observations | Measured result | What remains |
|---|---:|---|---|
| Kitty alternate keys / all keys / associated text | 3 | Requested flags are masked out | Extend keyboard encoding and routing, including text/IME/layout interactions; the [Kitty protocol](https://sw.kovidgoyal.net/kitty/keyboard-protocol/) defines flags 4, 8 and 16 |
| OSC 66 text sizing | 1 | Unsupported by the cursor-advance probe | Grid geometry, rendering and evidence for the [text-sizing protocol](https://sw.kovidgoyal.net/kitty/text-sizing-protocol/) |
| OSC 5522 clipboard | 1 | Mode explicitly unrecognized | Separate clipboard policy and fixture work |
| OSC 30001 / 30101 color stacks | 2 | Foreground change or stack restoration was not observed | Mutable terminal colors and bounded stack behavior |
| Color-scheme reporting | 1 | Mode 2031 explicitly unrecognized | Addressed by this change; see live before/after report |

The eight unsupported observations are not eight independent feature requests.
The 180 inconclusive observations include missing pixel/input evidence, absent
replies and deliberate clipboard-fixture refusal. In particular, cursor movement
does not prove text styling, and a keyboard negotiation reply does not prove
press/repeat/release delivery. No overall terminal ranking follows from these
counts. The collector still has one ungraded geometry diagnostic and marks the
suite incomplete.
