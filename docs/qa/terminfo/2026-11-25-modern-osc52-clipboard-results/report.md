# Ntilde Modern OSC 52 clipboard assessment

**INCONCLUSIVE · 66 pass, 0 fail, 3 inconclusive across 69 frozen requirements.**

| Observation | Retained value |
| --- | --- |
| Independent baseline read | `ntilde-osc52-baseline-ed4d01d9067a13e28f39fd096267346d` (GTK owner PID 4426) |
| XTEST a in the armed direct PTY | `61` |
| OSC 52 c write | `1b 5d 35 32 3b 63 3b 62 6e 52 70 62 47 52 6c 4c 57 39 7a 59 7a 55 79 4c 58 42 79 62 32 4a 6c 4c 54 51 35 4f 47 5a 68 4e 57 59 79 4d 54 51 31 4f 47 5a 6c 5a 47 45 31 5a 44 59 31 5a 6a 46 6c 5a 57 56 6a 4e 7a 42 68 5a 54 64 69 07` |
| Independent post-write CLIPBOARD read | `"ntilde-osc52-probe-498fa5f21458feda5d65f1eeec70ae7b"` (owner PID 4359) |
| Second nonce verified by a fresh GTK reader | `ntilde-osc52-seed-5406604a594582c064f15360224ec6a2` (GTK owner PID 4568) |
| OSC 52 c query | `1b 5d 35 32 3b 63 3b 3f 07` |
| Raw PTY query capture 1, including CPR | `1b 5d 35 32 3b 63 3b 1b 5c 1b 5b 31 3b 34 33 52` |
| Settled OSC 52 c reply | `1b 5d 35 32 3b 63 3b 1b 5c` |
| Independent restoration read | `ntilde-osc52-baseline-ed4d01d9067a13e28f39fd096267346d` (GTK owner PID 4664) |

**Write.** The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.

**Combined criterion.** OSC 52 c returned a complete empty-payload denial frame. The independently seeded nonce was absent from the reply.

The disposable profile used `AllowOsc52ClipboardWrite=true`. Ntilde’s OSC 52 read-denial policy remains in effect.

The [OSC 52 write predecessor](../2026-11-25-modern-osc52-write-results/report.md) recorded 66 pass, 0 fail and 3 inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `378d02a451f6381ffd934458f9095530` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). XTEST 2.2 delivered input to App window 2097171 on display `:0` with root 543. App PID 4359 owned pane `ce823724-1f6a-44e6-b783-865733c55f8b` and child PID 4414 on `/dev/pts/0`. XRes verified the App and all GTK peers on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at 2026-11-25T09:36:45.704Z.

[Earlier run](attempt-1.zip) `71e32f52ec276e6a05cbd08946d2a28d`. The GTK peer refused the second seed outside its baseline-only allowlist. The App write, independent baseline restoration, PTY restoration and cleanup are retained.

App 0.14.0 was built from source `5c7f7229102f4085640bf09e643ce592b1e65426` with .NET SDK 10.0.403. The archive hashes the App publish tree. Collector `8f3de545178a7f8f5d53892045fbff772859c0f7` ran under Node v24.22.2 and retained its source bytes, PTY requests and complete replies, X11 ownership checks, settings and cleanup. [Manifest](manifest.json).

The frozen App path retries a canonical reply carrying the earlier OSC 52 write nonce at 25 ms intervals within the default one-second write deadline. The collector uses XTEST `a` with verified child input as the activation event. The query captures a cursor-position control in the same PTY window, and a fresh GTK reader confirms the second nonce before the first query. [Frozen upstream probe](https://github.com/beorn/terminfo.dev/blob/3eea0f130d87d64eab33ab342c96e668daab0292/packages/probe-defs/src/extensions.ts#L4597).

Rederive the publication offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --osc52-clipboard
```

A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.

```sh
# From an App checkout at 5c7f7229102f4085640bf09e643ce592b1e65426
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at 8f3de545178a7f8f5d53892045fbff772859c0f7
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --osc52-clipboard
```
