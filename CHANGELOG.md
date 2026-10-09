# Changelog

User-facing changes, newest release first. This file starts with 0.12.0: the notes for earlier
releases are on [GitHub Releases](https://github.com/benyblack/ntilde/releases).

## 0.12.0

### Persistent sessions and the multiplexer

Shells can now outlive Ntilde's window. They run in a background process, Ntilde's own
multiplexer, and come back when Ntilde starts again. The
[user manual's chapter 12](docs/USER_MANUAL.md#12-persistent-sessions-and-the-multiplexer) covers
it all.

- **Local shells can keep running** when the window closes, when Ntilde crashes and across an update.
  Reopening Ntilde reattaches every tab to its shell, screen and scrollback included. Settings →
  Appearance → *Keep shells running when the window closes* switches it between *Keep running* and
  *Off*.
- **On by default** for local shells. A settings file without `SessionPersistence` - every install
  that never changed it - gets *Keep running*; one that says `"Off"` stays off. SSH tabs keep
  running only for the connections you opt in.
- **The first close asks** whether the shells keep running (*Keep running* or *Close them*, with
  *Don't ask again*). **Session: Quit and Close All Shells** in the command palette, or the link
  under the setting, ends every shell and the multiplexer.
- **After a restart of the computer** the tabs start fresh shells where they were, without a
  warning.
- **Detach and attach.** **Pane: Detach** closes a pane and keeps its shell running.
  **Session: Attach to Session…** opens any running shell in a new tab - a detached one, one another
  window shows, or one on a remote host - and several windows can share a shell.
- **SSH tabs that survive network drops.** Tick *Keep remote sessions running (ntilde-mux)* on an SSH
  connection and install `ntilde-mux` on the host from the same tab of the connection editor. The
  tab's shell then runs on the host: it survives a dropped network, a sleeping laptop and a closed
  window, and the tab reconnects by itself, without asking for anything. Hosts: Linux on x64 or
  arm64 with glibc 2.34 or newer, and macOS on Apple silicon. *Remote Files* and SFTP transfers work
  on these tabs (the sidebar with the native SSH backend); port forwards do not.
- **Updates keep your shells** when the new version can talk to the running multiplexer. On Windows
  it runs from its own copy in Ntilde's data folder for this. When it is from an older build, a
  notification offers **Restart multiplexer now**; when an update cannot keep it, Ntilde asks before
  it closes the shells.
- **Command line:** `ntilde mux ls [--all] [--json]`, `ntilde mux attach <id>` (shows a shell in any
  terminal; detach with Ctrl+\ then d), `ntilde mux kill <id>` and `ntilde mux kill-server [--force]`.
- **Windows console launcher.** `ntilde` typed in PowerShell or cmd now runs `ntilde.com`, which
  waits for a command-line mode and returns its exit code, and returns at once for a window. The
  installer adds Ntilde's folder to your user `PATH`.
- **Agents** (MCP, with *Agent access* on) also see the shells no window shows, marked windowless:
  they can read them, and with *Agent access (act)* type into and close them.

The other changes in 0.12.0 are listed in its GitHub release notes.
