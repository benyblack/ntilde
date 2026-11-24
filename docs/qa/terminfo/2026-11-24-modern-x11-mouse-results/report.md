# Ntilde Modern X11 mouse assessment

**62 pass / 0 fail / 7 inconclusive across 69 frozen requirements.**

The [child environment predecessor](../2026-11-24-modern-child-environment-results/report.md) recorded 59 pass, 0 fail and 10 inconclusive. This batch applies the [frozen App criteria and controls](../modern-x11-mouse.md).

| Case | Fresh 1000 + 1006 click bytes | Target bytes | Modern verdict |
| --- | --- | --- | --- |
| `input.x10-mouse` | `1b 5b 3c 30 3b 34 39 3b 31 35 4d` → `1b 5b 3c 30 3b 34 39 3b 31 35 6d` | `1b 5b 4d 20 66 2f` | pass |
| `input.button-event-mouse` | `1b 5b 3c 30 3b 34 39 3b 32 30 4d` → `1b 5b 3c 30 3b 34 39 3b 32 30 6d` | `1b 5b 3c 30 3b 37 30 3b 32 30 4d` → `1b 5b 3c 33 32 3b 39 37 3b 32 30 4d` → `1b 5b 3c 30 3b 39 37 3b 32 30 6d` | pass |
| `modes.alt-scroll-1007` | `1b 5b 3c 30 3b 34 39 3b 32 35 4d` → `1b 5b 3c 30 3b 34 39 3b 32 35 6d` | `1b 5b 41` | pass |

- `input.x10-mouse`. Legacy CSI M and three valid left-press payload bytes arrived on the press.
- `input.button-event-mouse`. SGR motion with the left-button motion bit arrived before the destination release.
- `modes.alt-scroll-1007`. The alternate-screen wheel-up byte stream was 1b5b41.

The report covers App delivery through the Linux X11 backend. Each target used separate raw observation windows ending with a unique XTEST keyboard delimiter. DECRPM exchanges qualified the exact initial, control and target mode selections. The final exchange and private pane read verified the initial effective modes and primary-screen marker. The worker restored and read back its original kernel terminal mode.

## Evidence and reproduction

The [owned run](owned-run.zip) `0e8540a355ecf2885806424b86d26eea` began at 2026-11-24T21:14:33.848Z on Ubuntu 26.04 LTS, Linux 7.0.0-38-generic x86_64. App `0.14.0` was built from `7b6858ebe104b69625fabc5371fcdb927d8c03d2` with .NET SDK 10.0.403. Collector `411c06c4da37359bf890a9af9f343f18ffdcc2f1` ran under Node v24.22.2.

Private display `:0` provided XTEST 2.2. The App PID was 1607704, the child PID 1607856 and the direct PTY `/dev/pts/0`. The owned pane measured 40 rows by 149 columns in X window 2097171. After the worker exited, the collector reaped its App and Xvfb processes and verified the display socket was gone. The isolated scratch tree was removed before the lease released at 2026-11-24T21:14:44.303Z.

The archive retains the exact mode writes and replies, XTEST actions and coordinates, timestamped raw PTY bytes, profile, process and display identities, complete App publish tree hashes, source policy `owned-modern-x11-mouse-v1` and the lease audit. [Manifest](manifest.json) · [All 69 rows](report.json).

[Retained run `99cbfe93f97e673edd4e05c73c31310b`](attempt-1.zip). First accepted capture. Superseded after the SonarCloud and lease guard fixes, including a stricter minimum silence window and explicit fixture-removal record. The three results repeated.

[Retained run `049caa77f45006d44a3c46652d2b0c89`](attempt-2.zip). Repeated after the SonarCloud and lease guard fixes. Superseded after CodeRabbit prompted the per-action XTEST keyboard fences and X10 payload validation. All three target sequences repeated.

Rederive the report from the archive with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --x11-mouse
```

To collect a fresh run, use Linux x64 with Xvfb, libX11, libXtst, libXRes and Python 3. The collector uses its own private display and the normal desktop collection lease.

```sh
# Build in an App checkout at 7b6858ebe104b69625fabc5371fcdb927d8c03d2.
scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# Launch from a separate collector checkout at 411c06c4da37359bf890a9af9f343f18ffdcc2f1.
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0
```
