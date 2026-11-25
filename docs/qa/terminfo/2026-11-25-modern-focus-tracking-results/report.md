# Ntilde Modern focus-tracking assessment

**PASS · 65 pass, 0 fail, 4 inconclusive across 69 frozen requirements.**

| Phase | X11 focus | Raw PTY bytes through XTEST a | DECRPM reply |
| --- | --- | --- | --- |
| DECSET 1004 | 2097171 → 543 → 2097171 | `1b 5b 4f 1b 5b 49 61` | `1b 5b 3f 31 30 30 34 3b 31 24 79` |
| DECRST 1004 | 2097171 → 543 → 2097171 | `61` | `1b 5b 3f 31 30 30 34 3b 32 24 79` |

The mode-on read contained CSI O then CSI I before the key. The mode-off read contained only the key.
Enable-time focus report `none`. The raw reader started before the mode write and saved an independent checkpoint before any X11 focus change. The archive retains each read and timestamp, including the trailing XTEST delimiters, the ordered X11 focus readbacks and the restoration exchange. DECRPM replies are supporting detail.

The [frozen App requirement](../2026-11-15-modern-results/requirements-v1.json) grades the raw mode-on and mode-off reads. The [application keypad predecessor](../2026-11-25-modern-application-keypad-results/report.md) recorded 64 pass, 0 fail and 5 inconclusive. [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `8a1b73d0b074c2ef0ca499ddd31ada9c` on Ubuntu 26.04 LTS, Linux 7.0.0-38-generic x86_64. XTEST 2.2 delivered input to App window 2097171 on display `:0` with root 543. App PID 3754176 owned pane `dc32d1f0-b6bc-4798-ac01-c2f69e5a2c21` and child PID 3754269 on `/dev/pts/0`. The child restored termios and exited. The App and Xvfb exited, the socket and scratch tree disappeared, and the lease released at 2026-11-25T07:21:32.655Z.

[Earlier run](attempt-1.zip) `64f49f176397f0205a006507323805be`. The baseline App delivered 61 in both modes even though X11 verified both focus changes. The frozen criterion grades this run unsupported. The App fix observes native window activation in addition to control focus.

App 0.14.0 was built from source archive `cf04b2385192f9fadf71dd8b31f465d54154ee0e` with .NET SDK 10.0.403. The archive hashes the App publish tree. Collector `89c67d544707a33b6794f8c09d15f0f26127d489` ran under Node v24.22.2 and retained the launcher and measured scripts. [Manifest](manifest.json).

Rederive the publication offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --focus-tracking
```

A fresh Linux x64 measurement requires Node 24, Python 3, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.

```sh
# From an App checkout at cf04b2385192f9fadf71dd8b31f465d54154ee0e
scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at 89c67d544707a33b6794f8c09d15f0f26127d489
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --focus-tracking
```
