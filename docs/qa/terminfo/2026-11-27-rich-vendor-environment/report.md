# Rich TUI vendor child environment assessment

The frozen catalogue designates all four cases manual. The pinned `terminfo.dev@4.0.0` bundle has no runnable registration for these IDs. The stock baseline has no grade for any of them. Each decision below applies to Ntilde’s own vendor-variable provision on the measured macOS direct-local path.

| Frozen case | Documented producer | Applicability to Ntilde | Pinned | Stock |
| --- | --- | --- | --- | --- |
| `device.env-lc-terminal` | iTerm2 | not-applicable | unprobed | ungraded |
| `device.env-kitty` | Kitty | not-applicable | unprobed | ungraded |
| `device.env-wezterm` | WezTerm | not-applicable | unprobed | ungraded |
| `device.env-wt-session` | Windows Terminal | not-applicable | unprobed | ungraded |

The seeded values show ordinary environment inheritance. These strings carry no vendor session, pane, process, executable or socket proof. This assessment contributes no stock, inventory, Core, Modern or comparison points.

## Observed values

The owner removed the allowlisted vendor names before the clean launch and seeded every one with a distinct synthetic value before the inherited launch. A disposable local profile launched Node directly with shell integration and session persistence disabled. The worker sampled only the allowlist. The raw format preserves empty strings separately from absent keys.

| Variable | Clean App input | Clean PTY child | Seeded App input | Seeded PTY child |
| --- | --- | --- | --- | --- |
| `LC_TERMINAL` | absent | absent (absent) | <code>ntilde-qa-vendor-lc-terminal</code> | <code>ntilde-qa-vendor-lc-terminal</code> (inherited) |
| `LC_TERMINAL_VERSION` | absent | absent (absent) | <code>ntilde-qa-vendor-lc-terminal-version</code> | <code>ntilde-qa-vendor-lc-terminal-version</code> (inherited) |
| `KITTY_PID` | absent | absent (absent) | <code>ntilde-qa-vendor-kitty-pid</code> | <code>ntilde-qa-vendor-kitty-pid</code> (inherited) |
| `KITTY_WINDOW_ID` | absent | absent (absent) | <code>ntilde-qa-vendor-kitty-window-id</code> | <code>ntilde-qa-vendor-kitty-window-id</code> (inherited) |
| `KITTY_INSTALLATION_DIR` | absent | absent (absent) | <code>ntilde-qa-vendor-kitty-installation-dir</code> | <code>ntilde-qa-vendor-kitty-installation-dir</code> (inherited) |
| `WEZTERM_PANE` | absent | absent (absent) | <code>ntilde-qa-vendor-wezterm-pane</code> | <code>ntilde-qa-vendor-wezterm-pane</code> (inherited) |
| `WEZTERM_EXECUTABLE` | absent | absent (absent) | <code>ntilde-qa-vendor-wezterm-executable</code> | <code>ntilde-qa-vendor-wezterm-executable</code> (inherited) |
| `WEZTERM_EXECUTABLE_DIR` | absent | absent (absent) | <code>ntilde-qa-vendor-wezterm-executable-dir</code> | <code>ntilde-qa-vendor-wezterm-executable-dir</code> (inherited) |
| `WEZTERM_CONFIG_DIR` | absent | absent (absent) | <code>ntilde-qa-vendor-wezterm-config-dir</code> | <code>ntilde-qa-vendor-wezterm-config-dir</code> (inherited) |
| `WEZTERM_UNIX_SOCKET` | absent | absent (absent) | <code>ntilde-qa-vendor-wezterm-unix-socket</code> | <code>ntilde-qa-vendor-wezterm-unix-socket</code> (inherited) |
| `WT_SESSION` | absent | absent (absent) | <code>ntilde-qa-vendor-wt-session</code> | <code>ntilde-qa-vendor-wt-session</code> (inherited) |
| `WT_PROFILE_ID` | absent | absent (absent) | <code>ntilde-qa-vendor-wt-profile-id</code> | <code>ntilde-qa-vendor-wt-profile-id</code> (inherited) |
| `TERM_PROGRAM` | absent | <code>Ntilde</code> (app-provided) | <code>ntilde-qa-vendor-term-program</code> | <code>Ntilde</code> (app-provided) |
| `NTILDE_QA_ENVIRONMENT_CONTROL` | <code>ntilde-qa-neutral-clean</code> | <code>ntilde-qa-neutral-clean</code> (inherited) | <code>ntilde-qa-neutral-inherited</code> | <code>ntilde-qa-neutral-inherited</code> (inherited) |

The App’s native PTY setup explicitly writes `TERM_PROGRAM=Ntilde`. The independent neutral control was inherited in both launches. [Ntilde PTY setup](https://github.com/benyblack/ntilde/blob/7824244ebd715d9f14ade754ce3fbb352ec7643d/src/Ntilde.App/native/src/lib.rs#L986-L994).

## Evidence and reproduction

The App checkout was `7824244ebd715d9f14ade754ce3fbb352ec7643d` (version `0.14.0`). The collector checkout was `a33b9bfacde48e6c35b7dbe7b70290661b3d5d12` with Node v24.22.2 on macOS 27.0.1 (26A434) arm64.

- [clean launch](clean-run.zip) `b9c0a789d71ceeaec3c58b62042948a5` bound App PID 77115, its direct child PID 77140, and pane `bba00c43-cb05-44a6-8cda-5577fd720686`. The kernel terminal attributes were restored and the desktop lease released at 2026-11-27T12:09:36.931Z.
- [inherited launch](inherited-run.zip) `ef2bc4be6e1e7427997bab8c73b52dd6` bound App PID 77181, its direct child PID 77206, and pane `579e75fc-6952-45a6-a7a4-cec1cd72bfed`. The kernel terminal attributes were restored and the desktop lease released at 2026-11-27T12:09:38.383Z.

The two archives retain the App executable, assembly, managed PTY and native PTY library hashes, the collector’s source snapshot, the launch settings, the child and pane proof, the raw environment result, and the filtered lock log. The executable and source hashes identify the measured inputs. The App checkout is build provenance. The observation covers local PTY inheritance on this build and host. [Contract and primary sources](../rich-vendor-environment.md) · [Measurement manifest](manifest.json) · [Exact criteria, identities and observations](report.json).

Re-derive both reports offline with Node 24 and Python 3.

~~~sh
node scripts/terminfo/rich-vendor-environment-results.mjs --check
~~~

To repeat the measurement, build the recorded App checkout and run both controls from the recorded collector checkout on macOS. Each launch uses the shared desktop lease.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
NTILDE_TERMINFO_VENDOR_ENVIRONMENT_CONTROL=clean node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope rich-vendor-environment
NTILDE_TERMINFO_VENDOR_ENVIRONMENT_CONTROL=inherited node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope rich-vendor-environment
~~~
