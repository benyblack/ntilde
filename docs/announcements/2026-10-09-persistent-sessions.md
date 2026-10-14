<!--
DRAFT: the release notes for 0.12.0, the release that ships the multiplexer.

Before publishing, check:
  1. The maintainer's call on the default (the PR's "keep local shells running by default" commit).
     The section "On by default" below belongs to that call: drop it with the flip.
  2. The three-OS manual checklist (Phase 5 Task 30) passed, including an update applied with shells
     running, on Windows and on macOS.
  3. The release notes Velopack ships carry the protocol marker line (release.yml writes it; an
     installed 0.12.0 reads it from the next update to decide whether the multiplexer survives).
  4. The version strings and links below name the released tag.

Targets: the GitHub release body (long form) and a short post. Keep "what happens to my shells"
first: that is what a user upgrading notices.
-->

# Ntilde 0.12.0: shells that outlive the window

Close Ntilde's window and your shells can keep running. Open it again and every tab is back where it
was, with its screen and scrollback - the build still compiling, the editor still open, the SSH
session still signed in. Your SSH tabs can do the same on the remote host, and survive a dropped
Wi-Fi or a laptop lid on the way.

## What it is

Ntilde 0.12.0 can run your shells in a background process, Ntilde's own *multiplexer*, instead of
in the window. The window shows them; it no longer owns them. With *Keep shells running when the
window closes* on:

- **Closing the window, a crash or an update** leaves the shells running, and reopening Ntilde
  reattaches each tab to its shell.
- **The first time you close a window** with shells in it, Ntilde asks: *Keep running* or *Close
  them*, with *Don't ask again*. **Quit and close all shells** (in the command palette, and under
  the setting in Settings) ends everything at once.
- **After a restart of the computer** the shells are gone with it, and Ntilde quietly starts fresh
  ones in the same tabs - no warning, because nothing went wrong.
- **Several windows can share a shell.** *Pane: Detach* puts a tab's shell aside, *Attach to
  session…* brings any running shell back into a tab, and `ntilde mux attach <id>` shows one in any
  terminal.
- **From the command line:** `ntilde mux ls` lists the shells, `--all` adds those on your SSH hosts,
  and `ntilde mux kill` / `kill-server` end them.

## On by default

Local shells keep running by default from 0.12.0 on. If you never changed the setting, updating
turns it on; if you set it to *Off*, it stays off. SSH tabs keep running only for the connections
you opt in.

## Turning it on or off

One setting: Settings → Appearance → *Keep shells running when the window closes*, *Keep running* or
*Off*. With *Off*, Ntilde treats shells exactly as 0.11 did: they end with their window, no
background process is started, and nothing new appears.

## SSH tabs that survive the network

Tick *Keep remote sessions running (ntilde-mux)* on an SSH connection and install `ntilde-mux` on the
host from the same tab of the connection editor (no root, and nothing else on the host changes). The
tab's shell then runs on the host. When the connection drops, the tab keeps its screen and the shell
carries on as if nothing happened. The tab reconnects by itself when it can sign in without you -
with your keys, your agent or the saved password, never by asking you - and otherwise waits for you
to press Enter. *Attach to session…* lists your hosts' shells too, and connects to a host for you.

Supported hosts: Linux on x64 or arm64 with glibc 2.34 or newer, and macOS on Apple silicon. Native
and OpenSSH profiles both work.

## Updates

Updating Ntilde no longer closes your shells when the new version can talk to the running
multiplexer: Ntilde restarts and reattaches. The multiplexer then is still the previous build's,
and a notification offers **Restart multiplexer now** when it suits you. An update that cannot keep
it says so and asks before it closes anything.

## Agents

With *Agent access* on, MCP agents also see the shells no window shows - a detached build, a shell
on a remote host - and can read them; with *Agent access (act)* they can type into them and close
them. Every such read is in the activity journal, since there is no pane to light up.

## Known limits

- A restart of the computer ends local shells (SSH tabs' shells live on their host).
- Inline images (sixel, kitty graphics) are not shown in persistent panes.
- A shell inherits the multiplexer's environment, not the window's.
- Persistent SSH tabs run no port forwards, and with the OpenSSH backend have no *Remote Files*
  sidebar (the palette's transfers work). Windows hosts are not supported.
- A persistent SSH tab reconnects only when you press Enter if signing in needs a typed answer (a
  password that is not saved, a key passphrase or a one-time code; native profiles reuse a password
  or passphrase already typed in the same window), if it signs in with a password through a jump
  host, or if it uses an OpenSSH older than 8.4 with a password-only host (Windows 10's built-in
  `ssh` is 8.1).
- `ntilde mux ls --all` on Linux and macOS skips OpenSSH profiles that go through a jump host or a
  proxy command; *Attach to session…* still lists those hosts.
- *Attach to session…* lists remote shells that no saved tab names, but they do not reopen on their
  own.
- On Linux hosts that end a user's processes at logout (`KillUserProcesses=yes`), run
  `loginctl enable-linger $USER` once.
- On macOS, Cmd+Q never asks the first-close question: it applies a remembered answer, and otherwise
  keeps the shells.
- An update that changes the multiplexer protocol still has to close the shells (it asks first):
  handing running shells over to a new multiplexer is not there yet.

The user manual's chapter 12, *Persistent sessions and the multiplexer*, has the details.
