# Ntilde Modern application keypad assessment

**PASS · 64 pass, 0 fail, 5 inconclusive across 69 frozen requirements.**

| Phase | XTEST key | Raw child bytes |
| --- | --- | --- |
| Delivery control | a | `61` |
| DECKPAM (`1b 3d`) | KP_Add | `1b 4f 6b` |
| DECKPNM (`1b 3e`) | KP_Add | `2b` |

KP_Add produced SS3 k under DECKPAM and + under DECKPNM. X11 resolved KP_Add (0xffab) to keycode 86 with symbol slots 0xffab, 0xffab, 0xffab, 0xffab, 0xffab, 0xffab, 0x1008fe22. XKB returned KP_Add with and without the NumLock mask 0x10. Both phases used the same unmodified press and release. Each phase armed a separate raw PTY window before injecting the key. The archive retains the complete stream with its phase-specific trailing keyboard delimiter.

The child verified inactive DECCKM, alternate screen, Kitty keyboard flags and modifyOtherKeys before the control and after each mode write. DECNKM reported numeric mode at entry and after the final DECKPNM. XQueryKeymap and the X11 modifier mask verified key release, and the child restored and read back its original termios settings.

The [frozen App requirement](../2026-11-15-modern-results/requirements-v1.json) grades the OS-delivered bytes. The [older generated keypad evidence](../2026-11-11-routed-input-qualification.md) retains its original verdicts. The [predecessor report](../2026-11-24-modern-modify-other-keys-results/report.md) recorded 63 pass, 0 fail and 6 inconclusive. [All 69 rows](report.json).

## Evidence and reproduction

[Owned run](owned-run.zip) `318af47c517228792e336e095d8275b4` on Ubuntu 26.04 LTS, Linux 7.0.0-38-generic x86_64. XTEST 2.2 targeted window 2097171 on display `:0`. App PID 3623848 owned pane `05e2c6d8-5a66-4b6b-890a-15c388100267` and child PID 3623993 on `/dev/pts/0`. The worker exited, the App and Xvfb were reaped, the socket and scratch tree disappeared, and the lease released at 2026-11-25T06:46:21.563Z.

[Initial attempt](attempt-1.zip) `2682b9dc32af2e075d279a6e56a1bc21` stopped before KP_Add injection. The original mapping check rejected the X11 table’s Ctrl+Alt video-mode symbol. The revised check verifies the effective XKB symbol at the actual NumLock mask. The first attempt also reset the mode and released its lease.

Earlier passing measurements captured the same bytes. The collector retries an unverified XTEST release and can send DECKPNM after an interruption. The published verdict uses a fresh run after the review changes. [`6143dc6ee34cd277e36a5f9560d41f27`](attempt-2.zip) · [`eccedbcf7afcfd727611c8f825a13909`](attempt-3.zip) · [`3ec1562eb2ccd49d834718598ddc8f0a`](attempt-4.zip).

App 0.14.0 was built from a source archive at `0506c07cb1b259a55871fdf71fa35f1d27bf3bdf` with .NET SDK 10.0.403. The archive hashes the complete publish tree. Collector `df9e548e38a6c2216362b4e44470386aa7dac354` ran under Node v24.22.2 and retained the launcher and its measured source files. [Manifest](manifest.json).

Rederive the publication offline using Node 24 and Python 3.

```sh
node scripts/terminfo-modern-results.mjs --check --application-keypad
```

For a fresh measurement, install Node 24, Python 3, Xvfb, libXtst, libX11 and libXRes on Linux x64. Building the App requires the pinned .NET SDK and Rust toolchain (cargo and rustc). See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites). The launcher requires the canonical path to the private executable, with every symlink component resolved.

```sh
# From an App checkout at 0506c07cb1b259a55871fdf71fa35f1d27bf3bdf
scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
chmod -R go-w /absolute/private/sidecar
# From a collector checkout at df9e548e38a6c2216362b4e44470386aa7dac354
node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" 0.14.0 --application-keypad
```
