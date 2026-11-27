# Rich TUI tertiary device attributes and terminal version

One disposable direct-local Ntilde pane on macOS answered both canonical queries and their equivalent spellings. Each historical stock grade is supported. [Frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json)

| Case or control | Request | Complete child PTY reply | Pinned terminal / termless | Exact reply |
| --- | --- | --- | --- | --- |
| primary | `CSI c` | `CSI ?62;4;22c` | Control | passed |
| secondary | `CSI >c` | `CSI >1;10;0c` | Control | passed |
| `device.tertiary-da` | `CSI =c` | `DCS !\|00000000 ST` | supported / supported | passed |
| da3-zero | `CSI =0c` | `DCS !\|00000000 ST` | Control | passed |
| da3-nonzero | `CSI =1c` | Silence | Control | passed |
| `device.xtversion` | `CSI >0q` | `DCS >\|Ntilde 0.14.0 ST` | supported / supported | passed |
| version-default | `CSI >q` | `DCS >\|Ntilde 0.14.0 ST` | Control | passed |
| version-nonzero | `CSI >1q` | Silence | Control | passed |

The pinned terminfo.dev 4.0.0 queries accept a complete DCS with eight hexadecimal digits for DA3 and a complete DCS with printable text for XTVERSION. Their terminal callbacks were replayed with the retained DA1 sentinel. Their termless callbacks received the isolated case replies. Each child PTY request, including the silence controls, had an independent two-nonce DECRPM ordering fence.

The exact DA3 oracle was the documented zero site/serial identifier `00000000`, which is unassigned and nonunique. The XTVERSION oracle came from `System.Reflection.AssemblyInformationalVersionAttribute` in the sidecar `Ntilde.VT.dll`. A .NET metadata reader selected the attribute directly from the assembly before App launch. Offline derivation verifies the selected file bytes and independently decodes the value, then takes the part before the first `+`. The complete value was `0.14.0+b267b01a054fc9bae4b6b4153f28fcec7a822a34` and the VT assembly SHA-256 was `963fd2fa4cf988d9bf912b4dc25bc908360fbfc93ff8a9291789217c592a4cd6`.

The App observer saw red underlined marker cells, a displaced cursor, and the prepared alternate-screen, origin and wrap state. The deliberate DECSCUSR change from steady block to steady beam changed the stored cursor style alone. The query stages retained the same cell details, cursor position, visibility, style, and buffer state. Separate fenced DECRPM reads confirmed application cursor keys, origin, wrap, alternate screen, bracketed paste and focus reporting before and after the identity queries. The initial pane snapshot and exact kernel PTY mode were restored. The runner removed the disposable fixture and verified the desktop lease release.

## Reproduce

Run `8265cdb79436de29751b860abf0b266c` used macOS 27.0.1 (26A434), arm64, App revision `b267b01a054fc9bae4b6b4153f28fcec7a822a34` and collector revision `6ff8c1d9e6d4e90ed4b09053c7583c8fec3f6891`. The [owned archive](owned-run.zip) retains the original requests, reply and fence bytes, app and worker identity, snapshots, VT assembly, and cleanup records. The [machine-readable report](report.json) records both verdicts.

Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24+ and that SDK on PATH.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+b267b01a054fc9bae4b6b4153f28fcec7a822a34 --scope device-identity
~~~

At the publication revision, rederive the archived evidence and verify the frozen map.

~~~sh
node scripts/terminfo/device-identity-publication.mjs --check
python3 scripts/terminfo-rich-baseline.py --check
~~~
