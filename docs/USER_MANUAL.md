# Ntilde User Manual

Welcome to Ntilde, a modern, cross-platform terminal emulator focused on correctness, performance, and predictability. This manual covers every feature currently available to help you maximize your productivity.

## 1. Getting Started
### 1.1 Command Palette
The **Command Palette** is the central hub for accessing all features and commands in Ntilde.
- **Shortcut:** `Ctrl+Shift+P`
- **Usage:** Type any feature name to filter and execute commands instantly.

### 1.2 Settings & Customization
- **Open Settings:** `Ctrl+,` or `Settings` in the palette. Changes apply live
  without requiring a restart. Pages: Appearance, Profiles, Shortcuts, Command
  Assist, Agent Access, SSH, Backup.
- **Themes:** Fourteen themes ship built in — Default, Dracula, Nord, Gruvbox Dark,
  Tokyo Night, Catppuccin Mocha, Solarized Dark/Light, GitHub Dark/Light, Monokai,
  OneHalf Dark/Light and Cobalt2. Switch with `Theme: <name>` in the palette or from
  Settings → Appearance, and import your own theme JSON there too.
- **Font & Sizing:** Increase (`Ctrl++`) or decrease (`Ctrl+-`) the terminal font
  size. You can also customize your preferred font family in Settings.
- **Interface scale:** The terminal font size leaves the rest of the app alone. To
  make tabs, sidebars, dialogs and Settings itself larger on a dense display, use
  Settings → Appearance → Window → *Interface scale* (80%–200%). The main window
  previews it live while you drag; the Settings window itself picks it up the next
  time it opens. The terminal grows with it, on top of its font size, and re-renders
  its glyphs at the new density so text stays sharp. Context menus and tooltips
  currently stay at 100%.
- **Shortcuts:** Every shortcut in this manual is the default. Settings → Shortcuts
  lists them all and lets you rebind them.

---

## 2. Window and Tab Management
Ntilde offers advanced windowing capabilities, including tabs, workspaces, and workspace bundles.

### 2.1 Tab Basics
- **New Tab:** `Ctrl+Shift+T` (Opens the default profile)
- **Close Tab:** `Ctrl+W`
- **Switch Tabs (MRU):** Use `Ctrl+Tab` for the next tab and `Ctrl+Shift+Tab` for the previous tab in Most Recently Used order.
- **Open Tab List:** `Ctrl+Shift+O` (Useful when you have many tabs hidden in overflow).
- **Reorder:** Drag a tab along the strip, or use `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`.
- **Vertical Sidebar:** `Ctrl+Shift+L` swaps the top tab strip for a left sidebar — see 2.3.

### 2.2 Advanced Tab Actions
- **Rename:** Use `Tab: Rename Current` to set a custom title.
- **Pin & Protect:** Use `Tab: Toggle Pin` to pin a tab and `Tab: Toggle Protect` to protect it from accidental closure.
- **Copy Title:** `Tab: Copy Current Title`
- **Close Others:** `Tab: Close Others` removes all tabs except the currently active one.

### 2.3 Vertical Tab Sidebar
Toggle with `Ctrl+Shift+L`, `Tabs: Toggle Vertical Tab Sidebar` in the palette, or
Settings → Appearance → **Tab strip orientation**. Vertical mode trades horizontal
space for a much richer row per tab, which is what you want when tabs are running
long jobs or agents rather than sitting idle.

Each row shows the tab title, a status dot, any marker chips, and the tab's most
recent non-empty output line as a live preview.

The **status dot** paints exactly one state, in this precedence:

| Dot | Meaning |
|---|---|
| Amber | A bell fired, or the tab wants attention |
| Amber (agent) | An agent typed into this tab and you have not looked yet |
| Blue | Output is streaming, or a command is still running |
| Blue (agent) | An agent is reading this tab (only under the "All" rollup policy) |
| *(none)* | Idle |

**Marker chips** trail the title and, unlike the dot, can stack — bell, plain
activity, agent-wrote and agent-watched are tracked separately. Bell and plain
activity are the one mutually exclusive pair. Attention markers clear when you
activate the tab.

Other sidebar behavior:

- **Resize:** drag the sidebar's right edge. The width is remembered.
- **Overflow:** when rows run past the bottom of the sidebar, a pill pinned to the
  bottom edge shows how many tabs are hidden and opens a list of them.
- **Reorder:** drag a row, or use `Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`.

### 2.4 Workspaces & Templates
Workspaces save your exact window state, including tabs, pane splits, and zooming.
- **Save/Load:** `Workspace: Save Current` and `Workspace: Load...`
- **Templates:** Save reusable layouts via `Workspace Template: Save Current` and apply them with `Workspace Template: Apply...`.
- **Profile Rules:** Automatically apply a template whenever a specific profile launches (`Tab Rule: Set Template for Current Profile...`).

### 2.5 Workspace Bundles (Portable Sessions)
Bundles allow exporting and importing tabs and pane layouts as portable `.ntildews.json` files.
- **Exporting:** `Workspace: Export Bundle...` or `Workspace: Export Current Session Bundle...`
- **Import/Open:** `Workspace: Import Bundle...` or `Workspace: Open Bundle...`
*(Note: Enterprise policies may restrict bundle sharing in managed environments).*

---

## 3. Panes and Layouts
Panes allow you to split a single tab into multiple terminal windows.

### 3.1 Splitting and Navigation
- **Split Vertical:** `Ctrl+Shift+D` (Places a new pane side-by-side).
- **Split Horizontal:** `Ctrl+Shift+E` (Places a new pane below).
- **Close Pane:** `Ctrl+Shift+W` (Closes only the active pane, leaving others open).
- **Navigation:** Use `Alt+Left`, `Alt+Right`, `Alt+Up`, `Alt+Down` to move focus between panes.
- **Equalize:** `Ctrl+Shift+G` resets pane sizes to be equal.

### 3.2 Advanced Pane Features
- **Zoom Pane:** `Ctrl+Shift+Z` toggles zooming of the active pane to fill the entire tab temporarily.
- **Broadcast Input:** `Ctrl+Shift+B` toggles sending keystrokes to *all* panes in the current tab simultaneously.
- **Find/Search:** `Ctrl+Shift+F` opens the search overlay for the active pane.

### 3.3 Persistent sessions (multiplexer)
Local shells can keep running when Ntilde's window closes, and come back when Ntilde starts
again. They run inside a small background process, the *multiplexer daemon*
(`Ntilde mux serve`), which Ntilde starts on demand. SSH tabs can persist too, on the remote
host, through a daemon installed there: see [Persistent SSH tabs (remote)](#persistent-ssh-tabs-remote)
below.

- **Turning it on:** Settings → Appearance → *Scrollback* → **Keep shells running when the
  window closes** → *Keep running*. The default is *Off*, and with it off no daemon is ever
  started. The setting applies to panes opened after you save it. Panes that are already open
  stay as they are.
- **What persists:** local shells, with their screen and scrollback. They survive closing the
  window, an Ntilde crash and a restart. On the next launch each saved pane reattaches to its
  shell. A running shell that no saved pane refers to (for example one left over from a
  crash) opens as a new background tab, and a toast reads "Reattached N detached sessions".
- **A second Ntilde window** (a second instance started while the first is open) never takes
  over shells the first one is showing: its panes start new shells instead.
- **Workspaces, templates and bundles** save a layout, not live shells. Loading one starts new
  shells in its panes, and exported bundles contain no session ids. Only Ntilde's own saved
  session reattaches to running shells.
- **What closes a shell:** closing its pane or tab, or the shell exiting. Closing the *window*
  only detaches: the shells keep running in the daemon. A shell that has exited and has no
  window attached is cleaned up after 60 seconds.
- **If the daemon cannot be reached:** a *new* pane starts a normal shell instead and the window
  shows a "Session not persistent" notification:
  `[Multiplexer unavailable — this session will not persist]`. Ntilde tries the daemon again
  for panes opened 30 seconds later. If the running daemon is from a different Ntilde version,
  the notification adds a second line telling you to run `ntilde mux kill-server --force` to
  replace it.
  A pane that is *reattaching* to a saved shell (at startup, say, while the daemon is slow to
  answer) does not start a stand-in shell, because its shell may still be running in the daemon.
  It shows `[Multiplexer not reachable — press Enter to retry]` and keeps the shell's id, so
  Enter tries again and your session file still names the shell. (For a version mismatch the
  same kill-server hint appears under it.)
  If a running daemon goes away, attached panes show
  `[Multiplexer disconnected] [Press Enter to reconnect]`. Enter reconnects, starting a new
  daemon if needed. When the old shell is gone the window shows a "Previous session lost"
  notification: `[Previous session was lost — started a new shell]`. When several panes hit
  the same thing at once (e.g. restoring after the daemon crashed) they share one notification.
  If the daemon stops tracking a shell's screen (its terminal parser failed; `ntilde mux ls`
  shows it as *faulted*), the pane shows
  `[Multiplexer session failed — press Enter to start a new shell]`. That shell cannot be
  shown again: Enter ends it and starts a new one.
- **One pane per shell:** if a saved session names the same shell in two panes (for example a
  hand-edited session file), only the first reattaches; the others start new shells.
- **If the daemon's endpoint breaks:** the daemon keeps retrying it (logging to `logs/mux.log`)
  and keeps serving every window that is already connected, so their shells are never ended
  because of it. Only when it has not accepted a connection for 60 seconds *and* no window is
  connected - so nobody can reach its shells - does it exit, ending those shells. Ntilde then
  starts a fresh daemon the next time it needs one.
- **If the daemon's files are deleted while it runs** (for example the whole data folder): the
  daemon rewrites its endpoint file `mux/mux-endpoint.json` within a second, so windows and
  `ntilde mux` commands find it again. On macOS and Linux a deleted socket cannot be restored; the
  daemon logs that it is unreachable until restarted. While it still holds its lock file but
  cannot be found, a new window cannot start a daemon of its own (`ntilde mux serve` exits with
  code 3: another multiplexer holds the lock), starts normal shells, and shows a *Multiplexer*
  notification once:
  `[Another multiplexer is running but cannot be reached. Shells in this window are not kept. Close other ntilde windows or end the old multiplexer.]`
  (On macOS and Linux, deleting the whole `mux` folder also removes the lock file's name, so a new
  window starts a fresh daemon instead; the old one keeps running unreachable, with its shells.)
  To end an unreachable daemon, end the process whose pid `logs/mux.log` shows, or use
  `ntilde mux kill-server --force` where it still applies (its endpoint file names a live daemon
  this Ntilde cannot talk to).
- **Command line** (from the Ntilde executable, e.g. `ntilde` or `Ntilde.exe`; on Windows,
  `ntilde` in PowerShell or cmd runs `ntilde.com`, see [On Windows: `ntilde.com`](#on-windows-ntildecom)):

  | Command | What it does |
  |---|---|
  | `ntilde mux ls` | Lists sessions: id, state (running / exited *code* / faulted), attached windows, size, title. |
  | `ntilde mux ls --json` | The same list as JSON. |
  | `ntilde mux kill <id>` | Ends one session. |
  | `ntilde mux kill-server` | Ends every session and stops the daemon. Waits up to 5 seconds for it to exit; if it has not, prints "Multiplexer did not stop within 5 s." and exits with code 1. |
  | `ntilde mux kill-server --force` | Also stops a daemon this Ntilde cannot talk to at all (no protocol version in common), once its pid and process name are re-verified — see below. A daemon from the previous version still talks to it, so plain `kill-server` stops that one. |
  | `ntilde mux attach <id\|prefix> [--read-only]` | Shows a session in this terminal — see below. |

  These commands never start a daemon. With none running they print "No multiplexer is
  running." and exit with code 1 (`attach` exits with code 2, its code for any connection
  error). The daemon writes its log to `logs/mux.log` in Ntilde's data folder.
- **Limitations:**
  - SSH panes persist only when their profile opts in, on the remote host (see
    [Persistent SSH tabs (remote)](#persistent-ssh-tabs-remote)). Every other SSH pane works
    exactly as before and does not persist.
  - Inline images (sixel, kitty graphics) are not shown in persistent panes.
  - Applying an update keeps persistent sessions running when the new version can use the
    running daemon: Ntilde restarts and reattaches to them. When it cannot — the new version
    speaks another multiplexer protocol, or, on Windows, the daemon runs from the install folder
    (an earlier Ntilde version started it there), which the update replaces — Ntilde asks first
    ("N multiplexed sessions will be closed by the update (the new version cannot keep them)",
    buttons *Close sessions and update* / *Cancel*) and leaves the update unapplied if you
    decline. While a daemon runs from the install folder, a downloaded update is not applied
    automatically when Ntilde starts; apply it from the update toast or the command palette so
    Ntilde can ask first. With the setting off, an update never keeps a running daemon: Ntilde
    asks first ("N multiplexed sessions will be closed by the update") and shuts it down, and
    while any daemon is running a downloaded update is not applied automatically at start.
  - If the daemon crashes, or is killed, its shells are gone.
  - A shell inherits the daemon's environment, not the window's. The daemon's environment is
    the one Ntilde had when it first started the daemon.
  - Turning the setting off does not stop sessions that are already running. Panes open with
    normal shells from then on, and the next time Ntilde saves your session it forgets which
    panes the running sessions belonged to. Use `ntilde mux kill-server` to end them.
  - The daemon exits by itself 10 minutes after its last session and its last connection
    close.
- **Security:** the daemon listens only on a local endpoint that only your user account can
  open (a per-user named pipe on Windows, a socket in a private `0700` folder on macOS and
  Linux). It never opens a network port.

#### Sharing a shell between windows

A persistent shell can be attached in more than one pane at once, so every attached window shows
the same screen and any of them can send input.

- **"Session: Attach to Session…"** in the command palette opens a picker. The entry appears only
  while persistent sessions are on. It has no default shortcut; bind one under Settings →
  Shortcuts (`attach_session`).
- The picker lists every session the daemon knows about: title, command, folder, size, how many
  windows are attached, and running (or exited *code*).
- The chosen shell opens in a new tab — from this window, a second Ntilde window, or a second
  instance. A "shared with N" badge appears over the pane and its tab carries a ⧉ marker. N counts
  every *other* attached window, including a read-only `ntilde mux attach --read-only` viewer.
- If that session is already open as a tab in this window, Ntilde focuses the existing tab instead
  of opening a second copy of it — one session cannot be shown in two tabs of the same window yet.
- An already-exited session can be attached too: it opens showing its last screen and the exit
  banner, and nothing closes it for you.
- If the session is gone by the time the attach completes (another window, or `ntilde mux kill`,
  beat you to it), no shell is started: the tab closes, and an "Attach to session" notification
  reads `[The shell you chose has ended]`.
- **Restoring a shell at launch** is never a share: the pane only gets its shell back if no other
  window has it open, and the daemon decides that. Otherwise Ntilde starts a fresh shell in that
  pane and shows a "Previous shell in use" notification: `[Your previous shell is open in another
  window — started a new shell]`. A crashed shell reopened automatically as a background tab that
  another window claims first just closes its tab, with no new shell and no notification. Against
  a daemon from before this version, Ntilde still makes that check on its own side first, the
  Phase 2 behavior.

#### Detach versus close

- **"Pane: Detach"** (no default shortcut; bind `detach_pane` under Settings → Shortcuts) closes
  only this pane and leaves the shell running in the daemon. Bring it back with "Attach to
  session…".
- Closing a pane or tab normally ends its shell.
- When another window is attached to the same shell, closing asks **Close** (ends the shell for
  every window), **Detach** (closes just this pane, keeps the shell running), or **Cancel**.
- An agent closing a pane through MCP cannot answer that prompt, so when another window is
  attached the pane detaches instead, and the shell keeps running for the other window.
- A shell ended from another window shows `[Shell ended from another window]`.
- **Detached shells stay detached.** A shell you detach on purpose — "Pane: Detach", Detach from
  the close prompt, or **Ctrl+\ then d** in `ntilde mux attach` — is *not* reopened the next time
  Ntilde starts; reopen it yourself with
  "Attach to session…". Once per launch, if any such shells exist, a toast reads "N detached
  shells are running — Attach to session… to reopen them" (singular for one: "1 detached shell is
  running — Attach to session… to reopen it"). `ntilde mux ls` marks them `running, detached`, and
  `--json` adds `"detachedByUser": true`. Shells orphaned by a crash are unaffected — they are
  still reopened automatically as background tabs. Against a daemon from before this version, a
  detached shell is still reopened at the next launch, until you replace the daemon: stop it with
  `ntilde mux kill-server` (it still talks to this version, so no `--force` is needed) and Ntilde
  starts a current one when it next needs one.

#### Size

The latest resize wins, from whichever attached window sent it last. A window that is not in
control — its own size does not match the shared grid — letterboxes: its content is clipped or
padded to fit, rather than resizing the shared shell out from under whoever it just took the size
from. It takes the size back when you focus or activate it, for every attached window. A read-only
`ntilde mux attach --read-only` viewer never resizes the session.

#### `ntilde mux attach <id|prefix> [--read-only]`

Shows a multiplexer session in this terminal, without going through the GUI.

- `<id|prefix>` is the session id from `ntilde mux ls`, or a unique prefix of at least 4
  characters.
- It renders from its own copy of the session's screen, so the shell's own escape-sequence
  queries (cursor position, device attributes, terminal capabilities) never reach the terminal
  you ran `mux attach` from.
- Detach with **Ctrl+\ then d**. Ctrl may stay held for the d (Ctrl+\ then Ctrl+D), as in GNU
  screen; the one thing this costs is that a literal Ctrl+\ followed by Ctrl+D cannot be sent.
  **Ctrl+\ Ctrl+\** sends a literal Ctrl+\, and a paste that contains Ctrl+\ then d (or Ctrl+D)
  detaches the same way.
- When the session is bigger than your terminal, the view is clipped and a status line reads
  "session is WxH, this terminal is WxH — resize to fit", with the detach hint kept alongside it.
- `--read-only` shows the session without sending input or resizing it, and the status line reads
  "read-only". **This is a convenience, not a security boundary** — the endpoint has no
  authentication beyond the same-user check, so anyone who can run programs as you can attach
  normally anyway.
- It refuses (exit code `2`) to attach to the session you are typing in: every shell the daemon
  starts has `NTILDE_MUX_SESSION` set to its own session id, and attaching a session to itself
  would loop. Attaching to a *different* session from inside one works.
- Exit codes: `0` you detached, `1` the session exited or was killed, `2` a usage or connection
  error.
- On Windows, run it straight from PowerShell or cmd: `ntilde mux attach <id>`. `ntilde` there is
  `ntilde.com`, which keeps the prompt waiting until you detach (see
  [On Windows: `ntilde.com`](#on-windows-ntildecom)).
- On a remote host, `ntilde-mux attach <id>` does the same for the shells of a persistent SSH tab
  (see [Persistent SSH tabs (remote)](#persistent-ssh-tabs-remote)).

#### `ntilde mux kill-server --force`

Stops a daemon this Ntilde has no protocol version in common with — a much older or a newer one —
once its pid and process name are re-verified against the endpoint descriptor, ending its shells.
Without `--force` against such a daemon, `kill-server` reports the mismatch and exits 1 instead of
guessing. A daemon from the previous version (v1) negotiates with this one, so a plain
`kill-server` stops it and `--force` changes nothing.

#### Limits

- A daemon from before this version (v1) keeps working for spawn, attach, detach and kill, but
  shows no sharing indicator, and `--read-only` needs a daemon from this version or later.
  Deliberately detached shells are re-adopted at the next launch against a v1 daemon (see above).
  A plain `ntilde mux kill-server` replaces it: it stops the v1 daemon, and the next launch
  starts a current one.

#### Persistent SSH tabs (remote)

An SSH tab can survive a network drop, a laptop going to sleep, or Ntilde closing. Its shell runs
on the remote host inside `ntilde-mux`, a multiplexer daemon installed in your home folder there,
and Ntilde talks to that daemon through the SSH connection itself (it runs
`ntilde-mux proxy --stdio` over SSH; neither side opens a port). When the connection drops, the
shell and the programs in it keep running on the host. When the connection comes back, the tab
reattaches and shows the current screen and scrollback.

**Turning it on** takes two switches:

1. persistent sessions: Settings → Appearance → *Scrollback* → **Keep shells running when the
   window closes** → *Keep running*;
2. per SSH profile: **Keep remote sessions running (ntilde-mux)** in the connection editor's
   *Reliability* tab.

With either one off, the profile opens plain SSH tabs, exactly as before. Native and OpenSSH
profiles both work. The change applies to tabs opened after you save it. Edits to a persistent
profile's host, port, user or jump hosts apply to its persistent tabs once all of that profile's
tabs in the window are closed: until then they, and new tabs of the profile, keep connecting where
they first connected, since that is where their shells run. Other edits - a new key, extra SSH
arguments, the backend - apply from the next reconnect.

**Installing `ntilde-mux` on a host.** Each host needs it once. **Install ntilde-mux on this
host…**, next to the checkbox, saves the profile and opens the install dialog (the
**Install ntilde-mux on <user@host>…** button on a *Persistent SSH unavailable* notification opens
the same dialog for that host). The dialog connects with the profile's usual prompts, checks the host's system, and puts
the binary at `~/.local/share/ntilde/bin/ntilde-mux`. It needs no root and changes nothing else on
the host: not your `PATH`, not your shell's startup files. It has three ways to get the binary:

- **Install** downloads `ntilde-mux-<platform>` for this Ntilde version from the project's GitHub
  release, checks it against the hash built into this Ntilde (a development build checks it against the
  release's `.sha256` file instead), and keeps the checked copy in Ntilde's
  data folder (`cache/ntilde-mux/<version>/<platform>`), so the next host of the same platform
  installs without downloading. The macOS `ntilde-mux` is signed and notarized; a copy you drag in
  through Finder from a browser download still gets Gatekeeper's prompt the first time, because of the
  quarantine attribute the browser sets, not because of the binary.
- **Choose file…** uploads an `ntilde-mux` you already have. Ntilde shows the file's SHA-256, and
  refuses the file before uploading it when it is built for another platform than the host's.
- **Copy install command** copies a one-line command that you run in a shell on the host
  yourself: it downloads the same release file with `curl` on the host, checks it with
  `sha256sum -c` (`shasum -a 256 -c` on macOS), and installs it to the same place. Use it when your
  machine cannot reach GitHub but the host can, or when you would rather install by hand.

When this Ntilde version has no release (a development build), **Install** says so and the dialog
leaves only **Choose file…**. The upload replaces an installed `ntilde-mux` only once the new file
has arrived complete and has run once on the host, so a cancelled or broken upload leaves the old
one in place. A daemon that is already running keeps running the binary it started with. When the
install succeeds, the dialog offers to tick the profile's checkbox. The editor's status line shows
what was installed: `ntilde-mux 0.11.0 installed`, `ntilde-mux not installed`, or
`ntilde-mux 0.10.0 installed — this app is 0.11.0`. The versions do not have to match: Ntilde and
the daemon agree on a protocol version when they connect, and only a daemon with no protocol
version in common asks for an update.

**Supported hosts:** Linux on x86-64 or arm64 with glibc 2.34 or newer (for example Ubuntu 22.04,
Debian 12 and later), and macOS on Apple silicon. The dialog refuses, with the reason, musl-based
systems such as Alpine, glibc older than 2.34, FreeBSD, OpenBSD and other systems, and Intel Macs.
Windows hosts are not supported.

**What survives a disconnect:** the remote shell and every program running in it (an editor, a
build, `top`), and the screen and scrollback the daemon keeps for it. Closing Ntilde's window only
detaches, as for local shells: on the next launch each saved tab connects to its host again and
reattaches to its shell, under the same rule as local tabs (a shell open in another window is not
taken over).

**What you see:**

- While a tab connects: `[Connecting to <user@host>…]`. Keys are ignored until it is connected,
  because the connection may be waiting for you to answer a prompt (for up to two minutes). Tabs of
  the same profile share one connection, so you answer its prompts once.
- When the connection drops: `[Connection to <user@host> lost — reconnecting…]`. The tab keeps its
  last screen. A connection that breaks is noticed at once; one that just goes silent is noticed
  within about 25 seconds (a ping every 15 seconds, 10 seconds to answer).
- While it reconnects, **what you type is not sent** to the shell, and nothing is queued to arrive
  later. The first key you press writes `[Input is not sent while reconnecting]`. Enter tries to
  reconnect at once.
- Ntilde retries on its own, waiting about 1, 2, 4, 8 and 16 seconds and then about 30 seconds
  between attempts, for up to 10 minutes after the drop. When the host answers, the tab reattaches
  and the current screen replaces the banners.
- After 10 minutes it stops: `[Connection to <user@host> lost] [Press Enter to reconnect]`.
- If the daemon itself ended (it was killed, or the host rebooted):
  `[ntilde-mux on <user@host> stopped] [Press Enter to reconnect]`. Its shells ended with it. Enter
  starts a new daemon and a new shell, and a *Previous session lost* notification reads
  `[Previous session was lost — started a new shell]`.
- If a new tab cannot reach the host over SSH, or a restored tab's host is down:
  `[<user@host> not reachable — press Enter to retry]`. A restored tab keeps its shell's id, so
  Enter reattaches once the host is back.
- If SSH works but `ntilde-mux` cannot be used (it is not installed, the host is not supported, it
  has no protocol version in common with this Ntilde, or the proxy failed), a *new* tab opens as a
  plain SSH tab, and a *Persistent SSH unavailable* notification reads
  `[<user@host>: <reason> — this tab will not survive a disconnect]`. When `ntilde-mux` is missing
  or incompatible, the notification has an **Install ntilde-mux on <user@host>…** or **Update
  ntilde-mux on <user@host>…** button. When several hosts' notices arrive together they share one
  notification, with a line per host and the button of the last one, named for its host.

**Reconnecting on its own never asks you anything.** The automatic retries use only what needs no
answer: your keys, your SSH agent, the profile's password if it is saved in the vault (*Remember
password* at a password prompt; the Connection Manager shows it), and, on native profiles, a
password or key passphrase that already signed this window in to the host (typed, or taken from the
vault). Ntilde keeps the last in memory only, and forgets it when the window closes or when the
server rejects it. Host keys must already be trusted.

If the host's key is unknown or has changed, the retries stop after one attempt and send no
password. The tab shows `[Connection to <user@host> lost] [Press Enter to reconnect]` with
`[Host key for <user@host> is unknown or has changed — press Enter to review]` under it, and Enter
shows the usual host key question. OpenSSH refuses a *changed* key outright, so there the line is
`[Host key for <user@host> has changed — if you trust the new key, remove the old one from known_hosts, then press Enter]`:
fix `known_hosts` first, then press Enter.

With OpenSSH older than 8.4 (Windows 10's built-in `ssh` is 8.1, Ubuntu 20.04's is 8.2), the
retries use only keys and the agent, never the saved password: a password-only host waits for
Enter after every drop. Put a newer OpenSSH first on your PATH, or use the native SSH backend, to
have the saved password tried on its own.

A saved password is tried **once**. If the server refuses it, the retries stop at once and the tab
shows `[Connection to <user@host> lost] [Press Enter to reconnect]` with
`[The saved password was refused — press Enter to sign in]` under it. Ntilde does not use that
password on its own again, for that host in this window: Enter asks you for the password straight
away; tick *Remember password* to replace the saved one, and Ntilde uses the new one from then on.
Signing in with a typed password without ticking it leaves the refused one unused. A connection
that drops *after* you are signed in is not a refusal: the retries go on and try the saved
password again. (Each window keeps its own record, so a second window with tabs on the same host
tries it once too.) At any other time, when you connect or press Enter Ntilde fills in the saved password at most
once per connection: if the server refuses it, you are asked.

If the host asks for more than the password - a one-time code, a second factor - the saved password
alone cannot sign in. The retries stop at once with
`[Automatic reconnect can't sign in without you — press Enter]`, and later ones do not try the saved
password again. Enter fills in the saved password for you and asks only for the code.
Ntilde fills only a prompt that ends in `password:` or reads `Password for <account>:`. A password
prompt in other words is not filled; the retries stop with
`[Automatic reconnect can't sign in without you — press Enter]`, and Enter then asks you for it.

When signing in would need a password or another typed answer, the retries stop at once rather than
fail again and again (failed logins that fail2ban and account lockouts count), and the tab shows
`[Connection to <user@host> lost] [Press Enter to reconnect]` with
`[Automatic reconnect can't sign in without you — press Enter]` under it. Enter connects with the
usual prompts. This happens for:

- profiles that sign in with a password that is not saved in the vault (and, on native profiles,
  has not been used in this window yet), or whose OpenSSH is older than 8.4: OpenSSH retries run
  `ssh` with `BatchMode=yes`;
- native profiles whose key has a passphrase that has not been used in this window yet, when
  neither the SSH agent nor another key signs in instead;
- profiles that go through jump hosts and sign in with a password: a password prompt may come from
  the jump host, so Ntilde never sends a saved or remembered password along a jump chain on its own.

**Closing, detaching, and turning it off:**

- Closing a tab or pane ends its remote shell. If the host cannot be reached at that moment, the
  kill waits and is sent the next time Ntilde connects to that host; Ntilde also tries once in the
  background, without prompting.
- Once no tab of the window uses a host any more (none shows a shell there, none waits to reattach
  one), Ntilde closes its connection to that host, after any such kill has gone out: it stops
  checking the link and reconnecting, and forgets a password it remembered for the host. If a kill
  cannot be sent because reconnecting gave up, the connection waits, idle, and sends it the next
  time you open a tab on that host. The daemon then exits on its own 10 minutes after its last
  shell has ended.
- **Pane: Detach** leaves the remote shell running, but *Attach to session…* lists local shells
  only. Reattach a detached remote shell on the host with `ntilde-mux attach <id>`: the *Shell
  detached* notification names the host and gives the command with the shell's id, the one
  `ntilde-mux ls` lists.
- Unticking the profile's checkbox makes its new tabs, and its saved tabs at the next launch, open
  plain SSH. Their shells keep running on the host, and Ntilde keeps their ids in its saved
  session, so once the checkbox is ticked again they reattach at the following launch. Closing such
  a tab ends its shell on the host when Ntilde can sign in there without asking you anything (your
  keys or SSH agent): it connects once in the background, without prompting, to send the kill. For
  the profiles listed above that need a password, that attempt fails and the shell keeps running: the
  kill waits until Ntilde next connects to that host in this window (tick the checkbox again and open
  a tab on it), and is dropped when the window closes. To end them all at once, run
  `ntilde-mux kill-server` on the host.

**Limitations:**

- No SFTP sidebar (*Remote Files*), no SFTP transfers and no port forwards on a persistent remote
  tab: *Remote Files*, its transfers and the palette's *SFTP: Upload…* and *SFTP: Download…*
  commands show a notification, "Not available on a persistent remote tab", and the profile's
  forwards are not set up. Use a plain SSH tab of the same host (from a profile with the checkbox off) for those.
- Windows hosts are not supported, nor are the hosts the installer refuses (see *Supported hosts*).
- Linux hosts where systemd-logind ends a user's processes at logout (`KillUserProcesses=yes` in
  `/etc/systemd/logind.conf`) end the daemon and its shells when your last SSH session closes. Run
  `loginctl enable-linger $USER` on the host once to keep them.
- Remote shells inherit the environment of the SSH connection that started the daemon. If you
  forward your SSH agent (for example `-A` in the profile's extra SSH arguments), `SSH_AUTH_SOCK` in
  those shells names that connection's agent socket, which goes away when that connection drops:
  after a reconnect, `git` or `ssh` inside them cannot reach your agent. A new daemon
  (`ntilde-mux kill-server`, which ends its shells) starts with a fresh environment.
- The daemon exits by itself 10 minutes after its last shell has ended and its last connection has
  closed.

**On the host.** `ntilde-mux` is not on the host's `PATH`: run it by its path,
`~/.local/share/ntilde/bin/ntilde-mux`, or add that folder to your `PATH`.

| Command | What it does |
|---|---|
| `ntilde-mux ls [--json]` | Lists the daemon's sessions, as `ntilde mux ls` does. |
| `ntilde-mux attach <id\|prefix> [--read-only]` | Shows a session in the terminal you run it from, as `ntilde mux attach` does (detach with **Ctrl+\ then d**). It works while the Ntilde tab is attached too: both show the same shell, and the tab shows a "shared with 1" badge. |
| `ntilde-mux kill <id>` | Ends one session. |
| `ntilde-mux kill-server [--force]` | Ends every session and stops the daemon. |
| `ntilde-mux --version [--json]` | Prints the version. |

`serve` and `proxy --stdio` are what Ntilde runs; you do not need them. The daemon keeps its files
in a folder of its own: `~/.local/share/ntilde/ntilde-mux` on Linux,
`~/Library/Application Support/ntilde/ntilde-mux` on macOS. Its log is `logs/mux.log` there. If you
also run the Ntilde app on that host, its own multiplexer uses `~/.local/share/ntilde` (or
`~/Library/Application Support/ntilde`) itself, so the two never share a daemon: `ntilde-mux ls`
lists only the shells of your persistent SSH tabs, and `ntilde mux ls` only the app's own. As on
your own machine, only your user can open the daemon's socket, and the daemon never opens a network
port.

#### On Windows: `ntilde.com`

`Ntilde.exe` is a windowed program, so PowerShell and cmd do not wait for it, and a command-line
mode run straight from the prompt would compete with the prompt for your keys. Ntilde therefore
ships a small console launcher, `ntilde.com`, next to `Ntilde.exe`. Windows tries `.com` before
`.exe`, so typing `ntilde` runs the launcher, which:

- starts `Ntilde.exe` in the same console, with your arguments passed through unchanged;
- for a command-line mode (`ntilde mux …`, `ntilde backup …` and the others), waits for it to finish
  and returns its exit code. So `ntilde mux attach <id>` works straight from PowerShell, and
  `$LASTEXITCODE` holds its exit code;
- leaves Ctrl+C and Ctrl+Break to the program instead of ending itself: in `mux attach`, Ctrl+C
  reaches the attached shell;
- for a plain `ntilde` that opens a window, returns at once with exit code 0, so the prompt is not
  held while the window is open.

The installer adds Ntilde's install folder to your user `PATH`, each update keeps it there, and
uninstalling removes it, so `ntilde` works in any terminal opened afterwards (a terminal that was
already open keeps its old `PATH`). The release's `.zip` bundle contains `ntilde.com` too, but
adds nothing to `PATH`.

---

## 4. Command Assist

Command Assist suggests completions, recalls history, and proposes fixes. It is
anchored to what the shell actually reports through OSC 133 shell-integration
marks rather than to a guess about where the prompt ends, and it reads the live
command line from the terminal grid rather than from a shadow copy of your
keystrokes — so it stays correct through re-prompts, wrapped lines and edits.

Everything below runs locally. There is no network call.

### 4.1 Using it
- **Toggle:** `Ctrl+Space`
- **History search:** `Ctrl+R` — accepting a row replaces what you had typed
- **Help:** `Ctrl+Shift+H`
- **Pin / unpin the selection as a snippet:** `Ctrl+Shift+S`
- **While the surface is open:** `Up` / `Down` to browse, `Enter` to accept the
  browsed row, `Ctrl+Enter` to insert it without running, `Escape` to close

A passive suggestion bubble appears as you type; its hint strip shows the current
key names, so it stays honest if you rebind them in Settings → Shortcuts. The
whole surface auto-hides while a fullscreen TUI owns the grid.

### 4.2 Fix mode
When a command fails, Command Assist can propose a correction derived from that
command's *own* output rather than from a generic rule — it captures the failing
output for exactly this purpose.

### 4.3 Knowledge and snippets
- A tldr-derived command catalogue supplies descriptions and common invocations.
- Pinning turns any suggestion or history entry into a saved snippet; snippets are
  managed from the same surface.
- History is stored locally as append-only JSONL and scoped to the context you ran
  the command in.

### 4.4 Shell integration
Marks come from a small shell hook (bash, zsh, fish and PowerShell are supported).
Settings → Command Assist offers a **one-line installer** you can paste on a remote
host, and mark detection works over SSH. In sessions that emit no marks at all,
Command Assist still captures straight-through-typed commands — you lose the
anchoring, not the feature.

---

## 5. Agent Output Panel

Coding agents print a lot of markdown into a terminal, where it renders as raw
`##` and backticks. The Agent Output panel renders that output properly, beside
the pane, while it streams.

### 5.1 Opening it
An **MD** button fades in at the top-right of a pane when its recent output looks
like markdown; click it to open the panel. If you open the panel with nothing
tracked yet, it snapshots the latest response already on screen (prompt lines
trimmed) so you are not looking at an empty pane.

The panel is per-pane, and it stays down while a fullscreen program owns the grid
— reopening on its own when you leave. Your toggle survives that suppression.

### 5.2 The panel header
- **Render** — renders fenced blocks as formatted nested documents. Turn it off to
  see fences as plain code. Diff fences get diff-aware rendering either way.
- **Copy** — copies the raw markdown source, not the rendered text.
- **✕** — closes the panel.

---

## 6. Remote Connections (SSH & SFTP)
Ntilde supports high-performance SSH sessions integrated directly into the terminal, with built-in remote file management.

### 6.1 SSH Profiles and Connection Manager
- Open the **Connection Manager** with `Ctrl+Shift+K` (or the toolbar button). It is a
  real window, so you can leave it open alongside the terminal, and it can show,
  clear, or delete a profile's saved password.
- Easily maintain local and SSH profiles in your Settings.
- **Security:** Credentials use secure platform vault backends. No unexpected password injections triggered by terminal output are allowed for your safety. Fast reconnects and config caching simplify remote work.

#### SSH backends

Ntilde ships two SSH backends:

- **Native SSH** — an in-process SSH client with its own host-key trust store. It is
  the **default for new profiles**, and supports password, identity-file and
  ssh-agent authentication, jump chains of any length, and local, remote and dynamic
  port forwarding. It does not silently fall back to OpenSSH if it fails.
- **OpenSSH** — drives the system `ssh` binary. Still selectable per profile, and
  still the default for profiles you created before the switch.

Settings → SSH holds the global native toggle. With it off, new profiles default to
OpenSSH instead, so the default can never point at a backend that will refuse to
run. Ntilde warns you if a native profile carries mux options or extra SSH
arguments that only the OpenSSH backend understands. See `docs/SSH_ROADMAP.md` for
the full capability matrix.

A profile can also keep its tabs' shells running on the host across network drops,
with both backends: see [Persistent SSH tabs (remote)](#persistent-ssh-tabs-remote) in
§3.3.

### 6.2 Built-in SFTP Transfers
Access the following commands via the palette to transfer files and folders between your local machine and the SSH host:
- `SFTP: Upload File...` / `SFTP: Upload Folder...`
- `SFTP: Download File...` / `SFTP: Download Folder...`
- `SFTP: Show Transfers`: Toggles the Transfer Center overlay to monitor ongoing transfers.

*(Note: SFTP commands only function when the active pane is an SSH session).*

Transfer behavior depends on the SSH backend:

- **OpenSSH profiles** use the system `scp` executable.
- **Native SSH profiles** use Ntilde's built-in native SFTP path for file and folder upload/download.

Native SSH panes also expose a pane-local `Remote Files` sidebar from the pane context menu. The sidebar is lightly navigable, opens from the pane's current remote directory when available, and keeps upload/download actions grounded in the directory or entry you are looking at. The compact rail shows the active host identity, current remote path, and per-entry modified dates so you can quickly spot recently changed files. `Upload File` and `Upload Folder` target the directory currently shown in the sidebar, while `Download Selected` uses the selected remote file or folder.

Current Native SSH transfer notes:

- Transfers use the native backend's known-hosts store and do not silently fall back to OpenSSH.
- Password and identity-file authentication are supported for non-interactive transfers.
- The transfer dialog offers remote path autocomplete when the same profile already has an active Native SSH session.
- Sidebar-initiated uploads go straight to a local file or folder picker, then start the transfer directly into the sidebar's current remote directory.
- Sidebar-initiated file downloads use a save picker with the remote filename prefilled; sidebar folder downloads use a local folder picker.
- Manual `SFTP:` command-palette flows still use the transfer dialog, including remote path autocomplete when an active Native SSH session is available.
- The `Remote Files` sidebar hides automatically while alternate-screen/fullscreen terminal applications are active.
- Single-file transfers show live byte progress when the total size is known.
- Folder transfers report per-file progress callbacks; they do not show a precomputed total for the entire tree.
- Cancellation is supported from the Transfer Center for native transfers.

---

## 7. Terminal Engine & UI Behavior

### 7.1 Scrolling and Cursor
- **Smooth Scrolling:** Toggle via `Scroll: Toggle Smooth`.
- **Cursor Styles:** Choose between `Cursor: Block`, `Cursor: Beam`, or `Cursor: Underline`.
- **Cursor Blink:** Toggle blinking on and off (`Cursor: Toggle Blink`). Note that TUI apps like `vim` or `yazi` may control their own blinking phase.

### 7.2 Visual & Audio Feedback
- **Audio Bell:** `Bell: Toggle Audio`.
- **Visual Bell:** `Bell: Toggle Visual Flash`.
- **Tab Indicators:** Tabs show status icons such as `•` (background activity), `🔔` (bell/attention), `✓` / `✖` (exit status), `📌` (pinned), and `🔒` (protected). The vertical sidebar shows a richer set, including agent activity — see 2.3.

### 7.3 Advanced Graphics Support
Ntilde supports rendering rich images inline natively:
- **Sixel Graphics** (via `libsixel`, `lsix`)
- **iTerm2 Inline Images** (via `imgcat`)
- **Kitty Graphics Protocol**

---

## 8. Configuration Backup & Restore

Export your Ntilde configuration to a single portable `.ntildebackup` file and
import it on another machine. Open **Settings → Backup**, or use `Export
configuration…`, `Import configuration…` or `Restore from snapshot…` in the palette
— all three route to that page, because export and import need a file picker and a
mode prompt, and restore needs its confirmation.

### 8.1 What a bundle contains
Six independently selectable categories: **Settings**, **Themes**, **Connections**,
**Workspaces**, **Policy** and **Snippets**. A bundle is a zip with a manifest, so
you can inspect one before importing it.

> Connection **passwords are not in a bundle**. They live in the OS credential
> store, not in the config folder, so imported SSH profiles need their passwords
> re-entered on first connect. A bundle does carry each profile's
> "remember password" preference.

### 8.2 Import modes and snapshots
Import either **merges** into your current configuration or **replaces** it.

Ntilde also takes automatic snapshots in the background: it watches the
backed-up paths and writes a snapshot once changes go quiet, deduplicated by
content hash and capped by a retention limit. A snapshot is taken immediately
before every import and every restore, so both are reversible from
**Restore from snapshot…**.

### 8.3 From the command line
```
Ntilde.Cli backup export <path>
Ntilde.Cli backup import <path> --merge | --replace
Ntilde.Cli backup list
Ntilde.Cli backup restore <id>
```

Agents get read-only `export` and `list` through MCP, never import or restore.

---

## 9. Agent Access (MCP)

Ntilde can expose your live terminal sessions to AI coding agents over a
local [Model Context Protocol](https://modelcontextprotocol.io) server. **Every
part of this is off by default** — with the toggles off there is no live endpoint
at all, and the server still answers the offline repo/documentation tools.

Settings → **Agent Access**:

- **Agent access (observe)** — the master switch. Lets an agent list sessions, read
  the screen and scrollback, query status, and wait for events.
- **Replay export** — additionally lets an agent save a session's recent output as a
  replay file. Exports contain output and window resizes only, never anything you
  type. Requires observe.
- **Agent screenshots** — additionally lets an agent render a pane to a PNG. A
  picture shows everything drawn in the pane, inline images included. Requires
  observe; every capture is journaled.
- **Agent access (act)** — additionally lets an agent type into, open and close
  sessions. SSH connections must *also* be allowlisted individually. Requires
  observe; every action is journaled.

### 9.1 Seeing what an agent did
Panes carry a live indicator while an agent is observing or acting on them, and
every acting call — allowed or denied — plus every screenshot is recorded in the
**agent activity journal**. Open it from the title-bar menu → **Agent Activity...**,
or put the Agent Activity button on the title bar from Settings → Appearance.

For an agent's own view of a session, `export_replay` plus
`Ntilde.Cli --replay <file>` re-renders it deterministically, frame by frame.

---

## 10. Exporting and Debugging
Tools designed for diagnosing visual issues, performance profiling, and saving session output.

### 10.1 Snapshot Export
Export the current terminal state containing text, colors, and styles.
- **Plain Text:** `Pane: Export Snapshot (Plain Text)`
- **ANSI:** `Pane: Export Snapshot (ANSI)` (Preserves original styling & colors).
- **PNG:** `Pane: Export Snapshot (PNG)`

### 10.2 Session Recording
Capture the active pane's session to a replayable recording.
- **Toggle Recording:** `Ctrl+Shift+R` (or the toolbar record button, or `Toggle Recording` in the command palette) starts/stops recording the active pane.
- **Open a recording:** `Open Recording...` from the command palette.
- **Browse recordings:** `Open Recordings Folder`.

### 10.3 Render Performance HUD
- **Toggle Render HUD:** Enables a real-time overlay showing frame time, dirty rows/cells, draw calls, and glyph cache hit rates. Use this when experiencing degraded visual performance.

### 10.4 Debug Screens
- **Box Drawing Test:** Accessible via `Debug: Box Drawing Test Screen` to verify font rendering, gaps, and line alignments.

### 10.5 VT Conformance Report CLI

Ntilde ships a machine-readable VT conformance report derived from its
coverage matrix. Run the terminal executable with:

- `Ntilde.Cli --vt-report` — concise summary (matrix path, support-status counts, validation counts).
- `Ntilde.Cli --vt-report --json` — full machine-readable JSON report.

On Windows, prefer the console-side executable for interactive shell use. The GUI app `Ntilde.exe` is intended for normal windowed startup, while `Ntilde.Cli.exe` is the reliable VT-report entrypoint from PowerShell or `cmd`.

This is useful when filing compatibility bug reports or comparing against
another terminal emulator's claims.

---

## 11. Updates

How Ntilde updates depends on how you installed it:

- **Windows installer** and the **macOS `.pkg`** check in the background. A new
  version downloads quietly and is applied when you accept the prompt and restart —
  never a surprise restart. On macOS, if the app lives in `/Applications`, the OS
  asks for your password once per update.
- **Linux AppImage** updates itself by rewriting the AppImage, so keep it somewhere
  writable such as `~/Applications`.
- **Debian/Ubuntu package** installs update through your package manager; the in-app
  updater is inactive there by design.
- **Portable zip / tarball** builds do not update themselves.

From the palette: `Update: Check for updates`, and once a version is staged,
`Update: Restart to apply <version>`. Automatic checks can be turned off in
Settings.
