# Rich TUI OSC 52 read qualification

| Measurement | Verdict |
| --- | --- |
| Pinned terminfo.dev 4.0.0 read-only terminal callback | **inconclusive** |
| Owned OSC 52 clipboard read capability | **unsupported** |
| Owned OSC 52 read denial (`ntilde-owned-x11-osc52-read-denial-v1`) | **supported** |
| Historical stock run `02be5c606e0e7d79ab8e11f142d21ae0` | **inconclusive** |

The unchanged pinned live callback seeds through its fixture and requires the OSC 52 c response to return that exact nonce. The replay supplies the observed seed and fenced reply. The complete empty frame grades `insufficient-evidence`. The pinned parser-context registration shares a round-trip path that issues an OSC write. This run measures the live read-only callback.

A fresh GTK peer read `terminfo-osc52-733a44ae-9ba5-4c17-b704-01b1f65153c6` from the independently seeded default X11 CLIPBOARD. The child sent the pinned query `1b 5d 35 32 3b 63 3b 3f 07` followed by a cursor-position request. The full PTY capture was `1b 5d 35 32 3b 63 3b 1b 5c 1b 5b 31 3b 34 38 52`. Its OSC reply was `1b 5d 35 32 3b 63 3b 1b 5c`, followed by the expected cursor-position fence. A different GTK peer read the same nonce from the same owner after the full one-second capture and before restoration.

The complete empty response and the surviving nonce satisfy the denial contract. Silence with a valid CPR, malformed frames, stale data, late bytes and missing or ambiguous delivery fences have distinct classifications. The owned read-capability result enters the qualification inventory as measured unsupported. The denial verdict records the policy separately. The original stock collector refused the query without a disposable clipboard. [Historical stock baseline](../2026-11-09-stock-baseline.json) · [Stock triage](../2026-11-02-inconclusive-triage.md).

The disposable profile had OSC 52 writes disabled. The prior Modern round trip captured the same denial but restored its baseline immediately after the query. This run verifies the seed before and after the query. OSC 5522 permissions remain governed by the existing policy. [Modern round trip](../2026-11-25-modern-osc52-clipboard-results/report.md) · [Clipboard permission policy](../../clipboard-permissions.md).

## Retained evidence and reproduction

App `0.14.0` came from `7824244ebd715d9f14ade754ce3fbb352ec7643d` with .NET SDK `10.0.403`. Collector `a8e95282ca51f4c57e72944a69c5915b371e2a17` ran with Node `v24.22.2` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). App PID 5999 owned pane `98930a73-37ad-430d-937f-8377defe6b35` and PTY child PID 6054 on `/dev/pts/0`. XRes verified the App and each GTK peer against the owned Xvfb server.

The restoring peer reset the fixture to `ntilde-osc52-baseline-559a9b27c85d91c18f653a444252fac3`, and a fresh reader verified `ntilde-osc52-baseline-559a9b27c85d91c18f653a444252fac3`. The archive retains the complete PTY stream, X11 ownership checks, App hashes, measured sources, termios restoration, and all process exits. The X11 socket and fixture tree were removed before the shared desktop lease released at 2026-11-27T12:12:09.765Z. [Owned run](owned-run.zip) · [Manifest](manifest.json) · [Derived report](report.json).

[Earlier attempt `2d19aa875ec313ec56ef4212626ee776`](attempt-1.zip). This run also verified empty denial and both clipboard readbacks. The collector was rerun after changing how it selects the observer, so the published measurement matches the final collector source.

Rederive offline with Node 24 and Python 3.

~~~sh
node scripts/terminfo/osc52-read-results.mjs --check
~~~

For a fresh Linux run, install Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. Use the shared desktop lock across any container boundary.

~~~sh
# Build from App revision 7824244ebd715d9f14ade754ce3fbb352ec7643d.
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# Launch from collector revision a8e95282ca51f4c57e72944a69c5915b371e2a17.
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0 --osc52-read
~~~
