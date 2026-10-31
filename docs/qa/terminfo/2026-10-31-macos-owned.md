# Owned terminfo.dev refresh and color-preference gap — 2026-10-31

A fresh uninstalled sidecar from merged main was measured, then rebuilt after
adding dark/light color-preference reports. Both runs used the same pinned
collector, private settings, ownership checks, 140-column × 41-row geometry and
fresh locked dependency installation. Neither attached to existing sessions.

| Context | Before | After |
|---|---|---|
| Source build | `954a1b7a8e7be757a1477b1f0c16c58357eee8e4` | `d19d05648308d5d5e6da159bf1cff356820e0f54` |
| App version | `0.12.0+954a1b7a8e7be757a1477b1f0c16c58357eee8e4` | `0.12.0+d19d05648308d5d5e6da159bf1cff356820e0f54` |
| Collection time (UTC) | `2026-10-31T19:06:13.732Z` | `2026-10-31T19:26:51.608Z` |
| Local evidence | `artifacts/terminfo/owned-mSQy5d` | `artifacts/terminfo/owned-3LSugv` |
| Raw SHA-256 | `9081c34579642b5dd190d349428bef97aaa0ee6e36fbbcdfd8e3e623bf740a5f` | `09e856a171220872201a708312a0df10f511263ad6ac02c6ed76871287ad4de1` |
| Assembly SHA-256 | `107a02a4855b1614534a55e0f3ff8f2452823874d5614b6d7decf33df8e3fe9c` | `79579c158e9b63ef2dd9e18bbb28e67ff4f22f61d843fdd09b82b125f9136f68` |

Platform: macOS 27.0.1. Runtime: Node v24.22.1. Collector: terminfo.dev 4.0.0,
suite/probe hash `16a690f34c0e`, collector source `cf7d8759c7e637aa8dba1e429f3dddd1c0124158`.
Adapter: `ntilde-owned-macos-v1`, derived bundle
`10f5db68f76ecdcdda27c7cd9ffc0b9b51bdbb71888d6e8fede333dea34be5ce`. This is a local collector extension,
not stock CLI data or an upstream-approved result. Supplied versions were read
from the newly built sidecar DLLs; upstream `identity` remains `unverified`.

## Outcomes

| Outcome | Original pilot (documented) | Fresh main | Feature build |
|---|---:|---:|---:|
| Supported observations | 63 | 67 | 69 |
| Unsupported observations | 11 | 8 | 7 |
| Inconclusive: insufficient evidence | 152 | 152 | 152 |
| Inconclusive: no response | 26 | 25 | 24 |
| Inconclusive: policy refused | 3 | 3 | 3 |
| Recorded observations | 255 | 255 | 255 |
| Additional ungraded diagnostic | 1 | 1 | 1 |

The first column is from the [original pilot report](2026-10-30-macos-owned.md).
It predates several merged fixes. The most recent retained pre-refresh run,
`owned-fAvkfz` on `f626397`, already had the same 67/8/180 totals as fresh main.
The two most recent cursor-blink/replay follow-ups therefore did not change
these collector outcomes; the collector does not directly test their complete
GUI/seek behavior. Counts measure observations, not independent feature totals
or a terminal compatibility percentage.

Exactly two observations changed between the fresh before/after runs:

| Feature | Before | After | Evidence scope |
|---|---|---|---|
| `modes.color-scheme-reporting` | Unsupported: DECRPM reports 2031 unrecognized (`0`) | Supported: recognized and reset (`2`) | Query only; unsolicited events were not tested by the collector |
| `device.dsr-996-color-scheme` | Inconclusive: no matching response | Supported: dark preference (`CSI ? 997 ; 1 n`) | Query only; does not prove theme-change delivery |

The other 253 observations retain their outcome/reason. The
[derived comparison JSON](2026-10-31-color-scheme-comparison.json) retains both
sets of counts, raw hashes, collector/adapter identity and the two exact query
traces. Original raw JSON, dependency receipts, archived collector/runner sources
and ownership/cleanup proofs remain in the complete Git-ignored evidence
directories; the derived summary does not replace or modify them.

## Selected gap and scope

Color-preference reporting was a bounded, explicitly reproduced gap. The
[protocol specification](https://contour-terminal.org/vt-extensions/color-palette-update-notifications/)
allows TUIs to query dark/light preference and opt into changes. The
[refreshed emulator comparison](../../ghostty-gaps/ntilde_vs_ghostty_status.md)
separates local measurements from other emulators' documented adoption.

The implementation reports the effective terminal background, including profile
overrides and shared-session presentation. Querying does not require subscribing.
Notifications are off by default, are silent on enable, and occur only when the
background crosses the shared perceived-luma dark/light threshold. Same-class
palette edits and unrelated settings are silent. DECSTR preserves the
subscription; RIS clears it. Parser snapshots preserve it while the receiving
host retains its own colors. Shared-session replies originate only in the daemon.

`ColorSchemeReportingTests` covers replies, strict query forms, opt-in/disable,
chunk boundaries, resets, paired state, serialized recording snapshots and
legacy state. Twelve original cases failed before implementation. Executable
catalog contracts and the 69-row conformance report were updated. Pane tests
exercise profile overrides and settings changes; mux tests exercise shared
presentation, read-only attach and reattach. These tests provide notification
and lifecycle evidence beyond the collector's query observations.

## Remaining evidence gaps

Seven unsupported observations remain: three advanced Kitty keyboard flags,
OSC 66 sizing, OSC 5522 clipboard, and two color-stack probes. See the comparison
for their scope. The 152 insufficient-evidence outcomes still lack the semantic,
pixel or generated-input fixtures needed to conclude support. The 24 absent
replies remain inconclusive. The three OSC 52 checks remain policy-refused;
no clipboard fixture was added and clipboard access was disabled.

Both runs retain `suiteComplete: false`. The unchanged `cursor.reverse-wrap`
callback accesses undeclared geometry and produces an ungraded
`UndeclaredTerminalGeometry` collector diagnostic. It is not a graded Ntilde
failure and was not silently repaired in this measurement.

Both app/worker pairs exited. Their ownership proofs record successful
completion and cleanup; private app-data roots and control sockets were removed.
The installed application and user profiles were not used.
