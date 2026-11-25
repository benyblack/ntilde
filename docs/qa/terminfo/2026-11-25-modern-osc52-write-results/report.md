# Ntilde Modern OSC 52 write assessment

**PASS · 66 pass, 0 fail, 3 inconclusive across 69 frozen requirements.**

| Observation | Retained value |
| --- | --- |
| Independent baseline read | `ntilde-osc52-baseline-598653bdf54ffc5b2f17ad2389498420` (GTK owner PID 9978) |
| XTEST a in the armed direct PTY | `61` |
| Exact OSC 52 c write | `1b 5d 35 32 3b 63 3b 62 6e 52 70 62 47 52 6c 4c 57 39 7a 59 7a 55 79 4c 58 42 79 62 32 4a 6c 4c 54 41 33 4e 32 4a 69 4f 54 55 77 5a 6d 51 32 4f 57 55 32 59 57 4d 77 4d 54 6b 30 59 54 45 77 5a 57 5a 6b 5a 47 4e 6c 4f 44 6c 6d 07` |
| Cursor-position reply after the write | `1b 5b 31 3b 34 33 52` |
| Independent post-write CLIPBOARD read | `"ntilde-osc52-probe-077bb950fd69e6ac0194a10efddce89f"` (owner PID 9911) |
| Independent restoration read | `ntilde-osc52-baseline-598653bdf54ffc5b2f17ad2389498420` (GTK owner PID 10129) |

The independent GTK peer read the exact nonce from the App-owned X11 CLIPBOARD.
The disposable profile used `AllowOsc52ClipboardWrite=true` and the production native clipboard path. The App write setting defaults to enabled. This case measures App output to X11 CLIPBOARD. The OSC 52 round-trip and native paste rows retain their prior verdicts.

The [focus-tracking predecessor](../2026-11-25-modern-focus-tracking-results/report.md) recorded 65 pass, 0 fail and 4 inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `aa59d5428eb0f2a5799b31b769596275` on Ubuntu 24.04.5 LTS, Linux 7.0.14-linuxkit aarch64 (Docker Desktop). XTEST 2.2 delivered input to X11 window 2097171 on display `:0` with root 543. App PID 9911 owned pane `bee98f2a-320e-4580-b8d1-5619a28adff5` and child PID 9966 on `/dev/pts/0`. XRes verified the App and GTK clients on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at 2026-11-25T08:29:15.593Z.

App 0.14.0 was built from source `61252b56a3e769d714b0d2635727f94d1b775bc9` with .NET SDK 10.0.403. The archive hashes the App publish tree. Collector `5a4350fd0d0da6faa80817c2660d2a2019cb12b8` ran under Node v24.22.2 and retained the measured sources, settings, process and selection-owner checks, PTY bytes, clipboard readings and cleanup. [Manifest](manifest.json).

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
# From a collector checkout at 5a4350fd0d0da6faa80817c2660d2a2019cb12b8
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --osc52-write
```
