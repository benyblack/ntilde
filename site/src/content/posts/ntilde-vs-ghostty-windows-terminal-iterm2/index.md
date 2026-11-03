---
title: 'Ntilde, Ghostty, Windows Terminal and iTerm2 compared'
description: 'A feature-by-feature look at where Ntilde stands against the three terminals people most often ask about, including the places where it is still behind.'
date: 2026-11-02
author: Behnam
---

People who try Ntilde usually already use something else: Ghostty, Windows Terminal or iTerm2. So
they ask the same question: what does Ntilde do that my terminal doesn't, and what will I give up?
This post answers it with one table per area, and it covers the places where Ntilde is behind as well
as the places where it is ahead.

The comparison covers **Ntilde 0.12**, **Ghostty 1.3**, **Windows Terminal 1.25** and **iTerm2
3.6 and later**, as of November 2026. All four move fast, so some cells will go out of date. If you
spot one that has, please [open an issue](https://github.com/benyblack/ntilde/issues/new) and
we'll fix it.

**Legend:** ✅ supported · ⚠️ partial or with limits · 🛠️ in development · ❌ not supported

## The short version

- **Ghostty** has the deepest protocol support and feels fully native on macOS and Linux. It does not
  run on Windows.
- **Windows Terminal** is the default on Windows. It is polished and well integrated with the OS, and
  it now supports the Kitty keyboard protocol. It runs only on Windows.
- **iTerm2** has the most features of the four on the Mac: tmux control mode, a Python API, triggers
  and a built-in AI chat. It runs only on macOS.
- **Ntilde** runs on Windows, macOS and Linux, with the same terminal behavior on all three. Its
  focus is on shells that outlive the window and the network connection, a built-in SSH client,
  deterministic replay, and controlled access for AI agents.

## Platform and foundations

| | Ntilde | Ghostty | Windows Terminal | iTerm2 |
|---|---|---|---|---|
| Windows | ✅ | ❌ | ✅ | ❌ |
| macOS | ✅ Apple silicon | ✅ | ❌ | ✅ |
| Linux | ✅ x64 and arm64 | ✅ | ❌ | ❌ |
| UI toolkit | Avalonia (cross-platform) | AppKit/SwiftUI, GTK4 | WinUI | Cocoa |
| Renderer | Skia on the GPU | Metal, OpenGL | Direct3D | Metal |
| License | MIT | MIT | MIT | GPLv2+ |
| Embeddable core | ❌ | ✅ libghostty | ⚠️ ConPTY / conhost | ❌ |

Ghostty, Windows Terminal and iTerm2 each draw their windows with the platform's own toolkit, and
it shows. Ntilde uses Avalonia instead, which is what lets one codebase behave identically
everywhere. The trade-off is that its menus and window chrome look like Ntilde's own on every OS, not
like the host's.

## Windows and everyday use

| | Ntilde | Ghostty | Windows Terminal | iTerm2 |
|---|---|---|---|---|
| Tabs and split panes | ✅ with pane zoom | ✅ | ✅ | ✅ |
| Vertical tab sidebar | ✅ with live preview | ❌ | ❌ | ⚠️ tabs on the left |
| Command palette | ✅ | ✅ | ✅ | ✅ |
| Drop-down (quake) window | ⚠️ Windows only | ✅ macOS and Linux | ✅ | ✅ |
| Settings window | ✅ searchable | ❌ text file | ✅ searchable | ✅ |
| Settings apply without restart | ✅ | ✅ | ✅ | ✅ |
| Scrollback search | ✅ | ✅ | ✅ | ✅ |
| Broadcast input to several panes | ✅ | ❌ | ✅ | ✅ |
| Saved layouts | ✅ workspaces and templates | ❌ | ⚠️ startup actions | ✅ window arrangements |
| Bundled themes | 10, plus your own | 400+ | a handful, plus your own | presets, plus import |
| Fonts that work on a fresh install | ✅ JetBrains Mono, Nerd Font symbols | ✅ JetBrains Mono, Nerd Font symbols | ✅ Cascadia | ❌ |
| Ligatures | ✅ off by default | ✅ | ✅ | ✅ |
| Transparency, blur, background image | ✅ | ✅ | ✅ | ✅ |
| Settings backup and restore | ✅ one file, plus automatic snapshots | ❌ copy the file | ❌ copy the file | ✅ sync to a folder |

## Terminal protocols

| | Ntilde | Ghostty | Windows Terminal | iTerm2 |
|---|---|---|---|---|
| `TERM` it advertises | `xterm-256color` | `xterm-ghostty`, own terminfo | `xterm-256color` | `xterm-256color` |
| Kitty keyboard protocol | ⚠️ first level only | ✅ | ✅ new in 1.25 | ✅ |
| Kitty graphics | ⚠️ placement incomplete | ✅ | ❌ | ✅ |
| Sixel | ⚠️ no scrolling yet | ❌ | ✅ | ✅ |
| iTerm2 inline images | ✅ | ❌ | ❌ | ✅ |
| Styled and colored underlines | 🛠️ | ✅ | ✅ | ✅ |
| Hyperlinks (OSC 8) | ✅ | ✅ | ✅ | ✅ |
| Clipboard from programs (OSC 52) | ⚠️ write only, by design | ✅ read asks first | ⚠️ write only | ✅ read is opt-in |
| Shell integration (OSC 133) | ⚠️ prompt and command marks | ✅ includes click to move the cursor | ✅ marks | ✅ |
| Notification when a command finishes | ❌ | ✅ | ❌ | ✅ |
| Synchronized output | ✅ | ✅ | ✅ | ✅ |
| Checked against a conformance suite in CI | ✅ | ✅ fuzzed | — | — |

Ntilde refuses requests from programs to *read* your clipboard. Any program on any remote host could
otherwise ask the terminal what you last copied, and so far no workflow we've seen needs that badly
enough to justify the risk.

## Sessions, remote work and automation

| | Ntilde | Ghostty | Windows Terminal | iTerm2 |
|---|---|---|---|---|
| Shells survive closing the app | ✅ built in | ❌ | ❌ restores text, not programs | ✅ |
| Remote shells survive a dropped network | ✅ built in | ❌ | ❌ | ⚠️ through tmux |
| tmux control mode | ❌ | ❌ | ❌ | ✅ |
| Built-in SSH client | ✅ | ❌ uses `ssh` | ❌ uses `ssh.exe` | ⚠️ `it2ssh` |
| Remote file browser | ✅ SFTP sidebar | ❌ | ❌ | ⚠️ upload and download |
| Record and replay a session | ✅ deterministic, renders headless | ❌ | ❌ | ⚠️ Instant Replay, in memory |
| Command suggestions | ✅ Command Assist | ❌ | ⚠️ experimental | ⚠️ autocomplete |
| AI agents | ✅ MCP server, read and act opt-ins | ❌ | ⚠️ experiments only | ✅ AI chat |
| Scripting | ⚠️ CLI and MCP | ⚠️ AppleScript | ⚠️ `wt.exe` arguments | ✅ Python API, AppleScript, triggers |
| Automatic updates | ✅ | ✅ | ✅ | ✅ |

## Where Ntilde is different

**Your shells outlive the window.** Ntilde runs your shells in its own background process. Close the
window, crash or install an update, and every tab comes back to the same shell, screen and
scrollback included. For SSH tabs it does the same on the remote host, so a dropped network or a
laptop that went to sleep costs you nothing. iTerm2 gets part of the way with tmux control mode.
Ntilde builds it in, with no tmux on either end. See
[persistent sessions](/docs/persistent-sessions/).

**SSH is built in.** New connections use Ntilde's own SSH client. It stores passwords in the OS
keychain and supports jump hosts, port forwarding and ssh-agent, and it adds an SFTP file sidebar to
the pane. You can still choose OpenSSH for any profile. See [SSH and file transfer](/docs/ssh/).

**Replays are exact.** Ntilde can record a session and re-render it later to the exact same pixels,
on any machine, with no window. That is how we test the renderer. It also lets you see what an AI
agent did in your terminal, frame by frame.

**AI agents get controlled access.** An MCP server lets Claude Code and other agents read your live
sessions, and separately, act in them. Both permissions are off by default and turned on
independently, SSH hosts need to be allowlisted, and every action an agent takes is logged where you
can see it. See [agent access](/docs/agents/).

**Correctness is tested in CI.** Every VT feature Ntilde claims is a row in a
[conformance matrix](https://github.com/benyblack/ntilde/blob/main/docs/vt_coverage_matrix.md) that
names the tests behind it, and CI fails the build if a claim loses its evidence.

## Where Ntilde is behind

- **Keyboard:** Ntilde implements only the first level of the Kitty keyboard protocol, which
  distinguishes keys that legacy encoding mixes up. Ghostty, iTerm2 and now Windows Terminal report
  more, including key releases.
- **Capability advertising:** Ntilde still presents itself as `xterm-256color`, so programs won't
  discover features beyond what that entry describes.
- **Graphics:** Sixel images don't scroll yet, and Kitty graphics placement is incomplete.
- **Drop-down window:** only on Windows.
- **Polish:** Ghostty has a far larger theme collection and more of the rarely used protocol
  details, and on macOS iTerm2 and Ghostty feel more at home than a cross-platform UI can.

## What's next

Our current plan closes the protocol gaps in this order: styled and colored underlines (in development now), the rest
of the Kitty keyboard protocol, Sixel scrolling, full Kitty graphics placement, and finally an
accurate description of what Ntilde supports, so that programs can discover it.

To try Ntilde, [install it](/#install) on Windows, macOS or Linux.

## Sources

- [Ghostty 1.3.0 release notes](https://ghostty.org/docs/install/release-notes/1-3-0)
- [Windows Terminal Preview 1.25 release](https://devblogs.microsoft.com/commandline/windows-terminal-preview-1-25-release/)
- [iTerm2 releases](https://iterm2.com/downloads)
- Terminal compatibility tables for the
  [Kitty keyboard protocol](https://terminfo-dev.pages.dev/extensions/kitty-keyboard-protocol) and
  [Kitty graphics](https://terminfo-dev.pages.dev/extensions/kitty-graphics-protocol)
