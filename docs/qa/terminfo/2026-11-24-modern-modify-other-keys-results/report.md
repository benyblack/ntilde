# Ntilde Modern modifyOtherKeys assessment

**PASS · 63 pass, 0 fail, 6 inconclusive across 69 frozen requirements.**

| External input under level 2 | Raw child bytes |
| --- | --- |
| Plain a | `61` |
| Ctrl+I | `1b 5b 32 37 3b 35 3b 31 30 35 7e` |

Ctrl+I produced a frozen level-2 encoding. Each row used a separate raw PTY window armed before the XTEST chord. The archive includes the complete stream, including each window’s distinct trailing keyboard delimiter.

The child queried DECCKM, DECNKM, alternate screen, Kitty keyboard flags and modifyOtherKeys before enabling level 2. The collector required all five inactive. It set level 2 with `CSI > 4 ; 2 m`, read back level 2, verified plain-a delivery, and then injected Ctrl+I. After `CSI > 4 ; 0 m`, the child read back its incoming modes and restored its original termios settings. XQueryKeymap and the X11 modifier mask verified the key releases.

The [frozen requirement](../2026-11-15-modern-results/requirements-v1.json) accepts `CSI 27;5;105~` or `CSI 105;5u`. The [predecessor report](../2026-11-24-modern-x11-mouse-results/report.md) recorded 62 pass, 0 fail and 7 inconclusive. [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `f469ae5cf5f75aa36e38d399a8dd366c` on Ubuntu 26.04 LTS, Linux 7.0.0-38-generic x86_64. XTEST 2.2 delivered both chords to window 2097171 on display `:0`. App PID 1776200 owned pane `34464125-623c-49fd-b67b-732c58c15a6b` and child PID 1776354 on `/dev/pts/0`. The worker exited, the App and Xvfb were reaped, the socket and scratch tree disappeared, and the lease released at 2026-11-24T22:01:07.170Z.

App 0.14.0 was built from a source archive at `bc9a2d2337d299ada91522ef463209a926e4ee47` with .NET SDK 10.0.403. The archive hashes the complete publish tree. Collector `620d9c6e2c881b2a223f4628a5521c1204e8de97` ran under Node v24.22.2 and retained the launcher and its measured source files. [Manifest](manifest.json).

Rederive the publication offline using Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --modify-other-keys
```

For a fresh measurement, use Linux x64 with Xvfb, libXtst, libX11, libXRes and Python 3. Use an absolute private sidecar directory.

```sh
# From an App checkout at bc9a2d2337d299ada91522ef463209a926e4ee47
scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at 620d9c6e2c881b2a223f4628a5521c1204e8de97
node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0 --modify-other-keys
```
