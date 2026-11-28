# Rich TUI DECRQSS SGR and DSR 996 color scheme

One disposable direct-local Ntilde pane on macOS passed both exact state checks. Both historical stock grades are supported. This assessment leaves the frozen stock grades and inventory totals as published. [Frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json)

| Case | State | Request | Complete child PTY reply | Pinned terminal / termless | Exact state |
| --- | --- | --- | --- | --- | --- |
| `device.decrqss` | neutral | `DCS $qm ST` | `DCS 1$r0m ST` | supported / supported | passed |
| `device.decrqss` | styled | `DCS $qm ST` | `DCS 1$r0;1;4:3;31;48;5;200;58;2;9;8;7m ST` | supported / supported | passed |
| `device.decrqss` | reset | `DCS $qm ST` | `DCS 1$r0m ST` | supported / supported | passed |
| DECRQSS rejection | unknown | `DCS $qunknown ST` | `DCS 0$r ST` | Control | passed |
| `device.dsr-996-color-scheme` | dark `#101010` | `CSI ?996n` | `CSI ?997;1n` | supported / supported | passed |
| `device.dsr-996-color-scheme` | light `#f0f0f0` | `CSI ?996n` | `CSI ?997;2n` | supported / supported | passed |
| `device.dsr-996-color-scheme` | restored `#000000` | `CSI ?996n` | `CSI ?997;1n` | supported / supported | passed |

The pinned terminfo.dev 4.0.0 DECRQSS callback accepts a complete status-1 SGR frame with digits, colons and semicolons. The DSR callback accepts either framed scheme value. Both unchanged terminal callbacks were replayed over replies with their retained DA1 sentinel. The unchanged termless callbacks received the isolated replies. Each child PTY exchange used the two-nonce DECRPM ordering fence. The state controls and exact payload checks are additional evidence.

DECRQSS was tested with the neutral pen, then bold with curly underline, ANSI red foreground, indexed background 200 and literal underline RGB `#090807`, then SGR 0 again. Paired witness cells `Aa Bb Cc` had the independently specified stored attributes before and after each query. The unknown-setting control also preserved the pen. The DECRQSS scope is limited to SGR and that rejection. [Earlier query observations](../2026-11-05-query-observations.md)

With each OSC 11 override and after OSC 111, the App color binding and default-background cells showed the expected effective background. Separate OSC 11 readbacks before and after each DSR matched that binding, and DECRPM confirmed mode 2031 reset. The color writes themselves were silent. Dark and light were fixed grayscale controls. The restored-scheme expectation used the documented perceived-luma threshold on the original observed RGB. This grades Ntilde’s effective pane background policy. OS theme switching and opt-in notifications are outside this assessment.

Paired App observations showed the query stages preserved the cells, cursor, geometry, buffer modes and effective colors. The background reset removed the override and restored the initial RGB `#000000`. Leaving the alternate screen restored the original pane snapshot. The runner verified exact kernel PTY mode restoration, fixture removal and desktop lease release. The measured App worked with its existing handlers.

## Reproduce

Run `4c57bfa9a3bd28c9dc38956f11838e08` used macOS 27.0.1 (26A434), arm64, App revision `d56bc224cfc17e286aa48d7c4f0a93d02fe6d334` and collector revision `877dc367ff02987b4d62ef0f705405661fdfc914`. The [owned archive](owned-run.zip) contains the raw exchanges, pane observations, App and collector identity records, and cleanup proof. [Machine-readable report](report.json)

Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24 or later.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+d56bc224cfc17e286aa48d7c4f0a93d02fe6d334 --scope device-state
~~~

Rederive the archived evidence and verify the frozen map from the publication revision.

~~~sh
node scripts/terminfo/device-state-publication.mjs --check
python3 scripts/terminfo-rich-baseline.py --check
~~~
