# Kitty alternate keys — 2026-11-01

Fresh owned macOS collections compare the starting main commit with the feature
commit, using private 140-column × 41-row fixtures and separate uninstalled
sidecars. Versions came from generated assembly metadata; retained assembly
hashes bind the selected binaries. Original raw JSON and source/receipt archives
remain local and Git ignored. The [derived comparison](2026-11-01-alternate-keys-comparison.json)
retains the changed trace and input pins. Raw identity remains `unverified`.

| Evidence | Before | After |
|---|---|---|
| Source | `0b405ef95d128574485be944df335ac029ab4f7c` | `1ba8a4fad3064eb7f5cee214a217f10496423816` |
| App version | `0.12.0+0b405ef95d128574485be944df335ac029ab4f7c` | `0.12.0+1ba8a4fad3064eb7f5cee214a217f10496423816` |
| Collected at UTC | `2026-11-01T09:02:25.871Z` | `2026-11-01T09:22:45.057Z` |
| Retained directory | `artifacts/terminfo/owned-F71cxG` | `artifacts/terminfo/owned-hJtZUh` |
| Raw SHA-256 | `cab94b6395c2cf9080f3b1dbc1e626c9ba93f59975955a0862d85d3286f34ae6` | `4bc068a905361624da88c33daa0208261394c5481421934f714a0e8376f25ac3` |
| Assembly SHA-256 | `7cf2de52f68ea9246b90f8c7fc9e4a7a92a5e64673256dd98310ca5d613a70e2` | `8f2c1004a16324b8ba7313e47660766151fddf9a1f1234b152efc58ecece7760` |
| Supported observations | 73 | 74 |
| Unsupported observations | 5 | 4 |
| Inconclusive observations | 177 | 177 |

Both runs use terminfo.dev 4.0.0, Node v24.22.1, suite/probe
`16a690f34c0e`, collector source `cf7d8759c7e637aa8dba1e429f3dddd1c0124158`,
and adapter `ntilde-owned-macos-v1`. Both retain derived bundle SHA-256
`51e8a80eb47f414160f1a09c8f195810194f1212d4078e1312964c0746ff88c5` and upstream SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`. The derived digest includes this worktree's
ownership-module path; the source and dependency pins are the same in both runs.

The initial feature collection at `ba425a268eaa0e22f04959421e09eb802fc06aff`
remains in `artifacts/terminfo/owned-oAWVP4` and the comparison JSON as
`intermediateAfter`. The table uses the fresh collection after review fixes; its
255 outcome/reason pairs match that intermediate collection.

## Observed change

Exactly one observation changed outcome; the other 254 retain their outcome and
reason. The alternate-key probe pushes `CSI > 5 u` (disambiguation plus alternates),
queries `CSI ? u`, then pops. Before, it replies `CSI ? 1 u`; after, `CSI ? 5 u`.
This establishes negotiation only. The collector did not generate actual key
press/repeat/release events and does not establish keyboard layout fidelity.

## Implementation and separate event evidence

[Kitty's keyboard protocol](https://sw.kovidgoyal.net/kitty/keyboard-protocol/#report-alternate-keys)
defines flag 4 as an enrichment of keys already encoded by another enhancement.
Ntilde now negotiates flags 1, 2 and 4; flags 8 and 16 remain masked. Flag 4 alone
preserves legacy recovery and control bytes. Plain text, IME and AltGr remain on
TextInput. Available scalar Avalonia symbols supply primary/shifted glyphs and
physical positions supply PC-101 base-layout values. Shifted fields occur only
with Shift; absent/equal optional fields are omitted. Alt-produced symbols are
excluded from primary derivation. Press identity survives repeats/releases and
later layout changes, with existing context/generation invalidation.

Routed-event tests supply US, French, German, Cyrillic, Swedish and supplementary
Unicode metadata; they assert exact frames, missing/malformed symbols, composition,
AltGr, Alt-produced symbols and unmodified behavior without flag 4. A regression
keeps alternate identity across legacy Alt presses and negotiated repeat/release.
Combining marks and format characters retain the known primary; four new cases
fail against the old scalar filter and pass with the conservative fallback. Disabling
alternate formatting makes seven targeted cases fail. Two VT negotiation tests
also failed before implementation. These tests use supplied framework metadata;
they do not certify native keyboard layouts or physical-device events.

A real GUI/mux-client path checks exact bytes reaching the headless child, then
checks downgrade while an older peer is attached and restoration after detach.
The Platform SSH tests verify those protocol frames at the real managed session's
UTF-8 write boundary using fake native interop; no live SSH server was used for
that specific keyboard check. Normal CI still exercises native SSH end-to-end.

Full local App main: 7,304 passed / 51 expected skips; separate PlatformBoot:
75 passed / 4 expected font skips. Full VT: 1,315 passed; mux: 1,113 passed /
20 expected platform skips; architecture: 177 passed. Focused native SSH session
suite: 38 passed. After review fixes, the isolated keyboard/mux selection passes
216 tests. A preceding selection accidentally mixed four PlatformBoot cases
into that process and hit a bitmap-retirement assertion; that mixed run is not
used as lane evidence. The corrected main selection and separate boot run pass.
The 74-row conformance report has zero errors/warnings;
scoped formatting and whitespace checks pass. Required latest-head CI and actual
reviews are checked before merge.

## Limits and cleanup

Avalonia's [key event contract](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Base/Input/KeyEventArgs.cs)
exposes current symbols and physical positions, but not every unshifted layout
glyph. Missing, control, combining, format, multi-scalar or malformed symbols keep the legacy primary.
Shifted punctuation also keeps that primary. There is no native layout-map query
or complete layout normalization; unavailable optional values are omitted.

The four unsupported observations are all-key reporting, associated text, OSC 66
text sizing and OSC 5522 clipboard. Inconclusive reasons remain 152 insufficient
evidence, 22 no response and three policy refusals. Both runs retain the same
ungraded geometry diagnostic and `suiteComplete: false`. No clipboard or pixel
fixture was added and no other terminal binary was measured.

One initial baseline launch failed before probes with native RenderTimer error
`-6661`; its evidence remains in `artifacts/terminfo/owned-fu9olt`, with completed
cleanup and no probe results. Display wake and a fresh run succeeded. Both measured
app/worker pairs also completed cleanup: `2026-11-01T09:02:25.906Z` and
`2026-11-01T09:22:45.086Z`. Raw evidence was not edited and nothing was submitted upstream.
