# Troubleshooting

## File locations

Ntilde keeps everything in one data folder:

| System | Data folder |
|---|---|
| Windows | `%LOCALAPPDATA%\ntilde` |
| macOS | `~/Library/Application Support/ntilde` |
| Linux | `~/.local/share/ntilde` (`$XDG_DATA_HOME/ntilde` when that is set) |

Set `NTILDE_APPDATA_ROOT` to use another folder instead, for Ntilde, its multiplexer and
`ntilde-mcp` alike.

| What | Where in the data folder |
|---|---|
| Settings and local profiles | `settings.json` (previous version `settings.json.bak`) |
| Themes | `themes/` |
| SSH profiles | `ssh/profiles.json` |
| Native SSH trusted host keys | `ssh/native_known_hosts.json` |
| Workspaces, templates | `workspaces/`, `workspace_templates/` |
| Workspace policy | `policy/workspace_policy.json` |
| Command Assist history, snippets | `command-assist/history.jsonl`, `command-assist/snippets.json` |
| Recordings | `recordings/` |
| Automatic snapshots | `backups/` |
| The saved session, reopened at launch | `sessions/last_session.json` |
| Logs | `logs/` (see below) |
| The multiplexer's files | `mux/` |

Not in the data folder:

- **Saved passwords** are in your system's credential store: Windows Credential Manager, the macOS
  Keychain, or the Secret Service on Linux.
- **The program** is in `%LOCALAPPDATA%\NtildeApp` (Windows installer), `/Applications/Ntilde.app`
  (macOS), or `/usr/lib/ntilde` (`.deb`).
- **On remote hosts**, `ntilde-mux` and its files are under `~/.local/share/ntilde`
  ([On the host](persistent-sessions.md#remote-ssh-tabs-that-keep-running)), and remote shell
  integration is in `~/.ntilde-shell-integration.*`.

## Logs

| Log | What |
|---|---|
| `logs/debug.log` | Ntilde itself. Starts with the version, build and program path. |
| `logs/startup_error.txt` | Written when Ntilde fails to start |
| `logs/mux.log` | The multiplexer |
| `logs/mux-ls-all.log` | Why `ntilde mux ls --all` could not reach a host |
| `logs/workspace_audit.log` | Workspace, template and bundle operations |

Each keeps the previous run's copy as `*.1`. For more detail, start Ntilde with the environment
variable `NTILDE_LOG_LEVEL=debug`, for example from a terminal:

```sh
NTILDE_LOG_LEVEL=debug ntilde                  # Linux; macOS: /Applications/Ntilde.app/Contents/MacOS/Ntilde
$env:NTILDE_LOG_LEVEL = "debug"; ntilde        # PowerShell
```

## Installing and starting

**Windows says "Windows protected your PC".** Ntilde is not code-signed yet: choose **More info →
Run anyway**.

**macOS says Ntilde "cannot be opened".** System Settings → Privacy & Security → **Open Anyway**, or
`xattr -cr /Applications/Ntilde.app`.

**The AppImage does not start** with an error about FUSE. Install FUSE 2 (`sudo apt install fuse`)
or run it with `--appimage-extract-and-run`.

**Ntilde starts but shows the wrong shell, or a shell you installed is missing.** Shells are looked
for only on the first run: add the new one in Settings → Shells & Profiles and **Set as default**.

**Ntilde does not start at all.** Look in `logs/startup_error.txt` and `logs/debug.log`. If your
`settings.json` was damaged, Ntilde keeps it as `settings.json.corrupt` and starts from the backup
copy or the defaults; you can also restore a [snapshot](backup.md#snapshots).

**Typing `ntilde` runs something other than the window.** If `NTILDE_SSH_ASKPASS=1` is set in your
shell, `ntilde` acts as Ntilde's SSH password helper. Unset it.

## Text and fonts

**Prompt icons show as boxes.** Ntilde falls back to its bundled Nerd Font symbols for icons any font
lacks. If some still show as boxes, the prompt theme uses a glyph that font does not have: try one of
the bundled fonts, *JetBrains Mono NL* or *Cascadia Mono PL*.

**Box-drawing borders have gaps or look wrong.** Ntilde draws box-drawing characters itself, so
borders join in any font. *Debug: Box Drawing Test Screen* shows them. If a program's borders look
wrong only in Ntilde, start it with `NTILDE_BOX_PRIMITIVES=0` to draw them from the font instead,
and report it.

**Ligatures do not appear.** Turn on Settings → Terminal → **Programming ligatures**, and pick a
font that has them: the default, *JetBrains Mono NL*, is the no-ligatures cut.

**Keys do not work in a program** (wrong characters, modifiers ignored). Some programs mishandle the
kitty keyboard protocol: set `"EnableKittyKeyboardProtocol": false` in `settings.json` with Ntilde
closed. If a key does nothing at all, it may be an Ntilde shortcut: check Settings → Shortcuts.

**Ctrl+R opens Command Assist instead of my shell's history.** It only does when Command Assist has
history to show. Rebind *Command Assist History* in Settings → Shortcuts, or turn Command Assist off.

**Drawing is slow.** *Pane: Toggle Render HUD* shows frame times; include a screenshot when you
report it. Ntilde has no switch for software rendering.

**Images do not show.** Inline images are not shown in persistent panes: use a tab with persistent
sessions off ([Inline images](terminal.md#inline-images)).

## Persistent sessions

Most messages say what to do; [Persistent sessions](persistent-sessions.md) explains each.

| Message in the pane | What to do |
|---|---|
| `[Multiplexer unavailable — this session will not persist]` | The pane runs a normal shell. Ntilde tries again for panes opened 30 seconds later; `logs/mux.log` says why. |
| `[The running multiplexer is a different version — run 'ntilde mux kill-server --force' to replace it]` | Click **Restart multiplexer now**, or run the command. This ends its shells. |
| `[Multiplexer not reachable — press Enter to retry]` | Press Enter. |
| `[Multiplexer disconnected] [Press Enter to reconnect]` | Press Enter. |
| `[Multiplexer session failed — press Enter to start a new shell]` | That shell cannot be shown again: Enter starts a new one. |
| `[Another multiplexer is running but cannot be reached. …]` | Close other Ntilde windows, or end the old multiplexer: the process id is in `logs/mux.log`. |
| `[Previous session was lost — started a new shell]` | The multiplexer ended since you last used the shell. Nothing to do. |

**Shells are gone after a restart of the computer.** That is expected: the multiplexer runs on your
computer and ends with it. SSH tabs that keep running on their host come back.

**To stop the multiplexer** and every shell in it: *Session: Quit and Close All Shells*, or
`ntilde mux kill-server`.

## SSH

**My `~/.ssh/config` settings are ignored.** Ntilde builds the connection from the profile, and the
OpenSSH backend runs `ssh` with a config of its own ([SSH backends](ssh.md#ssh-backends)). Put the
setting in the profile, or in *Extra SSH arguments* (OpenSSH profiles). **Sync from ~/.ssh/config**
in the Connection Manager imports hosts, users, ports, keys and jump hosts.

**"Host key … has changed".** If you know why the host's key changed (it was reinstalled, say),
accept the new key in the dialog. On an OpenSSH profile, first remove the old key with
`ssh-keygen -R <host>`, then press Enter. If you do not know why, do not connect.

**A native profile refuses to connect** although `ssh` works. Check Settings → SSH → **Native SSH
backend** is on. The native backend ignores *Extra SSH arguments*; switch the profile to OpenSSH if
it needs them. On a profile whose OpenSSH arguments you want logged in full, set
`NTILDE_SSH_FULL_ARGS=1`.

**I have to type my password on every reconnect.** Tick **Remember password** in the password
dialog to save it in your system's credential store. Hosts that ask for a one-time code always need
you.

**Passwords are gone after importing a backup or moving to a new computer.** Passwords are never in a
backup: type each one once.

**SFTP fails on a host that asks for a code.** Transfers cannot answer a second factor: sign in to
that host with a key or your SSH agent.

**Persistent SSH tabs do not survive a disconnect**, and a notification says
*Persistent SSH unavailable*. `ntilde-mux` is missing, out of date or unsupported on that host: use
the notification's **Install** or **Update** button. Supported hosts are Linux x64 and arm64 with
glibc 2.34 or newer, and Apple-silicon macOS.

## Agents

**The agent says Ntilde is not running or has no sessions.** Turn on Settings → Agent Access →
**Agent access (observe)**, and keep Ntilde open. If you set `NTILDE_APPDATA_ROOT` for Ntilde, set
it for `ntilde-mcp` too.

**The agent cannot type into an SSH tab.** Turn on **Agent access (act)**, and tick **Allow AI agent
access to this connection** in that SSH profile.

## Reporting a bug

Open an issue at [github.com/benyblack/ntilde/issues](https://github.com/benyblack/ntilde/issues).
The form asks for:

- what happened and what you expected;
- how to reproduce it, ideally a `printf` of the bytes that trigger it;
- whether another terminal behaves differently;
- your system, Ntilde version, and shell (local, SSH or WSL).

It helps to attach `logs/debug.log`, a [recording](terminal.md#recording-and-replay) of the problem
(check it for secrets first), a screenshot, and for drawing problems the output of
`ntilde --vt-report`.

Report security problems privately, through the repository's **Security** tab, not as an issue.
