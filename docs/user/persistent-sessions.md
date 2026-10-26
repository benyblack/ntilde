# Persistent sessions and the multiplexer

Ntilde can keep your shells running when its window closes and give them back when it starts
again. The shells run in a background process, the *multiplexer*, instead of in the window, so
closing the window, an Ntilde crash or an update leaves them running. SSH tabs can keep running
too, on the remote host, through a multiplexer installed there ([Remote SSH tabs that keep running](#remote-ssh-tabs-that-keep-running)).

The setting is Settings → General → *Sessions* → **Keep shells running when the window
closes**: *Keep running* or *Off*. It applies to tabs and panes opened after you save it; the ones
already open stay as they are. This page describes *Keep running*; [Turning it off](#turning-it-off) says what *Off*
changes.

*Keep running* is the default for local shells. A settings file that never set it - every install
that did not change it, a new one included - gets *Keep running*; one that says *Off* stays *Off*,
updates included. SSH tabs keep running on their host only for the connections you opt in ([Remote SSH tabs that keep running](#remote-ssh-tabs-that-keep-running)).

## What runs

- **The multiplexer** is a background process, `Ntilde mux serve`, which Ntilde starts the first
  time a pane needs it. One runs per user (per data folder), and every Ntilde window, and
  `ntilde mux attach`, connects to it. Installed with the Windows installer, it runs from a copy of
  Ntilde in the data folder, `bin\<version>\`, so that an update can replace the installed one
  under it ([Updates](#updates)).
- **What it keeps:** each local shell, with its screen and scrollback. A shell keeps running when
  its window closes, when Ntilde crashes and across an update. It ends when it exits, when you
  close its pane or tab, or when the multiplexer ends, which a restart of the computer does ([Reopening Ntilde](#reopening-ntilde)).
- **Seeing it:** `ntilde mux ls` lists the shells ([The command line](#the-command-line)), and **Session: Attach to Session…** in the
  command palette lists them in Ntilde ([Detach, close, and Attach to session…](#detach-close-and-attach-to-session)). The multiplexer writes its log to `logs/mux.log` in
  Ntilde's data folder, and keeps the previous run's as `logs/mux.log.1` (Ntilde's own `debug.log`
  does the same).
- **It exits by itself** 10 minutes after its last shell has ended and its last connection has
  closed. A shell that has exited with no window attached is cleaned up after 60 seconds.
- **Security:** the multiplexer listens only on a local endpoint that only your user account can
  open (a per-user named pipe on Windows, a socket in a private `0700` folder on macOS and Linux). It
  never opens a network port.
- **Environment:** a shell inherits the multiplexer's environment, not the window's: the one Ntilde
  had when it first started the multiplexer.
- **Limits:** inline images (sixel, kitty graphics) are not shown in persistent panes. If the
  multiplexer crashes or is killed, its shells are gone.

### When the multiplexer cannot be reached

- A *new* pane starts a normal shell instead, and the window shows a *Session not persistent*
  notification: `[Multiplexer unavailable — this session will not persist]`. Ntilde tries the
  multiplexer again for panes opened 30 seconds later. If the running multiplexer is from an Ntilde
  version it has no protocol in common with, the notification adds
  `[The running multiplexer is a different version — run 'ntilde mux kill-server --force' to replace it]`
  and a **Restart multiplexer now** button that does it for you ([Updates](#updates)).
- A pane that is *reattaching* to a saved shell (at startup, say, while the multiplexer is slow to
  answer) does not start a stand-in shell, because its shell may still be running. It shows
  `[Multiplexer not reachable — press Enter to retry]` and keeps the shell's id, so Enter tries again
  and your session file still names the shell.
- If a running multiplexer goes away, attached panes show
  `[Multiplexer disconnected] [Press Enter to reconnect]`. Enter reconnects, starting a new
  multiplexer if needed. When the old shell is gone, the pane starts a new one ([Reopening Ntilde](#reopening-ntilde)).
- If the multiplexer stops tracking a shell's screen (its terminal parser failed; `ntilde mux ls`
  shows it as *faulted*), the pane shows
  `[Multiplexer session failed — press Enter to start a new shell]`. That shell cannot be shown
  again: Enter ends it and starts a new one.
- **If the multiplexer's endpoint breaks**, it keeps retrying it (logging to `logs/mux.log`) and
  keeps serving every window that is already connected, so their shells are never ended because of
  it. Only when it has not accepted a connection for 60 seconds *and* no window is connected - so
  nobody can reach its shells - does it exit, ending those shells. Ntilde then starts a fresh
  multiplexer the next time it needs one.
- **If its files are deleted while it runs** (for example the whole data folder), it rewrites its
  endpoint file `mux/mux-endpoint.json` within a second, so windows and `ntilde mux` commands find it
  again. On macOS and Linux a deleted socket cannot be restored; the multiplexer logs that it is
  unreachable until restarted. While it still holds its lock file but cannot be found, a new window
  cannot start a multiplexer of its own (`ntilde mux serve` exits with code 3: another multiplexer
  holds the lock), starts normal shells, and shows a *Multiplexer* notification once:
  `[Another multiplexer is running but cannot be reached. Shells in this window are not kept. Close other ntilde windows or end the old multiplexer.]`
  (On macOS and Linux, deleting the whole `mux` folder also removes the lock file's name, so a new
  window starts a fresh multiplexer instead; the old one keeps running unreachable, with its
  shells.) To end an unreachable multiplexer, end the process whose pid `logs/mux.log` shows, or use
  `ntilde mux kill-server --force` where it still applies (its endpoint file names a live
  multiplexer this Ntilde cannot talk to).

## Closing the window

Closing a pane or a tab ends its shell, as in any terminal ([Detach, close, and Attach to session…](#detach-close-and-attach-to-session) has the exceptions for shells
another window shares). Closing the *window* only detaches: its shells keep running in the
multiplexer, and the next launch reattaches them ([Reopening Ntilde](#reopening-ntilde)).

**The first-close question.** The first time you close a window with shells running in it, Ntilde
asks before the window goes. The dialog, *Close Ntilde*, reads "Your shells keep running in the
background." and "Reopen Ntilde to get them back." (followed by the number of shells when there are
several):

- **Keep running** closes the window and leaves the shells running. Enter answers this, wherever the
  focus is.
- **Close them** ends this window's shells, then closes the window. A shell that another window, or
  an `ntilde mux attach` without `--read-only`, also has open keeps running for it.
- Escape, or closing the dialog, leaves the window open.

Tick **Don't ask again** and Ntilde remembers the answer and applies it from then on without
asking. The answer is kept in the file `mux-close-choice` in Ntilde's data folder, not in your
settings. Changing **Keep shells running when the window closes** in Settings, or importing or
restoring settings that change it, forgets it, so you are asked again.

- Ntilde asks only when local shells would be left running. Remote shells never ask: they keep
  running on their host either way.
- The shells of tabs you have not opened since Ntilde restored them count as this window's too, and
  *Close them* ends them - except one you joined through **Attach to session…**, or one another
  window or `ntilde mux attach` has open. Ntilde asks the multiplexer which those are first; if it
  does not answer, they keep running.
- On macOS, quitting with Cmd+Q asks the same question, and *Don't ask again* works there too.
  *Cancel* keeps Ntilde running. Logging out, restarting or shutting down the Mac never asks.
- When the computer shuts down or you sign out, Ntilde ends no shell itself, whatever you chose
  (the shutdown ends them anyway, [Reopening Ntilde](#reopening-ntilde)).
- Shells that *Close them* ended are not looked for at the next launch: their panes start fresh
  shells, with no "previous session lost" notice.

**Quit and close all shells.** To end everything at once, use **Session: Quit and Close All
Shells** in the command palette, or the **Quit and close all shells…** link under the setting in
Settings, which closes Settings without saving. Both ask first when shells are running, in a
*Quit Ntilde* dialog ("Close
every shell?", "N shells running in the background will be closed, including detached and shared
ones.", with "Remote shells keep running." added when the window has remote persistent tabs, and a
**Quit and close** button). Then Ntilde ends every shell in the multiplexer - this window's, other
windows', and detached ones - stops the multiplexer and closes the window. Remote shells keep
running. The next launch starts fresh shells, with no "previous session lost" notice. The command
has no default shortcut; bind `quit_close_all_shells` under Settings → Shortcuts.

## Reopening Ntilde

- **What comes back:** each pane of your saved session reattaches to its shell, with its screen and
  scrollback, tabs in the background included. No notification is shown for that.
- **Shells no saved pane names** - left behind by a crash, say - open as new background tabs, and a
  *Sessions restored* notification reads "Reattached N detached sessions".
- **Shells you detached on purpose** are not reopened ([Detach, close, and Attach to session…](#detach-close-and-attach-to-session)). Once per launch, if any are running, a
  *Detached shells* notification reads "N detached shells are running — Attach to session… to
  reopen them" ("1 detached shell is running — Attach to session… to reopen it" for one).
- **After a restart of the computer** the shells are gone: they ended with the multiplexer. Ntilde
  starts a fresh shell in each saved pane, where it was, with no warning. On Windows the same goes
  after you sign out and back in, and after *Shut down* with Fast Startup on, which signs you out.
  Ntilde tells by the time your session was saved: a session saved before the computer started (on
  Windows, before the later of the start and your sign-in) names shells that cannot be running any
  more. When the system does not say when it started, Ntilde does not assume a restart, and a lost
  shell is announced as below. Remote SSH tabs reattach as usual: their shells ran on their host.
- **When a shell is gone for another reason** (the multiplexer crashed or was killed since the
  computer started), its pane starts a fresh shell and writes
  `[Previous session was lost — started a new shell]`, and a *Previous session lost* notification
  says so. Panes that hit this together share one notification. A tab you opened from *Attach to
  session…* is not given a new shell: it closes, and an *Attach to session* notification reads
  `[The shell you chose has ended]`, after a restart of the computer too.
- **Reopening never takes a shell from another window.** A pane gets its shell back only if no other
  window has it open; otherwise it starts a fresh shell, and a *Previous shell in use* notification
  reads `[Your previous shell is open in another window — started a new shell]`. A crash leftover
  that another window claims first just closes its tab, with no new shell and no notification. So a
  second Ntilde (a second instance started while the first is open) never takes over the first one's
  shells: its panes start new ones.
- **One pane per shell:** if a saved session names the same shell in two panes (for example a
  hand-edited session file), only the first reattaches; the others start new shells.
- **Workspaces, templates and bundles** save a layout, not live shells. Loading one starts new
  shells in its panes, and exported bundles contain no session ids. Only Ntilde's own saved session
  reattaches to running shells.

## Detach, close, and Attach to session…

**Closing.** Closing a pane or tab ends its shell, with these exceptions:

- When another window is attached to the same shell, closing asks **Close** (ends the shell for
  every window), **Detach** (closes just this pane, keeps the shell running), or **Cancel**.
- An agent closing a pane through MCP cannot answer that question, so when another window is
  attached the pane detaches instead, and the shell keeps running for the other window.
- A tab you opened from *Attach to session…* whose connection is down or reconnecting, or whose
  attach has not finished yet, cannot tell whether another window still uses the shell. Closing it
  detaches, without asking, and a *Shell detached* notification says the shell kept running.
- A shell ended from another window shows `[Shell ended from another window]`.

**Detaching.** **Pane: Detach** (no default shortcut; bind `detach_pane` under Settings →
Shortcuts) closes the pane and leaves its shell running. A *Shell detached* notification says how to
get it back: "Shell kept running — Attach to session… to get it back", or for a shell on a remote
host "Shell kept running on user@host — Attach to session… reopens it". When that host's profile no
longer keeps its remote sessions, the picker does not offer the host, and the notification gives
the command to run there instead: "Shell kept running on user@host — run 'ntilde-mux attach <id>'
on that host to get it back". `ntilde-mux` is not on the host's `PATH`, so run it by its path
([On the host](#remote-ssh-tabs-that-keep-running)).

**Detached shells stay detached.** A shell you detach on purpose - **Pane: Detach**, Detach in the
close question, or **Ctrl+\ then d** in `ntilde mux attach` - is *not* reopened the next time Ntilde
starts: reopen it with *Attach to session…*. `ntilde mux ls` marks such shells `running, detached`,
and `--json` adds `"detachedByUser": true`. Shells left behind by a crash are different: they are
reopened as background tabs ([Reopening Ntilde](#reopening-ntilde)).

**Attach to session….** **Session: Attach to Session…** in the command palette (no default
shortcut; bind `attach_session` under Settings → Shortcuts) opens the *Attach to Session* dialog:
"Attach to a running session. It opens in a new tab and stays shared with its other windows."

- Each line is a shell: its title (or its command when it has none), its command and folder, its
  size, how many windows have it open (or `detached`), and `running` or `exited <code>`. `open here`
  marks one this window already shows. Running shells come first. Shells whose screen the
  multiplexer lost (*faulted*) are not listed.
- This computer's shells come first, then those of every remote host the window is connected to,
  each line starting with the host: `[user@host]`.
- For each SSH profile with **Keep remote sessions running (ntilde-mux)** ticked whose host the
  window has no connection to now, the picker offers *Connect to user@host… (starts its multiplexer
  if none is running)*. Choosing it connects, starting the host's multiplexer if none runs there
  (with no shells to keep, it exits after 10 idle minutes), asking for a password if the host needs one, with a *Connecting to user@host…* notification
  meanwhile, and then reopens the picker with that host's shells. If it cannot connect, a
  notification reads "Could not connect to user@host."
- A host whose shells cannot be listed shows as `[user@host] not reachable: <reason>`, where the
  reason is "the connection failed", "it did not answer in time", "its multiplexer is not usable" or
  "the multiplexer is restarting", and it does not hold up the others. When there is nothing to
  choose, a notification gives those lines, or "No sessions are running in the multiplexer."
- The chosen shell opens in a new tab, shared with the windows that already show it ([Several windows and `ntilde mux attach`](#several-windows-and-ntilde-mux-attach)). If this
  window already shows it, Ntilde switches to that tab instead: one shell is not shown in two tabs of
  the same window.
- An exited shell can be chosen too: it opens on its last screen and its exit banner, and nothing
  closes it for you.
- If the shell is gone by the time the attach completes (another window, or `ntilde mux kill`, beat
  you to it), no shell is started: the tab closes, and an *Attach to session* notification reads
  `[The shell you chose has ended]`. If a remote shell's profile stopped keeping its sessions while
  the picker was open, the notification reads "The profile for user@host no longer keeps remote
  sessions running."

## Several windows and `ntilde mux attach`

A shell can be open in more than one window at once - another Ntilde window, a second instance, or
`ntilde mux attach` in any terminal. Every one of them shows the same screen, and any of them can
type.

- A pane whose shell another window also shows has a "shared with N" badge, and its tab carries a ⧉
  marker. N counts every *other* attached window, a read-only `ntilde mux attach --read-only` viewer
  included.
- **Size:** the latest resize wins, from whichever window sent it last. A window that is not in
  control - its own size does not match the shared grid - letterboxes: its content is clipped or
  padded to fit, instead of resizing the shared shell out from under whoever just took the size from
  it. It takes the size back when you focus or activate it, for every attached window. A read-only
  viewer never resizes the shell.

### `ntilde mux attach <id|prefix> [--read-only]`

Shows a shell of the multiplexer in this terminal, without going through Ntilde's window.

- `<id|prefix>` is the session id from `ntilde mux ls`, or a unique prefix of at least 4
  characters.
- It renders from its own copy of the shell's screen, so the shell's own escape-sequence queries
  (cursor position, device attributes, terminal capabilities) never reach the terminal you ran
  `mux attach` from.
- Detach with **Ctrl+\ then d**. Ctrl may stay held for the d (Ctrl+\ then Ctrl+D), as in GNU
  screen; the one thing this costs is that a literal Ctrl+\ followed by Ctrl+D cannot be sent.
  **Ctrl+\ Ctrl+\** sends a literal Ctrl+\, and a paste that contains Ctrl+\ then d (or Ctrl+D)
  detaches the same way.
- When the shell is bigger than your terminal, the view is clipped and a status line reads
  "session is WxH, this terminal is WxH — resize to fit", with the detach hint kept alongside it.
- `--read-only` shows the shell without sending input or resizing it, and the status line reads
  "read-only". **This is a convenience, not a security boundary**: the endpoint has no
  authentication beyond the same-user check, so anyone who can run programs as you can attach
  normally anyway.
- It refuses (exit code `2`) to attach to the shell you are typing in: every shell the multiplexer
  starts has `NTILDE_MUX_SESSION` set to its own session id, and attaching a shell to itself would
  loop. Attaching to a *different* shell from inside one works.
- Exit codes: `0` you detached, `1` the shell exited or was killed, `2` a usage or connection
  error.
- On Windows, run it straight from PowerShell or cmd: `ntilde mux attach <id>`. `ntilde` there is
  `ntilde.com`, which keeps the prompt waiting until you detach ([`ntilde.com` on Windows](#ntildecom-on-windows)).
- On a remote host, `ntilde-mux attach <id>` does the same for the shells of a persistent SSH tab
  ([Remote SSH tabs that keep running](#remote-ssh-tabs-that-keep-running)).

## Remote SSH tabs that keep running

An SSH tab can survive a network drop, a laptop going to sleep, or Ntilde closing. Its shell runs
on the remote host inside `ntilde-mux`, a multiplexer installed in your home folder there, and
Ntilde talks to it through the SSH connection itself (it runs `ntilde-mux proxy --stdio` over SSH;
neither side opens a port). When the connection drops, the shell and the programs in it keep running
on the host. When the connection comes back, the tab reattaches and shows the current screen and
scrollback.

**Turning it on** takes two switches:

1. Ntilde's own setting, **Keep shells running when the window closes**, on *Keep running* (the
   start of this page);
2. per SSH profile: **Keep remote sessions running (ntilde-mux)** in the connection editor's
   *Reliability* tab.

With either one off, the profile opens plain SSH tabs, exactly as before. Native and OpenSSH
profiles both work. The change applies to tabs opened after you save it. Edits to a persistent
profile's host, port, user or jump hosts apply to its persistent tabs once all of that profile's
tabs in the window are closed: until then they, and new tabs of the profile, keep connecting where
they first connected, since that is where their shells run. Other edits - a new key, extra SSH
arguments, the backend - apply from the next reconnect.

**Installing `ntilde-mux` on a host.** Each host needs it once. **Install ntilde-mux on this
host…**, under the checkbox, saves the profile and opens the install dialog (the
**Install ntilde-mux on <user@host>…** button on a *Persistent SSH unavailable* notification opens
the same dialog for that host). The dialog connects with the profile's usual prompts, checks the
host's system, and puts the binary at `~/.local/share/ntilde/bin/ntilde-mux`, or at
`$XDG_DATA_HOME/ntilde/bin/ntilde-mux` when the host sets `XDG_DATA_HOME` to an absolute path. It
needs no root and changes nothing else on the host: not your `PATH`, not your shell's startup files.
It has three ways to get the binary:

- **Install** downloads `ntilde-mux-<platform>` for this Ntilde version from the project's GitHub
  release, checks it against the hash built into this Ntilde (a development build checks it against
  the release's `.sha256` file instead), and keeps the checked copy in Ntilde's data folder
  (`cache/ntilde-mux/<version>/<platform>`), so the next host of the same platform installs without
  downloading. The macOS `ntilde-mux` is signed and notarized; a copy you drag in through Finder
  from a browser download still gets Gatekeeper's prompt the first time, because of the quarantine
  attribute the browser sets, not because of the binary.
- **Choose file…** uploads an `ntilde-mux` you already have. Ntilde shows the file's SHA-256, and
  refuses the file before uploading it when it is built for another platform than the host's.
- **Copy install command** copies a one-line command that you run in a shell on the host yourself:
  it downloads the same release file with `curl` on the host, checks it with `sha256sum -c`
  (`shasum -a 256 -c` on macOS), installs it to the same place, and prints where it put it. Use it
  when your machine cannot reach GitHub but the host can, or when you would rather install by hand.
  If you set `XDG_DATA_HOME` only in your interactive shell's startup files, the command installs
  under it while Ntilde's own connection, which does not read them, looks in `~/.local/share`: then
  install from the dialog instead.

When this Ntilde version has no release (a development build), **Install** says so and the dialog
leaves only **Choose file…**. The upload replaces an installed `ntilde-mux` only once the new file
has arrived complete and has run once on the host, so a cancelled or broken upload leaves the old
one in place. A multiplexer that is already running keeps running the binary it started with, and
so do its shells: installing never stops it. Updating Ntilde itself changes nothing on your hosts.
Once per launch, when Ntilde connects to an `ntilde-mux`, a *Multiplexer* notification may say one
of two things:

- The running `ntilde-mux` is older than the version installed on the host (you installed a newer
  one while it ran): "ntilde-mux on <user@host> is from a previous version (0.12.0); restart it when
  convenient — this closes its 2 shells." (The last clause is left out when it runs no shells.) Its
  **Restart ntilde-mux on <user@host>** button asks first, then stops that multiplexer - and only it;
  if the connection is down, or by then it is no longer behind the installed version, nothing is
  sent and a notification says so. Its tabs show
  `[ntilde-mux on <user@host> stopped] [Press Enter to reconnect]`, and Enter starts the installed
  version once the old one has stopped. Once a restart has stopped it, the notice is not offered
  again for that host until the next launch or the next install.
- The running `ntilde-mux` is older than this Ntilde and not behind the installed one - the same
  version, or Ntilde has no record of what is installed (an install by hand, say): "ntilde-mux on
  <user@host> is older (0.12.0) than this app (0.12.1); update it when convenient. Updating replaces
  the binary; your shells keep running until you restart it." Its **Update ntilde-mux on
  <user@host>…** button opens this dialog. No restart is offered then, since it would start the same
  old version again; once the update is installed, the restart notice replaces it, at once while
  Ntilde is connected to the host.

What counts as installed is what this connection recorded when Ntilde installed `ntilde-mux`. A
running version newer than that record (installed from another computer or another connection to
the same host) is never offered a restart.

When the install succeeds, the dialog offers to tick the
profile's checkbox. The editor's status line shows what was installed:
`ntilde-mux 0.12.1 installed`, `ntilde-mux not installed`, or `ntilde-mux 0.12.0 installed — this app is 0.12.1`. The
versions do not have to match: Ntilde and `ntilde-mux` agree on a protocol version when they
connect, and only one with no protocol version in common asks for an update.

**Supported hosts:** Linux on x86-64 or arm64 with glibc 2.34 or newer (for example Ubuntu 22.04,
Debian 12 and later), and macOS on Apple silicon. The dialog refuses, with the reason, musl-based
systems such as Alpine, glibc older than 2.34, FreeBSD, OpenBSD and other systems, and Intel Macs.
Windows hosts are not supported.

**What survives a disconnect:** the remote shell and every program running in it (an editor, a
build, `top`), and the screen and scrollback `ntilde-mux` keeps for it. Closing Ntilde's window only
detaches, as for local shells: on the next launch each saved tab connects to its host again and
reattaches to its shell, under the same rule as local tabs (a shell open in another window is not
taken over). A restart of your own computer does not end them: they run on the host.

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
- If `ntilde-mux` itself ended (it was killed, or the host rebooted):
  `[ntilde-mux on <user@host> stopped] [Press Enter to reconnect]`. Its shells ended with it. Enter
  starts a new `ntilde-mux` and a new shell, and a *Previous session lost* notification reads
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
tries it once too.) At any other time, when you connect or press Enter Ntilde fills in the saved
password at most once per connection: if the server refuses it, you are asked.

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
  When you connect yourself through a jump host with the OpenSSH backend, Ntilde fills in the saved
  password only where the prompt cannot be the jump host's. With an OpenSSH older than 8.4, a jump
  host given in the profile's extra SSH arguments, or a jump host with the target's user and host
  name, you type it.

**Closing, detaching, and turning it off:**

- Closing a tab or pane ends its remote shell (with the exceptions in [Detach, close, and Attach to session…](#detach-close-and-attach-to-session)). If the host cannot be
  reached at that moment, the kill waits and is sent the next time Ntilde connects to that host;
  Ntilde also tries once in the background, without prompting.
- Once no tab of the window uses a host any more (none shows a shell there, none waits to reattach
  one), Ntilde closes its connection to that host, after any such kill has gone out: it stops
  checking the link and reconnecting, and forgets a password it remembered for the host. If a kill
  cannot be sent because reconnecting gave up, the connection waits, idle, and sends it the next
  time you open a tab on that host. `ntilde-mux` then exits on its own 10 minutes after its last
  shell has ended.
- **Pane: Detach** leaves the remote shell running, and *Attach to session…* brings it back: the
  picker lists the shells of every host the window is connected to, and offers *Connect to
  user@host…* for the others ([Detach, close, and Attach to session…](#detach-close-and-attach-to-session)).
- Unticking the profile's checkbox makes its new tabs, and its saved tabs at the next launch, open
  plain SSH. Their shells keep running on the host, and Ntilde keeps their ids in its saved
  session, so once the checkbox is ticked again they reattach at the following launch. Closing such
  a tab ends its shell on the host when Ntilde can sign in there without asking you anything (your
  keys or SSH agent): it connects once in the background, without prompting, to send the kill. For
  the profiles listed above that need a password, that attempt fails and the shell keeps running: the
  kill waits until Ntilde next connects to that host in this window (tick the checkbox again and open
  a tab on it), and is dropped when the window closes. To end them all at once, run
  `ntilde-mux kill-server` on the host.

**What works on a persistent tab:**

- *Remote Files* and the palette's *SFTP: Upload…* and *SFTP: Download…* work as on a plain SSH tab,
  each on a connection of its own. That connection signs in without asking, as on a plain tab; with
  the native backend it can also use a password you typed to sign this window in to that host (not
  on a profile that goes through jump hosts). With the OpenSSH backend only the palette's transfers
  work: there is no *Remote Files* sidebar, as on a plain OpenSSH tab.
- After you change the profile's host, port, user or jump hosts, a tab opened before still runs on
  the old host, and *Remote Files*, its transfers and the palette's SFTP commands are refused there
  with "Not available while this tab still runs on the host it was opened on — reopen the tab to use
  the new host" (an open *Remote Files* sidebar closes).
- **No port forwards**, unless the profile uses the native backend with connection multiplexing on:
  then a persistent tab rides the profile's shared connection, which carries its forwards, as a plain
  tab's does (see [SSH backends](ssh.md#ssh-backends)). Otherwise use a plain SSH tab of the same host (from
  a profile with the checkbox off) for those. The connection editor says so under the checkbox.

**Limitations:**

- Windows hosts are not supported, nor are the hosts the installer refuses (see *Supported hosts*).
- Linux hosts where systemd-logind ends a user's processes at logout (`KillUserProcesses=yes` in
  `/etc/systemd/logind.conf`) end `ntilde-mux` and its shells when your last SSH session closes. Run
  `loginctl enable-linger $USER` on the host once to keep them.
- Remote shells inherit the environment of the SSH connection that started `ntilde-mux`, with one
  exception: if you forward your SSH agent (for example `-A` in the profile's extra SSH arguments),
  the shells' `SSH_AUTH_SOCK` names a link in `ntilde-mux`'s own folder, which every reconnect
  points at the new connection's agent, so `git` and `ssh` in them keep reaching your agent after a
  drop. On a host where that folder's path is too long for a socket, the link is skipped and the log
  says so. (An `ntilde-mux` from a development build before 0.12.0 has no link: its shells keep the
  first connection's socket, which goes away when it drops. `ntilde-mux kill-server`, which ends
  them, lets a current one start.)
- `ntilde-mux` exits by itself 10 minutes after its last shell has ended and its last connection
  has closed.

**On the host.** `ntilde-mux` is not on the host's `PATH`: run it by its path,
`~/.local/share/ntilde/bin/ntilde-mux` (under `$XDG_DATA_HOME` when the install put it there), or
add that folder to your `PATH`.

| Command | What it does |
|---|---|
| `ntilde-mux ls [--json]` | Lists its sessions, as `ntilde mux ls` does. (`--all` needs the Ntilde app: `ntilde-mux ls --all` exits with code 2.) |
| `ntilde-mux attach <id\|prefix> [--read-only]` | Shows a session in the terminal you run it from, as `ntilde mux attach` does (detach with **Ctrl+\ then d**). It works while the Ntilde tab is attached too: both show the same shell, and the tab shows a "shared with 1" badge. |
| `ntilde-mux kill <id>` | Ends one session. |
| `ntilde-mux kill-server [--force]` | Ends every session and stops `ntilde-mux`. |
| `ntilde-mux --version [--json]` | Prints the version. |

`serve` and `proxy --stdio [--no-spawn]` are what Ntilde runs (`--no-spawn`, for `ls --all`, never
starts a multiplexer); you do not need them. `ntilde-mux` keeps its files
in a folder of its own: `~/.local/share/ntilde/ntilde-mux` on Linux (under `$XDG_DATA_HOME` when it
is set), `~/Library/Application Support/ntilde/ntilde-mux` on macOS. Its log is `logs/mux.log`
there, with the previous run's in `logs/mux.log.1`. If you also run the Ntilde app on that host, its own multiplexer uses
`~/.local/share/ntilde` (or `~/Library/Application Support/ntilde`) itself, so the two never share
anything: `ntilde-mux ls` lists only the shells of your persistent SSH tabs, and `ntilde mux ls`
only the app's own. As on your own machine, only your user can open `ntilde-mux`'s socket, and it
never opens a network port.

## Updates

**Your shells survive an update** when the new version can talk to the running multiplexer: Ntilde
restarts and reattaches to them, as after any launch. The update itself says which multiplexer
protocol versions it speaks, so Ntilde knows before it applies it.

Installed with the Windows installer, the multiplexer runs from a copy of Ntilde in the data folder,
`bin\<version>\` (`Ntilde.exe`, the files beside it and the console hosts its shells use; about
75 MB), because the installer's update stops every program running from the install folder. A
launch removes other versions' copies once no multiplexer runs from them. Uninstalling Ntilde stops
the multiplexer, ending its shells, and removes the copies.

**A multiplexer from the previous build.** A multiplexer an update kept is still the previous
version's. Once per launch a *Multiplexer* notification says so: "The multiplexer is from the
previous build (0.12.0); restart it when convenient — this closes its 3 shells." (One newer than
this Ntilde is "from a newer build", one whose version cannot be compared "from a different build",
and the last clause is left out when it runs no shells.) It keeps working meanwhile. Its **Restart
multiplexer now** button asks first, in a *Restart Multiplexer* dialog ("Restart the
multiplexer?", "N shells running in the multiplexer will be closed.", **Restart**). Then it stops
the multiplexer (ending its process if it has not exited within 5 seconds) and starts one of this
version. The panes keep their place: each shows
`[Multiplexer disconnected] [Press Enter to reconnect]`, and Enter starts a new shell; pressed before
the restart is over, it writes
`[The multiplexer is restarting — the new shell starts once it is back]` and starts the shell once it
is. If the multiplexer is already this version's by then, or does not answer, nothing is stopped and
a *Multiplexer* notification says so. `ntilde-mux` on your remote hosts has a notice of its own,
judged against the version installed on each host rather than this Ntilde's ([Remote SSH tabs that keep running](#remote-ssh-tabs-that-keep-running)).

**An update that cannot keep the multiplexer.** When the new version speaks no multiplexer protocol
in common with the running one, or, on Windows, the multiplexer runs from the install folder (which
the update replaces), Ntilde asks first. The *Apply Update* dialog reads "Multiplexed sessions are
still running." and "N multiplexed sessions will be closed by the update", then the reason: "(the
new version cannot keep them)", "(the multiplexer is running from the install folder, so the update
has to stop it)", or, when Ntilde cannot tell where it runs from, "(the multiplexer could not be
checked, so the update has to stop it)". The buttons are **Close sessions and update** and
**Cancel**; Cancel leaves the update unapplied. A multiplexer runs from the install folder when an
Ntilde without the copy above started it, or when Ntilde could not make its copy (a full disk, say).
While one does, a downloaded update is not applied automatically when Ntilde starts (`debug.log`
says why): apply it from the update notification or the command palette, so Ntilde can ask first.

**A multiplexer this Ntilde cannot talk to.** If one is running when Ntilde starts (an update applied
while Ntilde started, say), new panes start normal shells with the *Session not persistent*
notification and its **Restart multiplexer now** button ([What runs](#what-runs)).

**With the setting off**, an update never keeps a running multiplexer: Ntilde asks first ("N
multiplexed sessions will be closed by the update.") and stops it, and while any multiplexer is
running a downloaded update is not applied automatically when Ntilde starts.

## Agents and windowless sessions

With **Agent access (observe)** on ([Agents](agents.md)), an agent sees more than the panes of the window:
`ntilde.list_sessions` also lists the shells the multiplexer keeps that no pane of this window
shows. Those are detached shells, shells another window or `ntilde mux attach` shows, and shells on
the remote hosts the window is connected to. They are marked *windowless*, with their host in the
profile column, and their id is the multiplexer's session id, the one `ntilde mux ls` prints.

- An agent can read a windowless shell's screen and scrollback (its newest 2000 rows), ask its
  status (always the heuristic tier) and capture it as a picture (`mode=render` only).
- With **Agent access (act)** on, it can also type into one and close it, which ends the shell. On a
  remote host both need that connection's **Allow AI agent access to this connection** too.
- Windowless shells send no events, so `ntilde.wait_for_events` never mentions them, and
  `ntilde.export_replay` does not take them.
- Asking about them never connects anywhere and never asks you anything: only the multiplexers the
  window is already connected to are asked, and one that does not answer within 7 seconds counts as
  not found. A multiplexer too old to read screens answers `unsupported`.
- There is no pane to light up, so the window's agent indicator shows these reads - a listing that
  includes windowless shells counts as one - and the
  **Agent Activity** journal records every read of a windowless shell, as
  `windowless · <host> · session <id>`; repeated reads fold into one line with a count (`×N`). Every
  act is recorded too, as for panes.

## The command line

Run these from the Ntilde executable (`ntilde`, or `Ntilde.exe`; on Windows, `ntilde` in PowerShell
or cmd runs `ntilde.com`, [`ntilde.com` on Windows](#ntildecom-on-windows)):

| Command | What it does |
|---|---|
| `ntilde mux ls` | Lists the shells: id, state (`running`, `running, detached`, `exited <code>` or `faulted`), attached windows, size, title. |
| `ntilde mux ls --json` | The same list as JSON. |
| `ntilde mux ls --all [--json]` | The same list with a HOST column (`this computer`, or `user@host`), followed by the shells on the host of every SSH profile that keeps its remote sessions. It connects to those hosts on its own, all at once, and never asks for anything: it signs in with keys, the SSH agent, a saved password or an existing shared connection. A host it cannot list within 15 seconds gets an `unreachable:` line, and the reason goes to `logs/mux-ls-all.log`. See below for how it differs from the other commands. |
| `ntilde mux attach <id\|prefix> [--read-only]` | Shows a shell in this terminal ([Several windows and `ntilde mux attach`](#several-windows-and-ntilde-mux-attach)). |
| `ntilde mux kill <id>` | Ends one shell. |
| `ntilde mux kill-server` | Ends every shell and stops the multiplexer. Waits up to 5 seconds for it to exit; if it has not, prints "Multiplexer did not stop within 5 s." and exits with code 1. |
| `ntilde mux kill-server --force` | Also stops a multiplexer this Ntilde cannot talk to at all (no protocol version in common), once its pid and process name are re-verified — see below. |

These commands never start a multiplexer. With none running they print "No multiplexer is
running." and exit with code 1 (`attach` exits with code 2, its code for any connection error).
With no shells, `ls` prints "No sessions."

`ls --all` differs in four ways:

- It never starts a multiplexer, on this computer or on a host: a host where none runs gets
  `unreachable: no multiplexer is running`. A host whose `ntilde-mux` is older than this Ntilde
  cannot be listed that way and gets `unreachable: its ntilde-mux is older than this app; update it
  to list its sessions` (open a persistent tab there and choose *Update ntilde-mux*). Listing never
  changes which SSH agent the host's shells use.
- With no multiplexer running on this computer, it still lists the remote hosts. This computer's
  line then reads `this computer  unreachable: no multiplexer is running`, and the exit code is 1,
  as for `ls`. Remote hosts it cannot reach do not change the exit code. A host with no shells gets a
  `No sessions.` line, and with no profile keeping its remote sessions the list ends with
  "No remote hosts keep sessions."
- On Linux and macOS it does not sign in through a jump host with the OpenSSH backend. A profile
  that goes through a jump host or a proxy command gets an `unreachable:` line instead, because a
  jump host's password prompt would appear in your terminal. *Attach to session…* in Ntilde still
  lists such hosts, and profiles on the native SSH backend are listed as usual.
- With `--json` it prints one object, each host's shells as `ls --json` writes them, or its
  `unreachable` reason in their place:
  `{"endpoints":[{"endpoint":"local","host":"this computer","sessions":[…]},{"endpoint":"ssh:<profile id>","host":"user@host","error":"…"}]}`.

**`kill-server --force`** stops a multiplexer this Ntilde has no protocol version in common with -
a much older or a newer one - once its pid and process name are re-verified against its endpoint
file, ending its shells. Without `--force` against such a multiplexer, `kill-server` reports the
mismatch and exits 1 instead of guessing. A multiplexer that still talks to this Ntilde (one from
the previous build, say) stops with plain `kill-server`, and `--force` changes nothing.

**A multiplexer from an early development build** (protocol v1) keeps working for starting,
attaching, detaching and ending shells, but shows no sharing indicator, `--read-only` needs a newer
one, and deliberately detached shells are reopened at the next launch anyway. A plain
`ntilde mux kill-server` replaces it: it stops the old one, and the next launch starts a current
one.

## `ntilde.com` on Windows

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

If you capture its output (`ntilde | Out-Null`, `$x = ntilde`), the caller waits until the window
closes; start it plainly or with `Start-Process ntilde`.

The installer adds Ntilde's install folder to your user `PATH`, each update keeps it there, and
uninstalling removes it, so `ntilde` works in any terminal opened afterwards (a terminal that was
already open keeps its old `PATH`). The release's `.zip` bundle contains `ntilde.com` too, but adds
nothing to `PATH`.

## Turning it off

Set Settings → General → *Sessions* → **Keep shells running when the window closes** to
*Off*. It is on (*Keep running*) by default; once you save *Off*, it stays off, updates included.
From then on:

- New tabs and panes run normal shells, which end when their window closes, and no multiplexer is
  started for them.
- Closing a window asks nothing, and **Session: Attach to Session…**, **Pane: Detach** and
  **Session: Quit and Close All Shells** leave the command palette.
- An update never keeps a running multiplexer ([Updates](#updates)).
- SSH tabs open plain, whatever their profile says.

Turning it off does not stop shells that are already running, and panes already open keep theirs
until their window closes. Closing that window then ends them, without asking, as *Close them* would
([Closing the window](#closing-the-window)): so do the shells of restored tabs you have not looked at yet, while a shell another window or
`ntilde mux attach` also shows keeps running there. Shells no window shows - detached ones - keep
running, and the next time Ntilde saves your session it forgets them. To end those, use **Quit and
close all shells** before you turn the setting off, or run `ntilde mux kill-server` afterwards. Saving the change also forgets a remembered first-close answer
([Closing the window](#closing-the-window)), so turning it back on asks again.

To keep local shells running but stop one SSH profile from keeping its remote shells, untick
**Keep remote sessions running (ntilde-mux)** in its connection editor instead ([Remote SSH tabs that keep running](#remote-ssh-tabs-that-keep-running)).
