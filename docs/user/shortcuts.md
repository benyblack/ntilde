# Keyboard shortcuts, the command palette and the mouse

Every shortcut on this page is the default. Settings → **Shortcuts** lists them all and lets you
rebind them ([Rebinding](#rebinding)).

## macOS

On macOS every default that starts with `Ctrl+` uses `Cmd+` instead, with these exceptions:

- **New Tab** is `Cmd+T`, not `Cmd+Shift+T`.
- **Next / Previous tab** stay `Ctrl+Tab` / `Ctrl+Shift+Tab`, because `Cmd+Tab` switches
  applications.
- **Command Assist Toggle** stays `Ctrl+Space`, because `Cmd+Space` opens Spotlight, and
  **Command Assist: Insert Selection** stays `Ctrl+Enter`.
- `Ctrl+C` is always the interrupt on macOS; copy is `Cmd+C`.

Only defaults are translated. A binding you set yourself is used exactly as you wrote it, on every
platform.

## Default shortcuts

### Application and tabs

| Action | Windows / Linux | macOS |
|---|---|---|
| Command Palette | `Ctrl+Shift+P` | `Cmd+Shift+P` |
| Settings | `Ctrl+,` | `Cmd+,` |
| Connection Manager | `Ctrl+Shift+K` | `Cmd+Shift+K` |
| New Tab (default profile) | `Ctrl+Shift+T` | `Cmd+T` |
| Close Tab | `Ctrl+W` | `Cmd+W` |
| Tab: Next (most recently used) | `Ctrl+Tab` | `Ctrl+Tab` |
| Tab: Previous (most recently used) | `Ctrl+Shift+Tab` | `Ctrl+Shift+Tab` |
| Tab: Move Previous | `Ctrl+Shift+PageUp` | `Cmd+Shift+PageUp` |
| Tab: Move Next | `Ctrl+Shift+PageDown` | `Cmd+Shift+PageDown` |
| Tab: Open Tab List | `Ctrl+Shift+O` | `Cmd+Shift+O` |
| Tabs: Toggle Vertical Tab Sidebar | `Ctrl+Shift+L` | `Cmd+Shift+L` |
| Font: Increase | `Ctrl+=` (or numpad `+`) | `Cmd+=` (or numpad `+`) |
| Font: Decrease | `Ctrl+-` (or numpad `-`) | `Cmd+-` (or numpad `-`) |

### Panes

| Action | Windows / Linux | macOS |
|---|---|---|
| Split Vertical (side by side) | `Ctrl+Shift+D` | `Cmd+Shift+D` |
| Split Horizontal (stacked) | `Ctrl+Shift+E` | `Cmd+Shift+E` |
| Close Pane | `Ctrl+Shift+W` | `Cmd+Shift+W` |
| Equalize Panes | `Ctrl+Shift+G` | `Cmd+Shift+G` |
| Pane: Toggle Zoom | `Ctrl+Shift+Z` | `Cmd+Shift+Z` |
| Pane: Toggle Broadcast Input (Tab) | `Ctrl+Shift+B` | `Cmd+Shift+B` |
| Focus pane left / right / up / down | `Alt+Arrow` | `Alt+Arrow` |
| Find in Terminal | `Ctrl+F` or `Ctrl+Shift+F` | `Cmd+F` or `Cmd+Shift+F` |
| Toggle Recording | `Ctrl+Shift+R` | `Cmd+Shift+R` |
| Paste | `Ctrl+V` | `Cmd+V` |
| Copy | `Ctrl+C` with a selection | `Cmd+C` |

`Alt+Arrow` and copy are fixed and cannot be rebound. `Alt+Arrow` goes to the terminal when there
is no pane in that direction. On Windows and Linux, `Ctrl+C` copies only when text is selected (and
then clears the selection); with nothing selected it sends the interrupt to the shell.

### Command Assist

Command Assist is off by default; turn it on in Settings → Command Assist
([Command Assist](command-assist.md)).

| Action | Windows / Linux | macOS |
|---|---|---|
| Command Assist Toggle | `Ctrl+Space` | `Ctrl+Space` |
| Command Assist History | `Ctrl+R` | `Cmd+R` |
| Command Assist Help | `Ctrl+Shift+H` | `Cmd+Shift+H` |
| Command Assist: Pin/Unpin Selection | `Ctrl+Shift+S` | `Cmd+Shift+S` |
| Previous / Next Suggestion | `Up` / `Down` | `Up` / `Down` |
| Accept While Browsing | `Enter` | `Enter` |
| Insert Selection (without running) | `Ctrl+Enter` | `Ctrl+Enter` |
| Close | `Escape` | `Escape` |

History, Help and Pin take the key only when there is something for them to act on; otherwise the
key reaches the shell, so `Ctrl+R` stays your shell's history search while Command Assist has
nothing to show. The last five keys act only while the suggestion surface is open.

### Commands with no default shortcut

These exist only while persistent sessions are on ([Persistent sessions](persistent-sessions.md)).
Bind them under Settings → Shortcuts if you use them often.

| Action | Command id |
|---|---|
| Session: Attach to Session… | `attach_session` |
| Pane: Detach | `detach_pane` |
| Session: Quit and Close All Shells | `quit_close_all_shells` |

### Other fixed keys

| Where | Key | Effect |
|---|---|---|
| Command palette | `Up` / `Down`, `Enter`, `Escape` | Move, run the command, close. Double-click also runs; clicking outside closes. |
| Settings window | `Ctrl+S` / `Cmd+S` | Save and close |
| Settings window | `Ctrl+F` / `Cmd+F` | Search settings; `Up` / `Down` and `Enter` pick a result, `Escape` clears |
| A pane whose session ended or disconnected | `Enter` | Reconnect or start a new shell |
| Windows | ``Alt+` `` | Show or hide the window (quake mode); change it with `GlobalHotkey` ([Settings with no page](settings.md#settings-with-no-page)) |

Keys that are not Ntilde shortcuts go to the shell: `Ctrl+letter` sends the control character,
`Alt+key` sends `Esc` followed by the key, and `Ctrl+Alt` is left alone so AltGr text works.
`Shift+PageUp` / `Shift+PageDown` also go to the shell; scroll the scrollback with the mouse wheel.

## Rebinding

In Settings → **Shortcuts**, rows are grouped by scope (*Application*, *Pane*, *Command Assist*)
and filtered by the search box. Each row shows its category, its scope and its default.

- Click a binding box and press the new chord. Modifier-only presses are ignored; plain `Tab` and
  `Shift+Tab` move focus instead of recording; `Escape` cancels (except for the Command Assist keys
  that use `Escape` themselves).
- A chord another command already uses is refused with "*chord* is already assigned to *command*".
- **Reset** on a row puts its default back. Recording the default does the same.
- The command palette, title-bar tooltips and the Command Assist hint strip show your bindings.
- The five in-surface Command Assist keys can be bound only to `Escape`, `Up`, `Down`, `Enter` or
  `Tab`.

Overrides are stored in `settings.json` under `Keybindings`, command id to chord, only for the
commands you changed:

```json
"Keybindings": {
  "new_tab": "Cmd+Shift+N",
  "detach_pane": "Ctrl+Shift+X"
}
```

Modifiers are `Ctrl`, `Alt`, `Shift` and `Cmd` (also written `Meta`, `Super` or `Win`). A key is a
letter, a digit, `,`, `Plus`, `Minus`, `Space`, `Tab`, `PageUp`, `Enter`, `Escape` or another
Avalonia key name. Modifiers must match exactly: `Ctrl+Shift+T` does not fire on
`Ctrl+Alt+Shift+T`. A binding Ntilde cannot read falls back to the default without a warning, and
Settings refuses to save a file in which two commands share a chord ("Resolve duplicate shortcuts
before saving.").

## The command palette

Open it with `Ctrl+Shift+P` (`Cmd+Shift+P`) and type part of a command's name or category. The
commands you use most, and then most recently, come first; using a command's shortcut counts too.
The right-hand column shows each command's current shortcut. An entry whose name has no prefix of
its own is shown with its category in front, for example *View: Split Vertical*.

| Category | Commands |
|---|---|
| General | New Tab, Close Tab, Close Pane, Tab: Next / Previous (MRU), Tab: Move Previous / Next, Tab: Open Tab List, Tabs: Toggle Vertical Tab Sidebar, Tab: Rename Current, Tab: Copy Current Title, Tab: Close Others (skips pinned and protected tabs), Tab: Toggle Pin, Tab: Toggle Protect, Settings, Connections, Toggle Recording, Open Recording..., Open Recordings Folder, Command Assist: Pin/Unpin Selection, Session: Attach to Session…, Session: Quit and Close All Shells |
| View | Split Vertical, Split Horizontal, Equalize Panes, Pane: Toggle Zoom, Pane: Toggle Broadcast Input (Tab), Pane: Reconnect, Pane: Detach, Focus Pane Left / Right / Up / Down, Pane: Export Snapshot (Plain Text / ANSI / PNG), Pane: Toggle Render HUD, Font: Increase / Decrease, Cursor: Block / Beam / Underline, Cursor: Toggle Blink, Bell: Toggle Audio, Bell: Toggle Visual Flash, Scroll: Toggle Smooth |
| Edit | Find in Terminal, Paste |
| Application | Update: Check for updates, Update: Restart to apply *version* (once an update is ready) |
| Shell | New Tab: *profile* — one per local profile |
| SSH | New Connection (SSH): *profile* — one per SSH profile |
| Tabs | Switch Tab: *title* [*folder* \| *process*] — one per open tab |
| Workspace | Workspace: Save Current, Workspace: Load..., Workspace: Load *name*, Workspace Template: Save Current, Workspace Template: Apply..., Workspace Template: Apply *name*, Tab Rule: Set / Clear Template for Current Profile, Workspace: Export Bundle..., Workspace: Export Current Session Bundle..., Workspace: Import Bundle..., Workspace: Open Bundle... |
| Backup | Export configuration…, Import configuration…, Restore from snapshot… (each opens Settings → Backup & Restore) |
| Remote | SFTP: Toggle Remote Files, SFTP: Upload File... / Upload Folder..., SFTP: Download File... / Download Folder..., SFTP: Show Transfers (only on an SSH pane) |
| Theme | Theme: *name* — one per installed theme |
| Debug | Debug: Box Drawing Test Screen |

Workspace bundle commands are hidden when a workspace policy disallows them
([Tabs, panes and workspaces](tabs-and-panes.md#workspace-policy)), and the session commands are
hidden when persistent sessions are off.

## Mouse

**Selecting.** Drag to select; dragging near the top or bottom edge scrolls. Double-click selects a
word, triple-click a line, and a click without moving clears the selection. While a program has
turned on mouse reporting (`vim` with `mouse=a`, `htop`, `tmux`), clicks, drags and the wheel go to
that program instead, and there is no modifier that overrides it. Ntilde does not copy on select,
and middle-click does not paste.

**Links.** Hovering a link underlines it. `Ctrl+click` (`Cmd+click` on macOS) opens it. Links a
program marks explicitly (OSC 8) win over URLs Ntilde detects in the text; turn detection off with
`EnableLinkDetection` in `settings.json` ([Settings](settings.md#settings-with-no-page)). `http`, `https` and `mailto` links open in your default
application. A `file:` link is revealed in Finder, Explorer or your file manager, never run. Other
schemes are not opened.

**Scrolling.** The wheel scrolls the scrollback, three lines per notch by default
(`WheelLinesPerNotch`), animated when smooth scrolling is on. Under mouse reporting each notch goes to the
program.

**Right-click.**

- On a tab: Close, Close Others, Rename..., Copy Title, Pin / Unpin, Protect / Unprotect.
- On the title bar: Customize Title Bar...
- In an SSH pane: Pane (split, equalize, zoom, broadcast, close), SFTP → Remote Files, and Explain
  Selection when text is selected.

**Other.** Middle-click a tab to close it. Double-click a splitter between panes to make that split
even.

**Dropping files.** Dropping files on a pane types their paths, quoted for the shell (as WSL paths
in a WSL shell). Dropping one text file asks first, with *Paste path* or *Paste contents*. While
the shell is reading a password (echo off), drops are blocked with "Drop blocked (secure input).
Hold Alt to force."; hold `Alt` while dropping to drop anyway. File names with characters the shell
would interpret are refused.

**Pasting.** Paste normalizes line endings and uses bracketed paste when the program asks for it.
When the clipboard holds an image and no text, Ntilde saves it to a temporary PNG and types the
file's path.
