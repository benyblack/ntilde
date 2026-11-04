# Windows and Linux inline baselines — 2026-11-04

The first terminfo.dev observations of Ntilde on Windows and Linux, collected with
the [inline runner](../QA_TERMINFO.md) (`scripts/terminfo-baseline.mjs`). These are
**query-only** runs. The stock collector refuses all state-changing checks (206 of
256) without verified ownership of a disposable terminal. These runs say nothing
about pixels, input, resize or clipboard behaviour, and they do not qualify
Windows or Linux for #775. They change no grade, capability claim, matrix row or
frozen evidence.

Raw originals are local and Git ignored. This summary does not replace them.

## Runs

All runs used `terminfo.dev@4.0.0`, suite `16a690f34c0e`, collector revision
`cf7d8759c7e637aa8dba1e429f3dddd1c0124158`, Node `v24.22.2`. The suite receipt
matched the pinned digests, and every run had `suiteComplete: true` and
`identity: unverified`. The app version was operator supplied (`0.13.0`) and is
not independently verified.

| Run | Platform and path | Tab | Grid | Run ID | Raw SHA256 |
|---|---|---|---|---|---|
| `windows-wsl-run-iSFOh4` | Windows, ConPTY → `wsl.exe` → WSL2 `6.18.54.1-microsoft-standard-WSL2` | Persistent (mux-backed) | 93×31 | `c3df39d8e48e6777ff45248f2caf3cfc` | `6955d8a28d1ef7bb52a8829b7242248b9de3254ab1a832970c9e5c8211da7f3a` |
| `windows-wsl-run-3OGRsT` | Same as above | Direct | 93×31 | `dcac5ca59032a64be17ff71f2616fc46` | `66652101517e7efdd6af3bd4362d2d84ee7218f992ad5d24e0815c6b34a8bbca` |
| `linux-run-cfc2766f` | Ubuntu 26.04.1 VM (VirtualBox), kernel `7.0.0-31-generic`, GNOME on Wayland | Direct sidecar | 149×41 | `cfc2766f291f1ea26665f85cd42d9ba5` | `2e7b67106875e6a07cb8d250d48540a31d558d5541fec68a4c382a494398d9c5` |

The Windows runs report `os: linux` because Node runs inside WSL. The raw files
are unedited. Their recorded checkout (`1959dfb4`) is a local commit in the
Windows clone that is not on GitHub, so the Windows build's source is not
traceable from the run. The Linux run's checkout,
`26d315644dedcced2578b182656a57560e0d6dee`, is on `main`. The Wayland session type
was reported by the operator.

| Run | Supported | Unsupported | Invalid reply | No response | Insufficient evidence | Policy refused |
|---|---:|---:|---:|---:|---:|---:|
| Windows, persistent | 26 | 1 | 1 | 15 | 7 | 206 |
| Windows, direct | 28 | 0 | 0 | 15 | 7 | 206 |
| Linux, direct | 28 | 0 | 0 | 15 | 7 | 206 |

## Comparison

The reference is a local macOS owned run of 0.13.0 from the same day (run
`ece931da9c34fd8b0af603c4007d81c5`, raw SHA256
`15c0c21534cbb611d654b0b76a747742c7b829fa5fb94e1204b86803f7568f8c`, a direct
pane). The comparison uses only the 50 probes the inline collector executed.

- **Linux, Windows direct and macOS give the same outcome on all 50.** Raw replies
  differ only in theme colors (OSC 4/10/11/12; the Windows install used a
  non-default theme) and in geometry (`CSI 14/16/18 t`).
- **The Windows persistent tab differs on two probes.** Both differences come from
  the persistent-session design, not from Windows or ConPTY:

| Probe | Persistent | Direct | Cause |
|---|---|---|---|
| `extensions.kitty-graphics` (`a=q`) | `ESC _ G i=31;ERR ESC \` | `ESC _ G i=31;OK ESC \` | The mux daemon parser has no image decoder and refuses non-Sixel images (`HeadlessTerminalSession`), as do mux-backed client panes. Tracked in #838. |
| `extensions.osc5522-clipboard` | `CSI ? 5522 ; 0 $ y` | `CSI ? 5522 ; 2 $ y` | `WireKittyClipboard` does not enable MIME clipboard for mux-backed panes, because the daemon cannot reach the desktop clipboard. |

## ConPTY

ConPTY did not intercept any probed query. ConPTY's console implements DECSCNM
(`?5`) and DECCOLM (`?3`) and would have reported them as reset (2). Both replies
were 0, Ntilde's answer for those modes in this build. DA1/DA2/DA3, XTVERSION,
OSC 4/10/11/12, DSR, DSR 996, `CSI 14/16/18 t` and DECRPM were all answered by
Ntilde. The Kitty APC query also reached Ntilde intact through ConPTY in both
Windows runs.

## Geometry note

`CSI 14 t` reports the pane's pixel size, including leftover space narrower than
a cell. On Linux it equalled the grid exactly (149 × 8 = 1192 px wide), but on
macOS it was 1190 px for 140 × 8 = 1120 px of cells. That matches kitty-style
behaviour. Clients should derive the cell size from `CSI 16 t`, not from
`14 t` divided by `18 t`.

## Native Windows

The stock collector cannot record results on native Windows: `terminfo.dev@4.0.0`
throws "JSON/file collection requires a proved Windows controlling TTY" when
`--output` is used. A native PowerShell attempt failed this way after preparing
the cache. WSL inside an Ntilde tab is the working path; see
[QA_TERMINFO.md](../QA_TERMINFO.md#windows-collect-through-wsl).

## Remaining

- Owned Windows and Linux runs (#775), which cover pixels, input, resize and
  clipboard. A pre-PR snapshot of the Linux process/PTY provider foundation
  (#842) passed its 36 unit tests and its self-test on this Ubuntu 26.04.1 VM.
  The provider is not yet connected to the owned runner.
- A decision on images in persistent sessions (#838).
