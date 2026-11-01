# Changelog

User-facing changes, newest release first. This file starts with 0.12.0: the notes for earlier
releases are on [GitHub Releases](https://github.com/benyblack/ntilde/releases).

## 0.13.0

0.13.0 puts the theme on every part of the app, ships the MCP server in every bundle, lets native
SSH tabs share one connection, adds focus mode, and brings Ntilde up to date with what modern TUIs
ask of a terminal: the full Kitty keyboard protocol, styled and colored underlines, runtime colors
and dark/light reporting.

### Themes

- **Themes reach every part of the app.** Settings, the Connection Manager, the tab strip and
  sidebar, dialogs, the command palette, toasts and everything inside a pane (search, the status
  bar, Command Assist, Remote Files) now follow the theme, live, and stay readable on light themes.
  A profile with its own theme colors its pane's overlays to match its terminal while the rest of
  the app keeps yours. See [Themes and fonts](docs/user/themes-and-fonts.md).
- **Minimum contrast.** Settings → Appearance → *Minimum contrast* (off by default) lightens or
  darkens terminal text that is too close to its background, keeping its hue: for themes whose
  colors clash with their own background, such as GitHub Light, where a prompt drawn on ANSI black
  was unreadable. See [Themes and fonts](docs/user/themes-and-fonts.md#minimum-contrast).
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

### Agents, SSH and the window

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
- **Focus mode.** `Ctrl+Shift+Enter` (`Cmd+Shift+Enter` on macOS), or *View: Toggle Focus Mode*,
  hides the tab strip or sidebar, the side panels and each pane's status bar and scrollbar, leaving
  the current tab's panes under one slim title strip. Every tab keeps running, splits and zoom stay
  as they were, and the same key puts everything back.
- **Keyboard-interactive servers ask for the password once.** On servers that take only
  keyboard-interactive (PAM, some Tailscale hosts), the native backend no longer shows a password
  window the server can never accept before the real prompt. A refused password gets one more
  window that is never filled from the vault, and ticking *remember* there replaces the stale
  saved password.
- **Old plaintext SSH passwords move to the OS credential store.** A settings file from an older
  version that still holds a profile password has it moved to the keyring on first start, checked,
  and only then removed from the file. If the move fails the file is left untouched and Ntilde says
  so at startup. Native SSH also wipes passwords and prompt answers from memory once used.
- **Links show where they go.** Hovering a link shows its target and *Ctrl+click to open*
  (*Cmd+click* on macOS), with hidden or look-alike characters made visible, and underlines the
  whole OSC 8 link, across rows, instead of one cell.
- **Search finds text across soft wraps**, highlighting each row of a wrapped match, and finds
  emoji and combining characters that have scrolled into scrollback.

### Terminal compatibility

- **The full Kitty keyboard protocol.** Programs can now ask for key repeat and release events,
  alternate keys, every key (including modifiers and keypad) and the text a key produces, on top of
  the disambiguation Ntilde already supported.
- **Styled and colored underlines.** Curly, dotted, dashed and double underlines (`SGR 4:1`–`4:5`,
  `SGR 21`) and underline colors (`SGR 58`/`59`), as Neovim and language servers use for
  diagnostics. They survive persistent sessions and recordings, and ANSI exports keep them.
- **Runtime colors.** Programs can change the palette and the foreground, background and cursor
  colors (OSC 4, 10–12, 17, 19, and their resets), and save and restore them with OSC 30001/30101.
- **Dark and light reporting.** Programs can ask whether the background is dark or light
  (`CSI ? 996 n`) and be told when it changes (mode 2031).
- **SIXEL scrolling.** Wide SIXEL images no longer cause extra wraps or overwrite text; DECSDM
  (mode 80) is supported, and images come back when a persistent session is reattached.
- **XTVERSION** (`CSI > q`) answers `Ntilde <version>`; **DECSTR** (`CSI ! p`) soft-resets the
  terminal; mode 1048 saves and restores the cursor; and a program's blinking or steady cursor
  is honored whatever the *Cursor blink* setting.
- **fish 4** commands are no longer marked twice, which had doubled long-command notifications,
  Fix mode runs and agents' `CommandFinished` events.

### Privacy and fixes

- **Recordings no longer contain what you type**, including passwords typed without echo. Program
  output is recorded as before, and older recordings still play their input.
- **Command Assist history hides credentials** passed as token or API-key flags and environment
  assignments. History saved before this version is not rewritten.
- A closed pane frees its glyph atlases at once; scrolling and heavy output redraw with less work;
  native SSH idles without polling.
- A `cmd.exe` profile's own `/k` or `/c` command runs again, and a fresh Windows install starts in
  PowerShell (7, else Windows PowerShell) instead of `cmd.exe`, the shell Ntilde already used
  whenever none was named.
- An agent's SSH or persistent-remote tab fails instead of raising a sign-in dialog, and an agent
  spawn with no profile named always picks a local one and checks the SSH allowlist.
- Legacy SCP transfers keep remote paths with spaces and special characters literal.

The other changes in 0.13.0 are listed in its GitHub release notes.
- **An agent's SSH session no longer opens a dialog over your shell.** When an agent opens a
  native SSH session and the server asks for something only you can give (a host key you have not
  trusted, a password Ntilde has none saved for, a key passphrase, a one-time code), the session ends
  instead of raising a host-key or password dialog that would take your keystrokes. The tab says
  what the server asked, and Enter there connects as you, with the usual dialogs; the agent is told
  why its session ended (`needsUser` in its session status) and carries on in that tab once you
  have signed in. Profiles that keep their sessions running on the remote host follow the same rule
  on either backend. To let agents in on their own, connect to the profile once yourself, trust its
  host key and save its password.
- **A pane whose shell exited comes back for agents.** After Enter restarts or reconnects it, an
  agent's session status starts over instead of staying `exited`.

## 0.12.0

### Persistent sessions and the multiplexer

Shells can now outlive Ntilde's window. They run in a background process, Ntilde's own
multiplexer, and come back when Ntilde starts again. The
[persistent sessions guide](docs/user/persistent-sessions.md) covers
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
