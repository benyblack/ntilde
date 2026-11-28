# Rich TUI Kitty keyboard negotiation and graphics query

| Exact terminfo.dev 4.0.0 case | Historical stock | Terminal replay | Termless replay | Owned scope |
| --- | --- | --- | --- | --- |
| `extensions.kitty-keyboard` | supported | supported | supported | Keyboard enhancement bit 1 negotiation and flag restoration (supported) |
| `extensions.kitty-graphics` | supported | supported | supported | Acceptance of the direct RGB query (supported) |

One disposable direct-local App pane with session persistence off carried both fixtures on its prepared alternate screen. The archive retains screen and placement state, two App offscreen PNGs per stage, exact actions and child PTY bytes, and two fresh DECRPM challenges per write. The pinned primary DA sentinel was `CSI c` and replied `CSI ?62;4;22c` after the tested reply. Both pinned callbacks consume the matching exchange, with the sentinel included only where the pinned path uses it.

Keyboard detection returned `CSI ?0u`. The exact `CSI >1u` push returned no bytes before its fence. The next `CSI ?u` returned `CSI ?1u`. The exact `CSI <u` pop was silent and a separate post-pop query returned `CSI ?0u`, the original flags.

The graphics request was `APC Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA ST`. It produced the complete `APC Gi=31;OK ST` reply (hex `1b5f47693d33313b4f4b1b5c`). Placement by ID 31 returned `ENOENT:Image not found` before and after the query. The query left 0 placements and identical pane PNGs. An explicit red upload under ID 31 then acknowledged successfully, placement painted the red square, and hard deletion restored the blank bound frame and the same missing-image reply.

## Separately measured capabilities

The five routed-input effects were supported for the tested keys and layout on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop), App `caea956de1f8b391892f61f89ba6fbf6c462cc77`, collector `fc73fe7ae8ab701f36aff06a8f1fdd9a8907eec5`. The child received the expected disambiguation, press/repeat/release, alternate-key, all-key and associated-text reports. [Five-flag XTEST assessment](../2026-11-26-rich-tui/kitty-report.md)

Transmit and display were supported by both pinned replies and their separately observed effects on macOS 27.0.1 (26A434), arm64, App `eb0abcdae2da73b55a6a2905be11acd316d66121`, collector `dae4f1f1763a7a324eac00f8d891cdd4d2b6c199`. The upload left the pane unchanged, and the separate display fixture painted a 1 × 1 RGB source into the requested cell envelope. [Transmit and display assessment](../2026-11-26-rich-kitty-graphics/report.md)

The retained animation and Unicode-placeholder publications grade each listed effect supported. Both pinned callbacks in those publications return inconclusive. Each row names its measured App and collector revisions and its rendering scope.

| Owned render observation | Platform | App revision | Collector revision | Evidence |
| --- | --- | --- | --- | --- |
| Ordinary-tab timed playback, loop, stop and PNG frame blending | macOS | `cf59628f0ef73dd0a99dba0952acb4b6552678a4` | `cf59628f0ef73dd0a99dba0952acb4b6552678a4` | [Publication](../2026-11-16-kitty-animation-qualification.md) |
| Live persistent-tab timed playback, loop, stop and PNG frame blending | macOS | `33394ffaef9bd7dd29852a420974e8dd17f26cdd` | `33394ffaef9bd7dd29852a420974e8dd17f26cdd` | [Publication](../2026-11-17-kitty-animation-persistent-qualification.md) |
| Persistent-tab App reattach after SIGTERM, including playback restoration | macOS | `601cd0514a515383b33a27435d5dcb2f3183a9fd` | `69c4571e91e628184ffa738d91612439f2fd2023` | [Publication](../2026-11-17-kitty-animation-restore-qualification.md) |
| Ordinary-tab whole-frame replacement, blending and rejection | macOS | `91b111e03dfbfba2a24bfcac85f6e05ede291182` | `a3404c55d55e67955ca212cd676ead81f7fe14fd` | [Publication](../2026-11-18-kitty-animation-compose-direct-qualification.md) |
| Persistent-tab whole-frame replacement, blending and rejection | macOS | `91b111e03dfbfba2a24bfcac85f6e05ede291182` | `a3404c55d55e67955ca212cd676ead81f7fe14fd` | [Publication](../2026-11-18-kitty-animation-compose-qualification.md) |
| Ordinary-tab two-cell tiles, erasure and mark inheritance | macOS | `8a2eb2952a7b965c101580093607d3f2681b7932` | `c46e42c786fc0646443b6d913771c86050b49da5` | [Publication](../2026-11-19-kitty-placeholders-qualification.md) |
| Persistent-tab two-cell tiles, erasure and mark inheritance | macOS | `8a2eb2952a7b965c101580093607d3f2681b7932` | `c46e42c786fc0646443b6d913771c86050b49da5` | [Publication](../2026-11-19-kitty-placeholders-qualification.md) |
| Ordinary-tab 4 × 2 grid, nonzero row inheritance and tile movement with text | macOS | `b5e6cec9fde963cceffb56e2498c13b50af570d7` | `dc45f4cd241968a003919410899d3923769a6f2d` | [Publication](../2026-11-19-kitty-placeholder-grid-qualification.md) |
| Persistent-tab 4 × 2 grid, nonzero row inheritance and tile movement with text | macOS | `b5e6cec9fde963cceffb56e2498c13b50af570d7` | `dc45f4cd241968a003919410899d3923769a6f2d` | [Publication](../2026-11-19-kitty-placeholder-grid-qualification.md) |

## Reproduction

Run `49ba958906332ecce520862be85d1e65` used macOS 27.0.1 (26A434), arm64, Node `v24.22.2`, a 41 × 140 grid, App `a2727571cbd07165797602a4271ef019099401eb` and collector `83c1b6e0f69a50394e53f782e71a0f16492b4462`. The initial screen and PTY mode were restored, the fixture and captured-source directory were removed, and the desktop lease was released after verification. The App build revision and the retained executable and assembly hashes are recorded separately. [Owned archive](owned-run.zip) · [Machine-readable report](report.json)

Run the offline verification from the repository root with Node 24 and Python 3.

~~~sh
node scripts/terminfo/kitty-aggregate-publication.mjs --check
~~~

For a fresh run, publish the recorded App revision to a private sidecar and run from the recorded collector revision. The launcher acquires the shared desktop lock.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0+a2727571cbd07165797602a4271ef019099401eb --scope kitty-aggregate
~~~
