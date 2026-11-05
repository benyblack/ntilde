# Neovim capability check (#844)

#844 asks for a manual check with at least one application that uses the new capability
replies. This note records one: Neovim discovering Ntilde's capabilities through XTGETTCAP,
DECRQSS `m` and the `xterm-ntilde` terminfo entry, on each session path.

It is a manual QA run. It does not change any published terminfo.dev observation or grade.

## Setup

| | |
|---|---|
| Ntilde under test | Debug build of `main` at `478351a1` (includes #853, #861 and #868) |
| Baseline | Released Ntilde 0.13.0 (`/Applications/Ntilde.app`) |
| Application | Neovim 0.12.6, Release build, LuaJIT 2.1.1793383622 (Homebrew) |
| Host | macOS 27.0.1, arm64 |

- **Isolation:** each run used a fresh `NTILDE_APPDATA_ROOT`, so it neither read the user's
  settings nor touched the user's multiplexer.
- **Profile:** the only profile ran [`run-nvim.sh`](neovim-capability-check/run-nvim.sh). It unsets
  `COLORTERM`, so Neovim has to discover truecolor by asking the terminal rather than trusting the
  environment.
- **Probe:** Neovim ran with [`probe.lua`](neovim-capability-check/probe.lua), with no user config
  (`--clean`). The probe records:
  - every terminal reply Neovim receives (`TermResponse`)
  - whether Neovim's own startup turned `termguicolors` on
  - the results of `vim.tty.query`, Neovim's XTGETTCAP client, for every name in the capability
    table plus `TN` and `kcuu1`

  It then quits. `vim.tty.query` sends all of its names in one request, so the probe asks in two
  batches to stay under Ntilde's 16-name limit.

## Results

The complete record of each run (environment, every reply and the probe results) is in [`neovim-capability-check/runs`](neovim-capability-check/runs), one file per run name.

| Run | Session | `termguicolors` set by Neovim's startup | Table names | `Ms` | `TN`, `kcuu1` |
|---|---|---|---|---|---|
| `release` | 0.13.0, direct pane | **off** | all refused | refused | refused |
| `direct` | `main`, direct pane | **on** | all answered | answered | refused |
| `noclip` | `main`, direct pane, `AllowOsc52ClipboardWrite` off | on | all answered | **refused** | refused |
| `persistent` | `main`, persistent pane (`SessionPersistence` `KeepOnClose`) | on | all answered | **refused** | refused |
| `ntilde` | `main`, direct pane, `TERM=xterm-ntilde` | on | all answered | answered | refused |

"All answered" means every table name except `Ms` came back as `DCS 1 + r`. Each value is exactly
the table's string or number, and each flag is a bare name. Neovim decoded the strings itself (for
example `Smulx` as `\E[4:%dm`; its decoder drops `%pN`). "Refused" means `DCS 0 + r ST`.

**What Neovim did with the replies:**

- **Truecolor.** With `COLORTERM` unset and `TERM=xterm-256color`, Neovim's startup asks
  XTGETTCAP for `Tc`, `RGB`, `setrgbf` and `setrgbb`. It also sets a background with SGR
  `48;2;1;2;3` and reads it back with DECRQSS `m`.
  - 0.13.0 refused every one of these queries, so Neovim left `termguicolors` off.
  - `main` answered all four names and replied `DCS 1 $ r 0;48;2;1;2;3m ST` to the DECRQSS, so
    Neovim turned truecolor on by itself.
- **Clipboard.** Neovim's startup also asks for `Ms` (OSC 52). The answer followed the host
  exactly:
  - present in a direct pane with OSC 52 writes allowed
  - absent with the setting off
  - absent in a persistent pane, whose replies come from the multiplexer's parser
- **The terminfo entry.** With `TERM=xterm-ntilde`, Neovim sent no truecolor queries at startup:
  it took truecolor from the entry's `Tc` and only asked for `Ms`. The entry is installed, readable
  and actually used.
- **Persistent sessions.** The persistent pane ran under the dev build's own multiplexer
  (`NTILDE_MUX_SESSION` was set). Its replies matched the direct pane's apart from `Ms`, which
  confirms that the daemon's parser answers.

## Limits

- One application, on one platform (macOS), through Ntilde's local PTY. Native SSH, OpenSSH
  profiles, Windows and Linux were not exercised. Remote persistent sessions were not exercised
  either.
- The check confirms what Neovim learns. Separate tests cover how Ntilde draws the features it then
  uses (truecolor, styled underlines, synchronized output); they were not re-verified here by eye.
- Neovim's own startup sends at most four names per XTGETTCAP request. A plugin that passes more
  than 16 names to `vim.tty.query` in one call would get a single refusal, because Ntilde rejects
  oversized requests as a whole (see [exact-query-audit.md](exact-query-audit.md)).

## Reproducing

1. **Build** `main` with `scripts/build.sh build Ntilde.sln`.
2. **Settings:** create an empty folder for `NTILDE_APPDATA_ROOT`, with a `settings.json` whose
   only profile is local (`"Type": 0`) and runs `/bin/bash` with these arguments:
   `<this folder>/neovim-capability-check/run-nvim.sh <log path> plain`. Use `ntilde` instead of
   `plain` for `TERM=xterm-ntilde`, after compiling the database the script names. Make it the
   `DefaultProfileId`, and set `SessionPersistence` and `AllowOsc52ClipboardWrite` for the run.
3. **Run:** start `src/Ntilde.App/bin/Debug/net10.0/Ntilde` with that `NTILDE_APPDATA_ROOT`.
   Neovim writes the log and quits within about ten seconds.
4. **Clean up:** close the app. After a persistent run, stop the isolated multiplexer with
   `Ntilde mux kill-server --force` under the same `NTILDE_APPDATA_ROOT`.
