# Ntilde Modern OSC 52 clipboard assessment

**INCONCLUSIVE · 66 pass, 0 fail, 3 inconclusive across 69 frozen requirements.**

| Observation | Retained value |
| --- | --- |
| Independent baseline read | `ntilde-osc52-baseline-aed88b07bc93538c797b26c01677ce0b` (GTK owner PID 5303) |
| XTEST a in the armed direct PTY | `61` |
| OSC 52 c write | `1b 5d 35 32 3b 63 3b 62 6e 52 70 62 47 52 6c 4c 57 39 7a 59 7a 55 79 4c 58 42 79 62 32 4a 6c 4c 54 67 77 5a 44 4d 7a 59 32 59 33 4e 57 49 32 5a 47 59 31 4f 54 52 68 5a 6a 59 32 4e 47 4a 6d 4d 7a 46 6a 5a 44 4e 6d 4d 54 6b 34 07` |
| Independent post-write CLIPBOARD read | `"ntilde-osc52-probe-80d33cf75b6df594af664bf31cd3f198"` (owner PID 5237) |
| Second nonce verified by a fresh GTK reader | `ntilde-osc52-seed-7754300655d81c31366739a122770583` (GTK owner PID 5445) |
| OSC 52 c query | `1b 5d 35 32 3b 63 3b 3f 07` |
| Raw PTY query capture 1, including CPR | `1b 5d 35 32 3b 63 3b 1b 5c 1b 5b 31 3b 34 33 52` |
| Settled OSC 52 c reply | `1b 5d 35 32 3b 63 3b 1b 5c` |
| Independent restoration read | `ntilde-osc52-baseline-aed88b07bc93538c797b26c01677ce0b` (GTK owner PID 5541) |

**Write.** The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.

**Combined criterion.** OSC 52 c returned a complete empty-payload denial frame. The independently seeded nonce was absent from the reply.

The disposable profile used `AllowOsc52ClipboardWrite=true`. Ntilde’s OSC 52 read-denial policy remains in effect.

The [OSC 52 write predecessor](../2026-11-25-modern-osc52-write-results/report.md) recorded 66 pass, 0 fail and 3 inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `7eb5acb92f5023227f9d082f23230e0b` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). XTEST 2.2 delivered input to App window 2097171 on display `:0` with root 543. App PID 5237 owned pane `aae9c660-bf4a-41c4-ae7a-b45604d1feb7` and child PID 5292 on `/dev/pts/0`. XRes verified the App and all GTK peers on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at 2026-11-25T09:59:02.366Z.

[Earlier run](attempt-1.zip) `71e32f52ec276e6a05cbd08946d2a28d`. The GTK peer refused the second seed outside its baseline-only allowlist. The App write, independent baseline restoration, PTY restoration and cleanup are retained.

[Earlier run](attempt-2.zip) `378d02a451f6381ffd934458f9095530`. This run reproduced the empty denial and verified restoration before the SonarCloud fixes changed the measured reply classifier.

[Earlier run](attempt-3.zip) `abebad8878bc12365c1d96ac47f905b9`. This run verified the write and empty denial before the Codex review fix bounded each stale-nonce retry to the original deadline.

App 0.14.0 was built from source `5c7f7229102f4085640bf09e643ce592b1e65426` with .NET SDK 10.0.403. The archive hashes the App publish tree. Collector `f91d2b858ad54608f21b0cf6febe392745a9f9a2` ran under Node v24.22.2 and retained its source bytes, PTY requests and complete replies, X11 ownership checks, settings and cleanup. [Manifest](manifest.json).

The frozen App path retries a canonical reply carrying the earlier OSC 52 write nonce at 25 ms intervals within the default one-second write deadline. Each retry uses the time left in the original window. The collector uses XTEST `a` with verified child input as the activation event. The query captures a cursor-position control in the same PTY window, and a fresh GTK reader confirms the second nonce before the first query. [Frozen upstream probe](https://github.com/beorn/terminfo.dev/blob/3eea0f130d87d64eab33ab342c96e668daab0292/packages/probe-defs/src/extensions.ts#L4597).

Rederive the publication offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --osc52-clipboard
```

A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.

```sh
# From an App checkout at 5c7f7229102f4085640bf09e643ce592b1e65426
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at f91d2b858ad54608f21b0cf6febe392745a9f9a2
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --osc52-clipboard
```
