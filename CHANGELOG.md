# Changelog

User-facing changes, newest release first. This file starts with 0.12.0: the notes for earlier
releases are on [GitHub Releases](https://github.com/benyblack/ntilde/releases).

## Unreleased

- **Themes reach every part of the app.** Settings, the Connection Manager, the tab strip and
  sidebar, dialogs, the command palette, toasts and everything inside a pane (search, the status
  bar, Command Assist, Remote Files) now follow the theme, live, and stay readable on light themes.
  A profile with its own theme colors its pane's overlays to match its terminal while the rest of
  the app keeps yours. See [1.3 Themes](docs/USER_MANUAL.md#13-themes).
- **Selection, search and the cursor follow the theme.** The fixed blue selection and yellow search
  highlights are gone: they come from the theme, with the text drawn on top so it stays readable. A
  block cursor now shows the character under it.
- **Themes can set ten more colors**: selection, selected text, cursor text, search match, current
  match, accent and the four status colors. Leave them out and Ntilde derives them, so existing
  theme files work unchanged. The theme editor lists them under **Advanced colors**. Dracula,
  Catppuccin Mocha, Tokyo Night, Nord and both Solarized themes now use their upstream selection
  colors.
- **Theme import keeps more and works in more places.** Windows Terminal, iTerm2 and Alacritty
  imports carry selection, cursor-text and search colors. Alacritty imports read their colors at
  all (a quoted `#` color was cut off as a comment), and iTerm2 imports read correctly under
  comma-decimal locales.
- Built-in themes are refreshed on update, overwriting local edits to their files, as before. Save
  a customized built-in theme under a new name to keep it.

- **The MCP server ships with Ntilde.** Every bundle now includes `ntilde-mcp` (`ntilde-mcp.exe`
  on Windows), a native binary next to the app that needs no .NET and updates with it. Connect
  Claude Code with `claude mcp add ntilde -- ntilde-mcp`: the Windows installer, the `.deb`, the
  AUR package and Homebrew put it on `PATH`. Before this, agent access meant cloning and building
  the repository. See [Use with AI agents](README.md#use-with-ai-agents-mcp).
- **Connection multiplexing works with the native SSH backend.** With *Enable connection
  multiplexing* on, a profile's tabs and splits share one connection: the second tab opens at once,
  with no second login. Its port forwards are set up once, with the connection, so a second tab of a
  profile with forwards no longer fails on a port the first one holds. *ControlPersist (sec)* keeps
  the connection open that long after its last tab closes. Persistent tabs, *Remote Files*, path
  completion and SFTP transfers of the profile ride the same connection, so none of them logs in
  again either; with it, persistent tabs on such a profile now run its port forwards.

## 0.12.0

### Persistent sessions and the multiplexer

Shells can now outlive Ntilde's window. They run in a background process, Ntilde's own
multiplexer, and come back when Ntilde starts again. The
[user manual's chapter 12](docs/USER_MANUAL.md#12-persistent-sessions-and-the-multiplexer) covers
it all.

- **Local shells can keep running** when the window closes, when Ntilde crashes and across an update.
  Reopening Ntilde reattaches every tab to its shell, screen and scrollback included. Settings →
  General → *Keep shells running when the window closes* switches it between *Keep running* and
  *Off*.
- **On by default** for local shells. A settings file without `SessionPersistence` - every install
  that never changed it - gets *Keep running*; one that says `"Off"` stays off. SSH tabs keep
  running only for the connections you opt in.
- **The first close asks** whether the shells keep running (*Keep running* or *Close them*, with
  *Don't ask again*). **Session: Quit and Close All Shells** in the command palette, or the link
  under the setting, ends every local shell and the multiplexer; remote shells keep running.
- **After a restart of the computer** the tabs start fresh shells where they were, without a
  warning.
- **Detach and attach.** **Pane: Detach** closes a pane and keeps its shell running.
  **Session: Attach to Session…** opens any running shell in a new tab - a detached one, one another
  window shows, or one on a remote host - and several windows can share a shell.
- **SSH tabs that survive network drops.** Tick *Keep remote sessions running (ntilde-mux)* on an SSH
  connection and install `ntilde-mux` on the host from the same tab of the connection editor. The
  tab's shell then runs on the host: it survives a dropped network, a sleeping laptop and a closed
  window. After a drop the tab reconnects by itself when it can sign in without you (your keys,
  your agent or a saved password), and otherwise waits for Enter. Hosts: Linux on x64 or arm64 with
  glibc 2.34 or newer, and macOS on Apple silicon. *Remote Files* and SFTP transfers work
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
