# Ntilde Modern clipboard paste assessment

**PASS · 67 pass, 0 fail, 2 inconclusive across 69 frozen requirements.**

Ctrl+V delivered the nonce raw with mode 2004 off, bracketed with it on, and raw again with it off.

The disposable Linux profile used the default `Ctrl+V` binding. The App read X11 `CLIPBOARD` from the independent GTK owner. [Shortcut catalog](https://github.com/benyblack/ntilde/blob/1b126168583bb8b2c144d0adde33a19f80ea1e30/src/Ntilde.App/Shell/Shortcuts/ShortcutCatalog.cs#L47) · [App paste route](https://github.com/benyblack/ntilde/blob/1b126168583bb8b2c144d0adde33a19f80ea1e30/src/Ntilde.App/MainWindow.axaml.cs#L668) · [Frozen upstream App probe](https://github.com/beorn/terminfo.dev/blob/3eea0f130d87d64eab33ab342c96e668daab0292/packages/probe-defs/src/extensions.ts#L5368).

| Observation | Retained value |
| --- | --- |
| Incoming DECRPM 2004 | reset |
| Baseline read by a fresh GTK peer | `ntilde-paste-baseline-dfcd1ee071714da5fc0925510a824935` (owner PID 91) |
| XTEST a in the armed raw PTY | `61` |
| Nonce read by a fresh GTK peer | `terminfo-paste-0bc5e52c-8af8-4121-911a-45c7ed8d9e86` (owner PID 187) |
| off-before · DECRPM `1b 5b 3f 32 30 30 34 3b 32 24 79` | `74 65 72 6d 69 6e 66 6f 2d 70 61 73 74 65 2d 30 62 63 35 65 35 32 63 2d 38 61 66 38 2d 34 31 32 31 2d 39 31 31 61 2d 34 35 63 37 65 64 38 64 39 65 38 36` |
| on · DECRPM `1b 5b 3f 32 30 30 34 3b 31 24 79` | `1b 5b 32 30 30 7e 74 65 72 6d 69 6e 66 6f 2d 70 61 73 74 65 2d 30 62 63 35 65 35 32 63 2d 38 61 66 38 2d 34 31 32 31 2d 39 31 31 61 2d 34 35 63 37 65 64 38 64 39 65 38 36 1b 5b 32 30 31 7e` |
| off-after · DECRPM `1b 5b 3f 32 30 30 34 3b 32 24 79` | `74 65 72 6d 69 6e 66 6f 2d 70 61 73 74 65 2d 30 62 63 35 65 35 32 63 2d 38 61 66 38 2d 34 31 32 31 2d 39 31 31 61 2d 34 35 63 37 65 64 38 64 39 65 38 36` |
| Restored DECRPM 2004 | reset |
| Restored selection read by a fresh GTK peer | `ntilde-paste-baseline-dfcd1ee071714da5fc0925510a824935` (restoring owner PID 431) |

The private display started with no CLIPBOARD owner. The GTK fixture prepared and verified a synthetic baseline before the input control. A fresh GTK owner restored the exact baseline text. Each paste used a separate armed raw window, verified the same seeded nonce afterward, and retained its full PTY chunks with the XTEST delimiter that closed the read.

The [OSC 52 clipboard predecessor](../2026-11-25-modern-osc52-clipboard-results/report.md) recorded 66 pass, 0 fail and 3 inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `3619280b54ef322f99fd7c906a2f55ae` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). XTEST 2.2 delivered input to App window 2097171 on display `:0` with root 543. App PID 23 owned pane `0219178e-9bc8-4881-b2e9-e4228dd2f98e` and child PID 78 on `/dev/pts/0`. XRes verified the App and GTK clients on owned Xvfb PID 20. The child restored termios and exited. All peers, the App and Xvfb exited. The display socket and scratch tree disappeared before the lease released at 2026-11-25T10:47:57.036Z.

[Earlier run](attempt-1.zip) `199f7fc1e08a9f3046e50e57e603a9c1`. This run passed the off/on/off byte comparison and verified restoration. The final run uses the frozen upstream callback’s UUID nonce format so the retained bytes can also be replayed through that callback.

App 0.14.0 was built from `1b126168583bb8b2c144d0adde33a19f80ea1e30` with .NET SDK 10.0.403. Collector `7733a21fc5166be8ad68b0ccabdaf38c154d7c59` ran with Node v24.22.3. The archive retains App publish tree hashes, collector source bytes, exact mode exchanges, raw child chunks, XTEST events, peer readbacks and the cleanup audit. [Manifest](manifest.json).

Rederive the publication offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --clipboard-paste
```

A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites).

```sh
# From an App checkout at 1b126168583bb8b2c144d0adde33a19f80ea1e30
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at 7733a21fc5166be8ad68b0ccabdaf38c154d7c59
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --clipboard-paste
```
