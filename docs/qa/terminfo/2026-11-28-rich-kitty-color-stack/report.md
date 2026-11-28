# Rich TUI Kitty OSC 30001 push and OSC 30101 pop

Both terminfo.dev 4.0.0 registrations passed in one disposable macOS direct-local pane. Each ID got its own copy of the pinned foreground fixture. Both IDs use the same round-trip criterion. OSC 10 must report the requested mutation after the push and the original foreground after the pop. The pinned bundle registered the same terminal callback and identical termless callbacks for both IDs.

| Case | Pinned terminal | Pinned termless | Initial foreground | After mutation | After pop |
| --- | --- | --- | --- | --- | --- |
| `extensions.osc30001-color-stack-push` | supported | supported | `rgb:d3d3/d3d3/d3d3` | `rgb:aaaa/bbbb/cccc` | `rgb:d3d3/d3d3/d3d3` |
| `extensions.osc30101-color-stack-pop` | supported | supported | `rgb:d3d3/d3d3/d3d3` | `rgb:aaaa/bbbb/cccc` | `rgb:d3d3/d3d3/d3d3` |

The worker journaled each OSC 10 query, OSC 30001 push, exact reply-dependent OSC 10 mutation, OSC 30101 pop, and the pinned cleanup write. Each query included the terminal callback’s DA1 sentinel. Every write and query used the two-nonce DECRPM ordering fence. Offline replay executed both unchanged callbacks against the corresponding exchanges and checked every request byte. After each pinned cleanup write, OSC 110 cleared the override created by writing the original RGB back.

## Owned nested-stack experiment

The independent contract passed. The fixture set A, pushed it, set B, pushed it, and set C. The first pop restored B and the second restored A. An extra pop on the drained stack was silent and preserved A. [Kitty color-stack specification](https://sw.kovidgoyal.net/kitty/color-stack/#saving-and-restoring-colors)

| Stage | Foreground | Background | Cursor | Selection background | Selection foreground | Palette overrides | Palette entries queried |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Original | `#d3d3d3` | `#000000` | `#ffffff` | Unset | Unset | None | 256 |
| A | `#162738` | `#263748` | `#364758` | `#465768` | `#566778` | 1, 5, 200 | 5 |
| B | `#a1b2c3` | `#b1c2d3` | `#c1d2e3` | `#d1e2f3` | `#e10213` | 1, 9, 200 | 5 |
| C | `#f1d2b3` | `#e1c2a3` | `#d1b293` | `#c1a283` | `#b19273` | 1, 5, 9, 200, 255 | 5 |
| First pop → B | `#a1b2c3` | `#b1c2d3` | `#c1d2e3` | `#d1e2f3` | `#e10213` | 1, 9, 200 | 256 |
| Second pop → A | `#162738` | `#263748` | `#364758` | `#465768` | `#566778` | 1, 5, 200 | 256 |
| Empty pop | `#162738` | `#263748` | `#364758` | `#465768` | `#566778` | 1, 5, 200 | 5 |
| Cleanup | `#d3d3d3` | `#000000` | `#ffffff` | Unset | Unset | None | 256 |

OSC 10, 11, 12, 17 and 19 replies agreed with the App theme readback. The original selection channels were unset and their fenced queries were silent. OSC 4 and App readback independently agreed at all 256 indices after each nested pop and after cleanup. The fixture distinguished both values and override membership across indices 1, 5, 9, 200 and 255. The JSON report includes their values and digests of the full palettes.

Paired App observations preserved the attributed witness cells, cursor position, geometry, stored buffer state and pane metadata. DECRPM confirmed that application cursor keys and bracketed paste stayed enabled across the stack operations and were reset during cleanup. Cleanup restored the original colors, unset selections, empty override list, original screen, and kernel PTY mode. The runner removed the fixture and released the desktop lease.

This grades direct-local color state. xterm CSI color stack commands, numbered slots, other Kitty color protocols and rendered pixels are outside the experiment. Both frozen stock grades are supported. This publication follows Rich TUI’s standalone assessment convention for stock-supported cases, leaving the frozen grades and inventory admission rules intact. [Frozen Rich TUI map](../2026-11-26-rich-tui/evidence-map.json)

The measured App passed with its existing handlers. The first attempt exceeded the existing owner request size limit during the initial palette query. It produced no verdict. The runner cleaned up and released its lease. [First attempt](attempt-1.zip)

## Reproduce

Run `8cf62e1ffb3864e4fbcd6c5b53067047` used macOS 27.0.1 (26A434), arm64, App revision `9454ef76b41f2f7fb0e6a0f3447e90a9321cc358` and collector revision `2de2743e139e507ac78979b79c920a2f2383909c`. The [owned archive](owned-run.zip) contains the raw fenced exchanges, paired pane observations, source and binary identity records, and cleanup receipt. [Machine-readable report](report.json)

Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24 or later.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+9454ef76b41f2f7fb0e6a0f3447e90a9321cc358 --scope kitty-color-stack
~~~

Rederive the archived evidence and verify the frozen map from the publication revision.

~~~sh
node scripts/terminfo/kitty-color-stack-publication.mjs --check
python3 scripts/terminfo-rich-baseline.py --check
~~~
