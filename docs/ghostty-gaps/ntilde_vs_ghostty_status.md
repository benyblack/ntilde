# Ntilde compatibility comparison — 2026-11-01

This refresh focuses on the gaps observed by the owned terminfo.dev collector.
The [latest alternate-key measurement](../qa/terminfo/2026-11-01-alternate-keys.md)
measures 74 supported / 4 unsupported / 177 inconclusive observations.
The [runtime-color before/after evidence](../qa/terminfo/2026-10-31-color-stack.md)
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

## Alternate keys

Flag 4 now enriches already encoded key events with available shifted and PC-101
base-layout fields. A fresh owned probe changes from requested flags 5 / reported
flags 1 to reported flags 5. This is negotiation evidence only; routed-event tests
and real GUI/mux delivery separately establish exact bytes for supplied metadata.
Missing symbols and shifted punctuation retain the existing primary-key fallback;
there is no complete native keyboard-layout qualification. See the
[report and limits](../qa/terminfo/2026-11-01-alternate-keys.md).

## Remaining observations after the alternate-key follow-up

| Probe family | Observations | Measured result | What remains |
|---|---:|---|---|
| Kitty all keys / associated text | 2 | Requested flags 8 and 16 remain masked out | Extend key/text routing while preserving IME/layout behavior; the [Kitty protocol](https://sw.kovidgoyal.net/kitty/keyboard-protocol/) defines the remaining flags |
| OSC 66 text sizing | 1 | Unsupported by the cursor-advance probe | Grid geometry, rendering and evidence for the [text-sizing protocol](https://sw.kovidgoyal.net/kitty/text-sizing-protocol/) |
| OSC 5522 clipboard | 1 | Mode explicitly unrecognized | Separate clipboard policy and fixture work |

The four unsupported observations are not four independent feature requests.
The 177 inconclusive observations include missing pixel/input evidence, absent
replies and deliberate clipboard-fixture refusal. In particular, a palette query
does not prove pixels, and a keyboard negotiation reply does not prove
press/repeat/release delivery. No overall terminal ranking follows from these
counts. The collector still has one ungraded geometry diagnostic and marks the
suite incomplete.
