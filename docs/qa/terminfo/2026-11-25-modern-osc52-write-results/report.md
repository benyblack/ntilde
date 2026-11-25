# Ntilde Modern OSC 52 write assessment

**PASS · 66 pass, 0 fail, 3 inconclusive across 69 frozen requirements.**

| Observation | Retained value |
| --- | --- |
| Independent baseline read | `ntilde-osc52-baseline-6b7f0bcf7826738f6141b1fe41c83ffb` (GTK owner PID 11435) |
| XTEST a in the armed direct PTY | `61` |
| Exact OSC 52 c write | `1b 5d 35 32 3b 63 3b 62 6e 52 70 62 47 52 6c 4c 57 39 7a 59 7a 55 79 4c 58 42 79 62 32 4a 6c 4c 54 45 78 59 54 59 79 59 54 63 35 5a 6a 56 6d 5a 54 67 30 4d 7a 45 7a 4d 32 52 6b 5a 6a 4d 34 4d 44 68 6d 4d 7a 63 32 4e 7a 6b 33 07` |
| Cursor-position reply after the write | `1b 5b 31 3b 34 33 52` |
| Independent post-write CLIPBOARD read | `"ntilde-osc52-probe-11a62a79f5fe843133ddf3808f376797"` (owner PID 11369) |
| Independent restoration read | `ntilde-osc52-baseline-6b7f0bcf7826738f6141b1fe41c83ffb` (GTK owner PID 11578) |

The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.
The disposable profile used `AllowOsc52ClipboardWrite=true` and the production native clipboard path. The App write setting defaults to enabled. This case measures App output to X11 CLIPBOARD. The OSC 52 round-trip and native paste rows retain their prior verdicts.

The [focus-tracking predecessor](../2026-11-25-modern-focus-tracking-results/report.md) recorded 65 pass, 0 fail and 4 inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `0a796d875f77a89f76a10eb94c83e2c0` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). XTEST 2.2 delivered input to X11 window 2097171 on display `:0` with root 543. App PID 11369 owned pane `06caac72-f7e9-4088-9674-32785cac159b` and child PID 11424 on `/dev/pts/0`. XRes verified the App and GTK clients on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at 2026-11-25T08:43:55.532Z.

[Earlier run](attempt-1.zip) `aa59d5428eb0f2a5799b31b769596275`. The first run passed before collector review changes.

[Earlier run](attempt-2.zip) `a59061d3e234fec876d17f72920a36fd`. GTK widget startup exceeded the peer admission deadline before any clipboard operation.

[Earlier run](attempt-3.zip) `be6a3277b78f477f6fd084c797984560`. A retry hit the same GTK peer admission deadline before any clipboard operation.

[Earlier run](attempt-4.zip) `9f46b85276fed8bd6cf4ebce40956bf9`. A diagnostic launch under strace exceeded the App readiness deadline before peer startup.

[Earlier run](attempt-5.zip) `2d7b576c4ab86173212efe50f7888092`. A process-state diagnostic reproduced the GTK widget admission timeout before any clipboard operation.

[Earlier run](attempt-6.zip) `b0e2b6d31bc32ae40d04b30dbb1802f7`. The native GDK surface admitted the text peers and the run passed. The final run includes further review changes.

App 0.14.0 was built from source `61252b56a3e769d714b0d2635727f94d1b775bc9` with .NET SDK 10.0.403. The archive hashes the App publish tree. Collector `00e45cbef63fb037b5a94aaa0e014929ec117c69` ran under Node v24.22.2 and retained the measured sources, settings, process and selection-owner checks, PTY bytes, clipboard readings and cleanup. [Manifest](manifest.json).

The batch applies the frozen App verdict rules with an XTEST `a` whose raw byte reached the child, as required for this collection. The upstream probe uses a silent modifier as its activation event. Both use a same-PTY cursor-position barrier and independent clipboard readback.

Rederive the publication offline with Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --osc52-write
```

A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. The collector starts its own Xvfb and admits the GTK text peers through XRes before allowing clipboard operations. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.

```sh
# From an App checkout at 61252b56a3e769d714b0d2635727f94d1b775bc9
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at 00e45cbef63fb037b5a94aaa0e014929ec117c69
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --osc52-write
```
